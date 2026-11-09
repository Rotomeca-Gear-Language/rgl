#include "rgl/standard/child.hpp"

#include "rgl/standard/bytes.hpp"

#include <chrono>
#include <csignal>
#include <map>
#include <memory>
#include <mutex>
#include <thread>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <poll.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

namespace rgl::standard::child {

namespace {

using clock = std::chrono::steady_clock;

struct entry {
#if defined(_WIN32)
    HANDLE process = nullptr;
    HANDLE in = nullptr;
    HANDLE pipes[2] = {nullptr, nullptr};
#else
    pid_t pid = -1;
    int in = -1;
    int pipes[2] = {-1, -1};
#endif
    std::string buffer[2];
    bool eof[2] = {false, false};
    bool exited = false;
    std::int32_t code = 0;
};

std::mutex table_lock;
std::map<std::int64_t, std::shared_ptr<entry>> table;
std::int64_t next_id = 1;

std::shared_ptr<entry> find(std::int64_t id) {
    const std::lock_guard<std::mutex> guard(table_lock);
    const auto found = table.find(id);
    return found == table.end() ? nullptr : found->second;
}

std::int64_t store(std::shared_ptr<entry> value) {
    const std::lock_guard<std::mutex> guard(table_lock);
    const auto id = next_id++;
    table[id] = std::move(value);
    return id;
}

// Le dernier élément d'un chemin, sans dossier.
std::string base_name(const string& path) {
    std::string name(path.data(), path.size());
    const auto cut = name.find_last_of("/\\");
    return cut == std::string::npos ? name : name.substr(cut + 1);
}

// Vrai pour `rgl` et `rgl.exe` : un programme RGL ne relance pas le compilateur.
bool is_compiler(const string& program) {
    std::string name = base_name(program);
    for (auto& c : name) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return name == "rgl" || name == "rgl.exe";
}

// Les millisecondes qui restent avant l'échéance ; -1 s'il n'y en a pas (0 demandé : sans limite).
std::int64_t remaining_ms(clock::time_point start, std::uint32_t timeout_ms) {
    if (timeout_ms == 0) return -1;
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - start).count();
    const auto left = static_cast<std::int64_t>(timeout_ms) - elapsed;
    return left < 0 ? 0 : left;
}

#if defined(_WIN32)

bool to_wide(const string& text, std::wstring& wide) {
    wide.clear();
    if (text.size() == 0) return true;
    const int count = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (count <= 0) return false;
    wide.resize(static_cast<std::size_t>(count));
    ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), wide.data(), count);
    return true;
}

// Un argument, cité comme le veut `CommandLineToArgvW`.
void append_quoted(std::wstring& command, const std::wstring& argument) {
    if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos) {
        command += argument;
        return;
    }
    command.push_back(L'"');
    for (auto it = argument.begin();; ++it) {
        std::size_t backslashes = 0;
        while (it != argument.end() && *it == L'\\') {
            ++it;
            ++backslashes;
        }
        if (it == argument.end()) {
            command.append(backslashes * 2, L'\\');
            break;
        }
        if (*it == L'"') {
            command.append(backslashes * 2 + 1, L'\\');
            command.push_back(L'"');
        } else {
            command.append(backslashes, L'\\');
            command.push_back(*it);
        }
    }
    command.push_back(L'"');
}

void close_handle(HANDLE& handle) {
    if (handle != nullptr && handle != INVALID_HANDLE_VALUE) ::CloseHandle(handle);
    handle = nullptr;
}

// Un tube dont l'extrémité de l'enfant est héritable et celle de ce programme ne l'est pas.
bool make_pipe(bool child_reads, HANDLE& child_end, HANDLE& parent_end) {
    SECURITY_ATTRIBUTES inheritable{sizeof inheritable, nullptr, TRUE};
    HANDLE read = nullptr, write = nullptr;
    if (!::CreatePipe(&read, &write, &inheritable, 0)) return false;
    child_end = child_reads ? read : write;
    parent_end = child_reads ? write : read;
    ::SetHandleInformation(parent_end, HANDLE_FLAG_INHERIT, 0);
    return true;
}

read_status read_raw(entry& e, int which, std::uint32_t timeout_ms, std::size_t limit, std::string& into) {
    HANDLE pipe = e.pipes[which];
    if (pipe == nullptr) return read_status::closed;
    const auto start = clock::now();
    for (;;) {
        DWORD available = 0;
        if (!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) return read_status::closed;
        if (available > 0) {
            const DWORD wanted = available < limit ? available : static_cast<DWORD>(limit);
            char data[65536];
            const DWORD request = wanted < sizeof data ? wanted : static_cast<DWORD>(sizeof data);
            DWORD got = 0;
            if (!::ReadFile(pipe, data, request, &got, nullptr) || got == 0) return read_status::closed;
            into.append(data, got);
            return read_status::data;
        }
        if (remaining_ms(start, timeout_ms) == 0) return read_status::timeout;
        ::Sleep(2);
    }
}

#else

void set_cloexec(int fd) {
    const int flags = ::fcntl(fd, F_GETFD);
    if (flags >= 0) ::fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
}

void close_fd(int& fd) {
    if (fd >= 0) ::close(fd);
    fd = -1;
}

read_status read_raw(entry& e, int which, std::uint32_t timeout_ms, std::size_t limit, std::string& into) {
    const int fd = e.pipes[which];
    if (fd < 0) return read_status::closed;
    const auto start = clock::now();
    for (;;) {
        const auto left = remaining_ms(start, timeout_ms);
        pollfd descriptor{fd, POLLIN, 0};
        const int ready = ::poll(&descriptor, 1, left < 0 ? -1 : static_cast<int>(left));
        if (ready < 0) {
            if (errno == EINTR) continue;
            return read_status::closed;
        }
        if (ready == 0) return read_status::timeout;
        char data[65536];
        const ssize_t got = ::read(fd, data, limit < sizeof data ? limit : sizeof data);
        if (got > 0) {
            into.append(data, static_cast<std::size_t>(got));
            return read_status::data;
        }
        if (got < 0 && errno == EINTR) continue;
        return read_status::closed;
    }
}

#endif

// Une séquence UTF-8 valide à `at` : sa longueur, ou 0.
std::size_t valid_sequence(const std::string& data, std::size_t at) {
    const auto lead = static_cast<unsigned char>(data[at]);
    if (lead < 0x80) return 1;
    std::size_t extra;
    std::uint32_t point;
    if (lead >= 0xC2 && lead <= 0xDF) {
        extra = 1;
        point = lead & 0x1Fu;
    } else if (lead >= 0xE0 && lead <= 0xEF) {
        extra = 2;
        point = lead & 0x0Fu;
    } else if (lead >= 0xF0 && lead <= 0xF4) {
        extra = 3;
        point = lead & 0x07u;
    } else {
        return 0;
    }
    if (data.size() - at <= extra) return 0;
    for (std::size_t k = 1; k <= extra; ++k) {
        const auto next = static_cast<unsigned char>(data[at + k]);
        if ((next & 0xC0) != 0x80) return 0;
        point = (point << 6) | (next & 0x3Fu);
    }
    if (extra == 2 && point < 0x800) return 0;
    if (extra == 3 && (point < 0x10000 || point > 0x10FFFF)) return 0;
    if (point >= 0xD800 && point <= 0xDFFF) return 0;
    return extra + 1;
}

}  // namespace

string to_text(const std::string& data) {
    if (bytes::is_valid_utf8(data.data(), data.size())) return string(data.data(), data.size());
    mstring text;
    for (std::size_t i = 0; i < data.size();) {
        if (const std::size_t length = valid_sequence(data, i)) {
            text.append(data.data() + i, length);
            i += length;
        } else {
            text.append("ï¿½", 3);
            ++i;
        }
    }
    return text.to_immutable();
}

std::int64_t start(const string& program, const std::vector<string>& args, const string& cwd) {
    if (is_compiler(program)) return denied;
    auto child = std::make_shared<entry>();
#if defined(_WIN32)
    std::wstring command, wide_program, wide_cwd;
    if (!to_wide(program, wide_program) || !to_wide(cwd, wide_cwd)) return failed;
    append_quoted(command, wide_program);
    for (const auto& argument : args) {
        std::wstring wide;
        if (!to_wide(argument, wide)) return failed;
        command.push_back(L' ');
        append_quoted(command, wide);
    }
    HANDLE child_in = nullptr, child_out = nullptr, child_err = nullptr;
    const auto cleanup = [&]() {
        close_handle(child_in);
        close_handle(child_out);
        close_handle(child_err);
        close_handle(child->in);
        close_handle(child->pipes[0]);
        close_handle(child->pipes[1]);
    };
    if (!make_pipe(true, child_in, child->in) || !make_pipe(false, child_out, child->pipes[0]) || !make_pipe(false, child_err, child->pipes[1])) {
        cleanup();
        return failed;
    }
    STARTUPINFOW startup{};
    startup.cb = sizeof startup;
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = child_in;
    startup.hStdOutput = child_out;
    startup.hStdError = child_err;
    PROCESS_INFORMATION info{};
    const BOOL created = ::CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, nullptr,
                                          wide_cwd.empty() ? nullptr : wide_cwd.c_str(), &startup, &info);
    const DWORD error = created ? 0 : ::GetLastError();
    close_handle(child_in);
    close_handle(child_out);
    close_handle(child_err);
    if (!created) {
        cleanup();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return not_found;
        if (error == ERROR_ACCESS_DENIED || error == ERROR_BAD_EXE_FORMAT) return denied;
        return failed;
    }
    ::CloseHandle(info.hThread);
    child->process = info.hProcess;
#else
    std::signal(SIGPIPE, SIG_IGN);
    int to_child[2] = {-1, -1}, from_out[2] = {-1, -1}, from_err[2] = {-1, -1}, failure[2] = {-1, -1};
    const auto close_all = [&]() {
        for (auto* pair : {to_child, from_out, from_err, failure}) {
            close_fd(pair[0]);
            close_fd(pair[1]);
        }
    };
    if (::pipe(to_child) != 0 || ::pipe(from_out) != 0 || ::pipe(from_err) != 0 || ::pipe(failure) != 0) {
        close_all();
        return failed;
    }
    set_cloexec(to_child[1]);
    set_cloexec(from_out[0]);
    set_cloexec(from_err[0]);
    set_cloexec(failure[1]);
    std::vector<std::string> storage;
    storage.emplace_back(program.data(), program.size());
    for (const auto& argument : args) storage.emplace_back(argument.data(), argument.size());
    std::vector<char*> argv;
    for (auto& item : storage) argv.push_back(item.data());
    argv.push_back(nullptr);
    const std::string directory(cwd.data(), cwd.size());
    const pid_t pid = ::fork();
    if (pid < 0) {
        close_all();
        return failed;
    }
    if (pid == 0) {
        ::dup2(to_child[0], 0);
        ::dup2(from_out[1], 1);
        ::dup2(from_err[1], 2);
        if (!directory.empty() && ::chdir(directory.c_str()) != 0) {
            const int code = errno;
            [[maybe_unused]] const ssize_t ignored = ::write(failure[1], &code, sizeof code);
            ::_exit(127);
        }
        ::execvp(argv[0], argv.data());
        const int code = errno;
        [[maybe_unused]] const ssize_t ignored = ::write(failure[1], &code, sizeof code);
        ::_exit(127);
    }
    close_fd(to_child[0]);
    close_fd(from_out[1]);
    close_fd(from_err[1]);
    close_fd(failure[1]);
    int code = 0;
    const ssize_t got = ::read(failure[0], &code, sizeof code);
    close_fd(failure[0]);
    if (got == static_cast<ssize_t>(sizeof code)) {
        int status = 0;
        ::waitpid(pid, &status, 0);
        close_fd(to_child[1]);
        close_fd(from_out[0]);
        close_fd(from_err[0]);
        return code == ENOENT || code == ENOTDIR ? not_found : code == EACCES || code == EPERM ? denied : failed;
    }
    child->pid = pid;
    child->in = to_child[1];
    child->pipes[0] = from_out[0];
    child->pipes[1] = from_err[0];
#endif
    return store(std::move(child));
}

bool write(std::int64_t id, const char* data, std::size_t size) {
    const auto child = find(id);
    if (!child) return false;
#if defined(_WIN32)
    if (child->in == nullptr) return false;
    while (size > 0) {
        DWORD written = 0;
        if (!::WriteFile(child->in, data, static_cast<DWORD>(size > 0x40000000u ? 0x40000000u : size), &written, nullptr) || written == 0) return false;
        data += written;
        size -= written;
    }
    return true;
#else
    if (child->in < 0) return false;
    while (size > 0) {
        const ssize_t written = ::write(child->in, data, size);
        if (written < 0 && errno == EINTR) continue;
        if (written <= 0) return false;
        data += written;
        size -= static_cast<std::size_t>(written);
    }
    return true;
#endif
}

void close_input(std::int64_t id) {
    const auto child = find(id);
    if (!child) return;
#if defined(_WIN32)
    close_handle(child->in);
#else
    close_fd(child->in);
#endif
}

read_status read(std::int64_t id, stream which, std::uint32_t timeout_ms, std::size_t limit, std::string& into) {
    const auto child = find(id);
    if (!child) return read_status::closed;
    auto& pending = child->buffer[static_cast<int>(which)];
    if (!pending.empty()) {
        const auto count = pending.size() < limit ? pending.size() : limit;
        into.append(pending, 0, count);
        pending.erase(0, count);
        return read_status::data;
    }
    if (child->eof[static_cast<int>(which)]) return read_status::closed;
    const auto status = read_raw(*child, static_cast<int>(which), timeout_ms, limit, into);
    if (status == read_status::closed) child->eof[static_cast<int>(which)] = true;
    return status;
}

read_status read_line(std::int64_t id, stream which, std::uint32_t timeout_ms, std::string& into) {
    const auto child = find(id);
    if (!child) return read_status::closed;
    const int index = static_cast<int>(which);
    auto& pending = child->buffer[index];
    const auto start = clock::now();
    for (;;) {
        const auto newline = pending.find('\n');
        if (newline != std::string::npos) {
            into.assign(pending, 0, newline);
            if (!into.empty() && into.back() == '\r') into.pop_back();
            pending.erase(0, newline + 1);
            return read_status::data;
        }
        if (child->eof[index]) {
            if (pending.empty()) return read_status::closed;
            into = pending;
            pending.clear();
            return read_status::data;
        }
        const auto left = remaining_ms(start, timeout_ms);
        if (timeout_ms != 0 && left == 0) return read_status::timeout;
        const auto status = read_raw(*child, index, left < 0 ? 0 : static_cast<std::uint32_t>(left), 65536, pending);
        if (status == read_status::closed) child->eof[index] = true;
        else if (status == read_status::timeout) return read_status::timeout;
    }
}

bool wait(std::int64_t id, std::uint32_t timeout_ms, std::int32_t& exit_code) {
    const auto child = find(id);
    if (!child) return false;
    if (child->exited) {
        exit_code = child->code;
        return true;
    }
#if defined(_WIN32)
    if (::WaitForSingleObject(child->process, timeout_ms == 0 ? INFINITE : timeout_ms) != WAIT_OBJECT_0) return false;
    DWORD code = 0;
    ::GetExitCodeProcess(child->process, &code);
    child->code = static_cast<std::int32_t>(code);
#else
    const auto start = clock::now();
    for (;;) {
        int status = 0;
        const pid_t done = ::waitpid(child->pid, &status, WNOHANG);
        if (done == child->pid) {
            child->code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + (WIFSIGNALED(status) ? WTERMSIG(status) : 0);
            break;
        }
        if (done < 0 && errno != EINTR) {
            child->code = -1;
            break;
        }
        if (timeout_ms != 0 && remaining_ms(start, timeout_ms) == 0) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
#endif
    child->exited = true;
    exit_code = child->code;
    return true;
}

void kill(std::int64_t id) {
    const auto child = find(id);
    if (!child || child->exited) return;
#if defined(_WIN32)
    ::TerminateProcess(child->process, 1);
#else
    ::kill(child->pid, SIGKILL);
#endif
}

void release(std::int64_t id) {
    std::shared_ptr<entry> child;
    {
        const std::lock_guard<std::mutex> guard(table_lock);
        const auto found = table.find(id);
        if (found == table.end()) return;
        child = found->second;
        table.erase(found);
    }
#if defined(_WIN32)
    close_handle(child->in);
    close_handle(child->pipes[0]);
    close_handle(child->pipes[1]);
    close_handle(child->process);
#else
    close_fd(child->in);
    close_fd(child->pipes[0]);
    close_fd(child->pipes[1]);
    if (!child->exited) {
        int status = 0;
        ::waitpid(child->pid, &status, WNOHANG);
    }
#endif
}

}  // namespace rgl::standard::child

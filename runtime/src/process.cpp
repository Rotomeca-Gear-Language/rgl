// Composant « process » du runtime : lancer un processus enfant, attendre sa fin et lire ses sorties.

#include "rgl/standard/process.hpp"

#include <cstring>
#include <string>

#include "rgl/standard/bytes.hpp"
#include "rgl/standard/format.hpp"

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
#include <csignal>
#include <fcntl.h>
#include <poll.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char** environ;
#endif

namespace rgl::standard::process {

namespace {

constexpr std::size_t chunk_size = 65536;

// Le dernier élément d'un chemin, sans dossier.
std::string base_name(const string& path) {
    std::string name(path.data(), path.size());
    const auto cut = name.find_last_of("/\\");
    return cut == std::string::npos ? name : name.substr(cut + 1);
}

// Vrai pour `rgl` et `rgl.exe`, en minuscules ou non : un programme RGL ne relance pas le compilateur.
bool is_compiler(const string& program) {
    std::string name = base_name(program);
    for (auto& c : name) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return name == "rgl" || name == "rgl.exe";
}

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

// Les octets d'une sortie en `string` : chaque octet qui n'est pas de l'UTF-8 devient « U+FFFD ».
string lossy(const std::string& data) {
    if (bytes::is_valid_utf8(data.data(), data.size())) return string(data.data(), data.size());
    mstring text;
    for (std::size_t i = 0; i < data.size();) {
        if (const std::size_t length = valid_sequence(data, i)) {
            text.append(data.data() + i, length);
            i += length;
        } else {
            text.append("\xEF\xBF\xBD", 3);
            ++i;
        }
    }
    return text.to_immutable();
}

outcome failed(failure error, const string& message) {
    outcome result;
    result.error = error;
    result.message = message;
    return result;
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

// Un argument, cité comme le veut `CommandLineToArgvW` : les guillemets et les antislashs qui les précèdent sont échappés.
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

bool name_equals(const std::wstring& entry, const std::wstring& name) {
    if (entry.size() <= name.size() || entry[name.size()] != L'=') return false;
    return ::CompareStringOrdinal(entry.c_str(), static_cast<int>(name.size()), name.c_str(), static_cast<int>(name.size()), TRUE) == CSTR_EQUAL;
}

// L'environnement de ce programme, avec les variables données ajoutées ou remplacées, en bloc « NOM=valeur\0…\0\0 ».
bool environment_block(const std::vector<std::pair<string, string>>& extra, std::wstring& block) {
    std::vector<std::wstring> entries;
    if (wchar_t* current = ::GetEnvironmentStringsW()) {
        for (const wchar_t* p = current; *p;) {
            const std::wstring entry(p);
            // Les entrées d'unité (« =C:=… ») commencent par « = » : elles restent telles quelles.
            entries.push_back(entry);
            p += entry.size() + 1;
        }
        ::FreeEnvironmentStringsW(current);
    }
    for (const auto& [key, value] : extra) {
        std::wstring name, text;
        if (!to_wide(key, name) || !to_wide(value, text)) return false;
        bool replaced = false;
        for (auto& entry : entries) {
            if (name_equals(entry, name)) {
                entry = name + L"=" + text;
                replaced = true;
                break;
            }
        }
        if (!replaced) entries.push_back(name + L"=" + text);
    }
    block.clear();
    for (const auto& entry : entries) {
        block += entry;
        block.push_back(L'\0');
    }
    block.push_back(L'\0');
    return true;
}

// Le côté du processus d'une de ses sorties, selon `mode`, et le côté de ce programme (`parent_read`) pour `pipe`.
bool child_output(output mode, DWORD standard, HANDLE& child, HANDLE& parent_read) {
    SECURITY_ATTRIBUTES inheritable{sizeof inheritable, nullptr, TRUE};
    child = nullptr;
    parent_read = nullptr;
    switch (mode) {
        case output::pipe: {
            HANDLE read = nullptr;
            if (!::CreatePipe(&read, &child, &inheritable, 0)) return false;
            ::SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0);
            parent_read = read;
            return true;
        }
        case output::null:
            child = ::CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &inheritable, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            return child != INVALID_HANDLE_VALUE;
        default: {
            const HANDLE own = ::GetStdHandle(standard);
            if (own == nullptr || own == INVALID_HANDLE_VALUE) return true;
            // Une copie héritable : le processus écrit là où ce programme écrit.
            return ::DuplicateHandle(::GetCurrentProcess(), own, ::GetCurrentProcess(), &child, 0, TRUE, DUPLICATE_SAME_ACCESS) != 0;
        }
    }
}

// Lit ce qui est déjà dans le tube, sans attendre ; vrai si quelque chose a été lu. Un tube fermé est fermé ici.
bool drain(HANDLE& pipe, std::string& into) {
    if (pipe == nullptr) return false;
    DWORD available = 0;
    if (!::PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) {
        ::CloseHandle(pipe);
        pipe = nullptr;
        return false;
    }
    if (available == 0) return false;
    char buffer[chunk_size];
    DWORD read = 0;
    if (!::ReadFile(pipe, buffer, available < sizeof buffer ? available : static_cast<DWORD>(sizeof buffer), &read, nullptr) || read == 0) {
        ::CloseHandle(pipe);
        pipe = nullptr;
        return false;
    }
    into.append(buffer, read);
    return true;
}

void close_handle(HANDLE& handle) {
    if (handle != nullptr && handle != INVALID_HANDLE_VALUE) ::CloseHandle(handle);
    handle = nullptr;
}

outcome spawn(const string& program, const std::vector<string>& args, const options& opts) {
    std::wstring command, wide_program, wide_cwd, block;
    if (!to_wide(program, wide_program)) return failed(failure::io_error, string("le chemin de l'exécutable n'est pas de l'UTF-8 valide"));
    append_quoted(command, wide_program);
    for (const auto& argument : args) {
        std::wstring wide;
        if (!to_wide(argument, wide)) return failed(failure::io_error, string("un argument n'est pas de l'UTF-8 valide"));
        command.push_back(L' ');
        append_quoted(command, wide);
    }
    if (!to_wide(opts.cwd, wide_cwd)) return failed(failure::io_error, string("le dossier de travail n'est pas de l'UTF-8 valide"));
    if (!opts.env.empty() && !environment_block(opts.env, block)) return failed(failure::io_error, string("une variable d'environnement n'est pas de l'UTF-8 valide"));

    HANDLE child_in = nullptr, child_out = nullptr, child_err = nullptr, out_read = nullptr, err_read = nullptr;
    auto cleanup = [&]() {
        close_handle(child_in);
        close_handle(child_out);
        close_handle(child_err);
        close_handle(out_read);
        close_handle(err_read);
    };

    SECURITY_ATTRIBUTES inheritable{sizeof inheritable, nullptr, TRUE};
    if (opts.in == input::null) {
        child_in = ::CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &inheritable, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (child_in == INVALID_HANDLE_VALUE) child_in = nullptr;
    }
    if (!child_output(opts.out, STD_OUTPUT_HANDLE, child_out, out_read) || !child_output(opts.err, STD_ERROR_HANDLE, child_err, err_read)) {
        cleanup();
        return failed(failure::io_error, string("impossible de créer les tubes du processus"));
    }

    STARTUPINFOW startup{};
    startup.cb = sizeof startup;
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = child_in;
    startup.hStdOutput = child_out;
    startup.hStdError = child_err;

    PROCESS_INFORMATION info{};
    const BOOL created = ::CreateProcessW(nullptr, command.data(), nullptr, nullptr, TRUE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                                          opts.env.empty() ? nullptr : block.data(), wide_cwd.empty() ? nullptr : wide_cwd.c_str(), &startup, &info);
    const DWORD error = created ? 0 : ::GetLastError();
    // Le processus a ses extrémités : ce programme ferme les siennes, pour voir la fin des sorties.
    close_handle(child_in);
    close_handle(child_out);
    close_handle(child_err);
    if (!created) {
        cleanup();
        switch (error) {
            case ERROR_FILE_NOT_FOUND:
            case ERROR_PATH_NOT_FOUND:
                return failed(failure::exec_not_found, interpolate(string("exécutable introuvable : "), program));
            case ERROR_ACCESS_DENIED:
            case ERROR_BAD_EXE_FORMAT:
                return failed(failure::permission_denied, interpolate(string("lancement refusé : "), program));
            default:
                return failed(failure::io_error, interpolate(string("échec du lancement (code "), static_cast<std::uint32_t>(error), string(") : "), program));
        }
    }
    ::CloseHandle(info.hThread);

    std::string captured_out, captured_err;
    const ULONGLONG start = ::GetTickCount64();
    bool timed_out = false;
    while (true) {
        const bool from_out = drain(out_read, captured_out);
        const bool from_err = drain(err_read, captured_err);
        const bool got = from_out || from_err;
        if (::WaitForSingleObject(info.hProcess, 0) == WAIT_OBJECT_0) {
            while (true) {
                const bool last_out = drain(out_read, captured_out);
                const bool last_err = drain(err_read, captured_err);
                if (!last_out && !last_err) break;
            }
            break;
        }
        if (opts.timeout_ms != 0 && ::GetTickCount64() - start >= opts.timeout_ms) {
            ::TerminateProcess(info.hProcess, 1);
            ::WaitForSingleObject(info.hProcess, 5000);
            timed_out = true;
            break;
        }
        if (!got) ::Sleep(1);
    }

    DWORD code = 0;
    ::GetExitCodeProcess(info.hProcess, &code);
    ::CloseHandle(info.hProcess);
    cleanup();

    if (timed_out) return failed(failure::timeout, interpolate(string("le processus a dépassé "), opts.timeout_ms, string(" ms : "), program));
    outcome result;
    result.exit_code = static_cast<std::int32_t>(code);
    result.out = lossy(captured_out);
    result.err = lossy(captured_err);
    return result;
}

#else

void close_fd(int& fd) {
    if (fd >= 0) ::close(fd);
    fd = -1;
}

long long now_ms() {
    timespec time{};
    ::clock_gettime(CLOCK_MONOTONIC, &time);
    return static_cast<long long>(time.tv_sec) * 1000 + time.tv_nsec / 1000000;
}

// Lit ce qui vient dans le tube ; faux quand il est fermé.
bool drain(int& fd, std::string& into) {
    char buffer[chunk_size];
    const ssize_t count = ::read(fd, buffer, sizeof buffer);
    if (count > 0) {
        into.append(buffer, static_cast<std::size_t>(count));
        return true;
    }
    if (count < 0 && (errno == EINTR || errno == EAGAIN)) return true;
    close_fd(fd);
    return false;
}

outcome spawn(const string& program, const std::vector<string>& args, const options& opts) {
    int out_pipe[2] = {-1, -1};
    int err_pipe[2] = {-1, -1};
    int exec_pipe[2] = {-1, -1};
    auto cleanup = [&]() {
        for (int* pair : {out_pipe, err_pipe, exec_pipe}) {
            close_fd(pair[0]);
            close_fd(pair[1]);
        }
    };
    if (::pipe(exec_pipe) != 0 || (opts.out == output::pipe && ::pipe(out_pipe) != 0) || (opts.err == output::pipe && ::pipe(err_pipe) != 0)) {
        cleanup();
        return failed(failure::io_error, string("impossible de créer les tubes du processus"));
    }
    ::fcntl(exec_pipe[0], F_SETFD, FD_CLOEXEC);
    ::fcntl(exec_pipe[1], F_SETFD, FD_CLOEXEC);

    std::vector<std::string> storage;
    storage.emplace_back(program.data(), program.size());
    for (const auto& argument : args) storage.emplace_back(argument.data(), argument.size());
    std::vector<char*> argv;
    for (auto& item : storage) argv.push_back(item.data());
    argv.push_back(nullptr);

    const pid_t pid = ::fork();
    if (pid < 0) {
        cleanup();
        return failed(failure::io_error, string("fork a échoué"));
    }

    if (pid == 0) {
        // Le processus enfant : ses entrées et sorties, son dossier, son environnement, puis l'exécutable.
        auto report = [&](int error) {
            const ssize_t written = ::write(exec_pipe[1], &error, sizeof error);
            (void)written;
            ::_exit(127);
        };
        if (opts.in == input::none) {
            ::close(0);
        } else {
            const int null = ::open("/dev/null", O_RDONLY);
            if (null >= 0) ::dup2(null, 0);
        }
        auto redirect = [&](output mode, int pipe_write, int target) {
            if (mode == output::pipe) {
                ::dup2(pipe_write, target);
            } else if (mode == output::null) {
                const int null = ::open("/dev/null", O_WRONLY);
                if (null >= 0) ::dup2(null, target);
            }
        };
        redirect(opts.out, out_pipe[1], 1);
        redirect(opts.err, err_pipe[1], 2);
        for (int* pair : {out_pipe, err_pipe}) {
            if (pair[0] >= 0) ::close(pair[0]);
            if (pair[1] >= 0) ::close(pair[1]);
        }
        if (opts.cwd.size() != 0 && ::chdir(opts.cwd.c_str()) != 0) report(errno);
        for (const auto& [key, value] : opts.env) ::setenv(key.c_str(), value.c_str(), 1);
        ::execvp(storage[0].c_str(), argv.data());
        report(errno);
    }

    // Ce programme : il garde le côté lecture de chaque tube.
    close_fd(exec_pipe[1]);
    close_fd(out_pipe[1]);
    close_fd(err_pipe[1]);

    int exec_error = 0;
    ssize_t got;
    do {
        got = ::read(exec_pipe[0], &exec_error, sizeof exec_error);
    } while (got < 0 && errno == EINTR);
    close_fd(exec_pipe[0]);
    if (got == static_cast<ssize_t>(sizeof exec_error)) {
        int status = 0;
        ::waitpid(pid, &status, 0);
        cleanup();
        switch (exec_error) {
            case ENOENT:
            case ENOTDIR:
                return failed(failure::exec_not_found, interpolate(string("exécutable introuvable : "), program));
            case EACCES:
            case EPERM:
            case ENOEXEC:
                return failed(failure::permission_denied, interpolate(string("lancement refusé : "), program));
            default:
                return failed(failure::io_error, interpolate(string("échec du lancement (errno "), exec_error, string(") : "), program));
        }
    }

    std::string captured_out, captured_err;
    const long long start = now_ms();
    bool timed_out = false;
    while (out_pipe[0] >= 0 || err_pipe[0] >= 0) {
        pollfd fds[2];
        nfds_t count = 0;
        int* owners[2];
        for (int* pair : {out_pipe, err_pipe}) {
            if (pair[0] < 0) continue;
            fds[count] = {pair[0], POLLIN, 0};
            owners[count++] = pair;
        }
        int wait = -1;
        if (opts.timeout_ms != 0) {
            const long long left = static_cast<long long>(opts.timeout_ms) - (now_ms() - start);
            if (left <= 0) {
                timed_out = true;
                break;
            }
            wait = static_cast<int>(left);
        }
        const int ready = ::poll(fds, count, wait);
        if (ready < 0 && errno != EINTR) break;
        for (nfds_t i = 0; i < count; ++i) {
            if (fds[i].revents == 0) continue;
            drain(owners[i][0], owners[i] == out_pipe ? captured_out : captured_err);
        }
    }

    if (timed_out) {
        ::kill(pid, SIGKILL);
    } else if (opts.timeout_ms != 0) {
        // Les sorties sont fermées : le processus finit, mais dans le temps qui reste.
        int status = 0;
        while (::waitpid(pid, &status, WNOHANG) == 0) {
            if (now_ms() - start >= static_cast<long long>(opts.timeout_ms)) {
                timed_out = true;
                ::kill(pid, SIGKILL);
                break;
            }
            ::usleep(1000);
        }
        if (!timed_out) {
            cleanup();
            outcome result;
            result.exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
            result.out = lossy(captured_out);
            result.err = lossy(captured_err);
            return result;
        }
    }

    int status = 0;
    while (::waitpid(pid, &status, 0) < 0 && errno == EINTR) {
    }
    cleanup();
    if (timed_out) return failed(failure::timeout, interpolate(string("le processus a dépassé "), opts.timeout_ms, string(" ms : "), program));

    outcome result;
    result.exit_code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    result.out = lossy(captured_out);
    result.err = lossy(captured_err);
    return result;
}

#endif

}  // namespace

outcome run(const string& program, const std::vector<string>& args, const options& opts) {
    if (program.size() == 0) return failed(failure::exec_not_found, string("aucun exécutable donné"));
    if (is_compiler(program)) return failed(failure::permission_denied, string("un programme RGL ne relance pas rgl : cette décision reste à l'humain"));
    return spawn(program, args, opts);
}

}  // namespace rgl::standard::process

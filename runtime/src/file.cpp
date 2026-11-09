// Composant « file » du runtime : lecture et écriture de fichiers entiers, chemins en UTF-8.

#include "rgl/standard/file.hpp"

#include <cstring>
#include <memory>

#include "rgl/standard/memory.hpp"

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
#include <unistd.h>
#endif

namespace rgl::standard::file {

namespace {

constexpr std::size_t chunk_size = 65536;

#if defined(_WIN32)

// Le chemin UTF-8 en UTF-16 terminé par un zéro, dans un bloc libéré avec lui.
class wide_path {
   public:
    // Faux (et rien d'alloué) si le chemin est vide ou n'est pas de l'UTF-8 valide.
    explicit wide_path(const string& path) {
        if (path.size() == 0) return;
        const int count = ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path.data(), static_cast<int>(path.size()), nullptr, 0);
        if (count <= 0) return;
        text_ = static_cast<wchar_t*>(allocate_or_abort(heap_allocator(), (static_cast<std::size_t>(count) + 1) * sizeof(wchar_t)));
        ::MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path.data(), static_cast<int>(path.size()), text_, count);
        text_[count] = L'\0';
    }
    wide_path(const wide_path&) = delete;
    wide_path& operator=(const wide_path&) = delete;
    ~wide_path() {
        if (text_) release_block(heap_allocator(), text_);
    }
    explicit operator bool() const { return text_ != nullptr; }
    const wchar_t* c_str() const { return text_; }

   private:
    wchar_t* text_ = nullptr;
};

status from_error(DWORD error) {
    switch (error) {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND:
        case ERROR_INVALID_NAME:
        case ERROR_BAD_PATHNAME:
            return status::not_found;
        case ERROR_ACCESS_DENIED:
        case ERROR_SHARING_VIOLATION:
        case ERROR_LOCK_VIOLATION:
            return status::access_denied;
        default:
            return status::failed;
    }
}

status read_all(const string& path, mstring& content) {
    wide_path wide(path);
    if (!wide) return status::failed;
    HANDLE handle = ::CreateFileW(wide.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                  OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return from_error(::GetLastError());

    char buffer[chunk_size];
    status result = status::ok;
    while (true) {
        DWORD read = 0;
        if (!::ReadFile(handle, buffer, static_cast<DWORD>(sizeof buffer), &read, nullptr)) {
            result = from_error(::GetLastError());
            break;
        }
        if (read == 0) break;
        content.append(buffer, read);
    }
    ::CloseHandle(handle);
    return result;
}

status write_all(const string& path, const char* data, std::size_t size) {
    wide_path wide(path);
    if (!wide) return status::failed;
    HANDLE handle = ::CreateFileW(wide.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return from_error(::GetLastError());

    status result = status::ok;
    while (size > 0) {
        const DWORD count = size > chunk_size ? static_cast<DWORD>(chunk_size) : static_cast<DWORD>(size);
        DWORD written = 0;
        if (!::WriteFile(handle, data, count, &written, nullptr) || written == 0) {
            result = from_error(::GetLastError());
            if (result == status::ok) result = status::failed;
            break;
        }
        data += written;
        size -= written;
    }
    if (!::CloseHandle(handle) && result == status::ok) result = status::failed;
    return result;
}

#else

status from_error(int error) {
    switch (error) {
        case ENOENT:
        case ENOTDIR:
            return status::not_found;
        case EACCES:
        case EPERM:
        case EISDIR:
        case ETXTBSY:
            return status::access_denied;
        default:
            return status::failed;
    }
}

status read_all(const string& path, mstring& content) {
    if (path.size() == 0) return status::failed;
    const int descriptor = ::open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) return from_error(errno);

    char buffer[chunk_size];
    status result = status::ok;
    while (true) {
        const ssize_t read = ::read(descriptor, buffer, sizeof buffer);
        if (read < 0) {
            if (errno == EINTR) continue;
            result = from_error(errno);
            break;
        }
        if (read == 0) break;
        content.append(buffer, static_cast<std::size_t>(read));
    }
    ::close(descriptor);
    return result;
}

status write_all(const string& path, const char* data, std::size_t size) {
    if (path.size() == 0) return status::failed;
    const int descriptor = ::open(path.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (descriptor < 0) return from_error(errno);

    status result = status::ok;
    while (size > 0) {
        const ssize_t written = ::write(descriptor, data, size);
        if (written < 0) {
            if (errno == EINTR) continue;
            result = from_error(errno);
            break;
        }
        data += written;
        size -= static_cast<std::size_t>(written);
    }
    if (::close(descriptor) != 0 && result == status::ok) result = status::failed;
    return result;
}

#endif

}  // namespace

status read_text(const string& path, string& text) {
    mstring content;
    const status result = read_all(path, content);
    if (result != status::ok) return result;

    const char* data = content.data();
    std::size_t size = content.size();
    // Le BOM n'est pas du texte : il marque seulement l'encodage.
    if (size >= 3 && static_cast<unsigned char>(data[0]) == 0xEF && static_cast<unsigned char>(data[1]) == 0xBB &&
        static_cast<unsigned char>(data[2]) == 0xBF) {
        data += 3;
        size -= 3;
    }
    if (!bytes::is_valid_utf8(data, size)) return status::invalid_utf8;

    text = string(data, size);
    return status::ok;
}

status read_bytes(const string& path, array<std::uint8_t>& bytes) {
    mstring content;
    const status result = read_all(path, content);
    if (result != status::ok) return result;

    array<std::uint8_t> read;
    read.reserve(content.size());
    for (std::size_t i = 0; i < content.size(); ++i) read.push(static_cast<std::uint8_t>(content.data()[i]));
    bytes = static_cast<array<std::uint8_t>&&>(read);
    return status::ok;
}

status write(const string& path, const char* data, std::size_t size) { return write_all(path, data, size); }

string message(status result) {
    switch (result) {
        case status::ok:
            return string("aucune erreur");
        case status::not_found:
            return string("fichier introuvable");
        case status::access_denied:
            return string("accès refusé");
        case status::invalid_utf8:
            return string("le fichier n'est pas de l'UTF-8 valide");
        case status::closed:
            return string("le flux est fermé");
        case status::wrong_mode:
            return string("le flux n'est pas ouvert pour cette opération");
        default:
            return string("échec de l'entrée-sortie");
    }
}

// ---- Flux ouverts

namespace {

#if defined(_WIN32)

status native_open(const string& path, open_mode mode, std::intptr_t& handle) {
    wide_path wide(path);
    if (!wide) return status::failed;
    const DWORD access = mode == open_mode::read ? GENERIC_READ : mode == open_mode::write ? GENERIC_WRITE : FILE_APPEND_DATA;
    const DWORD share = mode == open_mode::read ? (FILE_SHARE_READ | FILE_SHARE_WRITE) : FILE_SHARE_READ;
    const DWORD creation = mode == open_mode::read ? OPEN_EXISTING : mode == open_mode::write ? CREATE_ALWAYS : OPEN_ALWAYS;
    HANDLE opened = ::CreateFileW(wide.c_str(), access, share, nullptr, creation, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (opened == INVALID_HANDLE_VALUE) return from_error(::GetLastError());
    handle = reinterpret_cast<std::intptr_t>(opened);
    return status::ok;
}

// Le nombre d'octets lus (0 en fin de fichier), ou -1.
long native_read(std::intptr_t handle, char* buffer, std::size_t size) {
    DWORD read = 0;
    if (!::ReadFile(reinterpret_cast<HANDLE>(handle), buffer, static_cast<DWORD>(size), &read, nullptr)) return -1;
    return static_cast<long>(read);
}

bool native_write(std::intptr_t handle, const char* data, std::size_t size) {
    while (size > 0) {
        const DWORD count = size > chunk_size ? static_cast<DWORD>(chunk_size) : static_cast<DWORD>(size);
        DWORD written = 0;
        if (!::WriteFile(reinterpret_cast<HANDLE>(handle), data, count, &written, nullptr) || written == 0) return false;
        data += written;
        size -= written;
    }
    return true;
}

void native_close(std::intptr_t handle) noexcept { ::CloseHandle(reinterpret_cast<HANDLE>(handle)); }

#else

status native_open(const string& path, open_mode mode, std::intptr_t& handle) {
    if (path.size() == 0) return status::failed;
    const int flags = mode == open_mode::read ? O_RDONLY : mode == open_mode::write ? (O_WRONLY | O_CREAT | O_TRUNC) : (O_WRONLY | O_CREAT | O_APPEND);
    const int descriptor = ::open(path.c_str(), flags | O_CLOEXEC, 0644);
    if (descriptor < 0) return from_error(errno);
    handle = descriptor;
    return status::ok;
}

long native_read(std::intptr_t handle, char* buffer, std::size_t size) {
    while (true) {
        const ssize_t read = ::read(static_cast<int>(handle), buffer, size);
        if (read >= 0) return static_cast<long>(read);
        if (errno != EINTR) return -1;
    }
}

bool native_write(std::intptr_t handle, const char* data, std::size_t size) {
    while (size > 0) {
        const ssize_t written = ::write(static_cast<int>(handle), data, size);
        if (written < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        data += written;
        size -= static_cast<std::size_t>(written);
    }
    return true;
}

void native_close(std::intptr_t handle) noexcept { ::close(static_cast<int>(handle)); }

#endif

}  // namespace

struct stream::state {
    string path;
    open_mode mode = open_mode::read;
    bool open = false;
    std::intptr_t handle = -1;
    // La lecture se fait par blocs : une ligne peut s'étendre sur plusieurs, un bloc contenir plusieurs lignes.
    char buffer[chunk_size];
    std::size_t start = 0;
    std::size_t end = 0;
    bool eof = false;

    state() = default;
    state(const state&) = delete;
    state& operator=(const state&) = delete;
    ~state() { close_handle(); }

    void close_handle() noexcept {
        if (!open) return;
        open = false;
        native_close(handle);
    }
};

status open(const string& path, open_mode mode, stream& out) {
    std::intptr_t handle = -1;
    if (const status result = native_open(path, mode, handle); result != status::ok) return result;
    auto opened = std::make_shared<stream::state>();
    opened->path = path;
    opened->mode = mode;
    opened->open = true;
    opened->handle = handle;
    out._state = static_cast<std::shared_ptr<stream::state>&&>(opened);
    return status::ok;
}

string stream::path() const { return _state ? _state->path : string(); }

bool stream::is_open() const noexcept { return _state && _state->open; }

status stream::read_line(nullable<string>& line) const {
    if (!is_open()) return status::closed;
    state& s = *_state;
    if (s.mode != open_mode::read) return status::wrong_mode;

    mstring text;
    bool found = false;
    bool any = false;
    while (!found) {
        if (s.start == s.end) {
            if (s.eof) break;
            const long count = native_read(s.handle, s.buffer, sizeof s.buffer);
            if (count < 0) return status::failed;
            if (count == 0) {
                s.eof = true;
                break;
            }
            s.start = 0;
            s.end = static_cast<std::size_t>(count);
        }
        const char* begin = s.buffer + s.start;
        const std::size_t available = s.end - s.start;
        any = true;
        if (const void* newline = std::memchr(begin, '\n', available)) {
            const std::size_t length = static_cast<std::size_t>(static_cast<const char*>(newline) - begin);
            text.append(begin, length);
            s.start += length + 1;
            found = true;
        } else {
            text.append(begin, available);
            s.start = s.end;
        }
    }

    if (!any) {
        line.reset();
        return status::ok;
    }
    if (!text.empty() && text.data()[text.size() - 1] == '\r') text.resize(text.size() - 1);
    if (!bytes::is_valid_utf8(text.data(), text.size())) return status::invalid_utf8;
    line.emplace(text.to_immutable());
    return status::ok;
}

status stream::read_bytes(std::size_t count, array<std::uint8_t>& bytes) const {
    if (!is_open()) return status::closed;
    state& s = *_state;
    if (s.mode != open_mode::read) return status::wrong_mode;

    array<std::uint8_t> result;
    result.reserve(count < chunk_size ? count : chunk_size);
    while (result.size() < count) {
        if (s.start == s.end) {
            if (s.eof) break;
            const long read = native_read(s.handle, s.buffer, sizeof s.buffer);
            if (read < 0) return status::failed;
            if (read == 0) {
                s.eof = true;
                break;
            }
            s.start = 0;
            s.end = static_cast<std::size_t>(read);
        }
        const std::size_t wanted = count - result.size();
        const std::size_t take = s.end - s.start < wanted ? s.end - s.start : wanted;
        for (std::size_t i = 0; i < take; ++i) result.push(static_cast<std::uint8_t>(s.buffer[s.start + i]));
        s.start += take;
    }
    bytes = static_cast<array<std::uint8_t>&&>(result);
    return status::ok;
}

status stream::write(const char* data, std::size_t size) const {
    if (!is_open()) return status::closed;
    if (_state->mode == open_mode::read) return status::wrong_mode;
    return native_write(_state->handle, data, size) ? status::ok : status::failed;
}

void stream::close() const noexcept {
    if (_state) _state->close_handle();
}

}  // namespace rgl::standard::file

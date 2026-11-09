// Composant « console » du runtime : écriture et lecture de la console en UTF-8.

#include "rgl/standard/console.hpp"

#include <csignal>
#include <cstdlib>
#include <cstring>
#include <mutex>

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
#include <unistd.h>
#endif

namespace rgl::standard::console {

namespace {

// La console est partagée par tous les threads (une tâche du pool peut écrire pendant que la boucle écrit aussi) : une écriture ou une lecture se fait
// sous ce verrou, ce qui garde chaque écriture entière — deux écritures de threads différents ne s'entrecoupent jamais au milieu.
std::mutex console_lock;

// L'entrée standard lue par blocs : une ligne peut s'étendre sur plusieurs blocs, et un bloc
// contenir plusieurs lignes.
char input[4096];
std::size_t input_start = 0;
std::size_t input_end = 0;

// Lit le bloc suivant de l'entrée standard ; faux en fin d'entrée (ou sur une erreur de lecture).
bool fill_input();

// Écrit tout de suite sur la sortie standard.
void write_now(const char* data, std::size_t size);

// Vrai si la sortie standard n'est pas une vraie console (un fichier, un tube).
bool is_redirected();

// La sortie redirigée passe par un tampon : un appel système par bloc, pas par écriture. Une vraie
// console écrit tout de suite, pour qu'on voie le texte au fur et à mesure.
char pending[16384];
std::size_t pending_size = 0;

void flush_output() {
    if (pending_size == 0) return;
    const std::size_t size = pending_size;
    pending_size = 0;
    write_now(pending, size);
}

// La fin normale du programme vide le tampon sous le verrou.
void flush_at_exit() {
    const std::lock_guard lock(console_lock);
    flush_output();
}

// Un arrêt du programme (abort, sur une erreur RGL) vide d'abord le tampon : rien d'écrit ne se perd. Si un thread tient le verrou, l'arrêt vient peut-être
// de lui : on ne l'attend pas (un signal ne bloque pas), le tampon part tel quel.
void flush_on_abort(int) {
    std::unique_lock lock(console_lock, std::try_to_lock);
    flush_output();
}

// 0 : pas encore décidé ; 1 : tamponnée ; 2 : directe.
int output_mode = 0;

bool buffered() {
    if (output_mode == 0) {
        output_mode = is_redirected() ? 1 : 2;
        if (output_mode == 1) {
            std::atexit(flush_at_exit);
            std::signal(SIGABRT, flush_on_abort);
        }
    }
    return output_mode == 1;
}

// Une ligne lue octet par octet dans l'entrée standard, sans son retour à la ligne.
string read_bytes_line() {
    mstring line;
    while (input_start < input_end || fill_input()) {
        for (std::size_t i = input_start; i < input_end; ++i) {
            if (input[i] != '\n') continue;
            line.append(input + input_start, i - input_start);
            input_start = i + 1;
            if (!line.empty() && line.data()[line.size() - 1] == '\r') line.resize(line.size() - 1);
            return line.to_immutable();
        }
        line.append(input + input_start, input_end - input_start);
        input_start = input_end;
    }

    // Dernière ligne, sans retour à la ligne final.
    if (!line.empty() && line.data()[line.size() - 1] == '\r') line.resize(line.size() - 1);
    return line.to_immutable();
}

#if defined(_WIN32)

bool fill_input() {
    DWORD read = 0;
    if (!::ReadFile(::GetStdHandle(STD_INPUT_HANDLE), input, sizeof input, &read, nullptr) || read == 0) return false;
    input_start = 0;
    input_end = read;
    return true;
}

// Une vraie console, et non une redirection vers un fichier ou un tube.
bool is_console(HANDLE handle) {
    DWORD mode = 0;
    return handle != nullptr && handle != INVALID_HANDLE_VALUE && ::GetConsoleMode(handle, &mode);
}

// Une ligne tapée dans la console, lue en UTF-16 puis convertie en UTF-8.
string read_console_line(HANDLE handle) {
    wchar_t* text = nullptr;
    std::size_t size = 0;
    std::size_t capacity = 0;

    while (true) {
        if (capacity - size < 256) {
            capacity = capacity == 0 ? 512 : capacity * 2;
            text = static_cast<wchar_t*>(reallocate_or_abort(heap_allocator(), text, capacity * sizeof(wchar_t)));
        }

        DWORD read = 0;
        if (!::ReadConsoleW(handle, text + size, static_cast<DWORD>(capacity - size), &read, nullptr) || read == 0) break;
        size += read;
        if (text[size - 1] == L'\n') break;
    }

    // Sans le retour à la ligne : « \r\n » dans la console.
    while (size > 0 && (text[size - 1] == L'\n' || text[size - 1] == L'\r')) --size;

    mstring line;
    if (size > 0) {
        const int bytes = ::WideCharToMultiByte(CP_UTF8, 0, text, static_cast<int>(size), nullptr, 0, nullptr, nullptr);
        line.resize(static_cast<std::size_t>(bytes));
        ::WideCharToMultiByte(CP_UTF8, 0, text, static_cast<int>(size), line.data(), bytes, nullptr, nullptr);
    }
    if (text) release_block(heap_allocator(), text);
    return line.to_immutable();
}

bool is_redirected() { return !is_console(::GetStdHandle(STD_OUTPUT_HANDLE)); }

void write_now(const char* data, std::size_t size) {
    if (size == 0) return;
    const HANDLE output = ::GetStdHandle(STD_OUTPUT_HANDLE);
    if (output == nullptr || output == INVALID_HANDLE_VALUE) return;

    if (!is_console(output)) {
        // Redirigée : les octets UTF-8 tels quels.
        while (size > 0) {
            DWORD written = 0;
            const DWORD chunk = size > 0x40000000u ? 0x40000000u : static_cast<DWORD>(size);
            if (!::WriteFile(output, data, chunk, &written, nullptr) || written == 0) return;
            data += written;
            size -= written;
        }
        return;
    }

    // Une console : en UTF-16, que la page de code de la console soit UTF-8 ou non.
    const int length = ::MultiByteToWideChar(CP_UTF8, 0, data, static_cast<int>(size), nullptr, 0);
    if (length <= 0) return;
    auto* text = static_cast<wchar_t*>(allocate_or_abort(heap_allocator(), static_cast<std::size_t>(length) * sizeof(wchar_t)));
    ::MultiByteToWideChar(CP_UTF8, 0, data, static_cast<int>(size), text, length);

    for (DWORD done = 0; done < static_cast<DWORD>(length);) {
        DWORD written = 0;
        if (!::WriteConsoleW(output, text + done, static_cast<DWORD>(length) - done, &written, nullptr) || written == 0) break;
        done += written;
    }
    release_block(heap_allocator(), text);
}

}  // namespace

string read_line() {
    const std::lock_guard lock(console_lock);
    flush_output();
    const HANDLE handle = ::GetStdHandle(STD_INPUT_HANDLE);
    return is_console(handle) ? read_console_line(handle) : read_bytes_line();
}

#else

bool fill_input() {
    while (true) {
        const ssize_t read = ::read(0, input, sizeof input);
        if (read > 0) {
            input_start = 0;
            input_end = static_cast<std::size_t>(read);
            return true;
        }
        if (read < 0 && errno == EINTR) continue;
        return false;
    }
}

bool is_redirected() { return ::isatty(1) == 0; }

void write_now(const char* data, std::size_t size) {
    while (size > 0) {
        const ssize_t written = ::write(1, data, size);
        if (written < 0 && errno == EINTR) continue;
        if (written <= 0) return;
        data += written;
        size -= static_cast<std::size_t>(written);
    }
}

}  // namespace

string read_line() {
    const std::lock_guard lock(console_lock);
    flush_output();
    return read_bytes_line();
}

#endif

string read_all() {
    const std::lock_guard lock(console_lock);
    flush_output();
    mstring all;
#if defined(_WIN32)
    if (is_console(::GetStdHandle(STD_INPUT_HANDLE))) {
        for (;;) {
            const string line = read_console_line(::GetStdHandle(STD_INPUT_HANDLE));
            if (line.size() == 0) break;
            all.append(line.data(), line.size());
            all.append("\n", 1);
        }
        return all.to_immutable();
    }
#endif
    while (input_start < input_end || fill_input()) {
        all.append(input + input_start, input_end - input_start);
        input_start = input_end;
    }
    return all.to_immutable();
}

void flush() { flush_output(); }

void write(const char* data, std::size_t size) {
    if (size == 0) return;
    const std::lock_guard lock(console_lock);
    if (!buffered()) {
        write_now(data, size);
        return;
    }
    if (size > sizeof pending - pending_size) flush_output();
    if (size > sizeof pending) {
        write_now(data, size);
        return;
    }
    std::memcpy(pending + pending_size, data, size);
    pending_size += size;
}

}  // namespace rgl::standard::console

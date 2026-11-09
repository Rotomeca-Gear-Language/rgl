#include "rgl/standard/system.hpp"

#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <map>
#include <string>
#include <thread>

#include "rgl/standard/console.hpp"

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <poll.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <unistd.h>
#endif

namespace rgl::standard::system {

namespace {

namespace fs = std::filesystem;

string make_string(const std::string& text) { return string(text.c_str(), text.size()); }

using clock = std::chrono::steady_clock;

// Le temps qui reste avant l'échéance, en millisecondes (jamais négatif) ; `infinite` si `timeout_ms` vaut 0.
std::int64_t remaining_ms(clock::time_point start, std::uint32_t timeout_ms) {
    if (timeout_ms == 0) return -1;
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - start).count();
    const auto left = static_cast<std::int64_t>(timeout_ms) - elapsed;
    return left < 0 ? 0 : left;
}

#if defined(_WIN32)

HANDLE input_handle() { return ::GetStdHandle(STD_INPUT_HANDLE); }
HANDLE output_handle() { return ::GetStdHandle(STD_OUTPUT_HANDLE); }

bool is_console(HANDLE handle) {
    DWORD mode = 0;
    return handle != INVALID_HANDLE_VALUE && handle != nullptr && ::GetConsoleMode(handle, &mode) != 0;
}

std::string wide_to_utf8(const wchar_t* text, int length) {
    if (length <= 0) return {};
    const int bytes = ::WideCharToMultiByte(CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(bytes), '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, text, length, result.data(), bytes, nullptr, nullptr);
    return result;
}

#else

bool is_tty(int descriptor) { return ::isatty(descriptor) != 0; }

// Met le terminal en lecture touche par touche (sans écho ni ligne) et le remet comme il était à la destruction.
struct raw_mode {
    termios saved{};
    bool active = false;
    explicit raw_mode(bool echo) {
        if (::tcgetattr(0, &saved) != 0) return;
        termios changed = saved;
        changed.c_lflag &= static_cast<tcflag_t>(~(ICANON | (echo ? 0 : ECHO)));
        changed.c_cc[VMIN] = 1;
        changed.c_cc[VTIME] = 0;
        active = ::tcsetattr(0, TCSANOW, &changed) == 0;
    }
    ~raw_mode() {
        if (active) ::tcsetattr(0, TCSANOW, &saved);
    }
    raw_mode(const raw_mode&) = delete;
    raw_mode& operator=(const raw_mode&) = delete;
};

// Attend qu'un octet soit lisible : vrai s'il l'est, faux au délai (-1 : sans limite).
bool wait_readable(int milliseconds) {
    pollfd descriptor{0, POLLIN, 0};
    return ::poll(&descriptor, 1, milliseconds) > 0;
}

bool read_byte(unsigned char& out) { return ::read(0, &out, 1) == 1; }

#endif

// Les signaux gardés, par numéro ; le gestionnaire ne fait qu'écrire dans ce tableau.
volatile std::sig_atomic_t pending_signals[65];

struct named_signal {
    const char* name;
    int number;
};

const named_signal known_signals[] = {
    {"interrupt", SIGINT},
    {"terminate", SIGTERM},
#if !defined(_WIN32)
    {"hangup", SIGHUP},
    {"user1", SIGUSR1},
    {"user2", SIGUSR2},
#endif
};

extern "C" void rgl_signal_handler(int number) {
    if (number > 0 && number < 65) pending_signals[number] = 1;
    std::signal(number, rgl_signal_handler);
}

// Un instantané d'un fichier ou d'un dossier : pour chaque chemin, sa taille et sa date de modification.
struct stamp {
    std::int64_t size = 0;
    std::int64_t modified = 0;
    bool operator==(const stamp& other) const { return size == other.size && modified == other.modified; }
};

fs::path to_path(const string& text) {
    const std::u8string wide(reinterpret_cast<const char8_t*>(text.data()), text.size());
    return fs::path(wide);
}

std::string to_text(const fs::path& path) {
    const auto text = path.generic_u8string();
    return std::string(reinterpret_cast<const char*>(text.data()), text.size());
}

void add_stamp(std::map<std::string, stamp>& snapshot, const fs::path& path) {
    std::error_code error;
    stamp entry;
    const auto size = fs::is_regular_file(path, error) ? fs::file_size(path, error) : 0;
    entry.size = error ? 0 : static_cast<std::int64_t>(size);
    const auto time = fs::last_write_time(path, error);
    entry.modified = error ? 0 : static_cast<std::int64_t>(time.time_since_epoch().count());
    snapshot[to_text(path)] = entry;
}

std::map<std::string, stamp> take_snapshot(const fs::path& root) {
    std::map<std::string, stamp> snapshot;
    std::error_code error;
    if (!fs::exists(root, error)) return snapshot;
    add_stamp(snapshot, root);
    if (fs::is_directory(root, error)) {
        fs::recursive_directory_iterator iterator(root, fs::directory_options::skip_permission_denied, error);
        const fs::recursive_directory_iterator end;
        while (!error && iterator != end) {
            add_stamp(snapshot, iterator->path());
            iterator.increment(error);
        }
    }
    return snapshot;
}

}  // namespace

bool is_terminal_input() {
#if defined(_WIN32)
    return is_console(input_handle());
#else
    return is_tty(0);
#endif
}

bool is_terminal_output() {
#if defined(_WIN32)
    return is_console(output_handle());
#else
    return is_tty(1);
#endif
}

void terminal_size(std::int32_t& columns, std::int32_t& rows) {
    columns = 80;
    rows = 24;
#if defined(_WIN32)
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (::GetConsoleScreenBufferInfo(output_handle(), &info) != 0) {
        columns = info.srWindow.Right - info.srWindow.Left + 1;
        rows = info.srWindow.Bottom - info.srWindow.Top + 1;
    }
#else
    winsize size{};
    if (::ioctl(1, TIOCGWINSZ, &size) == 0 && size.ws_col > 0 && size.ws_row > 0) {
        columns = size.ws_col;
        rows = size.ws_row;
    }
#endif
}

bool enable_ansi() {
#if defined(_WIN32)
    const HANDLE handle = output_handle();
    DWORD mode = 0;
    if (!is_console(handle) || ::GetConsoleMode(handle, &mode) == 0) return false;
    return ::SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0;
#else
    return is_tty(1);
#endif
}

string read_password() {
    console::flush();
    if (!is_terminal_input()) return console::read_line();
#if defined(_WIN32)
    const HANDLE handle = input_handle();
    DWORD mode = 0;
    ::GetConsoleMode(handle, &mode);
    ::SetConsoleMode(handle, mode & ~static_cast<DWORD>(ENABLE_ECHO_INPUT));
    std::wstring text;
    wchar_t buffer[256];
    for (;;) {
        DWORD read = 0;
        if (!::ReadConsoleW(handle, buffer, 256, &read, nullptr) || read == 0) break;
        text.append(buffer, read);
        if (text.back() == L'\n') break;
    }
    ::SetConsoleMode(handle, mode);
    while (!text.empty() && (text.back() == L'\n' || text.back() == L'\r')) text.pop_back();
    console::write("\n", 1);
    return make_string(wide_to_utf8(text.data(), static_cast<int>(text.size())));
#else
    std::string text;
    {
        raw_mode quiet(false);
        unsigned char byte = 0;
        while (read_byte(byte) && byte != '\n' && byte != '\r') {
            if (byte == 127 || byte == 8) {
                while (!text.empty() && (static_cast<unsigned char>(text.back()) & 0xC0) == 0x80) text.pop_back();
                if (!text.empty()) text.pop_back();
            } else {
                text.push_back(static_cast<char>(byte));
            }
        }
    }
    console::write("\n", 1);
    return make_string(text);
#endif
}

string read_key(std::uint32_t timeout_ms, bool& available) {
    console::flush();
    available = is_terminal_input();
    if (!available) return string();
    const auto start = clock::now();
#if defined(_WIN32)
    const HANDLE handle = input_handle();
    DWORD mode = 0;
    ::GetConsoleMode(handle, &mode);
    ::SetConsoleMode(handle, mode & ~static_cast<DWORD>(ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT));
    std::string key;
    wchar_t high = 0;
    for (;;) {
        const auto left = remaining_ms(start, timeout_ms);
        if (left == 0) break;
        if (::WaitForSingleObject(handle, left < 0 ? INFINITE : static_cast<DWORD>(left)) != WAIT_OBJECT_0) break;
        INPUT_RECORD record;
        DWORD count = 0;
        if (!::ReadConsoleInputW(handle, &record, 1, &count) || count == 0) break;
        if (record.EventType != KEY_EVENT || !record.Event.KeyEvent.bKeyDown) continue;
        const auto& event = record.Event.KeyEvent;
        switch (event.wVirtualKeyCode) {
            case VK_UP: key = "up"; break;
            case VK_DOWN: key = "down"; break;
            case VK_LEFT: key = "left"; break;
            case VK_RIGHT: key = "right"; break;
            case VK_HOME: key = "home"; break;
            case VK_END: key = "end"; break;
            case VK_PRIOR: key = "pageup"; break;
            case VK_NEXT: key = "pagedown"; break;
            case VK_DELETE: key = "delete"; break;
            case VK_RETURN: key = "enter"; break;
            case VK_TAB: key = "tab"; break;
            case VK_ESCAPE: key = "escape"; break;
            case VK_BACK: key = "backspace"; break;
            default: break;
        }
        if (!key.empty()) break;
        const wchar_t character = event.uChar.UnicodeChar;
        if (character == 0) continue;
        if (character >= 0xD800 && character <= 0xDBFF) {
            high = character;
            continue;
        }
        if (character >= 0xDC00 && character <= 0xDFFF && high != 0) {
            const wchar_t pair[2] = {high, character};
            key = wide_to_utf8(pair, 2);
        } else {
            key = wide_to_utf8(&character, 1);
        }
        break;
    }
    ::SetConsoleMode(handle, mode);
    return make_string(key);
#else
    raw_mode raw(false);
    const auto left = remaining_ms(start, timeout_ms);
    if (!wait_readable(left < 0 ? -1 : static_cast<int>(left))) return string();
    unsigned char byte = 0;
    if (!read_byte(byte)) return string();
    if (byte == '\n' || byte == '\r') return make_string("enter");
    if (byte == '\t') return make_string("tab");
    if (byte == 127 || byte == 8) return make_string("backspace");
    if (byte == 27) {
        if (!wait_readable(30)) return make_string("escape");
        unsigned char next = 0;
        if (!read_byte(next)) return make_string("escape");
        if (next != '[' && next != 'O') return make_string("escape");
        std::string sequence;
        unsigned char part = 0;
        while (wait_readable(30) && read_byte(part)) {
            sequence.push_back(static_cast<char>(part));
            if (part >= 0x40 && part <= 0x7E) break;
        }
        if (sequence == "A") return make_string("up");
        if (sequence == "B") return make_string("down");
        if (sequence == "C") return make_string("right");
        if (sequence == "D") return make_string("left");
        if (sequence == "H" || sequence == "1~") return make_string("home");
        if (sequence == "F" || sequence == "4~") return make_string("end");
        if (sequence == "3~") return make_string("delete");
        if (sequence == "5~") return make_string("pageup");
        if (sequence == "6~") return make_string("pagedown");
        return make_string("escape");
    }
    std::string key(1, static_cast<char>(byte));
    int extra = byte >= 0xF0 ? 3 : byte >= 0xE0 ? 2 : byte >= 0xC0 ? 1 : 0;
    while (extra-- > 0 && wait_readable(30) && read_byte(byte)) key.push_back(static_cast<char>(byte));
    return make_string(key);
#endif
}

std::int32_t menu(const string& title, const std::vector<string>& options, std::int32_t selected) {
    const auto count = static_cast<std::int32_t>(options.size());
    if (count == 0 || !is_terminal_input() || !is_terminal_output()) return -1;
    enable_ansi();
    if (selected < 0 || selected >= count) selected = 0;
    const auto draw = [&](bool first) {
        std::string out;
        if (!first) out += "\x1b[" + std::to_string(count) + "A";
        for (std::int32_t i = 0; i < count; ++i) {
            out += "\x1b[2K\r";
            out += i == selected ? "\x1b[7m> " : "  ";
            out.append(options[static_cast<std::size_t>(i)].data(), options[static_cast<std::size_t>(i)].size());
            if (i == selected) out += "\x1b[0m";
            out += "\n";
        }
        console::write(out.data(), out.size());
    };
    if (title.size() > 0) {
        console::write(title.data(), title.size());
        console::write("\n", 1);
    }
    draw(true);
    for (;;) {
        bool available = true;
        const string key = read_key(0, available);
        if (!available) return -1;
        const std::string name(key.data(), key.size());
        if (name == "enter") return selected;
        if (name == "escape") return -1;
        if (name == "up") selected = (selected + count - 1) % count;
        else if (name == "down") selected = (selected + 1) % count;
        else if (name == "home") selected = 0;
        else if (name == "end") selected = count - 1;
        else if (name.size() == 1 && name[0] >= '1' && name[0] <= '9' && name[0] - '1' < count) selected = name[0] - '1';
        else continue;
        draw(false);
    }
}

bool install_signal(const string& name) {
    const std::string wanted(name.data(), name.size());
    for (const auto& known : known_signals) {
        if (wanted == known.name) return std::signal(known.number, rgl_signal_handler) != SIG_ERR;
    }
    return false;
}

string take_signal() {
    for (const auto& known : known_signals) {
        if (pending_signals[known.number] != 0) {
            pending_signals[known.number] = 0;
            return make_string(known.name);
        }
    }
    return string();
}

string wait_signal(std::uint32_t timeout_ms) {
    const auto start = clock::now();
    for (;;) {
        auto taken = take_signal();
        if (taken.size() > 0) return taken;
        if (timeout_ms != 0 && remaining_ms(start, timeout_ms) == 0) return string();
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
}

std::vector<string> wait_for_change(const string& path, std::uint32_t timeout_ms) {
    const fs::path root = to_path(path);
    const auto before = take_snapshot(root);
    const auto start = clock::now();
    for (;;) {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        const auto after = take_snapshot(root);
        std::vector<string> changes;
        for (const auto& [name, entry] : after) {
            const auto found = before.find(name);
            if (found == before.end()) changes.push_back(make_string("created " + name));
            else if (!(found->second == entry)) changes.push_back(make_string("modified " + name));
        }
        for (const auto& [name, entry] : before) {
            (void)entry;
            if (after.find(name) == after.end()) changes.push_back(make_string("removed " + name));
        }
        if (!changes.empty()) return changes;
        if (timeout_ms != 0 && remaining_ms(start, timeout_ms) == 0) return {};
    }
}

}  // namespace rgl::standard::system

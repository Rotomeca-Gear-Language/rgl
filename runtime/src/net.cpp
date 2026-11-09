// Composant « net » du runtime : sockets TCP et UDP bloquants avec délai.

#include "rgl/standard/net.hpp"

#include <cerrno>
#include <cstring>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

namespace rgl::standard::net {
namespace {

#if defined(_WIN32)
using native_socket = SOCKET;
constexpr native_socket invalid = INVALID_SOCKET;

void start() noexcept {
    static const bool started = [] {
        WSADATA data;
        return ::WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }();
    (void)started;
}

void close_native(native_socket s) noexcept { ::closesocket(s); }

// Attend que la prise soit prête à lire (read == true) ou à écrire ; faux au délai.
bool wait(native_socket s, bool read, std::uint32_t timeout_ms) noexcept {
    if (timeout_ms == 0) return true;
    fd_set set;
    FD_ZERO(&set);
    FD_SET(s, &set);
    timeval limit{static_cast<long>(timeout_ms / 1000), static_cast<long>((timeout_ms % 1000) * 1000)};
    return ::select(0, read ? &set : nullptr, read ? nullptr : &set, nullptr, &limit) > 0;
}

void set_blocking(native_socket s, bool blocking) noexcept {
    u_long mode = blocking ? 0 : 1;
    ::ioctlsocket(s, FIONBIO, &mode);
}
#else
using native_socket = int;
constexpr native_socket invalid = -1;

void start() noexcept {}
void close_native(native_socket s) noexcept { ::close(s); }

bool wait(native_socket s, bool read, std::uint32_t timeout_ms) noexcept {
    if (timeout_ms == 0) return true;
    pollfd entry{s, static_cast<short>(read ? POLLIN : POLLOUT), 0};
    return ::poll(&entry, 1, static_cast<int>(timeout_ms)) > 0;
}

void set_blocking(native_socket s, bool blocking) noexcept {
    const int flags = ::fcntl(s, F_GETFL, 0);
    ::fcntl(s, F_SETFL, blocking ? (flags & ~O_NONBLOCK) : (flags | O_NONBLOCK));
}
#endif

std::string text_of(const string& value) { return std::string(value.data(), value.size()); }

std::int64_t wrap(native_socket s) noexcept { return s == invalid ? failure : static_cast<std::int64_t>(s); }
native_socket unwrap(std::int64_t s) noexcept { return static_cast<native_socket>(s); }

thread_local std::string sender;

// Ouvre une prise liée à une adresse littérale : « » et « 0.0.0.0 » toutes les interfaces IPv4, « :: » toutes les
// interfaces IPv4 et IPv6, sinon une adresse IPv4 ou IPv6 (crochets permis). `listening` met la prise TCP à l'écoute.
native_socket open_bound(const string& address, std::uint16_t port, int type, bool listening) noexcept {
    start();
    std::string host = text_of(address);
    if (host.size() >= 2 && host.front() == '[' && host.back() == ']') host = host.substr(1, host.size() - 2);
    if (host.empty()) host = "0.0.0.0";
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = type;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST;
    const std::string service = std::to_string(port);
    addrinfo* list = nullptr;
    if (::getaddrinfo(host.c_str(), service.c_str(), &hints, &list) != 0) return invalid;
    native_socket found = invalid;
    for (addrinfo* entry = list; entry != nullptr && found == invalid; entry = entry->ai_next) {
        const native_socket s = ::socket(entry->ai_family, entry->ai_socktype, entry->ai_protocol);
        if (s == invalid) continue;
        int yes = 1;
        if (listening) ::setsockopt(s, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&yes), sizeof yes);
        int no = 0;
        if (entry->ai_family == AF_INET6) ::setsockopt(s, IPPROTO_IPV6, IPV6_V6ONLY, reinterpret_cast<const char*>(&no), sizeof no);
        if (::bind(s, entry->ai_addr, static_cast<int>(entry->ai_addrlen)) != 0 || (listening && ::listen(s, 64) != 0)) {
            close_native(s);
            continue;
        }
        found = s;
    }
    ::freeaddrinfo(list);
    return found;
}

}  // namespace

std::int64_t tcp_connect(const string& host, std::uint16_t port, std::uint32_t timeout_ms) noexcept {
    start();
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* list = nullptr;
    const std::string name = text_of(host);
    const std::string service = std::to_string(port);
    if (::getaddrinfo(name.c_str(), service.c_str(), &hints, &list) != 0) return failure;

    native_socket found = invalid;
    for (addrinfo* entry = list; entry != nullptr && found == invalid; entry = entry->ai_next) {
        const native_socket s = ::socket(entry->ai_family, entry->ai_socktype, entry->ai_protocol);
        if (s == invalid) continue;
        bool connected = false;
        if (timeout_ms == 0) connected = ::connect(s, entry->ai_addr, static_cast<int>(entry->ai_addrlen)) == 0;
        else {
            set_blocking(s, false);
            const int result = ::connect(s, entry->ai_addr, static_cast<int>(entry->ai_addrlen));
            if (result == 0) connected = true;
            else if (wait(s, false, timeout_ms)) {
                int error = 0;
#if defined(_WIN32)
                int length = sizeof error;
                ::getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &length);
#else
                socklen_t length = sizeof error;
                ::getsockopt(s, SOL_SOCKET, SO_ERROR, &error, &length);
#endif
                connected = error == 0;
            }
            set_blocking(s, true);
        }
        if (connected) found = s;
        else close_native(s);
    }
    ::freeaddrinfo(list);
    return wrap(found);
}

std::int64_t tcp_listen(const string& address, std::uint16_t port) noexcept { return wrap(open_bound(address, port, SOCK_STREAM, true)); }

std::int64_t tcp_accept(std::int64_t listener, std::uint32_t timeout_ms) noexcept {
    const native_socket l = unwrap(listener);
    if (!wait(l, true, timeout_ms)) return timed_out;
    const native_socket s = ::accept(l, nullptr, nullptr);
    return wrap(s);
}

std::int64_t udp_open(const string& address, std::uint16_t port) noexcept { return wrap(open_bound(address, port, SOCK_DGRAM, false)); }

std::int64_t send(std::int64_t socket, const std::uint8_t* data, std::size_t size) noexcept {
    std::size_t sent = 0;
    while (sent < size) {
        const auto count = ::send(unwrap(socket), reinterpret_cast<const char*>(data + sent), static_cast<int>(size - sent), 0);
        if (count <= 0) return failure;
        sent += static_cast<std::size_t>(count);
    }
    return static_cast<std::int64_t>(sent);
}

std::int64_t send_to(std::int64_t socket, const string& host, std::uint16_t port, const std::uint8_t* data, std::size_t size) noexcept {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    addrinfo* list = nullptr;
    const std::string name = text_of(host);
    const std::string service = std::to_string(port);
    if (::getaddrinfo(name.c_str(), service.c_str(), &hints, &list) != 0 || list == nullptr) return failure;
    const auto count = ::sendto(unwrap(socket), reinterpret_cast<const char*>(data), static_cast<int>(size), 0, list->ai_addr, static_cast<int>(list->ai_addrlen));
    ::freeaddrinfo(list);
    return count < 0 ? failure : static_cast<std::int64_t>(count);
}

std::int64_t receive(std::int64_t socket, std::uint8_t* buffer, std::size_t size, std::uint32_t timeout_ms) noexcept {
    const native_socket s = unwrap(socket);
    if (!wait(s, true, timeout_ms)) return timed_out;
    sockaddr_storage from{};
#if defined(_WIN32)
    int length = sizeof from;
#else
    socklen_t length = sizeof from;
#endif
    const auto count = ::recvfrom(s, reinterpret_cast<char*>(buffer), static_cast<int>(size), 0, reinterpret_cast<sockaddr*>(&from), &length);
    if (count < 0) return failure;
    if (length > 0 && from.ss_family == AF_INET) {
        const auto& address = reinterpret_cast<const sockaddr_in&>(from);
        char host[INET_ADDRSTRLEN] = {0};
        ::inet_ntop(AF_INET, &address.sin_addr, host, sizeof host);
        sender = std::string(host) + ":" + std::to_string(ntohs(address.sin_port));
    } else if (length > 0 && from.ss_family == AF_INET6) {
        const auto& address = reinterpret_cast<const sockaddr_in6&>(from);
        char host[INET6_ADDRSTRLEN] = {0};
        ::inet_ntop(AF_INET6, &address.sin6_addr, host, sizeof host);
        sender = "[" + std::string(host) + "]:" + std::to_string(ntohs(address.sin6_port));
    }
    return static_cast<std::int64_t>(count);
}

const std::string& last_sender() noexcept { return sender; }

std::uint16_t local_port(std::int64_t socket) noexcept {
    sockaddr_storage local{};
#if defined(_WIN32)
    int length = sizeof local;
#else
    socklen_t length = sizeof local;
#endif
    if (::getsockname(unwrap(socket), reinterpret_cast<sockaddr*>(&local), &length) != 0) return 0;
    if (local.ss_family == AF_INET6) return ntohs(reinterpret_cast<const sockaddr_in6&>(local).sin6_port);
    return ntohs(reinterpret_cast<const sockaddr_in&>(local).sin_port);
}

void close(std::int64_t socket) noexcept { close_native(unwrap(socket)); }

}  // namespace rgl::standard::net

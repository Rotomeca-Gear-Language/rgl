#pragma once

// Parties communes aux transports HTTP et HTTPS : lecture des requêtes (Content-Length ou
// « chunked », plusieurs requêtes sur une même connexion), connexions persistantes, WebSocket,
// adresses IPv4 et IPv6. Détail d'implémentation du runtime, pas une API.

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cerrno>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#else
#include <arpa/inet.h>
#include <netdb.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>
#endif

#include "http.hpp"

namespace rgl::standard::http::detail {

inline constexpr std::size_t max_request_size = 16 * 1024 * 1024;
// Une connexion persistante inactive libère son worker au bout de ce délai.
inline constexpr std::uint32_t idle_timeout_ms = 5000;
// Après autant de requêtes, le serveur ferme la connexion.
inline constexpr std::uint32_t max_requests_per_connection = 100;

#if defined(_WIN32)
using socket_handle = SOCKET;
inline constexpr socket_handle invalid_socket = INVALID_SOCKET;
inline int last_error() noexcept { return WSAGetLastError(); }
inline void close_socket(socket_handle socket) noexcept { closesocket(socket); }
#else
using socket_handle = int;
inline constexpr socket_handle invalid_socket = -1;
inline int last_error() noexcept { return errno; }
inline void close_socket(socket_handle socket) noexcept { close(socket); }
#endif

struct socket_guard {
    socket_handle value = invalid_socket;
    ~socket_guard() { if (value != invalid_socket) close_socket(value); }
};

// Initialise la bibliothèque de sockets de Windows pour la durée de la portée.
struct network_guard {
    bool ready = true;
    network_guard() {
#if defined(_WIN32)
        WSADATA data{};
        ready = WSAStartup(MAKEWORD(2, 2), &data) == 0;
#endif
    }
    ~network_guard() {
#if defined(_WIN32)
        if (ready) WSACleanup();
#endif
    }
    network_guard(const network_guard&) = delete;
    network_guard& operator=(const network_guard&) = delete;
};

inline void set_receive_timeout(socket_handle socket, std::uint32_t timeout_ms) noexcept {
#if defined(_WIN32)
    const DWORD timeout = timeout_ms;
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
#else
    const timeval timeout{static_cast<time_t>(timeout_ms / 1000), static_cast<suseconds_t>((timeout_ms % 1000) * 1000)};
    setsockopt(socket, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
#endif
}

inline void set_timeouts(socket_handle socket, std::uint32_t timeout_ms) noexcept {
    set_receive_timeout(socket, timeout_ms);
#if defined(_WIN32)
    const DWORD timeout = timeout_ms;
    setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
#else
    const timeval timeout{static_cast<time_t>(timeout_ms / 1000), static_cast<suseconds_t>((timeout_ms % 1000) * 1000)};
    setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
#endif
}

inline std::string lower(std::string_view text) {
    std::string result(text);
    for (char& c : result) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return result;
}

// Valeur d'un en-tête (nom en minuscules) dans des lignes « Nom: valeur » séparées par CRLF ; vide s'il est absent.
inline std::string header_value(std::string_view headers, std::string_view wanted) {
    std::size_t line = 0;
    while (line < headers.size()) {
        const auto end = headers.find("\r\n", line);
        const auto line_end = end == std::string_view::npos ? headers.size() : end;
        const auto colon = headers.find(':', line);
        if (colon < line_end && colon - line == wanted.size()) {
            bool same = true;
            for (std::size_t i = 0; i < wanted.size(); ++i) {
                char c = headers[line + i];
                if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
                if (c != wanted[i]) { same = false; break; }
            }
            if (same) {
                auto first = colon + 1;
                while (first < line_end && (headers[first] == ' ' || headers[first] == '\t')) ++first;
                return std::string(headers.substr(first, line_end - first));
            }
        }
        if (end == std::string_view::npos) break;
        line = end + 2;
    }
    return {};
}

// Les lignes d'en-têtes d'un message : tout ce qui suit sa première ligne (requête ou statut).
inline std::string_view header_lines(std::string_view message) noexcept {
    const auto first = message.find("\r\n");
    return first == std::string_view::npos ? std::string_view{} : message.substr(first + 2);
}

inline std::size_t content_length(std::string_view message) noexcept {
    const auto value = header_value(header_lines(message), "content-length");
    std::size_t length = 0;
    const auto parsed = std::from_chars(value.data(), value.data() + value.size(), length);
    return parsed.ec == std::errc{} ? length : 0;
}

inline bool is_chunked(std::string_view message) {
    return lower(header_value(header_lines(message), "transfer-encoding")).find("chunked") != std::string::npos;
}

// Décode un corps « chunked » commençant à `start` ; vrai s'il est complet, `end` étant alors la fin du message.
// Rend false aussi si la taille d'un morceau est invalide : `invalid` le dit.
inline bool decode_chunked(const std::string& raw, std::size_t start, std::string& body, bool& invalid, std::size_t& end) noexcept {
    std::size_t position = start;
    body.clear();
    for (;;) {
        const auto line_end = raw.find("\r\n", position);
        if (line_end == std::string::npos) return false;
        std::size_t size = 0;
        const auto size_end = raw.find(';', position);
        const auto last = size_end != std::string::npos && size_end < line_end ? size_end : line_end;
        const auto parsed = std::from_chars(raw.data() + position, raw.data() + last, size, 16);
        if (parsed.ec != std::errc{}) { invalid = true; return false; }
        position = line_end + 2;
        if (size == 0) {
            // Les éventuelles remorques finissent par une ligne vide.
            for (;;) {
                const auto trailer_end = raw.find("\r\n", position);
                if (trailer_end == std::string::npos) return false;
                if (trailer_end == position) { end = trailer_end + 2; return true; }
                position = trailer_end + 2;
            }
        }
        if (position + size + 2 > raw.size()) return false;
        body.append(raw, position, size);
        position += size + 2;
    }
}

// Une réponse lue jusqu'à la fermeture de la connexion : si son corps est « chunked » (beaucoup de serveurs l'écrivent même quand on
// demande `Connection: close`), il est recomposé et la réponse porte un `Content-Length`. Une réponse qui ne l'est pas, ou dont le
// corps est incomplet, reste telle quelle.
inline void dechunk_response(std::string& response) {
    const auto header_end = response.find("\r\n\r\n");
    if (header_end == std::string::npos || !is_chunked(std::string_view(response.data(), header_end + 2))) return;
    std::string body;
    bool invalid = false;
    std::size_t end = 0;
    if (!decode_chunked(response, header_end + 4, body, invalid, end)) return;
    std::string rebuilt;
    std::size_t line = 0;
    while (line < header_end + 2) {
        const auto stop = response.find("\r\n", line);
        const std::string text = response.substr(line, stop - line);
        const std::string name = lower(text);
        if (line == 0 || (name.rfind("transfer-encoding:", 0) != 0 && name.rfind("content-length:", 0) != 0)) rebuilt += text + "\r\n";
        line = stop + 2;
    }
    rebuilt += "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n" + body;
    response = std::move(rebuilt);
}

enum class read_status { ok, closed, failed };

// Extrait une requête complète de `pending` (les octets déjà reçus), en lisant sur `io` au besoin ; les octets
// en trop (une requête suivante) restent dans `pending`. Un corps « chunked » est recomposé avec un Content-Length.
template <class Io>
read_status read_request(Io& io, std::string& pending, std::string& request) noexcept {
    char buffer[8192];
    for (;;) {
        std::size_t blank = 0;
        while (blank + 1 < pending.size() && pending[blank] == '\r' && pending[blank + 1] == '\n') blank += 2;
        if (blank != 0) pending.erase(0, blank);

        const auto header_end = pending.find("\r\n\r\n");
        if (header_end != std::string::npos) {
            const std::string_view head(pending.data(), header_end + 2);
            if (is_chunked(head)) {
                std::string body;
                bool invalid = false;
                std::size_t end = 0;
                if (decode_chunked(pending, header_end + 4, body, invalid, end)) {
                    std::string rebuilt;
                    std::size_t line = 0;
                    while (line < header_end + 2) {
                        const auto stop = pending.find("\r\n", line);
                        const std::string text = pending.substr(line, stop - line);
                        const std::string name = lower(text);
                        if (line == 0 || (name.rfind("transfer-encoding:", 0) != 0 && name.rfind("content-length:", 0) != 0)) rebuilt += text + "\r\n";
                        line = stop + 2;
                    }
                    rebuilt += "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n" + body;
                    request = std::move(rebuilt);
                    pending.erase(0, end);
                    return read_status::ok;
                }
                if (invalid) return read_status::failed;
            } else {
                const auto total = header_end + 4 + content_length(head);
                if (pending.size() >= total) {
                    request.assign(pending, 0, total);
                    pending.erase(0, total);
                    return read_status::ok;
                }
            }
        }
        if (pending.size() > max_request_size) return read_status::failed;
        const auto received = io.read(buffer, sizeof(buffer));
        if (received <= 0) return pending.empty() ? read_status::closed : read_status::failed;
        pending.append(buffer, static_cast<std::size_t>(received));
    }
}

inline bool split_request(const std::string& raw, string& method, string& target, string& headers, string& body, std::string& version) {
    const auto first_space = raw.find(' ');
    const auto second_space = first_space == std::string::npos ? std::string::npos : raw.find(' ', first_space + 1);
    const auto first_line = raw.find("\r\n");
    const auto header_end = raw.find("\r\n\r\n");
    if (first_space == std::string::npos || second_space == std::string::npos || first_line == std::string::npos || header_end == std::string::npos) return false;
    method = string(raw.data(), first_space);
    target = string(raw.data() + first_space + 1, second_space - first_space - 1);
    version = raw.substr(second_space + 1, first_line - second_space - 1);
    headers = string(raw.data() + first_line + 2, header_end - first_line - 2);
    body = string(raw.data() + header_end + 4, raw.size() - header_end - 4);
    return true;
}

// Ajoute « Connection: keep-alive » ou « Connection: close » à une réponse qui n'en dit rien ; `keep` dit si la
// connexion reste ouverte (le client la veut, et la réponse a une longueur connue pour que le client sache où elle finit).
inline string prepare_response(const string& response, bool wanted, bool& keep) {
    const std::string_view text(response.data(), response.size());
    const auto head_end = text.find("\r\n\r\n");
    const auto status_end = text.find("\r\n");
    keep = false;
    if (head_end == std::string_view::npos || status_end == std::string_view::npos) return response;
    const auto lines = text.substr(status_end + 2, head_end - status_end);
    const bool has_length = !header_value(lines, "content-length").empty();
    const auto existing = lower(header_value(lines, "connection"));
    if (!existing.empty()) {
        keep = wanted && has_length && existing.find("keep-alive") != std::string::npos;
        return response;
    }
    keep = wanted && has_length;
    std::string result;
    result.reserve(text.size() + 24);
    result.append(text.substr(0, status_end + 2)).append(keep ? "Connection: keep-alive\r\n" : "Connection: close\r\n").append(text.substr(status_end + 2));
    return string(result.data(), result.size());
}

// Le client veut-il garder la connexion ouverte ? HTTP/1.1 : oui sauf « Connection: close » ; HTTP/1.0 : seulement avec « keep-alive ».
inline bool client_wants_keep_alive(const std::string& version, std::string_view headers) {
    const auto connection = lower(header_value(headers, "connection"));
    if (version == "HTTP/1.1") return connection.find("close") == std::string::npos;
    return connection.find("keep-alive") != std::string::npos;
}

inline std::array<std::uint8_t, 20> sha1(std::string_view input) {
    std::vector<std::uint8_t> data(input.begin(), input.end());
    const std::uint64_t bits = static_cast<std::uint64_t>(data.size()) * 8;
    data.push_back(0x80);
    while (data.size() % 64 != 56) data.push_back(0);
    for (int shift = 56; shift >= 0; shift -= 8) data.push_back(static_cast<std::uint8_t>(bits >> shift));
    std::uint32_t h0 = 0x67452301, h1 = 0xefcdab89, h2 = 0x98badcfe, h3 = 0x10325476, h4 = 0xc3d2e1f0;
    for (std::size_t offset = 0; offset < data.size(); offset += 64) {
        std::uint32_t w[80]{};
        for (int i = 0; i < 16; ++i) {
            const auto p = offset + static_cast<std::size_t>(i) * 4;
            w[i] = static_cast<std::uint32_t>(data[p]) << 24 | static_cast<std::uint32_t>(data[p + 1]) << 16 |
                   static_cast<std::uint32_t>(data[p + 2]) << 8 | data[p + 3];
        }
        for (int i = 16; i < 80; ++i) w[i] = std::rotl(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        auto a = h0, b = h1, c = h2, d = h3, e = h4;
        for (int i = 0; i < 80; ++i) {
            const std::uint32_t f = i < 20 ? ((b & c) | (~b & d)) : i < 40 ? (b ^ c ^ d) : i < 60 ? ((b & c) | (b & d) | (c & d)) : (b ^ c ^ d);
            const std::uint32_t k = i < 20 ? 0x5a827999 : i < 40 ? 0x6ed9eba1 : i < 60 ? 0x8f1bbcdc : 0xca62c1d6;
            const std::uint32_t temp = std::rotl(a, 5) + f + e + k + w[i];
            e = d; d = c; c = std::rotl(b, 30); b = a; a = temp;
        }
        h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
    }
    std::array<std::uint8_t, 20> out{};
    const std::uint32_t words[]{h0, h1, h2, h3, h4};
    for (std::size_t i = 0; i < 5; ++i) for (int byte = 0; byte < 4; ++byte) out[i * 4 + byte] = static_cast<std::uint8_t>(words[i] >> (24 - byte * 8));
    return out;
}

inline std::string base64(const std::uint8_t* data, std::size_t size) {
    constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((size + 2) / 3 * 4);
    for (std::size_t i = 0; i < size; i += 3) {
        const std::uint32_t value = static_cast<std::uint32_t>(data[i]) << 16 |
            (i + 1 < size ? static_cast<std::uint32_t>(data[i + 1]) << 8 : 0) | (i + 2 < size ? data[i + 2] : 0);
        out.push_back(alphabet[(value >> 18) & 63]); out.push_back(alphabet[(value >> 12) & 63]);
        out.push_back(i + 1 < size ? alphabet[(value >> 6) & 63] : '='); out.push_back(i + 2 < size ? alphabet[value & 63] : '=');
    }
    return out;
}

template <class Io>
bool receive_exact(Io& io, void* destination, std::size_t size) noexcept {
    auto* bytes = static_cast<char*>(destination);
    std::size_t done = 0;
    while (done < size) {
        const auto count = io.read(bytes + done, size - done);
        if (count <= 0) return false;
        done += static_cast<std::size_t>(count);
    }
    return true;
}

template <class Io>
bool send_frame(Io& io, std::uint8_t opcode, std::string_view payload) noexcept {
    std::vector<std::uint8_t> frame;
    frame.push_back(static_cast<std::uint8_t>(0x80 | opcode));
    if (payload.size() < 126) frame.push_back(static_cast<std::uint8_t>(payload.size()));
    else if (payload.size() <= 0xffff) {
        frame.push_back(126); frame.push_back(static_cast<std::uint8_t>(payload.size() >> 8)); frame.push_back(static_cast<std::uint8_t>(payload.size()));
    } else {
        frame.push_back(127);
        for (int shift = 56; shift >= 0; shift -= 8) frame.push_back(static_cast<std::uint8_t>(payload.size() >> shift));
    }
    frame.insert(frame.end(), payload.begin(), payload.end());
    return io.write(std::string_view(reinterpret_cast<const char*>(frame.data()), frame.size()));
}

template <class Io>
bool websocket_session(Io& io, const string& target, const string& headers, const websocket_factory& factory) noexcept {
    const auto key = header_value(std::string_view(headers.data(), headers.size()), "sec-websocket-key");
    if (key.empty()) return false;
    const auto digest = sha1(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
    const auto accept = base64(digest.data(), digest.size());
    const std::string handshake = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
    if (!io.write(handshake)) return false;
    // La connexion WebSocket vit autant qu'elle veut : plus de délai d'inactivité.
    io.set_idle_timeout(0);
    auto session = factory(target, headers);
    for (;;) {
        std::uint8_t head[2];
        if (!receive_exact(io, head, 2)) return true;
        const bool final = (head[0] & 0x80) != 0;
        const std::uint8_t opcode = head[0] & 0x0f;
        const bool masked = (head[1] & 0x80) != 0;
        std::uint64_t length = head[1] & 0x7f;
        if (length == 126) { std::uint8_t ext[2]; if (!receive_exact(io, ext, 2)) return false; length = static_cast<std::uint64_t>(ext[0]) << 8 | ext[1]; }
        else if (length == 127) { std::uint8_t ext[8]; if (!receive_exact(io, ext, 8)) return false; length = 0; for (auto byte : ext) length = length << 8 | byte; }
        if (!masked || !final || length > max_request_size) return false;
        std::uint8_t mask[4];
        if (!receive_exact(io, mask, 4)) return false;
        std::string payload(static_cast<std::size_t>(length), '\0');
        if (length != 0 && !receive_exact(io, payload.data(), payload.size())) return false;
        for (std::size_t i = 0; i < payload.size(); ++i) payload[i] ^= static_cast<char>(mask[i % 4]);
        if (opcode == 8) { send_frame(io, 8, payload); return true; }
        if (opcode == 9) { if (!send_frame(io, 10, payload)) return false; continue; }
        if (opcode != 1) return false;
        const string reply = session(string(payload.data(), payload.size()));
        if (!send_frame(io, 1, std::string_view(reply.data(), reply.size()))) return false;
    }
}

// Traite les requêtes d'une connexion, l'une après l'autre tant que le client et le serveur la gardent ouverte.
// Rend 0, ou le code d'erreur de la première erreur de transport.
template <class Io>
std::int32_t serve_connection(Io& io, const handler& callback, const websocket_acceptor& accept_websocket,
                              const websocket_factory& open_websocket, const std::atomic<bool>& stopping) noexcept {
    std::string pending;
    for (std::uint32_t served = 0;; ++served) {
        std::string raw;
        const auto status = read_request(io, pending, raw);
        if (status == read_status::closed) return 0;
        if (status == read_status::failed) return io.read_failure();
        string method, target, headers, body;
        std::string version;
        if (!split_request(raw, method, target, headers, body, version)) return -2;
        if (accept_websocket(target, headers)) return websocket_session(io, target, headers, open_websocket) ? 0 : io.websocket_failure();

        const bool wanted = served + 1 < max_requests_per_connection && !stopping.load() &&
            client_wants_keep_alive(version, std::string_view(headers.data(), headers.size()));
        const string response = callback(method, target, headers, body);
        bool keep = false;
        const string message = prepare_response(response, wanted, keep);
        if (!io.write(std::string_view(message.data(), message.size()))) return io.write_failure();
        if (!keep) return 0;
        io.set_idle_timeout(idle_timeout_ms);
    }
}

struct parsed_url {
    std::string host;       // sans crochets : ce que résout getaddrinfo
    std::string port;
    std::string authority;  // tel qu'écrit (crochets et port compris) : l'en-tête Host
    std::string target;
};

inline bool parse_url(std::string_view url, std::string_view scheme, std::string_view default_port, parsed_url& parsed) {
    if (!url.starts_with(scheme)) return false;
    url.remove_prefix(scheme.size());
    const auto stop = url.find_first_of("/?");
    const auto authority = url.substr(0, stop);
    if (stop == std::string_view::npos) parsed.target = "/";
    else if (url[stop] == '/') parsed.target = std::string(url.substr(stop));
    else parsed.target = "/" + std::string(url.substr(stop));
    if (authority.empty()) return false;
    parsed.authority = std::string(authority);
    if (authority.front() == '[') {
        const auto close = authority.find(']');
        if (close == std::string_view::npos) return false;
        parsed.host = std::string(authority.substr(1, close - 1));
        const auto rest = authority.substr(close + 1);
        if (rest.empty()) parsed.port = std::string(default_port);
        else if (rest.front() == ':') parsed.port = std::string(rest.substr(1));
        else return false;
    } else {
        const auto colon = authority.rfind(':');
        parsed.host = std::string(authority.substr(0, colon));
        parsed.port = colon == std::string_view::npos ? std::string(default_port) : std::string(authority.substr(colon + 1));
    }
    return !parsed.host.empty() && !parsed.port.empty();
}

// Se connecte à l'adresse (IPv4 ou IPv6) ; invalid_socket si aucune ne répond.
inline socket_handle connect_to(const parsed_url& url, std::uint32_t timeout_ms) noexcept {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* addresses = nullptr;
    if (getaddrinfo(url.host.c_str(), url.port.c_str(), &hints, &addresses) != 0) return invalid_socket;
    socket_handle connected = invalid_socket;
    for (auto* address = addresses; address != nullptr; address = address->ai_next) {
        socket_handle candidate = socket(address->ai_family, address->ai_socktype, address->ai_protocol);
        if (candidate == invalid_socket) continue;
        set_timeouts(candidate, timeout_ms);
        if (connect(candidate, address->ai_addr, static_cast<int>(address->ai_addrlen)) == 0) {
            connected = candidate;
            break;
        }
        close_socket(candidate);
    }
    freeaddrinfo(addresses);
    return connected;
}

// Ouvre un socket d'écoute. « » et « 0.0.0.0 » : toutes les interfaces IPv4 ; « :: » : toutes les interfaces,
// IPv6 et IPv4 ; sinon une adresse IPv4 ou IPv6 littérale (les crochets sont permis). 0 en cas de succès.
inline std::int32_t open_listener(std::string_view address, std::uint16_t port, socket_handle& listener) noexcept {
    listener = invalid_socket;
    std::string host(address);
    if (host.size() >= 2 && host.front() == '[' && host.back() == ']') host = host.substr(1, host.size() - 2);
    if (host.empty()) host = "0.0.0.0";
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST;
    const std::string service = std::to_string(port);
    addrinfo* results = nullptr;
    if (getaddrinfo(host.c_str(), service.c_str(), &hints, &results) != 0) return -1;
    std::int32_t result = -1;
    for (auto* candidate = results; candidate != nullptr; candidate = candidate->ai_next) {
        socket_handle handle = socket(candidate->ai_family, candidate->ai_socktype, candidate->ai_protocol);
        if (handle == invalid_socket) { result = last_error(); continue; }
        int enabled = 1;
#if defined(_WIN32)
        setsockopt(handle, SOL_SOCKET, SO_REUSEADDR, reinterpret_cast<const char*>(&enabled), sizeof(enabled));
        int disabled = 0;
        if (candidate->ai_family == AF_INET6) setsockopt(handle, IPPROTO_IPV6, IPV6_V6ONLY, reinterpret_cast<const char*>(&disabled), sizeof(disabled));
#else
        setsockopt(handle, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
        int disabled = 0;
        if (candidate->ai_family == AF_INET6) setsockopt(handle, IPPROTO_IPV6, IPV6_V6ONLY, &disabled, sizeof(disabled));
#endif
        if (bind(handle, candidate->ai_addr, static_cast<int>(candidate->ai_addrlen)) != 0 || listen(handle, SOMAXCONN) != 0) {
            result = last_error();
            close_socket(handle);
            continue;
        }
        listener = handle;
        result = 0;
        break;
    }
    freeaddrinfo(results);
    return result;
}

// Entrées-sorties d'une connexion en clair.
struct plain_io {
    socket_handle socket;

    long long read(char* destination, std::size_t size) noexcept {
#if defined(_WIN32)
        return recv(socket, destination, static_cast<int>(std::min<std::size_t>(size, 0x7fffffff)), 0);
#else
        return recv(socket, destination, size, 0);
#endif
    }
    bool write(std::string_view data) noexcept {
        std::size_t sent = 0;
        while (sent < data.size()) {
            const auto remaining = data.size() - sent;
#if defined(_WIN32)
            const int count = send(socket, data.data() + sent, static_cast<int>(std::min<std::size_t>(remaining, 0x7fffffff)), 0);
#else
            const auto count = send(socket, data.data() + sent, remaining, 0);
#endif
            if (count <= 0) return false;
            sent += static_cast<std::size_t>(count);
        }
        return true;
    }
    void set_idle_timeout(std::uint32_t timeout_ms) noexcept { set_receive_timeout(socket, timeout_ms); }
    std::int32_t read_failure() const noexcept { return last_error(); }
    std::int32_t write_failure() const noexcept { return last_error(); }
    std::int32_t websocket_failure() const noexcept { return last_error(); }
};

}  // namespace rgl::standard::http::detail

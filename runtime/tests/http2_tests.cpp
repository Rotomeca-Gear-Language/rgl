// Tests du runtime : le client HTTP/2 (http2.hpp) — la compression d'en-têtes HPACK avec les exemples du RFC 7541, et un échange
// complet avec un serveur simulé. Le programme renvoie 0 si tout passe, 1 sinon.

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "rgl/standard/http2.hpp"

namespace h2 = rgl::standard::http::h2;

namespace {

int failures = 0;

#define CHECK(condition)                                                                        \
    do {                                                                                        \
        if (!(condition)) {                                                                     \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition);         \
            ++failures;                                                                         \
        }                                                                                       \
    } while (false)

std::string bytes_of(const std::string& hex) {
    std::string out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2) out.push_back(static_cast<char>(std::stoi(hex.substr(i, 2), nullptr, 16)));
    return out;
}

bool has(const std::vector<h2::header>& list, const char* name, const char* value) {
    for (const auto& item : list)
        if (item.name == name && item.value == value) return true;
    return false;
}

// Un serveur simulé : ce que le client lit est écrit d'avance, ce qu'il écrit est gardé.
struct mock_io {
    std::string script;
    std::size_t position = 0;
    std::string written;

    long long read(char* destination, std::size_t size) {
        if (position >= script.size()) return 0;
        const std::size_t count = std::min(size, script.size() - position);
        std::memcpy(destination, script.data() + position, count);
        position += count;
        return static_cast<long long>(count);
    }
    bool write(std::string_view data) {
        written.append(data);
        return true;
    }
};

void add(std::string& out, std::uint8_t type, std::uint8_t flags, std::uint32_t stream, const std::string& payload) {
    // put_frame est interne : on écrit l'en-tête de trame ici pour que le test ne dépende pas de ce qu'il vérifie.
    const auto size = static_cast<std::uint32_t>(payload.size());
    out.push_back(static_cast<char>(size >> 16));
    out.push_back(static_cast<char>(size >> 8));
    out.push_back(static_cast<char>(size));
    out.push_back(static_cast<char>(type));
    out.push_back(static_cast<char>(flags));
    out.push_back(static_cast<char>(stream >> 24));
    out.push_back(static_cast<char>(stream >> 16));
    out.push_back(static_cast<char>(stream >> 8));
    out.push_back(static_cast<char>(stream));
    out += payload;
}

struct sent_frame {
    int type;
    int flags;
    std::uint32_t stream;
    std::string payload;
};

std::vector<sent_frame> parse_written(const std::string& written) {
    std::vector<sent_frame> frames;
    std::size_t at = 24;  // l'en-tête de connexion
    while (at + 9 <= written.size()) {
        const auto* p = reinterpret_cast<const unsigned char*>(written.data() + at);
        const std::size_t length = (static_cast<std::size_t>(p[0]) << 16) | (static_cast<std::size_t>(p[1]) << 8) | p[2];
        const std::uint32_t stream = (static_cast<std::uint32_t>(p[5]) << 24) | (static_cast<std::uint32_t>(p[6]) << 16) |
                                     (static_cast<std::uint32_t>(p[7]) << 8) | p[8];
        frames.push_back({p[3], p[4], stream, written.substr(at + 9, length)});
        at += 9 + length;
    }
    return frames;
}

void huffman_and_hpack() {
    // RFC 7541, annexe C.4 : trois requêtes de suite, codées en Huffman, avec la table dynamique.
    h2::hpack_decoder decoder;
    std::vector<h2::header> first;
    CHECK(decoder.decode(bytes_of("828684418cf1e3c2e5f23a6ba0ab90f4ff"), first));
    CHECK(first.size() == 4);
    CHECK(has(first, ":method", "GET") && has(first, ":scheme", "http") && has(first, ":path", "/") && has(first, ":authority", "www.example.com"));

    std::vector<h2::header> second;
    CHECK(decoder.decode(bytes_of("828684be5886a8eb10649cbf"), second));
    CHECK(second.size() == 5);
    CHECK(has(second, ":authority", "www.example.com") && has(second, "cache-control", "no-cache"));

    std::vector<h2::header> third;
    CHECK(decoder.decode(bytes_of("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf"), third));
    CHECK(third.size() == 5);
    CHECK(has(third, ":scheme", "https") && has(third, ":path", "/index.html") && has(third, "custom-key", "custom-value"));

    // Sans Huffman (annexe C.3.1), puis un bloc refusé.
    h2::hpack_decoder plain;
    std::vector<h2::header> list;
    CHECK(plain.decode(bytes_of("828684410f7777772e6578616d706c652e636f6d"), list));
    CHECK(has(list, ":authority", "www.example.com"));
    std::vector<h2::header> bad;
    CHECK(!plain.decode(bytes_of("be"), bad) || !bad.empty());  // l'index 62 existe maintenant (la table dynamique garde l'autorité)
    h2::hpack_decoder fresh;
    std::vector<h2::header> none;
    CHECK(!fresh.decode(bytes_of("be"), none));
    CHECK(!fresh.decode(bytes_of("40"), none));

    // Aller-retour de l'encodeur.
    const std::vector<h2::header> request{{":method", "POST"}, {"x-nom", "valeur é"}, {"accept", "*/*"}};
    h2::hpack_decoder back;
    std::vector<h2::header> decoded;
    CHECK(back.decode(h2::hpack_encode(request), decoded));
    CHECK(decoded.size() == 3 && has(decoded, "x-nom", "valeur é") && has(decoded, ":method", "POST"));

    // Les en-têtes d'une requête : noms en minuscules, ceux que HTTP/2 interdit retirés.
    const auto lines = h2::request_headers("Accept: text/html\r\nHost: x\r\nConnection: close\r\nX-Token:  abc \r\nContent-Length: 4");
    CHECK(lines.size() == 2 && has(lines, "accept", "text/html") && has(lines, "x-token", "abc"));
}

void simple_exchange() {
    mock_io io;
    add(io.script, 4, 0, 0, std::string("\x00\x03\x00\x00\x00\x64", 6));      // réglages du serveur
    add(io.script, 4, 1, 0, "");                                              // son accusé de nos réglages
    add(io.script, 6, 0, 0, std::string("12345678"));                          // un ping
    // 200, content-type: text/plain (index 31 = content-type, valeur littérale), x-nombre: 7.
    const std::string block = bytes_of("88") + std::string("\x0f\x10\x0atext/plain", 13) + std::string("\x00\x08x-nombre\x01" "7", 12);
    add(io.script, 1, 4, 1, block);
    add(io.script, 0, 0, 1, "bonjour ");
    add(io.script, 0, 1, 1, "monde");

    std::string response;
    const bool ok = h2::exchange(io, "GET", "https", "exemple.test", "/chemin?x=1", h2::request_headers("Accept: */*"), "", 1000000, response);
    CHECK(ok);
    CHECK(response.starts_with("HTTP/1.1 200 \r\n"));
    CHECK(response.find("content-type: text/plain\r\n") != std::string::npos);
    CHECK(response.find("x-nombre: 7\r\n") != std::string::npos);
    CHECK(response.ends_with("Content-Length: 13\r\nConnection: close\r\n\r\nbonjour monde"));

    const auto frames = parse_written(io.written);
    CHECK(io.written.starts_with("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"));
    CHECK(frames.size() >= 5);
    CHECK(frames[0].type == 4 && frames[0].payload == std::string("\x00\x02\x00\x00\x00\x00", 6));  // pas de poussée
    CHECK(frames[1].type == 1 && frames[1].flags == 5 && frames[1].stream == 1);                       // en-têtes, fin de flux
    bool acked = false, pinged = false;
    int updates = 0;
    for (const auto& frame : frames) {
        if (frame.type == 4 && frame.flags == 1) acked = true;
        if (frame.type == 6 && frame.flags == 1 && frame.payload == "12345678") pinged = true;
        if (frame.type == 8) ++updates;
    }
    CHECK(acked && pinged);
    CHECK(updates >= 2);

    // La requête décodée à l'autre bout.
    h2::hpack_decoder decoder;
    std::vector<h2::header> sent;
    CHECK(decoder.decode(frames[1].payload, sent));
    CHECK(has(sent, ":method", "GET") && has(sent, ":path", "/chemin?x=1") && has(sent, ":authority", "exemple.test") && has(sent, "accept", "*/*"));
}

void body_and_window() {
    // Un corps plus grand que la fenêtre de départ (65 535) : le client attend le feu vert du serveur.
    std::string body(70000, 'a');
    for (std::size_t i = 0; i < body.size(); ++i) body[i] = static_cast<char>('a' + i % 26);
    mock_io io;
    add(io.script, 4, 0, 0, "");
    add(io.script, 8, 0, 0, std::string("\x00\x00\x27\x10", 4));
    add(io.script, 8, 0, 1, std::string("\x00\x00\x27\x10", 4));
    add(io.script, 1, 5, 1, bytes_of("89"));  // 204, fin de flux

    std::string response;
    CHECK(h2::exchange(io, "POST", "https", "exemple.test", "/", {}, body, 1000000, response));
    CHECK(response.starts_with("HTTP/1.1 204 \r\n"));
    std::string data;
    int last_flags = -1;
    for (const auto& frame : parse_written(io.written)) {
        if (frame.type == 0) {
            data += frame.payload;
            last_flags = frame.flags;
        }
    }
    CHECK(data == body);
    CHECK(last_flags == 1);
}

void failures_are_reported() {
    {
        mock_io io;  // le serveur ne dit rien : connexion coupée
        std::string response;
        CHECK(!h2::exchange(io, "GET", "https", "x", "/", {}, "", 1000, response));
    }
    {
        mock_io io;
        add(io.script, 7, 0, 0, std::string(8, '\0'));  // GOAWAY
        std::string response;
        CHECK(!h2::exchange(io, "GET", "https", "x", "/", {}, "", 1000, response));
    }
    {
        mock_io io;
        add(io.script, 3, 0, 1, std::string(4, '\0'));  // RST_STREAM
        std::string response;
        CHECK(!h2::exchange(io, "GET", "https", "x", "/", {}, "", 1000, response));
    }
    {
        mock_io io;  // une réponse plus grande que la limite
        add(io.script, 1, 4, 1, bytes_of("88"));
        add(io.script, 0, 1, 1, std::string(100, 'x'));
        std::string response;
        CHECK(!h2::exchange(io, "GET", "https", "x", "/", {}, "", 50, response));
    }
    {
        mock_io io;  // un bloc d'en-têtes illisible
        add(io.script, 1, 5, 1, bytes_of("ff"));
        std::string response;
        CHECK(!h2::exchange(io, "GET", "https", "x", "/", {}, "", 1000, response));
    }
}

}  // namespace

int main() {
    huffman_and_hpack();
    simple_exchange();
    body_and_window();
    failures_are_reported();
    if (failures == 0) std::printf("http2_tests : tout passe\n");
    return failures == 0 ? 0 : 1;
}

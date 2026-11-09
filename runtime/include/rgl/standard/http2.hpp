#pragma once

// Un client HTTP/2 : une requête sur une connexion chiffrée où le serveur a choisi `h2` (négociation ALPN). Header seul, comme
// `http_detail.hpp` : le transport (`Io`) est un paramètre de patron qui sait `write(std::string_view)` et `read(char*, size)`.
//
// Ce qui est pris en charge : l'en-tête de connexion et les réglages, une requête (corps compris, découpé selon la fenêtre du
// serveur), la réponse en plusieurs trames, la compression d'en-têtes HPACK au complet (table dynamique, code de Huffman), le contrôle
// de flux, les `PING` et les `CONTINUATION`. Ce qui ne l'est pas : le multiplexage de plusieurs requêtes (une seule par connexion), les
// priorités, les poussées du serveur (refusées dans les réglages), les trailers.
//
// La réponse est rendue dans la forme d'une réponse HTTP/1.1 — ligne de statut, en-têtes, ligne vide, corps — pour que le code qui lit
// celle de HTTP/1.1 la lise sans changement. Le corps n'est jamais en `chunked` : `Content-Length` donne sa taille.

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <deque>
#include <string>
#include <string_view>
#include <vector>

#include "rgl/standard/hpack_tables.hpp"

namespace rgl::standard::http::h2 {

struct header {
    std::string name;
    std::string value;
};

namespace detail {

struct huffman_node {
    int child[2] = {-1, -1};
    int symbol = -1;
};

// L'arbre de décodage du code de Huffman, construit une fois depuis la table de l'annexe B du RFC 7541.
inline const std::vector<huffman_node>& huffman_tree() {
    static const std::vector<huffman_node> tree = [] {
        std::vector<huffman_node> nodes(1);
        for (int symbol = 0; symbol < 257; ++symbol) {
            const auto& entry = hpack_tables::huffman[symbol];
            int at = 0;
            for (int bit = entry.length - 1; bit >= 0; --bit) {
                const int branch = static_cast<int>((entry.code >> bit) & 1u);
                if (nodes[static_cast<std::size_t>(at)].child[branch] < 0) {
                    nodes[static_cast<std::size_t>(at)].child[branch] = static_cast<int>(nodes.size());
                    nodes.emplace_back();
                }
                at = nodes[static_cast<std::size_t>(at)].child[branch];
            }
            nodes[static_cast<std::size_t>(at)].symbol = symbol;
        }
        return nodes;
    }();
    return tree;
}

}  // namespace detail

// Décode un texte codé en Huffman ; faux s'il est mal formé (symbole de fin dedans, remplissage trop long ou qui n'est pas fait de 1).
inline bool huffman_decode(const unsigned char* data, std::size_t size, std::string& out) {
    const auto& tree = detail::huffman_tree();
    int at = 0;
    int depth = 0;
    bool all_ones = true;
    for (std::size_t i = 0; i < size; ++i) {
        for (int bit = 7; bit >= 0; --bit) {
            const int branch = (data[i] >> bit) & 1;
            at = tree[static_cast<std::size_t>(at)].child[branch];
            if (at < 0) return false;
            ++depth;
            all_ones = all_ones && branch == 1;
            const int symbol = tree[static_cast<std::size_t>(at)].symbol;
            if (symbol >= 0) {
                if (symbol == 256) return false;
                out.push_back(static_cast<char>(symbol));
                at = 0;
                depth = 0;
                all_ones = true;
            }
        }
    }
    return depth <= 7 && all_ones;
}

// Un texte HPACK : la longueur sur 7 bits (puis plus si elle déborde), puis les octets, sans Huffman.
inline void hpack_integer(std::string& out, std::uint8_t first_bits, int prefix, std::uint64_t value) {
    const std::uint64_t limit = (1u << prefix) - 1u;
    if (value < limit) {
        out.push_back(static_cast<char>(first_bits | value));
        return;
    }
    out.push_back(static_cast<char>(first_bits | limit));
    value -= limit;
    while (value >= 128) {
        out.push_back(static_cast<char>((value & 127u) | 128u));
        value >>= 7;
    }
    out.push_back(static_cast<char>(value));
}

inline void hpack_string(std::string& out, std::string_view text) {
    hpack_integer(out, 0, 7, text.size());
    out.append(text);
}

// Écrit des en-têtes en « champ littéral sans indexation, nom nouveau » : plus gros qu'une table dynamique, mais toujours valide.
inline std::string hpack_encode(const std::vector<header>& headers) {
    std::string out;
    for (const header& item : headers) {
        out.push_back('\0');
        hpack_string(out, item.name);
        hpack_string(out, item.value);
    }
    return out;
}

// Le décodeur d'en-têtes d'une connexion : il garde la table dynamique d'un bloc au suivant.
class hpack_decoder {
public:
    bool decode(const std::string& block, std::vector<header>& out) {
        const auto* p = reinterpret_cast<const unsigned char*>(block.data());
        const auto* end = p + block.size();
        while (p < end) {
            const unsigned char first = *p;
            header item;
            if (first & 0x80) {
                std::uint64_t index = 0;
                if (!_integer(p, end, 7, index) || !_lookup(index, item)) return false;
                out.push_back(std::move(item));
            } else if ((first & 0x20) && !(first & 0x40)) {
                std::uint64_t size = 0;
                if (!_integer(p, end, 5, size) || size > _limit) return false;
                _maximum = static_cast<std::size_t>(size);
                _evict();
            } else {
                const bool indexed = (first & 0x40) != 0;
                std::uint64_t index = 0;
                if (!_integer(p, end, indexed ? 6 : 4, index)) return false;
                if (index == 0) {
                    if (!_string(p, end, item.name)) return false;
                } else {
                    header named;
                    if (!_lookup(index, named)) return false;
                    item.name = std::move(named.name);
                }
                if (!_string(p, end, item.value)) return false;
                if (indexed) _add(item);
                out.push_back(std::move(item));
            }
        }
        return true;
    }

private:
    static constexpr std::size_t _limit = 4096;  // la taille de table annoncée dans nos réglages (la valeur par défaut)
    std::deque<header> _dynamic;
    std::size_t _size = 0;
    std::size_t _maximum = _limit;

    static bool _integer(const unsigned char*& p, const unsigned char* end, int prefix, std::uint64_t& value) {
        if (p >= end) return false;
        const std::uint64_t mask = (1u << prefix) - 1u;
        value = *p++ & mask;
        if (value < mask) return true;
        int shift = 0;
        for (;;) {
            if (p >= end || shift > 56) return false;
            const unsigned char next = *p++;
            value += static_cast<std::uint64_t>(next & 127u) << shift;
            shift += 7;
            if (!(next & 128)) return true;
        }
    }

    static bool _string(const unsigned char*& p, const unsigned char* end, std::string& out) {
        if (p >= end) return false;
        const bool huffman = (*p & 0x80) != 0;
        std::uint64_t length = 0;
        if (!_integer(p, end, 7, length) || length > static_cast<std::uint64_t>(end - p)) return false;
        const auto size = static_cast<std::size_t>(length);
        const bool ok = huffman ? huffman_decode(p, size, out) : (out.append(reinterpret_cast<const char*>(p), size), true);
        p += size;
        return ok;
    }

    bool _lookup(std::uint64_t index, header& item) const {
        if (index == 0) return false;
        if (index <= 61) {
            const auto& entry = hpack_tables::static_table[index - 1];
            item = {entry.name, entry.value};
            return true;
        }
        const std::uint64_t at = index - 62;
        if (at >= _dynamic.size()) return false;
        item = _dynamic[static_cast<std::size_t>(at)];
        return true;
    }

    void _add(const header& item) {
        _dynamic.push_front(item);
        _size += item.name.size() + item.value.size() + 32;
        _evict();
    }

    void _evict() {
        while (_size > _maximum && !_dynamic.empty()) {
            _size -= _dynamic.back().name.size() + _dynamic.back().value.size() + 32;
            _dynamic.pop_back();
        }
    }
};

namespace frame {
constexpr std::uint8_t data = 0, headers = 1, rst_stream = 3, settings = 4, ping = 6, goaway = 7, window_update = 8, continuation = 9;
constexpr std::uint8_t end_stream = 1, ack = 1, end_headers = 4, padded = 8, priority = 0x20;
}  // namespace frame

namespace detail {

inline void put_frame(std::string& out, std::uint8_t type, std::uint8_t flags, std::uint32_t stream, std::string_view payload) {
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
    out.append(payload);
}

inline std::string be32(std::uint32_t value) {
    std::string out;
    out.push_back(static_cast<char>(value >> 24));
    out.push_back(static_cast<char>(value >> 16));
    out.push_back(static_cast<char>(value >> 8));
    out.push_back(static_cast<char>(value));
    return out;
}

inline std::uint32_t read_be32(const unsigned char* p) {
    return (static_cast<std::uint32_t>(p[0]) << 24) | (static_cast<std::uint32_t>(p[1]) << 16) | (static_cast<std::uint32_t>(p[2]) << 8) | p[3];
}

template <typename Io>
bool read_exact(Io& io, char* destination, std::size_t size) {
    std::size_t done = 0;
    while (done < size) {
        const long long count = io.read(destination + done, size - done);
        if (count <= 0) return false;
        done += static_cast<std::size_t>(count);
    }
    return true;
}

inline std::string lower(std::string_view text) {
    std::string out(text);
    for (char& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

inline std::string_view trim(std::string_view text) {
    while (!text.empty() && (text.front() == ' ' || text.front() == '\t')) text.remove_prefix(1);
    while (!text.empty() && (text.back() == ' ' || text.back() == '\t' || text.back() == '\r')) text.remove_suffix(1);
    return text;
}

}  // namespace detail

// Découpe des lignes « Nom: valeur » séparées par `\r\n` en en-têtes HTTP/2 : noms en minuscules, et sans ceux que HTTP/2 interdit ou
// reconstruit lui-même.
inline std::vector<header> request_headers(std::string_view lines) {
    std::vector<header> out;
    std::size_t start = 0;
    while (start <= lines.size()) {
        std::size_t stop = lines.find('\n', start);
        if (stop == std::string_view::npos) stop = lines.size();
        const std::string_view line = lines.substr(start, stop - start);
        start = stop + 1;
        const auto colon = line.find(':');
        if (colon == std::string_view::npos || colon == 0) continue;
        std::string name = detail::lower(detail::trim(line.substr(0, colon)));
        if (name == "host" || name == "connection" || name == "content-length" || name == "transfer-encoding" || name == "keep-alive" ||
            name == "upgrade" || name == "proxy-connection" || name == "te")
            continue;
        out.push_back({std::move(name), std::string(detail::trim(line.substr(colon + 1)))});
    }
    return out;
}

// Une requête, sa réponse. `response` reçoit le texte d'une réponse HTTP/1.1 ; faux si la connexion se coupe, si le serveur refuse ou si
// la réponse dépasse `max_size` octets.
template <typename Io>
bool exchange(Io& io, std::string_view method, std::string_view scheme, std::string_view authority, std::string_view target,
              const std::vector<header>& extra, std::string_view body, std::size_t max_size, std::string& response) {
    // Les réglages : pas de poussée du serveur. Le reste garde sa valeur par défaut.
    std::string start = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
    std::string settings;
    settings.push_back('\0');
    settings.push_back('\x02');
    settings += detail::be32(0);
    detail::put_frame(start, frame::settings, 0, 0, settings);

    std::vector<header> request{{":method", std::string(method)}, {":scheme", std::string(scheme)}, {":authority", std::string(authority)},
                                {":path", std::string(target)}};
    request.insert(request.end(), extra.begin(), extra.end());
    if (!body.empty()) request.push_back({"content-length", std::to_string(body.size())});
    const std::string block = hpack_encode(request);

    constexpr std::size_t initial_frame_size = 16384;
    std::size_t peer_frame_size = initial_frame_size;
    // Les en-têtes tiennent presque toujours dans une trame ; sinon on les continue avec CONTINUATION (dont la taille limite est celle
    // du serveur, 16384 tant qu'il n'en dit pas plus : on s'y tient avant de lire ses réglages).
    const std::uint8_t end = body.empty() ? frame::end_stream : 0;
    if (block.size() <= peer_frame_size) {
        detail::put_frame(start, frame::headers, static_cast<std::uint8_t>(frame::end_headers | end), 1, block);
    } else {
        detail::put_frame(start, frame::headers, end, 1, std::string_view(block).substr(0, peer_frame_size));
        for (std::size_t at = peer_frame_size; at < block.size(); at += peer_frame_size) {
            const std::size_t length = std::min(peer_frame_size, block.size() - at);
            detail::put_frame(start, frame::continuation, at + length >= block.size() ? frame::end_headers : 0, 1,
                              std::string_view(block).substr(at, length));
        }
    }
    if (!io.write(start)) return false;

    hpack_decoder decoder;
    std::vector<header> answer;
    std::string fragment;
    std::string received;
    std::int64_t connection_window = 65535;
    std::int64_t stream_window = 65535;
    std::int64_t peer_initial_window = 65535;
    std::size_t sent = 0;
    bool finished = false;
    bool have_headers = false;
    bool collecting = false;
    bool final_headers = false;

    for (;;) {
        while (sent < body.size() && connection_window > 0 && stream_window > 0) {
            const std::size_t length = std::min<std::size_t>({body.size() - sent, peer_frame_size, static_cast<std::size_t>(connection_window),
                                                              static_cast<std::size_t>(stream_window)});
            std::string out;
            detail::put_frame(out, frame::data, sent + length == body.size() ? frame::end_stream : 0, 1, body.substr(sent, length));
            if (!io.write(out)) return false;
            sent += length;
            connection_window -= static_cast<std::int64_t>(length);
            stream_window -= static_cast<std::int64_t>(length);
        }
        if (finished) break;

        char head[9];
        if (!detail::read_exact(io, head, sizeof(head))) return false;
        const auto* h = reinterpret_cast<const unsigned char*>(head);
        const std::size_t length = (static_cast<std::size_t>(h[0]) << 16) | (static_cast<std::size_t>(h[1]) << 8) | h[2];
        const std::uint8_t type = h[3];
        const std::uint8_t flags = h[4];
        const std::uint32_t stream = detail::read_be32(h + 5) & 0x7fffffffu;
        if (length > initial_frame_size) return false;
        std::string payload(length, '\0');
        if (length && !detail::read_exact(io, payload.data(), length)) return false;
        const auto* bytes = reinterpret_cast<const unsigned char*>(payload.data());

        if (collecting && type != frame::continuation) return false;
        switch (type) {
            case frame::settings: {
                if (flags & frame::ack) break;
                if (length % 6 != 0) return false;
                for (std::size_t i = 0; i < length; i += 6) {
                    const unsigned id = (static_cast<unsigned>(bytes[i]) << 8) | bytes[i + 1];
                    const std::uint32_t value = detail::read_be32(bytes + i + 2);
                    if (id == 4) {
                        if (value > 0x7fffffffu) return false;
                        stream_window += static_cast<std::int64_t>(value) - peer_initial_window;
                        peer_initial_window = value;
                    } else if (id == 5) {
                        if (value < initial_frame_size || value > 16777215u) return false;
                        peer_frame_size = value;
                    }
                }
                std::string out;
                detail::put_frame(out, frame::settings, frame::ack, 0, {});
                if (!io.write(out)) return false;
                break;
            }
            case frame::window_update: {
                if (length != 4) return false;
                const std::uint32_t increment = detail::read_be32(bytes) & 0x7fffffffu;
                if (increment == 0) return false;
                std::int64_t& window = stream == 0 ? connection_window : stream_window;
                if (stream != 0 && stream != 1) break;
                window += increment;
                if (window > 0x7fffffffLL) return false;
                break;
            }
            case frame::ping: {
                if (length != 8) return false;
                if (!(flags & frame::ack)) {
                    std::string out;
                    detail::put_frame(out, frame::ping, frame::ack, 0, payload);
                    if (!io.write(out)) return false;
                }
                break;
            }
            case frame::headers:
            case frame::continuation: {
                if (stream != 1) return false;
                std::size_t from = 0;
                std::size_t to = length;
                if (type == frame::headers) {
                    if (flags & frame::padded) {
                        if (length < 1) return false;
                        const std::size_t padding = bytes[0];
                        from = 1;
                        if (padding > length - from) return false;
                        to = length - padding;
                    }
                    if (flags & frame::priority) {
                        if (to - from < 5) return false;
                        from += 5;
                    }
                    if (flags & frame::end_stream) finished = true;
                } else if (!collecting) {
                    return false;
                }
                fragment.append(payload, from, to - from);
                if (fragment.size() > 262144) return false;
                if (flags & frame::end_headers) {
                    collecting = false;
                    std::vector<header> block_headers;
                    if (!decoder.decode(fragment, block_headers)) return false;
                    fragment.clear();
                    // Une réponse intermédiaire (100, 103…) est suivie de la vraie : on la laisse passer.
                    const bool interim = !block_headers.empty() && block_headers.front().name == ":status" &&
                                         !block_headers.front().value.empty() && block_headers.front().value[0] == '1';
                    if (!interim && !final_headers) {
                        answer = std::move(block_headers);
                        final_headers = true;
                    }
                    have_headers = true;
                } else {
                    collecting = true;
                }
                break;
            }
            case frame::data: {
                if (stream != 1) return false;
                std::size_t from = 0;
                std::size_t to = length;
                if (flags & frame::padded) {
                    if (length < 1) return false;
                    const std::size_t padding = bytes[0];
                    from = 1;
                    if (padding > length - from) return false;
                    to = length - padding;
                }
                if (received.size() + (to - from) > max_size) return false;
                received.append(payload, from, to - from);
                if (length > 0) {
                    std::string out;
                    const std::string increment = detail::be32(static_cast<std::uint32_t>(length));
                    detail::put_frame(out, frame::window_update, 0, 0, increment);
                    if (!(flags & frame::end_stream)) detail::put_frame(out, frame::window_update, 0, 1, increment);
                    if (!io.write(out)) return false;
                }
                if (flags & frame::end_stream) finished = true;
                break;
            }
            case frame::rst_stream:
                if (stream == 1) return false;
                break;
            case frame::goaway:
                return false;
            default:
                break;
        }
    }
    if (!have_headers || !final_headers) return false;

    std::string status;
    std::string lines;
    for (const header& item : answer) {
        if (item.name == ":status") {
            status = item.value;
        } else if (!item.name.empty() && item.name[0] != ':' && item.name != "transfer-encoding" && item.name != "connection" &&
                   item.name != "content-length" && item.name != "keep-alive") {
            lines.append(item.name).append(": ").append(item.value).append("\r\n");
        }
    }
    if (status.size() != 3) return false;
    response.clear();
    response.append("HTTP/1.1 ").append(status).append(" \r\n").append(lines);
    response.append("Content-Length: ").append(std::to_string(received.size())).append("\r\nConnection: close\r\n\r\n").append(received);
    return true;
}

}  // namespace rgl::standard::http::h2

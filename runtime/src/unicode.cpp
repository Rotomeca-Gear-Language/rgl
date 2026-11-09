#include "rgl/standard/unicode.hpp"

#include <algorithm>
#include <vector>

#include "rgl/standard/unicode_tables.hpp"

namespace rgl::standard::unicode {

namespace {

namespace t = tables;

constexpr std::uint32_t s_base = 0xAC00;
constexpr std::uint32_t l_base = 0x1100;
constexpr std::uint32_t v_base = 0x1161;
constexpr std::uint32_t t_base = 0x11A7;
constexpr std::uint32_t l_count = 19;
constexpr std::uint32_t v_count = 21;
constexpr std::uint32_t t_count = 28;
constexpr std::uint32_t n_count = v_count * t_count;
constexpr std::uint32_t s_count = l_count * n_count;

std::size_t sequence_length(const unsigned char* data, std::size_t size, std::size_t at, std::uint32_t& code) {
    const unsigned char first = data[at];
    std::size_t length = 0;
    std::uint32_t value = 0;
    if (first < 0x80) {
        code = first;
        return 1;
    }
    if (first >= 0xC2 && first <= 0xDF) {
        length = 2;
        value = first & 0x1F;
    } else if (first >= 0xE0 && first <= 0xEF) {
        length = 3;
        value = first & 0x0F;
    } else if (first >= 0xF0 && first <= 0xF4) {
        length = 4;
        value = first & 0x07;
    } else {
        return 0;
    }
    if (at + length > size) return 0;
    for (std::size_t i = 1; i < length; ++i) {
        const unsigned char next = data[at + i];
        if ((next & 0xC0) != 0x80) return 0;
        value = (value << 6) | (next & 0x3F);
    }
    if ((length == 3 && value < 0x800) || (length == 4 && (value < 0x10000 || value > 0x10FFFF))) return 0;
    if (value >= 0xD800 && value <= 0xDFFF) return 0;
    code = value;
    return length;
}

void append_utf8(std::string& out, std::uint32_t code) {
    if (code < 0x80) {
        out.push_back(static_cast<char>(code));
    } else if (code < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (code >> 6)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    } else if (code < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (code >> 12)));
        out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (code >> 18)));
        out.push_back(static_cast<char>(0x80 | ((code >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
    }
}

const t::decomposition_entry* find_decomposition(const t::decomposition_entry* begin, const t::decomposition_entry* end,
                                                 std::uint32_t code) {
    const auto it = std::lower_bound(begin, end, code, [](const t::decomposition_entry& e, std::uint32_t c) { return e.code < c; });
    return it != end && it->code == code ? it : nullptr;
}

// Décompose un caractère, récursivement, dans `out`.
void decompose(std::uint32_t code, bool compatibility, std::vector<std::uint32_t>& out) {
    if (code >= s_base && code < s_base + s_count) {
        const std::uint32_t index = code - s_base;
        out.push_back(l_base + index / n_count);
        out.push_back(v_base + (index % n_count) / t_count);
        if (const std::uint32_t trailing = index % t_count) out.push_back(t_base + trailing);
        return;
    }
    const t::decomposition_entry* entry = nullptr;
    if (compatibility) entry = find_decomposition(std::begin(t::compatibility), std::end(t::compatibility), code);
    if (!entry) entry = find_decomposition(std::begin(t::canonical), std::end(t::canonical), code);
    if (!entry) {
        out.push_back(code);
        return;
    }
    for (std::size_t i = 0; i < entry->length; ++i) decompose(t::pool[entry->offset + i], compatibility, out);
}

// Range par classe de combinaison croissante chaque suite de caractères combinants, sans jamais permuter deux classes égales.
void reorder(std::vector<std::uint32_t>& codes) {
    std::size_t i = 0;
    while (i < codes.size()) {
        if (combining_class(codes[i]) == 0) {
            ++i;
            continue;
        }
        std::size_t end = i;
        while (end < codes.size() && combining_class(codes[end]) != 0) ++end;
        std::stable_sort(codes.begin() + static_cast<std::ptrdiff_t>(i), codes.begin() + static_cast<std::ptrdiff_t>(end),
                         [](std::uint32_t a, std::uint32_t b) { return combining_class(a) < combining_class(b); });
        i = end;
    }
}

// Le caractère que forment deux caractères, ou 0.
std::uint32_t compose_pair(std::uint32_t first, std::uint32_t second) {
    if (first >= l_base && first < l_base + l_count && second >= v_base && second < v_base + v_count)
        return s_base + ((first - l_base) * v_count + (second - v_base)) * t_count;
    if (first >= s_base && first < s_base + s_count && (first - s_base) % t_count == 0 && second > t_base && second < t_base + t_count)
        return first + (second - t_base);
    const auto begin = std::begin(t::composition);
    const auto end = std::end(t::composition);
    const auto it = std::lower_bound(begin, end, std::pair{first, second}, [](const t::composition_entry& e, const std::pair<std::uint32_t, std::uint32_t>& key) {
        return e.first != key.first ? e.first < key.first : e.second < key.second;
    });
    return it != end && it->first == first && it->second == second ? it->composed : 0;
}

std::vector<std::uint32_t> compose(const std::vector<std::uint32_t>& codes) {
    std::vector<std::uint32_t> out;
    out.reserve(codes.size());
    std::size_t starter = SIZE_MAX;
    int last_class = -1;
    for (const std::uint32_t code : codes) {
        const int klass = combining_class(code);
        if (starter != SIZE_MAX && (last_class < klass || last_class == 0)) {
            if (const std::uint32_t composed = compose_pair(out[starter], code)) {
                out[starter] = composed;
                continue;
            }
        }
        if (klass == 0) {
            starter = out.size();
            last_class = 0;
        } else {
            last_class = klass;
        }
        out.push_back(code);
    }
    return out;
}

}  // namespace

std::uint8_t combining_class(std::uint32_t code) {
    if (code < 0x300) return 0;
    const auto begin = std::begin(t::combining);
    const auto end = std::end(t::combining);
    const auto it = std::lower_bound(begin, end, code, [](const t::combining_entry& e, std::uint32_t c) { return e.code < c; });
    return it != end && it->code == code ? it->klass : 0;
}

string normalize(const string& text, form which) {
    const auto* data = reinterpret_cast<const unsigned char*>(text.data());
    const std::size_t size = text.size();
    const bool compatibility = which == form::nfkc || which == form::nfkd;
    const bool composing = which == form::nfc || which == form::nfkc;

    // Les octets refusés restent à leur place : ils coupent le texte en tranches qu'on normalise chacune.
    std::string result;
    result.reserve(size);
    std::vector<std::uint32_t> codes;
    const auto flush = [&] {
        if (codes.empty()) return;
        reorder(codes);
        if (composing) codes = compose(codes);
        for (const std::uint32_t code : codes) append_utf8(result, code);
        codes.clear();
    };

    for (std::size_t i = 0; i < size;) {
        std::uint32_t code = 0;
        const std::size_t length = sequence_length(data, size, i, code);
        if (length == 0) {
            flush();
            result.push_back(static_cast<char>(data[i]));
            ++i;
            continue;
        }
        decompose(code, compatibility, codes);
        i += length;
    }
    flush();
    return string(result.data(), result.size());
}

string strip_marks(const string& text) {
    const string decomposed = normalize(text, form::nfd);
    const auto* data = reinterpret_cast<const unsigned char*>(decomposed.data());
    const std::size_t size = decomposed.size();
    std::string result;
    result.reserve(size);
    for (std::size_t i = 0; i < size;) {
        std::uint32_t code = 0;
        const std::size_t length = sequence_length(data, size, i, code);
        if (length == 0) {
            result.push_back(static_cast<char>(data[i]));
            ++i;
            continue;
        }
        if (combining_class(code) == 0) result.append(decomposed.data() + i, length);
        i += length;
    }
    return string(result.data(), result.size());
}

}  // namespace rgl::standard::unicode

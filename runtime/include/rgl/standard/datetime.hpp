#pragma once

// rgl::standard::datetime : le type RGL datetime, un instant en UTC compté en millisecondes depuis
// 1970-01-01T00:00:00Z, sur 64 bits signés.
//
// On ajoute ou retranche une durée (`d + 2h`), on soustrait deux dates pour avoir une durée
// (`b - a`), on les compare ; rien d'autre ne se mélange avec un entier. Un débordement arrête le
// programme, dans tous les modes (comme duration). Tout est constexpr tant que rien n'échoue.
//
// Une date s'écrit en texte en ISO 8601, toujours en UTC : `2026-10-24T10:30:00Z`, avec `.mmm` quand
// les millisecondes ne sont pas nulles ; une année hors de 0000 à 9999 s'écrit avec son signe et
// au moins six chiffres (`+012345-…`, `-000001-…`).

#include <chrono>
#include <cstdint>

#include "duration.hpp"

namespace rgl::standard {

namespace primitives {

class datetime {
   public:
    constexpr datetime() noexcept = default;
    constexpr explicit datetime(std::int64_t milliseconds) noexcept : milliseconds_(milliseconds) {}

    // L'instant présent, d'après l'horloge du système.
    static datetime now() noexcept {
        const auto since_epoch = std::chrono::system_clock::now().time_since_epoch();
        return datetime(std::chrono::duration_cast<std::chrono::milliseconds>(since_epoch).count());
    }

    // Les millisecondes depuis 1970-01-01T00:00:00Z.
    constexpr std::int64_t milliseconds() const noexcept { return milliseconds_; }

    friend constexpr bool operator==(datetime left, datetime right) noexcept = default;
    friend constexpr std::strong_ordering operator<=>(datetime left, datetime right) noexcept = default;

    friend constexpr datetime operator+(datetime left, duration right) noexcept {
        std::int64_t result;
        if (__builtin_add_overflow(left.milliseconds_, right.milliseconds(), &result)) detail::duration_failure("débordement de date");
        return datetime(result);
    }

    friend constexpr datetime operator+(duration left, datetime right) noexcept { return right + left; }

    friend constexpr datetime operator-(datetime left, duration right) noexcept {
        std::int64_t result;
        if (__builtin_sub_overflow(left.milliseconds_, right.milliseconds(), &result)) detail::duration_failure("débordement de date");
        return datetime(result);
    }

    friend constexpr duration operator-(datetime left, datetime right) noexcept {
        std::int64_t result;
        if (__builtin_sub_overflow(left.milliseconds_, right.milliseconds_, &result)) detail::duration_failure("débordement de durée");
        return duration(result);
    }

   private:
    std::int64_t milliseconds_ = 0;
};

}  // namespace primitives

using primitives::datetime;

namespace detail {

// Le jour du calendrier grégorien (proleptique) qui est à `days` jours de 1970-01-01.
struct civil_date {
    std::int64_t year;
    unsigned month;  // 1 à 12
    unsigned day;    // 1 à 31
};

// Algorithme de Howard Hinnant (« civil_from_days ») : exact sur tout l'intervalle des int64.
constexpr civil_date civil_from_days(std::int64_t days) noexcept {
    days += 719468;
    const std::int64_t era = (days >= 0 ? days : days - 146096) / 146097;
    const auto day_of_era = static_cast<unsigned>(days - era * 146097);                                          // [0, 146096]
    const unsigned year_of_era = (day_of_era - day_of_era / 1460 + day_of_era / 36524 - day_of_era / 146096) / 365;  // [0, 399]
    const std::int64_t year = static_cast<std::int64_t>(year_of_era) + era * 400;
    const unsigned day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);      // [0, 365]
    const unsigned mp = (5 * day_of_year + 2) / 153;                                                           // [0, 11]
    const unsigned day = day_of_year - (153 * mp + 2) / 5 + 1;                                                 // [1, 31]
    const unsigned month = mp < 10 ? mp + 3 : mp - 9;                                                          // [1, 12]
    return {year + (month <= 2 ? 1 : 0), month, day};
}

// Écrit `value` sur au moins `width` chiffres.
template <class Out>
void datetime_digits(Out& out, std::uint64_t value, int width) {
    char buffer[24];
    int length = 0;
    do {
        buffer[length++] = static_cast<char>('0' + value % 10);
        value /= 10;
    } while (value != 0);
    for (int i = length; i < width; ++i) out.append("0", 1);
    while (length > 0) out.append(&buffer[--length], 1);
}

}  // namespace detail

}  // namespace rgl::standard

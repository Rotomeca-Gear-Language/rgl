#pragma once

// La boucle de guard `*` : le guard reçoit à chaque tour la valeur qu'il vient de renvoyer. Un done
// arrête la boucle, un fail aussi (le train part alors sur le rail) ; une limite de tours l'arrête
// sans l'un ni l'autre. Tout est dans l'en-tête : un programme sans boucle de guard ne l'inclut pas.

#include <cstdint>

#include "rgl/standard/exception.hpp"

namespace rgl::standard {

// Le résultat d'une boucle de guard : le dernier résultat du guard, et si la limite de tours a été
// atteinte avant un done ou un fail.
template <class Guarded>
class [[nodiscard]] loop_result {
public:
    loop_result(Guarded last, bool exhausted) : _last(static_cast<Guarded&&>(last)), _exhausted(exhausted) {}

    bool failed() const noexcept { return _last.failed(); }
    bool exhausted() const noexcept { return _exhausted; }
    const auto& value() const& noexcept { return _last.value(); }
    auto& value() & noexcept { return _last.value(); }
    const auto& error() const& noexcept { return _last.error(); }
    auto& error() & noexcept { return _last.error(); }

private:
    Guarded _last;
    bool _exhausted;
};

// Appelle `step` sur `first`, puis sur chaque valeur qu'il renvoie, jusqu'à un done, un fail ou
// `limit` tours ; une limite nulle n'arrête jamais la boucle.
// `first` est transmis tel quel : déplacé quand l'appelant le cède, sans copie quand step le lit seulement.
template <class T, class Step, class First>
auto repeat(const Step& step, First&& first, std::uint32_t limit) {
    auto result = step(static_cast<First&&>(first));
    using result_type = decltype(result);
    for (std::uint32_t turns = 1; !result.failed() && !result.finished(); ++turns) {
        if (limit != 0 && turns == limit) return loop_result<result_type>(static_cast<result_type&&>(result), true);
        // La nouvelle valeur est construite avant de remplacer l'ancienne, que step lit encore ; un
        // step qui la prend par valeur (paramètre mut) la reçoit déplacée, sans copie.
        result = step(static_cast<result_type&&>(result).value());
    }
    return loop_result<result_type>(static_cast<result_type&&>(result), false);
}

}  // namespace rgl::standard

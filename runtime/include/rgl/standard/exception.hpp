#pragma once

// Les échecs RGL, sans exception C++ : un guard renvoie un rgl::standard::guarded<T, E>, qui contient
// sa valeur, ou l'erreur envoyée par un fail. E est le type d'erreur du guard : un mold d'erreur
// généré (Exception, CastError, une error déclarée), une union de plusieurs, ou no_error pour un
// guard qui ne peut pas échouer. Le train teste le résultat de chaque guard et part sur son rail
// en cas d'échec. Un done laisse passer la valeur reçue en notant que le guard a fini : une boucle
// de guard (loop.hpp) s'arrête alors. Tout est dans l'en-tête : un programme sans guard ne l'inclut pas.

#include <new>

namespace rgl::standard {

// L'erreur d'un guard qui ne peut jamais échouer.
struct no_error {
    friend bool operator==(no_error, no_error) noexcept = default;
};

// Un échec à renvoyer depuis un guard : `return rgl::standard::fail<E>(erreur);`.
template <class E>
struct failure {
    E error;
};

template <class E>
failure<E> fail(E error) {
    return failure<E>{static_cast<E&&>(error)};
}

// Un done dans un guard : `return rgl::standard::done(valeur);` réussit comme `return valeur;`, et
// arrête en plus la boucle de guard qui l'appelle.
template <class T>
struct finish {
    T value;
};

template <class T>
finish<T> done(T value) {
    return finish<T>{static_cast<T&&>(value)};
}

// Le résultat d'un guard : sa valeur, ou l'erreur de son fail. Seule l'une des deux est construite :
// T n'a pas besoin de valeur vide (un [T;N], un mold qui en contient un).
template <class T, class E>
class [[nodiscard]] guarded {
public:
    guarded(T value) : _failed(false) { ::new (static_cast<void*>(&_value)) T(static_cast<T&&>(value)); }
    guarded(finish<T> finished) : _failed(false), _finished(true) { ::new (static_cast<void*>(&_value)) T(static_cast<T&&>(finished.value)); }
    guarded(failure<E> failed) : _failed(true) { ::new (static_cast<void*>(&_error)) E(static_cast<E&&>(failed.error)); }

    guarded(const guarded& other) : _failed(other._failed), _finished(other._finished) {
        if (_failed) ::new (static_cast<void*>(&_error)) E(other._error);
        else ::new (static_cast<void*>(&_value)) T(other._value);
    }

    guarded(guarded&& other) noexcept : _failed(other._failed), _finished(other._finished) {
        if (_failed) ::new (static_cast<void*>(&_error)) E(static_cast<E&&>(other._error));
        else ::new (static_cast<void*>(&_value)) T(static_cast<T&&>(other._value));
    }

    guarded& operator=(const guarded& other) {
        if (this == &other) return *this;
        _destroy();
        ::new (static_cast<void*>(this)) guarded(other);
        return *this;
    }

    guarded& operator=(guarded&& other) noexcept {
        if (this == &other) return *this;
        _destroy();
        ::new (static_cast<void*>(this)) guarded(static_cast<guarded&&>(other));
        return *this;
    }

    ~guarded() { _destroy(); }

    bool failed() const noexcept { return _failed; }
    // Vrai si le guard a réussi par un done.
    bool finished() const noexcept { return _finished; }
    // La valeur, modifiable quand le résultat l'est (un paramètre mut d'une étape de train), ou
    // déplacée hors d'un résultat qui n'est plus lu (le tour suivant d'une boucle de guard).
    const T& value() const& noexcept { return _value; }
    T& value() & noexcept { return _value; }
    T&& value() && noexcept { return static_cast<T&&>(_value); }
    const E& error() const& noexcept { return _error; }
    E& error() & noexcept { return _error; }

private:
    union {
        T _value;
        E _error;
    };
    bool _failed;
    bool _finished = false;

    void _destroy() noexcept {
        if (_failed) _error.~E();
        else _value.~T();
    }
};

// Le résultat d'un guard qui ne renvoie rien : réussi, ou l'erreur de son fail.
template <class E>
class [[nodiscard]] guarded<void, E> {
public:
    guarded() noexcept = default;
    guarded(failure<E> failed) : _error(static_cast<E&&>(failed.error)), _failed(true) {}

    bool failed() const noexcept { return _failed; }
    const E& error() const& noexcept { return _error; }
    E& error() & noexcept { return _error; }

private:
    E _error{};
    bool _failed = false;
};

}  // namespace rgl::standard

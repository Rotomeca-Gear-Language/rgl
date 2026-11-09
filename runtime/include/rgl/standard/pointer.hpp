#pragma once
#include <cstddef>
#include <utility>
#include <type_traits>

namespace rgl::standard {
// ptr possède ; uptr ne ferme que sur une demande explicite de RGL.
template<class T, bool Owned> class pointer {
    mutable T* value_ = nullptr;
public:
    pointer() noexcept = default;
    pointer(std::nullptr_t) noexcept {}
    explicit pointer(T* value) noexcept : value_(value) {}
    pointer(const pointer&) requires (!Owned) = default;
    pointer(const pointer&) requires Owned = delete;
    pointer& operator=(const pointer&) requires (!Owned) = default;
    pointer& operator=(const pointer&) requires Owned = delete;
    pointer(pointer&& other) noexcept : value_(std::exchange(other.value_, nullptr)) {}
    pointer& operator=(pointer&& other) noexcept {
        if (this != &other) {
            if constexpr (Owned) free();
            value_ = std::exchange(other.value_, nullptr);
        }
        return *this;
    }
    ~pointer() { if constexpr (Owned) free(); }
    T* get() const noexcept { return value_; }
    std::add_lvalue_reference_t<T> operator*() const noexcept requires (!std::is_void_v<T>) { return *value_; }
    T* operator->() const noexcept { return value_; }
    T* release() const noexcept { return std::exchange(value_, nullptr); }
    void free() const noexcept requires (!std::is_void_v<T>) { delete std::exchange(value_, nullptr); }
    friend pointer<T, false> operator+(const pointer& p, std::ptrdiff_t n) noexcept { return pointer<T, false>(p.value_ + n); }
    friend pointer<T, false> operator-(const pointer& p, std::ptrdiff_t n) noexcept { return pointer<T, false>(p.value_ - n); }
    friend std::ptrdiff_t operator-(const pointer& a, const pointer& b) noexcept { return a.value_ - b.value_; }
    friend bool operator==(const pointer& a, const pointer& b) noexcept { return a.value_ == b.value_; }
};
}

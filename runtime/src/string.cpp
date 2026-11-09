#include "rgl/standard/string.hpp"

#include <cstring>
#include <functional>
#include <limits>

namespace rgl::standard::primitives {

namespace {

// Plus grande taille de chaîne : au-delà, size + 1 ou l'en-tête du bloc déborderait.
constexpr std::size_t max_size = std::numeric_limits<std::size_t>::max() / 2;

void check_size(std::size_t size) noexcept {
    if (size > max_size) out_of_memory(size);
}

}  // namespace

//==============================================================================
// string
//==============================================================================

string::string(const char* text, std::size_t size, const AbiAllocator& allocator) {
    if (!text || size == 0) return;
    if (size <= small_capacity) {
        _set_small(text, size);
        return;
    }
    _init_heap(text, size, allocator);
}

char* string::_init_heap_uninitialized(std::size_t size, const AbiAllocator& allocator) {
    check_size(size);
    void* block = allocate_or_abort(allocator, sizeof(Control) + size + 1);

    ctrl_ = new (block) Control{1};
    char* characters = reinterpret_cast<char*>(ctrl_) + sizeof(Control);
    characters[size] = '\0';

    ptr_ = characters;
    size_ = size;
    alloc_ = &allocator;
    kind_ = Kind::Heap;
    return characters;
}

void string::_deep_copy_from(const string& other) {
    counters::note_copy(counters::copy_kind::text, other.size_);
    _init_heap(other.ptr_, other.size_, heap_allocator());
}

// Affectation depuis un texte d'arène : *this prend une copie sur le tas, jamais le bloc de l'arène.
void string::_assign_detached(const string& other) {
    counters::note_copy(counters::copy_kind::text, other.size_);
    if (kind_ == Kind::Heap) _release();
    _init_heap(other.ptr_, other.size_, heap_allocator());
}

void string::_init_heap(const char* text, std::size_t size, const AbiAllocator& allocator) {
    std::memcpy(_init_heap_uninitialized(size, allocator), text, size);
}

void string::_retain() const noexcept { ctrl_->references.fetch_add(1, std::memory_order_relaxed); }

void string::_release() noexcept {
    if (ctrl_->references.fetch_sub(1, std::memory_order_acq_rel) == 1) {
        ctrl_->~Control();
        release_block(*alloc_, ctrl_);
    }
    _reset();
}

string string::substr(std::size_t position, std::size_t count) const {
    if (position >= size_) return string();
    const std::size_t remaining = size_ - position;
    return string(data() + position, count < remaining ? count : remaining);
}

std::size_t string::length() const noexcept {
    constexpr std::size_t unknown = static_cast<std::size_t>(-1);
    if (kind_ == Kind::Heap) {
        std::size_t known = ctrl_->characters.load(std::memory_order_relaxed);
        if (known != unknown) return known;
    }

    // Les octets qui ne sont pas des suites d'un caractère multi-octet (10xxxxxx) : un par caractère.
    const unsigned char* bytes = reinterpret_cast<const unsigned char*>(data());
    std::size_t count = 0;
    for (std::size_t i = 0; i < size_; ++i) count += (bytes[i] & 0xC0) != 0x80;

    if (kind_ == Kind::Heap) ctrl_->characters.store(count, std::memory_order_relaxed);
    return count;
}

string string::character(std::size_t rank) const {
    // Un texte sans caractère multi-octet : le caractère de rang rank est l'octet de rang rank.
    if (is_ascii()) return rank < size_ ? string(data() + rank, 1) : string();

    std::size_t seen = 0;
    for (std::size_t i = 0; i < size_; ++i) {
        if ((static_cast<unsigned char>(data()[i]) & 0xC0) == 0x80) continue;
        if (seen++ != rank) continue;
        std::size_t end = i + 1;
        while (end < size_ && (static_cast<unsigned char>(data()[end]) & 0xC0) == 0x80) ++end;
        return string(data() + i, end - i);
    }
    return string();
}

string string::_concat(const char* left, std::size_t left_size, const char* right, std::size_t right_size) {
    if (left_size > max_size - right_size) out_of_memory(left_size + right_size);
    const std::size_t total = left_size + right_size;

    string result;
    char* target = total <= small_capacity ? result.small_ : result._init_heap_uninitialized(total, default_allocator());
    if (left_size) std::memcpy(target, left, left_size);
    if (right_size) std::memcpy(target + left_size, right, right_size);
    target[total] = '\0';
    result.size_ = total;
    return result;
}

string operator+(const string& left, const string& right) {
    return string::_concat(left.data(), left.size(), right.data(), right.size());
}

string operator+(const string& left, const char* right) {
    return string::_concat(left.data(), left.size(), right, right ? std::strlen(right) : 0);
}

string operator+(const char* left, const string& right) {
    return string::_concat(left, left ? std::strlen(left) : 0, right.data(), right.size());
}

//==============================================================================
// mstring
//==============================================================================

mstring::mstring(const AbiAllocator& allocator) noexcept : alloc_(&allocator) {}

mstring::mstring(const char* text, const AbiAllocator& allocator) : mstring(text, text ? std::strlen(text) : 0, allocator) {}

mstring::mstring(const char* text, std::size_t size, const AbiAllocator& allocator) : alloc_(&allocator) {
    if (text) append(text, size);
}

mstring::mstring(const string& text, const AbiAllocator& allocator) : alloc_(&allocator) { append(text); }

// Une copie garde l'allocateur de l'original, sauf celui d'une arène (sa mémoire est rendue à la remise à zéro) : elle est alors sur le tas.
mstring::mstring(const mstring& other) : alloc_(other.alloc_->scoped && other.alloc_ != &default_allocator() ? &heap_allocator() : other.alloc_) {
    counters::note_copy(counters::copy_kind::text, other.size_);
    append(other);
}

// Un déplacement reprend le tampon de l'original, sauf pour une arène : le contenu passe alors dans un tampon du tas.
mstring::mstring(mstring&& other) noexcept : alloc_(other.alloc_->scoped && other.alloc_ != &default_allocator() ? &heap_allocator() : other.alloc_) {
    if (other.alloc_->scoped && other.alloc_ != &default_allocator()) {
        append(other);
        other.clear();
        return;
    }

    buffer_ = other.buffer_;
    size_ = other.size_;
    capacity_ = other.capacity_;
    is_small_ = other.is_small_;
    std::memcpy(small_, other.small_, sizeof(small_));

    other.buffer_ = nullptr;
    other.size_ = 0;
    other.capacity_ = 0;
    other.is_small_ = true;
    other.small_[0] = '\0';
}

mstring& mstring::operator=(const mstring& other) {
    if (this == &other) return *this;

    // On garde notre allocateur : notre tampon a été alloué par lui et doit lui être rendu.
    counters::note_copy(counters::copy_kind::text, other.size_);
    clear();
    append(other);
    return *this;
}

mstring& mstring::operator=(mstring&& other) noexcept {
    if (this == &other) return *this;

    // Le contenu d'une arène qui n'est pas la nôtre est recopié dans notre tampon : *this a pu être créé hors de l'arène et lui survivre.
    if (other.alloc_->scoped && other.alloc_ != alloc_) {
        clear();
        append(other);
        other.clear();
        return *this;
    }

    // Le tampon repris appartient à l'allocateur de other : on adopte cet allocateur avec lui.
    _release_buffer();
    buffer_ = other.buffer_;
    size_ = other.size_;
    capacity_ = other.capacity_;
    alloc_ = other.alloc_;
    is_small_ = other.is_small_;
    std::memcpy(small_, other.small_, sizeof(small_));

    other.buffer_ = nullptr;
    other.size_ = 0;
    other.capacity_ = 0;
    other.is_small_ = true;
    other.small_[0] = '\0';
    return *this;
}

mstring::~mstring() { _release_buffer(); }

void mstring::_release_buffer() noexcept {
    if (!is_small_) release_block(*alloc_, buffer_);
    buffer_ = nullptr;
    capacity_ = 0;
    is_small_ = true;
}

void mstring::clear() noexcept {
    size_ = 0;
    data()[0] = '\0';
}

void mstring::reserve(std::size_t capacity) {
    if (capacity > this->capacity()) _ensure_capacity(capacity);
}

void mstring::shrink_to_fit() noexcept {
    if (is_small_) return;

    if (size_ <= small_capacity) {
        char* old = buffer_;
        std::memcpy(small_, old, size_ + 1);
        release_block(*alloc_, old);
        buffer_ = nullptr;
        capacity_ = 0;
        is_small_ = true;
        return;
    }

    // Réduire est une optimisation : si l'allocateur refuse, on garde le tampon actuel.
    if (size_ < capacity_) {
        if (void* smaller = try_reallocate(*alloc_, buffer_, size_ + 1)) {
            buffer_ = static_cast<char*>(smaller);
            capacity_ = size_;
        }
    }
}

void mstring::_ensure_capacity(std::size_t needed) {
    check_size(needed);
    if (needed <= capacity()) return;

    std::size_t grown = capacity() + capacity() / 2;
    if (grown < needed) grown = needed;
    if (grown < 32) grown = 32;
    if (grown > max_size) grown = needed;

    if (is_small_) {
        char* buffer = static_cast<char*>(allocate_or_abort(*alloc_, grown + 1));
        std::memcpy(buffer, small_, size_ + 1);
        buffer_ = buffer;
        is_small_ = false;
    } else {
        buffer_ = static_cast<char*>(reallocate_or_abort(*alloc_, buffer_, grown + 1));
    }
    capacity_ = grown;
}

bool mstring::_contains(const char* text) const noexcept {
    const char* begin = data();
    // std::less donne un ordre total même entre pointeurs sans rapport.
    return !std::less<const char*>{}(text, begin) && std::less<const char*>{}(text, begin + size_ + 1);
}

void mstring::append(const char* text) {
    if (text) append(text, std::strlen(text));
}

void mstring::append(const char* text, std::size_t count) {
    if (count == 0) return;
    if (size_ > max_size - count) out_of_memory(size_ + count);

    // Si text pointe dans notre propre contenu, l'agrandissement peut déplacer le tampon :
    // on retient sa position relative, pas son adresse.
    const bool inside = _contains(text);
    const std::size_t offset = inside ? static_cast<std::size_t>(text - data()) : 0;

    _ensure_capacity(size_ + count);
    if (inside) text = data() + offset;

    std::memmove(data() + size_, text, count);
    size_ += count;
    data()[size_] = '\0';
}

void mstring::insert(std::size_t position, const char* text, std::size_t count) {
    if (count == 0) return;
    if (position > size_) position = size_;

    // Insérer une partie de soi-même : on passe par une copie, plus simple que de suivre les décalages.
    if (_contains(text)) {
        const mstring copy(text, count, *alloc_);
        insert(position, copy.data(), count);
        return;
    }

    if (size_ > max_size - count) out_of_memory(size_ + count);
    _ensure_capacity(size_ + count);

    char* base = data();
    std::memmove(base + position + count, base + position, size_ - position + 1);
    std::memcpy(base + position, text, count);
    size_ += count;
}

void mstring::erase(std::size_t position, std::size_t count) noexcept {
    if (position >= size_) return;
    if (count > size_ - position) count = size_ - position;

    char* base = data();
    std::memmove(base + position, base + position + count, size_ - position - count + 1);
    size_ -= count;
}

void mstring::resize(std::size_t size, char fill) {
    if (size > size_) {
        _ensure_capacity(size);
        std::memset(data() + size_, fill, size - size_);
    }
    size_ = size;
    data()[size_] = '\0';
}

string mstring::to_immutable() const { return string(data(), size_, *alloc_); }

}  // namespace rgl::standard::primitives

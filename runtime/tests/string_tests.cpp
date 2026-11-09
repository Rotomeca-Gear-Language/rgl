// Tests du runtime : rgl::standard::string et mstring.
// Compilation : zig c++ -std=c++20 -Iruntime/include runtime/src/*.cpp runtime/tests/string_tests.cpp
// Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include "rgl/standard/string.hpp"

using rgl::standard::AbiAllocator;
using rgl::standard::mstring;
using rgl::standard::string;

namespace {

int failures = 0;

#define CHECK(condition)                                                          \
    do {                                                                          \
        if (!(condition)) {                                                       \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition); \
            ++failures;                                                           \
        }                                                                         \
    } while (false)

// Allocateur qui compte ses blocs vivants : un compte non nul à la fin est une fuite, un compte
// négatif un bloc rendu au mauvais allocateur.
template <int Id>
struct Counting {
    static inline long live = 0;
    static void* allocate(const AbiAllocator&, std::size_t size) { ++live; return std::malloc(size); }
    static void* reallocate(const AbiAllocator&, void* block, std::size_t size) { return std::realloc(block, size); }
    static void release(const AbiAllocator&, void* block) { --live; std::free(block); }
    static constexpr AbiAllocator instance{&allocate, &reallocate, &release};
};

using CountingA = Counting<1>;
using CountingB = Counting<2>;

const std::string_view text(const string& s) { return {s.data(), s.size()}; }
const std::string_view text(const mstring& s) { return {s.data(), s.size()}; }

static void test_cpp_interop() {
    const std::string native("a\0b", 3);
    const auto from_native = string::from_std_string(native);
    CHECK(from_native.size() == 3);
    CHECK(from_native.view() == std::string_view(native.data(), native.size()));
    CHECK(from_native.to_std_string() == native);
    CHECK(text(string::from_cstr("texte")) == "texte");
    CHECK(text(string(native)) == std::string_view(native.data(), native.size()));
}

// Un texte de plus de 22 caractères : il passe sur le tas.
constexpr const char* long_text = "une chaîne assez longue pour le tas";

}  // namespace

// --- constexpr : vérifié à la compilation ---------------------------------------

static_assert(string().empty());
static_assert(string("abc").size() == 3);
static_assert(string("abc") == "abc");
static_assert(string("abc") < string("abd"));
static_assert(string("ab") < string("abc"));
static_assert(string("une chaîne longue connue à la compilation").size() > string::small_capacity);
constexpr string constant_long = "une chaîne longue connue à la compilation";
static_assert(constant_long[0] == 'u');

// --- string ---------------------------------------------------------------------

static void test_string_representations() {
    const string small("bonjour");
    CHECK(text(small) == "bonjour");
    CHECK(small.c_str()[small.size()] == '\0');

    const string exact(std::string(22, 'x').c_str());
    CHECK(exact.size() == 22);

    const string heap(long_text);
    CHECK(text(heap) == long_text);
    CHECK(heap.c_str()[heap.size()] == '\0');

    const string null_text(static_cast<const char*>(nullptr));
    CHECK(null_text.empty());

    // La vue constexpr est utilisable à l'exécution, terminée par '\0'.
    CHECK(std::strlen(constant_long.c_str()) == constant_long.size());
}

static void test_string_copies_share_the_heap_block() {
    const string original(long_text);
    const string copy = original;
    CHECK(copy.data() == original.data());

    string assigned;
    assigned = original;
    CHECK(assigned.data() == original.data());
    const string& same = assigned;
    assigned = same;  // auto-affectation, via une référence pour ne pas alerter le compilateur
    CHECK(text(assigned) == long_text);

    const string small_copy = string("abc");
    CHECK(text(small_copy) == "abc");
}

static void test_string_move_empties_the_source() {
    string source(long_text);
    const char* block = source.data();
    string target(std::move(source));
    CHECK(target.data() == block);
    CHECK(source.empty());
    CHECK(source.c_str()[0] == '\0');

    string other("abc");
    other = std::move(target);
    CHECK(other.data() == block);
    CHECK(target.empty());
}

static void test_string_operations() {
    const string s("abcdef");
    CHECK(text(s.substr(2, 3)) == "cde");
    CHECK(text(s.substr(4)) == "ef");
    CHECK(s.substr(10).empty());

    CHECK(text(string("ab") + string("cd")) == "abcd");
    CHECK(text(string("ab") + "cd") == "abcd");
    CHECK(text("ab" + string("cd")) == "abcd");

    // 11 + 11 = 22 : reste petit ; 11 + 12 = 23 : passe sur le tas.
    CHECK((string("aaaaaaaaaaa") + string("bbbbbbbbbbb")).size() == 22);
    const string joined = string("aaaaaaaaaaa") + string("bbbbbbbbbbbb");
    CHECK(text(joined) == "aaaaaaaaaaabbbbbbbbbbbb");
    CHECK(joined.c_str()[23] == '\0');

    CHECK(string("é") > string("z"));  // comparaison en octets non signés
}

static void test_string_custom_allocator_and_no_leak() {
    {
        const string a(long_text, std::strlen(long_text), CountingA::instance);
        CHECK(CountingA::live == 1);
        string b = a;
        CHECK(CountingA::live == 1);  // partagé, pas recopié
        b = string("court");
        CHECK(CountingA::live == 1);
    }
    CHECK(CountingA::live == 0);
}

static void test_string_refcount_across_threads() {
    const string shared(long_text, std::strlen(long_text), CountingA::instance);
    std::vector<std::thread> threads;
    for (int t = 0; t < 4; ++t)
        threads.emplace_back([&shared] {
            for (int i = 0; i < 10000; ++i) {
                string copy = shared;
                (void)copy.size();
            }
        });
    for (auto& thread : threads) thread.join();
    CHECK(CountingA::live == 1);
}

// --- mstring --------------------------------------------------------------------

static void test_mstring_grows_from_small_to_heap() {
    mstring m;
    CHECK(m.empty());
    CHECK(m.capacity() == mstring::small_capacity);

    for (int i = 0; i < 100; ++i) m.push_back(static_cast<char>('a' + i % 26));
    CHECK(m.size() == 100);
    CHECK(m[25] == 'z');
    CHECK(m.c_str()[100] == '\0');
    CHECK(m.capacity() >= 100);

    m.resize(3);
    CHECK(text(m) == "abc");
    m.shrink_to_fit();
    CHECK(m.capacity() == mstring::small_capacity);
    CHECK(text(m) == "abc");
}

static void test_mstring_editing() {
    mstring m("bonjour");
    m.insert(3, "--", 2);
    CHECK(text(m) == "bon--jour");
    m.erase(3, 2);
    CHECK(text(m) == "bonjour");
    m.erase(3, 100);
    CHECK(text(m) == "bon");
    m.insert(100, "!", 1);  // position ramenée à la fin
    CHECK(text(m) == "bon!");
    m.resize(6, '?');
    CHECK(text(m) == "bon!??");
    m.clear();
    CHECK(m.empty() && m.c_str()[0] == '\0');

    m += "a";
    m += 'b';
    m += string("c");
    m += mstring("d");
    CHECK(text(m) == "abcd");
    CHECK(text(m + "e") == "abcde");
    CHECK(text(m.to_immutable()) == "abcd");
}

static void test_mstring_append_and_insert_from_itself() {
    // Le contenu est relu après l'agrandissement : sans précaution, le tampon déplacé serait lu.
    mstring m("0123456789abcdefghij");  // 20 caractères : encore petit
    m.append(m);                        // 40 : passe sur le tas pendant l'ajout
    CHECK(text(m) == "0123456789abcdefghij0123456789abcdefghij");
    m.append(m.data() + 10, 5);
    CHECK(m.size() == 45);
    CHECK(text(m).substr(40) == "abcde");

    mstring n("abcdef");
    n.insert(2, n.data(), 3);
    CHECK(text(n) == "ababccdef");
}

static void test_mstring_keeps_its_allocator() {
    {
        mstring a(long_text, CountingA::instance);
        const mstring b(long_text, CountingB::instance);
        CHECK(CountingA::live == 1 && CountingB::live == 1);

        a = b;  // copie : a garde l'allocateur A
        CHECK(&a.allocator() == &CountingA::instance);
        a.append(long_text);  // agrandit avec A
        CHECK(CountingA::live == 1 && CountingB::live == 1);

        mstring c(CountingB::instance);
        c = std::move(a);  // déplacement : c reprend le tampon de A, et l'allocateur A avec lui
        CHECK(&c.allocator() == &CountingA::instance);
    }
    CHECK(CountingA::live == 0);
    CHECK(CountingB::live == 0);
}

static void test_mstring_comparisons() {
    CHECK(mstring("abc") == mstring("abc"));
    CHECK(mstring("abc") < mstring("abd"));
}

int main() {
    test_cpp_interop();
    test_string_representations();
    test_string_copies_share_the_heap_block();
    test_string_move_empties_the_source();
    test_string_operations();
    test_string_custom_allocator_and_no_leak();
    test_string_refcount_across_threads();
    test_mstring_grows_from_small_to_heap();
    test_mstring_editing();
    test_mstring_append_and_insert_from_itself();
    test_mstring_keeps_its_allocator();
    test_mstring_comparisons();

    // Le bloc de test_string_refcount_across_threads est libéré à la sortie de sa fonction.
    CHECK(CountingA::live == 0);

    if (failures == 0) std::puts("runtime string : toutes les vérifications passent");
    return failures == 0 ? 0 : 1;
}

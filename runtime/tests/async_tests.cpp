// Tests du runtime : l'asynchrone coopératif (promise, boucle d'événements, sleep, block_on).
// Le programme renvoie 0 si tout passe, 1 sinon, et affiche chaque vérification ratée.

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "rgl/standard/async.hpp"
#include "rgl/standard/union.hpp"

namespace {

int failures = 0;

#define CHECK(condition)                                                                \
    do {                                                                                \
        if (!(condition)) {                                                             \
            std::fprintf(stderr, "ÉCHEC %s:%d : %s\n", __FILE__, __LINE__, #condition); \
            ++failures;                                                                 \
        }                                                                               \
    } while (false)

using rgl::standard::async_sleep;
using rgl::standard::block_on;
using rgl::standard::cancel;
using rgl::standard::duration;
using rgl::standard::promise;
using rgl::standard::run_blocking;
using rgl::standard::run_main;
using rgl::standard::task_state;

// Une erreur comme le générateur en écrit : un message et un endroit.
struct test_error {
    std::string rgl_f_message;
    std::string rgl_f_where;
    int rgl_f_kind = -1;   // 0 : Cancelled
};

using nothing = std::optional<test_error>;   // ce que `await` donne d'une promesse sans valeur
using outcome = rgl::standard::result<std::uint32_t, test_error>;

// Les journaux dans lesquels les tâches écrivent, pour vérifier l'ordre.
std::vector<int> journal;

promise<int> add_later(int a, int b, int ms) {
    co_await async_sleep<nothing>(duration(ms));
    co_return a + b;
}

promise<int> immediate(int value) { co_return value; }

promise<nothing> note_after(int ms, int what) {
    co_await async_sleep<nothing>(duration(ms));
    journal.push_back(what);
    co_return nothing{};
}

promise<int> chain(int depth) {
    if (depth == 0) co_return 0;
    const int below = co_await chain(depth - 1);
    co_await async_sleep<nothing>(duration(0));   // une durée nulle ne suspend pas
    co_return below + 1;
}

promise<int> two_at_once(int ms) {
    auto first = add_later(1, 2, ms);    // démarrées toutes les deux avant la première attente
    auto second = add_later(10, 20, ms);
    const int a = co_await first;
    const int b = co_await second;
    co_return a + b;
}

promise<int> twice(promise<int> shared) {
    const int a = co_await shared;
    const int b = co_await shared;   // une promesse terminée se relit
    co_return a * 100 + b;
}

bool detached_ran = false;

promise<nothing> detached() {
    co_await async_sleep<nothing>(duration(30));
    detached_ran = true;
    co_return nothing{};
}

promise<int> starts_detached() {
    detached();   // personne ne tient la promesse : la tâche continue seule
    co_return 7;
}

promise<outcome> exit_ok() { co_return outcome::ok(42u); }
promise<outcome> exit_err() { co_return outcome::err(test_error{"raté", "main.rgl:1:1"}); }
promise<nothing> exit_void_ok() { co_return nothing{}; }
promise<nothing> exit_void_err() { co_return nothing{test_error{"raté", "main.rgl:2:2"}}; }

// L'annulation.
bool slept_through = false;
bool cancelled_seen = false;
bool reached_after = false;
promise<outcome> g_victim;

promise<outcome> sleeper(int ms) {
    co_await async_sleep<nothing>(duration(ms));
    slept_through = true;
    co_return outcome::ok(1u);
}

promise<outcome> parent_of_sleeper() {
    const auto inner = co_await sleeper(5000);
    co_return inner;
}

promise<nothing> watches(promise<outcome> other) {
    const auto result = co_await other;
    cancelled_seen = !result.is_ok() && result.error().rgl_f_kind == 0;
    co_return nothing{};
}

promise<nothing> cancels_victim() {
    cancel(g_victim);
    co_return nothing{};
}

promise<outcome> victim() {
    co_await async_sleep<nothing>(duration(10));
    co_await cancels_victim();   // la victime s'exécute ici : l'annulation la prend à ce point de suspension
    reached_after = true;
    co_return outcome::ok(1u);
}

promise<outcome> finished_ok() { co_return outcome::ok(9u); }

// Les travaux bloquants du pool.
promise<outcome> blocks(int ms, std::uint32_t result) {
    co_return co_await run_blocking<outcome>([ms, result] {
        std::this_thread::sleep_for(std::chrono::milliseconds(ms));
        return outcome::ok(result);
    });
}

promise<outcome> blocks_twice() {
    auto first = blocks(150, 1u);
    auto second = blocks(150, 2u);
    const auto a = co_await first;
    const auto b = co_await second;
    co_return outcome::ok(a.value() * 10u + b.value());
}

long long elapsed_ms(std::chrono::steady_clock::time_point since) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - since).count();
}

}  // namespace

int main() {
    // Une promesse sans attente est terminée dès l'appel.
    {
        auto p = immediate(5);
        CHECK(p.done());
        CHECK(p.state() == task_state::completed);
        CHECK(block_on(p) == 5);
    }

    // Une attente suspend la tâche : elle n'est pas terminée à l'appel, et l'est après la boucle.
    {
        auto p = add_later(1, 2, 20);
        CHECK(!p.done());
        CHECK(p.state() == task_state::suspended);
        CHECK(block_on(p) == 3);
        CHECK(p.state() == task_state::completed);
    }

    // Deux tâches qui dorment en même temps durent le temps de la plus longue, pas la somme.
    {
        const auto start = std::chrono::steady_clock::now();
        auto p = two_at_once(120);
        CHECK(block_on(p) == 33);
        const auto took = elapsed_ms(start);
        CHECK(took >= 115);
        CHECK(took < 220);
    }

    // Les tâches reprennent dans l'ordre des échéances.
    {
        journal.clear();
        auto a = note_after(60, 3);
        auto b = note_after(20, 1);
        auto c = note_after(40, 2);
        block_on(a);
        CHECK(b.done() && c.done());
        CHECK((journal == std::vector<int>{1, 2, 3}));
    }

    // Une chaîne de tâches qui s'attendent.
    {
        auto p = chain(50);
        CHECK(block_on(p) == 50);
    }

    // Une copie de la promesse partage la tâche ; une promesse terminée se relit.
    {
        auto original = add_later(4, 5, 10);
        auto copy = original;
        CHECK(&original != &copy);
        auto p = twice(original);
        CHECK(block_on(p) == 909);
        CHECK(copy.done() && copy.value() == 9);
    }

    // Une tâche que personne n'attend est attendue par block_on avant la fin.
    {
        detached_ran = false;
        auto p = starts_detached();
        CHECK(p.done() && p.value() == 7);
        CHECK(!detached_ran);
        block_on(p);
        CHECK(detached_ran);
    }

    // Le code de sortie : la valeur de l'emit, ou 1 avec une erreur.
    CHECK(run_main(exit_ok()) == 42u);
    CHECK(run_main(exit_err()) == 1u);
    CHECK(run_main(exit_void_ok()) == 0u);
    CHECK(run_main(exit_void_err()) == 1u);

    // Annuler une tâche qui attend : elle s'arrête tout de suite, sans finir son attente.
    {
        slept_through = false;
        const auto start = std::chrono::steady_clock::now();
        auto p = sleeper(5000);
        CHECK(p.state() == task_state::suspended);
        cancel(p);
        CHECK(p.state() == task_state::cancelled);
        CHECK(p.done());
        CHECK(!p.value().is_ok() && p.value().error().rgl_f_kind == 0);
        block_on(p);   // plus aucune tâche : rend la main sans attendre les 5 secondes
        CHECK(elapsed_ms(start) < 1000);
        CHECK(!slept_through);
    }

    // Celui qui attendait la tâche annulée reprend avec son erreur.
    {
        cancelled_seen = false;
        auto p = sleeper(5000);
        auto w = watches(p);
        CHECK(w.state() == task_state::suspended);
        cancel(p);
        block_on(w);
        CHECK(cancelled_seen);
    }

    // L'annulation suit ce que la tâche attend : le sommeil du dessous est annulé aussi.
    {
        slept_through = false;
        const auto start = std::chrono::steady_clock::now();
        auto outer = parent_of_sleeper();
        cancel(outer);
        CHECK(outer.state() == task_state::cancelled);
        block_on(outer);
        CHECK(elapsed_ms(start) < 1000);
        CHECK(!slept_through);
    }

    // Une tâche qui s'exécute est annulée à son prochain point de suspension : rien de ce qui suit ne s'exécute.
    {
        reached_after = false;
        g_victim = victim();
        block_on(g_victim);
        CHECK(g_victim.state() == task_state::cancelled);
        CHECK(!reached_after);
    }

    // Annuler une tâche finie ne change rien.
    {
        auto p = finished_ok();
        cancel(p);
        CHECK(p.state() == task_state::completed);
        CHECK(p.value().is_ok() && p.value().value() == 9u);
    }

    // Un travail bloquant ne bloque pas la boucle : un minuteur plus court passe avant, et deux travaux durent le temps d'un seul.
    {
        journal.clear();
        const auto start = std::chrono::steady_clock::now();
        auto p = blocks(120, 7u);
        auto quick = note_after(30, 1);
        CHECK(p.state() == task_state::suspended);
        CHECK(block_on(p).value() == 7u);
        CHECK(quick.done() && journal == std::vector<int>{1});
        CHECK(elapsed_ms(start) >= 115);
    }
    {
        const auto start = std::chrono::steady_clock::now();
        auto p = blocks_twice();
        CHECK(block_on(p).value() == 12u);
        CHECK(elapsed_ms(start) < 280);
    }

    // Annuler une tâche qui attend le pool : elle s'arrête, son travail va à son terme sans la reprendre.
    {
        auto p = blocks(60, 3u);
        cancel(p);
        CHECK(p.state() == task_state::cancelled);
        block_on(p);   // attend la fin du travail, sans toucher à la coroutine détruite
        CHECK(!p.value().is_ok());
    }

    // Les travaux du même fichier gardent leur ordre, même quand le premier est plus long ; ceux d'un autre fichier passent en parallèle.
    {
        std::mutex guard;
        std::vector<int> order;
        const auto record = [&](int what) {
            std::lock_guard lock(guard);
            order.push_back(what);
        };
        const rgl::standard::string a("dossier/a.txt", 13);
        const rgl::standard::string same("dossier\\.\\sous\\..\\a.txt", 23);   // le même fichier, écrit autrement
        const rgl::standard::string other("dossier/b.txt", 13);
        CHECK(rgl::standard::path_key(a) == rgl::standard::path_key(same));
        CHECK(rgl::standard::path_key(a) != rgl::standard::path_key(other));

        const auto start = std::chrono::steady_clock::now();
        auto first = rgl::standard::run_file<outcome>(a, [&](const rgl::standard::string&) {
            std::this_thread::sleep_for(std::chrono::milliseconds(120));
            record(1);
            return outcome::ok(1u);
        });
        auto second = rgl::standard::run_file<outcome>(same, [&](const rgl::standard::string&) {
            record(2);
            return outcome::ok(2u);
        });
        auto third = rgl::standard::run_file<outcome>(other, [&](const rgl::standard::string&) {
            record(3);
            return outcome::ok(3u);
        });
        block_on(second);
        block_on(first);
        block_on(third);
        CHECK((order == std::vector<int>{3, 1, 2}));   // le troisième, d'un autre fichier, n'attend personne ; le second attend le premier
        CHECK(elapsed_ms(start) >= 115);
    }

    // Le pool à vol de travail : tous les travaux s'exécutent, sur plusieurs threads, y compris ceux soumis depuis un travail.
    {
        std::atomic<int> ran{0};
        std::mutex lock;
        std::set<std::thread::id> threads;
        {
            rgl::standard::async_detail::work_pool pool(4);
            CHECK(pool.thread_count() == 4);
            for (int i = 0; i < 64; ++i) {
                pool.submit([&] {
                    std::this_thread::sleep_for(std::chrono::milliseconds(i % 7 == 0 ? 15 : 2));   // des durées inégales : de quoi voler
                    {
                        std::lock_guard guard(lock);
                        threads.insert(std::this_thread::get_id());
                    }
                    ++ran;
                });
            }
            pool.submit([&] {
                pool.submit([&] { ++ran; });   // soumis depuis un thread du pool
                ++ran;
            });
        }   // le destructeur laisse finir ce qui est en file, puis joint
        CHECK(ran == 66);
        CHECK(threads.size() >= 2);
    }

    if (failures == 0) std::puts("async : tout passe");
    return failures == 0 ? 0 : 1;
}

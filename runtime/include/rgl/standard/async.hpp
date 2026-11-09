#pragma once

// L'asynchrone coopératif de RGL, en coroutines C++20 sur un seul thread. Tout est dans l'en-tête : un programme sans gear
// @async ne l'inclut pas, et rien ne s'ajoute à son exécutable.
//
// - promise<R> est la poignée d'une tâche : une copie partage la même tâche. R est ce que `await` donne : le résultat
//   `T ! E` du gear, ou `nullable<E>` quand il ne renvoie rien (null veut dire que tout s'est bien passé).
// - Un gear @async est une coroutine qui démarre tout de suite, comme un appel ordinaire, et rend sa promesse ; elle
//   ne rend la main qu'à son premier `co_await` qui doit attendre.
// - La boucle d'événements (event_loop) reprend les tâches dont une attente est finie : celles qui dorment, puis celles qu'une
//   annulation a libérées. block_on la fait tourner jusqu'à ce que la promesse soit terminée et qu'il ne reste plus aucune tâche :
//   le programme attend aussi les tâches que personne n'attend.
// - cancel(promesse) est coopératif : la tâche s'arrête à son prochain point de suspension (tout de suite si elle attend), sans
//   exécuter une instruction de plus ; ce qu'elle attendait est annulé aussi, et celui qui l'attend reçoit `err(Cancelled)`.
// - Un appel bloquant (fichier, processus) ou un gear @task se confie au pool de threads à vol de travail du runtime (run_blocking) : seul le travail s'y
//   exécute, le code RGL asynchrone reste sur le thread de la boucle, qui reprend la tâche quand le travail est fini. Une tâche annulée n'est pas reprise ;
//   son travail va à son terme. Le travail ne touche qu'à ses copies : il n'attend, ne lance et ne bloque jamais sur la boucle.
// - Plusieurs tâches peuvent attendre la même promesse ; when_any rend le résultat de la première qui finit, les autres continuent.
// - Une promesse que plus personne ne tient ne détruit pas sa tâche : le cadre de la coroutine se détruit à sa fin.
// - Rien ne lève d'exception : un blocage (rien ne peut plus reprendre une tâche en attente) arrête le programme.

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <coroutine>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <queue>
#include <string>
#include <unordered_map>
#include <thread>
#include <type_traits>
#include <unordered_set>
#include <utility>
#include <vector>

#include "rgl/standard/duration.hpp"
#include "rgl/standard/nullable.hpp"
#include "rgl/standard/string.hpp"
#include "rgl/standard/union.hpp"

namespace rgl::standard {

// Où en est une tâche : Ready (créée, pas démarrée), Running, Suspended (elle attend), Completed, Cancelled.
// Cet ordre est celui de l'enum TaskState de la std.
enum class task_state : std::uint8_t { ready, running, suspended, completed, cancelled };

namespace async_detail {

[[noreturn]] inline void failure(const char* message) noexcept {
    std::fprintf(stderr, "rgl : %s\n", message);
    std::abort();
}

// Le pool de threads à vol de travail : chaque thread a sa file de travaux. Un thread prend d'abord les siens, par le dernier arrivé (le plus chaud en cache) ;
// sans travail, il vole le plus ancien de la file d'un autre. Un travail soumis depuis un thread du pool va dans la file de ce thread, un travail venu de la boucle
// d'événements est réparti en tournant. Il y a au moins deux threads, un par cœur sinon. Un travail ne touche à rien que ses propres copies.
class work_pool {
   public:
    explicit work_pool(unsigned count = 0) {
        if (count == 0) count = std::max(2u, std::thread::hardware_concurrency());
        count_ = count;
        queues_ = std::make_unique<queue[]>(count);
        for (unsigned i = 0; i < count; ++i) threads_.emplace_back([this, i] { _run(i); });
    }

    ~work_pool() {
        {
            std::lock_guard lock(wake_mutex_);
            stopping_ = true;
        }
        wake_.notify_all();
        for (auto& thread : threads_) thread.join();
    }

    work_pool(const work_pool&) = delete;
    work_pool& operator=(const work_pool&) = delete;

    unsigned thread_count() const noexcept { return count_; }

    void submit(std::function<void()> job) {
        const std::size_t target = current_ != no_thread ? current_ : next_++ % count_;
        {
            std::lock_guard lock(queues_[target].mutex);
            queues_[target].jobs.push_back(std::move(job));
        }
        queued_.fetch_add(1);
        {
            // Le verrou ferme la fenêtre où un thread vient de voir « rien à faire » et pas encore dormi.
            std::lock_guard lock(wake_mutex_);
        }
        wake_.notify_one();
    }

   private:
    struct queue {
        std::mutex mutex;
        std::deque<std::function<void()>> jobs;
    };

    static constexpr std::size_t no_thread = static_cast<std::size_t>(-1);
    inline static thread_local std::size_t current_ = no_thread;   // le numéro du thread du pool qui s'exécute ici

    bool _take(std::size_t self, std::function<void()>& job) {
        {
            std::lock_guard lock(queues_[self].mutex);
            if (!queues_[self].jobs.empty()) {
                job = std::move(queues_[self].jobs.back());
                queues_[self].jobs.pop_back();
                queued_.fetch_sub(1);
                return true;
            }
        }
        for (std::size_t step = 1; step < count_; ++step) {
            queue& victim = queues_[(self + step) % count_];
            std::lock_guard lock(victim.mutex);
            if (victim.jobs.empty()) continue;
            job = std::move(victim.jobs.front());
            victim.jobs.pop_front();
            queued_.fetch_sub(1);
            return true;
        }
        return false;
    }

    void _run(std::size_t self) {
        current_ = self;
        for (;;) {
            std::function<void()> job;
            if (_take(self, job)) {
                job();
                continue;
            }
            std::unique_lock lock(wake_mutex_);
            wake_.wait(lock, [this] { return stopping_ || queued_.load() > 0; });
            if (stopping_ && queued_.load() == 0) return;
        }
    }

    unsigned count_ = 0;
    std::unique_ptr<queue[]> queues_;
    std::vector<std::thread> threads_;
    std::atomic<std::size_t> queued_{0};
    std::atomic<std::size_t> next_{0};
    std::mutex wake_mutex_;
    std::condition_variable wake_;
    bool stopping_ = false;
};

// La boucle d'événements : un seul thread, des tâches prêtes à reprendre, des minuteurs ordonnés par échéance puis par ordre de demande.
class event_loop {
   public:
    using clock = std::chrono::steady_clock;

    static event_loop& current() noexcept {
        static event_loop loop;
        return loop;
    }

    // Reprendra la coroutine à l'échéance donnée ; rend le numéro du minuteur, pour l'annuler.
    std::uint64_t add_timer(clock::time_point at, std::coroutine_handle<> handle) {
        const std::uint64_t order = next_++;
        timers_.push(timer{at, order, handle});
        return order;
    }

    // Le minuteur ne reprendra plus personne : sa coroutine n'existe plus.
    void cancel_timer(std::uint64_t order) { dead_.insert(order); }

    // Reprendra la coroutine au prochain tour de la boucle, avant les minuteurs.
    void post(std::coroutine_handle<> handle) { ready_.push(handle); }

    // Fait exécuter `work` par un thread du pool, hors de la boucle ; quand il a fini, la boucle reprend `handle` — sauf si `cancelled` est
    // devenu vrai entre-temps (la coroutine n'existe plus).
    //
    // Avec une clé (le chemin d'un fichier), les travaux de même clé s'exécutent un par un, dans l'ordre où ils ont été soumis ; ceux de clés différentes
    // restent en parallèle.
    void submit(std::function<void()> work, std::coroutine_handle<> handle, std::shared_ptr<std::atomic<bool>> cancelled, std::string key = {}) {
        ++pending_;
        if (!key.empty()) {
            const auto active = keyed_.find(key);
            if (active != keyed_.end()) {
                active->second.push_back(waiting{std::move(work), handle, std::move(cancelled)});
                return;
            }
            keyed_[key];   // cette clé a maintenant un travail en cours
        }
        _dispatch(std::move(work), handle, std::move(cancelled), std::move(key));
    }

    // Le nombre de threads du pool de travail (le pool n'existe qu'une fois un travail soumis).
    unsigned pool_threads() const noexcept { return pool_ ? pool_->thread_count() : 0; }

    // Nombre de travaux confiés au pool dont la boucle n'a pas encore repris le résultat.
    std::size_t pending() const noexcept { return pending_; }

    // Nombre de tâches commencées et pas finies.
    std::size_t live() const noexcept { return live_; }
    void started() noexcept { ++live_; }
    void finished() noexcept { --live_; }

    ~event_loop() { pool_.reset(); }

    // Reprend une tâche prête, puis un travail du pool terminé, sinon attend l'échéance la plus proche ou la fin d'un travail et reprend la coroutine
    // concernée. Faux s'il n'y a rien à reprendre ni à attendre.
    bool step() {
        if (!ready_.empty()) {
            const std::coroutine_handle<> handle = ready_.front();
            ready_.pop();
            handle.resume();
            return true;
        }
        for (;;) {
            while (!timers_.empty() && dead_.erase(timers_.top().order) != 0) timers_.pop();
            std::unique_lock lock(mutex_);
            if (!done_.empty()) {
                finished_job finished = std::move(done_.front());
                done_.pop();
                lock.unlock();
                --pending_;
                _release(finished.key);
                if (!finished.cancelled->load()) finished.handle.resume();
                return true;
            }
            if (timers_.empty()) {
                if (pending_ == 0) return false;
                loop_wake_.wait(lock);
                continue;
            }
            const auto at = timers_.top().at;
            if (clock::now() < at) {
                if (pending_ == 0) {
                    lock.unlock();
                    std::this_thread::sleep_for(at - clock::now());
                } else {
                    loop_wake_.wait_until(lock, at);
                }
                continue;
            }
            lock.unlock();
            const timer next = timers_.top();
            timers_.pop();
            next.handle.resume();
            return true;
        }
    }

   private:
    struct timer {
        clock::time_point at;
        std::uint64_t order;
        std::coroutine_handle<> handle;

        friend bool operator>(const timer& left, const timer& right) noexcept { return left.at != right.at ? left.at > right.at : left.order > right.order; }
    };

    struct finished_job {
        std::coroutine_handle<> handle;
        std::shared_ptr<std::atomic<bool>> cancelled;
        std::string key;
    };
    struct waiting {
        std::function<void()> work;
        std::coroutine_handle<> handle;
        std::shared_ptr<std::atomic<bool>> cancelled;
    };

    // Envoie le travail au pool ; quand il est fini, la boucle le saura par done_.
    void _dispatch(std::function<void()> work, std::coroutine_handle<> handle, std::shared_ptr<std::atomic<bool>> cancelled, std::string key) {
        if (!pool_) pool_ = std::make_unique<work_pool>();
        pool_->submit([this, work = std::move(work), handle, cancelled = std::move(cancelled), key = std::move(key)]() mutable {
            work();
            {
                std::lock_guard lock(mutex_);
                done_.push(finished_job{handle, std::move(cancelled), std::move(key)});
            }
            loop_wake_.notify_one();
        });
    }

    // Le travail de cette clé est fini : le suivant en attente part, sinon la clé est libre.
    void _release(const std::string& key) {
        if (key.empty()) return;
        const auto active = keyed_.find(key);
        if (active == keyed_.end()) return;
        if (active->second.empty()) {
            keyed_.erase(active);
            return;
        }
        waiting next = std::move(active->second.front());
        active->second.pop_front();
        _dispatch(std::move(next.work), next.handle, std::move(next.cancelled), key);
    }

    std::priority_queue<timer, std::vector<timer>, std::greater<timer>> timers_;
    std::mutex mutex_;                         // protège done_
    std::condition_variable loop_wake_;        // un travail fini pour la boucle
    std::queue<finished_job> done_;
    std::unordered_map<std::string, std::deque<waiting>> keyed_;   // clé -> travaux qui attendent celui en cours (boucle seulement)
    std::size_t pending_ = 0;
    std::unique_ptr<work_pool> pool_;          // en dernier : détruit (donc joint) avant le reste
    std::queue<std::coroutine_handle<>> ready_;
    std::unordered_set<std::uint64_t> dead_;
    std::uint64_t next_ = 0;
    std::size_t live_ = 0;
};

// Attend une durée sans bloquer le thread : la coroutine reprend quand la boucle atteint l'échéance.
struct sleep_awaiter {
    std::int64_t milliseconds;
    mutable std::uint64_t timer = 0;   // le minuteur demandé, pour l'annuler

    bool await_ready() const noexcept { return milliseconds <= 0; }
    void await_suspend(std::coroutine_handle<> handle) const {
        timer = event_loop::current().add_timer(event_loop::clock::now() + std::chrono::milliseconds(milliseconds), handle);
    }
    void await_resume() const noexcept {}
};

// Attend le résultat d'un travail bloquant exécuté par le pool : la coroutine reprend, sur le thread de la boucle, quand il est fini.
template <class R, class F>
struct blocking_awaiter {
    F work;
    std::string key;   // vide : aucun ordre à garder avec d'autres travaux
    std::shared_ptr<std::optional<R>> slot = std::make_shared<std::optional<R>>();
    std::shared_ptr<std::atomic<bool>> cancelled = std::make_shared<std::atomic<bool>>(false);

    bool await_ready() const noexcept { return false; }
    void await_suspend(std::coroutine_handle<> handle) {
        event_loop::current().submit([slot = slot, work = work] { slot->emplace(work()); }, handle, cancelled, key);
    }
    R await_resume() { return std::move(**slot); }
    std::shared_ptr<std::atomic<bool>> token() const { return cancelled; }
};

// Ce que toute tâche partage avec ses poignées, quel que soit son type de résultat. Un seul thread le touche : pas d'atomique.
struct state_base {
    std::size_t references = 0;
    task_state phase = task_state::ready;
    std::coroutine_handle<> waiter;   // celle qui attend la fin de la tâche
    std::vector<std::coroutine_handle<>> more_waiters;   // les autres, quand plusieurs tâches attendent la même promesse
    std::coroutine_handle<> frame;    // le cadre de la coroutine
    bool cancelling = false;          // une annulation est demandée, la tâche s'arrêtera à sa prochaine suspension
    state_base* child = nullptr;      // la tâche que celle-ci attend en ce moment
    std::uint64_t timer = 0;          // le minuteur qui la reprendra
    bool timed = false;
    std::shared_ptr<std::atomic<bool>> token;   // le travail du pool qu'elle attend : annulé, il ne la reprendra pas
    void (*store_cancelled)(state_base*) = nullptr;   // range le résultat « annulée »
};

// Le résultat d'une tâche annulée : l'erreur dont le genre est Cancelled (la première variante de AsyncKind), de type E, sans connaître le type généré.
template <class E>
E cancelled_error() {
    E error{};
    if constexpr (requires { error.rgl_f_message = string("", 0); }) error.rgl_f_message = string("tâche annulée", sizeof("tâche annulée") - 1);
    if constexpr (requires { error.rgl_f_kind; }) error.rgl_f_kind = static_cast<decltype(error.rgl_f_kind)>(0);
    return error;
}

template <class R>
struct error_of;
template <class T, class E>
struct error_of<result<T, E>> {
    using type = E;
};
template <class E>
struct error_of<std::optional<E>> {
    using type = E;
};

template <class R>
R make_cancelled() {
    if constexpr (requires { typename error_of<R>::type; }) {
        using error = typename error_of<R>::type;
        if constexpr (requires { R::err(cancelled_error<error>()); }) return R::err(cancelled_error<error>());
        else return R(cancelled_error<error>());
    } else {
        // Une promesse dont le résultat n'est pas `T ! E` ni `E?` : le code généré n'en écrit pas.
        failure("annulation d'une tâche dont le résultat ne peut pas porter d'erreur");
    }
}

// L'état d'une tâche dont `await` donne R.
template <class R>
struct state : state_base {
    std::optional<R> value;

    state() {
        store_cancelled = [](state_base* base) { static_cast<state*>(base)->value.emplace(make_cancelled<R>()); };
    }
};

template <class R>
void release(state<R>* shared) noexcept {
    if (--shared->references == 0) delete shared;
}

// La tâche cesse d'attendre ce qu'elle attendait, range son résultat « annulée » et rend celle qui l'attend (ou vide). Ce qu'elle attendait est annulé aussi.
inline void request_cancel(state_base* task);

inline std::coroutine_handle<> abandon(state_base* task) {
    state_base* const awaited = std::exchange(task->child, nullptr);
    if (awaited != nullptr) {
        // Elle n'attend plus : une autre attendeuse de la même promesse prend sa place de première.
        if (awaited->waiter == task->frame) {
            awaited->waiter = {};
            if (!awaited->more_waiters.empty()) {
                awaited->waiter = awaited->more_waiters.back();
                awaited->more_waiters.pop_back();
            }
        } else {
            auto& more = awaited->more_waiters;
            for (auto it = more.begin(); it != more.end(); ++it) {
                if (*it == task->frame) {
                    more.erase(it);
                    break;
                }
            }
        }
    }
    if (task->timed) {
        event_loop::current().cancel_timer(task->timer);
        task->timed = false;
    }
    if (task->token) {
        task->token->store(true);
        task->token.reset();
    }
    task->store_cancelled(task);
    task->phase = task_state::cancelled;
    const std::coroutine_handle<> waiter = std::exchange(task->waiter, {});
    for (const std::coroutine_handle<> other : std::exchange(task->more_waiters, {})) event_loop::current().post(other);
    request_cancel(awaited);
    return waiter;
}

// Demande l'annulation : une tâche qui attend s'arrête tout de suite (celle qui l'attend reprend au prochain tour de la boucle), une tâche qui s'exécute
// s'arrêtera à sa prochaine suspension.
inline void request_cancel(state_base* task) {
    if (task == nullptr || task->phase == task_state::completed || task->phase == task_state::cancelled) return;
    if (task->phase != task_state::suspended) {
        task->cancelling = true;
        return;
    }
    const std::coroutine_handle<> frame = task->frame;
    const std::coroutine_handle<> waiter = abandon(task);
    if (waiter) event_loop::current().post(waiter);
    frame.destroy();   // task peut disparaître ici : plus rien de lui ensuite
}

}  // namespace async_detail

template <class R>
class promise {
   public:
    struct promise_type;

    promise() noexcept = default;
    promise(const promise& other) noexcept : state_(other.state_) {
        if (state_) ++state_->references;
    }
    promise(promise&& other) noexcept : state_(std::exchange(other.state_, nullptr)) {}
    promise& operator=(promise other) noexcept {
        std::swap(state_, other.state_);
        return *this;
    }
    ~promise() {
        if (state_) async_detail::release(state_);
    }

    // Où en est la tâche.
    task_state state() const noexcept { return state_->phase; }

    // Vrai une fois la tâche terminée ou annulée : value() est alors lisible.
    bool done() const noexcept { return state_->phase == task_state::completed || state_->phase == task_state::cancelled; }

    // Le résultat d'une tâche terminée.
    const R& value() const noexcept { return *state_->value; }

    // La tâche, pour l'annuler ou la suivre.
    async_detail::state_base* task() const noexcept { return state_; }

    // co_await sur une promesse : tout de suite si elle est finie, sinon la coroutine reprend à sa fin. Plusieurs tâches peuvent l'attendre : la première
    // reprend tout de suite à la fin de la tâche, les autres au tour suivant de la boucle.
    bool await_ready() const noexcept { return done(); }
    void await_suspend(std::coroutine_handle<> waiter) const {
        if (!state_->waiter) state_->waiter = waiter;
        else state_->more_waiters.push_back(waiter);
    }
    R await_resume() const { return *state_->value; }

   private:
    explicit promise(async_detail::state<R>* shared) noexcept : state_(shared) { ++shared->references; }

    async_detail::state<R>* state_ = nullptr;
};

template <class R>
struct promise<R>::promise_type {
    async_detail::state<R>* state_;

    promise_type() : state_(new async_detail::state<R>) {
        state_->references = 1;   // celle de la coroutine
        state_->frame = std::coroutine_handle<promise_type>::from_promise(*this);
        async_detail::event_loop::current().started();
    }
    ~promise_type() {
        async_detail::event_loop::current().finished();
        async_detail::release(state_);
    }

    promise get_return_object() { return promise(state_); }

    // La coroutine démarre tout de suite : appeler un gear async commence son travail.
    std::suspend_never initial_suspend() noexcept {
        state_->phase = task_state::running;
        return {};
    }

    // À la fin, la coroutine détruit son propre cadre puis reprend celle qui l'attend (s'il y en a une), sans empiler d'appels.
    struct final_awaiter {
        async_detail::state<R>* state;

        bool await_ready() const noexcept { return false; }
        std::coroutine_handle<> await_suspend(std::coroutine_handle<promise_type> self) noexcept {
            const std::coroutine_handle<> waiter = state->waiter;
            state->waiter = {};
            const auto others = std::move(state->more_waiters);
            self.destroy();   // ce final_awaiter vit dans le cadre : plus rien de lui ensuite
            for (const std::coroutine_handle<> other : others) async_detail::event_loop::current().post(other);
            return waiter ? waiter : std::noop_coroutine();
        }
        void await_resume() const noexcept {}
    };
    final_awaiter final_suspend() noexcept { return final_awaiter{state_}; }

    void return_value(R value) {
        state_->value.emplace(std::move(value));
        state_->phase = task_state::completed;
    }

    // Le code RGL ne lève jamais d'exception ; le runtime compile sans.
    void unhandled_exception() noexcept { std::abort(); }

    // Tout co_await de la coroutine passe par là, pour que la tâche soit Suspended pendant son attente et que l'annulation la retrouve.
    // Une annulation demandée pendant que la tâche s'exécutait la prend ici, à son prochain point de suspension.
    template <class A>
    struct tracked {
        A awaitable;
        async_detail::state<R>* state;

        bool await_ready() { return !state->cancelling && awaitable.await_ready(); }
        std::coroutine_handle<> await_suspend(std::coroutine_handle<promise_type> self) {
            if (state->cancelling) {
                const std::coroutine_handle<> waiter = async_detail::abandon(state);
                self.destroy();   // cet attendeur vit dans le cadre : plus rien de lui ensuite
                return waiter ? waiter : std::noop_coroutine();
            }
            state->phase = task_state::suspended;
            awaitable.await_suspend(self);
            if constexpr (requires { awaitable.task(); }) state->child = awaitable.task();
            if constexpr (requires { awaitable.token(); }) state->token = awaitable.token();
            if constexpr (requires { awaitable.timer; }) {
                state->timer = awaitable.timer;
                state->timed = true;
            }
            return std::noop_coroutine();
        }
        auto await_resume() {
            state->phase = task_state::running;
            state->child = nullptr;
            state->timed = false;
            state->token.reset();
            return awaitable.await_resume();
        }
    };

    template <class A>
    tracked<std::decay_t<A>> await_transform(A&& awaitable) {
        return tracked<std::decay_t<A>>{std::forward<A>(awaitable), state_};
    }
};

// cancel promesse; : demande l'annulation de la tâche. Sans effet sur une tâche finie.
template <class R>
void cancel(const promise<R>& awaited) {
    async_detail::request_cancel(awaited.task());
}

// sleep(durée) : une promesse qui se termine une fois la durée écoulée, sans bloquer le thread. R est ce que `await` donne d'une promesse sans valeur.
template <class R>
promise<R> async_sleep(duration time) {
    co_await async_detail::sleep_awaiter{time.milliseconds()};
    co_return R{};
}

// Une promesse dont le résultat vient de `work`, exécuté par un thread du pool pendant que la boucle continue : un appel bloquant (fichier, processus)
// sans bloquer les autres tâches. `work` ne touche à rien que ses propres copies.
template <class R, class F>
promise<R> run_blocking(F work) {
    co_return co_await async_detail::blocking_awaiter<R, F>{std::move(work), std::string()};
}

namespace async_detail {

// Le signal que when_any attend : il reprend sa coroutine une seule fois, à la première promesse finie, sauf si elle a été annulée entre-temps.
struct any_trigger {
    std::coroutine_handle<> handle;
    bool fired = false;
    std::shared_ptr<std::atomic<bool>> cancelled = std::make_shared<std::atomic<bool>>(false);
};

// Une coroutine sans promesse, qui démarre tout de suite et se détruit seule à sa fin.
struct watcher {
    struct promise_type {
        watcher get_return_object() noexcept { return {}; }
        std::suspend_never initial_suspend() noexcept { return {}; }
        std::suspend_never final_suspend() noexcept { return {}; }
        void return_void() noexcept {}
        void unhandled_exception() noexcept { std::abort(); }
    };
};

template <class P>
watcher watch(P awaited, std::shared_ptr<any_trigger> trigger) {
    co_await awaited;
    if (trigger->fired) co_return;
    trigger->fired = true;
    if (!trigger->cancelled->load()) event_loop::current().post(trigger->handle);
}

// Attend que l'une des promesses soit finie.
template <class R, class Promises>
struct any_awaiter {
    Promises promises;
    std::shared_ptr<any_trigger> trigger = std::make_shared<any_trigger>();

    bool await_ready() const {
        for (std::size_t i = 0; i < promises.size(); ++i) {
            if (promises[i].done()) return true;
        }
        return false;
    }
    void await_suspend(std::coroutine_handle<> handle) {
        trigger->handle = handle;
        for (std::size_t i = 0; i < promises.size(); ++i) watch(promises[i], trigger);
    }
    R await_resume() const {
        for (std::size_t i = 0; i < promises.size(); ++i) {
            if (promises[i].done()) return promises[i].value();
        }
        __builtin_unreachable();
    }
    std::shared_ptr<std::atomic<bool>> token() const { return trigger->cancelled; }
};

}  // namespace async_detail

// whenAny(promesses) : une promesse du résultat de la première qui finit ; les autres continuent. Un tableau vide n'a rien à attendre : le résultat est « annulée ».
template <class R, class Promises>
promise<R> when_any(Promises promises) {
    if (promises.size() == 0) co_return async_detail::make_cancelled<R>();
    co_return co_await async_detail::any_awaiter<R, Promises>{std::move(promises)};
}

// Dans un gear @sync : fait tourner la boucle jusqu'à la fin de cette promesse seulement, puis rend sa valeur. Les autres tâches avancent pendant l'attente ;
// l'appelant, lui, ne reprend qu'ensuite. Appelé depuis une coroutine, il bloque toutes les autres tâches de l'appelant, c'est la responsabilité du développeur.
template <class R>
R block_until(const promise<R>& awaited) {
    auto& loop = async_detail::event_loop::current();
    while (!awaited.done()) {
        if (!loop.step()) async_detail::failure("interblocage : rien ne peut plus terminer la promesse attendue");
    }
    return awaited.value();
}

// La clé d'un chemin de fichier : séparateurs unifiés, segments « . » et « .. » résolus, casse ignorée sous Windows. Deux écritures du même fichier ont la même
// clé, qu'elles écrivent « a/./b.txt » ou « a\\b.txt » ; un chemin relatif et un absolu ne se reconnaissent pas.
inline std::string path_key(const string& path) {
    std::string out;
    std::vector<std::string> parts;
    std::string current;
    const auto flush = [&] {
        if (current.empty() || current == ".") {
        } else if (current == ".." && !parts.empty() && parts.back() != "..") {
            parts.pop_back();
        } else {
            parts.push_back(current);
        }
        current.clear();
    };
    for (std::size_t i = 0; i < path.size(); ++i) {
        char c = path.data()[i];
        if (c == '/' || c == '\\') {
            flush();
            continue;
        }
#if defined(_WIN32)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
#endif
        current.push_back(c);
    }
    flush();
    if (path.size() > 0 && (path.data()[0] == '/' || path.data()[0] == '\\')) out = "/";
    for (const auto& part : parts) {
        out += part;
        out += '/';
    }
    return out.empty() ? std::string("/") : out;
}

// Un travail bloquant sur un fichier : les travaux du même chemin s'exécutent dans l'ordre où ils sont lancés (une lecture après une écriture voit l'écriture),
// ceux de fichiers différents en parallèle. `work` reçoit une copie du chemin.
template <class R, class F>
promise<R> run_file(const string& path, F work) {
    string copy = path;
    co_return co_await async_detail::blocking_awaiter<R, std::function<R()>>{[copy, work = std::move(work)] { return work(copy); }, path_key(path)};
}

// Fait tourner la boucle jusqu'à ce que la promesse soit terminée et qu'aucune tâche ne reste (celles que personne n'attend sont attendues aussi).
template <class R>
const R& block_on(const promise<R>& awaited) {
    auto& loop = async_detail::event_loop::current();
    while (!awaited.done() || loop.live() > 0 || loop.pending() > 0) {
        if (!loop.step()) async_detail::failure("interblocage : rien ne peut plus reprendre les tâches qui attendent");
    }
    return awaited.value();
}

namespace async_detail {

// Une erreur qui termine le programme : son message et l'endroit, comme tout arrêt du runtime.
template <class E>
void report(const E& error) noexcept {
    const auto& message = error.rgl_f_message;
    const auto& where = error.rgl_f_where;
    std::fprintf(stderr, "rgl : %.*s (%.*s)\n", static_cast<int>(message.size()), message.data(), static_cast<int>(where.size()), where.data());
}

}  // namespace async_detail

// L'emit async qui termine le train d'entrée : la boucle tourne jusqu'à sa fin, et sa valeur est le code de sortie ; une erreur arrête le programme (code 1).
template <class R>
std::uint32_t run_main(promise<R> entry) {
    const R& outcome = block_on(entry);
    if constexpr (requires { outcome.is_ok(); }) {
        if (outcome.is_ok()) return static_cast<std::uint32_t>(outcome.value());
        async_detail::report(outcome.error());
        return 1u;
    } else {
        // Une promesse sans valeur : null veut dire que tout s'est bien passé.
        if (!outcome.has_value()) return 0u;
        async_detail::report(*outcome);
        return 1u;
    }
}

}  // namespace rgl::standard

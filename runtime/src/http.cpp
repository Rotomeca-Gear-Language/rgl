// HTTP/1.1 concurrent (connexions persistantes), client HTTP et WebSocket, IPv4 et IPv6.

#include "rgl/standard/http.hpp"
#include "rgl/standard/http_detail.hpp"

#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>

namespace rgl::standard::http {

std::int32_t serve(const string& address, std::uint16_t port, std::uint32_t max_requests,
                   std::uint32_t workers, const handler& callback,
                   const websocket_acceptor& accept_websocket,
                   const websocket_factory& open_websocket) noexcept {
    detail::network_guard network;
    if (!network.ready) return detail::last_error();

    detail::socket_guard listener;
    if (const auto opened = detail::open_listener(std::string_view(address.data(), address.size()), port, listener.value); opened != 0) return opened;

    if (workers == 0) workers = std::max(2u, std::thread::hardware_concurrency());
    std::mutex mutex;
    std::condition_variable ready;
    std::deque<detail::socket_handle> clients;
    std::atomic<bool> stopping{false};
    std::int32_t failure = 0;

    auto work = [&] {
        for (;;) {
            detail::socket_guard client;
            {
                std::unique_lock lock(mutex);
                ready.wait(lock, [&] { return stopping.load() || !clients.empty(); });
                if (clients.empty()) return;
                client.value = clients.front();
                clients.pop_front();
            }
            detail::plain_io io{client.value};
            if (const auto error = detail::serve_connection(io, callback, accept_websocket, open_websocket, stopping); error != 0) {
                std::lock_guard lock(mutex);
                if (failure == 0) failure = error;
            }
        }
    };

    std::vector<std::thread> pool;
    pool.reserve(workers);
    for (std::uint32_t i = 0; i < workers; ++i) pool.emplace_back(work);

    std::uint32_t accepted = 0;
    while (max_requests == 0 || accepted < max_requests) {
        const detail::socket_handle client = accept(listener.value, nullptr, nullptr);
        if (client == detail::invalid_socket) {
            std::lock_guard lock(mutex);
            if (failure == 0) failure = detail::last_error();
            break;
        }
        {
            std::lock_guard lock(mutex);
            clients.push_back(client);
        }
        ++accepted;
        ready.notify_one();
    }
    {
        std::lock_guard lock(mutex);
        stopping.store(true);
    }
    ready.notify_all();
    for (auto& thread : pool) thread.join();
    return failure;
}

string exchange(const string& method, const string& url, const string& headers,
                const string& body, std::uint32_t timeout_ms) noexcept {
    detail::network_guard network;
    detail::parsed_url parsed;
    if (!network.ready || !detail::parse_url(std::string_view(url.data(), url.size()), "http://", "80", parsed)) return {};
    detail::socket_guard socket{detail::connect_to(parsed, timeout_ms)};
    if (socket.value == detail::invalid_socket) return {};
    std::string request;
    request.reserve(method.size() + parsed.target.size() + parsed.authority.size() + headers.size() + body.size() + 128);
    request.append(method.data(), method.size()).append(" ").append(parsed.target).append(" HTTP/1.1\r\nHost: ").append(parsed.authority)
        .append("\r\nConnection: close\r\n");
    if (!headers.empty()) request.append(headers.data(), headers.size()).append("\r\n");
    if (!body.empty()) request.append("Content-Length: ").append(std::to_string(body.size())).append("\r\n");
    request.append("\r\n").append(body.data(), body.size());
    detail::plain_io io{socket.value};
    if (!io.write(request)) return {};

    std::string response;
    char buffer[8192];
    for (;;) {
        const auto received = io.read(buffer, sizeof(buffer));
        if (received == 0) break;
        if (received < 0 || response.size() + static_cast<std::size_t>(received) > 4 * detail::max_request_size) return {};
        response.append(buffer, static_cast<std::size_t>(received));
    }
    detail::dechunk_response(response);
    return string(response.data(), response.size());
}

}  // namespace rgl::standard::http

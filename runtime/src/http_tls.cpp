// HTTPS synchrone concurrent (connexions persistantes), avec validation des certificats par OpenSSL.

#include "rgl/standard/http_tls.hpp"
#include "rgl/standard/http_detail.hpp"
#include "rgl/standard/http2.hpp"

#include <condition_variable>
#include <cerrno>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <thread>

#include <openssl/err.h>
#include <openssl/ssl.h>
#include <openssl/x509v3.h>

namespace rgl::standard::http {
namespace {

struct context_guard {
    SSL_CTX* value = nullptr;
    ~context_guard() { if (value) SSL_CTX_free(value); }
};
struct ssl_guard {
    SSL* value = nullptr;
    ~ssl_guard() { if (value) { SSL_shutdown(value); SSL_free(value); } }
};

// Entrées-sorties d'une connexion TLS ; le délai d'inactivité est celui du socket sous-jacent.
struct tls_io {
    SSL* ssl;
    detail::socket_handle socket;

    long long read(char* destination, std::size_t size) noexcept {
        return SSL_read(ssl, destination, static_cast<int>(std::min<std::size_t>(size, 0x7fffffff)));
    }
    bool write(std::string_view data) noexcept {
        std::size_t sent = 0;
        while (sent < data.size()) {
            const int count = SSL_write(ssl, data.data() + sent, static_cast<int>(std::min<std::size_t>(data.size() - sent, 0x7fffffff)));
            if (count <= 0) return false;
            sent += static_cast<std::size_t>(count);
        }
        return true;
    }
    void set_idle_timeout(std::uint32_t timeout_ms) noexcept { detail::set_receive_timeout(socket, timeout_ms); }
    std::int32_t read_failure() const noexcept { return -14; }
    std::int32_t write_failure() const noexcept { return -15; }
    std::int32_t websocket_failure() const noexcept { return -16; }
};

}  // namespace

std::int32_t serve_tls(const string& address, std::uint16_t port, const string& certificate,
                       const string& private_key, std::uint32_t max_requests,
                       std::uint32_t workers, const handler& callback,
                       const websocket_acceptor& accept_websocket,
                       const websocket_factory& open_websocket) noexcept {
    detail::network_guard network;
    if (!network.ready) return detail::last_error();
    context_guard context{SSL_CTX_new(TLS_server_method())};
    if (!context.value) return -10;
    const std::string cert(certificate.data(), certificate.size()), key(private_key.data(), private_key.size());
    if (SSL_CTX_use_certificate_chain_file(context.value, cert.c_str()) != 1) return -11;
    if (SSL_CTX_use_PrivateKey_file(context.value, key.c_str(), SSL_FILETYPE_PEM) != 1 || SSL_CTX_check_private_key(context.value) != 1) return -12;

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
            ssl_guard ssl{SSL_new(context.value)};
            std::int32_t error = 0;
            if (!ssl.value || SSL_set_fd(ssl.value, static_cast<int>(client.value)) != 1 || SSL_accept(ssl.value) != 1) error = -13;
            else {
                tls_io io{ssl.value, client.value};
                error = detail::serve_connection(io, callback, accept_websocket, open_websocket, stopping);
            }
            if (error != 0) {
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

namespace {

// Une connexion TLS brute : les membres se détruisent dans l'ordre inverse (la session, puis le contexte, puis la prise).
struct tls_session {
    detail::network_guard network;
    detail::socket_guard socket;
    context_guard context;
    ssl_guard ssl;
};

std::mutex& tls_mutex() {
    static std::mutex value;
    return value;
}
std::map<std::int64_t, std::shared_ptr<tls_session>>& tls_sessions() {
    static std::map<std::int64_t, std::shared_ptr<tls_session>> value;
    return value;
}
std::shared_ptr<tls_session> tls_find(std::int64_t id) {
    const std::lock_guard<std::mutex> lock(tls_mutex());
    const auto it = tls_sessions().find(id);
    return it == tls_sessions().end() ? nullptr : it->second;
}

}  // namespace

std::int64_t tls_connect(const string& host, std::uint16_t port, std::uint32_t timeout_ms) noexcept {
    auto session = std::make_shared<tls_session>();
    detail::parsed_url parsed;
    if (!session->network.ready) return -1;
    if (!detail::parse_url(std::string("https://") + std::string(host.data(), host.size()) + ":" + std::to_string(port) + "/", "https://", "443", parsed)) return -1;
    session->context.value = SSL_CTX_new(TLS_client_method());
    if (!session->context.value || SSL_CTX_set_default_verify_paths(session->context.value) != 1) return -1;
    SSL_CTX_set_verify(session->context.value, SSL_VERIFY_PEER, nullptr);
    session->socket.value = detail::connect_to(parsed, timeout_ms);
    if (session->socket.value == detail::invalid_socket) return -1;
    session->ssl.value = SSL_new(session->context.value);
    if (!session->ssl.value || SSL_set_fd(session->ssl.value, static_cast<int>(session->socket.value)) != 1) return -1;
    const bool is_address = parsed.host.find(':') != std::string::npos ||
        parsed.host.find_first_not_of("0123456789.") == std::string::npos;
    if (is_address) {
        if (X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(session->ssl.value), parsed.host.c_str()) != 1) return -1;
    } else if (SSL_set_tlsext_host_name(session->ssl.value, parsed.host.c_str()) != 1 || SSL_set1_host(session->ssl.value, parsed.host.c_str()) != 1) {
        return -1;
    }
    detail::set_timeouts(session->socket.value, timeout_ms);
    if (SSL_connect(session->ssl.value) != 1 || SSL_get_verify_result(session->ssl.value) != X509_V_OK) return -1;
    detail::set_receive_timeout(session->socket.value, 0);
    static std::int64_t next = 1;
    const std::lock_guard<std::mutex> lock(tls_mutex());
    const std::int64_t id = next++;
    tls_sessions()[id] = std::move(session);
    return id;
}

std::int64_t tls_send(std::int64_t id, const std::uint8_t* data, std::size_t size) noexcept {
    const auto session = tls_find(id);
    if (!session) return -1;
    tls_io io{session->ssl.value, session->socket.value};
    return io.write(std::string_view(reinterpret_cast<const char*>(data), size)) ? static_cast<std::int64_t>(size) : -1;
}

std::int64_t tls_receive(std::int64_t id, std::uint8_t* buffer, std::size_t size, std::uint32_t timeout_ms) noexcept {
    const auto session = tls_find(id);
    if (!session || size == 0) return -1;
    detail::set_receive_timeout(session->socket.value, timeout_ms);
    ERR_clear_error();
    const int count = SSL_read(session->ssl.value, buffer, static_cast<int>(std::min<std::size_t>(size, 0x7fffffff)));
    if (count > 0) return count;
    const int error = SSL_get_error(session->ssl.value, count);
    if (error == SSL_ERROR_ZERO_RETURN) return 0;
    if (error == SSL_ERROR_WANT_READ || error == SSL_ERROR_WANT_WRITE) return tls_timeout;
    if (error == SSL_ERROR_SYSCALL) {
#if defined(_WIN32)
        if (WSAGetLastError() == WSAETIMEDOUT) return tls_timeout;
#else
        if (errno == EAGAIN || errno == EWOULDBLOCK) return tls_timeout;
#endif
        if (count == 0) return 0;  // fermeture sans close_notify
    }
    return -1;
}

void tls_close(std::int64_t id) noexcept {
    std::shared_ptr<tls_session> session;
    {
        const std::lock_guard<std::mutex> lock(tls_mutex());
        const auto it = tls_sessions().find(id);
        if (it == tls_sessions().end()) return;
        session = std::move(it->second);
        tls_sessions().erase(it);
    }
}

string exchange_tls(const string& method, const string& url, const string& headers,
                    const string& body, std::uint32_t timeout_ms) noexcept {
    detail::network_guard network;
    detail::parsed_url parsed;
    if (!network.ready || !detail::parse_url(std::string_view(url.data(), url.size()), "https://", "443", parsed)) return {};
    context_guard context{SSL_CTX_new(TLS_client_method())};
    if (!context.value || SSL_CTX_set_default_verify_paths(context.value) != 1) return {};
    SSL_CTX_set_verify(context.value, SSL_VERIFY_PEER, nullptr);
    detail::socket_guard socket{detail::connect_to(parsed, timeout_ms)};
    if (socket.value == detail::invalid_socket) return {};
    ssl_guard ssl{SSL_new(context.value)};
    if (!ssl.value || SSL_set_fd(ssl.value, static_cast<int>(socket.value)) != 1) return {};
    // Une adresse IP littérale se vérifie contre l'adresse du certificat ; un nom, contre son nom d'hôte (et sert de SNI).
    const bool is_address = parsed.host.find(':') != std::string::npos ||
        parsed.host.find_first_not_of("0123456789.") == std::string::npos;
    if (is_address) {
        if (X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(ssl.value), parsed.host.c_str()) != 1) return {};
    } else if (SSL_set_tlsext_host_name(ssl.value, parsed.host.c_str()) != 1 || SSL_set1_host(ssl.value, parsed.host.c_str()) != 1) {
        return {};
    }
    // Le client propose HTTP/2 puis HTTP/1.1 ; ce que le serveur choisit décide de la suite.
    static const unsigned char protocols[] = {2, 'h', '2', 8, 'h', 't', 't', 'p', '/', '1', '.', '1'};
    if (SSL_set_alpn_protos(ssl.value, protocols, sizeof(protocols)) != 0) return {};
    if (SSL_connect(ssl.value) != 1 || SSL_get_verify_result(ssl.value) != X509_V_OK) return {};
    const unsigned char* selected = nullptr;
    unsigned int selected_size = 0;
    SSL_get0_alpn_selected(ssl.value, &selected, &selected_size);
    if (selected_size == 2 && selected[0] == 'h' && selected[1] == '2') {
        tls_io io{ssl.value, socket.value};
        std::string reply;
        if (!h2::exchange(io, std::string_view(method.data(), method.size()), "https", parsed.authority, parsed.target,
                          h2::request_headers(std::string_view(headers.data(), headers.size())),
                          std::string_view(body.data(), body.size()), detail::max_request_size, reply))
            return {};
        return string(reply.data(), reply.size());
    }
    std::string request;
    request.append(method.data(), method.size()).append(" ").append(parsed.target).append(" HTTP/1.1\r\nHost: ").append(parsed.authority).append("\r\nConnection: close\r\n");
    if (!headers.empty()) request.append(headers.data(), headers.size()).append("\r\n");
    if (!body.empty()) request.append("Content-Length: ").append(std::to_string(body.size())).append("\r\n");
    request.append("\r\n").append(body.data(), body.size());
    tls_io io{ssl.value, socket.value};
    if (!io.write(request)) return {};
    std::string response;
    char buffer[8192];
    for (;;) {
        const auto received = io.read(buffer, sizeof(buffer));
        if (received <= 0) break;
        if (response.size() + static_cast<std::size_t>(received) > detail::max_request_size) return {};
        response.append(buffer, static_cast<std::size_t>(received));
    }
    detail::dechunk_response(response);
    return string(response.data(), response.size());
}

}  // namespace rgl::standard::http

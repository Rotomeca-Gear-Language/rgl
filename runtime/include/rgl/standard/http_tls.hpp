#pragma once

// Transport HTTPS fondé sur OpenSSL. Ce composant est lié uniquement lorsqu'un programme
// appelle listenTls ou utilise une URL https://.

#include <cstdint>

#include "http.hpp"

namespace rgl::standard::http {

std::int32_t serve_tls(const string& address, std::uint16_t port, const string& certificate,
                       const string& private_key, std::uint32_t max_requests,
                       std::uint32_t workers, const handler& callback,
                       const websocket_acceptor& accept_websocket,
                       const websocket_factory& open_websocket) noexcept;

string exchange_tls(const string& method, const string& url, const string& headers,
                    const string& body, std::uint32_t timeout_ms) noexcept;

// Une connexion TLS brute, hors HTTP : le client y lit et écrit des octets (WebSocket `wss://`, protocoles maison). Le certificat du
// serveur et son nom sont vérifiés avec les autorités du système (ou `SSL_CERT_FILE`). Chaque connexion est désignée par un
// identifiant qui ne vaut que dans ce programme. Rien ne lève : un échec est une valeur rendue.
constexpr std::int64_t tls_timeout = -2;  // `tls_receive` : rien n'est arrivé dans le délai

// Ouvre la connexion et fait la poignée de main TLS ; l'identifiant (positif), ou -1.
std::int64_t tls_connect(const string& host, std::uint16_t port, std::uint32_t timeout_ms) noexcept;

// Envoie tous les octets ; le nombre envoyé, ou -1.
std::int64_t tls_send(std::int64_t id, const std::uint8_t* data, std::size_t size) noexcept;

// Lit au plus `size` octets, en attendant au plus `timeout_ms` millisecondes (0 : sans limite) : le nombre lu, 0 si le serveur a fermé
// la connexion, `tls_timeout` au bout du délai, -1 sur une erreur.
std::int64_t tls_receive(std::int64_t id, std::uint8_t* buffer, std::size_t size, std::uint32_t timeout_ms) noexcept;

// Ferme la connexion et oublie l'identifiant.
void tls_close(std::int64_t id) noexcept;

}  // namespace rgl::standard::http

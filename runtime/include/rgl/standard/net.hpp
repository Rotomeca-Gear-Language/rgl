#pragma once

// Sockets TCP et UDP bloquants, avec délai. Une prise est un entier (le descripteur du système) ; une valeur
// négative signale l'échec. Utilisé par std/Net.forge ; le serveur HTTP a son propre transport.

#include <cstddef>
#include <cstdint>
#include <string>

#include "rgl/standard/string.hpp"

namespace rgl::standard::net {

constexpr std::int64_t failure = -1;
constexpr std::int64_t timed_out = -2;

// Ouvre une connexion TCP ; timeout_ms == 0 attend sans limite.
std::int64_t tcp_connect(const string& host, std::uint16_t port, std::uint32_t timeout_ms) noexcept;

// Écoute en TCP sur une adresse IPv4 ou IPv6 (« :: » : les deux ; port 0 : le système en choisit un).
std::int64_t tcp_listen(const string& address, std::uint16_t port) noexcept;

// Accepte une connexion ; timed_out si le délai passe.
std::int64_t tcp_accept(std::int64_t listener, std::uint32_t timeout_ms) noexcept;

// Ouvre une prise UDP liée à une adresse et un port (port 0 : le système en choisit un).
std::int64_t udp_open(const string& address, std::uint16_t port) noexcept;

// Envoie des octets sur une prise TCP ; rend le nombre d'octets envoyés ou failure.
std::int64_t send(std::int64_t socket, const std::uint8_t* data, std::size_t size) noexcept;

// Envoie un datagramme UDP ; rend le nombre d'octets envoyés ou failure.
std::int64_t send_to(std::int64_t socket, const string& host, std::uint16_t port, const std::uint8_t* data, std::size_t size) noexcept;

// Reçoit au plus `size` octets : le nombre reçu, 0 si la connexion TCP est fermée, timed_out au délai, failure sinon.
// Pour une prise UDP, l'expéditeur est noté dans last_sender().
std::int64_t receive(std::int64_t socket, std::uint8_t* buffer, std::size_t size, std::uint32_t timeout_ms) noexcept;

// L'expéditeur du dernier datagramme reçu par ce thread, « hôte:port ».
const std::string& last_sender() noexcept;

// Le port local d'une prise, ou 0.
std::uint16_t local_port(std::int64_t socket) noexcept;

void close(std::int64_t socket) noexcept;

}  // namespace rgl::standard::net

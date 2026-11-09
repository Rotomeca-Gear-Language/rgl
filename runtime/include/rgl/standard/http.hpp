#pragma once

// Serveur HTTP/1.1 concurrent. Le transport reste dans le runtime ; le routage et la
// construction des réponses vivent dans std/Http.forge et rappellent du code RGL.

#include <cstdint>

#include "function.hpp"
#include "string.hpp"

namespace rgl::standard::http {

using handler = function<string(string, string, string, string)>;
using websocket_acceptor = function<bool(string, string)>;
using websocket_session_handler = function<string(string)>;
using websocket_factory = function<websocket_session_handler(string, string)>;

// Écoute une adresse IPv4 ou IPv6 (« :: » : les deux) ; chaque connexion garde son worker tant que
// le client la laisse ouverte (HTTP/1.1 persistant, fermée après un délai d'inactivité). max_requests == 0 signifie
// jusqu'à l'arrêt du processus. Le callback reçoit la méthode, la cible brute, les en-têtes
// bruts et le corps, puis rend la réponse HTTP/1.1 complète. 0 signifie succès ; une autre
// valeur est un code système dont le sens dépend de la plateforme.
std::int32_t serve(const string& address, std::uint16_t port, std::uint32_t max_requests,
                   std::uint32_t workers, const handler& callback,
                   const websocket_acceptor& accept_websocket,
                   const websocket_factory& open_websocket) noexcept;

// Exécute une requête HTTP cliente. Une chaîne vide signale une erreur de transport.
string exchange(const string& method, const string& url, const string& headers,
                const string& body, std::uint32_t timeout_ms) noexcept;

}  // namespace rgl::standard::http

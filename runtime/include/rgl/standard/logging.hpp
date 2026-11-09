#pragma once

// L'état du journal de la bibliothèque standard : le niveau minimal des messages écrits et un fichier où les
// recopier. Il vit ici, dans des fonctions inline, pour que le programme et ses composants natifs partagent le
// même état.

#include <atomic>
#include <cstdio>
#include <mutex>
#include <string>

namespace rgl::standard::logging {

inline std::atomic<int>& threshold() noexcept {
    static std::atomic<int> value{0};
    return value;
}

inline std::mutex& lock() noexcept {
    static std::mutex value;
    return value;
}

inline std::string& file_path() noexcept {
    static std::string value;
    return value;
}

// Choisit le fichier du journal (vide : aucun) ; faux s'il ne peut pas être ouvert en ajout.
inline bool set_file(const std::string& path) {
    const std::lock_guard guard(lock());
    if (!path.empty()) {
        std::FILE* file = std::fopen(path.c_str(), "ab");
        if (!file) return false;
        std::fclose(file);
    }
    file_path() = path;
    return true;
}

// Ajoute le texte au fichier du journal, s'il y en a un.
inline void append(const char* data, std::size_t size) {
    const std::lock_guard guard(lock());
    if (file_path().empty()) return;
    std::FILE* file = std::fopen(file_path().c_str(), "ab");
    if (!file) return;
    std::fwrite(data, 1, size, file);
    std::fclose(file);
}

}  // namespace rgl::standard::logging

// L'appel protégé de code C++ qui peut lancer une exception (native.hpp). Ce fichier est le seul du runtime compilé AVEC les exceptions (les
// autres le sont sans, `-fno-exceptions`) : il attrape ce que lance le C++ d'une liaison pour que le programme n'en voie jamais.

#include "rgl/standard/native.hpp"

#include <cstring>
#include <exception>

namespace {

void set_failure(rgl_native_status& status, const char* text) noexcept {
    status.failed = 1;
    std::strncpy(status.message, text != nullptr ? text : "", sizeof(status.message) - 1);
    status.message[sizeof(status.message) - 1] = '\0';
}

}  // namespace

extern "C" int rgl_native_guard(void (*body)(void*), void* context, rgl_native_status* status) noexcept {
    status->failed = 0;
    status->message[0] = '\0';
    try {
        body(context);
        return 0;
    } catch (const std::exception& e) {
        set_failure(*status, e.what());
    } catch (...) {
        set_failure(*status, rgl::standard::native::unknown_exception);
    }
    return 1;
}

#pragma once
#include "rgl/standard/array.hpp"

namespace rgl::standard {
template<class Target, class Source>
Target pack_array(const Source& source) {
    if constexpr (requires { typename Target::rgl_sequence; typename Source::rgl_sequence; }) {
        array<typename Target::rgl_sequence> values;
        for (std::size_t i = 0; i < source.size(); ++i)
            values.push(pack_array<typename Target::rgl_sequence>(source[i]));
        return Target::from(values);
    } else {
        return source;
    }
}
}

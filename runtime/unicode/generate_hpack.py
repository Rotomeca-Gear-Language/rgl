#!/usr/bin/env python3
"""Écrit runtime/include/rgl/standard/hpack_tables.hpp depuis le texte du RFC 7541 (compression d'en-têtes de HTTP/2).

Usage : python generate_hpack.py rfc7541.txt sortie.hpp

Le texte vient de https://www.rfc-editor.org/rfc/rfc7541.txt : la table statique (annexe A) et le code de Huffman (annexe B).
"""
import re
import sys


def main() -> None:
    text = open(sys.argv[1], encoding="utf-8").read()

    # Annexe A : « | 1 | :authority | |  » puis, pour les valeurs, « | 2 | :method | GET | ».
    start = text.index("Appendix A.  Static Table Definition")
    end = text.index("Appendix B.  Huffman Code")
    static = {}
    for match in re.finditer(r"^\s+\|\s*(\d+)\s*\|\s*([^|\s]+)\s*\|\s*([^|]*?)\s*\|\s*$", text[start:end], re.M):
        static[int(match.group(1))] = (match.group(2), match.group(3))
    assert sorted(static) == list(range(1, 62)), sorted(static)

    # Annexe B : « ( 48)  |00000  0  [ 5] ».
    start = text.index("Appendix B.  Huffman Code")
    codes = {}
    for match in re.finditer(r"^\s*(?:'.'|\S+)?\s*\(\s*(\d+)\)\s+\|([01|]+)\s+([0-9a-f]+)\s+\[\s*(\d+)\]", text[start:], re.M):
        symbol = int(match.group(1))
        value = int(match.group(3), 16)
        length = int(match.group(4))
        assert value == int(match.group(2).replace("|", ""), 2) and len(match.group(2).replace("|", "")) == length
        codes[symbol] = (value, length)
    # Le symbole 256 est écrit « EOS (256) ».
    for match in re.finditer(r"EOS \((256)\)\s+\|([01|]+)\s+([0-9a-f]+)\s+\[\s*(\d+)\]", text[start:]):
        codes[256] = (int(match.group(3), 16), int(match.group(4)))
    assert sorted(codes) == list(range(257)), [i for i in range(257) if i not in codes]

    out = ["#pragma once", "",
           "// Généré par runtime/unicode/generate_hpack.py depuis le RFC 7541 : ne pas modifier à la main.", "",
           "#include <cstdint>", "", "namespace rgl::standard::http::hpack_tables {", "",
           "struct header_entry { const char* name; const char* value; };",
           "struct huffman_entry { std::uint32_t code; std::uint8_t length; };", "",
           "// La table statique : l'index 1 est le premier élément.",
           "inline constexpr header_entry static_table[] = {"]
    for index in range(1, 62):
        name, value = static[index]
        out.append(f'    {{"{name}", "{value}"}},')
    out += ["};", "", "// Le code de Huffman de chaque octet, puis (256) celui de la fin de texte.",
            "inline constexpr huffman_entry huffman[257] = {"]
    for symbol in range(257):
        value, length = codes[symbol]
        out.append(f"    {{0x{value:X}, {length}}},")
    out += ["};", "", "}  // namespace rgl::standard::http::hpack_tables", ""]
    with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))
    print("statique 61, huffman 257")


if __name__ == "__main__":
    main()

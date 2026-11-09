#!/usr/bin/env python3
"""Écrit runtime/include/rgl/standard/unicode_tables.hpp depuis la base de caractères Unicode.

Usage : python generate_tables.py UnicodeData.txt CompositionExclusions.txt sortie.hpp

Les deux fichiers viennent de https://www.unicode.org/Public/UCD/latest/ucd/. Les syllabes Hangul ne sont pas dans les tables :
leur décomposition et leur composition sont calculées.
"""
import sys


def main() -> None:
    data_path, exclusions_path, out_path = sys.argv[1:4]

    exclusions = set()
    with open(exclusions_path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#")[0].strip()
            if line:
                exclusions.add(int(line, 16))

    ccc = {}
    canonical = {}
    compat = {}
    with open(data_path, encoding="utf-8") as f:
        for line in f:
            fields = line.rstrip("\n").split(";")
            cp = int(fields[0], 16)
            name = fields[1]
            if name.endswith(", First>") or name.endswith(", Last>"):
                continue
            klass = int(fields[3])
            if klass:
                ccc[cp] = klass
            mapping = fields[5]
            if not mapping:
                continue
            if mapping.startswith("<"):
                compat[cp] = [int(x, 16) for x in mapping.split(" ")[1:]]
            else:
                canonical[cp] = [int(x, 16) for x in mapping.split(" ")]

    pool = []

    def pooled(values):
        start = len(pool)
        pool.extend(values)
        return start, len(values)

    # Une paire se compose si sa décomposition canonique a deux éléments, si elle n'est pas exclue, et si ni le composé ni son premier
    # élément n'est un caractère combinant.
    compositions = []
    for cp, mapping in canonical.items():
        if len(mapping) != 2 or cp in exclusions:
            continue
        if ccc.get(cp, 0) != 0 or ccc.get(mapping[0], 0) != 0:
            continue
        compositions.append((mapping[0], mapping[1], cp))
    compositions.sort()

    lines = []
    lines.append("#pragma once")
    lines.append("")
    lines.append("// Généré par runtime/unicode/generate_tables.py depuis UnicodeData.txt et CompositionExclusions.txt : ne pas modifier à la main.")
    lines.append("")
    lines.append("#include <cstdint>")
    lines.append("")
    lines.append("namespace rgl::standard::unicode::tables {")
    lines.append("")
    lines.append("struct combining_entry { std::uint32_t code; std::uint8_t klass; };")
    lines.append("struct decomposition_entry { std::uint32_t code; std::uint16_t offset; std::uint8_t length; };")
    lines.append("struct composition_entry { std::uint32_t first; std::uint32_t second; std::uint32_t composed; };")
    lines.append("")

    lines.append("inline constexpr combining_entry combining[] = {")
    for cp in sorted(ccc):
        lines.append(f"    {{0x{cp:X}, {ccc[cp]}}},")
    lines.append("};")
    lines.append("")

    canonical_entries = []
    for cp in sorted(canonical):
        offset, length = pooled(canonical[cp])
        canonical_entries.append((cp, offset, length))
    compat_entries = []
    for cp in sorted(compat):
        offset, length = pooled(compat[cp])
        compat_entries.append((cp, offset, length))

    lines.append("inline constexpr decomposition_entry canonical[] = {")
    for cp, offset, length in canonical_entries:
        lines.append(f"    {{0x{cp:X}, {offset}, {length}}},")
    lines.append("};")
    lines.append("")
    lines.append("inline constexpr decomposition_entry compatibility[] = {")
    for cp, offset, length in compat_entries:
        lines.append(f"    {{0x{cp:X}, {offset}, {length}}},")
    lines.append("};")
    lines.append("")
    lines.append("inline constexpr std::uint32_t pool[] = {")
    for start in range(0, len(pool), 12):
        lines.append("    " + ", ".join(f"0x{v:X}" for v in pool[start:start + 12]) + ",")
    lines.append("};")
    lines.append("")
    lines.append("inline constexpr composition_entry composition[] = {")
    for first, second, composed in compositions:
        lines.append(f"    {{0x{first:X}, 0x{second:X}, 0x{composed:X}}},")
    lines.append("};")
    lines.append("")
    lines.append("}  // namespace rgl::standard::unicode::tables")
    lines.append("")

    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))

    print(f"combinants {len(ccc)}, canoniques {len(canonical_entries)}, compatibilité {len(compat_entries)}, "
          f"compositions {len(compositions)}, pool {len(pool)}")


if __name__ == "__main__":
    main()

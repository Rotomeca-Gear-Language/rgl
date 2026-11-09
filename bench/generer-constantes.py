"""Génère les bancs constantes-demarrage et constantes-recherche : de grosses constantes (dictionnaires et tableau)
écrites en littéral dans main.rgl, et leur expected.txt calculé ici, jamais copié de la sortie du programme.

    python bench/generer-constantes.py [taille]

La taille (5000 par défaut) est le nombre d'entrées de chaque constante.
"""
import json
import os
import sys

taille = int(sys.argv[1]) if len(sys.argv) > 1 else 5000
ici = os.path.dirname(os.path.abspath(__file__))

entiers = {j * 7: j % 10 for j in range(taille)}                 # [int:int]
noms = {f"n{j}": j % 10 for j in range(taille)}                  # [string:int]
suite = [(j * 3) % 1000 for j in range(taille)]                  # [int]
cles = [f"n{(j * 77) % taille}" for j in range(64)]              # [string;64] : 64 clés connues


def litteral_dictionnaire(d, texte):
    return "[" + ", ".join(f"{k!r}: {v}".replace("'", '"') if texte else f"{k}: {v}" for k, v in d.items()) + "]"


constantes = f"""const [int:int] ids: {litteral_dictionnaire(entiers, False)};
const [string:int] noms: {litteral_dictionnaire(noms, True)};
const [int] suite: [{", ".join(map(str, suite))}];
const [string;64] cles: [{", ".join(json.dumps(c) for c in cles)}];
"""

# --- constantes-demarrage : une recherche dans chaque constante, le reste est le coût de construction.
demarrage = f"""// Grosses constantes (dictionnaires, tableau) et une seule lecture de chacune : mesure le coût de construction
// au démarrage, la taille de l'exécutable et le temps de compilation. Généré par bench/generer-constantes.py ({taille} entrées).
{constantes}
emit fin(): uint {{
    `{{ids[7]}} {{noms["n3"]}} {{suite[{taille // 2}]}} {{cles[1]}}\\n` -> console;
    return 0;
}}

!train |> fin;
"""
attendu_demarrage = f"{entiers[7]} {noms['n3']} {suite[taille // 2]} {cles[1]}\n"

# --- constantes-recherche : dix millions de recherches dans chaque dictionnaire.
iterations = 10_000_000
recherche = f"""// Dix millions de recherches par clé entière puis par clé texte dans de grosses constantes : mesure le débit d'une recherche
// dans un dictionnaire constant. Généré par bench/generer-constantes.py ({taille} entrées).
{constantes}
emit fin(): uint {{
    int a: [0..<{iterations}] |+> 0 with (acc, x) => acc + ids[(x % {taille}) * 7];
    int b: [0..<{iterations}] |+> 0 with (acc, x) => acc + noms[cles[x % 64]];
    `{{a}} {{b}}\\n` -> console;
    return 0;
}}

!train |> fin;
"""
a = sum(entiers[(x % taille) * 7] for x in range(iterations))
b = sum(noms[cles[x % 64]] for x in range(iterations))
attendu_recherche = f"{a} {b}\n"

for nom, source, attendu in (("constantes-demarrage", demarrage, attendu_demarrage), ("constantes-recherche", recherche, attendu_recherche)):
    dossier = os.path.join(ici, nom)
    os.makedirs(dossier, exist_ok=True)
    with open(os.path.join(dossier, "main.rgl"), "w", encoding="utf-8", newline="\n") as f:
        f.write(source)
    with open(os.path.join(dossier, "expected.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(attendu)
    with open(os.path.join(dossier, "rgl.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f'{{\n    "name": "{nom}",\n    "version": "1.0.0",\n    "rgl": "0"\n}}\n')
    print(nom, os.path.getsize(os.path.join(dossier, "main.rgl")), "octets")

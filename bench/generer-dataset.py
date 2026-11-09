"""Génère bench/dataset/main.rgl : un dataset de 5 000 entrées lues par leur identifiant. Usage : python bench/generer-dataset.py"""
import os

N = 5000
lines = [
    "// Dataset : 5 000 entrées (nom, puissance, coût) et quelques lectures par identifiant : mesure le temps de compilation, la taille de l'exécutable",
    "// et la construction de la table au démarrage. Généré par bench/generer-dataset.py.",
    "mold Carte {",
    "    string nom;",
    "    int puissance;",
    "    int cout;",
    "    double ratio;",
    "}",
    "",
    "dataset Cartes of Carte {",
]
for i in range(N):
    lines.append(f"    c{i}: {{nom: 'Carte numéro {i}', puissance: {(i * 7) % 100}, cout: {(i * 3) % 10}, ratio: {((i % 50) + 1) / 4}}},")
lines += [
    "}",
    "",
    "emit fin(): uint {",
    "    const Carte a: Cartes.c42.get();",
    "    const Carte b: Cartes.c4999.get();",
    "    `{a.nom} {a.puissance} {b.nom} {b.cout} {Cartes.length}\n` -> console;",
    "    return 0;",
    "}",
    "",
    "!train |> fin;",
]
directory = os.path.join(os.path.dirname(__file__), "dataset")
os.makedirs(directory, exist_ok=True)
with open(os.path.join(directory, "main.rgl"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
with open(os.path.join(directory, "rgl.json"), "w", encoding="utf-8", newline="\n") as f:
    f.write('{\n    "name": "dataset",\n    "version": "1.0.0",\n    "rgl": "0"\n}\n')
with open(os.path.join(directory, "expected.txt"), "w", encoding="utf-8", newline="\n") as f:
    f.write(f"Carte numéro 42 {(42 * 7) % 100} Carte numéro 4999 {(4999 * 3) % 10} {N}\n")

# Mesures des bancs (release)

Date : 2026-10-25 16:18 · commit : a935fc9+modifié · 10 lancements · 13th Gen Intel(R) Core(TM) i5-13600KF (20 cœurs logiques) · Zig 0.17.0

| Banc | Médiane (ms) | Min (ms) | Travail (ms) | Taille (Ko) | Compilation (s) | vs base | Sortie |
|---|---:|---:|---:|---:|---:|---:|---|
| demarrage | 12,3 | 11,8 |  | 100 | 1,1 |  | ok |
| chaines | 590 | 583,9 | 578 | 114 | 1,3 |  | ok |
| json | 266 | 261,6 | 254 | 445 | 2 |  | ok |
| parcours | 103 | 101,8 | 91 | 369 | 1,2 |  | ok |
| particules | 78,9 | 77,8 | 67 | 105 | 1,2 |  | ok |
| recursif | 157 | 155,2 | 145 | 104 | 1,1 |  | ok |

Travail = médiane moins celle de demarrage (coût de lancement du processus).

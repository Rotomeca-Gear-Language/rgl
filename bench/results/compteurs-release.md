# Compteurs de mémoire des bancs (release)

Date : 2026-11-04 20:25 · commit : c4bf097+modifié

| Banc | Allocations | Octets (Ko) | Réallocations | Copies de tableaux | Éléments copiés | Copies de textes | Octets de textes copiés |
|---|---:|---:|---:|---:|---:|---:|---:|
| particules | 74 | 167318 | 0 | 1 | 0 | 0 | 0 |
| chaines | 13829 | 83559 | 0 | 2 | 0 | 0 | 0 |
| json | 800023 | 63805 | 46 | 600005 | 2200003 | 0 | 0 |
| constantes-recherche | 7 | 703 | 0 | 0 | 0 | 0 | 0 |
| constantes-demarrage | 7 | 703 | 0 | 0 | 0 | 0 | 0 |
| parcours | 1 | 0 | 0 | 0 | 0 | 0 | 0 |
| recursif | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| demarrage | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

Trié par octets alloués. Seuls les blocs du système (tas) sont comptés ; le pool et les arènes ont leurs propres allocateurs. Une copie profonde est un tableau ou un texte recopié élément par élément (les textes courts ou partagés par compteur ne comptent pas). Un banc sans tableau ni texte long n'écrit rien : il est à zéro.

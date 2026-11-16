# Dépôt d'index RGL (modèle)

Ce dossier est le **modèle d'un dépôt `rgl-index`** : l'index de découverte que lisent `rgl search`, `rgl add nom-court` et `rgl run script`. Rien n'y est hébergé par RGL : c'est un dépôt GitHub ordinaire dont une action fabrique `index.json` et le publie par GitHub Pages. L'index ne sert qu'à **trouver un nom** — un projet compile sans lui, ses dépendances sont des adresses écrites dans son `rgl.json`.

## Ajouter un paquet

Une demande de fusion ajoute `packages/<nom>.json`, où `<nom>` est le champ `name` du `rgl.json` du paquet :

```json
{ "source": "github.com/alice/json", "keywords": ["json", "parse"] }
```

L'action lance `rgl index build` : le paquet est récupéré à sa dernière version (tag `vX.Y.Z`), son manifeste doit porter le nom du fichier, il doit **compiler**. La description, la licence et la version viennent de son manifeste.

## Ajouter un script

Un script est un gist public, figé à une révision : `scripts/<nom>.json`.

```json
{ "gist": "abcdef0123456789abcdef0123456789", "revision": "0123456789abcdef0123456789abcdef01234567", "description": "Dit bonjour" }
```

Le gist contient **un seul `.rgl`**, éventuellement des `.forge` et un `rgl.json` dont seules les `dependencies` comptent — des paquets **de l'index**, jamais `package-trust`. Le compilateur écrit lui-même le manifeste du projet avec `no-cpp` et `no-trust` : un script ne s'autorise ni C++ ni `@trusted`. Il est compilé à la construction de l'index ; `rgl run` ne lit **que la révision** de l'index, jamais la suite du gist : son auteur peut le modifier, pas ce qui a été vérifié. Pour publier une correction, une nouvelle demande de fusion change la révision.

## Construire l'index à la main

```sh
rgl index build            # lit ./packages et ./scripts, écrit ./index.json
rgl index build depot -o public/index.json
```

La moindre erreur n'écrit rien. L'adresse publiée se règle par la variable d'environnement `RGL_INDEX_URL` (`https://rotomeca.github.io/rgl-index/index.json` par défaut).

## L'action

`.github/workflows/index.yml` (ci-contre) vérifie chaque demande de fusion et publie `index.json` par GitHub Pages à chaque fusion sur `main`. L'étape qui installe `rgl` est à adapter à la façon dont tu distribues le compilateur.

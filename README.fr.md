# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · **Français** · [Español](README.es.md)

**Un atelier local pour les auteurs de mods [Factorio](https://factorio.com).** Créez un mod, trouvez ses erreurs avant le jeu,
traduisez-le, empaquetez une version propre et testez-le avec un ensemble de mods isolé : en ligne de commande ou depuis une page
de votre navigateur. Tout tourne sur votre machine, à côté de votre éditeur de code habituel.

> **Version précoce (0.1).** La logique est couverte par des tests ; le lancement du vrai jeu (`run`, `smoke`) fonctionne avec Factorio 2.1 sous
> Windows. Les autres versions et systèmes n'ont pas encore été essayés. Vos retours et rapports de bugs sont les bienvenus.
> Projet communautaire non officiel, sans lien avec Wube Software. « Factorio » est une marque de Wube Software Ltd.
>
> Ceci est une traduction de la partie d'introduction. La documentation complète est [en anglais](README.md#documentation) ; en cas de divergence, le README anglais fait foi.

## Ce qu'il fait

- **Un mod en quelques secondes.** Des modèles (vide, objet + recette, entité, script d'exécution) pour Factorio 2.0 et 2.1 (tous sauf l'entité aussi pour 1.1), qui passent toutes les vérifications d'emblée.
- **Les erreurs avant le jeu.** `info.json`, changelog, miniature, chemins `__mod__/` et `require` cassés, traductions manquantes ou incorrectes : tout avec le fichier et la ligne.
- **Voir l'intérieur de n'importe quel mod.** Un studio dans le navigateur avec l'arborescence des fichiers, un éditeur Lua avec vérifications en direct, les prototypes et recettes du mod, ses graphismes et sons, et les fichiers que rien n'utilise.
- **Traduire confortablement.** Éditeur original et traduction côte à côte, avec la progression par langue, la vérification des espaces réservés et des suggestions tirées de vos traductions précédentes.
- **Une version propre.** `NAME_VERSION.zip` avec la bonne structure de dossiers, construit seulement si le mod passe la vérification.
- **Tester sans toucher à votre jeu.** Lancez Factorio ou un test de fumée sans interface avec un ensemble de mods isolé par profil ; votre dossier de mods habituel n'est jamais modifié. Fonctionne en CI.
- **Lire les logs plus vite.** Un log de Factorio réduit à ses erreurs et avertissements, doublons fusionnés, chacun avec le fichier, la ligne et une piste.

## Feuille de route

Où en est le projet et ce qui vient ensuite. Les versions sont des objectifs, pas des dates ; l'ordre suit les retours, alors
[ouvrez une issue](https://github.com/4a11/factorio-mod-studio/issues) si un point compte pour vous (ou s'il manque ici).

**0.1 : maintenant.** La ligne de commande (`new`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) et le studio dans le
navigateur (Aperçu, Fichiers, Prototypes, Ressources, Localisation, Problèmes, Compilation) en 8 langues d'interface.

**0.2 : éprouvé sur le vrai jeu**
- [ ] `run` et `smoke` vérifiés avec Factorio 1.1, 2.0 et 2.1 sous Windows, Linux et macOS, y compris le serveur sans interface ; *2.1 sous Windows fonctionne*
- [ ] après un lancement ou un test de fumée, lire aussi `factorio-current.log` et afficher ses erreurs dans Problèmes et Sortie
- [ ] les prototypes générés par les modèles (entité, script d'exécution) chargés dans le jeu, pas seulement écrits d'après la documentation
- [ ] installation en une ligne : l'outil publié sur NuGet (`dotnet tool install -g FactorioModStudio.Cli`), sans clonage
- [ ] des versions de Factorio installées par le studio : `fmstudio factorio install 2.0` télécharge le serveur sans interface gratuit (Linux, WSL, CI), où `smoke` le trouve, aussi depuis l'onglet Compilation ; *fait, pas encore essayé avec le vrai téléchargement*

**0.3 : les onglets restants**
- [ ] Problèmes dans le nouveau style : regroupés par fichier et par règle, corrections rapides sûres (ajouter une clé de traduction manquante, corriger la casse d'un chemin)
- [ ] Compilation : choisir le profil, des mods supplémentaires et une sauvegarde ; un historique des lancements à comparer
- [ ] icônes des ingrédients et résultats issus du jeu de base ou d'une dépendance
- [ ] les dépendances d'un coup d'œil : requises, optionnelles, incompatibles, et lesquelles sont disponibles dans `modsSource`

**0.4 : processus de publication**
- [ ] montée de version et entrée de changelog en une étape (`fmstudio bump patch|minor|major`)
- [ ] envoi sur le portail de mods Factorio (facultatif, avec votre propre clé d'API)
- [ ] un workflow GitHub Actions prêt à l'emploi (`fmstudio init --ci`) : vérifier, empaqueter et tester à chaque push

**Plus tard**
- les prototypes finaux via `--dump-data` : « quel mod a modifié cet objet, et en quoi ? »
- lancer depuis le studio les tests écrits pour [FactorioTest](https://mods.factorio.com/mod/factorio-test)
- une extension d'éditeur (VS Code) sur la même API

## Pourquoi

L'édition de Lua, le débogage et les tests unitaires sont déjà bien couverts (par exemple par *Factorio Modding Tool Kit* pour VS Code et
*FactorioTest*). Ce qui reste fastidieux, c'est le liant autour : un zip de version correct, un ensemble de mods reproductible à lancer,
la seule vraie erreur noyée dans un mur de log, des traductions qui divergent. Factorio Mod Studio s'occupe de ce liant.

## Démarrage rapide

**Le plus simple :** téléchargez l'archive pour votre système depuis [Releases](https://github.com/4a11/factorio-mod-studio/releases/latest) et décompressez-la ; .NET n'est pas nécessaire.
Sous Windows, double-cliquez sur `fmstudio.exe` et le studio s'ouvre dans votre navigateur ; ailleurs, lancez `./fmstudio ui`.

**Comme outil .NET** (nécessite le [SDK .NET 8](https://dotnet.microsoft.com/download)) :

```sh
# 1. Installer l'outil `fmstudio` (une fois)
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg

# 2. Créer un mod dans le dossier de votre choix et ouvrir le studio
cd ~/factorio-dev
fmstudio new my-mod --template item-recipe   # un mod prêt à lancer dans ./mods/my-mod
fmstudio ui                                  # tous les mods de ./mods, dans votre navigateur
```

Un cycle typique : les boutons en haut de chaque mod dans le navigateur, ou la même chose dans le terminal depuis le dossier du mod :

```sh
cd mods/my-mod
fmstudio validate      # ce qui ne va pas, avec fichier et ligne
fmstudio pack          # dist/my-mod_0.1.0.zip
fmstudio smoke         # charger sans interface pendant 600 ticks ; échoue à la moindre erreur
fmstudio run           # lancer Factorio avec ce mod et ses dépendances seulement
```

Factorio est trouvé automatiquement (les dossiers d'installation habituels, `FACTORIO_PATH` ou les installations ajoutées dans
l'interface) ; la version qui correspond au `factorio_version` du mod est utilisée.

## Documentation

La documentation complète (commandes, configuration, onglets du studio, règles de vérification, API) est [en anglais](README.md#documentation).

## Licence

[MIT](LICENSE)

# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · **Français** · [Español](README.es.md)

**Un atelier local pour les auteurs de mods [Factorio](https://factorio.com).** Créez un mod, trouvez ses erreurs avant le jeu,
traduisez-le, empaquetez une version propre et testez-le avec un ensemble de mods isolé : dans sa propre fenêtre, en ligne de commande
ou dans votre navigateur. Tout tourne sur votre machine, à côté de votre éditeur de code habituel.

> **Version précoce (0.1).** La logique est couverte par des tests ; le lancement du vrai jeu (`run`, `smoke`) fonctionne avec Factorio 2.1 sous
> Windows. Les autres versions et systèmes n'ont pas encore été essayés. Vos retours et rapports de bugs sont les bienvenus.
> L'application de bureau est nouvelle : elle fonctionne sous Windows ; Linux et macOS n'ont pas encore été essayés.
> Projet communautaire non officiel, sans lien avec Wube Software. « Factorio » est une marque de Wube Software Ltd.
>
> Ceci est une traduction de la partie d'introduction. La documentation complète est [en anglais](README.md#documentation) ; en cas de divergence, le README anglais fait foi.

![The Overview tab: release readiness, counts and the prototypes a mod changes](docs/images/overview.png)

| Files | Localization |
| --- | --- |
| ![The Files tab: the file tree with entry points and unused files](docs/images/files.png) | ![The Localization tab: progress per language and the side-by-side editor](docs/images/localization.png) |

## Ce qu'il fait

- **Un mod en quelques secondes.** Des modèles (vide, objet + recette, entité, script d'exécution) pour Factorio 2.0 et 2.1 (tous sauf l'entité aussi pour 1.1), qui passent toutes les vérifications d'emblée.
- **Les erreurs avant le jeu.** `info.json`, changelog, miniature, chemins `__mod__/` et `require` cassés, traductions manquantes ou incorrectes : tout avec le fichier et la ligne.
- **Voir l'intérieur de n'importe quel mod.** Le studio, dans sa propre fenêtre ou dans le navigateur, avec l'arborescence des fichiers, un éditeur Lua avec vérifications en direct, les prototypes et recettes du mod, ses graphismes et sons, et les fichiers que rien n'utilise.
- **Ajouter des mods sans surprise.** Ajoutez un mod ou tout un dossier de mods, comme le dossier `mods` de Factorio : tout est vérifié avant la moindre copie.
- **Traduire confortablement.** Éditeur original et traduction côte à côte, avec la progression par langue, la vérification des espaces réservés et des suggestions tirées de vos traductions précédentes.
- **Une version propre.** `NAME_VERSION.zip` avec la bonne structure de dossiers, construit seulement si le mod passe la vérification.
- **Tester sans toucher à votre jeu.** Lancez Factorio ou un test de fumée sans interface avec un ensemble de mods isolé par profil ; votre dossier de mods habituel n'est jamais modifié. Fonctionne en CI.
- **Lire les logs plus vite.** Un log de Factorio réduit à ses erreurs et avertissements, doublons fusionnés, chacun avec le fichier, la ligne et une piste.

## Feuille de route

Où en est le projet et ce qui vient ensuite. Les versions sont des objectifs, pas des dates ; l'ordre suit les retours, alors
[ouvrez une issue](https://github.com/4a11/factorio-mod-studio/issues) si un point compte pour vous (ou s'il manque ici).

**0.1 : maintenant.** La ligne de commande (`new`, `add`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) et le studio, dans sa propre fenêtre ou dans le navigateur, en 8 langues d'interface : onglets Aperçu, Fichiers, Prototypes, Ressources, Localisation, Problèmes et Compilation ; 20 blocs (armure, ennemis, niveaux de convoyeurs, machines, véhicules…) en copies recolorées des originaux, vérifiés dans Factorio 2.1 ; des dossiers entiers de mods ajoutés après vérification.

**0.2 : prêt pour tout le monde**
- [ ] l'application de bureau partout : dans les archives de version pour Windows, Linux et macOS, et un seul programme avec la ligne de commande (un double-clic ouvre la fenêtre) ; *fonctionne sous Windows, compilée depuis les sources*
- [ ] `run` et `smoke` vérifiés avec Factorio 1.1, 2.0 et 2.1 sous Windows, Linux et macOS, y compris le serveur sans interface ; *2.1 sous Windows fonctionne*
- [ ] après un lancement ou un test de fumée, lire aussi `factorio-current.log` et afficher ses erreurs dans Problèmes et Sortie
- [x] les prototypes générés par les modèles (entité, script d'exécution) chargés dans le jeu, pas seulement écrits d'après la documentation; *les quatre se chargent dans Factorio 2.1*
- [x] installation en une ligne : l'outil publié sur NuGet (`dotnet tool install -g FactorioModStudio.Cli`), sans clonage; *publié : 0.1.1*
- [ ] des versions de Factorio installées par le studio : `fmstudio factorio install 2.0` télécharge le serveur sans interface gratuit (Linux, WSL, CI), où `smoke` le trouve, aussi depuis l'onglet Compilation ; *fait, pas encore essayé avec le vrai téléchargement*

**0.3 : voir et modifier le contenu**
- [ ] l'onglet Prototypes d'après les données du jeu lui-même (`--dump-data`) : ce que le mod crée et modifie vraiment, avec toutes les valeurs, y compris les copies faites avec `table.deepcopy` ; « quel mod a modifié cet objet, et en quoi ? »
- [ ] un panneau de propriétés pour les blocs : vie, vitesse, inventaire, dégâts… en champs « original → votre valeur », réécrits dans le Lua du bloc
- [ ] un essai rapide en jeu : un bouton lance Factorio sur une carte de test avec les nouveautés déjà dans l'inventaire
- [ ] renommer ou retirer un bloc en toute sécurité : le fichier, le require, les clés de traduction, les icônes et toutes les références changent ensemble
- [ ] la couleur d'un bloc choisie avec un curseur, avec l'aperçu de l'icône recolorée avant l'ajout
- [ ] traductions des nouveaux blocs : leurs clés ajoutées à toutes les langues du mod, marquées non traduites dans l'onglet Localisation

**0.4 : un éditeur confortable**
- [ ] aides Lua dans l'éditeur : complétion, documentation au survol et aide sur les paramètres pour l'API de Factorio (`data.raw`, champs des prototypes, `game`, `script`, `defines`, événements), lues dans la description de l'API fournie avec le jeu installé (`doc-html/*-api.json`), donc conformes à sa version
- [ ] Problèmes dans le nouveau style : regroupés par fichier et par règle, corrections rapides sûres (ajouter une clé de traduction manquante, corriger la casse d'un chemin)
- [ ] icônes des ingrédients et résultats issus du jeu de base ou d'une dépendance
- [ ] les dépendances d'un coup d'œil : requises, optionnelles, incompatibles, et lesquelles sont disponibles dans `modsSource`
- [ ] un éditeur de recettes et de technologies : ingrédients choisis avec leurs icônes du jeu et des dépendances, et la technologie qui débloque la recette

**0.5 : processus de publication**
- [ ] montée de version et entrée de changelog en une étape (`fmstudio bump patch|minor|major`)
- [ ] envoi sur le portail de mods Factorio (facultatif, avec votre propre clé d'API)
- [ ] un workflow GitHub Actions prêt à l'emploi (`fmstudio init --ci`) : vérifier, empaqueter et tester à chaque push
- [ ] Compilation : choisir le profil, des mods supplémentaires et une sauvegarde ; un historique des lancements à comparer

**0.6 : graphismes**
- [ ] un éditeur de sprites dans le studio : créer et modifier des icônes et des sprites, et monter des animations (images, feuille de sprites, aperçu en direct) sans quitter le studio

**Plus tard**
- lancer depuis le studio les tests écrits pour [FactorioTest](https://mods.factorio.com/mod/factorio-test)
- une extension d'éditeur (VS Code) sur la même API

## Pourquoi

L'édition de Lua, le débogage et les tests unitaires sont déjà bien couverts (par exemple par *Factorio Modding Tool Kit* pour VS Code et
*FactorioTest*). Ce qui reste fastidieux, c'est le liant autour : un zip de version correct, un ensemble de mods reproductible à lancer,
la seule vraie erreur noyée dans un mur de log, des traductions qui divergent. Factorio Mod Studio s'occupe de ce liant.

## Démarrage rapide

**Application de bureau :** le studio dans sa propre fenêtre. Pour l'instant, elle se compile depuis les sources (nécessite le [SDK .NET 10](https://dotnet.microsoft.com/download)) ; elle arrivera dans les archives avec la prochaine version.

```sh
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet run --project src/FactorioModStudio.Desktop -c Release   # vos mods vont dans ./mods
```

**Sans .NET :** téléchargez l'archive pour votre système depuis [Releases](https://github.com/4a11/factorio-mod-studio/releases/latest) et décompressez-la ; .NET n'est pas nécessaire.
Sous Windows, double-cliquez sur `fmstudio.exe` et le studio s'ouvre dans votre navigateur ; ailleurs, lancez `./fmstudio ui`.

**Comme outil .NET** (nécessite le [SDK .NET 10](https://dotnet.microsoft.com/download)) :

```sh
# 1. Installer l'outil `fmstudio` (une fois)
dotnet tool install --global FactorioModStudio.Cli

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

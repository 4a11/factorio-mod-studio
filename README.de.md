# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · **Deutsch** · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · [Français](README.fr.md) · [Español](README.es.md)

**Eine lokale Werkstatt für Autorinnen und Autoren von [Factorio](https://factorio.com)-Mods.** Eine Mod anlegen, ihre Fehler finden,
bevor das Spiel es tut, sie übersetzen, ein sauberes Release packen und sie mit einem isolierten Mod-Satz testen: in einem eigenen
Programmfenster, auf der Kommandozeile oder im Browser. Alles läuft auf Ihrem Rechner, neben Ihrem gewohnten Code-Editor.

> **Frühe Version (0.1).** Die Logik ist durch Tests abgedeckt; der Start des echten Spiels (`run`, `smoke`) funktioniert mit Factorio 2.1
> unter Windows. Andere Versionen und Systeme wurden noch nicht ausprobiert. Rückmeldungen und Fehlerberichte sind sehr willkommen.
> Die Desktop-App ist neu: Sie läuft unter Windows; Linux und macOS wurden noch nicht ausprobiert.
> Inoffizielles Community-Projekt, nicht mit Wube Software verbunden. „Factorio“ ist eine Marke der Wube Software Ltd.
>
> Dies ist eine Übersetzung des einleitenden Teils. Die ausführliche Dokumentation gibt es [auf Englisch](README.md#documentation); bei Abweichungen gilt das englische README.

![The Overview tab: release readiness, counts and the prototypes a mod changes](docs/images/overview.png)

| Files | Localization |
| --- | --- |
| ![The Files tab: the file tree with entry points and unused files](docs/images/files.png) | ![The Localization tab: progress per language and the side-by-side editor](docs/images/localization.png) |

## Was es kann

- **Eine Mod in Sekunden.** Vorlagen (leer, Gegenstand + Rezept, Entität, Laufzeit-Skript) für Factorio 2.0 und 2.1 (alle außer der Entität auch für 1.1), die sofort jede Prüfung bestehen.
- **Fehler früh finden.** `info.json`, Changelog, Vorschaubild, kaputte `__mod__/`-Pfade und `require`s, fehlende oder fehlerhafte Übersetzungen: alles mit Datei und Zeile.
- **In jede Mod hineinsehen.** Das Studio, im eigenen Fenster oder im Browser, mit Dateibaum, einem Lua-Editor mit Live-Prüfungen, den Prototypen und Rezepten der Mod, ihren Grafiken und Sounds und den Dateien, die nichts verwendet.
- **Mods sicher hereinholen.** Einen Mod oder einen ganzen Mod-Ordner hinzufügen, etwa Factorios eigenen `mods`-Ordner: Alles wird geprüft, bevor etwas kopiert wird.
- **Bequem übersetzen.** Editor mit Original und Übersetzung nebeneinander, Fortschritt pro Sprache, Platzhalterprüfung und Vorschlägen aus Ihren früheren Übersetzungen.
- **Ein sauberes Release.** `NAME_VERSION.zip` mit der richtigen Ordnerstruktur, nur gebaut, wenn die Mod die Prüfung besteht.
- **Testen, ohne Ihr Spiel anzufassen.** Factorio oder einen Headless-Smoke-Test mit einem isolierten Mod-Satz pro Profil starten; Ihr normaler Mod-Ordner bleibt unberührt. Funktioniert in CI.
- **Logs schneller lesen.** Ein Factorio-Log auf seine Fehler und Warnungen verdichtet, Duplikate zusammengefasst, jeweils mit Datei, Zeile und Hinweis.

## Roadmap

Wo das Projekt steht und was als Nächstes kommt. Versionen sind Ziele, keine Termine; die Reihenfolge richtet sich nach Rückmeldungen,
also [eröffnen Sie ein Issue](https://github.com/4a11/factorio-mod-studio/issues), wenn Ihnen ein Punkt wichtig ist (oder hier fehlt).

**0.1: jetzt.** Die Kommandozeile (`new`, `add`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) und das Studio, im eigenen Fenster oder im Browser, in 8 Oberflächensprachen: die Tabs Übersicht, Dateien, Prototypen, Ressourcen, Lokalisierung, Probleme und Build; 20 Bausteine (Rüstung, Gegner, Förderband-Stufen, Maschinen, Fahrzeuge…) als umgefärbte Kopien der Originale, in Factorio 2.1 geprüft; ganze Mod-Ordner werden vor dem Hinzufügen geprüft.

**0.2: bereit für alle**
- [ ] die Desktop-App überall: in den Release-Archiven für Windows, Linux und macOS, und ein Programm zusammen mit der Kommandozeile (ein Doppelklick öffnet das Fenster); *läuft unter Windows, aus dem Quellcode gebaut*
- [ ] `run` und `smoke` mit Factorio 1.1, 2.0 und 2.1 unter Windows, Linux und macOS geprüft, einschließlich Headless-Server; *2.1 unter Windows funktioniert; `smoke` auf dem Linux-Headless-Server für 1.1, 2.0 und 2.1, in der CI*
- [x] nach einem Start oder Smoke-Test auch `factorio-current.log` lesen und seine Fehler unter Probleme und Ausgabe zeigen
- [x] die Prototypen der Vorlagen (Entität, Laufzeit-Skript) im Spiel geladen, nicht nur nach der Dokumentation geschrieben; *alle vier laden in Factorio 2.1*
- [x] Installation in einer Zeile: das Werkzeug auf NuGet veröffentlicht (`dotnet tool install -g FactorioModStudio.Cli`), kein Klonen nötig; *veröffentlicht: 0.1.1*
- [x] Factorio-Versionen installiert das Studio selbst: `fmstudio factorio install 2.0` lädt den kostenlosen Headless-Server (Linux, WSL, CI), wo `smoke` ihn findet, auch aus dem Build-Tab; *der echte Download für 1.1, 2.0 und 2.1 in der CI mit Smoke-Tests geprüft*

**0.3: Inhalte sehen und bearbeiten**
- [ ] der Prototypen-Tab aus den Daten des Spiels selbst (`--dump-data`): was der Mod wirklich anlegt und ändert, mit allen Werten, auch für Kopien per `table.deepcopy`; „Welcher Mod hat diesen Gegenstand geändert, und wie?“
- [ ] ein Eigenschaften-Panel für Bausteine: Lebenspunkte, Tempo, Inventar, Schaden… als Felder „Original → Ihr Wert“, die zurück ins Lua des Bausteins geschrieben werden
- [ ] schnell im Spiel ausprobieren: ein Knopf startet Factorio auf einer Testkarte, das Neue liegt schon im Inventar
- [ ] Bausteine sicher umbenennen oder entfernen: Datei, require, Übersetzungsschlüssel, Symbole und alle Verweise ändern sich gemeinsam
- [ ] die Farbe eines Bausteins per Schieberegler, mit Vorschau des umgefärbten Symbols vor dem Hinzufügen
- [ ] Übersetzungen neuer Bausteine: ihre Schlüssel kommen in alle Sprachen des Mods, im Lokalisierungs-Tab als unübersetzt markiert

**0.4: ein komfortabler Editor**
- [ ] Lua-Hilfen im Editor: Vervollständigung, Dokumentation beim Überfahren und Parameterhilfe für die Factorio-API (`data.raw`, Prototyp-Felder, `game`, `script`, `defines`, Ereignisse), gelesen aus der API-Beschreibung, die das installierte Spiel mitbringt (`doc-html/*-api.json`), also passend zu dessen Version
- [ ] Probleme im neuen Stil: nach Datei und Regel gruppiert, sichere Schnellkorrekturen (fehlenden Übersetzungsschlüssel anlegen, Groß-/Kleinschreibung eines Pfads korrigieren)
- [ ] Symbole für Zutaten und Ergebnisse aus dem Basisspiel oder einer Abhängigkeit
- [ ] Abhängigkeiten auf einen Blick: erforderlich, optional, inkompatibel, und welche davon in `modsSource` verfügbar sind
- [ ] ein Editor für Rezepte und Technologien: Zutaten mit ihren Symbolen aus dem Spiel und den Abhängigkeiten, dazu die Technologie, die das Rezept freischaltet

**0.5: Release-Ablauf**
- [ ] Version erhöhen und Changelog-Eintrag in einem Schritt (`fmstudio bump patch|minor|major`)
- [ ] Upload auf das Factorio-Mod-Portal (optional, mit Ihrem eigenen API-Schlüssel)
- [ ] ein fertiger GitHub-Actions-Workflow (`fmstudio init --ci`): bei jedem Push prüfen, packen und smoke-testen
- [ ] Build: Profil, zusätzliche Mods und einen Spielstand wählen; ein Verlauf der Läufe zum Vergleichen

**0.6: Grafik**
- [ ] ein Sprite-Editor im Studio: Icons und Sprites erstellen und bearbeiten, Animationen bauen (Frames, Spritesheet, Live-Vorschau), ohne das Studio zu verlassen

**0.7: Welten**
- [ ] ein Karteneditor: neue Planeten und Oberflächen, Biome (welche Kacheln, Dekorationen, Ressourcen und Gegner wo erscheinen, über Autoplace und Noise-Ausdrücke) und Einstellungen der Kartengenerierung, mit einer Vorschau des erzeugten Geländes

**Später**
- Tests, die für [FactorioTest](https://mods.factorio.com/mod/factorio-test) geschrieben sind, aus dem Studio starten
- eine Editor-Erweiterung (VS Code) auf derselben API

## Warum

Lua bearbeiten, debuggen und Unit-Tests sind schon gut abgedeckt (zum Beispiel durch das *Factorio Modding Tool Kit* für VS Code und
*FactorioTest*). Mühsam bleibt der Kitt dazwischen: ein korrektes Release-Zip, ein reproduzierbarer Mod-Satz zum Starten, die Suche
nach dem einen echten Fehler in einer Wand aus Log-Ausgaben, Übersetzungen, die auseinanderlaufen. Factorio Mod Studio übernimmt diesen Kitt.

## Schnellstart

**Desktop-App:** das Studio in einem eigenen Fenster. Vorerst wird sie aus dem Quellcode gebaut (braucht das [.NET 10 SDK](https://dotnet.microsoft.com/download)); in die Release-Archive kommt sie mit der nächsten Version.

```sh
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet run --project src/FactorioModStudio.Desktop -c Release   # Ihre Mods liegen in ./mods
```

**Ohne .NET:** das Archiv für Ihr System von [Releases](https://github.com/4a11/factorio-mod-studio/releases/latest) herunterladen und entpacken; .NET wird nicht gebraucht.
Unter Windows `fmstudio.exe` doppelt anklicken, und das Studio öffnet sich im Browser; sonst `./fmstudio ui` starten.

**Als .NET-Werkzeug** (braucht das [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```sh
# 1. Das Werkzeug `fmstudio` installieren (einmalig)
dotnet tool install --global FactorioModStudio.Cli

# 2. Eine Mod in einem beliebigen Ordner anlegen und das Studio öffnen
cd ~/factorio-dev
fmstudio new my-mod --template item-recipe   # eine startbereite Mod in ./mods/my-mod
fmstudio ui                                  # alle Mods in ./mods, im Browser
```

Ein typischer Ablauf: die Schaltflächen oben bei jeder Mod im Browser, oder dasselbe im Terminal aus dem Ordner der Mod:

```sh
cd mods/my-mod
fmstudio validate      # was nicht stimmt, mit Datei und Zeile
fmstudio pack          # dist/my-mod_0.1.0.zip
fmstudio smoke         # headless für 600 Ticks laden; schlägt bei jedem Fehler fehl
fmstudio run           # Factorio nur mit dieser Mod und ihren Abhängigkeiten starten
```

Factorio wird automatisch gefunden (die üblichen Installationsordner, `FACTORIO_PATH` oder in der Oberfläche hinzugefügte Installationen);
verwendet wird die Version, die zur `factorio_version` der Mod passt.

## Dokumentation

Die ausführliche Dokumentation (Befehle, Konfiguration, die Tabs des Studios, Prüfregeln, API) gibt es [auf Englisch](README.md#documentation).

## Lizenz

[MIT](LICENSE)

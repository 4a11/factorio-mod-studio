# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · **Deutsch** · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · [Français](README.fr.md) · [Español](README.es.md)

**Eine lokale Werkstatt für Autorinnen und Autoren von [Factorio](https://factorio.com)-Mods.** Eine Mod anlegen, ihre Fehler finden,
bevor das Spiel es tut, sie übersetzen, ein sauberes Release packen und sie mit einem isolierten Mod-Satz testen: auf der
Kommandozeile oder auf einer Seite im Browser. Alles läuft auf Ihrem Rechner, neben Ihrem gewohnten Code-Editor.

> **Frühe Version (0.1).** Die Logik ist durch Tests abgedeckt; der Start des echten Spiels (`run`, `smoke`) funktioniert mit Factorio 2.1
> unter Windows. Andere Versionen und Systeme wurden noch nicht ausprobiert. Rückmeldungen und Fehlerberichte sind sehr willkommen.
> Inoffizielles Community-Projekt, nicht mit Wube Software verbunden. „Factorio“ ist eine Marke der Wube Software Ltd.
>
> Dies ist eine Übersetzung des einleitenden Teils. Die ausführliche Dokumentation gibt es [auf Englisch](README.md#documentation); bei Abweichungen gilt das englische README.

## Was es kann

- **Eine Mod in Sekunden.** Vorlagen (leer, Gegenstand + Rezept, Entität, Laufzeit-Skript) für Factorio 2.0 und 2.1 (alle außer der Entität auch für 1.1), die sofort jede Prüfung bestehen.
- **Fehler früh finden.** `info.json`, Changelog, Vorschaubild, kaputte `__mod__/`-Pfade und `require`s, fehlende oder fehlerhafte Übersetzungen: alles mit Datei und Zeile.
- **In jede Mod hineinsehen.** Ein Studio im Browser mit Dateibaum, einem Lua-Editor mit Live-Prüfungen, den Prototypen und Rezepten der Mod, ihren Grafiken und Sounds und den Dateien, die nichts verwendet.
- **Bequem übersetzen.** Editor mit Original und Übersetzung nebeneinander, Fortschritt pro Sprache, Platzhalterprüfung und Vorschlägen aus Ihren früheren Übersetzungen.
- **Ein sauberes Release.** `NAME_VERSION.zip` mit der richtigen Ordnerstruktur, nur gebaut, wenn die Mod die Prüfung besteht.
- **Testen, ohne Ihr Spiel anzufassen.** Factorio oder einen Headless-Smoke-Test mit einem isolierten Mod-Satz pro Profil starten; Ihr normaler Mod-Ordner bleibt unberührt. Funktioniert in CI.
- **Logs schneller lesen.** Ein Factorio-Log auf seine Fehler und Warnungen verdichtet, Duplikate zusammengefasst, jeweils mit Datei, Zeile und Hinweis.

## Roadmap

Wo das Projekt steht und was als Nächstes kommt. Versionen sind Ziele, keine Termine; die Reihenfolge richtet sich nach Rückmeldungen,
also [eröffnen Sie ein Issue](https://github.com/4a11/factorio-mod-studio/issues), wenn Ihnen ein Punkt wichtig ist (oder hier fehlt).

**0.1: jetzt.** Die Kommandozeile (`new`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) und das Studio im Browser
(Übersicht, Dateien, Prototypen, Ressourcen, Lokalisierung, Probleme, Build) in 8 Oberflächensprachen.

**0.2: am echten Spiel erprobt**
- [ ] `run` und `smoke` mit Factorio 1.1, 2.0 und 2.1 unter Windows, Linux und macOS geprüft, einschließlich Headless-Server; *2.1 unter Windows funktioniert*
- [ ] nach einem Start oder Smoke-Test auch `factorio-current.log` lesen und seine Fehler unter Probleme und Ausgabe zeigen
- [ ] die Prototypen der Vorlagen (Entität, Laufzeit-Skript) im Spiel geladen, nicht nur nach der Dokumentation geschrieben
- [ ] Installation in einer Zeile: das Werkzeug auf NuGet veröffentlicht (`dotnet tool install -g FactorioModStudio.Cli`), kein Klonen nötig
- [ ] Factorio-Versionen installiert das Studio selbst: `fmstudio factorio install 2.0` lädt den kostenlosen Headless-Server (Linux, WSL, CI), wo `smoke` ihn findet, auch aus dem Build-Tab; *gebaut, mit dem echten Download noch nicht erprobt*

**0.3: die übrigen Tabs**
- [ ] Probleme im neuen Stil: nach Datei und Regel gruppiert, sichere Schnellkorrekturen (fehlenden Übersetzungsschlüssel anlegen, Groß-/Kleinschreibung eines Pfads korrigieren)
- [ ] Build: Profil, zusätzliche Mods und einen Spielstand wählen; ein Verlauf der Läufe zum Vergleichen
- [ ] Symbole für Zutaten und Ergebnisse aus dem Basisspiel oder einer Abhängigkeit
- [ ] Abhängigkeiten auf einen Blick: erforderlich, optional, inkompatibel, und welche davon in `modsSource` verfügbar sind

**0.4: Release-Ablauf**
- [ ] Version erhöhen und Changelog-Eintrag in einem Schritt (`fmstudio bump patch|minor|major`)
- [ ] Upload auf das Factorio-Mod-Portal (optional, mit Ihrem eigenen API-Schlüssel)
- [ ] ein fertiger GitHub-Actions-Workflow (`fmstudio init --ci`): bei jedem Push prüfen, packen und smoke-testen

**Später**
- die endgültigen Prototypen über `--dump-data`: „Welche Mod hat diesen Gegenstand geändert, und wie?“
- Tests, die für [FactorioTest](https://mods.factorio.com/mod/factorio-test) geschrieben sind, aus dem Studio starten
- eine Editor-Erweiterung (VS Code) auf derselben API

## Warum

Lua bearbeiten, debuggen und Unit-Tests sind schon gut abgedeckt (zum Beispiel durch das *Factorio Modding Tool Kit* für VS Code und
*FactorioTest*). Mühsam bleibt der Kitt dazwischen: ein korrektes Release-Zip, ein reproduzierbarer Mod-Satz zum Starten, die Suche
nach dem einen echten Fehler in einer Wand aus Log-Ausgaben, Übersetzungen, die auseinanderlaufen. Factorio Mod Studio übernimmt diesen Kitt.

## Schnellstart

Sie brauchen das [.NET 8 SDK](https://dotnet.microsoft.com/download).

```sh
# 1. Das Werkzeug `fmstudio` installieren (einmalig)
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg

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

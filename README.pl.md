# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · **Polski** · [Français](README.fr.md) · [Español](README.es.md)

**Lokalny warsztat dla autorów modów do [Factorio](https://factorio.com).** Utwórz moda, wyłap jego błędy, zanim zrobi to gra,
przetłumacz go, spakuj czyste wydanie i uruchom z odizolowanym zestawem modów: we własnym oknie aplikacji, z wiersza poleceń albo w przeglądarce.
Wszystko działa na Twoim komputerze, obok Twojego ulubionego edytora kodu.

> **Wczesna wersja (0.2).** Logika jest pokryta testami; `smoke` sprawdzono z Factorio 1.1, 2.0 i 2.1 na Linuksie (CI), a `run` z 2.1 na Windows.
> macOS nie został jeszcze sprawdzony. Opinie i zgłoszenia błędów są bardzo mile widziane.
> Aplikacja okienkowa działa na Windows; dla Linuksa i macOS jest zbudowana, ale jeszcze niesprawdzona.
> Nieoficjalny projekt społeczności, niezwiązany z Wube Software. „Factorio” jest znakiem towarowym Wube Software Ltd.
>
> To tłumaczenie części wprowadzającej. Pełna dokumentacja jest [po angielsku](README.md#documentation); w razie rozbieżności obowiązuje angielskie README.

![The Overview tab: release readiness, counts and the prototypes a mod changes](docs/images/overview.png)

| Files | Localization |
| --- | --- |
| ![The Files tab: the file tree with entry points and unused files](docs/images/files.png) | ![The Localization tab: progress per language and the side-by-side editor](docs/images/localization.png) |

| Prototypes | Resources |
| --- | --- |
| ![The Prototypes tab: everything the mod defines, by type, with icons and where it is defined](docs/images/prototypes.png) | ![The Resources tab: graphics and sounds by folder, with previews and the prototypes that use them](docs/images/resources.png) |

## Co potrafi

- **Mod w kilka sekund.** Szablony (pusty, przedmiot + receptura, obiekt, skrypt czasu działania) dla Factorio 2.0 i 2.1 (wszystkie poza obiektem także dla 1.1), które od razu przechodzą każdą kontrolę.
- **Błędy wcześniej niż w grze.** `info.json`, changelog, miniatura, zepsute ścieżki `__mod__/` i `require`, brakujące lub błędne tłumaczenia: wszystko z plikiem i wierszem.
- **Wgląd w dowolny mod.** Studio we własnym oknie lub w przeglądarce, z drzewem plików, edytorem Lua ze sprawdzaniem na bieżąco, prototypami i recepturami moda, jego grafiką i dźwiękami oraz plikami, których nic nie używa.
- **Mody bez niespodzianek.** Dodaj moda albo cały folder modów, np. folder `mods` z Factorio: wszystko jest sprawdzane, zanim cokolwiek zostanie skopiowane.
- **Wygodne tłumaczenie.** Edytor „oryginał obok tłumaczenia” z postępem dla każdego języka, sprawdzaniem placeholderów i podpowiedziami z Twoich wcześniejszych tłumaczeń.
- **Czyste wydanie.** `NAME_VERSION.zip` z poprawną strukturą folderów, budowane tylko wtedy, gdy mod przechodzi kontrolę.
- **Testy bez ruszania Twojej gry.** Uruchom Factorio albo bezgłowy test dymny z odizolowanym zestawem modów dla każdego profilu; Twój zwykły folder modów pozostaje nietknięty. Działa w CI.
- **Szybsze czytanie logów.** Log Factorio skrócony do błędów i ostrzeżeń, duplikaty scalone, każdy z plikiem, wierszem i wskazówką.

## Plan rozwoju

Gdzie projekt jest teraz i co dalej. Wersje to cele, a nie terminy; kolejność wyznaczają opinie, więc
[otwórz zgłoszenie](https://github.com/4a11/factorio-mod-studio/issues), jeśli jakiś punkt jest dla Ciebie ważny (albo go tu brakuje).

**0.1.** Wiersz poleceń (`new`, `add`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) i studio — we własnym oknie lub w przeglądarce, w 8 językach interfejsu: zakładki Przegląd, Pliki, Prototypy, Zasoby, Lokalizacja, Problemy i Budowanie; 20 bloków (pancerz, wrogowie, poziomy taśmociągów, maszyny, pojazdy…) jako przemalowane kopie oryginałów, sprawdzone w Factorio 2.1; całe foldery modów dodawane po wcześniejszym sprawdzeniu.

**0.2: teraz, gotowe dla ludzi**
- [x] aplikacja okienkowa wszędzie: w archiwach wydania dla Windows, Linux i macOS, w każdym z czymś do podwójnego kliknięcia, oraz jeden program razem z wierszem poleceń (podwójne kliknięcie otwiera okno); *sprawdzona na Windows; zbudowana dla Linuksa i macOS, opinie mile widziane*
- [x] `run` i `smoke` sprawdzone z Factorio 1.1, 2.0 i 2.1, łącznie z serwerem bez interfejsu; *`run` z 2.1 na Windows, `smoke` na linuksowym serwerze bez interfejsu dla 1.1, 2.0 i 2.1 w CI; macOS jeszcze niesprawdzony*
- [x] po uruchomieniu lub teście dymnym odczytywany jest także `factorio-current.log`, a jego błędy widać w zakładkach Problemy i Wyjście
- [x] prototypy tworzone przez szablony (obiekt, skrypt czasu działania) wczytane w grze, a nie tylko napisane według dokumentacji; *wszystkie cztery wczytują się w Factorio 2.1*
- [x] instalacja jednym poleceniem: narzędzie opublikowane w NuGet (`dotnet tool install -g FactorioModStudio.Cli`), bez klonowania; *opublikowano: 0.2.0*
- [x] wersje Factorio instalowane przez studio: `fmstudio factorio install 2.0` pobiera darmowy serwer bez interfejsu (Linux, WSL, CI), gdzie znajdzie go `smoke`, także z zakładki Budowanie; *prawdziwe pobieranie 1.1, 2.0 i 2.1 sprawdzone testami dymnymi w CI*

**0.3: widzieć i edytować zawartość**
- [x] zakładka Prototypy na podstawie danych samej gry (`--dump-data`): co mod naprawdę tworzy i zmienia, ze wszystkimi wartościami, także dla kopii przez `table.deepcopy`; „który mod zmienił ten przedmiot i na co?”; *w 0.2.1 (wersja zapoznawcza)*
- [x] panel właściwości bloku: zdrowie, prędkość, ekwipunek, obrażenia… jako pola „oryginał → twoja wartość”, zapisywane z powrotem w Lua bloku; *w 0.2.1 (wersja zapoznawcza)*
- [ ] szybka próba w grze: jeden przycisk uruchamia Factorio na mapie testowej, a nowości są już w ekwipunku
- [x] bezpieczna zmiana nazwy i usuwanie bloku: plik, require, klucze tłumaczeń, ikony i wszystkie odwołania zmieniają się razem; *w 0.2.1 (wersja zapoznawcza)*
- [ ] kolor bloku wybierany suwakiem, z podglądem przemalowanej ikony przed dodaniem
- [ ] tłumaczenia nowych bloków: ich klucze trafiają do wszystkich języków moda, oznaczone jako nieprzetłumaczone w zakładce Lokalizacja

**0.4: wygodny edytor**
- [ ] podpowiedzi Lua w edytorze: uzupełnianie, dokumentacja po najechaniu i pomoc dla parametrów API Factorio (`data.raw`, pola prototypów, `game`, `script`, `defines`, zdarzenia), czytane z opisu API dołączonego do zainstalowanej gry (`doc-html/*-api.json`), więc zgodne z jej wersją
- [ ] Problemy w nowym stylu: grupowanie według pliku i reguły, bezpieczne szybkie poprawki (dodanie brakującego klucza tłumaczenia, poprawienie wielkości liter w ścieżce)
- [ ] ikony składników i produktów pochodzących z gry podstawowej lub zależności
- [ ] zależności w jednym miejscu: wymagane, opcjonalne, niezgodne i które z nich są dostępne w `modsSource`
- [ ] edytor receptur i technologii: składniki wybierane z ikonami z gry i zależności oraz technologia odblokowująca recepturę

**0.5: proces wydawania**
- [ ] podbicie wersji i wpis do changelogu w jednym kroku (`fmstudio bump patch|minor|major`)
- [ ] wysyłanie na portal modów Factorio (opcjonalnie, z Twoim własnym kluczem API)
- [ ] gotowy workflow GitHub Actions (`fmstudio init --ci`): sprawdzanie, pakowanie i test dymny przy każdym pushu
- [ ] Budowanie: wybór profilu, dodatkowych modów i zapisu gry; historia uruchomień do porównania

**0.6: grafika**
- [ ] edytor sprite'ów w studiu: tworzenie i edycja ikon i sprite'ów oraz animacje (klatki, arkusz sprite'ów, podgląd na żywo) bez wychodzenia ze studia

**0.7: światy**
- [ ] edytor map: nowe planety i powierzchnie, biomy (jakie kafelki, dekoracje, surowce i wrogowie pojawiają się gdzie, przez autoplace i wyrażenia szumu) oraz ustawienia generowania mapy, z podglądem wygenerowanego terenu

**Później**
- uruchamianie testów napisanych dla [FactorioTest](https://mods.factorio.com/mod/factorio-test) bezpośrednio ze studia
- rozszerzenie edytora (VS Code) na tym samym API

## Dlaczego

Edycja Lua, debugowanie i testy jednostkowe są już dobrze obsłużone (na przykład przez *Factorio Modding Tool Kit* dla VS Code i
*FactorioTest*). Żmudne pozostaje to, co je spaja: poprawny zip wydania, powtarzalny zestaw modów do uruchomienia, ten jeden
prawdziwy błąd w ścianie logów, tłumaczenia, które się rozjeżdżają. Factorio Mod Studio zajmuje się właśnie tym.

## Szybki start

**Aplikacja okienkowa:** pobierz archiwum dla swojego systemu ze strony [Releases](https://github.com/4a11/factorio-mod-studio/releases/latest) i rozpakuj je; .NET nie jest potrzebny. Kliknij dwukrotnie `FactorioModStudio.exe` (Windows), `Factorio Mod Studio.app` (macOS) lub `Factorio Mod Studio.desktop` (Linux): studio otworzy się we własnym oknie, z folderem `mods` obok na twoje mody. Czego Linux i macOS potrzebują wcześniej, opisują informacje o wydaniu.

**Ze źródeł** (wymaga [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```sh
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet run --project src/FactorioModStudio.Desktop -c Release   # Twoje mody trafiają do ./mods
```

**Jako narzędzie .NET** (wymaga [.NET 10 SDK](https://dotnet.microsoft.com/download)):

```sh
# 1. Zainstaluj narzędzie `fmstudio` (jednorazowo)
dotnet tool install --global FactorioModStudio.Cli

# 2. Utwórz moda w dowolnym folderze i otwórz studio
cd ~/factorio-dev
fmstudio new my-mod --template item-recipe   # gotowy do uruchomienia mod w ./mods/my-mod
fmstudio ui                                  # wszystkie mody z ./mods w przeglądarce
```

Typowy cykl: przyciski na górze każdego moda w przeglądarce albo to samo w terminalu z folderu moda:

```sh
cd mods/my-mod
fmstudio validate      # co jest nie tak, z plikiem i wierszem
fmstudio pack          # dist/my-mod_0.1.0.zip
fmstudio smoke         # wczytaj bez interfejsu na 600 ticków; przerywa się przy dowolnym błędzie
fmstudio run           # uruchom Factorio tylko z tym modem i jego zależnościami
```

Factorio jest wyszukiwane automatycznie (typowe foldery instalacji, `FACTORIO_PATH` albo instalacje dodane w interfejsie); używana jest
wersja zgodna z `factorio_version` moda.

## Dokumentacja

Pełna dokumentacja (polecenia, konfiguracja, zakładki studia, reguły kontroli, API) jest [po angielsku](README.md#documentation).

## Licencja

[MIT](LICENSE)

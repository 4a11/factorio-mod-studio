# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · **Polski** · [Français](README.fr.md) · [Español](README.es.md)

**Lokalny warsztat dla autorów modów do [Factorio](https://factorio.com).** Utwórz moda, wyłap jego błędy, zanim zrobi to gra,
przetłumacz go, spakuj czyste wydanie i uruchom z odizolowanym zestawem modów: z wiersza poleceń albo ze strony w przeglądarce.
Wszystko działa na Twoim komputerze, obok Twojego ulubionego edytora kodu.

> **Wczesna wersja (0.1).** Logika jest pokryta testami, ale uruchamianie prawdziwej gry (`run`, `smoke`) nie zostało jeszcze
> sprawdzone z Factorio. Opinie i zgłoszenia błędów są bardzo mile widziane.
> Nieoficjalny projekt społeczności, niezwiązany z Wube Software. „Factorio” jest znakiem towarowym Wube Software Ltd.
>
> To tłumaczenie części wprowadzającej. Pełna dokumentacja jest [po angielsku](README.md#documentation); w razie rozbieżności obowiązuje angielskie README.

## Co potrafi

- **Mod w kilka sekund.** Szablony (pusty, przedmiot + receptura, obiekt, skrypt czasu działania) dla Factorio 2.0 i 2.1 (wszystkie poza obiektem także dla 1.1), które od razu przechodzą każdą kontrolę.
- **Błędy wcześniej niż w grze.** `info.json`, changelog, miniatura, zepsute ścieżki `__mod__/` i `require`, brakujące lub błędne tłumaczenia: wszystko z plikiem i wierszem.
- **Wgląd w dowolny mod.** Studio w przeglądarce z drzewem plików, edytorem Lua ze sprawdzaniem na bieżąco, prototypami i recepturami moda, jego grafiką i dźwiękami oraz plikami, których nic nie używa.
- **Wygodne tłumaczenie.** Edytor „oryginał obok tłumaczenia” z postępem dla każdego języka, sprawdzaniem placeholderów i podpowiedziami z Twoich wcześniejszych tłumaczeń.
- **Czyste wydanie.** `NAME_VERSION.zip` z poprawną strukturą folderów, budowane tylko wtedy, gdy mod przechodzi kontrolę.
- **Testy bez ruszania Twojej gry.** Uruchom Factorio albo bezgłowy test dymny z odizolowanym zestawem modów dla każdego profilu; Twój zwykły folder modów pozostaje nietknięty. Działa w CI.
- **Szybsze czytanie logów.** Log Factorio skrócony do błędów i ostrzeżeń, duplikaty scalone, każdy z plikiem, wierszem i wskazówką.

## Plan rozwoju

Gdzie projekt jest teraz i co dalej. Wersje to cele, a nie terminy; kolejność wyznaczają opinie, więc
[otwórz zgłoszenie](https://github.com/4a11/factorio-mod-studio/issues), jeśli jakiś punkt jest dla Ciebie ważny (albo go tu brakuje).

**0.1: teraz.** Wiersz poleceń (`new`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) i studio w przeglądarce
(Przegląd, Pliki, Prototypy, Zasoby, Lokalizacja, Problemy, Budowanie) w 8 językach interfejsu.

**0.2: sprawdzone na prawdziwej grze**
- [ ] `run` i `smoke` sprawdzone z Factorio 1.1, 2.0 i 2.1 na Windows, Linux i macOS, łącznie z serwerem bez interfejsu
- [ ] po uruchomieniu lub teście dymnym odczytywany jest także `factorio-current.log`, a jego błędy widać w zakładkach Problemy i Wyjście
- [ ] prototypy tworzone przez szablony (obiekt, skrypt czasu działania) wczytane w grze, a nie tylko napisane według dokumentacji
- [ ] instalacja jednym poleceniem: narzędzie opublikowane w NuGet (`dotnet tool install -g FactorioModStudio.Cli`), bez klonowania

**0.3: pozostałe zakładki**
- [ ] Problemy w nowym stylu: grupowanie według pliku i reguły, bezpieczne szybkie poprawki (dodanie brakującego klucza tłumaczenia, poprawienie wielkości liter w ścieżce)
- [ ] Budowanie: wybór profilu, dodatkowych modów i zapisu gry; historia uruchomień do porównania
- [ ] ikony składników i produktów pochodzących z gry podstawowej lub zależności
- [ ] zależności w jednym miejscu: wymagane, opcjonalne, niezgodne i które z nich są dostępne w `modsSource`

**0.4: proces wydawania**
- [ ] podbicie wersji i wpis do changelogu w jednym kroku (`fmstudio bump patch|minor|major`)
- [ ] wysyłanie na portal modów Factorio (opcjonalnie, z Twoim własnym kluczem API)
- [ ] gotowy workflow GitHub Actions (`fmstudio init --ci`): sprawdzanie, pakowanie i test dymny przy każdym pushu

**Później**
- końcowe prototypy przez `--dump-data`: „który mod zmienił ten przedmiot i na co?”
- uruchamianie testów napisanych dla [FactorioTest](https://mods.factorio.com/mod/factorio-test) bezpośrednio ze studia
- rozszerzenie edytora (VS Code) na tym samym API

## Dlaczego

Edycja Lua, debugowanie i testy jednostkowe są już dobrze obsłużone (na przykład przez *Factorio Modding Tool Kit* dla VS Code i
*FactorioTest*). Żmudne pozostaje to, co je spaja: poprawny zip wydania, powtarzalny zestaw modów do uruchomienia, ten jeden
prawdziwy błąd w ścianie logów, tłumaczenia, które się rozjeżdżają. Factorio Mod Studio zajmuje się właśnie tym.

## Szybki start

Potrzebujesz [.NET 8 SDK](https://dotnet.microsoft.com/download).

```sh
# 1. Zainstaluj narzędzie `fmstudio` (jednorazowo)
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg

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

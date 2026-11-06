# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · [Français](README.fr.md) · **Español**

**Un taller local para quienes crean mods de [Factorio](https://factorio.com).** Crea un mod, encuentra sus errores antes que el
juego, tradúcelo, empaqueta una versión limpia y pruébalo con un conjunto de mods aislado: desde la línea de comandos o desde una
página en tu navegador. Todo funciona en tu equipo, junto a tu editor de código habitual.

> **Versión temprana (0.1).** La lógica está cubierta por pruebas; el lanzamiento del juego real (`run`, `smoke`) funciona con Factorio 2.1 en
> Windows. Otras versiones y sistemas aún no se han probado. Los comentarios y los informes de errores son muy bienvenidos.
> Proyecto comunitario no oficial, sin relación con Wube Software. «Factorio» es una marca de Wube Software Ltd.
>
> Esta es una traducción de la parte introductoria. La documentación completa está [en inglés](README.md#documentation); en caso de discrepancia, prevalece el README en inglés.

## Qué hace

- **Un mod en segundos.** Plantillas (vacía, objeto + receta, entidad, script en tiempo de ejecución) para Factorio 2.0 y 2.1 (todas salvo la de entidad también para 1.1) que pasan todas las comprobaciones desde el principio.
- **Errores antes que el juego.** `info.json`, changelog, miniatura, rutas `__mod__/` y `require` rotas, traducciones que faltan o están mal: todo con archivo y línea.
- **Ver por dentro cualquier mod.** Un estudio en el navegador con el árbol de archivos, un editor de Lua con comprobaciones al vuelo, los prototipos y recetas del mod, sus gráficos y sonidos, y los archivos que nada usa.
- **Traducir con comodidad.** Editor con original y traducción lado a lado, progreso por idioma, comprobación de marcadores y sugerencias de tus traducciones anteriores.
- **Una versión limpia.** `NAME_VERSION.zip` con la estructura de carpetas correcta, que solo se genera si el mod pasa la comprobación.
- **Probar sin tocar tu juego.** Lanza Factorio o una prueba de humo sin interfaz con un conjunto de mods aislado por perfil; tu carpeta de mods habitual nunca se toca. Funciona en CI.
- **Leer los logs más rápido.** Un log de Factorio resumido a sus errores y advertencias, con duplicados unidos, cada uno con archivo, línea y una pista.

## Hoja de ruta

Dónde está el proyecto y qué viene después. Las versiones son objetivos, no fechas; el orden lo marcan los comentarios, así que
[abre una issue](https://github.com/4a11/factorio-mod-studio/issues) si algún punto te importa (o si falta aquí).

**0.1: ahora.** La línea de comandos (`new`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) y el estudio en el
navegador (Resumen, Archivos, Prototipos, Recursos, Localización, Problemas, Compilación) en 8 idiomas de interfaz.

**0.2: probado con el juego real**
- [ ] `run` y `smoke` verificados con Factorio 1.1, 2.0 y 2.1 en Windows, Linux y macOS, incluido el servidor sin interfaz; *2.1 en Windows funciona*
- [ ] tras un lanzamiento o una prueba de humo, leer también `factorio-current.log` y mostrar sus errores en Problemas y Salida
- [ ] los prototipos que generan las plantillas (entidad, script en tiempo de ejecución) cargados en el juego, no solo escritos según la documentación
- [ ] instalación en una línea: la herramienta publicada en NuGet (`dotnet tool install -g FactorioModStudio.Cli`), sin clonar
- [ ] versiones de Factorio instaladas por el estudio: `fmstudio factorio install 2.0` descarga el servidor sin interfaz gratuito (Linux, WSL, CI), donde `smoke` lo encuentra, también desde la pestaña Compilación; *hecho, aún sin probar con la descarga real*

**0.3: las pestañas restantes**
- [ ] Problemas con el nuevo estilo: agrupados por archivo y regla, correcciones rápidas seguras (añadir una clave de traducción que falta, corregir mayúsculas y minúsculas de una ruta)
- [ ] Compilación: elegir el perfil, mods adicionales y una partida guardada; un historial de ejecuciones para comparar
- [ ] iconos de ingredientes y resultados que vienen del juego base o de una dependencia
- [ ] dependencias de un vistazo: obligatorias, opcionales, incompatibles y cuáles están disponibles en `modsSource`

**0.4: flujo de publicación**
- [ ] subir la versión y añadir la entrada del changelog en un solo paso (`fmstudio bump patch|minor|major`)
- [ ] subida al portal de mods de Factorio (opcional, con tu propia clave de API)
- [ ] un workflow de GitHub Actions listo (`fmstudio init --ci`): comprobar, empaquetar y hacer la prueba de humo en cada push

**Más adelante**
- los prototipos finales mediante `--dump-data`: «¿qué mod cambió este objeto, y cómo?»
- ejecutar desde el estudio las pruebas escritas para [FactorioTest](https://mods.factorio.com/mod/factorio-test)
- una extensión de editor (VS Code) sobre la misma API

## Por qué

Editar Lua, depurar y las pruebas unitarias ya están bien cubiertos (por ejemplo, con *Factorio Modding Tool Kit* para VS Code y
*FactorioTest*). Lo que sigue siendo tedioso es el pegamento entre ellos: un zip de versión correcto, un conjunto de mods reproducible
para lanzar, el único error real en un muro de log, traducciones que se desincronizan. Factorio Mod Studio se encarga de ese pegamento.

## Inicio rápido

Necesitas el [SDK de .NET 8](https://dotnet.microsoft.com/download).

```sh
# 1. Instalar la herramienta `fmstudio` (una vez)
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet pack src/FactorioModStudio.Cli -c Release -o ./nupkg
dotnet tool install --global FactorioModStudio.Cli --add-source ./nupkg

# 2. Crear un mod en la carpeta que quieras y abrir el estudio
cd ~/factorio-dev
fmstudio new my-mod --template item-recipe   # un mod listo para lanzar en ./mods/my-mod
fmstudio ui                                  # todos los mods de ./mods, en tu navegador
```

Un ciclo típico: los botones en la parte superior de cada mod en el navegador, o lo mismo en la terminal desde la carpeta del mod:

```sh
cd mods/my-mod
fmstudio validate      # qué está mal, con archivo y línea
fmstudio pack          # dist/my-mod_0.1.0.zip
fmstudio smoke         # cargar sin interfaz durante 600 ticks; falla ante cualquier error
fmstudio run           # lanzar Factorio solo con este mod y sus dependencias
```

Factorio se encuentra automáticamente (las carpetas de instalación habituales, `FACTORIO_PATH` o las instalaciones añadidas en la
interfaz); se usa la versión que coincide con el `factorio_version` del mod.

## Documentación

La documentación completa (comandos, configuración, pestañas del estudio, reglas de comprobación, API) está [en inglés](README.md#documentation).

## Licencia

[MIT](LICENSE)

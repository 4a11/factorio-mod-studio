# Factorio Mod Studio

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [简体中文](README.zh-CN.md) · [Українська](README.uk.md) · [Polski](README.pl.md) · [Français](README.fr.md) · **Español**

**Un taller local para quienes crean mods de [Factorio](https://factorio.com).** Crea un mod, encuentra sus errores antes que el
juego, tradúcelo, empaqueta una versión limpia y pruébalo con un conjunto de mods aislado: en una ventana propia, desde la línea de
comandos o en tu navegador. Todo funciona en tu equipo, junto a tu editor de código habitual.

> **Versión temprana (0.2).** La lógica está cubierta por pruebas; `smoke` está verificado con Factorio 1.1, 2.0 y 2.1 en Linux (CI), y `run` con 2.1
> en Windows. macOS aún no se ha probado. Los comentarios y los informes de errores son muy bienvenidos.
> La aplicación de escritorio funciona en Windows; para Linux y macOS está compilada, pero aún no se ha probado.
> Proyecto comunitario no oficial, sin relación con Wube Software. «Factorio» es una marca de Wube Software Ltd.
>
> Esta es una traducción de la parte introductoria. La documentación completa está [en inglés](README.md#documentation); en caso de discrepancia, prevalece el README en inglés.

![The Overview tab: release readiness, counts and the prototypes a mod changes](docs/images/overview.png)

| Files | Localization |
| --- | --- |
| ![The Files tab: the file tree with entry points and unused files](docs/images/files.png) | ![The Localization tab: progress per language and the side-by-side editor](docs/images/localization.png) |

## Qué hace

- **Un mod en segundos.** Plantillas (vacía, objeto + receta, entidad, script en tiempo de ejecución) para Factorio 2.0 y 2.1 (todas salvo la de entidad también para 1.1) que pasan todas las comprobaciones desde el principio.
- **Errores antes que el juego.** `info.json`, changelog, miniatura, rutas `__mod__/` y `require` rotas, traducciones que faltan o están mal: todo con archivo y línea.
- **Ver por dentro cualquier mod.** El estudio, en una ventana propia o en el navegador, con el árbol de archivos, un editor de Lua con comprobaciones al vuelo, los prototipos y recetas del mod, sus gráficos y sonidos, y los archivos que nada usa.
- **Añadir mods sin sorpresas.** Añade un mod o una carpeta entera de mods, como la carpeta `mods` de Factorio: todo se comprueba antes de copiar nada.
- **Traducir con comodidad.** Editor con original y traducción lado a lado, progreso por idioma, comprobación de marcadores y sugerencias de tus traducciones anteriores.
- **Una versión limpia.** `NAME_VERSION.zip` con la estructura de carpetas correcta, que solo se genera si el mod pasa la comprobación.
- **Probar sin tocar tu juego.** Lanza Factorio o una prueba de humo sin interfaz con un conjunto de mods aislado por perfil; tu carpeta de mods habitual nunca se toca. Funciona en CI.
- **Leer los logs más rápido.** Un log de Factorio resumido a sus errores y advertencias, con duplicados unidos, cada uno con archivo, línea y una pista.

## Hoja de ruta

Dónde está el proyecto y qué viene después. Las versiones son objetivos, no fechas; el orden lo marcan los comentarios, así que
[abre una issue](https://github.com/4a11/factorio-mod-studio/issues) si algún punto te importa (o si falta aquí).

**0.1.** La línea de comandos (`new`, `add`, `validate`, `pack`, `prepare` / `run`, `smoke`, `log`, `factorio`) y el estudio, en una ventana propia o en el navegador, en 8 idiomas de interfaz: pestañas Resumen, Archivos, Prototipos, Recursos, Localización, Problemas y Compilación; 20 bloques (armadura, enemigos, niveles de cintas, máquinas, vehículos…) como copias recoloreadas de los originales, comprobados en Factorio 2.1; carpetas enteras de mods añadidas tras una comprobación.

**0.2: ahora, listo para la gente**
- [x] la aplicación de escritorio en todas partes: en los archivos de la versión para Windows, Linux y macOS, cada uno con algo a lo que hacer doble clic, y un solo programa junto con la línea de comandos (un doble clic abre la ventana); *probada en Windows; compilada para Linux y macOS, se agradecen los comentarios*
- [x] `run` y `smoke` verificados con Factorio 1.1, 2.0 y 2.1, incluido el servidor sin interfaz; *`run` con 2.1 en Windows, `smoke` en el servidor sin interfaz de Linux para 1.1, 2.0 y 2.1 en la CI; macOS aún sin probar*
- [x] tras un lanzamiento o una prueba de humo, leer también `factorio-current.log` y mostrar sus errores en Problemas y Salida
- [x] los prototipos que generan las plantillas (entidad, script en tiempo de ejecución) cargados en el juego, no solo escritos según la documentación; *los cuatro cargan en Factorio 2.1*
- [x] instalación en una línea: la herramienta publicada en NuGet (`dotnet tool install -g FactorioModStudio.Cli`), sin clonar; *publicado: 0.1.1*
- [x] versiones de Factorio instaladas por el estudio: `fmstudio factorio install 2.0` descarga el servidor sin interfaz gratuito (Linux, WSL, CI), donde `smoke` lo encuentra, también desde la pestaña Compilación; *la descarga real de 1.1, 2.0 y 2.1 probada con pruebas de humo en la CI*

**0.3: ver y editar el contenido**
- [ ] la pestaña Prototipos con los datos del propio juego (`--dump-data`): lo que el mod crea y cambia de verdad, con todos los valores, también en copias hechas con `table.deepcopy`; «¿qué mod cambió este objeto, y cómo?»
- [ ] un panel de propiedades para los bloques: vida, velocidad, inventario, daño… como campos «original → tu valor» que se escriben de vuelta en el Lua del bloque
- [ ] probar rápido en el juego: un botón abre Factorio en un mapa de prueba con lo nuevo ya en el inventario
- [ ] renombrar o quitar un bloque con seguridad: el archivo, el require, las claves de traducción, los iconos y todas las referencias cambian a la vez
- [ ] el color de un bloque elegido con un control deslizante, con vista previa del icono recoloreado antes de añadirlo
- [ ] traducciones de los bloques nuevos: sus claves se añaden a todos los idiomas del mod, marcadas como sin traducir en la pestaña Localización

**0.4: un editor cómodo**
- [ ] ayudas de Lua en el editor: autocompletado, documentación al pasar el ratón y ayuda de parámetros para la API de Factorio (`data.raw`, campos de prototipos, `game`, `script`, `defines`, eventos), leídas de la descripción de la API que trae el juego instalado (`doc-html/*-api.json`), así que coinciden con su versión
- [ ] Problemas con el nuevo estilo: agrupados por archivo y regla, correcciones rápidas seguras (añadir una clave de traducción que falta, corregir mayúsculas y minúsculas de una ruta)
- [ ] iconos de ingredientes y resultados que vienen del juego base o de una dependencia
- [ ] dependencias de un vistazo: obligatorias, opcionales, incompatibles y cuáles están disponibles en `modsSource`
- [ ] un editor de recetas y tecnologías: ingredientes elegidos con sus iconos del juego y de las dependencias, y la tecnología que desbloquea la receta

**0.5: flujo de publicación**
- [ ] subir la versión y añadir la entrada del changelog en un solo paso (`fmstudio bump patch|minor|major`)
- [ ] subida al portal de mods de Factorio (opcional, con tu propia clave de API)
- [ ] un workflow de GitHub Actions listo (`fmstudio init --ci`): comprobar, empaquetar y hacer la prueba de humo en cada push
- [ ] Compilación: elegir el perfil, mods adicionales y una partida guardada; un historial de ejecuciones para comparar

**0.6: gráficos**
- [ ] un editor de sprites en el estudio: crear y editar iconos y sprites, y montar animaciones (fotogramas, hoja de sprites, vista previa en vivo) sin salir del estudio

**0.7: mundos**
- [ ] un editor de mapas: nuevos planetas y superficies, biomas (qué casillas, decoraciones, recursos y enemigos aparecen y dónde, mediante autoplace y expresiones de ruido) y ajustes de generación del mapa, con una vista previa del terreno generado

**Más adelante**
- ejecutar desde el estudio las pruebas escritas para [FactorioTest](https://mods.factorio.com/mod/factorio-test)
- una extensión de editor (VS Code) sobre la misma API

## Por qué

Editar Lua, depurar y las pruebas unitarias ya están bien cubiertos (por ejemplo, con *Factorio Modding Tool Kit* para VS Code y
*FactorioTest*). Lo que sigue siendo tedioso es el pegamento entre ellos: un zip de versión correcto, un conjunto de mods reproducible
para lanzar, el único error real en un muro de log, traducciones que se desincronizan. Factorio Mod Studio se encarga de ese pegamento.

## Inicio rápido

**Aplicación de escritorio:** descarga el archivo para tu sistema desde [Releases](https://github.com/4a11/factorio-mod-studio/releases/latest) y descomprímelo; no hace falta .NET. Haz doble clic en `FactorioModStudio.exe` (Windows), `Factorio Mod Studio.app` (macOS) o `Factorio Mod Studio.desktop` (Linux): el estudio se abre en una ventana propia, con una carpeta `mods` al lado para tus mods. Las notas de la versión dicen qué necesitan antes Linux y macOS.

**Desde el código fuente** (necesita el [SDK de .NET 10](https://dotnet.microsoft.com/download)):

```sh
git clone https://github.com/4a11/factorio-mod-studio
cd factorio-mod-studio
dotnet run --project src/FactorioModStudio.Desktop -c Release   # tus mods van en ./mods
```

**Como herramienta de .NET** (necesita el [SDK de .NET 10](https://dotnet.microsoft.com/download)):

```sh
# 1. Instalar la herramienta `fmstudio` (una vez)
dotnet tool install --global FactorioModStudio.Cli

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

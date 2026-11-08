## Install

Download the archive for your system, unpack it anywhere, done: no .NET needed.

- **Windows** (`win-x64.zip`): double-click `FactorioModStudio.exe` (or `fmstudio.exe`) and the studio opens in a window of its own, with a `mods` folder next to it as the workspace. Windows may warn about an unknown publisher: *More info → Run anyway*.
- **Linux** (`linux-x64.tar.gz`): double-click `Factorio Mod Studio.desktop` in the unpacked folder where the file manager starts desktop entries (KDE, Xfce and others ask once to trust it); GNOME Files opens it as text instead, so there run `./FactorioModStudio` in a terminal in that folder. The window needs the WebKitGTK package (`webkit2gtk-4.1`); without it, it offers the browser instead, as does `./fmstudio ui`.
- **macOS** (`osx-arm64` for Apple Silicon, `osx-x64` for Intel): clear the download quarantine first (`xattr -dr com.apple.quarantine .` in the unpacked folder), then double-click `Factorio Mod Studio.app`, or run `./FactorioModStudio`; `./fmstudio ui` opens the studio in the browser.
- The desktop window is new and has been tried on Windows; reports from Linux and macOS are welcome.
- **With the .NET 10 SDK**: `dotnet tool install -g FactorioModStudio.Cli` installs `fmstudio` from NuGet (the `.nupkg` here is the same package).

Put the unpacked folder on your `PATH` to use `fmstudio validate`, `pack`, `smoke` and the rest from any mod folder.

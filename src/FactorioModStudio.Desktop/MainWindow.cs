using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using FactorioModStudio.Core;
using FactorioModStudio.Server;

namespace FactorioModStudio.Desktop;

/// <summary>
/// The studio in a window of its own: the same web UI the browser shows, served by an in-process server on a free local
/// port and rendered by the system's web engine (WebView2, WebKit, WebKitGTK). Closing the window stops the server.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBlock _status = new() { Text = "Starting Factorio Mod Studio...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private RunningUi? _ui;
    private readonly List<PosixSignalRegistration> _signals = new();

    public MainWindow()
    {
        Title = "Factorio Mod Studio";
        Width = 1400;
        Height = 900;
        MinWidth = 800;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _status;

        // The window and taskbar icon: without one, Linux desktops show a generic placeholder.
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FactorioModStudio/Assets/icon.png"))); }
        catch (Exception ex) when (ex is IOException or FileNotFoundException) { /* the default icon will do */ }

        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
        Closed += (_, _) => { foreach (var signal in _signals) signal.Dispose(); };
        // Ctrl+C in the terminal, `kill`, logging out: close the window like its close button does. Without this the in-process
        // web server's host takes these signals, stops only itself and keeps the program from exiting, leaving a window with a
        // dead page that only `kill -9` ends.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
                _signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(Close);
                }));
        }
        // The system's web engine is created after the window: when it cannot start (Linux without WebKitGTK, a broken
        // WebView2), the error surfaces here instead of ending the program.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            ShowFallback(e.Exception.Message);
        };
    }

    /// <summary>The studio still runs: say why the window cannot show it and offer the browser instead.</summary>
    private void ShowFallback(string reason)
    {
        var url = _ui?.Url;
        var text = "This window cannot show the studio: " + reason + Environment.NewLine + Environment.NewLine +
                   (OperatingSystem.IsLinux() ? LinuxWebKitHelp + Environment.NewLine + Environment.NewLine : "") +
                   (url is null ? "" : "The studio is running and can be opened in your browser: " + url);
        // Also in the terminal, for a start from one (and for bug reports).
        Console.Error.WriteLine(text);
        var panel = new StackPanel { Spacing = 12, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = text });
        if (url is not null)
        {
            var open = new Button { Content = "Open in the browser" };
            open.Click += (_, _) => OpenInBrowser(url);
            panel.Children.Add(open);
        }
        Content = panel;
    }

    /// <summary>The web engine the window uses on Linux (WebKitGTK 4.1, the GTK 3 build).</summary>
    private const string WebKitGtkLibrary = "libwebkit2gtk-4.1.so.0";

    private const string LinuxWebKitHelp =
        "On Linux the window needs WebKitGTK 4.1. Install it and start the studio again:" + "\n" +
        "  Fedora:          sudo dnf install webkit2gtk4.1" + "\n" +
        "  Debian / Ubuntu: sudo apt install libwebkit2gtk-4.1-0" + "\n" +
        "  Arch:            sudo pacman -S webkit2gtk-4.1" + "\n" +
        "  openSUSE:        sudo zypper install libwebkit2gtk-4_1-0";

    private static bool WebKitGtkInstalled()
    {
        if (!NativeLibrary.TryLoad(WebKitGtkLibrary, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else Process.Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no browser either */ }
    }

    private async Task StartAsync()
    {
        try
        {
            // Like `fmstudio ui` started by a double click: the folder the program runs in is the project, its mods folder
            // the workspace.
            var context = ProjectContext.Load(Directory.GetCurrentDirectory());
            Directory.CreateDirectory(context.WorkspaceDirectory);
            var options = new UiOptions(Port: 0, FactorioExecutable: NewestFactorio(context), WorkspaceDirectory: context.WorkspaceDirectory,
                Restart: Restart);
            _ui = await Task.Run(() => UiServer.StartAsync(context, options));
            // Without WebKitGTK the web view does not fail: it shows a bare "NativeWebView is not supported on this
            // platform/configuration." So look for the library first and say what to install.
            if (OperatingSystem.IsLinux() && !WebKitGtkInstalled())
            {
                ShowFallback("the WebKitGTK library (" + WebKitGtkLibrary + ") is not installed.");
                return;
            }
            var view = new NativeWebView();
            view.EnvironmentRequested += (_, e) =>
            {
                // WebView2 keeps its profile next to the program by default, which fails where the program may not write
                // (e.g. Program Files): keep it with the studio's other per-user data instead.
                if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
                    windows.UserDataFolder = Path.Combine(FactorioInstallations.DataDirectory(), "webview");
            };
            view.Source = new Uri(_ui.Url);
            Content = view;
        }
        catch (Exception ex)
        {
            _status.Text = "Factorio Mod Studio could not start:\n" + ex.Message;
        }
    }

    /// <summary>
    /// "Restart now" after an update was downloaded: a fresh copy of this program waits until this window has closed (and stopped
    /// its server), swaps in the new version and starts it (Program.Main). This process never runs on swapped files.
    /// </summary>
    private void Restart()
    {
        var program = Environment.ProcessPath ?? throw new InvalidOperationException("The program's own path is not known.");
        Process.Start(new ProcessStartInfo(program)
        {
            WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false,
            ArgumentList = { "--apply-update", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>Any installation will do for browsing the game data: the newest one, as for `fmstudio ui`.</summary>
    private static string? NewestFactorio(ProjectContext context)
    {
        try { return FactorioInstallations.Pick(FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read()), null).Chosen?.Path; }
        catch (FmsException) { return null; }
    }

    /// <summary>
    /// The window goes away at once; the server (and any Factorio started from the Build tab) is stopped in the background and
    /// the window is closed for real afterwards, which ends the program.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var ui = _ui;
        if (ui is null) return;
        e.Cancel = true;
        _ui = null;
        Hide();
        _ = Task.Run(async () =>
        {
            try { await ui.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* the program ends either way */ }
            Dispatcher.UIThread.Post(Close);
        });
    }
}

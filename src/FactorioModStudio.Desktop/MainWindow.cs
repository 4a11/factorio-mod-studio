using System.Diagnostics;
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

    public MainWindow()
    {
        Title = "Factorio Mod Studio";
        Width = 1400;
        Height = 900;
        MinWidth = 800;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _status;

        Opened += async (_, _) => await StartAsync();
        Closing += OnClosing;
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
        var panel = new StackPanel { Spacing = 12, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "This window cannot show the studio: " + reason + Environment.NewLine + Environment.NewLine +
                   (OperatingSystem.IsLinux() ? "On Linux the window needs the WebKitGTK package (webkit2gtk-4.1). " : "") +
                   (url is null ? "" : "The studio is running and can be opened in your browser.")
        });
        if (url is not null)
        {
            var open = new Button { Content = "Open in the browser" };
            open.Click += (_, _) => OpenInBrowser(url);
            panel.Children.Add(open);
        }
        Content = panel;
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

    /// <summary>After an update: the new program starts in the same folder, then this window closes (and stops its server).</summary>
    private void Restart()
    {
        if (Environment.ProcessPath is { } program)
        {
            try { Process.Start(new ProcessStartInfo(program) { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return; }
        }
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

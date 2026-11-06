using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
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
        Closing += (_, _) => StopServer();
    }

    private async Task StartAsync()
    {
        try
        {
            // Like `fmstudio ui` started by a double click: the folder the program runs in is the project, its mods folder
            // the workspace.
            var context = ProjectContext.Load(Directory.GetCurrentDirectory());
            Directory.CreateDirectory(context.WorkspaceDirectory);
            var options = new UiOptions(Port: 0, FactorioExecutable: NewestFactorio(context), WorkspaceDirectory: context.WorkspaceDirectory);
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

    /// <summary>Any installation will do for browsing the game data: the newest one, as for `fmstudio ui`.</summary>
    private static string? NewestFactorio(ProjectContext context)
    {
        try { return FactorioInstallations.Pick(FactorioInstallations.Candidates(context, new FactorioSettings(context.StateDirectory).Read()), null).Chosen?.Path; }
        catch (FmsException) { return null; }
    }

    private void StopServer()
    {
        var ui = _ui;
        _ui = null;
        if (ui is null) return;
        // Off the UI thread, so a slow shutdown (a running Factorio being stopped) cannot freeze the closing window for long.
        Task.Run(() => ui.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
    }
}

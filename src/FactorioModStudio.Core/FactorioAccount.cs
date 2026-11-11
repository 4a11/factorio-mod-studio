using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FactorioModStudio.Core;

/// <summary>A factorio.com username and its service token, and where they came from: "env", "studio" or "game".</summary>
public sealed record FactorioCredentials(string Username, string Token, string Source)
{
    // Never print the token, not even by accident in a log line or an exception.
    public override string ToString() => $"{Username} ({Source})";
}

/// <summary>What <see cref="FactorioAccount.LoginAsync"/> got: the account, or factorio.com's error code and message.</summary>
/// <param name="Error">"login-failed", "email-authentication-required", "unreachable", "bad-answer", "invalid-input" or another code of factorio.com.</param>
public sealed record FactorioLoginResult(bool Ok, string? Error, string? Message, FactorioCredentials? Credentials)
{
    public static FactorioLoginResult Failed(string error, string message) => new(false, error, message, null);
    public bool NeedsEmailCode => Error == "email-authentication-required";
}

/// <param name="Source">Where the download would take its account from: "env", "studio", "game" or null (none).</param>
/// <param name="Username">That account's username (never the token).</param>
/// <param name="GameUsername">The account the installed Factorio is signed in to, when its player-data.json has one.</param>
/// <param name="UseGame">The user chose "use the account Factorio is signed in to" (rather than a saved token).</param>
public sealed record FactorioAccountStatus(string? Source, string? Username, string? GameUsername, bool UseGame);

/// <summary>
/// The factorio.com account used to download the full game (<see cref="FactorioDownloads.InstallGameAsync"/>): only someone who
/// owns Factorio can. Three places, in this order: <c>FACTORIO_USERNAME</c> + <c>FACTORIO_TOKEN</c> (CI); what the user saved in the
/// studio (<c>&lt;data dir&gt;/factorio-account.json</c>, outside every workspace; on Windows the token is encrypted for the current
/// Windows user with DPAPI, elsewhere the file is readable by its owner only, like Factorio's own player-data.json); or, when the
/// user chose it, the account the installed game is signed in to, read from its player-data.json each time and never copied.
/// The token is only ever sent to factorio.com over HTTPS, and no API returns it.
/// </summary>
public static class FactorioAccount
{
    public const string UsernameVariable = "FACTORIO_USERNAME";
    public const string TokenVariable = "FACTORIO_TOKEN";
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9_.@+\-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new(@"^[A-Za-z0-9]{8,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsUsername(string? text) => text is not null && UsernamePattern.IsMatch(text);
    public static bool IsToken(string? text) => text is not null && TokenPattern.IsMatch(text);

    /// <summary>The studio's own file: <c>&lt;data dir&gt;/factorio-account.json</c>.</summary>
    public static string StorePath() => Path.Combine(FactorioInstallations.DataDirectory(), "factorio-account.json");

    /// <summary>Where the installed game keeps its account (player-data.json in Factorio's user data folder).</summary>
    public static string GamePlayerDataPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Factorio", "player-data.json");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "factorio", "player-data.json")
            : Path.Combine(home, ".factorio", "player-data.json");
    }

    /// <summary>The account in the game's player-data.json ("service-username" / "service-token"), or null.</summary>
    public static FactorioCredentials? FromGame(string? playerDataPath = null)
    {
        var root = ReadObject(playerDataPath ?? GamePlayerDataPath());
        if (root is null) return null;
        var username = Text(root, "service-username");
        var token = Text(root, "service-token");
        return IsUsername(username) && IsToken(token) ? new FactorioCredentials(username!, token!, "game") : null;
    }

    /// <summary>The account a download uses (see the class summary), or null when there is none.</summary>
    public static FactorioCredentials? Resolve(string? storePath = null, string? playerDataPath = null, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var envUser = environment(UsernameVariable);
        var envToken = environment(TokenVariable);
        if (IsUsername(envUser) && IsToken(envToken)) return new FactorioCredentials(envUser!, envToken!, "env");

        var saved = ReadObject(storePath ?? StorePath());
        if (saved is null) return null;
        if (saved["useGame"] is JsonValue flag && flag.TryGetValue<bool>(out var useGame) && useGame) return FromGame(playerDataPath);

        var username = Text(saved, "username");
        var protectedToken = Text(saved, "token");
        if (!IsUsername(username) || protectedToken is null) return null;
        var token = Unprotect(protectedToken, Text(saved, "protection"));
        return IsToken(token) ? new FactorioCredentials(username!, token!, "studio") : null;
    }

    /// <summary>What the Build tab shows: which account would be used, and whether the game is signed in.</summary>
    public static FactorioAccountStatus Status(string? storePath = null, string? playerDataPath = null, Func<string, string?>? environment = null)
    {
        var active = Resolve(storePath, playerDataPath, environment);
        var saved = ReadObject(storePath ?? StorePath());
        var useGame = saved?["useGame"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        return new FactorioAccountStatus(active?.Source, active?.Username, FromGame(playerDataPath)?.Username, useGame);
    }

    /// <summary>Saves a username and token typed by the user (the token protected, see the class summary).</summary>
    public static void Save(string username, string token, string? storePath = null)
    {
        if (!IsUsername(username)) throw new FmsException("The username may contain letters, digits and _ . @ + - (at most 100 characters).");
        if (!IsToken(token)) throw new FmsException("The token is the line of letters and digits from your profile on factorio.com (8 to 128 characters).");
        var (text, protection) = Protect(token);
        Write(storePath ?? StorePath(), new JsonObject { ["username"] = username, ["token"] = text, ["protection"] = protection });
    }

    // ------------------------------------------------------------------ logging in

    public const string LoginUrl = "https://auth.factorio.com/api-login";

    /// <summary>
    /// Asks factorio.com for a service token with the username (or e-mail) and password, the way the game's own launcher does
    /// (https://wiki.factorio.com/Web_authentication_API, api_version 6, the account must own the game). The password goes to
    /// auth.factorio.com over HTTPS once and is kept nowhere; only the token that comes back is saved (see <see cref="Save"/>).
    /// When factorio.com wants the code it e-mailed (a login from a new place), the result says so: ask for it and call again with it.
    /// </summary>
    public static async Task<FactorioLoginResult> LoginAsync(HttpClient http, string login, string password, string? emailCode = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        login = (login ?? "").Trim();
        if (login.Length is 0 or > 200 || login.Any(char.IsControl)) return FactorioLoginResult.Failed("invalid-input", "Enter your factorio.com username or e-mail.");
        if (string.IsNullOrEmpty(password) || password.Length > 1024) return FactorioLoginResult.Failed("invalid-input", "Enter your password.");
        emailCode = string.IsNullOrWhiteSpace(emailCode) ? null : emailCode.Trim();
        if (emailCode is { Length: > 100 }) return FactorioLoginResult.Failed("invalid-input", "The e-mail code is too long.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("username", login), new("password", password), new("api_version", "6"), new("require_game_ownership", "true"),
        };
        if (emailCode is not null) form.Add(new("email_authentication_code", emailCode));

        string body;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(LoginUrl, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return FactorioLoginResult.Failed("unreachable", $"Could not reach factorio.com: {ex.Message}");
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject answer) return FactorioLoginResult.Failed("bad-answer", "factorio.com answered with something unexpected.");
            var token = Text(answer, "token");
            var username = Text(answer, "username");
            if (IsToken(token) && IsUsername(username)) return new FactorioLoginResult(true, null, null, new FactorioCredentials(username!, token!, "studio"));
            var code = Text(answer, "error") ?? "login-failed";
            var message = Text(answer, "message");
            // factorio.com's own text is shown as it is, cut to a sane length; it never contains the password.
            message = string.IsNullOrWhiteSpace(message) ? null : message.Length > 300 ? message[..300] : message;
            return FactorioLoginResult.Failed(code, message ?? (code == "email-authentication-required"
                ? "factorio.com sent a code to your e-mail: enter it to finish logging in."
                : "factorio.com did not accept the username and password, or the account does not own Factorio."));
        }
        catch (JsonException)
        {
            return FactorioLoginResult.Failed("bad-answer", "factorio.com answered with something that is not JSON.");
        }
    }

    /// <summary>Remembers "use the account the installed game is signed in to"; nothing from player-data.json is copied.</summary>
    public static void SaveUseGame(string? storePath = null) => Write(storePath ?? StorePath(), new JsonObject { ["useGame"] = true });

    /// <summary>Deletes the saved account. False when there was none.</summary>
    public static bool Forget(string? storePath = null)
    {
        var path = storePath ?? StorePath();
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ------------------------------------------------------------------ files

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return JsonNode.Parse(File.ReadAllText(info.FullName), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void Write(string path, JsonObject content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".factorio-account-{Guid.NewGuid():N}.tmp");
        try
        {
            // Created owner-only before anything is written into it.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
                stream.Write(Encoding.UTF8.GetBytes(content.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ protection (DPAPI on Windows)

    private static (string Text, string Protection) Protect(string token)
    {
        if (!OperatingSystem.IsWindows()) return (token, "none");
        return (Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(token))), "dpapi");
    }

    private static string? Unprotect(string text, string? protection)
    {
        if (protection == "none") return OperatingSystem.IsWindows() ? null : text; // a plain token is never accepted on Windows
        if (protection != "dpapi" || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(text))); }
        catch (Exception ex) when (ex is FormatException or System.ComponentModel.Win32Exception) { return null; } // another Windows user, or damaged
    }

    /// <summary>CryptProtectData / CryptUnprotectData for the current Windows user, without the extra NuGet package.</summary>
    private static class Dpapi
    {
        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(byte[] data) => Run(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var output = new Blob();
            try
            {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, "Factorio Mod Studio", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                handle.Free();
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}

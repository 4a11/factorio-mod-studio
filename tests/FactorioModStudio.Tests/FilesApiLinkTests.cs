using System.Diagnostics;
using FactorioModStudio.Server;

namespace FactorioModStudio.Tests;

/// <summary>The cross-volume folder move (copy + delete) must never follow a symbolic link or junction inside the folder.</summary>
public class FilesApiLinkTests
{
    private static bool TryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }

        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void CrossVolumeFolderMove_WithLinkInside_IsRefused_AndTouchesNothing()
    {
        using var dir = new TempDir();
        dir.Write("src/a.txt", "a");
        dir.Write("outside/secret.txt", "secret");
        var source = dir.Combine("src");
        if (!TryLink(Path.Combine(source, "link"), dir.Combine("outside"))) return; // no symlink / junction support here: nothing to test

        var target = dir.Combine("dst");
        Assert.Throws<FilesApi.FolderLinkException>(() => FilesApi.MoveEntry(source, target, isFile: false, crossVolume: true));

        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.False(Directory.Exists(target));
        Assert.Equal("secret", File.ReadAllText(dir.Combine("outside", "secret.txt")));
        Directory.Delete(Path.Combine(source, "link")); // remove the link itself before TempDir cleans up
    }

    [Fact]
    public void CrossVolumeFolderMove_WithoutLinks_CopiesAndRemovesTheSource()
    {
        using var dir = new TempDir();
        dir.Write("src/a.txt", "a");
        dir.Write("src/sub/b.txt", "b");
        var target = dir.Combine("dst");

        FilesApi.MoveEntry(dir.Combine("src"), target, isFile: false, crossVolume: true);

        Assert.False(Directory.Exists(dir.Combine("src")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
    }
}

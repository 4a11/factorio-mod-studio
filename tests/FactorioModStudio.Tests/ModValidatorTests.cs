using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class ModValidatorTests
{
    private static bool Has(ValidationResult r, string code) => r.Issues.Any(i => i.Code == code);

    [Fact]
    public void ValidMod_HasNoIssues()
    {
        using var mod = Fixtures.ValidMod();
        var result = ModValidator.Validate(mod.Path);
        Assert.Empty(result.Issues);
        Assert.Equal("my-mod", result.Info?.Name);
    }

    [Fact]
    public void MissingInfoJson_IsAnError()
    {
        using var dir = new TempDir();
        var result = ModValidator.Validate(dir.Path);
        Assert.True(result.HasErrors);
        Assert.True(Has(result, "FMS001"));
    }

    [Fact]
    public void BrokenJson_IsAnError()
    {
        using var mod = Fixtures.ValidMod("{ \"name\": ");
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS002"));
    }

    [Fact]
    public void MissingRequiredField_IsAnError()
    {
        using var mod = Fixtures.ValidMod("""{ "name": "my-mod", "version": "1.0.0", "author": "a", "factorio_version": "2.0" }""");
        var result = ModValidator.Validate(mod.Path);
        Assert.Contains(result.Issues, i => i.Code == "FMS003" && i.Message.Contains("\"title\""));
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData("naïve")]
    public void InvalidName_IsAnError(string name)
    {
        using var mod = Fixtures.ValidMod($$"""{ "name": "{{name}}", "version": "1.0.0", "title": "t", "author": "a", "factorio_version": "2.0" }""");
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS004"));
    }

    [Fact]
    public void InvalidVersion_IsAnError()
    {
        using var mod = Fixtures.ValidMod("""{ "name": "m", "version": "1.0", "title": "t", "author": "a", "factorio_version": "2.0" }""");
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS005"));
    }

    [Fact]
    public void MissingFactorioVersion_IsAWarning()
    {
        using var mod = Fixtures.ValidMod("""{ "name": "m", "version": "1.0.0", "title": "t", "author": "a" }""");
        var result = ModValidator.Validate(mod.Path);
        Assert.Contains(result.Issues, i => i.Code == "FMS006" && i.Severity == Severity.Warning);
    }

    [Fact]
    public void InvalidFactorioVersion_IsAnError()
    {
        using var mod = Fixtures.ValidMod("""{ "name": "m", "version": "1.0.0", "title": "t", "author": "a", "factorio_version": "2.0.1" }""");
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS007"));
    }

    [Fact]
    public void InvalidDependencyAndMissingBase_AreReported()
    {
        using var mod = Fixtures.ValidMod("""
            { "name": "m", "version": "1.0.0", "title": "t", "author": "a", "factorio_version": "2.0",
              "dependencies": ["flib >= nope"] }
            """);
        var result = ModValidator.Validate(mod.Path);
        Assert.True(Has(result, "FMS008"));
        Assert.True(Has(result, "FMS009"));
    }

    [Fact]
    public void WrongThumbnailSize_IsAWarning()
    {
        using var mod = Fixtures.ValidMod();
        Fixtures.WritePngHeader(mod.Combine("thumbnail.png"), 64, 64);
        Assert.Contains(ModValidator.Validate(mod.Path).Issues, i => i.Code == "FMS011" && i.Message.Contains("64x64"));
    }

    [Fact]
    public void MissingThumbnail_IsAWarning()
    {
        using var mod = Fixtures.ValidMod();
        File.Delete(mod.Combine("thumbnail.png"));
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS010"));
    }

    [Fact]
    public void ChangelogVersionMismatch_IsAWarning()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("changelog.txt", new string('-', 99) + "\nVersion: 1.0.0\nDate: 2026-01-01\n  Features:\n    - x\n");
        var result = ModValidator.Validate(mod.Path);
        Assert.True(Has(result, "FMS013"));
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void MissingReferencedAsset_IsAnError()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("data.lua", "icon = \"__my-mod__/graphics/missing.png\"\n");
        var result = ModValidator.Validate(mod.Path);
        Assert.Contains(result.Issues, i => i.Code == "FMS030" && i.File == "data.lua" && i.Line == 1);
    }

    [Fact]
    public void AssetReferenceWithWrongCase_IsAnError()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("graphics/Icon.png", "x");
        mod.Write("data.lua", "icon = \"__my-mod__/graphics/icon.png\"\n");
        Assert.True(Has(ModValidator.Validate(mod.Path), "FMS031"));
    }

    [Fact]
    public void ExistingAsset_OtherModsAndComments_AreIgnored()
    {
        using var mod = Fixtures.ValidMod();
        mod.Write("graphics/icon.png", "x");
        mod.Write("data.lua", """
            icon = "__my-mod__/graphics/icon.png"
            other = "__base__/graphics/icons/iron-plate.png"
            -- old = "__my-mod__/graphics/deleted.png"
            dynamic = "__my-mod__/graphics/" .. name .. ".png"
            """);
        Assert.Empty(ModValidator.Validate(mod.Path).Issues);
    }
}

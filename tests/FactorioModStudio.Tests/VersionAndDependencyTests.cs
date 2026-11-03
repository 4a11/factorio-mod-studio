using FactorioModStudio.Core;
using Xunit;

namespace FactorioModStudio.Tests;

public class VersionAndDependencyTests
{
    [Theory]
    [InlineData("0.0.1", true)]
    [InlineData("1.12.345", true)]
    [InlineData("65535.65535.65535", true)]
    [InlineData("65536.0.0", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("v1.2.3", false)]
    [InlineData("", false)]
    public void ModVersion_TryParse(string text, bool expected) =>
        Assert.Equal(expected, ModVersion.TryParse(text, out _));

    [Fact]
    public void ModVersion_ComparesNumerically()
    {
        Assert.True(ModVersion.Parse("1.10.0") > ModVersion.Parse("1.9.9"));
        Assert.True(ModVersion.Parse("2.0.0") >= ModVersion.Parse("2.0.0"));
        Assert.Equal("1.2.3", ModVersion.Parse("1.2.3").ToString());
    }

    [Theory]
    [InlineData("base", DependencyKind.Required, "base", null, null)]
    [InlineData("base >= 2.0.0", DependencyKind.Required, "base", ">=", "2.0.0")]
    [InlineData("base>=2.0.0", DependencyKind.Required, "base", ">=", "2.0.0")]
    [InlineData("? flib", DependencyKind.Optional, "flib", null, null)]
    [InlineData("?flib < 1.0.0", DependencyKind.Optional, "flib", "<", "1.0.0")]
    [InlineData("(?) space-age", DependencyKind.HiddenOptional, "space-age", null, null)]
    [InlineData("! bad_mod", DependencyKind.Incompatible, "bad_mod", null, null)]
    [InlineData("~ other-mod = 1.0.0", DependencyKind.NoLoadOrder, "other-mod", "=", "1.0.0")]
    public void Dependency_Parses(string text, DependencyKind kind, string name, string? op, string? version)
    {
        Assert.True(Dependency.TryParse(text, out var dep));
        Assert.Equal(kind, dep.Kind);
        Assert.Equal(name, dep.Name);
        Assert.Equal(op, dep.Operator);
        Assert.Equal(version, dep.Version?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(">= 1.0.0")]
    [InlineData("base >= 2.0")]
    [InlineData("bad!name")]
    public void Dependency_RejectsInvalid(string text) =>
        Assert.False(Dependency.TryParse(text, out _));

    [Fact]
    public void Dependency_ChecksVersionConstraints()
    {
        Dependency.TryParse("flib >= 0.15.0", out var atLeast);
        Assert.True(atLeast.IsSatisfiedBy(ModVersion.Parse("0.15.0")));
        Assert.True(atLeast.IsSatisfiedBy(ModVersion.Parse("1.0.0")));
        Assert.False(atLeast.IsSatisfiedBy(ModVersion.Parse("0.14.9")));

        Dependency.TryParse("flib", out var any);
        Assert.True(any.IsSatisfiedBy(ModVersion.Parse("0.0.1")));
    }
}

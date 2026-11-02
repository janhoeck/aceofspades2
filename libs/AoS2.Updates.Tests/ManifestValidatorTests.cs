namespace AoS2.Updates.Tests;

public class ManifestValidatorTests
{
    private static readonly string Hash = new('a', 64);

    private static GameManifest Manifest(params ManifestFile[] files) => new()
    {
        GameVersion = "0.1.0",
        Executable = "AoS2.exe",
        Files = files,
    };

    [Fact]
    public void Validate_ValidManifest_DoesNotThrow()
    {
        ManifestValidator.Validate(Manifest(
            new ManifestFile("AoS2.exe", 10, Hash),
            new ManifestFile("AoS2_Data/Managed/Assembly-CSharp.dll", 20, Hash)));
    }

    [Theory]
    [InlineData("AoS2.exe")]
    [InlineData("AoS2_Data/Managed/Assembly-CSharp.dll")]
    [InlineData("MonoBleedingEdge/etc/mono/4.5/machine.config")]
    [InlineData("Ordner mit Leerzeichen/datei.txt")]
    [InlineData(".hidden/file")]
    public void IsValidPath_RelativePath_ReturnsTrue(string path)
    {
        Assert.True(ManifestValidator.IsValidPath(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("./a")]
    [InlineData("/absolute")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData(".aos2/install.json")]
    [InlineData(".AOS2/x")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    [InlineData("star*.txt")]
    public void IsValidPath_UnsafePath_ReturnsFalse(string path)
    {
        Assert.False(ManifestValidator.IsValidPath(path));
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("10.20.30", true)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0/../x", false)]
    [InlineData("", false)]
    public void IsValidVersion(string version, bool expected)
    {
        Assert.Equal(expected, ManifestValidator.IsValidVersion(version));
    }

    [Fact]
    public void Validate_DuplicatePathIgnoringCase_Throws()
    {
        var manifest = Manifest(new ManifestFile("AoS2.exe", 1, Hash), new ManifestFile("aos2.EXE", 1, Hash));
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Validate_ExecutableNotInFiles_Throws()
    {
        var manifest = Manifest(new ManifestFile("Other.exe", 1, Hash));
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(manifest));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Validate_BadHash_Throws(string sha256)
    {
        var manifest = Manifest(new ManifestFile("AoS2.exe", 1, sha256));
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Validate_NegativeSize_Throws()
    {
        var manifest = Manifest(new ManifestFile("AoS2.exe", -1, Hash));
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(manifest));
    }

    [Fact]
    public void Validate_NoFiles_Throws()
    {
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(Manifest()));
    }

    [Fact]
    public void Validate_NewerFormat_Throws()
    {
        var manifest = Manifest(new ManifestFile("AoS2.exe", 1, Hash)) with { FormatVersion = GameManifest.CurrentFormatVersion + 1 };
        Assert.Throws<InvalidManifestException>(() => ManifestValidator.Validate(manifest));
    }
}

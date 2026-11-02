namespace AoS2.ManifestBuilder.Tests;

public class BuilderOptionsTests
{
    [Fact]
    public void Parse_RequiredArguments_UsesDefaults()
    {
        var options = BuilderOptions.Parse(["--input", "build", "--output", "release", "--version", "0.1.0"], out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("build", options.InputDirectory);
        Assert.Equal("release", options.OutputDirectory);
        Assert.Equal("0.1.0", options.GameVersion);
        Assert.Equal("AoS2.exe", options.Executable);
        Assert.Equal(BuilderOptions.DefaultExcludes, options.Excludes);
    }

    [Fact]
    public void Parse_TagWithV_StripsPrefix()
    {
        var options = BuilderOptions.Parse(["--input", "b", "--output", "r", "--version", "v1.2.3"], out _);

        Assert.Equal("1.2.3", options!.GameVersion);
    }

    [Fact]
    public void Parse_ExecutableAndExcludes()
    {
        var options = BuilderOptions.Parse(
            ["--input", "b", "--output", "r", "--version", "1.0.0", "--executable", @"bin\Game.exe", "--exclude", "*.pdb", "--exclude", "logs/**"],
            out _);

        Assert.Equal("bin/Game.exe", options!.Executable);
        Assert.Contains("*.pdb", options.Excludes);
        Assert.Contains("logs/**", options.Excludes);
    }

    [Theory]
    [InlineData("--input", "b", "--output", "r")]
    [InlineData("--input", "b", "--output", "r", "--version", "eins")]
    [InlineData("--input", "b", "--output", "r", "--version", "1.0.0", "--unknown", "x")]
    [InlineData("--input", "--output", "r", "--version", "1.0.0")]
    [InlineData("--input", "b", "--output", "r", "--version")]
    public void Parse_InvalidArguments_ReturnsError(params string[] args)
    {
        var options = BuilderOptions.Parse(args, out var error);

        Assert.Null(options);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Parse_Help_ReturnsEmptyError()
    {
        Assert.Null(BuilderOptions.Parse(["--help"], out var error));
        Assert.Equal("", error);
    }
}

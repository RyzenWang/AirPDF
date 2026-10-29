using AirPdf.App.Services;
using Xunit;

namespace AirPdf.App.Tests;

public sealed class CommandLineOptionsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"airpdf-cli-{Guid.NewGuid():N}");

    public CommandLineOptionsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Parse_NoArguments_OpensNothing()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.Null(options.FilePath);
        Assert.False(options.ShowHelp);
        Assert.False(options.ShowVersion);
        Assert.Empty(options.IgnoredFiles);
        Assert.Empty(options.UnknownOptions);
    }

    [Fact]
    public void Parse_RecognisesHelpAndVersion()
    {
        Assert.True(CommandLineOptions.Parse(["--help"]).ShowHelp);
        Assert.True(CommandLineOptions.Parse(["-h"]).ShowHelp);
        Assert.True(CommandLineOptions.Parse(["--version"]).ShowVersion);
        Assert.True(CommandLineOptions.Parse(["-v"]).ShowVersion);

        // Options must not be mistaken for file paths.
        Assert.Null(CommandLineOptions.Parse(["--help"]).FilePath);
    }

    [Fact]
    public void Parse_TakesFirstExistingFile_AndReportsTheRest()
    {
        var first = CreateFile("first.pdf");
        var second = CreateFile("second.pdf");

        var options = CommandLineOptions.Parse([first, second]);

        Assert.Equal(first, options.FilePath);
        Assert.Equal([second], options.IgnoredFiles);
    }

    [Fact]
    public void Parse_SkipsOptionsBeforeTheFile()
    {
        var file = CreateFile("doc.pdf");

        var options = CommandLineOptions.Parse(["--unknown", file]);

        Assert.Equal(file, options.FilePath);
        Assert.Equal(["--unknown"], options.UnknownOptions);
    }

    [Fact]
    public void Parse_ReportsPathsThatDoNotExist()
    {
        var missing = Path.Combine(_directory, "nope.pdf");

        var options = CommandLineOptions.Parse([missing]);

        Assert.Null(options.FilePath);
        Assert.Equal([missing], options.IgnoredFiles);
    }

    [Fact]
    public void Parse_DoubleDashTreatsTheRestAsPaths()
    {
        var file = CreateFile("doc.pdf");

        var options = CommandLineOptions.Parse(["--", file, "--strange.pdf"]);

        Assert.Equal(file, options.FilePath);
        Assert.Empty(options.UnknownOptions);
        Assert.Equal(["--strange.pdf"], options.IgnoredFiles);
    }

    [Fact]
    public void Parse_LeadingDashArgumentIsTreatedAsAnOption()
    {
        var options = CommandLineOptions.Parse(["--help-me.pdf"]);

        Assert.Null(options.FilePath);
        Assert.Equal(["--help-me.pdf"], options.UnknownOptions);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "%PDF-1.4\n");
        return path;
    }
}

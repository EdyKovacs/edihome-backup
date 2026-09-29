using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class ExternalCommandRunnerTests
{
    [Fact]
    public async Task StreamsOutputAndReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = await new ExternalCommandRunner().RunAsync(
            new ExternalCommand("dotnet", ["--version"]), output);

        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
    }

    [Fact]
    public async Task ReturnsNonzeroExitCodeAndErrorOutput()
    {
        using var output = new StringWriter();

        var exitCode = await new ExternalCommandRunner().RunAsync(
            new ExternalCommand("dotnet", ["unknown-command-for-backup-tests"]), output);

        Assert.NotEqual(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output.ToString()));
    }
}
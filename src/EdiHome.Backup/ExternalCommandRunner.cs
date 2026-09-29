using System.Diagnostics;

namespace EdiHome.Backup;

public sealed record ExternalCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardInput = null);

public interface IExternalCommandRunner
{
    Task<int> RunAsync(ExternalCommand command, TextWriter output, CancellationToken cancellationToken = default);
}

public sealed class ExternalCommandRunner : IExternalCommandRunner
{
    public async Task<int> RunAsync(
        ExternalCommand command,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.StandardInput is not null
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (command.Environment is not null)
        {
            foreach (var (key, value) in command.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (command.Timeout is { } duration)
        {
            timeout.CancelAfter(duration);
        }

        process.Start();
        var synchronizedOutput = TextWriter.Synchronized(output);
        var stdout = CopyLinesAsync(process.StandardOutput, synchronizedOutput, timeout.Token);
        var stderr = CopyLinesAsync(process.StandardError, synchronizedOutput, timeout.Token);
        var input = command.StandardInput is null
            ? Task.CompletedTask
            : WriteInputAsync(process.StandardInput, command.StandardInput);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, input);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Command timed out: {command.Executable}");
            }

            throw;
        }
    }

    private static async Task CopyLinesAsync(StreamReader reader, TextWriter output, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    private static async Task WriteInputAsync(StreamWriter writer, string input)
    {
        await writer.WriteAsync(input);
        writer.Close();
    }
}
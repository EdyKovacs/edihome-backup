using System.Diagnostics;

namespace EdiHome.Backup;

public sealed class FindmntMountUuidReader : IMountUuidReader
{
    public async Task<string?> ReadUuidAsync(string mountPoint, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("findmnt")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("UUID");
        process.StartInfo.ArgumentList.Add("-M");
        process.StartInfo.ArgumentList.Add(mountPoint);

        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        if (process.ExitCode == 1)
        {
            return null;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"findmnt failed for {mountPoint}: {await process.StandardError.ReadToEndAsync()}");
        }

        return (await process.StandardOutput.ReadToEndAsync()).Trim();
    }
}
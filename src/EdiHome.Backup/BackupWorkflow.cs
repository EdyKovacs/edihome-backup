namespace EdiHome.Backup;

public sealed record BackupStep(string Name, ExternalCommand Command);

public sealed class BackupWorkflow(MountVerifier mountVerifier, IExternalCommandRunner commandRunner)
{
    public async Task<int> RunAsync(
        IReadOnlyList<MountRequirement> mounts,
        IReadOnlyList<BackupStep> steps,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (steps.Count == 0)
        {
            throw new ArgumentException("At least one backup step is required.", nameof(steps));
        }

        foreach (var step in steps)
        {
            var mountErrors = await mountVerifier.CheckAsync(mounts, cancellationToken);
            if (mountErrors.Count > 0)
            {
                foreach (var error in mountErrors)
                {
                    await output.WriteLineAsync(error);
                }

                return 1;
            }

            await output.WriteLineAsync($"Starting {step.Name}");
            var exitCode = await commandRunner.RunAsync(step.Command, output, cancellationToken);
            if (exitCode != 0)
            {
                await output.WriteLineAsync($"Failed {step.Name} (exit {exitCode})");
                return exitCode;
            }

            await output.WriteLineAsync($"Finished {step.Name}");
        }

        return 0;
    }
}
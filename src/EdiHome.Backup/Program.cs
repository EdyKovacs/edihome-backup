using System.Runtime.InteropServices;

namespace EdiHome.Backup;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var noPrune = args.Length > 0 && args[^1] == "--no-prune";
        var commandArgs = noPrune ? args[..^1] : args;
        var validMode = commandArgs is ["preflight", "--config", _] or
            ["run", "--config", _, "--dry-run"] or ["run", "--config", _, "--apply"];
        var selectedComponent = commandArgs is ["run", "--config", _, "--dry-run" or "--apply", "--only", var component]
            ? component : null;
        if ((!validMode && selectedComponent is not ("nextcloud" or "immich" or "proxmox" or "surveillance")) ||
            (noPrune && commandArgs is not ["run", "--config", _, "--apply"] and not
                ["run", "--config", _, "--apply", "--only", _]))
        {
            Console.Error.WriteLine("Usage: EdiHome.Backup preflight --config <path> | run --config <path> (--dry-run | --apply) [--only nextcloud|immich|proxmox|surveillance] [--no-prune with --apply]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        using var termination = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            })
            : null;

        try
        {
            var settings = BackupSettings.Load(commandArgs[2]);
            var reader = new FindmntMountUuidReader();
            if (commandArgs[0] == "preflight")
            {
                var errors = await new MountVerifier(reader).CheckAsync(settings.Mounts, cancellation.Token);
                foreach (var error in errors) Console.Error.WriteLine(error);
                if (errors.Count > 0) return 1;

                Console.WriteLine("All required backup disks are mounted with the expected UUIDs.");
                return 0;
            }

            var plan = settings.RequireBackupPlan();
            var apply = commandArgs[3] == "--apply";
            var runner = new ExternalCommandRunner();
            if (apply)
            {
                await EnsureNoConflictingJobsAsync(runner, cancellation.Token);
            }

            var changes = new RecentFiles();
            var cycle = new BackupCycle(settings, runner, reader, recentFiles: changes);
            if (!apply)
            {
                await cycle.RunAsync(false, Console.Out, cancellationToken: cancellation.Token, onlyComponent: selectedComponent);
                return 0;
            }

            using var runLock = BackupRunLock.Acquire(plan.OldBase);
            var status = new BackupStatus(settings, reader, changes);
            var publisher = new MqttStatusPublisher(plan.MqttEnvFile);
            async Task SaveAndPublishAsync(CancellationToken token)
            {
                var json = await status.SaveAsync(token);
                try
                {
                    await publisher.PublishAsync(json, token);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"MQTT publish failed; status saved locally: {error.Message}");
                }
            }

            await SaveAndPublishAsync(cancellation.Token);
            try
            {
                await cycle.RunAsync(true, Console.Out, async component =>
                {
                    status.Completed(component);
                    await SaveAndPublishAsync(cancellation.Token);
                }, cancellation.Token, selectedComponent, allowPrune: !noPrune);
            }
            catch
            {
                status.Finish(false);
                await SaveAndPublishAsync(CancellationToken.None);
                throw;
            }
            status.Finish(true, partial: selectedComponent is not null);
            await SaveAndPublishAsync(cancellation.Token);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task EnsureNoConflictingJobsAsync(IExternalCommandRunner runner, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var timer = await runner.RunAsync(new ExternalCommand("systemctl",
            ["--user", "is-enabled", "nextcloud-backup-cycle.timer"]), output, cancellationToken);
        if (timer == 0 || output.ToString().Trim() != "disabled")
            throw new InvalidOperationException("Disable the old backup timer before running the C# cycle.");

        foreach (var service in new[] { "nextcloud-backup-cycle.service", "edihome-verify-migration-4tb.service",
            "edihome-retire-cloud-copies.service" })
        {
            if (await runner.RunAsync(new ExternalCommand("systemctl",
                ["--user", "is-active", "--quiet", service]), TextWriter.Null, cancellationToken) == 0)
                throw new InvalidOperationException($"Conflicting job is still running: {service}");
        }
    }
}

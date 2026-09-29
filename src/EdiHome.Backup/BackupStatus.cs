using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;

namespace EdiHome.Backup;

public sealed class BackupStatus(BackupSettings settings, IMountUuidReader mountReader, RecentFiles changes)
{
    private readonly BackupPlan plan = settings.RequireBackupPlan();
    private readonly MountRequirement usbMount = settings.Mounts.First(mount =>
        settings.RequireBackupPlan().NewBase.StartsWith(mount.MountPoint.TrimEnd('/') + '/', StringComparison.Ordinal));
    private readonly Dictionary<string, string> results = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nextcloud"] = "pending", ["immich"] = "pending", ["proxmox"] = "pending", ["surveillance"] = "pending"
    };
    private readonly string startedAt = DateTimeOffset.Now.ToString("O");
    private string state = "running";
    private string result = "running";

    public void Completed(string component) => results[component] = "ok";

    public void Finish(bool success, bool partial = false)
    {
        state = "idle";
        result = success ? partial ? "partial" : "ok" : "failed";
    }

    public async Task<string> SaveAsync(CancellationToken cancellationToken)
    {
        var usbUuid = await mountReader.ReadUuidAsync(usbMount.MountPoint, cancellationToken);
        var disk = string.Equals(usbUuid, usbMount.Uuid, StringComparison.OrdinalIgnoreCase)
            ? Storage(usbMount.MountPoint) : null;
        var payload = new
        {
            host = Environment.MachineName,
            state,
            last_result = result,
            mode = result == "partial" ? "partial" : "real",
            remote_reachable = "unknown",
            nextcloud_result = results["nextcloud"],
            nextcloud_mode = "real",
            nextcloud_remote_reachable = "unknown",
            immich_result = results["immich"],
            immich_mode = "real",
            immich_remote_reachable = "unknown",
            proxmox_result = results["proxmox"],
            proxmox_remote_reachable = "unknown",
            surveillance_result = results["surveillance"],
            last_run = startedAt,
            last_log = (string?)null,
            next_wake_minutes = (int?)null,
            updated_at = DateTimeOffset.Now.ToString("O"),
            recent_files = changes.Entries,
            storage = Storage(plan.OldBase),
            storage_4tb = disk
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var stateDirectory = Path.Combine(plan.OldBase, "state");
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, "csharp-status.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, path, true);
        return json;
    }

    private static object Storage(string path)
    {
        var drive = new DriveInfo(path);
        return new
        {
            path,
            total_bytes = drive.TotalSize,
            free_bytes = drive.AvailableFreeSpace,
            used_bytes = drive.TotalSize - drive.TotalFreeSpace
        };
    }
}

public sealed class MqttStatusPublisher(string envFile)
{
    public async Task PublishAsync(string json, CancellationToken cancellationToken)
    {
        var env = File.ReadLines(envFile)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

        foreach (var key in new[] { "MQTT_HOST", "MQTT_USERNAME", "MQTT_PASSWORD" })
        {
            if (!env.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException($"Missing {key} in MQTT environment file");
        }

        var port = env.TryGetValue("MQTT_PORT", out var configuredPort) ? int.Parse(configuredPort) : 1883;
        var topic = env.GetValueOrDefault("MQTT_TOPIC_BACKUP_STATUS", "edihome/backup-box/status");
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(env["MQTT_HOST"], port)
            .WithCredentials(env["MQTT_USERNAME"], env["MQTT_PASSWORD"])
            .Build();
        using var client = new MqttFactory().CreateMqttClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(options, timeout.Token);
        var message = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(json).WithRetainFlag().Build();
        await client.PublishAsync(message, timeout.Token);
        await client.DisconnectAsync(cancellationToken: timeout.Token);
    }
}
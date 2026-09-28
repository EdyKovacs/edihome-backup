namespace EdiHome.Backup;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["preflight", "--config", var configPath])
        {
            Console.Error.WriteLine("Usage: EdiHome.Backup preflight --config <path>");
            return 2;
        }

        try
        {
            var settings = BackupSettings.Load(configPath);
            var errors = await new MountVerifier(new FindmntMountUuidReader()).CheckAsync(settings.Mounts);
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }
            if (errors.Count > 0)
            {
                return 1;
            }

            Console.WriteLine("All required backup disks are mounted with the expected UUIDs.");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

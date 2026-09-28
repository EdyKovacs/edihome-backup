using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class BackupSettingsTests
{
    [Fact]
    public void RejectsMissingUuid()
    {
        var configPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, """{"mounts":[{"name":"USB","mountPoint":"/mnt/backup4tb","uuid":""}]}""");

            Assert.Throws<InvalidDataException>(() => BackupSettings.Load(configPath));
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
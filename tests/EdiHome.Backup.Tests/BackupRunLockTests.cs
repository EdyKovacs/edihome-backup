using EdiHome.Backup;

namespace EdiHome.Backup.Tests;

public class BackupRunLockTests
{
    [Fact]
    public void SecondRunCannotAcquireLockUntilFirstExits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "edihome-lock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (BackupRunLock.Acquire(directory))
            {
                Assert.Throws<IOException>(() => BackupRunLock.Acquire(directory));
            }

            using var nextRun = BackupRunLock.Acquire(directory);
            Assert.True(nextRun.CanWrite);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
namespace EdiHome.Backup;

public static class BackupRunLock
{
    public static FileStream Acquire(string oldBase) => new(
        Path.Combine(oldBase, ".edihome-backup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
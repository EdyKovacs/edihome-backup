# EdiHome Backup

Standalone .NET 10 backup orchestrator for Backup Box (`faust`). The first milestone is a read-only mount preflight. It does not invoke `rsync`, `restic`, MQTT, or sleep, and it is not installed as an active service.

## Build and test

```sh
dotnet test EdiHome.Backup.slnx
dotnet publish src/EdiHome.Backup/EdiHome.Backup.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

## Preflight on Backup Box

Publish for Linux, transfer the published files and `backup.example.json` to Backup Box, then run:

```sh
./EdiHome.Backup preflight --config backup.example.json
```

Exit code `0` means every configured mount has the expected UUID. Exit code `1` means the configuration, disk, or probe failed; exit code `2` means invalid arguments. The example contains mount identifiers but no credentials. Keep SSH keys, the restic passphrase, and MQTT credentials on the host, outside this repository.

The legacy backup cycle remains the production implementation until the new application has been tested against all four backup sources and restore workflows. GitHub release updates, scheduling, copying, and sleep control are planned, not implemented by this milestone.
# EdiHome Backup

Standalone .NET 10 backup orchestrator for Backup Box (`faust`). It has a manual four-source cycle but is not installed as an active service. The old timer remains disabled until cutover; no sleep or wake scheduling is implemented.

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

## Manual cycle candidate

`backup.run.example.json` describes the real paths and remote hosts but contains no credentials. From the isolated candidate directory on Backup Box:

```sh
./EdiHome.Backup run --config backup.run.example.json --dry-run
```

This checks the two HDD UUIDs, all three NFS mounts, required Proxmox VM/config backups, the capacity needed for Proxmox staging and a conservatively sized snapshot, and closed Frigate MP4 files. It does not dump databases, copy files or publish MQTT. Select one source for a focused test with `--only nextcloud`, `--only immich`, `--only proxmox` or `--only surveillance` after `--dry-run` or `--apply`.

`run --config backup.run.example.json --apply` is the explicit **writing** command. It requires the old timer to be disabled and the migration verifier and old cycle to be inactive. It runs Nextcloud and Immich DB dumps, rsync and restic snapshots; pulls the two newest backups for each configured Proxmox VM and host config; then copies closed surveillance segments into a separate restic repo. It checks repo integrity before retention, stops on failure, publishes status with existing on-host MQTT credentials, and never suspends the laptop. A successful `--only` run is reported as `partial`, not a full `ok` backup.

For an isolated manual test, use `--apply --only nextcloud --no-prune` (or another single source). This still updates that source's staging and creates/checks a snapshot, but does not run `restic forget --prune`. Use `systemd-run --user` for long runs and check the service journal; do not run two writers against the same staging or repository.

**Current validation (2026-09-29):** The migrated Nextcloud and Immich repos passed `restic check --read-data`. C# Nextcloud, Immich, Proxmox and surveillance single-source tests finished with `exit 0`, snapshots `6747a2a0`, `a7e8c63f`, `e0967e36` and `24094332`, successful `restic check`, no prune and `partial` status. Immich config, a DB dump and a JPG restored from the new snapshot matched staging by SHA-256; a restored surveillance MP4 matched by SHA-256 and decoded with `ffmpeg` (non-fatal DTS warnings). After conditional approval, the four old Nextcloud/Immich staging and restic directories were removed from the 1 TB HDD; they are no longer an immediate Bash rollback. The Proxmox backup stays on 1 TB. The complete C# `--apply --no-prune` manual cycle finished with `exit 0` at 11:27:17, all four repository checks passed, and the final status was `idle / ok` with all four components `ok`. The old timer remains disabled. Retention, scheduled wake, USB unmount and sleep are not yet enabled or validated.

**Retention preview (no deletion):** On 2026-09-29, `restic forget --dry-run` left snapshot counts unchanged at Nextcloud 15, Immich 10, surveillance 2, Proxmox 4. Restic's default path grouping would keep separate eight-week histories across the 1 TB to 4 TB migration. The local C# code now supplies `--group-by host,tags` for weekly repositories; its dry-run preview would keep eight Nextcloud, eight Immich and both surveillance snapshots. Proxmox `--keep-last 2` would keep the two newest snapshots. This code change is not deployed on Faust; no real `forget --prune` has been run or approved.

**Retained-snapshot restore samples (2026-09-30):** `restic dump` restored a Nextcloud config from `3acf5820`, an Immich DB dump from `978429b4`, an MP4 from `54efa59a` and a Proxmox host-config archive from `a467d07f`. All four matched their staged SHA-256; `gzip -t` and `tar -tzf` passed, and `ffprobe` read 200 H.264 frames. The private temporary files were removed. This is a representative file restore, not a complete VM or application restore. It did not delete snapshots or run prune.
# PS3HDDTool

PS3 hard-drive/image browsing and verified same-console storage migration, with
a headless CLI for progress monitoring, cancellation and recovery. This public
fork is maintained by [Onur Büyükçağlar](https://github.com/Buyukcaglar) and is
derived from [Pheeeeenom/PS3HDDTool](https://github.com/Pheeeeenom/PS3HDDTool).
The upstream Avalonia browser and PS4 read-only facilities are retained.

**Release v1.4.0:** Windows x64 GUI and CLI packages, complete corresponding source,
license/attributions, release notes and SHA256 checksums are on the
[releases page](https://github.com/Buyukcaglar/PS3HDDTool/releases/latest).

## What this fork adds

- Offline PS3 UFS2 tree migration between different filesystem sizes; images and
  strictly identified offline Windows devices are supported by the migration CLI.
- Accounts' HDD data, saves, trophies, licenses, installed games, hard links,
  sparse files, non-UTF8 names and extended attributes are preserved and checked.
  The matching console registry must cover the source and current user identities.
- Same-console preflight, strict allocation/ownership checks, durable metadata undo,
  target preservation archive, source hash snapshots and full destination comparison.
- Native NAND CBC192 ciphertext byte swapping and explicit EID-root/derived key modes;
  NOR/XTS migration is covered by synthetic tests, not the real case study.
- A narrowly qualified detached recovery-directory view; generic filesystem errors
  remain blockers. No force/ignore-integrity switch is provided.
- NDJSON progress, heartbeats, structured errors, exit codes, cancellation,
  destination-only rollback and verification-only pause/resume.
- Contiguous hash/source-read batching, bounded pointer caching and up to 4 MiB
  contiguous copy buffering, with write-through and durability checks retained.

## Verified real migration

On 12–13 October 2026, a verified image of a 1 TB PS3 HDD was migrated into a
console-prepared 2 TB SATA SSD for the same console, running Evilnat CFW 4.93.
The migration handled **331,755 nodes** and checked **830,359,369,613 logical
content bytes**. Full content, filesystem, metadata and protected-sector checks
passed; clean markers were committed and read back; process and terminal exits
were zero. The owner installed the SSD and reported that everything appeared OK.
This is an initial user acceptance report, not an exhaustive test of every game.

See [the case study](docs/MIGRATION-CASE-STUDY.md), [findings](docs/FINDINGS.md),
[test coverage](docs/TESTING.md) and [v1.4.0 release notes](docs/releases/v1.4.0.md).
The 4 MiB copy buffer added after that completed copy is synthetically validated;
no sustained real-drive speedup is claimed for that later change.

## Start here

Read [operations and recovery](docs/OPERATIONS.md) and [the CLI reference](docs/CLI.txt).
You need the correct console key and registry snapshots, obtained separately.
**No keys, user data, firmware, games, disk images or real journals are bundled.**
Physical migration is Windows-only and requires an Administrator terminal. The
destination must already be correctly prepared in the same console and offline
in Windows. Use exact freshly checked drive identities; disk numbers can change.
GUI/legacy physical write operations are disabled in this fork. Use the guarded
migration CLI for writes. PS4 migration/writes are not implemented.

Extract the CLI ZIP and open PowerShell in its extracted directory:

```powershell
.\PS3HddTool.Migrate.exe --help
.\PS3HddTool.Migrate.exe direct-plan --request C:\PS3Migration\request.json --json
# Run only after reviewing a successful native plan and preservation archive:
.\PS3HddTool.Migrate.exe direct-run --request C:\PS3Migration\request.json `
  --json --event-log C:\PS3Migration\run.ndjson --cancel-file C:\PS3Migration\cancel.flag
```

The [device request template](actual-migration.request.template.json) deliberately
contains invalid REQUIRED placeholders. Fill every field from actual preflight;
it is not an example that is safe to execute unchanged. Image request examples
are also provided. Never post your populated request or logs containing private data.

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet restore PS3HddTool.sln
dotnet build PS3HddTool.sln -c Release
dotnet run --project PS3HddTool.MigrationTests -c Release --no-build
dotnet run --project PS3HddTool.Tests -c Release --no-build
dotnet run --project PS3HddTool.Avalonia -c Release --no-build
```

Migration regression tests require Windows and generate their own keys and images.
Optional full-geometry stress tests remain sparse and use a bounded host-space budget.
The browser's upstream cross-platform code is retained; this release's binaries
and physical migration qualification are Windows x64. Packaging also requires
PowerShell 7, Python 3.11+ and Git. After committing the reviewed tree, use
`scripts/Build-Release.ps1 -IncludeSource` to build binary/source ZIPs and checksums.
Build/run details and limits are in docs/TESTING.md.

## Documentation and attribution

The original [filesystem reference](docs/PS3_UFS2_Filesystem_Reference_Final.md)
and [browser documentation](docs/UPSTREAM-README.md) are retained. Historical
investigation reports are collected in [the documentation index](docs/README.md)
as redacted development notes; their earlier
limitations do not override the current operations guide. Original screenshot
assets are from upstream's synthetic fixtures.

The complete upstream **BSD-3-Clause plus Winter's Weak Copyleft Addendum v1.3.0**
is retained in [LICENSE.md](LICENSE.md). Read [NOTICE.md](NOTICE.md),
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the maintainer's
[non-liability disclaimer](DISCLAIMER.md). These terms accompany binary packages.

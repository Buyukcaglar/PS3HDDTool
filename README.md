# PS3HDDTool

PS3 hard-drive/image browsing and verified same-console storage migration, with
a headless CLI for progress monitoring, cancellation and recovery. This public
fork is maintained by [Onur Büyükçağlar](https://github.com/Buyukcaglar) and is
derived from [Pheeeeenom/PS3HDDTool](https://github.com/Pheeeeenom/PS3HDDTool).
The upstream Avalonia browser and PS4 read-only facilities are retained.

**Release v1.4.0:** Windows x64 GUI and CLI packages, complete corresponding source,
license/attributions, release notes and SHA256 checksums are on the
[releases page](https://github.com/Buyukcaglar/PS3HDDTool/releases/latest).

## AI assistance

Parts of this fork, including migration extensions, the CLI, synthetic tests,
investigations and documentation, were developed with assistance from **OpenAI
Codex**, under the maintainer's direction. Original upstream work remains credited
to its authors in [NOTICE.md](NOTICE.md).

AI-assisted code and explanations can contain errors. The documented tests and
successful migration are evidence for the specific cases tested, not a guarantee
of correctness, data preservation or compatibility with every console or drive.
AI assistance does not imply endorsement or certification by OpenAI, Sony or the
upstream authors.

## Non-liability disclaimer

**Use this software at your own risk.** Onur Büyükçağlar (Buyukcaglar) provides this
fork **AS IS and AS AVAILABLE**, without express or implied warranties, including
fitness for a particular purpose or preservation of data. To the maximum extent
permitted by applicable law, the maintainer, original authors and contributors
disclaim liability for losses arising from its use, including damaged storage,
data corruption, lost accounts, saves, trophies, licenses or games, and lost time
or profits. Nothing excludes liability that applicable law does not allow to be
excluded.

Keep independent backups and follow the documented identification, preflight,
verification and recovery checks. Read the full [maintainer's disclaimer](DISCLAIMER.md)
and [license terms](LICENSE.md); this summary supplements and does not replace them.

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
  contiguous copy buffering, with write-through and durability checks retained
  ([performance details](#performance-improvements)).

## Performance improvements

The migration path reduces small I/O calls and repeated filesystem metadata reads:

- **Faster source hashing and destination verification:** traverse each indirect
  pointer page once and combine logically and physically contiguous data into
  reads up to **1 MiB**. The CBC transport retains its **128 KiB** raw-read limit;
  sparse holes and partial tails keep their exact logical hash semantics.
- **Faster copying:** replace individual 16 KiB payload writes with contiguous
  buffering and cache up to **16 indirect-pointer pages per inode**, avoiding a
  full metadata-page read/write for every pointer update. The later follow-up
  batches source reads up to **1 MiB** and destination payload writes up to **4 MiB**.
- **Avoid repeated work:** a checksummed source-hash cache can eliminate a separate
  source pre-hash pass after metadata qualification. Source contents are still
  checked while copying. Verification-only resume reuses saved source hashes and
  restarts destination verification without source payload rehash or recopy.

Measured results use different workloads and baselines, identified below.
Throughput is decimal MB/s; verification rates count logical content bytes.

| Measurement | Before | After | Scope |
| --- | ---: | ---: | --- |
| Destination content verification | 15.9 MB/s | 126.6 MB/s | Actual migration, about **8×** observed improvement |
| Encrypted copying, CBC192 with byte swapping | 12.26 MB/s | 113.02–119.30 MB/s | Short SSD benchmark with about 64 MiB of generated payload |
| Source-hash raw reads, CBC192 | 10,219 calls | 521 calls | Generated 64 MiB fixture; identical SHA256 |
| Copy source reads, CBC192 | 4,114 calls | 534 calls | Later source-read/buffer follow-up on generated images |
| Copy destination writes | 93 calls | 45 calls | Same follow-up; identical bytes written and verified contents |

The original real verifier was stopped after **34.73%** of the content; the
optimized verifier restarted at zero and checked all **830.36 GB**. These are
observed averages over different portions of the same migrated filesystem, not
a controlled whole-volume before/after benchmark or an overall migration speedup.
The short SSD copy benchmark also does not establish sustained terabyte-copy speed.
The later **4 MiB** copy buffer was tested synthetically and was not used for the
already-completed real copy. Synthetic timing tests inject I/O delay; their call
counts and byte equality are reported here instead of predicting hardware speed.

Exclusive device ownership, write-through, durable undo, protected-data checks,
mandatory full destination verification and rollback remain enabled. The follow-up
passed **200 migration regression cases**, including partial large-write rollback,
and populated sparse 1 TB-to-2 TB CLI stress.

See the [initial write/cache investigation](docs/PERFORMANCE-VALIDATION-20261012.txt),
[hashing and copy follow-up](docs/PERFORMANCE-FOLLOWUP-20261013.txt),
[synthetic I/O counters](evidence/development-source-copy-performance.json),
[real verification measurements](evidence/verification-performance.json) and
[verification pause/resume guide](docs/VERIFICATION-RESUME.txt).

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

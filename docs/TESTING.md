# Tests and evidence boundaries

All default migration tests use generated regular images and keys. They never
open physical disks. No private key, registry, real image or copyrighted payload
is required. The private maintainer-only physical benchmark entry point has been
removed from this public test project.

```powershell
dotnet build PS3HddTool.sln -c Release
dotnet run --project PS3HddTool.MigrationTests -c Release --no-build
dotnet run --project PS3HddTool.Tests -c Release --no-build
# Optional generated benchmarks / sparse population stress:
dotnet run --project PS3HddTool.MigrationTests -c Release --no-build -- --source-copy-performance
dotnet run --project PS3HddTool.MigrationTests -c Release --no-build -- --cli-populated-stress cbc192-bswap16
```

Migration tests require Windows (the identity/exclusive-handle transport is Windows
specific). The GUI/browser suite uses Avalonia headless bindings and synthetic
PS3/PS4 data. Results go under ignored artifacts/. Sparse stress generates logical
1 TB/2 TB images but allocates only touched ranges; allow at least 6 GiB scratch
headroom. A sparse geometry test does not copy a fully occupied terabyte.

## Completed development validation

198 existing migration regressions and two additional large-write partial-failure
tests passed. Coverage includes plain/CBC/CBC-bswap16/XTS, keys, registry coverage,
strict allocation/overlap rejection, source cache integrity, source change during
copy, hard links, sparse/indirect addressing, non-UTF8 names, orphan preservation,
target archives, cancellation, process loss, corrupt journals, recovery interruption,
protected sectors, clean-marker failure and reporting/observer failures.

Verification-only resume tests deny all source-file payload reads, forbid payload
writes, test repeated pause with no writes, wrong identities/metadata/hash/undo/
registry/clean-state rejection, corruption rollback and commit failure. An exact
legacy-executable frozen-process handoff was also validated synthetically before
the real session was stopped. It remains an opt-in historical compatibility test;
an old independently retained executable must be supplied explicitly.

The 1 TB-to-2 TB CBC-bswap16 CLI stress passed with 44,123 nodes, 16 synthetic
accounts, 2,048 save sets, 1,536 trophy sets and 96 game trees. Copy, full comparison,
live monitoring, cancellation/rollback, process-loss recovery, corrupt-undo refusal,
repeat recovery and independent full-geometry fixture checks passed. Peak host
storage was 2.61 GiB. Generated disk images were released afterward.

The generated performance comparison used previous/revised/previous/revised order,
an independent content/filesystem oracle and exact raw I/O counters. Requested
per-call sleeps expose overhead but are not sustained hardware benchmarks.
The real migration and initial console acceptance are separately documented in
MIGRATION-CASE-STUDY.md. Windows x64 release build/smoke/regression results are in
evidence/release-validation.json, generated for the published release.

## Historical reports

The root .txt reports retain the investigation history, including earlier test
counts, old CLI limitations and superseded performance claims. Machine identifiers
and private paths/fingerprints were redacted for publication. Their historical
counts must not be added together as independent new tests. Synthetic aggregate
evidence is included under evidence/; original user-data evidence remains private.

# Operations, progress and recovery

Use the same PS3 console. This is a filesystem migration into a console-prepared
larger target, not a raw clone into an empty SSD and not a transfer between consoles.
Source and target crypto mode, explicit partition start, key kind and hardware
identity must be verified. Obtain genuine source/current xRegistry.sys snapshots;
unchanged same-console users can use the same matching snapshot after qualification.
Keep an independent source image/backup and do not initialize disks in Windows.

## Preflight and run

1. In an Administrator PowerShell terminal, identify the exact disks using Windows
   metadata (number, model, serial, byte length, offline/boot/system state). Refresh
   these checks after every reconnect. Keep the target offline.
2. Fill an image or device request. Use a new journal directory and unique log/marker
   names. A prior cancelled/reformatted target must never reuse its former journal.
3. Run `target-plan`, `target-preserve`, and `target-verify` when replacing existing
   target data. Preserve the resulting archive independently of the destination.
4. Run `direct-plan --request ... --json`; investigate any blocker. No force bypass
   exists. Strict target allocation qualification remains mandatory.
5. Run `direct-run --request ... --json --event-log ... --cancel-file ...` once.
   The CLI acquires exclusive handles, validates durable undo/protected data,
   copies and checks source contents, verifies destination contents/topology,
   commits clean markers and flushes/readbacks. Retain every journal and archive.

For complete request fields, key modes, archive commands and image-only preparation,
see CLI.txt and DIRECT-MIGRATION.txt. The native tool does not set disks offline
for you and rejects unidentified devices, wrong keys, unsafe geometry or overlaps.

## Progress and control

NDJSON events include `type`, `runId`, sequence, timestamp, command and process ID.
Progress data identifies the phase, stage, completed/total units, rate and phase ETA.
Heartbeats continue during unmeasured work. Percent and ETA are **phase-local**;
100% in hashing or allocation scanning is not a successful final commit.
Do not infer disk ownership or process liveness from `direct-status` alone.

`cancel --cancel-file FILE` or Ctrl+C cancels copying and attempts verified rollback.
During durable destination verification, cancellation saves `verification-paused`
without rollback and exits **131**. After that process exits:

```powershell
PS3HddTool.Migrate.exe direct-status --journal C:\PS3Migration\session\journal --json
PS3HddTool.Migrate.exe direct-resume-verification --journal C:\PS3Migration\session\journal --json
```

Resume reuses checksummed/pinned source hashes, audits source metadata, restarts
destination hashing at zero, validates every remaining check and commits. It
does not recopy payload or rehash source file contents. A valid hash checkpoint
is mandatory. `direct-verify` is different: it rehashes both endpoints.
Partial destination hashes are not a durable resumable checkpoint.

For failure/process loss, preserve evidence and first confirm that the writer has
exited. Freshly verify destination identity, then use `direct-status` and the native
recovery checks. `direct-recover --journal ...` needs the destination and intact
journal, not the original source/key. It restores the original target metadata;
it is not a continue-copy command. Never recover against a reformatted target or
run recovery while a writer still owns the disk. Recovery defers cancellation.

Exit codes: 0 success; 1 failure; 2 input/usage; 4 recovery required/unknown;
5 integrity failure; 6 reporting failure; 130 safely cancelled; 131 verification paused.
An integrity/I/O failure can require rollback even during resumed verification.

## Completion and console acceptance

Require process exit 0, terminal `result` exit 0, `Phase=complete`, no reporting or
stderr error, and checksummed `direct-status` confirming complete. That status
opens no disk/key. Ensure the writer has closed before disconnecting the offline
SSD. Keep the source image, source hashes, preservation archive and undo journal.

Install the SSD in the same PS3 and try normal boot first. No preventive filesystem
restore/database rebuild is required after successful qualification and commit.
Initially check local users, saves, trophy collections, licensed games/DLC, ISO
launches, reported capacity, and a new save/reboot/reload cycle. If formatting,
firmware installation or unexpected repair is requested, preserve the exact
message before making changes. `Restore PS3 System` erases system storage.
The source image remains the recovery baseline until console acceptance is established.

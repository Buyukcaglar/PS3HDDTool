# Successful 1 TB HDD to 2 TB SSD migration

## Scope

One same-console PS3 migration was executed on Windows on 12–13 October 2026.
The console used Evilnat CFW 4.93 and native NAND CBC192 with ciphertext bswap16.
The original 1 TB HDD was captured into a host image and fully read back before
being disconnected. The 2 TB SSD was prepared in that same console. No universal
claim is made for every PS3 model, firmware, disk size or USB enclosure.

Hardware serials, source/registry fingerprints, account identifiers, paths, keys,
memory dumps and original journals remain private. Aggregate results are published
in evidence/migration-case-study.json; no game/save/trophy payload is distributed.

## Qualification and execution

- Source image length: 1,000,204,886,016 bytes; full capture/readback verified.
- Destination SSD length: 2,000,398,934,016 bytes.
- Matching same-console registry/user coverage was qualified after an extra target
  user was deleted on-console; target preservation was then freshly repeated.
- Target archive: 26,728,520,588 bytes, 139 nodes, zero orphan files.
- Source tree: 331,755 nodes; 13 recovery files and nine non-UTF8 names preserved.
- Allocated tree copied: 833,020,190,720 bytes. Logical contents checked:
  830,359,369,613 bytes; differing totals include filesystem allocation/pointer overhead.
- Metadata undo: 245,998,864 bytes, retained with the original session journal.

An earlier slow attempt was explicitly abandoned without rollback and its target
re-prepared; that journal was never reused. Its source hashes were preserved and
qualified against a fresh metadata inventory. The admitted second copy began at
20:23 Istanbul on 12 October; actual payload copying ran from approximately 20:35
to 03:45 the next day. It became durable before destination verification.

## Verification-only performance handoff

The old verifier was slow because of small reads/repeated indirect-page traversal.
At the owner's explicit request it was frozen, its exact process identity and
read-only phase checked, then stopped without a cancellation marker or rollback
at 34.7308% destination hashing. The journal stayed unchanged. This exact handoff
had first passed on generated images using the old executable.

A separately pinned optimized verifier started at 08:53 Istanbul, reusing saved
source hashes. Source metadata, undo, preservation archive and protected data were
validated again without source payload rehash or payload recopy. Full destination
hashing restarted at zero; partial old hashes were not reused.

## Results

Full destination content/hash comparison, filesystem topology/attributes/summaries,
original target-data preservation and protected outside sectors all passed. Clean
markers were committed, flushed and read back. At **10:58 Istanbul on 13 October
2026**, the process and terminal result exited zero, reporting successful=true and
journalPhase=complete. The newly pinned CLI direct-status then confirmed the journal
checksum and complete phase, with no recovery required. No stderr/reporting errors
occurred. All session processes were closed; the target remained offline in Windows.

The owner installed the SSD, booted the same console, and reported: "All seems to
be ok." This is initial user-reported console acceptance; it is not an exhaustive
audit of every save, trophy, title, DLC item or extended gaming session.

The verified image, hash cache, preservation archive and undo journal were retained.
Monitoring was disabled after authoritative completion. The later 4 MiB copy-buffer
change is tested on synthetic images and was not used for this already-finished copy.

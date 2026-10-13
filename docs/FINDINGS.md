# Findings and engineering changes

The upstream reader/writer was not a complete, transactional different-size disk
migrator. A raw clone would retain source filesystem geometry; casual file copying
would omit ownership, hard links, sparse layout, account/registry coupling or
protected target areas. A strict standalone migration engine was added.

## Encryption and filesystem preservation

Native NAND CBC192 requires ciphertext bswap16, unlike older unswapped synthetic
fixtures. Known synthetic ciphertext was checked with an independent cipher path;
explicit crypto/key-kind controls prevent wrong-mode interpretation. EID-root key
derivation is explicit and secret bytes are not logged.

UFS2 qualification checks allocation ownership, reserved regions, initialized
inodes, fragment/block/cluster summaries, directory records/links, capacity and
geometry. Dirty state, unexplained allocations, overlap and malformed metadata
are rejected. Source console Restore Filesystem/Rebuild Database operations were
followed by fresh read-only audits; neither was treated as proof on its own.

A narrowly certified detached recovery-directory view preserves 13 recovered files
in the real case. It retains original evidence and does not erase unexplained data
or relax strict target qualification. Raw filesystem names avoid UTF-8 conversion
loss; nine non-UTF8 names were preserved in the source case. The generated recovery
container is explicit; its generated attributes are distinguished from originals.

Target preservation archives retain original allocated data and matching registry
information, and are checked before writes. Target bytes that existed before the
copy are not used as free workspace. Registry identity/coverage mismatch remains
a blocker; an extra target user in the real case was removed on-console before
fresh admission. No account reconciliation bypass was added.

## Transaction and recovery

Before-images of metadata are durable before dirty markers or data writes. Payload
is allocated only in original free space; a write guard enforces exact ranges.
The copy preserves filesystem metadata, links, sparse layout, attributes and names.
Full destination content/tree comparison and protected-sector hashes precede clean
commit; primary/backup markers are flushed and read back in the required order.
The journal is checksummed, identity-bound and exclusively leased. Corrupt undo,
changed identity, range violations and interrupted recovery are explicitly tested.

Source hashes are saved durably. Cache reuse is bound to a checksum and a fresh
metadata/tree inventory; copying still checks content against the saved hashes.
Verification resume forbids payload writes and allows only validated clean-marker
sectors. Verification cancellation pauses; copying cancellation rolls back.

## Performance

The first copy path performed 16 KiB payload writes and repeated whole indirect-page
reads/writes. Bounded pointer caching and 1 MiB output batching eliminated most of
that overhead. The real run later settled near 28–29 MB/s after an initial faster
period. Cache/NAND/background-device behavior is a plausible contributor, not a
measured diagnosis; no sustained device improvement is inferred from a short test.

Hashing originally reread indirect pages for each logical block. Walking pages once
and merging physically/logically contiguous reads up to 1 MiB accelerated the real
destination verifier from approximately 15–18 MB/s to roughly 120–133 MB/s.
The same hash routine also improves source pre-hashing.

After the real copy, the development copy gained contiguous source reads up to
1 MiB, span-based block slices and up to 4 MiB output buffering. In a generated
64 MiB CBC-bswap16 workload, raw source copy reads fell from 4,114 to 534;
destination writes fell from 93 to 45 with identical bytes. Source hashing used
521 raw reads versus 10,219 for the original block loop. The latency model requested
1 ms sleep per call; OS rounding means its elapsed ratios are not hardware forecasts.
Write-through, journal guard, flush/rollback and full comparison are retained.

The redacted root-level investigation notes preserve earlier audits, failures,
qualification criteria and test evolution. Current release behavior is described
in OPERATIONS.md; historical notes can refer to superseded builds.

# Snapshot Design (D4) — compressed vmstate sidecars via TCP migration relay

Optional upgrade on top of [D3](snapshot-design-d3.md). Same thin-disk snapshot tree;
this replaces the *RAM transport* only.

**Status:** design + transport verified empirically (2026-07-23, bundled QEMU 10.1.0
Windows build). Not implemented.

**Idea in one sentence:** Store each snapshot's RAM/CPU state as a compressed
`.vmstate` sidecar produced by QEMU migration over a loopback TCP socket relayed by
Unity, instead of an uncompressed `savevm` blob inside the qcow2 — removing the
per-snapshot RAM-size floor and the last remaining `loadvm` constraint.

---

## 1. Why bother beyond D3

D3 gives true thin disk diffs, but every `.uqsnap` still embeds a raw `savevm`
vmstate — a floor of roughly non-zero guest RAM (~100–250 MB for a 256 MB guest).
Moving the vmstate outside the qcow2:

- allows compression (idle Win9x/XP RAM compresses very well; expect tens of MB);
- eliminates `loadvm` from durable boots entirely, so the boot-copy +
  live-snapshot dance in D3 §4 collapses to "create thin overlay on the `.uqsnap`,
  start QEMU `-incoming`, feed the file, `cont`" — no byte-copy, no `snapshot_blkdev`;
- `savevm`/`loadvm` remain for in-session quick slots only (work overlay, ephemeral).

`savevm` blobs and migration streams are the same versioned device-state format, so
version-compatibility constraints are unchanged from D2/D3.

## 2. Transport: what actually works on the Windows build (verified)

| URI | Result on bundled QEMU 10.1.0 / Windows |
|---|---|
| `migrate file:…` → `-incoming file:…` | **Broken.** Source reports `completed` and writes the file, but the destination fails with `error while loading state section id 2(ram)` / `load of migration failed: Input/output error`. |
| `migrate exec:…` | **Unavailable.** `Failed to execute helper program (No such file or directory)` — Windows build cannot spawn the POSIX shell helper. |
| `migrate tcp:127.0.0.1:…` → `-incoming tcp:127.0.0.1:…` | **Works.** Live loopback migration completed (~770 Mbps, 64 MB guest → 1.56 MB stream; zero pages skipped on the wire); destination restored and sat correctly paused. |

Conclusion: don't touch the file channel; relay the stream through Unity over
loopback TCP. Unity never parses the stream — it just pumps bytes.

## 3. Pipelines

Unity already manages three loopback TCP ports per VM (VNC/QMP/GDB); this adds one
more, only open during a save or load.

### Save (replaces D3's `savevm` step)
1. Pause guest (QMP `stop`).
2. Unity opens a `TcpListener` on a free port.
3. QMP `migrate` with `tcp:127.0.0.1:PORT`, detached. QEMU connects out to Unity.
4. Unity reads the socket through `GZipStream` → `X.uqsnap.vmstate`. Completion via
   QMP `MIGRATION` events (also gives byte counts for a progress bar).
5. Disk part exactly as D3 sibling/child math (copy work / `convert -B parent`),
   minus the `savevm` step.
6. Resume. (After a completed migration the source stays paused; `cont` is ours to
   send — same as today's pause/resume bracket.)

### Load / boot snapshot B
1. Work = thin overlay created directly on `B.uqsnap` (no byte-copy — nothing needs
   to be writable for `loadvm` anymore).
2. Start QEMU with `-incoming tcp:127.0.0.1:PORT` (QEMU listens), paused.
3. Unity connects, streams the decompressed `.vmstate` into the socket, closes.
4. On QMP `MIGRATION` completed event: `cont`.

The restored guest sees a disk that is bit-exactly B (immutable chain + empty
overlay), which is the correctness requirement for incoming migration.

### Run-state subtlety
The migration stream carries the source's run state. We always migrate while paused,
so the destination comes up paused and Unity's explicit `cont` is authoritative —
deterministic either way.

## 4. Artifact & compatibility

- Snapshot = `X.uqsnap` (thin qcow2 diff) + `X.uqsnap.vmstate` (gzip migration
  stream). Importer treats the pair as one `DiskAsset`; missing sidecar = disk-only
  snapshot (boots cold, no state restore).
- Stream format is QEMU-version-bound exactly like `savevm` (same format);
  `uqsnapMetadata.qemuVersion` already records the writer.
- Device topology must match at load — already true for `savevm`, launch config is
  already stored per snapshot.
- Legacy files: D2 fat and D3 thin `.uqsnap`s (embedded savevm, no sidecar) keep
  booting via their existing paths; detect by sidecar presence.
- Retest `file:` on future QEMU upgrades — if fixed upstream, the relay can be
  swapped for direct file migration with zero format changes (minus compression).

## 5. Implementation sketch

1. `MigrationRelay` (Runtime): listener/connector + GZip pump, progress callbacks.
2. QMP: `migrate` command + `MIGRATION` event subscription (exists? extend QmpClient).
3. `VirtualMachine`: `-incoming` launch mode (paused start, port allocation, `cont`).
4. `DurableSnapshotUI`: swap savevm step for relay save; boot path per §3.
5. Importer: pick up `.vmstate` sidecar; move/rename together with the `.uqsnap`.

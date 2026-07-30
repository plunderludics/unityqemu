# Snapshot Design (D4.1) — thin disk diffs + migration streams

**Status:** implemented and verified end-to-end (branch `d4-tcp-migration`), including
the full conversion of the existing snapshot library. QEMU-side mechanisms verified
empirically on the bundled QEMU 10.1.0 Windows build (2026-07-23):

- migration out over a duplicated socket (`migrate fd:`), in over loopback TCP
  (`-incoming tcp:`) — save + restore with real WinXP guests, including mid-game
  states with active SB16 audio (see §2 for why plain `migrate tcp:` is *not* used);
- live external snapshot (`snapshot_blkdev` / `blockdev-snapshot-sync`): active layer
  switched mid-session, post-snapshot guest writes landed only in the new top layer
  (~35 MB of boot writes frozen below, **5.5 MiB** overlay above after 30 s of activity).

D3 (savevm into a live-inserted overlay) is superseded: it depended on the *untested
combination* of `blockdev-snapshot-sync` + `savevm` node targeting, kept the
uncompressed-RAM-size floor, and required opening snapshot files read-write at boot.
D4.1 uses each mechanism only in its well-trodden form.

**Idea in one sentence:** A durable snapshot is a thin qcow2 disk delta plus a gzipped
QEMU migration stream; the disk delta is frozen live with `blockdev-snapshot-sync`,
the RAM/CPU state travels over a loopback socket pumped (and compressed) by Unity.

---

## 1. Artifact

Snapshot `X` = two Unity assets, side by side:

| File | Contents | Typical size (256 MB WinXP guest) |
|---|---|---|
| `X.qcow2` | thin disk tip (`DiskAsset`), backing = parent disk | MBs (true diff) |
| `X.uqsnap` | gzip/raw migration stream (`UqsnapAsset`) + ref to `X.qcow2` + launch metadata | ~30–180 MB |

- **VM boot slots:** `snapshot` (`UqsnapAsset`) and `diskAsset` (`DiskAsset`, read-only /
  auto-filled when a snapshot is set). Bare disk = cold boot; snapshot + Auto Load =
  restore machine state.
- **Ownership:** one-way `UqsnapAsset.disk` → `DiskAsset`. State is not loaded alone.
- Legacy D2 (embedded savevm inside a fat `.uqsnap` disk) is no longer used in this
  project library.
- Snapshot / disk files are only ever **read** after creation (backing files are opened
  read-only; the `.uqsnap` stream is fed in). They can live on read-only media, be shared
  by concurrent VMs, and ship in builds without a copy step — unlike embedded `loadvm`,
  which required opening a qcow2 containing savevm blobs as the writable active drive.

## 2. Transport: what works on the Windows build (verified)

| URI | Result on bundled QEMU 10.1.0 / Windows |
|---|---|
| `migrate file:…` | **Broken.** Source reports completed, but the output fails to load (`Unknown combination of migration flags: 0x0` / `error while loading state section id 3(ram)`) via both `-incoming file:` and `-incoming tcp:` — even for a trivial fresh-boot state. |
| `migrate exec:…` | **Unavailable.** Needs a POSIX shell helper. |
| `migrate tcp:127.0.0.1:…` | **Hangs in `setup` forever on exactly the states we care about** (see below). Works for idle guests. |
| `migrate fd:` (socket duplicated into QEMU via QMP `get-win32-socket`) | **Works**, incl. wedged states. This is what saving uses. |
| `-incoming tcp:127.0.0.1:…` | **Works** for restore (accept happens before any state is loaded). This is what loading uses. |

### The `migrate tcp:` wedge (root-caused empirically)

A guest state with an **active SB16/ISA-DMA transfer** (i.e. any snapshot taken while
a game plays audio — most of this project) re-arms the i8257 DMA bottom-half
continuously after being restored, so QEMU's main loop **never goes idle** — merely
*loading* such a state wedges it, even if the guest never runs a single instruction
afterwards. Outgoing TCP/Unix migration delivers its async connect-completion via a
glib *idle* source on that main loop, so `migrate tcp:` never leaves `setup` status
(QMP keeps answering — the monitor runs on its own thread). `savevm` is unaffected
(synchronous, main-thread). A pre-connected fd (`migrate fd:`) is adopted
synchronously inside the QMP command and sidesteps the idle source entirely.

Two follow-on quirks, both handled in `MigrationRelay` / `VirtualMachine`:

- **No EOF after save**: a handle to the write end survives inside QEMU after the
  migration completes, so the capture reader treats
  `query-migrate = completed` + QMP `closefd` + a quiet period on the socket as
  end-of-stream instead of insisting on EOF.
- **No close after restore**: the same wedge means the incoming side may never close
  its socket after applying the state, so the feeder returns after writing +
  half-closing, and completion is detected by polling `query-status` out of
  `inmigrate`.

Unity never parses the stream — it pumps bytes through a `GZipStream`. Loopback-only
sockets: no firewall prompt, no AV concern; the project already runs QMP/VNC/GDB over
loopback TCP.

## 3. Pipelines

### Boot / load snapshot B (D4 format)

1. Work image = **thin overlay directly on `B.qcow2`** (the snapshot's linked disk).
2. Start QEMU with `-incoming tcp:127.0.0.1:PORT` (QEMU listens; port picked free).
3. Unity connects, streams the decompressed `B.uqsnap` bytes, half-closes; QEMU applies them.
4. Poll QMP `query-status` until runstate leaves `inmigrate` (arrives `paused`,
   since we always migrate while stopped). Don't wait for QEMU to close the socket
   (§2).
5. `cont` — must precede the quick-save: block devices stay *inactive* on the
   destination until the VM starts, and savevm is refused while they are
   ("no block device can store vmstate").
6. **Auto quick-save**: `savevm __unityqemu_state` into the fresh work overlay, so
   Reload (quick-load) with no prior quick-save rewinds to the just-loaded state.
   Costs seconds + ~RAM size in the ephemeral work file (deleted at session end).

If the incoming load fails (bad stream, device-topology mismatch, etc.), fall back to
a cold boot of the same thin overlay without `-incoming` so the user at least gets
the disk state.

### Save (child / sibling / overwrite — one pipeline)

1. QMP `stop`.
2. QMP `blockdev-snapshot-sync` (device `ide0-hd0`, absolute `snapshot-file` path):
   the current work layer freezes as the disk delta; a fresh work layer becomes active.
3. `savevm __unityqemu_state` into the *new* active layer (quick-save slot).
   Must happen **before** the migrate: after outgoing migration the source sits in the
   `postmigrate` runstate, where savevm is refused (`cont` from postmigrate is allowed).
4. Unity builds a connected loopback socket pair, duplicates one end into the QEMU
   process (`WSADuplicateSocketW` → QMP `get-win32-socket`), runs `migrate fd:<name>`;
   pump socket → gzip/raw → `X.uqsnap` (temp + rename). Completion by polling
   QMP `query-migrate` until `completed`, then `closefd` + quiet-drain (§2).
5. `cont` — total pause is steps 1–5, a few seconds. QEMU keeps running throughout;
   no process restart on any save.
6. Offline, while the guest runs: `qemu-img convert -O qcow2 -B <parent> <frozen layer>
   → X.qcow2` (temp + rename). Then import the pair and wire `UqsnapAsset.disk`.

Why `convert -B` instead of copy + rebase:

- The frozen layer contains fat quick-save savevm blobs (boot-time auto quick-save,
  user quick-saves). `convert` copies only active disk content — internal snapshots
  are dropped, output is guaranteed savevm-free and thin.
- It computes a true content diff against any `-B` base, so **one command covers all
  parent choices**: child (`-B` = current tip), sibling/overwrite (`-B` = current tip's
  parent — merges current's delta + session delta).
- Cost: reads the full virtual disk (~2 GB → seconds on SSD), but runs while the
  guest is already resumed.

Child vs sibling remains purely which parent the UI passes.

### In-session quick save / load

Unchanged: `savevm`/`loadvm __unityqemu_state` in the ephemeral work layer. File-size
cost is irrelevant there (work files are deleted at session end). This is the only
in-process load; loading any durable snapshot restarts QEMU (a process accepts
incoming migration only at startup — ~1 s startup + 2–3 s stream feed).

### Work-layer bookkeeping

`blockdev-snapshot-sync` adds one work file per save; a session's chain is
`work_lN → … → work_l1 → work → boot.qcow2`. All layers share the session-id file
name prefix, are deleted on session end, and are swept by the orphan cleanup on the
next start. **Path hygiene (verified gotcha):** QEMU records the backing reference
verbatim from the path the parent was opened with, and qcow2 resolves relative
backing paths against the overlay's directory — all work/snapshot-file paths must be
absolute (already the convention for work images).

## 4. Correctness & compatibility notes

- The restored guest must see a disk bit-identical to save time: guaranteed because
  the frozen delta + immutable chain never change after the freeze, and the boot
  overlay on top starts empty.
- We always migrate paused, so the destination arrives paused and Unity's `cont` is
  authoritative.
- Stream format is QEMU-version-bound exactly like savevm (same vmstate code);
  `uqsnapMetadata.qemuVersion` already records the writer.
- Device topology must match at load, as with `loadvm`. Unlike `savevm`, the *save*
  side has no "all writable devices must support snapshots" rule (though our quick-save
  keeps that constraint satisfied anyway, as today). Writable vvfat would block
  migration; detach USB vvfat before a full RAM save.
- Retest `file:` migration on future QEMU upgrades; if fixed, saving could use it
  directly with zero format changes (minus compression). Likewise retest whether the
  i8257 idle-source wedge (§2) still exists — if QEMU ever stops re-arming the DMA
  bottom-half for stopped guests, plain `migrate tcp:` becomes viable again.

## 5. Legacy (D2) snapshots

Embedded-savevm fat `.uqsnap` disks are no longer supported. The project library was
converted to the D4 pair (`X.qcow2` + `X.uqsnap`); the one-shot batch migrator has
been removed.

## 6. Implementation map

1. `MigrationRelay` (Runtime): `OutgoingCapture` (socket pair + `WSADuplicateSocketW`
   + gzip receive with quiet-drain), `.uqsnap` feeder with connect retry (load).
   Pumps on a worker thread.
2. `VirtualMachine`: `-incoming` launch mode + state feed + auto quick-save;
   cold-boot fallback when state restore fails; `CaptureStateAsync`;
   work-layer chain tracking + cleanup; thin overlay on the linked `DiskAsset`.
3. `DiskOverlay`: `ConvertThin` (qemu-img convert -B, temp + rename);
   prefix-based orphan sweep for layered work files.
4. `DiskAsset` (`.qcow2`) / `UqsnapAsset` (`.uqsnap`): one-way `UqsnapAsset.disk`
   reference; separate ScriptedImporters.
5. `SnapshotUI`: save writes the `.uqsnap` stream + thin `.qcow2`, imports
   both, assigns the boot snapshot while QEMU keeps running after child saves.

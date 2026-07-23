# Snapshot Design (D3) — true thin-diff snapshot tree

Proposed successor to [D2](snapshot-design-d2.md).

**Status:** design only, not implemented.

**Idea in one sentence:** Keep D2's tree of immutable `.uqsnap` assets, but make each
node an actual thin qcow2 diff against its parent by using QEMU **live external
snapshots** (`blockdev-snapshot-sync`) to isolate session writes from ancestor
clusters at boot — so `savevm`/`loadvm` keep working unchanged and each saved file
contains only *its own* delta plus one vmstate.

---

## 1. Why D2 files are fat

Observed: a child snapshot saved after "click New Game" is ~445 MB when its parent is
~439 MB. Causes:

1. **Byte-copy boot.** Booting a `.uqsnap` copies the whole file into the work image so
   its `savevm` tag sits in a *writable active layer* (`loadvm` cannot see tags in
   read-only backing files). The work file therefore starts out containing every
   allocated cluster of the booted snapshot.
2. **Save copies the whole work file.** Ancestor clusters and session writes live in the
   same file, and `qemu-img rebase` never deallocates clusters that duplicate the new
   backing — so ancestor deltas ride along into every descendant, forever.
3. **`savevm` RAM is uncompressed** (zero pages skipped only) — a floor of roughly
   non-zero guest RAM per file.

`qemu-img convert -B parent` would thin a file, but **convert drops internal
snapshots** (the embedded vmstate), so it cannot be bolted onto D2.

## 2. Key insight

The fat comes from ancestor clusters and session writes sharing one writable file.
QEMU can separate them *at runtime*: `blockdev-snapshot-sync` (HMP `snapshot_blkdev`)
pushes a fresh, empty overlay on top of the running disk without a restart. Boot the
snapshot copy directly (so `loadvm` works), then immediately freeze it behind a live
snapshot. From that point on, the writable layer contains **only session writes**, and
`savevm` lands the vmstate in that same thin layer.

No migrate streams, no sidecars, no new state format — just savevm/loadvm plus one
extra block-layer call that has existed since QEMU 0.14.

## 3. Model

```
base.qcow2                 plain DiskAsset (immutable)
     ↑
A.uqsnap                   thin: A∆ + vmstate_A   (backing → base)
     ↑
B.uqsnap                   thin: B∆ + vmstate_B   (backing → A)
```

One file per node, same `DiskAsset`/`backingDisk`/importer machinery as D2.
Assets are immutable; QEMU only writes files under `Library/UnityQemu/work/`.
The freeze warning (❄) becomes genuinely load-bearing: children really are thin
overlays, so overwriting a frozen parent really can corrupt them.

### Runtime chain during a session booted from B

```
work.qcow2  ←  boot.qcow2  ←  A.uqsnap  ←  base.qcow2
(writable,     (byte-copy         (read-only asset chain)
 starts empty)  of B, frozen
                after loadvm)
```

## 4. Pipelines

### Boot plain disk (unchanged from D2)
Work = thin overlay on the disk. `savevm`-based child save already yields a thin file
here; D2's only fat path is snapshot boots.

### Boot snapshot B
1. Byte-copy `B.uqsnap` → `Library/…/work/{session}-boot.qcow2` (cheap — B is thin);
   header-repair backing → absolute path of A.
2. Start QEMU with `-hda boot.qcow2`, paused.
3. `loadvm __unityqemu_state` — tag is in the active writable layer ✓.
4. `snapshot_blkdev` → push fresh empty `{session}-work.qcow2` on top (QEMU creates it,
   backed by boot.qcow2). boot.qcow2 is now frozen read-only backing, content-identical
   to B (nothing ran between loadvm and the live snapshot).
5. `cont`.

### Save child C (parent = booted snapshot B)
1. Pause → `savevm __unityqemu_state` → vmstate + tag land in `work` (the only writable
   layer; the read-only chain below doesn't participate).
2. `CopyAtomic(work → C.uqsnap)`; delete any in-session quicksave tags on the copy
   (`qemu-img snapshot -d`); header-repair backing → B (valid: boot.qcow2 ≅ B).
3. Resume. **No QEMU restart** (D2 child-save restarts to flatten).

C = session delta + one vmstate. Nothing inherited.

### Save sibling S / Overwrite (parent = A, current's parent)
1. Pause → `savevm` → `CopyAtomic(work → S.uqsnap)`.
2. Offline **full rebase of the copy** onto A: pulls in B∆ for clusters where the
   bypassed B differs (that content genuinely belongs in a sibling's diff). Rebase
   preserves internal snapshots — D2 already relies on this and verifies with
   `HasInternalSnapshot`.
3. Header-repair → A. Resume. No restart (D2 flattens the live work file and must stop
   QEMU; here we flatten the copy).

Overwrite = sibling into the existing path, keeping the child-disk warning.

### In-session quick slots (unchanged)
Extra `savevm` tags on `work`; discarded with the session. Strip them from durable
copies at save time.

## 5. Sizes (expected)

For a 256 MB WinXP guest, "click New Game, save child":

| | D2 | D3 |
|---|---|---|
| Disk part | ~190 MB (accumulated ancestor deltas) | ~MBs (session delta) |
| vmstate | ~250 MB raw | ~100–250 MB raw (unchanged) |
| Total | ~445 MB | **~RAM floor + MBs** |

The remaining floor is the uncompressed savevm vmstate — see §7 for the compression
upgrade path.

Minor within-session accumulation: saving several children in one session copies all
session deltas since boot into each. Optional fix: push another live overlay after each
durable save so the writable layer resets to empty. Skip initially.

## 6. Compatibility, risks, details

- **savevm multi-device rule**: all *writable* block devices must support snapshots.
  work.qcow2 ✓; CD/floppy drives are readonly (recent change) ✓.
- **Device name for `snapshot_blkdev`**: with `-hda` the device is `ide0-hd0`. Verify
  once against the bundled build; QMP `blockdev-snapshot-sync` with `device:` works the
  same.
- **boot.qcow2 lifecycle**: second per-session file under `Library/UnityQemu/work/`;
  include in the orphan sweep and OnDestroy cleanup (same session-id naming).
- **loadvm-at-boot is unchanged** from D2 (byte-copy + loadvm), so legacy fat
  `.uqsnap`s keep booting with zero special-casing. They just stay fat until re-saved;
  a one-boot "compact" pass (load → save sibling → replace) thins them.
- **Copying work while QEMU runs**: already done in D2 (`CopyAtomic` with a
  stop-QEMU fallback); unchanged.
- The genuinely missing QEMU APIs (for the record): no offline vmstate copy/extract
  between images, and `loadvm` can't see backing-file tags. The boot-copy +
  live-snapshot dance is the workaround for exactly those two.

## 7. Upgrade path: compressed vmstate sidecars — see [D4](snapshot-design-d4.md)

If the ~RAM-size floor per snapshot matters later, the same thin-disk model composes
with moving the vmstate out of the qcow2 into a compressed sidecar written/read via
QEMU migration. Note: `migrate file:` is **broken** on the bundled Windows QEMU 10.1
build and `exec:` is unavailable, but loopback **TCP migration works** (verified) —
D4 specifies a Unity-side TCP relay. Removes loadvm from durable boots entirely (work
overlays the `.uqsnap` directly, no byte-copy or live-snapshot dance) and cuts totals
to roughly tens of MB. The disk-diff math is identical, so D4 is an incremental
upgrade, not a fork.

## 8. Implementation sketch

1. **Phase 1 — boot dance:** add paused start + `loadvm` + `snapshot_blkdev` + `cont`
   to the snapshot boot path; `{session}-boot.qcow2` naming + cleanup. Verify device
   name & that savevm lands in work.
2. **Phase 2 — save pipeline:** child save = savevm + copy + tag-strip + header repair
   (drop FlattenOnto/restart); sibling/overwrite = copy + offline rebase.
3. **Phase 3 — polish:** Δ-vs-parent column in the tree, "compact legacy snapshot"
   utility, optional post-save re-overlay for multi-save sessions.

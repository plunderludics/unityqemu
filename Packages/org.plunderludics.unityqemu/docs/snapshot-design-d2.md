# Snapshot Design (D2)

Working design for durable QEMU snapshots in UnityQemu.

**Status:** prototype on branch `prototype/d2-snapshots` (worktree `unityqemu-d2`), based on current `main` naming (`VirtualMachine`).

**Idea in one sentence:** Run QEMU against an ephemeral Library work image; durable snapshots are single `.uqsnap` files (qcow2 bytes + embedded `savevm` tag) imported as `SnapshotAsset`.

---

## Prototype usage

1. Open the `unityqemu-d2` worktree project (branch `prototype/d2-snapshots`).
2. Put a base `.qcow2` under `Assets/` (imported as `DiskAsset`), **or** use **Assets → Create → UnityQemu → Disk Asset From QCOW2…** for files under `qemu~/`.
3. On `VirtualMachine`, assign either **Disk Asset** (fresh boot) or **Boot Snapshot** (`.uqsnap`), never both. Leave **Use Ephemeral Work Overlay** on for disk boots.
4. Add a `DurableSnapshotUI` component (same GameObject is fine).
5. Boot the VM, then **Save durable snapshot** — writes `Assets/Qemu/Snapshots/<name>.uqsnap`.
6. **Load durable snapshot** — stops QEMU, **copies** `.uqsnap` → Library work image, restarts, `loadvm __unityqemu_state`.

Existing `SnapshotUI` remains for **session-only** `savevm` tags on the work image.

---

## Why this shape

`savevm` / `loadvm` already store disk + CPU/RAM in one qcow2 and work reliably.

External `migrate` / `-incoming` restore has failed on our builds. Do not depend on it for the prototype.

Problem with today's workflow: all `savevm` tags live in one mutable overlay, so one corruption wipes every snapshot. Fix: copy out an immutable snapshot after each durable save.

`.uqsnap` is qcow2 under another extension so one Project item is the whole snapshot. QEMU does not care about the extension; we still never `-hda` the asset itself.

---

## Model

```
base.qcow2 / o1.qcow2   ← Unity disk asset, never written by QEMU
     ↑
work-XXXX.qcow2         ← Library/UnityQemu/work/ (not an Asset)
     │                     disk boot: thin overlay on base
     │                     snapshot boot: full byte-copy of .uqsnap
     │
     │  durable Save: savevm → copy work → snap1.uqsnap
     ▼
snap1.uqsnap            ← one draggable asset (SnapshotAsset)
  └─ backingDisk → underlying disk asset
```

| Layer | Where | Mutable? | User-facing? |
|---|---|---|---|
| Base disk | `Assets/…/*.qcow2` (imported) | No | Yes (`DiskAsset`) |
| Work image | `Library/UnityQemu/work/` | Yes | No |
| Snapshot | `Assets/…/*.uqsnap` | No (after save) | Yes (`SnapshotAsset`) |

### Why snapshot load is a copy, not an overlay

`loadvm` only sees tags in the **top writable** qcow2. A thin overlay on top of `.uqsnap` would hide `__unityqemu_state`. So snapshot boot always **byte-copies** the `.uqsnap` into the work path (the copy still backs onto the same base disk).

Disk boot still uses a thin overlay — different mechanism, same “never write the durable file” goal.

---

## Code map (prototype)

| Piece | Path |
|---|---|
| `DiskAsset` | `Runtime/Qemu/DiskAsset.cs` |
| `SnapshotAsset` | `Runtime/Qemu/SnapshotAsset.cs` |
| Overlay helpers | `Runtime/Qemu/DiskOverlay.cs` |
| Boot wiring | `Runtime/Qemu/VirtualMachine.cs` (`diskAsset` or `bootSnapshot`, work image) |
| Save/load UI | `Runtime/Qemu/DurableSnapshotUI.cs` |
| `.qcow2` importer | `Editor/Qemu/Qcow2Importer.cs` |
| `.uqsnap` importer | `Editor/Qemu/UqsnapImporter.cs` |
| Create-from-file menu | `Editor/Qemu/DiskAssetMenu.cs` |

---

## Runtime flow

### Boot (disk)

1. Resolve base path from `DiskAsset`.
2. Create a thin work overlay under `Library/UnityQemu/work/` backed by that base.
3. Start QEMU with `-hda` pointing at the work image only.

### Boot (snapshot)

1. Resolve `.uqsnap` path from `SnapshotAsset`.
2. Byte-copy `.uqsnap` → work image (repair backing path from `backingDisk` first).
3. Start QEMU; after QMP connects, `loadvm __unityqemu_state`.

### Durable save

1. Pause guest, `savevm __unityqemu_state`.
2. Prefer copying the work image to `Assets/.../Name.uqsnap` **while QEMU stays paused**, then `cont`
   (guest is already at the saved point — feels like pause/resume).
3. If the OS locks the open qcow2 (common on Windows), fall back to: stop QEMU → copy → restart →
   `loadvm __unityqemu_state`.
4. Set importer `backingDisk` / note / createdAt and reimport as `SnapshotAsset`.

### Durable load

1. Stop QEMU.
2. Copy the `.uqsnap` → work image (never boot the asset file itself).
3. Start QEMU; after QMP connects, `loadvm __unityqemu_state`.

### Backing-path repair

Unity object references are the source of truth for the image graph. Before an image is
used, UnityQemu compares each qcow2 header with `backingDisk`. If an asset move changed
the filesystem path, it warns and runs `qemu-img rebase -u` to update only the header.
Missing files, cycles, inspection failures, and failed repairs are warned and abort
startup rather than booting against the wrong backing image.

Missing Unity metadata is inferred from `qemu-img info` when possible (qcow2 / uqsnap
importers, and **Disk Asset From QCOW2…** for `qemu~/` chains).

---

## Concurrent VMs

Two QEMU processes may share the same base with **different** work images. The base is opened read-only.

---

## Non-goals

- External `migrate` / `-incoming` restore.
- Overlay commit-back as the user-facing model.
- Flattened full-disk clones as the default save.
- Pointing `-hda` at the durable `.uqsnap` (would mutate the asset).

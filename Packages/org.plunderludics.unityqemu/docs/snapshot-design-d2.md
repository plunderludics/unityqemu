# Snapshot Design (D2)

Working design for durable QEMU snapshots in UnityQemu.

**Status:** implemented in package (DurableSnapshotUI + DiskOverlay + DiskAsset).

**Idea in one sentence:** Run QEMU against an ephemeral Library work image; durable snapshots are `.uqsnap` files (qcow2 + embedded `savevm`) imported as the same `DiskAsset` type as plain disks, with optional `uqsnapMetadata`.

---

## Prototype usage

1. Put a base `.qcow2` under `Assets/` (imported as `DiskAsset`).
2. On `VirtualMachine`, assign **Disk Asset** — plain disk for fresh boot, or a `.uqsnap` DiskAsset to resume (`autoLoadVmState` on by default).
3. Add `DurableSnapshotUI`. Save sibling / Save child / Overwrite write `Assets/…/*.uqsnap`.

---

## Model

```
o1.qcow2                ← DiskAsset (uqsnapMetadata = null)
     ↑
winxp-Desktop.uqsnap    ← DiskAsset (uqsnapMetadata set; backingDisk → o1)
     ↑
winxp-solitaire.uqsnap  ← DiskAsset (uqsnapMetadata set; backingDisk → Desktop)
```

| Layer | Where | Mutable? |
|---|---|---|
| Image asset | `Assets/…/*.qcow2` or `*.uqsnap` | No (after save) |
| Work image | `Library/UnityQemu/work/` | Yes |

### Boot

| Disk Asset | Work image |
|---|---|
| Plain `.qcow2` | Thin overlay on that disk |
| `.uqsnap` | **Byte-copy** of the `.uqsnap` into work (never a thin overlay on the `.uqsnap`). Backing header rewritten to absolute parent path so relative Asset sibling names still resolve. `autoLoadVmState` controls `loadvm` only. |

### Save (child vs sibling)

Both call the same pipeline with a different `immediateParent`:

| Action | Parent (`backingDisk` of new file) |
|---|---|
| **Save sibling** | `current.backingDisk` (same parent as current) |
| **Overwrite** | `current.backingDisk` (replace current in place) |
| **Save child** | `current` itself |

Shared steps:

1. `savevm` into the work image.
2. If work does not already back onto `immediateParent`, **flatten the work image** onto that parent (full `qemu-img rebase` while the old backing file still exists). This is what turns a sibling-style work disk into a child of `current`.
3. Copy work → destination `.uqsnap`.
4. Header-only repair on the destination (`rebase -u`) so Assets siblings can keep relative backing names.
5. Never full-rebase the destination file (self-backing / Windows stack overflow).

### Chain / junctions

- One field: `DiskAsset.backingDisk` (immediate parent only).
- Frozen parents: overwrite warns if any disk lists this asset as `backingDisk`.
- Path compare uses Windows file identity so a junction (`sketches-urp/Assets/qemu` → `unityqemu/Assets/qemu`) counts as the same file.
- `FindByFilesystemPath` remaps foreign `…/Assets/…` absolute paths into this project.

---

## Code map

| Piece | Path |
|---|---|
| `DiskAsset` + `uqsnapMetadata` | `Runtime/Qemu/DiskAsset.cs`, `UqsnapMetadata.cs` |
| Overlay helpers | `Runtime/Qemu/DiskOverlay.cs` |
| Boot | `Runtime/Qemu/VirtualMachine.cs` |
| Save/load UI | `Runtime/Qemu/DurableSnapshotUI.cs` |
| Importers | `Editor/Qemu/DiskAssetImporter.cs` (`.qcow2` + `.uqsnap`) |

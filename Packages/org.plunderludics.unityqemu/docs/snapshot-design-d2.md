# Snapshot Design (D2)

Working design for durable QEMU snapshots in UnityQemu.

**Status:** prototype on branch `prototype/d2-snapshots` (worktree `unityqemu-d2`), based on current `main` naming (`VirtualMachine`).

**Idea in one sentence:** Run QEMU against an ephemeral work overlay; durable snapshots are ScriptableObjects that reference immutable `.qcow2` copies (with an embedded `savevm` tag), stored as normal Unity assets.

---

## Prototype usage

1. Open the `unityqemu-d2` worktree project (branch `prototype/d2-snapshots`).
2. Put a base `.qcow2` under `Assets/` (imported as `QemuDiskAsset`), **or** use **Assets → Create → UnityQemu → Disk Asset From QCOW2…** for files under `qemu~/`.
3. On `VirtualMachine`, assign **Disk Asset**, leave **Use Ephemeral Work Overlay** on.
4. Add a `DurableSnapshotUI` component (same GameObject is fine).
5. Boot the VM, then **Save durable snapshot** — writes `Assets/Qemu/Snapshots/<name>.qcow2` + `<name>.asset`.
6. **Load durable snapshot** — stops QEMU, copies image → work overlay, restarts, `loadvm __unityqemu_state`.

Existing `SnapshotUI` remains for **session-only** `savevm` tags on the work overlay.

---

## Why this shape

`savevm` / `loadvm` already store disk + CPU/RAM in one qcow2 and work reliably.

External `migrate` / `-incoming` restore has failed on our builds. Do not depend on it for the prototype.

Problem with today's workflow: all `savevm` tags live in one mutable overlay, so one corruption wipes every snapshot. Fix: copy out an immutable snapshot after each durable save.

---

## Model

```
base.qcow2          ← Unity asset, never written by QEMU
     ↑
work-XXXX.qcow2     ← ephemeral overlay in Library/UnityQemu/work/ (not an Asset)
     │
     │  durable Save: savevm → copy work → new snapshot.qcow2 in Assets
     ▼
Level 3.asset       ← ScriptableObject (what the user drags / sees)
  └─ references Assets/.../Level 3.qcow2   ← immutable overlay copy + savevm tag
```

| Layer | Where | Mutable? | User-facing? |
|---|---|---|---|
| Base disk | `Assets/…/*.qcow2` (imported) | No | Yes (`QemuDiskAsset`) |
| Work overlay | `Library/UnityQemu/work/` | Yes | No |
| Snapshot | `Assets/…` ScriptableObject + sibling `.qcow2` | No (after save) | Yes (`QemuSnapshotAsset`) |

---

## Code map (prototype)

| Piece | Path |
|---|---|
| `QemuDiskAsset` | `Runtime/Qemu/QemuDiskAsset.cs` |
| `QemuSnapshotAsset` | `Runtime/Qemu/QemuSnapshotAsset.cs` |
| Overlay helpers | `Runtime/Qemu/QemuDiskOverlay.cs` |
| Boot wiring | `Runtime/Qemu/VirtualMachine.cs` (`diskAsset`, work overlay, `PrepareBootFromSnapshot`) |
| Save/load UI | `Runtime/Qemu/DurableSnapshotUI.cs` |
| `.qcow2` importer | `Editor/Qemu/Qcow2Importer.cs` |
| Create-from-file menu | `Editor/Qemu/QemuDiskAssetMenu.cs` |

---

## Runtime flow

### Boot

1. Resolve base path from `QemuDiskAsset`.
2. Create a work overlay under `Library/UnityQemu/work/` backed by that base.
3. Start QEMU with `-hda` pointing at the work overlay only.

### Durable save

1. Pause guest, `savevm __unityqemu_state`.
2. **Stop QEMU** (prototype always stops before copy — safer on Windows locks).
3. Atomic-copy work overlay → `Assets/.../Name.qcow2`, import as `QemuDiskAsset`.
4. Create/update `QemuSnapshotAsset` pointing at image + parent disk.
5. Restart QEMU on the same work overlay.

### Durable load

1. Stop QEMU.
2. Copy the snapshot `.qcow2` → work overlay (never boot the asset file itself).
3. Start QEMU; after QMP connects, `loadvm __unityqemu_state`.

---

## Concurrent VMs

Two QEMU processes may share the same base with **different** work overlays. The base is opened read-only.

---

## Non-goals

- External `migrate` / `-incoming` restore.
- Overlay commit-back as the user-facing model.
- Flattened full-disk clones as the default save.

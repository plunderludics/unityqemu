# Snapshot Design (D2)

Working design for durable QEMU snapshots in UnityQemu.

**Status:** notes only — not implementing yet.

**Idea in one sentence:** Run QEMU against an ephemeral work overlay; durable snapshots are ScriptableObjects that reference immutable `.qcow2` copies (with an embedded `savevm` tag), stored as normal Unity assets.

---

## Why this shape

`savevm` / `loadvm` already store disk + CPU/RAM in one qcow2 and work reliably.

External `migrate` / `-incoming` restore has failed on our builds (both `exec:cat` and file-based). Do not depend on it for the prototype. Re-test on a newer QEMU later if desired.

Problem with today's workflow: all `savevm` tags live in one mutable overlay, so one corruption wipes every snapshot. Fix: never keep the library of saves in the live work file — copy out an immutable snapshot after each durable save.

---

## Model

```
base.qcow2          ← Unity asset, never written by QEMU
     ↑
work-XXXX.qcow2     ← ephemeral overlay in Library/ or Temp/ (not an Asset)
     │
     │  durable Save: savevm → copy work → new snapshot.qcow2 in Assets
     ▼
Level 3.asset       ← ScriptableObject (what the user drags / sees)
  └─ references Assets/.../Level 3.qcow2   ← immutable overlay copy + savevm tag
```

| Layer | Where | Mutable? | User-facing? |
|---|---|---|---|
| Base disk | `Assets/…/*.qcow2` (imported) | No | Yes (`QemuDiskAsset`) |
| Work overlay | `Library/UnityQemu/work/` or system temp | Yes | No |
| Snapshot | `Assets/…` ScriptableObject + sibling `.qcow2` | No (after save) | Yes (`QemuSnapshotAsset`) |

---

## Unity assets in Assets/

Yes — put the qcow2 files in `Assets/` as native imported assets. That is fine because **QEMU never writes those files**.

Only the ephemeral work overlay is modified while the guest runs, and that file lives outside the AssetDatabase (`Library/` or temp). Unity therefore does not reimport on every guest write.

On **durable save**, we intentionally write a new `.qcow2` into `Assets/`. Unity imports it once — that is the same as creating any other asset, not continuous churn.

Suggested importer behavior for `.qcow2`:

- Produce (or pair with) a small ScriptableObject handle the Project window can show nicely.
- Treat the source qcow2 as read-only data for runtime.
- Do not require a separate off-Assets "base store" copy unless we later need one for packaging/distribution.

---

## Types

### `QemuDiskAsset` (ScriptableObject)

Represents an immutable base image.

| Field | Purpose |
|---|---|
| reference to imported `.qcow2` (or path) | Base disk |
| `label` | Display name |
| `recommendedRamMiB` | Optional hint for `VirtualMachine` |

Created by dropping a `.qcow2` into the project (custom importer) or "Create from file…".

### `QemuSnapshotAsset` (ScriptableObject)

Represents one durable snapshot. **This is the snapshot format** — no package folder, no `manifest.json`.

| Field | Purpose |
|---|---|
| `disk` | Parent `QemuDiskAsset` (shared base) |
| `image` | Reference to the immutable snapshot `.qcow2` |
| `note` | User annotation |
| `createdAt` | Timestamp (optional) |

The `.qcow2` holds the actual state (`savevm` tag `__unityqemu_state` plus disk diffs relative to the base). The ScriptableObject is the Unity-facing object you name, drag, and assign.

Layout example:

```
Assets/Qemu/Win95/
  win95.qcow2              ← imported base
  Win95.asset              ← QemuDiskAsset
  Snapshots/
    Level 3.qcow2          ← immutable overlay copy
    Level 3.asset          ← QemuSnapshotAsset → disk=Win95, image=Level 3.qcow2
```

Editor can create the `.asset` + `.qcow2` pair together on Save so the user mostly interacts with the ScriptableObject.

---

## Runtime flow

### Boot

1. Resolve base path from `QemuDiskAsset`.
2. Create a fresh work overlay (e.g. `Library/UnityQemu/work/<guid>.qcow2`) backed by that base.
3. Start QEMU with `-hda` pointing at the work overlay only.

### Durable save

1. Pause guest (`stop`), or briefly stop QEMU if Windows cannot copy an open qcow2.
2. `savevm __unityqemu_state` into the work overlay.
3. Copy work overlay → temp file next to the destination, then atomic rename into `Assets/.../Name.qcow2`.
4. Create/update `QemuSnapshotAsset` pointing at that image + parent disk.
5. Resume / restart guest.

### Durable load

1. Stop QEMU.
2. Copy the snapshot `.qcow2` → a new work overlay (never boot the asset file itself).
3. Start QEMU on the work copy.
4. `loadvm __unityqemu_state`.

### Optional in-session quick slots

Extra `savevm` tags on the work overlay only. Session-local, discarded when work is deleted. Useful for fast undo; clearly labeled temporary in UI.

---

## Concurrent VMs

Two QEMU processes may share the same base with **different** work overlays. The base is opened read-only.

Do not share one work overlay between two instances, and never write/commit the shared base while VMs are running.

---

## Non-goals (for now)

- External `migrate` / `-incoming` restore (blocked on current QEMU; re-test later).
- Overlay commit-back / timeline chains as the user-facing model.
- Flattened full-disk clones as the default save (optional later for portable export).
- Multi-disk guests (extend `QemuSnapshotAsset` when needed).

---

## Open questions

1. Can Windows copy the work qcow2 while QEMU is paused (`stop`), or must we fully stop the process first?
2. Should saving a snapshot always create a new asset, or allow overwrite of an existing `QemuSnapshotAsset`?
3. Keep in-session quick slots in the UI, or durable snapshots only at first?

---

## Implementation phases (later)

1. `QemuDiskAsset` + `.qcow2` importer; auto work overlay; wire into `VirtualMachine`.
2. Durable save: `savevm` → copy into Assets → create `QemuSnapshotAsset`.
3. Durable load: copy snapshot → work → restart → `loadvm`.
4. Snapshot UI (list / save / load) on top of these assets.
5. Polish: quick slots, overwrite policy, optional flatten-on-export.

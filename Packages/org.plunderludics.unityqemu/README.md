# UnityQemu

Run QEMU guests inside Unity (VNC framebuffer, QMP control, GDB memory access).

## Layout

- `Runtime/Qemu/` — VirtualMachine + VNC/QMP/GDB clients, input providers, RAM search tools
- `Runtime/RemoteViewing/` — vendored VNC client (`UnityQemu.RemoteViewing` assembly)
- `Editor/Qemu/` — editor shortcuts
- `qemu~/` — QEMU Windows binaries (full tree in-package; player builds trim by default)

## Usage

Add a `VirtualMachine` component. Assign either:
- a `DiskAsset` (`.qcow2`) for a cold boot, or
- a `UqsnapAsset` (`snapshot`, `.uqsnap`) to resume saved machine state
  (`autoLoadVmState` on by default; `diskAsset` is filled from the snapshot's linked disk).
Drop `.iso` files into the project (imported as `CdRomAsset`) and `.img`/`.ima`
floppy images (imported as `FloppyAsset`), then assign them under launch config.
vvfat drives are attached from `PeripheralsUI` (not launch config).
Then press Play or enable edit-mode run.

Project Settings → **UnityQemu** sets the default **QEMU Directory** (e.g. `Assets/qemu`)
used as the starting folder for media pickers, plus player-build options (**Trim QEMU
To i386**, **Obfuscate Guest File Names**). Snapshot save/load still uses the current
snap/disk folder.

### Input

When no input provider is assigned or attached, `VirtualMachine` adds a
`BasicInputProvider` in Play mode. It maps the Unity screen to the VNC framebuffer
and forwards keyboard and mouse input.

For custom input, subclass `InputProvider`, override `PollInput`, and use:

- `AddKeyEvent(KeyCode, bool)` or `AddKeyEvent(int keysym, bool)`
- `SetMousePosition(int, int)` and `SetMouseButtons(...)`
- `SetMouseState(...)`

Assign the component to `VirtualMachine.inputProvider`, or attach it to the same
GameObject and leave the field empty for automatic discovery.

## Player builds (Windows)

`BuildProcessing` (same idea as UnityHawk) runs on player build:

1. Scans build scenes for `DiskAsset` / `UqsnapAsset` / `CdRomAsset` / `FloppyAsset` (and nested
   launch-config CDs), walks qcow2 backing chains, and copies those files into
   `{exe}_Data/QemuAssets/` (Assets-relative layout; package samples under
   `{exe}_Data/Packages/…`). With **Obfuscate Guest File Names** (off by default),
   files are stored as `SHA-256(project-relative path)` instead, and qcow2 backing
   headers are rebased to match.
2. If any were found, copies `qemu~` per Project Settings → **UnityQemu** →
   **Trim QEMU To i386** (on by default):
   - **Trimmed:** only `qemu-i386.manifest`
     (~123 MB: `qemu-system-i386` + `qemu-img` + DLL closure + SeaBIOS PC
     firmware / option-ROMs / keymaps). Regenerate the manifest with
     `python Editor/Qemu/GenerateQemuI386Manifest.py` after updating QEMU.
   - **Full tree (~1.2 GB):** turn trim off.
3. Optional: add a `QemuExtraBuildAssets` component to force guest assets into the
   build even when nothing in the scene references them.

Runtime path roots (`Paths`):

| | Editor | Player |
|--|--------|--------|
| QEMU binaries | `Packages/…/qemu~` | `{exe}_Data/org.plunderludics.unityqemu/qemu~` |
| Disk / uqsnap / ISO / floppy | project files | `{exe}_Data/QemuAssets/…` |
| Work overlays | `Library/UnityQemu/work` | `persistentDataPath/UnityQemu/work` |

Guest images stay as real files on disk (not TextAssets) — QEMU needs filesystem
paths, and disks are often multi‑GB.


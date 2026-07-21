# UnityQemu

Run QEMU guests inside Unity (VNC framebuffer, QMP control, GDB memory access).

## Layout

- `Runtime/Qemu/` — emulator + VNC/QMP/GDB clients
- `Editor/Qemu/` — editor shortcuts
- `Plugins/RemoteViewing.dll` — VNC
- `qemu~/` — QEMU Windows binaries (full upstream tree for now; can be trimmed later)

## Usage

Add a `QemuEmulator` component, set `diskImagePath` to a qcow2 under your project (prefer a `~`-suffixed folder so Unity does not import live disks), and press Play / enable edit-mode run.

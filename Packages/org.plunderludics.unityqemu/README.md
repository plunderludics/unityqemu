# UnityQemu

Run QEMU guests inside Unity (VNC framebuffer, QMP control, GDB memory access).

## Layout

- `Runtime/Qemu/` — VirtualMachine + VNC/QMP/GDB clients, input providers, RAM search tools
- `Editor/Qemu/` — editor shortcuts
- `Plugins/RemoteViewing.dll` — VNC
- `qemu~/` — QEMU Windows binaries (full upstream tree for now; can be trimmed later)

## Usage

Add a `VirtualMachine` component and assign a `DiskAsset` to `diskAsset` for a
fresh boot, or a `.uqsnap` (`SnapshotAsset`) to `bootSnapshot` to resume saved
state. Setting a boot snapshot fills in (and locks) the disk field from the
snapshot's backing disk. Then press Play or enable edit-mode run.

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

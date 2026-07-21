# UnityQemu

Run QEMU guests inside Unity (VNC framebuffer, QMP control, GDB memory access).

## Layout

- `Runtime/Qemu/` — VirtualMachine + VNC/QMP/GDB clients, input providers, RAM search tools
- `Editor/Qemu/` — editor shortcuts
- `Plugins/RemoteViewing.dll` — VNC
- `qemu~/` — QEMU Windows binaries (full upstream tree for now; can be trimmed later)

## Usage

Add a `VirtualMachine` component, set `diskImagePath` to a qcow2 under your project (prefer a `~`-suffixed folder so Unity does not import live disks), and press Play / enable edit-mode run.

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

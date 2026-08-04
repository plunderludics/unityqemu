# Host QEMU binaries (Windows / macOS / Linux)

UnityQemu ships guest tooling via a gitignored `qemu~` tree next to the package sources.
Player builds copy the tree that matches the **build target** (not necessarily the editor host).

## Layout

```
Packages/org.plunderludics.unityqemu/qemu~/
  win/        # Windows PE
  macos/      # Apple Silicon (arm64) — Homebrew bottles + dylib rewrite
  macos-x64/  # Intel (x86_64) — same, sonoma bottle
  linux/      # Debian amd64 qemu-system-i386 + seabios (needs host .so libs)
```

**Legacy:** Windows binaries directly under `qemu~/` (no `win/`) still resolve if `win/` is absent.

Editor Play Mode picks `macos` vs `macos-x64` from the process architecture.
Mac player builds follow **Architecture** in Build Settings:

| Architecture | Packaged QEMU |
|--------------|---------------|
| Apple Silicon | flat `macos/` → player `qemu~` |
| Intel 64-bit | flat `macos-x64/` → player `qemu~` |
| Universal | both under player `qemu~/arm64` and `qemu~/x64` (runtime picks) |

## Current trees (as of fetch)

| Host | Source | Notes |
|------|--------|--------|
| `win/` | Existing Stefan Weil–style PE build | QEMU **10.1.0** |
| `macos/` | Homebrew `qemu` **11.0.3** `arm64_sonoma` + deps | `@loader_path` rewrite |
| `macos-x64/` | Homebrew `qemu` **11.0.3** `sonoma` (x86_64) + deps | `@loader_path` rewrite |
| `linux/` | Debian **10.0.11** `qemu-system-x86` + `seabios` | Distro-linked; Debian/Ubuntu amd64 |

`.uqsnap` streams are QEMU-version-bound — prefer matching major/minor across hosts when
sharing snapshots.

## Durable snapshots

| | Windows | macOS / Linux |
|--|---------|----------------|
| Load `.uqsnap` (`-incoming tcp:`) | yes | yes |
| Save `.uqsnap` (`migrate fd:`) | `get-win32-socket` | `getfd` over unix-domain QMP |
| Disk tip save (`.qcow2`) | yes | yes |

Unix save is best-effort (not run-tested on this Windows machine).

## After first Mac / Linux launch

```bash
# macOS (Gatekeeper / execute bit) — path may be qemu~ or qemu~/arm64|x64
chmod +x "$APP/Contents/org.plunderludics.unityqemu/qemu~/qemu-system-i386"
chmod +x "$APP/Contents/org.plunderludics.unityqemu/qemu~/qemu-img"
xattr -cr "$APP/Contents/org.plunderludics.unityqemu/qemu~"

# Linux
chmod +x …/qemu~/qemu-system-i386 …/qemu~/qemu-img
```

## Cross-building players

Unity can produce macOS/Linux players from a Windows Editor when the matching
Standalone Build Support module is installed. UnityQemu still needs the matching
host trees under `qemu~/` at build time.

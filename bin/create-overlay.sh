#!/usr/bin/env bash
# Create a qcow2 overlay (snapshot) backed by an existing disk image.
# Usage:
#   ./bin/create-overlay.sh <base.qcow2> [overlay.qcow2]
#
# Examples:
#   ./bin/create-overlay.sh Assets/Qemu/qemu~/win95/win95.qcow2
#   ./bin/create-overlay.sh Assets/Qemu/qemu~/win95/win95.qcow2 Assets/Qemu/qemu~/win95/o1.qcow2
#
# Then run QEMU with -hda pointing at the overlay (not the base).

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

QEMU_IMG="${QEMU_IMG:-Packages/org.plunderludics.unityqemu/qemu~/qemu-img.exe}"

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "Usage: $0 <base.qcow2> [overlay.qcow2]" >&2
  exit 1
fi

BASE="$1"
if [[ ! -f "$BASE" ]]; then
  echo "Base image not found: $BASE" >&2
  exit 1
fi

if [[ $# -eq 2 ]]; then
  OVERLAY="$2"
else
  dir="$(dirname "$BASE")"
  stem="$(basename "$BASE")"
  stem="${stem%.*}"
  OVERLAY="${dir}/${stem}-overlay.qcow2"
fi

if [[ -e "$OVERLAY" ]]; then
  echo "Overlay already exists: $OVERLAY" >&2
  exit 1
fi

if [[ ! -f "$QEMU_IMG" ]]; then
  echo "qemu-img not found at: $QEMU_IMG" >&2
  echo "Set QEMU_IMG to override." >&2
  exit 1
fi

mkdir -p "$(dirname "$OVERLAY")"

# Store backing path relative to the overlay so the pair stays movable together.
base_abs="$(cd "$(dirname "$BASE")" && pwd)/$(basename "$BASE")"
overlay_dir="$(cd "$(dirname "$OVERLAY")" && pwd)"
# portable relpath without requiring python
rel_base="$(realpath --relative-to="$overlay_dir" "$base_abs" 2>/dev/null || true)"
if [[ -z "$rel_base" ]]; then
  # Git Bash / older realpath: fall back to basename when same directory
  if [[ "$(cd "$(dirname "$BASE")" && pwd)" == "$overlay_dir" ]]; then
    rel_base="$(basename "$BASE")"
  else
    rel_base="$base_abs"
  fi
fi

# -b / -F: backing file (base) and its format. Overlay stores only diffs.
"$QEMU_IMG" create -f qcow2 -b "$rel_base" -F qcow2 "$OVERLAY"

echo "Created overlay: $OVERLAY"
echo "  backing: $rel_base"
echo "Boot with: -hda $OVERLAY"

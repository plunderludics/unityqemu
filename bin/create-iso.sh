#!/usr/bin/env bash
# Usage: ./bin/create-iso.sh <source-dir> <out.iso>
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <source-dir> <out.iso>" >&2
  exit 1
fi

CMD="mkisofs -o $2 -J -R $1"

echo $CMD
$CMD
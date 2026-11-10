#!/bin/bash
# Regenerate every raster icon from the one SVG source of truth.
#
# `docs/images/icon.svg` is the only authored file: the README/site PNGs, the
# NuGet package icon, the .ico and the demo favicons are all derived from it
# here. Re-run this after editing the SVG so no committed bitmap drifts from the
# vector.
#
# Requires: rsvg-convert (librsvg2-bin), and either Python 3 + Pillow or
# ImageMagick 7 (`magick`) to build the .ico.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="${REPO_ROOT}/docs/images/icon.svg"
IMG_DIR="${REPO_ROOT}/docs/images"
WWWROOT="${REPO_ROOT}/samples/LexiSharp.WasmDemo/wwwroot"

if ! command -v rsvg-convert >/dev/null 2>&1; then
    echo "Error: rsvg-convert not found. Install librsvg2-bin, then re-run." >&2
    exit 1
fi

if [[ ! -f "${SRC}" ]]; then
    echo "Error: icon source not found at ${SRC}" >&2
    exit 1
fi

mkdir -p "${WWWROOT}"

# Web / README / docs, and the 128x128 PNG NuGet asks for at the package root.
rsvg-convert -w 512 -h 512 "${SRC}" -o "${IMG_DIR}/icon-512.png"
rsvg-convert -w 128 -h 128 "${SRC}" -o "${IMG_DIR}/icon-128.png"

# Blazor demo: the 32px tab icon and the 192px home-screen icon it already ships.
rsvg-convert -w 32  -h 32  "${SRC}" -o "${WWWROOT}/favicon.png"
rsvg-convert -w 192 -h 192 "${SRC}" -o "${WWWROOT}/icon-192.png"

# Multi-resolution icon for the docs site favicon and any Windows consumer.
#
# Pillow is preferred: it stores every frame PNG-compressed (~20 KB here).
# ImageMagick's ICO coder writes BMP frames and cannot PNG-compress them, so its
# output is valid but much larger; it is only the fallback.
if python3 -c "import PIL.Image" >/dev/null 2>&1; then
    python3 - "${IMG_DIR}/icon-512.png" "${IMG_DIR}/icon.ico" <<'PY'
import sys
from PIL import Image

src, dst = sys.argv[1], sys.argv[2]
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
Image.open(src).save(dst, format="ICO", sizes=sizes)
PY
    echo "  .ico built with Pillow (PNG frames)"
elif command -v magick >/dev/null 2>&1; then
    magick "${IMG_DIR}/icon-512.png" \
        -define icon:auto-resize=256,128,64,48,32,24,16 "${IMG_DIR}/icon.ico"
    echo "  .ico built with ImageMagick (BMP frames, larger file)"
else
    echo "Error: need Python 3 + Pillow or ImageMagick to build the .ico." >&2
    exit 1
fi

echo "Regenerated icons from ${SRC#"${REPO_ROOT}"/}:"
echo "  docs/images/icon-512.png"
echo "  docs/images/icon-128.png"
echo "  docs/images/icon.ico"
echo "  samples/LexiSharp.WasmDemo/wwwroot/favicon.png"
echo "  samples/LexiSharp.WasmDemo/wwwroot/icon-192.png"

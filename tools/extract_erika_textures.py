"""Restore the external texture files demanded by the MonoGame content import.

Background (see docs/ERIKA_ASSET_AUDIT.md and README.md):
  * `erika/idle_looking_around.fbx` (local, git-ignored) embeds its textures as
    Video/Content blobs, but also records the original Mixamo workstation paths
    (e.g. `/home/app/mixamo-mini/tmp/skins_<guid>.fbm/*.png`).
  * Stock MonoGame importers (FbxImporter / OpenAssetImporter, 3.8.4.1) follow the
    recorded paths instead of the embedded blobs, and MGCB fails the build when
    those files do not exist. There is no bare-filename fallback (verified).
  * MGCB roots the unix-absolute path onto the drive hosting this repository, so
    on this machine it demands `D:\\home\\app\\...`.

This script extracts the embedded blobs (read-only on the FBX; the source file is
never modified) and writes them exactly where the importer looks. It is
deterministic: same FBX bytes always produce the same PNG bytes (verified PNG
magic + sizes). Run it once per machine before `dotnet build`:

    python tools/extract_erika_textures.py

Only the canonical Phase 2B source (`erika/idle_looking_around.fbx`) is handled;
nothing is extracted from the other 36 files.
"""

import os
import struct
import sys

CANONICAL_FBX = os.path.join("erika", "idle_looking_around.fbx")


def parse_props(data, pos, nprops):
    props = []
    for _ in range(nprops):
        kind = chr(data[pos])
        pos += 1
        if kind == "Y":
            props.append(struct.unpack_from("<h", data, pos)[0])
            pos += 2
        elif kind == "C":
            props.append(bool(data[pos]))
            pos += 1
        elif kind == "I":
            props.append(struct.unpack_from("<i", data, pos)[0])
            pos += 4
        elif kind == "F":
            props.append(struct.unpack_from("<f", data, pos)[0])
            pos += 4
        elif kind == "D":
            props.append(struct.unpack_from("<d", data, pos)[0])
            pos += 8
        elif kind == "L":
            props.append(struct.unpack_from("<q", data, pos)[0])
            pos += 8
        elif kind in ("f", "d", "l", "i", "b"):
            struct.unpack_from("<I", data, pos)[0]
            pos += 4
            pos += 4  # encoding
            clen = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            pos += clen
            props.append(("arr", kind))
        elif kind == "S":
            ln = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            props.append(data[pos:pos + ln].decode("utf-8", errors="replace"))
            pos += ln
        elif kind == "R":
            ln = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            props.append(("blob", data[pos:pos + ln]))
            pos += ln
        else:
            raise ValueError("unknown FBX property type %r" % kind)
    return props, pos


def parse_node(data, pos, is64, stack, videos):
    if is64:
        end, nprops, _plen = struct.unpack_from("<QQQ", data, pos)
        pos += 24
    else:
        end, nprops, _plen = struct.unpack_from("<III", data, pos)
        pos += 12
    if end == 0 and nprops == 0:
        return None, pos
    nlen = data[pos]
    pos += 1
    name = data[pos:pos + nlen].decode("ascii", errors="replace")
    pos += nlen
    props, pos = parse_props(data, pos, nprops)
    stack.append((name, props))
    if len(stack) >= 2 and stack[-2][0] == "Video":
        vid = stack[-2][1][0]
        slot = videos.setdefault(vid, {})
        # Note: Video nodes use `Filename`; Texture nodes use `FileName`.
        if name in ("FileName", "Filename") and props and isinstance(props[0], str):
            slot["path"] = props[0]
        if name == "Content" and props and isinstance(props[0], tuple):
            slot["blob"] = props[0][1]
    if end > 0:
        limit = end - (13 if not is64 else 25)
        while pos < limit:
            pad = 13 if not is64 else 25
            if data[pos:pos + pad] == b"\x00" * pad:
                pos += pad
                break
            child, pos = parse_node(data, pos, is64, stack, videos)
            if child is None:
                break
        pos = end
    stack.pop()
    return name, pos


def main():
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    fbx_path = os.path.join(repo_root, CANONICAL_FBX)
    if not os.path.isfile(fbx_path):
        print(
            "Missing required local asset: %s\n"
            "Restore the ignored local erika/ source directory, then re-run."
            % CANONICAL_FBX
        )
        return 1
    with open(fbx_path, "rb") as handle:
        data = handle.read()
    version = struct.unpack_from("<I", data, 23)[0]
    is64 = version >= 7500
    videos = {}
    pos = 27
    while pos < len(data) - 13:
        pad = 25 if is64 else 13
        if data[pos:pos + pad] == b"\x00" * pad:
            break
        node, pos = parse_node(data, pos, is64, [], videos)
        if node is None:
            break
    drive = os.path.splitdrive(os.path.abspath(fbx_path))[0] or os.path.splitdrive(os.getcwd())[0]
    written = 0
    for vid, slot in sorted(videos.items()):
        blob = slot.get("blob")
        path = slot.get("path", "")
        if blob is None or not path.endswith((".png", ".jpg", ".jpeg", ".tga", ".bmp")):
            continue
        # Mirror MGCB: a unix-absolute recorded path is rooted on the repo drive.
        target = drive + path.replace("/", os.sep) if path.startswith("/") else path
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "wb") as handle:
            handle.write(blob)
        print("wrote %s (%d bytes)" % (target, len(blob)))
        written += 1
    if written == 0:
        print("No embedded textures found; nothing written.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

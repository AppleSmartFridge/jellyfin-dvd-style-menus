#!/usr/bin/env python3
"""Writes Native/runtime-manifest.json: where the plugin downloads runtime bundles from and their SHA-256."""
import argparse
import glob
import hashlib
import json
import os

RIDS = ["linux-x64", "linux-arm64", "linux-musl-x64", "win-x64", "osx-arm64", "osx-x64"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dist", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--base-url", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    bundles = {}
    for rid in RIDS:
        name = f"bdmenu-runtime-{rid}.zip"
        path = os.path.join(a.dist, name)
        bundles[rid] = {"file": name, "sha256": sha256(path) if os.path.exists(path) else ""}
        if not os.path.exists(path):
            print(f"warning: {name} missing; that platform will have no download")

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"version": a.version, "baseUrl": a.base_url, "bundles": bundles}, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()

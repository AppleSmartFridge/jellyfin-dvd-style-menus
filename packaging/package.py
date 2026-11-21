#!/usr/bin/env python3
"""
Builds the release files:
  discmenus_<version>.zip          plugin only (downloads the runtime on first use)
  discmenus_<version>_offline.zip  plugin + every runtime bundle unpacked under runtime/<rid>/
  manifest.json                    Jellyfin plugin repository manifest
"""
import argparse
import datetime
import hashlib
import json
import os
import zipfile

GUID = "ad6c53d6-78ce-4d7f-bda4-595ac69bf03d"
NAME = "Disc Menus"
TARGET_ABI = "12.1.0.0"
DESCRIPTION = "Plays decrypted Blu-ray ISOs and BDMV folders with HDMV and BD-J menus, rendered on the server for every client."
OVERVIEW = "Blu-ray menus (HDMV and BD-J) for every Jellyfin client"
# Only the plugin's own files: the server provides Jellyfin.*, MediaBrowser.* and ASP.NET Core.
KEEP = {"Jellyfin.Plugin.DiscMenus.dll", "QRCoder.dll"}


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--plugin-dir", required=True)
    ap.add_argument("--dist", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--owner", required=True)
    ap.add_argument("--repo-url", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    meta = {
        "category": "General",
        "changelog": f"See {a.repo_url}/releases/tag/{a.tag}",
        "description": DESCRIPTION,
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "owner": a.owner,
        "targetAbi": TARGET_ABI,
        "timestamp": timestamp,
        "version": a.version,
    }

    files = [f for f in os.listdir(a.plugin_dir) if f in KEEP]
    missing = KEEP - set(files)
    if missing:
        raise SystemExit(f"missing from publish output: {missing}")

    plain = os.path.join(a.out, f"discmenus_{a.version}.zip")
    with zipfile.ZipFile(plain, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            z.write(os.path.join(a.plugin_dir, f), f)
        z.writestr("meta.json", json.dumps(meta, indent=2))

    offline = os.path.join(a.out, f"discmenus_{a.version}_offline.zip")
    with zipfile.ZipFile(offline, "w", zipfile.ZIP_DEFLATED) as z:
        for f in files:
            z.write(os.path.join(a.plugin_dir, f), f)
        z.writestr("meta.json", json.dumps(meta, indent=2))
        for bundle in sorted(os.listdir(a.dist)):
            if not (bundle.startswith("bdmenu-runtime-") and bundle.endswith(".zip")):
                continue
            rid = bundle[len("bdmenu-runtime-"):-len(".zip")]
            with zipfile.ZipFile(os.path.join(a.dist, bundle)) as src:
                for info in src.infolist():
                    if info.is_dir():
                        continue
                    target = zipfile.ZipInfo(f"runtime/{rid}/{info.filename}", info.date_time)
                    target.external_attr = info.external_attr  # keep executable bits
                    target.compress_type = zipfile.ZIP_DEFLATED
                    z.writestr(target, src.read(info))

    manifest = [{
        "guid": GUID,
        "name": NAME,
        "description": DESCRIPTION,
        "overview": OVERVIEW,
        "owner": a.owner,
        "category": "General",
        "versions": [{
            "version": a.version,
            "changelog": meta["changelog"],
            "targetAbi": TARGET_ABI,
            "sourceUrl": f"{a.repo_url}/releases/download/{a.tag}/discmenus_{a.version}.zip",
            "checksum": md5(plain),
            "timestamp": timestamp,
        }],
    }]
    with open(os.path.join(a.out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()

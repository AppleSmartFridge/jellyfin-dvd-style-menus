# Disc Menus for Jellyfin

> [!WARNING]
> **This entire project was vibecoded.** All of it (the C# plugin, the native C helper, the web remote, the build scripts and this README) was written by prompting an AI model, not by hand. Nobody has reviewed it line by line, and it has not yet been tested end-to-end against real discs or real Jellyfin clients. Treat it as experimental, read the code before you trust it, and expect bugs.

A Jellyfin **12.1+** plugin that plays **decrypted** Blu-ray ISOs and BDMV folders **with their menus**, both **HDMV** (standard Blu-ray menus) and **BD-J** (Java menus). It aims to work on most Jellyfin clients: official web, desktop, Android, Android TV and iOS clients, Swiftfin, Moonfin, TV apps and others.

## How it works

No Jellyfin client can run Blu-ray menus itself, and this project does not modify any client. Instead:

1. **The server plays the disc.** A small native helper, `bdmenu`, runs the disc through [libbluray](https://www.videolan.org/developers/libbluray.html). It uses a bundled Java runtime for BD-J menus.
2. **Menus are drawn into the picture.** The helper draws the menu and subtitle graphics over the video and encodes the result as a live H.264/AAC stream.
3. **Jellyfin delivers it like any other video.** The stream is offered as a version of the movie called **"Disc Menu (Interactive)"**. Jellyfin's normal transcoder sends it to each client in a format that client can play, which is why it works on so many clients.
4. **You navigate with a remote.** Because a client can't send its remote's button presses to the server, the plugin gives you three ways to navigate:
   - **Web player button.** In jellyfin-web (browsers, Jellyfin Media Player and Jellyfin Desktop, and the Android and iOS apps that wrap the web client), a **Disc Remote** button appears in the player's bottom button row while a disc menu plays. It opens an on-screen remote (D-pad, OK, Top Menu, Pop-up, colour keys, chapters, audio and subtitles, a touchpad for pointer menus). The phone remote is offered inside it, not forced on you.
   - **Disc Remote page.** A **Disc Remote** entry in the Jellyfin side menu shows the same remote as a full page.
   - **Phone remote.** Scan a QR code, or open the link printed under it, and your phone becomes the remote. This works with *every* client, including native TV apps, because the menu is part of the video.
   - **Keyboard (optional).** With "Send keyboard keys to disc menus" turned on, the web player sends arrow keys and Enter straight to the menu.

Each disc also gets plain versions: **"Main Feature"** and one per extra or episode playlist. These play on every client with no menu, and you can seek in them.

## Install

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**, and add the `manifest.json` URL from this project's latest release. Then install **Disc Menus** from the catalogue. Alternatively, unzip `discmenus_<version>.zip` into `<config>/plugins/DiscMenus_<version>/`.
2. Restart Jellyfin.
3. Optional, for the web player button: install the community **File Transformation** plugin. Disc Menus registers its script with it automatically. Without it, everything else still works, including the Disc Remote menu page and the phone remote. You can also load `/DiscMenus/web-inject.js` with any script-injector plugin.

### The native runtime

The plugin needs nothing installed on the server. The first time a disc menu plays, it downloads the runtime bundle for your platform from this project's release and checks it against a SHA-256 built into the plugin. A download that doesn't match is rejected. Platforms: `linux-x64`, `linux-arm64`, `linux-musl-x64`, `win-x64`, `osx-arm64`, `osx-x64`.

For servers without internet access, use `discmenus_<version>_offline.zip`. It includes every platform's runtime under `runtime/<rid>/`. You can also point **Settings → Runtime** at a folder holding an unpacked runtime.

## Settings

**Dashboard → Plugins → Disc Menus**:

| Setting | What it does |
|---|---|
| Offer "Disc Menu (Interactive)" | Adds the menu version to every Blu-ray. |
| Offer main feature and extras | Adds one version per playlist (main feature first). |
| Shortest extra / Most titles | Controls which playlists are listed. |
| Press Enter automatically | Presses Enter in the first menu after N seconds. Handy when no remote is at hand. |
| Bitrate, encoder, speed | Menu stream quality. Hardware encoders (NVENC, VideoToolbox, Media Foundation) are tried when chosen, with a fallback to the next one that works. |
| Region, language | What the player tells the disc. |
| Most sessions, idle timeout | Each menu session costs one live encode. |
| Web player button, keyboard keys | The jellyfin-web integration. |
| Runtime | Auto-download, local folder or another download address. |

The page also shows runtime status, running sessions (with a Stop button) and the **phone remote QR code, with its link printed underneath**.

## Limits

- **Decrypted discs only.** There is no AACS or BD+ code. Encrypted discs are detected and not offered.
- **Native clients need the phone remote** (or Enter-after-N-seconds) to move through menus.
- **Menu playback is a live re-encode on the server**, so it uses CPU or GPU like a transcode. A key press takes about 1–2 seconds to show on screen, because of client buffering.
- **No seeking inside the menu version.** Use the chapter buttons on the remote, or play the Main Feature version, which can seek.
- Title versions play the whole clips in a playlist. Multi-angle and seamless-branching playlists play angle 1 only, and in/out points inside a clip are not trimmed.
- BD-Live and other online BD-J features are not supported.
- ISO reading uses a small built-in UDF reader. Unusual images may not be recognised; a BDMV folder always works.

## Building

```bash
# plugin + tests (.NET 10 SDK)
dotnet test
dotnet publish src/Jellyfin.Plugin.DiscMenus -c Release -o out

# native runtime for this machine (see the script header for prerequisites)
PREFIX=$PWD/build/prefix ./native/scripts/build-deps.sh
PREFIX=$PWD/build/prefix RID=linux-x64 OUT=$PWD/dist ./native/scripts/bundle.sh
```

`.github/workflows/build.yml` builds every runtime bundle, writes their checksums into the plugin, runs the tests and publishes a release with `manifest.json` when you push a `v*` tag.

### Layout

```
src/Jellyfin.Plugin.DiscMenus/
  Disc/        BDMV parsers (index.bdmv, .mpls), folder + UDF ISO readers, main-feature detection
  Media/       media source provider (versions), live stream, playlist concat stream, URL signing
  Sessions/    bdmenu process management, phone pairing
  Native/      runtime download/verification
  Api/         REST API (/DiscMenus/...)
  Providers/   runtime + chapters from the disc
  Web/         remote UI, web player integration
native/bdmenu/ the helper (C: libbluray + FFmpeg)
native/scripts build-deps.sh (FFmpeg + libbluray + bdmenu), bundle.sh (runtime zip with jlink'd JRE)
packaging/     release + repository manifest scripts
tests/         xUnit tests with synthetic BDMV structures
```

## API

All under `/DiscMenus`. Remote calls accept a normal Jellyfin login or a phone-remote token (`X-DiscMenus-Remote` header, or `rt` query for event streams).

| Method | Path | |
|---|---|---|
| GET | `Sessions` | Menu sessions you may control |
| GET | `Sessions/ForDevice?deviceId=` | Session playing on a device |
| POST | `Sessions/{id}/Key/{key}` | `up down left right enter menu popup back 0-9 red green yellow blue play pause stop next prev` |
| POST | `Sessions/{id}/Mouse` | `{ "x": 0..1, "y": 0..1, "click": true }` |
| POST | `Sessions/{id}/Command` | `{ "command": "title"\|"audio"\|"subtitle"\|"chapter", "value": n }` |
| GET | `Sessions/{id}/Events` | Server-sent disc state |
| POST | `Pairing` | New phone pairing link + QR (10 minutes, single use) |
| GET | `Remote` | Phone remote page |
| GET | `Status`, POST `Runtime/Install`, GET `Discs/{itemId}` | Admin |

## Licence

GPL-3.0 (see `LICENSE`). The runtime bundles libbluray (LGPL-2.1), FFmpeg built with libx264 (GPL), and an OpenJDK runtime image (GPL-2.0 with Classpath Exception). Each bundle has a `LICENSES/` folder.

Blu-ray is a trademark of the Blu-ray Disc Association. This project is not affiliated with Jellyfin or the BDA.

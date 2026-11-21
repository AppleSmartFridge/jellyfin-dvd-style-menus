#!/usr/bin/env bash
# Packs bdmenu, its shared libraries, the libbluray BD-J jar and a minimal Java runtime into
# bdmenu-runtime-<rid>.zip, the layout the plugin's NativeRuntimeManager expects:
#
#   bin/bdmenu(.exe)            helper (Windows: DLLs live here too)
#   lib/                        shared libraries (Linux/macOS)
#   share/java/libbluray-*.jar  BD-J support classes
#   jre/                        jlink'd Java 17 runtime for BD-J
#   LICENSES/                   third-party licences
#
#   PREFIX=build/prefix RID=linux-x64 OUT=dist ./native/scripts/bundle.sh
set -euo pipefail

PREFIX="${PREFIX:?}"
RID="${RID:?}"
OUT="${OUT:-$PWD/dist}"
STAGE="$OUT/stage-$RID"
JAVA_HOME="${JAVA_HOME:?set JAVA_HOME to a JDK 17 (used for jlink)}"

rm -rf "$STAGE"
mkdir -p "$STAGE/bin" "$STAGE/lib" "$STAGE/share/java" "$STAGE/LICENSES" "$OUT"

case "$RID" in
  win-*) exe=bdmenu.exe ;;
  *) exe=bdmenu ;;
esac
cp "$PREFIX/bin/$exe" "$STAGE/bin/"
cp "$PREFIX"/share/java/libbluray-*.jar "$STAGE/share/java/"

# ---------------------------------------------------------------- shared libraries
case "$RID" in
  linux-*)
    # Copy every non-system dependency, then point them all at $ORIGIN/../lib.
    copy_deps() {
      ldd "$1" | awk '/=> \// { print $3 }' | while read -r lib; do
        base="$(basename "$lib")"
        case "$base" in
          libc.so*|libm.so*|libdl.so*|libpthread.so*|librt.so*|ld-linux*|ld-musl*|libgcc_s.so*|libstdc++.so*) continue ;;
        esac
        if [ ! -f "$STAGE/lib/$base" ]; then
          cp -L "$lib" "$STAGE/lib/$base"
          copy_deps "$lib"
        fi
      done
    }
    # The staged binary's rpath points at the (still empty) bundle lib folder, so let ldd find the
    # freshly built libraries in $PREFIX/lib.
    export LD_LIBRARY_PATH="$PREFIX/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    copy_deps "$STAGE/bin/bdmenu"
    for lib in libbluray libavcodec libavformat libavutil libswscale libswresample; do
      ls "$STAGE"/lib/"$lib".so* >/dev/null 2>&1 || { echo "bundle: $lib was not copied" >&2; exit 1; }
    done
    for f in "$STAGE"/lib/*.so*; do patchelf --set-rpath '$ORIGIN' "$f"; done
    patchelf --set-rpath '$ORIGIN/../lib' "$STAGE/bin/bdmenu"
    ;;
  osx-*)
    dylibbundler -od -b -x "$STAGE/bin/bdmenu" -d "$STAGE/lib/" -p @executable_path/../lib/
    ;;
  win-*)
    # MSYS2: copy every DLL from the MinGW prefix that bdmenu needs, next to the exe.
    ldd "$STAGE/bin/bdmenu.exe" | awk '/=> \// { print $3 }' | grep -viE '/c/windows/' | while read -r dll; do
      cp -u "$dll" "$STAGE/bin/"
    done
    rmdir "$STAGE/lib" || true
    ;;
esac

# ---------------------------------------------------------------- Java runtime for BD-J
"$JAVA_HOME/bin/jlink" \
  --add-modules java.base,java.desktop,java.xml,java.naming,java.rmi,java.logging,jdk.unsupported \
  --strip-debug --no-header-files --no-man-pages --compress=2 \
  --output "$STAGE/jre"

# ---------------------------------------------------------------- licences
cp "$(dirname "$0")/../../LICENSE" "$STAGE/LICENSES/bdmenu-GPL-3.0.txt" 2>/dev/null || true
cat > "$STAGE/LICENSES/README.txt" <<'TXT'
This runtime bundles: libbluray (LGPL-2.1), FFmpeg built with --enable-gpl and libx264 (GPL-2.0+),
freetype (FTL/GPL-2.0), fontconfig (MIT) and an OpenJDK runtime image (GPL-2.0 with Classpath Exception).
Source for each is available from its upstream project at the versions recorded in build-info.txt.
TXT
{
  echo "rid=$RID"
  echo "ffmpeg=${FFMPEG_VERSION:-7.1.1}"
  echo "libbluray=${LIBBLURAY_VERSION:-1.3.4}"
  "$JAVA_HOME/bin/java" -version 2>&1 | head -1
} > "$STAGE/LICENSES/build-info.txt"

# ---------------------------------------------------------------- zip + checksum
zip_name="bdmenu-runtime-$RID.zip"
rm -f "$OUT/$zip_name"
( cd "$STAGE" && if command -v zip >/dev/null; then zip -qr9y "$OUT/$zip_name" .; else 7z a -tzip "$OUT/$zip_name" . >/dev/null; fi )
if command -v sha256sum >/dev/null; then
  sha256sum "$OUT/$zip_name" | awk '{ print $1 }' > "$OUT/$zip_name.sha256"
else
  shasum -a 256 "$OUT/$zip_name" | awk '{ print $1 }' > "$OUT/$zip_name.sha256"
fi
echo "Wrote $OUT/$zip_name ($(cat "$OUT/$zip_name.sha256"))"

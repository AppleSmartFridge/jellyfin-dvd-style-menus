#!/usr/bin/env bash
# Builds a small FFmpeg and libbluray (with the BD-J jar) into $PREFIX, then builds bdmenu.
# Works on Linux (glibc and musl), macOS and MSYS2 (UCRT64). Needs: a C toolchain, make, cmake,
# pkg-config, nasm, a JDK 8-17 with ant, freetype, fontconfig and libx264 development files.
#
#   PREFIX=/opt/bdmenu ./native/scripts/build-deps.sh
set -euo pipefail

FFMPEG_VERSION="${FFMPEG_VERSION:-7.1.1}"
LIBBLURAY_VERSION="${LIBBLURAY_VERSION:-1.3.4}"
NVCODEC_VERSION="${NVCODEC_VERSION:-n12.2.72.0}"
PREFIX="${PREFIX:-$PWD/build/prefix}"
WORK="${WORK:-$PWD/build/work}"
JOBS="${JOBS:-$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)}"
HERE="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$PREFIX" "$WORK"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig:${PKG_CONFIG_PATH:-}"

os="$(uname -s)"
case "$os" in
  Linux*) platform=linux ;;
  Darwin*) platform=macos ;;
  MINGW*|MSYS*) platform=windows ;;
  *) echo "unsupported OS $os"; exit 1 ;;
esac

fetch() { # url file
  [ -f "$WORK/$2" ] || curl -fsSL --retry 3 -o "$WORK/$2" "$1"
}

# ---------------------------------------------------------------- nv-codec-headers (NVENC, Linux/Windows x64)
hw_flags=()
if [ "$platform" != macos ] && [ "$(uname -m)" = x86_64 ]; then
  if [ ! -d "$WORK/nv-codec-headers" ]; then
    git clone --depth 1 --branch "$NVCODEC_VERSION" https://github.com/FFmpeg/nv-codec-headers.git "$WORK/nv-codec-headers"
  fi
  make -C "$WORK/nv-codec-headers" PREFIX="$PREFIX" install
  hw_flags+=(--enable-ffnvcodec --enable-nvenc --enable-encoder=h264_nvenc)
fi
case "$platform" in
  macos) hw_flags+=(--enable-videotoolbox --enable-encoder=h264_videotoolbox) ;;
  windows) hw_flags+=(--enable-mediafoundation --enable-encoder=h264_mf) ;;
esac

# ---------------------------------------------------------------- FFmpeg (only what bdmenu needs)
fetch "https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz" "ffmpeg.tar.xz"
rm -rf "$WORK/ffmpeg" && mkdir -p "$WORK/ffmpeg"
tar -xJf "$WORK/ffmpeg.tar.xz" -C "$WORK/ffmpeg" --strip-components=1
(
  cd "$WORK/ffmpeg"
  ./configure \
    --prefix="$PREFIX" \
    --enable-shared --disable-static \
    --enable-gpl --enable-libx264 \
    --disable-programs --disable-doc --disable-network --disable-autodetect \
    --disable-everything \
    --enable-avcodec --enable-avformat --enable-avutil --enable-swscale --enable-swresample \
    --enable-decoder=h264,hevc,vc1,mpeg1video,mpeg2video,ac3,eac3,dca,pcm_bluray,mp2,truehd \
    --enable-parser=h264,hevc,vc1,mpegvideo,ac3,dca,mpegaudio \
    --enable-encoder=aac,libx264 \
    --enable-muxer=mpegts \
    --enable-protocol=pipe,file \
    --enable-pthreads \
    ${hw_flags[@]+"${hw_flags[@]}"}
  make -j"$JOBS"
  make install
)

# ---------------------------------------------------------------- libbluray with BD-J
fetch "https://download.videolan.org/pub/videolan/libbluray/$LIBBLURAY_VERSION/libbluray-$LIBBLURAY_VERSION.tar.bz2" "libbluray.tar.bz2"
rm -rf "$WORK/libbluray" && mkdir -p "$WORK/libbluray"
tar -xjf "$WORK/libbluray.tar.bz2" -C "$WORK/libbluray" --strip-components=1
(
  cd "$WORK/libbluray"
  ./configure \
    --prefix="$PREFIX" \
    --enable-shared --disable-static \
    --disable-examples --without-libxml2 \
    --enable-bdjava-jar --with-java9 \
    JDK_HOME="${JAVA_HOME:?set JAVA_HOME to a JDK 8-17}"
  make -j"$JOBS"
  make install
)

# ---------------------------------------------------------------- bdmenu
cmake -S "$HERE/../bdmenu" -B "$WORK/bdmenu" -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_PREFIX_PATH="$PREFIX"
cmake --build "$WORK/bdmenu" --parallel "$JOBS"
cmake --install "$WORK/bdmenu"

echo "Built into $PREFIX"

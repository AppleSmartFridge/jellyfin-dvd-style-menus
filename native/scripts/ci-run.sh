#!/usr/bin/env bash
# Runs a build command in CI. If it fails, posts the end of its output (and of any configure logs)
# as GitHub error annotations, which are readable without signing in.
#
#   ./native/scripts/ci-run.sh <title> <command> [args...]
set -uo pipefail

title="$1"
shift
log="$(mktemp)"

"$@" 2>&1 | tee "$log"
status=${PIPESTATUS[0]}
if [ "$status" -eq 0 ]; then
  exit 0
fi

annotate() { # title file lines
  [ -f "$2" ] || return 0
  local msg
  msg="$(tail -n "$3" "$2" | sed -e 's/%/%25/g' -e 's/\r/%0D/g' | awk '{ printf "%s%%0A", $0 }')"
  echo "::error title=$1::$msg"
}

# Compiler and configure errors first: GitHub cuts long annotations short.
errors="$(mktemp)"
grep -nE 'error:|error [A-Z]+[0-9]+|ERROR|undefined reference|ld: |fatal:|No such file|not found' "$log" | grep -v 'warning:' | head -n 30 > "$errors" || true
[ -s "$errors" ] && annotate "$title (errors)" "$errors" 30
annotate "$title (output)" "$log" 25
annotate "$title (ffmpeg config.log)" "${WORK:-$PWD/build/work}/ffmpeg/ffbuild/config.log" 40
annotate "$title (libbluray config.log)" "${WORK:-$PWD/build/work}/libbluray/config.log" 40
exit "$status"

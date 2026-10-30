#!/bin/bash
# Refresh the committed macOS (Apple Silicon) mock_server prebuilt.
#
# CS2 does not run on macOS, so there is no macOS leg in build-native-plugins.yml and
# GitHub's macOS runners are expensive. Instead, the osx-arm64 mock_server is built
# LOCALLY on an Apple Silicon Mac and committed to the repo at
# prebuilt/osx-arm64/mock_server; the release pipeline folds it in alongside the
# CI-built win-x64/linux-x64 natives (see .github/actions/setup-native-assets and
# docs/RELEASING.md). Re-run this whenever the mock engine or its wire protocol changes.
#
# Requirements: an Apple Silicon Mac with the native toolchain the `release-unix`
# preset uses (Apple Clang, CMake, Ninja, a bootstrapped vcpkg). This is the same
# toolchain that produces the local osx-arm64 build during normal development.

set -euo pipefail

# Always run from the plugin root (one level up from scripts/).
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ "$(uname -sm)" != "Darwin arm64" ]]; then
  echo "error: this script must run on an Apple Silicon Mac (uname is '$(uname -sm)')." >&2
  echo "The bundled mock is osx-arm64 only; nothing else can produce a matching binary." >&2
  exit 1
fi

# The build directory is baked into the binary (see docs/build-path-privacy.md), and
# cmake/path-privacy.cmake maps away only the checkout it is part of. A linked worktree
# under some scratch directory therefore still discloses that directory's name and
# layout -- which is how an earlier mock build ended up advertising one.
# git-common-dir differs from git-dir only in a linked worktree. Both are resolved to
# real absolute paths first: --git-common-dir is reported RELATIVE TO THE CWD when run
# from a subdirectory, so comparing the raw strings reports every build as a worktree.
_git_dir="$(cd "$(git rev-parse --git-dir)" && pwd -P)"
_git_common_dir="$(cd "$(git rev-parse --git-common-dir)" && pwd -P)"
if [[ "$_git_dir" != "$_git_common_dir" ]]; then
  echo "error: refusing to build the shipped mock from a linked git worktree." >&2
  echo "Run this from the canonical checkout so the committed binary carries no scratch paths." >&2
  exit 1
fi

PRESET="release-unix"
BUILT="../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/Release/bin/osx-arm64/mock_server"
DEST="prebuilt/osx-arm64/mock_server"

echo "==> Configuring + building the mock_server target ($PRESET)"
cmake --preset "$PRESET"
cmake --build --preset "$PRESET" --target mock_server --parallel

if [[ ! -f "$BUILT" ]]; then
  echo "error: expected build output not found at $BUILT" >&2
  exit 1
fi

echo "==> Staging -> $DEST"
mkdir -p "$(dirname "$DEST")"
cp "$BUILT" "$DEST"

# Strip local symbols to shrink the committed binary. On Apple Silicon this leaves the
# linker's ad-hoc code signature intact, but re-sign explicitly so a stripped binary can
# never ship with an invalid signature (which would SIGKILL on other Macs).
echo "==> Stripping + ad-hoc re-signing"
strip "$DEST"
codesign --force --sign - "$DEST"
codesign --verify --verbose "$DEST"
chmod +x "$DEST"

echo "==> Smoke test"
"$DEST" --help >/dev/null 2>&1 || true   # exercises loader/signature; arg parsing is not asserted here

# Refuse to leave a binary staged that would disclose this machine's paths. CI enforces
# the same check on the committed file (ci.yml: path-privacy), so a leaking binary can
# never reach main -- but failing here means finding out now rather than in review.
echo "==> Checking for embedded build paths"
if ! python3 ../../.github/scripts/check-embedded-paths.py "$DEST"; then
  echo >&2
  echo "error: the freshly built mock still embeds absolute build paths." >&2
  echo "Reconfigure from scratch (rm -rf ../../artifacts/cmake/Cs2VideoGenerator.Plugin.GRPC/release-unix)" >&2
  echo "so the path-privacy flags in cmake/path-privacy.cmake are applied to every port." >&2
  rm -f "$DEST"
  exit 1
fi

echo
echo "Done. Committed prebuilt refreshed:"
ls -la "$DEST"
file "$DEST"
echo
echo "Remember to 'git add $DEST' and commit — this binary is intentionally tracked."

#!/bin/bash
# Builds Expanse for Windows in the dev VM, for Arm and x64 (one self-contained Expanse.exe each,
# zipped as Expanse-<version>-windows-<arm64|x64>.zip), and publishes them as a GitHub release:
# where Expanse's update check looks (src/Expanse/Updater.cs).
#
# Not code-signed yet, so SmartScreen warns on first run (see docs/spikes.md).
#
# Usage: scripts/release.sh            # version from src/Expanse/Expanse.csproj
#        DRY_RUN=1 scripts/release.sh  # everything except publishing: the zips in artifacts/release
#        VERSION=0.1.9 DRY_RUN=1 ...   # another version (for testing updates)
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Expanse/Expanse.csproj)}
OUT="artifacts/release"
GITHUB_REPO=invig/expanse-windows

echo "==> Expanse $VERSION for Windows"
if [[ -z "${DRY_RUN:-}" ]]; then
  git diff --quiet HEAD || { echo "Commit first: the release is tagged at HEAD." >&2; exit 1; }
  [[ "$(git rev-parse HEAD)" == "$(git rev-parse '@{u}')" ]] || { echo "Push first: GitHub tags the release at HEAD." >&2; exit 1; }
  if git ls-remote --tags origin "v$VERSION" | grep -q .; then
    echo "Tag v$VERSION already exists; bump <Version> in src/Expanse/Expanse.csproj." >&2
    exit 1
  fi
fi

scripts/vm.sh sync
rm -rf "$OUT"
mkdir -p "$OUT"
for arch in arm64 x64; do
  echo "==> Building win-$arch"
  zip="Expanse-$VERSION-windows-$arch.zip"
  scripts/vm.sh run "cd expanse-windows && (if exist out\\release rmdir /s /q out\\release) & dotnet publish src\\Expanse\\Expanse.csproj -c Release -r win-$arch --self-contained -nologo -v q -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:ExpanseRelease=true -p:Version=$VERSION -o out\\release\\$arch && powershell -NoProfile -Command \"Compress-Archive -Force -Path out\\release\\$arch\\* -DestinationPath out\\release\\$zip\""
  scripts/vm.sh fetch "out/release/$zip" "$OUT/$zip"
  unzip -l "$OUT/$zip" | tail -n +4 | sed '$d' | sed '$d'
done
shasum -a 256 "$OUT"/*.zip

if [[ -n "${DRY_RUN:-}" ]]; then
  echo "==> Dry run: not publishing. The zips are in $OUT"
  exit 0
fi

echo "==> Publishing GitHub release v$VERSION ($GITHUB_REPO)"
gh release create "v$VERSION" "$OUT"/*.zip --repo "$GITHUB_REPO" --target "$(git rev-parse HEAD)" \
  --title "Expanse $VERSION" --notes-file - <<NOTES
**Your PC, on an infinite canvas.**

Download the zip for your PC: **x64** for most PCs, **arm64** for Windows on Arm (Snapdragon laptops,
Surface Pro X). Unzip it into a folder of your own (in your user folder, not Program Files, so
Expanse can update itself) and run **Expanse.exe**. It runs on Windows 11, with nothing else to
install.

Expanse isn't code-signed yet, so Windows may say "Windows protected your PC": choose **More info**,
then **Run anyway**.

A Welcome window shows the shortcuts. See the [README](https://github.com/$GITHUB_REPO#readme) for
everything else.
NOTES
echo "==> Done"

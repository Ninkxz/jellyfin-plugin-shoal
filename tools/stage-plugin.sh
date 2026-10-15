#!/usr/bin/env bash
# Build the plugin (Release) and stage it for lab/deploy.sh:
#
#   tools/stage-plugin.sh            -> artifacts/plugin/{Jellyfin.Plugin.Shoal.dll,meta.json}
#   lab/deploy.sh artifacts/plugin
#
# meta.json is generated from build.yaml so the two never drift (jprm does the same for releases).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/plugin"

yget() { sed -n "s/^$1: *\"\(.*\)\" *$/\1/p" "$ROOT/build.yaml" | head -1; }
GUID="$(yget guid)"; VERSION="$(yget version)"; ABI="$(yget targetAbi)"; NAME="$(yget name)"; OWNER="$(yget owner)"
CATEGORY="$(yget category)"; OVERVIEW="$(yget overview)"
[ -n "$GUID" ] && [ -n "$VERSION" ] && [ -n "$ABI" ] || { echo "could not read guid/version/targetAbi from build.yaml" >&2; exit 1; }

dotnet build "$ROOT/Jellyfin.Shoal.sln" -c Release --nologo -v quiet
rm -rf "$OUT"; mkdir -p "$OUT"
cp "$ROOT/src/Jellyfin.Plugin.Shoal/bin/Release/net10.0/Jellyfin.Plugin.Shoal.dll" "$OUT/"

cat > "$OUT/meta.json" <<EOF
{
  "category": "$CATEGORY",
  "changelog": "spike build",
  "description": "$OVERVIEW",
  "guid": "$GUID",
  "name": "$NAME",
  "overview": "$OVERVIEW",
  "owner": "$OWNER",
  "targetAbi": "$ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": []
}
EOF
echo "staged:"; ls -l "$OUT"

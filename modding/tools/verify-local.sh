#!/usr/bin/env bash
# Local verification without a game install:
#   1. builds minimal UnityEngine.CoreModule / Mirror stub assemblies,
#   2. type-checks the mod source against the REAL ref/gamefiles/managed Dew DLLs (check project),
#   3. runs the real mod csproj's wildcard-reference pipeline against a fake Managed folder,
#   4. cleans all outputs (stub-built DLLs are NOT installable — stub Debug.Log is a no-op).
# Requires: dotnet SDK 8+ at ~/.dotnet (or in PATH), NuGet access for reference-assembly pkg.
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

GAME_MANAGED="${SOD_GAME_MANAGED:-../ref/gamefiles/managed}"
FAKE="tools/fake-managed"

echo "== [1/3] stub assemblies (UnityEngine.CoreModule, Mirror) =="
dotnet build tools/build-check/stubs/UnityEngine.CoreModule/UnityEngine.CoreModule.csproj -c Release -v q -nologo
dotnet build tools/build-check/stubs/Mirror/Mirror.csproj -c Release -v q -nologo

echo "== [2/3] check project: mod source x real Dew DLLs =="
dotnet build tools/build-check/BossPracticeOverlay.check.csproj -v q -nologo

echo "== [3/3] real csproj reference pipeline via fake Managed =="
mkdir -p "$FAKE"
cp tools/build-check/bin-stubs/*.dll "$FAKE"/
cp "$GAME_MANAGED/Dew.Core.dll" "$GAME_MANAGED/Dew.Contents.dll" "$GAME_MANAGED/Newtonsoft.Json.dll" "$FAKE"/
dotnet build BossPracticeOverlay/BossPracticeOverlay.csproj -c Release -v q -nologo \
  -p:GameManaged="$PWD/$FAKE"

# cleanup — stub-linked artifacts must never be installed
# (PlaceDllForInstall copies to the project root; remove that copy too on cleanup)
rm -rf "$FAKE" BossPracticeOverlay/bin BossPracticeOverlay/obj tools/build-check/bin-check tools/build-check/obj
rm -f BossPracticeOverlay/BossPracticeOverlay.dll

echo "OK: mod source compiles against real Dew.Core.dll/Dew.Contents.dll and the csproj pipeline works."

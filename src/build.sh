#!/bin/bash
# 内构透视：与其余 mod 一样用 VS2022 BuildTools 自带的 Roslyn csc 编译。
set -e

# Set GHPC_DIR / CSC if your install lives somewhere else.
BIN="${GHPC_DIR:-/e/steam/steamapps/common/Gunner HEAT PC/Bin}"
MGD="$BIN/GHPC_Data/Managed"
ML="$BIN/MelonLoader/net35"
CSC="${CSC:-/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe}"
SRC="$(cd "$(dirname "$0")" && pwd)"
OUT="$SRC/bin"

mkdir -p "$OUT"

"$CSC" -nologo -noconfig -target:library -optimize+ -langversion:latest -codepage:65001 \
  -out:"$OUT/XrayInterior.dll" \
  -r:System.dll \
  -r:System.Core.dll \
  -r:"$MGD/netstandard.dll" \
  -r:"$ML/MelonLoader.dll" \
  -r:"$ML/0Harmony.dll" \
  -r:"$ML/Tomlet.dll" \
  -r:"$MGD/UnityEngine.dll" \
  -r:"$MGD/UnityEngine.CoreModule.dll" \
  -r:"$MGD/UnityEngine.UIModule.dll" \
  -r:"$MGD/UnityEngine.TextRenderingModule.dll" \
  -r:"$MGD/UnityEngine.IMGUIModule.dll" \
  -r:"$MGD/UnityEngine.InputLegacyModule.dll" \
  -r:"$MGD/UnityEngine.PhysicsModule.dll" \
  -r:"$MGD/UnityEngine.UI.dll" \
  -r:"$MGD/Assembly-CSharp.dll" \
  "$SRC/XrayInteriorMod.cs"

if [ "$1" == "deploy" ]; then
  cp "$OUT/XrayInterior.dll" "$BIN/Mods/XrayInterior.dll"
  echo "deployed -> $BIN/Mods/XrayInterior.dll"
fi

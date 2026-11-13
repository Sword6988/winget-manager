#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# 单文件分发产物构建脚本（7-Zip SFX 自解压安装器）
#
# 产物：dist/WingetManager-<版本>-win-x64-sfx.exe
#   双击 → 确认 → 解压到临时目录 → 拷贝到 %LOCALAPPDATA%\Programs\WingetManager
#   → 启动应用 → 自动清理临时目录。解压后的目录结构与 publish 完全一致。
#
# 前置条件：
#   - dotnet（AOT publish 需 MSVC/WinSDK 工具链，见 ARCHITECTURE.md）
#   - 7-Zip（7z.exe）安装于 C:\Program Files\7-Zip\
#   - packaging/7zSD.sfx（官方安装器 SFX 模块，取自 7-zip.org/a/lzma<ver>.7z，
#     Public Domain；注意 7z.sfx/7zCon.sfx 不实现安装器管线，InstallPath/
#     RunProgram 会被忽略，必须用 7zSD.sfx）
# 用法：在仓库根目录执行  bash packaging/build-dist.sh
# ═══════════════════════════════════════════════════════════════════
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

VERSION="$(grep -oP '(?<=public const string Version = ")[^"]+' src/WingetManager.App/App.xaml.cs)"
SEVENZIP="/c/Program Files/7-Zip/7z.exe"
SFX_MODULE="packaging/7zSD.sfx"
SFX_CONFIG="packaging/sfx-config.txt"
INSTALL_CMD="packaging/install.cmd"

PUBLISH_DIR="src/WingetManager.App/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/publish"
OUT_DIR="dist"
ARCHIVE="$(mktemp -d)/payload.7z"
OUT_FILE="$OUT_DIR/WingetManager-$VERSION-win-x64-sfx.exe"

mkdir -p "$OUT_DIR"

# 1. AOT publish（含 MSVC/WinSDK 工具链环境，见团队环境注意项）
MSVC='/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC/14.44.35207'
WINSDK='/c/Program Files (x86)/Windows Kits/10'
MSVC_WIN='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
WINSDK_WIN='C:\Program Files (x86)\Windows Kits\10'
env 'ProgramData=C:\ProgramData' 'ProgramFiles=C:\Program Files' \
    'ProgramFiles(x86)=C:\Program Files (x86)' 'OS=Windows_NT' \
    'PATHEXT=.COM;.EXE;.BAT;.CMD' 'SystemRoot=C:\WINDOWS' \
    PATH="$MSVC/bin/Hostx64/x64:$WINSDK/bin/10.0.26100.0/x64:$PATH" \
    LIB="$MSVC_WIN\\lib\\x64;$WINSDK_WIN\\Lib\\10.0.26100.0\\um\\x64;$WINSDK_WIN\\Lib\\10.0.26100.0\\ucrt\\x64" \
    "/c/Program Files/dotnet/dotnet.exe" publish src/WingetManager.App \
    -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true

[ -f "$PUBLISH_DIR/WingetManager.exe" ] || { echo "publish 产物缺失：$PUBLISH_DIR"; exit 1; }

# 2. 压缩 publish 目录内容（归档根即 publish 根）+ install.cmd
rm -f "$ARCHIVE"
( cd "$PUBLISH_DIR" && "$SEVENZIP" a -mx9 "$ARCHIVE" . -xr'!'.vs > /dev/null )
( cd packaging && "$SEVENZIP" a "$ARCHIVE" install.cmd > /dev/null )

# 3. 拼接 SFX：模块 + 配置(UTF-8 无 BOM) + 归档
cat "$SFX_MODULE" "$SFX_CONFIG" "$ARCHIVE" > "$OUT_FILE"

echo "════════════════════════════════════════"
echo "产物：$OUT_FILE"
ls -lh "$OUT_FILE"
echo "校验归档条目："
"$SEVENZIP" l "$ARCHIVE" | head -12

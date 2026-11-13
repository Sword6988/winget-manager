#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# 单文件分发产物构建脚本（7-Zip SFX 自解压）
#
# 两种产物（同一份载荷，只差引导脚本与配置）：
#
#   安装版  dist/WingetManager-<版本>-win-x64-sfx.exe
#     双击 → 解压到临时目录 → 拷贝到 %LOCALAPPDATA%\Programs\WingetManager
#     → 启动 → 清理临时目录。装过同版本则跳过拷贝直接启动。
#
#   便携版  dist/WingetManager-<版本>-win-x64-portable.exe
#     双击 → 解压到 %TEMP%\7zSxxxx → **直接在临时目录里运行** → 关窗后
#     整个解压目录被删除，程序文件零残留（用户数据仍在
#     %LOCALAPPDATA%\WingetManager）。代价是每次启动多花 1~2 秒解压。
#
# 前置条件：
#   - dotnet SDK 8+（AOT publish 需 MSVC/WinSDK 工具链，见 ARCHITECTURE.md）
#   - 7-Zip（7z.exe）
#   - packaging/7zSD.sfx（官方安装器 SFX 模块，取自 7-zip.org/a/lzma<ver>.7z，
#     Public Domain；注意 7z.sfx/7zCon.sfx 不实现安装器管线，InstallPath/
#     RunProgram 会被忽略，必须用 7zSD.sfx）
#     SFX 图标由 packaging/retarget-sfx-icon.py 一次性写入，不需要每次构建重跑
#     （该脚本会追加新节，重复执行会不断增大模块体积）。
#
# 用法：
#   bash packaging/build-dist.sh                  构建安装版（publish + 打包）
#   bash packaging/build-dist.sh --portable       构建便携版
#   bash packaging/build-dist.sh both             两个都出
#   bash packaging/build-dist.sh --skip-publish   复用上次 publish 产物，只重打包
#
# 覆盖项（可选环境变量）：
#   DOTNET / SEVENZIP / MSVC_DIR / WINSDK_DIR / WINSDK_VER / STAGE_DIR
#   —— 工具链默认自动探测：vswhere → VC\Tools\MSVC 最新版本；
#      Windows Kits\10 下含 um+ucrt 的最高版本。
# ═══════════════════════════════════════════════════════════════════
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

SKIP_PUBLISH=0
MODES="install"
for arg in "$@"; do
    case "$arg" in
        --skip-publish) SKIP_PUBLISH=1 ;;
        --portable) MODES="portable" ;;
        both) MODES="install portable" ;;
        -h|--help) sed -n '2,34p' "$0"; exit 0 ;;
        *) echo "未知参数：$arg（支持 --portable / both / --skip-publish）" >&2; exit 2 ;;
    esac
done

VERSION="$(grep -oP '(?<=public const string Version = ")[^"]+' src/WingetManager.App/App.xaml.cs)"
[ -n "$VERSION" ] || { echo "无法从 App.xaml.cs 解析版本号"; exit 1; }

SFX_MODULE="packaging/7zSD.sfx"
OUT_DIR="dist"
STAGE_DIR="${STAGE_DIR:-$OUT_DIR/.stage/app}"

mkdir -p "$OUT_DIR"


# ── 工具定位辅助 ────────────────────────────────────────────────────
win_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }

find_7z() {
    if [ -n "${SEVENZIP:-}" ]; then echo "$SEVENZIP"; return; fi
    local c
    for c in "/c/Program Files/7-Zip/7z.exe" "/c/Program Files (x86)/7-Zip/7z.exe"; do
        [ -f "$c" ] && { echo "$c"; return; }
    done
    c="$(command -v 7z || command -v 7za || true)"
    [ -n "$c" ] || { echo "未找到 7z.exe，请安装 7-Zip 或设置 SEVENZIP" >&2; exit 1; }
    echo "$c"
}

find_dotnet() {
    if [ -n "${DOTNET:-}" ]; then echo "$DOTNET"; return; fi
    local c
    for c in "/c/Program Files/dotnet/dotnet.exe"; do
        [ -f "$c" ] && { echo "$c"; return; }
    done
    c="$(command -v dotnet || true)"
    [ -n "$c" ] || { echo "未找到 dotnet，请设置 DOTNET" >&2; exit 1; }
    echo "$c"
}

# VC 工具目录：优先显式指定，其次 vswhere，最后按已知安装位置兜底
find_msvc() {
    if [ -n "${MSVC_DIR:-}" ]; then echo "$MSVC_DIR"; return; fi
    local vswhere="/c/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe" vs root base
    if [ -x "$vswhere" ]; then
        vs="$("$vswhere" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>/dev/null | tr -d '\r' | head -1)"
        if [ -n "$vs" ]; then
            root="$(win_path_to_unix "$vs")/VC/Tools/MSVC"
            base="$(latest_child "$root")"
            [ -n "$base" ] && { echo "$root/$base"; return; }
        fi
    fi
    local c
    for c in "/c/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC" \
             "/c/Program Files/Microsoft Visual Studio/2022/BuildTools/VC/Tools/MSVC" \
             "/c/Program Files/Microsoft Visual Studio/2022/Community/VC/Tools/MSVC"; do
        [ -d "$c" ] || continue
        base="$(latest_child "$c")"
        [ -n "$base" ] && { echo "$c/$base"; return; }
    done
    echo "未找到 MSVC 工具链（需 Visual Studio Build Tools + C++ 工作负载），可设置 MSVC_DIR" >&2
    exit 1
}

# Windows 风格路径（C:\...）→ Git Bash 风格（/c/...），便于 [ -d ] 判断
win_path_to_unix() {
    local p="$1"
    if command -v cygpath >/dev/null 2>&1; then cygpath -u "$p"; else echo "$p"; fi
}

# 取目录下版本号最大的子目录名（按版本排序，非字典序）
latest_child() {
    local dir="$1"
    [ -d "$dir" ] || return 0
    ls -1 "$dir" 2>/dev/null | grep -E '^[0-9]+(\.[0-9]+)+$' | sort -V | tail -1
}

find_winsdk_root() {
    if [ -n "${WINSDK_DIR:-}" ]; then echo "$WINSDK_DIR"; return; fi
    local c
    for c in "/c/Program Files (x86)/Windows Kits/10" "/c/Program Files/Windows Kits/10"; do
        [ -d "$c" ] && { echo "$c"; return; }
    done
    echo "未找到 Windows SDK（Windows Kits\\10），可设置 WINSDK_DIR" >&2
    exit 1
}

# SDK 版本：Include\um + Lib\um\x64 + bin\x64\rc.exe 齐全的最高版本
# （cl.exe 属于 MSVC，不在 Windows Kits 内；SDK 侧真正被用到的是 rc.exe）
find_winsdk_ver() {
    local root="$1" v
    if [ -n "${WINSDK_VER:-}" ]; then echo "$WINSDK_VER"; return; fi
    for v in $(ls -1 "$root/Include" 2>/dev/null | grep -E '^[0-9]+(\.[0-9]+)+$' | sort -Vr); do
        if [ -d "$root/Include/$v/um" ] && [ -d "$root/Lib/$v/um/x64" ] && [ -f "$root/bin/$v/x64/rc.exe" ]; then
            echo "$v"; return
        fi
    done
    echo "Windows Kits\\10 下没有可用的 SDK 版本（需 Include\\um + Lib\\um\\x64 + bin\\x64\\rc.exe）" >&2
    exit 1
}

SEVENZIP="$(find_7z)"
DOTNET_BIN="$(find_dotnet)"

# ── 1. AOT publish ─────────────────────────────────────────────────
if [ "$SKIP_PUBLISH" -eq 0 ]; then
    MSVC="$(find_msvc)"
    WINSDK="$(find_winsdk_root)"
    SDKVER="$(find_winsdk_ver "$WINSDK")"
    echo "工具链：MSVC $(basename "$MSVC") / WinSDK $SDKVER"

    # AOT 需要 cl.exe / link.exe 与 LIB 可被 ILCompiler 直接找到；
    # 沙箱环境下还缺若干 Windows 基础环境变量，这里补齐（已存在则沿用）。
    # 注意：ProgramFiles(x86) 不是合法的 bash 变量名，只能按系统盘拼字面量。
    SYSDRIVE="${SYSTEMDRIVE:-C:}"
    rm -rf "$STAGE_DIR"
    env "ProgramData=$SYSDRIVE\\ProgramData" \
        "ProgramFiles=$SYSDRIVE\\Program Files" \
        "ProgramFiles(x86)=$SYSDRIVE\\Program Files (x86)" \
        "OS=${OS:-Windows_NT}" \
        "PATHEXT=${PATHEXT:-.COM;.EXE;.BAT;.CMD}" \
        "SystemRoot=${SystemRoot:-$SYSDRIVE\\WINDOWS}" \
        PATH="$MSVC/bin/Hostx64/x64:$WINSDK/bin/$SDKVER/x64:$PATH" \
        LIB="$(win_path "$MSVC")\\lib\\x64;$(win_path "$WINSDK")\\Lib\\$SDKVER\\um\\x64;$(win_path "$WINSDK")\\Lib\\$SDKVER\\ucrt\\x64" \
        "$DOTNET_BIN" publish src/WingetManager.App \
        -c Release -r win-x64 -p:IlcUseEnvironmentalTools=true -o "$STAGE_DIR"
fi

[ -f "$STAGE_DIR/WingetManager.exe" ] || {
    echo "publish 产物缺失：$STAGE_DIR/WingetManager.exe（先跑一次完整构建，勿用 --skip-publish）" >&2
    exit 1
}

# ── 1.5 精简载荷 ────────────────────────────────────────────────────
# AOT 会在 publish 目录留下 58 MB 的 WingetManager.pdb（调试符号）。
# 分发包不需要它：删掉后载荷 130 MB → 72 MB，SFX 体积直接减半。
# install.cmd 里还有 /xf *.pdb，负责把旧安装里已有的 pdb 一并清掉。
find "$STAGE_DIR" -maxdepth 1 -name '*.pdb' -type f -delete
# 版本标记：install.cmd 用它判断"已装同版本 → 直接启动"，避免重复拷贝。
printf '%s' "$VERSION" > "$STAGE_DIR/version.txt"

# ── 2/3. 打包一个 SFX ───────────────────────────────────────────────
# $1 = 引导脚本（packaging/ 下）  $2 = 配置模板  $3 = 产物路径  $4 = 说明
build_sfx() {
    local launcher="$1" config="$2" out="$3" label="$4"
    # 用仓库内的相对路径做临时目录：本环境的 mktemp -d 返回 C:\... 形式，
    # rm 的安全包装器会拒绝"带盘符"的路径，导致清理失败（set -e 直接中断）。
    local tmpdir archive rendered abs_archive
    tmpdir="$OUT_DIR/.tmp"
    rm -rf "$tmpdir"
    mkdir -p "$tmpdir"
    archive="$tmpdir/payload.7z"
    rendered="$tmpdir/sfx-config.txt"

    sed "s/@VERSION@/$VERSION/" "$config" > "$rendered"
    abs_archive="$(pwd)/$archive"
    ( cd "$STAGE_DIR" && "$SEVENZIP" a -mx9 "$abs_archive" . > /dev/null )
    ( cd packaging && "$SEVENZIP" a "$abs_archive" "$launcher" > /dev/null )
    cat "$SFX_MODULE" "$rendered" "$archive" > "$out"

    echo "════════════════════════════════════════"
    echo "[$label] 产物：$out"
    echo "[$label] 载荷：$(du -sh "$STAGE_DIR" | cut -f1)（已剔除 pdb）"
    ls -lh "$out"
    echo "[$label] 归档条目校验（应含 $launcher / WingetManager.exe，不含 pdb）："
    "$SEVENZIP" l "$archive" 2>/dev/null | grep -E "$launcher|version\.txt|WingetManager\.exe|\.pdb|files" | head -6 || true
    rm -rf "$tmpdir"
}

for MODE in $MODES; do
    case "$MODE" in
        install)
            build_sfx install.cmd packaging/sfx-config.txt \
                "$OUT_DIR/WingetManager-$VERSION-win-x64-sfx.exe" "安装版"
            ;;
        portable)
            build_sfx launch-portable.cmd packaging/sfx-config-portable.txt \
                "$OUT_DIR/WingetManager-$VERSION-win-x64-portable.exe" "便携版"
            ;;
    esac
done

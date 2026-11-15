"""下载并解压离线构建依赖（无需 Visual Studio，也无需管理员权限）。

产物布局（供 tools/build-local 使用）：
    tools/build-deps/roslyn/csc.exe                         Roslyn 4.8 编译器（.NET Framework 版）
    tools/build-deps/refasm/build/.NETFramework/v4.8/*.dll  官方 .NET Framework 4.8 参考程序集

两个包都来自 nuget.org 官方源：
    Microsoft.Net.Compilers.Toolset 4.8.0
    Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3

用法： python tools/fetch_build_deps.py
"""

import io
import os
import sys
import zipfile

try:
    from urllib.request import urlopen
except ImportError:  # pragma: no cover
    print("需要 Python 3")
    sys.exit(2)

BASE = os.path.dirname(os.path.abspath(__file__))
DEPS = os.path.join(BASE, "build-deps")

PACKAGES = [
    (
        "https://www.nuget.org/api/v2/package/Microsoft.Net.Compilers.Toolset/4.8.0",
        "roslyn.nupkg",
        "tasks/net472/",
        os.path.join(DEPS, "roslyn"),
    ),
    (
        "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3",
        "refasm.nupkg",
        "build/.NETFramework/v4.8/",
        os.path.join(DEPS, "refasm", "build", ".NETFramework", "v4.8"),
    ),
]


def fetch(url, dest):
    if os.path.exists(dest) and os.path.getsize(dest) > 1024 * 1024:
        print("  已存在，跳过下载：%s" % os.path.basename(dest))
        return

    print("  下载 %s ..." % url)
    with urlopen(url, timeout=120) as resp:
        data = resp.read()
    with open(dest, "wb") as f:
        f.write(data)
    print("  %s (%.1f MB)" % (os.path.basename(dest), len(data) / 1048576.0))


def extract(nupkg, prefix, dest):
    with zipfile.ZipFile(nupkg) as z:
        count = 0
        for name in z.namelist():
            if not name.lower().startswith(prefix.lower()):
                continue
            rel = name[len(prefix):].replace("/", os.sep)
            if not rel or rel.endswith(os.sep):
                continue
            out = os.path.join(dest, rel)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with z.open(name) as src, open(out, "wb") as dst:
                dst.write(src.read())
            count += 1
    print("  解压 %d 个文件 -> %s" % (count, dest))


def main():
    if not os.path.isdir(DEPS):
        os.makedirs(DEPS)

    for url, filename, prefix, dest in PACKAGES:
        nupkg = os.path.join(DEPS, filename)
        fetch(url, nupkg)
        extract(nupkg, prefix, dest)

    checks = [
        os.path.join(DEPS, "roslyn", "csc.exe"),
        os.path.join(DEPS, "refasm", "build", ".NETFramework", "v4.8", "mscorlib.dll"),
        os.path.join(DEPS, "refasm", "build", ".NETFramework", "v4.8", "PresentationFramework.dll"),
    ]
    missing = [c for c in checks if not os.path.exists(c)]
    if missing:
        print("\n[失败] 缺少关键文件：")
        for m in missing:
            print("  " + m)
        return 1

    print("\n[完成] 离线工具链就绪，现在可以直接运行 build-release.bat")
    return 0


if __name__ == "__main__":
    sys.exit(main())

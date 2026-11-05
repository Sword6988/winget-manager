"""查「引用了但没导入」的全局名 —— 通配符导入留下的暗坑专用。

静态算出模块里所有「必须来自全局命名空间」的标识符，再减去模块级定义、
通配符来源模块的公开名与内建名，剩下的就是运行到那行才会炸的 NameError。

跑法（项目根目录）：
    python tools/check_globals.py
"""
from __future__ import annotations

import ast
import builtins
import importlib
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
TARGETS = ["qt_common.py", "qt_ui.py", "qt_app.py"]


def _star_exports(module_name: str) -> set[str]:
    """通配符导入实际能带进来的名字。"""
    try:
        mod = importlib.import_module(module_name)
    except Exception:  # noqa: BLE001  导不进来就当作没有
        return set()
    return {n for n in dir(mod) if not n.startswith("_")}


def _scope_bound(node: ast.AST) -> set[str]:
    """某段代码里被「绑定」的名字：赋值/形参/推导式变量/except/with/内嵌 def。"""
    bound: set[str] = set()
    for n in ast.walk(node):
        if isinstance(n, ast.Name) and isinstance(n.ctx, (ast.Store, ast.Del)):
            bound.add(n.id)
        elif isinstance(n, ast.arg):
            bound.add(n.arg)
        elif isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            bound.add(n.name)
        elif isinstance(n, ast.ExceptHandler) and n.name:
            bound.add(n.name)
        elif isinstance(n, (ast.Import, ast.ImportFrom)):
            for a in n.names:
                bound.add((a.asname or a.name).split(".")[0])
    return bound


def check(path: Path) -> list[str]:
    tree = ast.parse(path.read_text(encoding="utf-8"))
    module_level: set[str] = set()
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            module_level.add(node.name)
        elif isinstance(node, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
            for n in ast.walk(node):
                if isinstance(n, ast.Name) and isinstance(n.ctx, ast.Store):
                    module_level.add(n.id)
        elif isinstance(node, (ast.If, ast.Try, ast.With)):
            module_level |= _scope_bound(node)      # if/else 里的模块级赋值
        elif isinstance(node, (ast.Import, ast.ImportFrom)):
            if isinstance(node, ast.ImportFrom) and any(a.name == "*" for a in node.names):
                module_level |= _star_exports(node.module or "")
            for a in node.names:
                if a.name != "*":
                    module_level.add((a.asname or a.name).split(".")[0])

    problems: list[str] = []
    base = module_level | set(dir(builtins)) | {"__file__", "__name__"}

    def visit(node: ast.AST, bound: set[str]) -> None:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            inner = bound | _scope_bound(node)      # 闭包：沿用外层已绑定的名字
            for child in node.body:
                visit(child, inner)
            return
        if isinstance(node, ast.ClassDef):
            # 类体是个独立命名空间：同类里先定义的方法名可直接引用（Property(...)）
            inner = bound | _scope_bound(node)
            for child in node.body:
                visit(child, inner)
            return
        if (isinstance(node, ast.Name) and isinstance(node.ctx, ast.Load)
                and node.id not in bound):
            problems.append(f"{path.name}:{node.lineno} 未导入的全局名: {node.id}")
        for child in ast.iter_child_nodes(node):
            visit(child, bound)

    visit(tree, base)
    return sorted(set(problems), key=lambda s: int(s.split(":")[1].split()[0]))


def main() -> int:
    out: list[str] = []
    for name in TARGETS:
        out += check(ROOT / name)
    for line in out:
        print(line)
    print(f"\n发现 {len(out)} 处")
    return 1 if out else 0


if __name__ == "__main__":
    sys.exit(main())

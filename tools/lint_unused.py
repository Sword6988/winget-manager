"""轻量静态检查：没有 ruff/pyflakes 的环境也能跑。

两项检查（都只报「确定无用」的，不猜）：
  1. 未使用的 import —— 该行带 `# noqa` 视为有意转出，跳过；
  2. 从未被引用的模块级常量 —— 令牌表里删了用法却没删定义时抓它。

跑法（项目根目录）：
    python tools/lint_unused.py
"""
from __future__ import annotations

import ast
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKIP_DIRS = {"legacy", "docs", "build", "dist", "__pycache__", ".venv"}


def py_files() -> list[Path]:
    out = []
    for p in ROOT.rglob("*.py"):
        if SKIP_DIRS & set(p.relative_to(ROOT).parts):
            continue
        out.append(p)
    return out


def _loaded_names(tree: ast.AST) -> set[str]:
    """模块里出现过的所有标识符（读 + 写 + 属性名都算，宁可漏报不误报）。"""
    names: set[str] = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Name):
            names.add(node.id)
        elif isinstance(node, ast.Attribute):
            names.add(node.attr)
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            names.add(node.name)
        elif isinstance(node, ast.arg):
            names.add(node.arg)
        elif isinstance(node, ast.keyword) and node.arg:
            names.add(node.arg)
    return names


def check_unused_imports(path: Path, tree: ast.AST) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    used = _loaded_names(tree)
    problems = []
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Import, ast.ImportFrom)):
            continue
        if isinstance(node, ast.ImportFrom) and node.module == "__future__":
            continue
        # 多行 import 的 noqa 常写在收尾括号那行，按整段语句取源码
        span = "\n".join(lines[node.lineno - 1:node.end_lineno or node.lineno])
        if "# noqa" in span or "import *" in span:
            continue
        for alias in node.names:
            name = (alias.asname or alias.name).split(".")[0]
            if name != "*" and name not in used:
                problems.append(f"{path.relative_to(ROOT)}:{node.lineno} 未使用 import: {name}")
    return problems


def check_dead_constants(files: list[Path]) -> list[str]:
    """模块级全大写常量：全项目引用数为 0 才报。"""
    consts: dict[str, Path] = {}
    trees = {}
    for path in files:
        tree = ast.parse(path.read_text(encoding="utf-8"))
        trees[path] = tree
        for node in tree.body:
            if isinstance(node, ast.Assign):
                for tgt in node.targets:
                    if isinstance(tgt, ast.Name) and tgt.id.isupper():
                        consts[tgt.id] = path

    references: dict[str, int] = {k: 0 for k in consts}
    for path, tree in trees.items():
        for node in ast.walk(tree):
            if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Load):
                if node.id in references:
                    references[node.id] += 1

    return [f"{consts[n].relative_to(ROOT)} 未引用常量: {n}"
            for n, c in references.items() if c == 0 and n != "__all__"]


def main() -> int:
    files = py_files()
    problems: list[str] = []
    for path in files:
        tree = ast.parse(path.read_text(encoding="utf-8"))
        problems += check_unused_imports(path, tree)
    problems += check_dead_constants(files)

    for line in problems:
        print(line)
    print(f"\n扫描 {len(files)} 个文件，发现 {len(problems)} 处")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

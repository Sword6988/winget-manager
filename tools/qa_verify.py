# -*- coding: utf-8 -*-
"""
QA 静态验证脚本（纯标准库，无第三方依赖）。

用法:
    python tools/qa_verify.py

输出:
    1) 控制台问题清单（severity: BLOCKER / MAJOR / MINOR / INFO）
    2) docs/QA-REPORT-raw-data.md（供 docs/QA-REPORT.md 引用的原始数据）

检查范围（被测工程 WingetManagerWpf/WingetManagerWpf/）：
  A. XAML 结构：可解析、x:Class 与路径一致、partial class 配套、x:Name 不重复、
     Page/ApplicationDefinition 与 csproj 一致
  B. 事件处理器存在性（XAML 事件属性 ↔ .xaml.cs 方法）
  C. 资源解析（StaticResource/DynamicResource/TryFindResource/FindResource）
  D. 绑定路径粗查（Binding 属性存在于 ViewModel / DP）
  E. csproj 完整性（磁盘 ↔ Compile/Page 双向配对）
  F. C# 语法风险（括号配平状态机、#if 配平、summary 闭合、中文标识符、
     async void / await 粗查、using 命名空间白名单）
  G. .NET Framework 4.8 API 禁用清单
  H. 分层纪律（Core/Models/Services/ViewModels 禁用 System.Windows*）
  I. x:Static 引用存在性（Metrics/Strings 等常量）
  J. 与原版（_ref/）的文案一致性抽查
"""
import io
import os
import re
import sys
import xml.etree.ElementTree as ET

def find_project_base(start):
    """从脚本位置向上找「工程所在的父目录」。

    约定：BASE 是**仓库目录的上一层**，被测工程固定在
    ``BASE/WingetManagerWpf/WingetManagerWpf/``。
    这样脚本放在 ``tools/``、``.workbuddy/checks/`` 或任何子目录里都能跑，
    换一台机器也不用改路径——之前这里写死过 D:\\Program Files\\... 的绝对路径，
    换个目录跑就直接失效。
    """
    marker = os.path.join("WingetManagerWpf", "WingetManagerWpf", "WingetManagerWpf.csproj")
    current = os.path.abspath(start)
    for _ in range(8):
        if os.path.isfile(os.path.join(current, marker)):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            break
        current = parent
    # 找不到就退回「脚本在 <BASE>/<两层子目录>/ 下」的老约定
    return os.path.dirname(os.path.dirname(os.path.abspath(start)))


BASE = find_project_base(os.path.dirname(os.path.abspath(__file__)))
PROJ = os.path.join(BASE, "WingetManagerWpf", "WingetManagerWpf")
ROOT_NS = "WingetManagerWpf"
CS_NS = "http://schemas.microsoft.com/developer/msbuild/2003"
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"
OUT_RAW = os.path.join(BASE, "WingetManagerWpf", "docs", "QA-REPORT-raw-data.md")

ISSUES = []


def issue(severity, code, rel_path, line, message):
    ISSUES.append({
        "severity": severity,
        "code": code,
        "file": rel_path,
        "line": line,
        "message": message,
    })


def rel(path):
    return os.path.relpath(path, PROJ).replace("\\", "/")


def read_text(path):
    with io.open(path, "r", encoding="utf-8-sig") as f:
        return f.read()


# ════════════════════ 收集文件 ════════════════════

def collect(ext):
    result = []
    for root, dirs, files in os.walk(PROJ):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", ".vs")]
        for name in files:
            if name.endswith(ext):
                result.append(os.path.join(root, name))
    return sorted(result)


XAML_FILES = collect(".xaml")
CS_FILES = collect(".cs")
CSPROJ = os.path.join(PROJ, "WingetManagerWpf.csproj")

# ════════════════════ E. csproj 完整性 ════════════════════

def parse_csproj():
    tree = ET.parse(CSPROJ)
    root = tree.getroot()
    compiles, pages, appdef, icon, resources = [], [], [], None, []
    for item in root.iter("{%s}Compile" % CS_NS):
        compiles.append(item.get("Include", ""))
    for item in root.iter("{%s}Page" % CS_NS):
        pages.append(item.get("Include", ""))
    for item in root.iter("{%s}ApplicationDefinition" % CS_NS):
        appdef.append(item.get("Include", ""))
    for item in root.iter("{%s}ApplicationIcon" % CS_NS):
        icon = item.get("Include")
    for tag in ("Resource", "Content", "None", "EmbeddedResource"):
        for item in root.iter("{%s}%s" % (CS_NS, tag)):
            inc = item.get("Include", "")
            if inc:
                resources.append(inc)
    return compiles, pages, appdef, icon, resources


def check_csproj():
    compiles, pages, appdef, icon, resources = parse_csproj()
    norm = lambda p: p.replace("\\", "/").lower()

    disk_cs = {rel(p).replace("\\", "/").lower() for p in CS_FILES}
    disk_xaml = {rel(p).replace("\\", "/").lower() for p in XAML_FILES}
    proj_cs = {norm(c) for c in compiles}
    proj_xaml = {norm(p) for p in pages} | {norm(a) for a in appdef}

    for f in sorted(disk_cs - proj_cs):
        issue("MAJOR", "CSPROJ-MISSING", f, 0, "磁盘上的 .cs 未被 csproj <Compile> 包含（不会被编译）")
    for f in sorted(proj_cs - disk_cs):
        issue("BLOCKER", "CSPROJ-PHANTOM", f, 0, "csproj <Compile> 指向不存在的文件（MSBuild 直接失败）")
    for f in sorted(disk_xaml - proj_xaml):
        issue("MAJOR", "CSPROJ-XAML-MISSING", f, 0, "磁盘上的 .xaml 未被 csproj <Page>/<ApplicationDefinition> 包含")
    for f in sorted(proj_xaml - disk_xaml):
        issue("BLOCKER", "CSPROJ-XAML-PHANTOM", f, 0, "csproj 声明的 XAML 不存在（MSBuild 直接失败）")

    # Resource/Content 引用的资产必须存在（如 Assets\app.ico）
    for inc in resources:
        for part in inc.split(";"):
            part = part.strip()
            if not part or part.startswith("**") or "*." in os.path.basename(part):
                continue
            asset = os.path.join(PROJ, part.replace("\\", os.sep))
            if not os.path.isfile(asset):
                issue("BLOCKER", "CSPROJ-ASSET", rel(CSPROJ), 0,
                      "<Resource/Content> 引用的资产不存在: %s（运行时 pack:// 解析会抛 IOException）" % part)

    if icon:
        icon_path = os.path.join(PROJ, icon.replace("\\", os.sep))
        if not os.path.isfile(icon_path):
            issue("BLOCKER", "CSPROJ-ICON", rel(CSPROJ), 0, "<ApplicationIcon> 指向的文件不存在: %s" % icon)

    # ApplicationDefinition 必须是 App.xaml
    if [a for a in appdef if norm(a) == "app.xaml"] == []:
        issue("MAJOR", "CSPROJ-APPDEF", rel(CSPROJ), 0, "缺少 App.xaml 的 <ApplicationDefinition>")
    return compiles, pages, appdef


# ════════════════════ A. XAML 结构 ════════════════════

EVENT_ATTRS = (
    "Click|Checked|Unchecked|TextChanged|SelectionChanged|ScrollChanged|SizeChanged|"
    "MouseLeftButtonUp|MouseLeftButtonDown|MouseMove|KeyDown|Loaded|Closing|Closed|"
    "Drop|DragDelta|StateChanged|MouseDoubleClick|PreviewKeyDown|LostFocus|GotFocus"
)


def expected_class_for(xaml_path):
    rel_path = os.path.relpath(xaml_path, PROJ).replace("\\", "/")
    stem = os.path.splitext(os.path.basename(rel_path))[0]
    parent = os.path.dirname(rel_path)
    ns = ROOT_NS + (("." + parent.replace("/", ".")) if parent else "")
    return ns, stem


def check_xaml_structure():
    parsed = {}
    for path in XAML_FILES:
        r = rel(path)
        try:
            tree = ET.parse(path)
            parsed[path] = tree.getroot()
        except ET.ParseError as exc:
            issue("BLOCKER", "XAML-XML", r, 0, "XAML 无法被 XML 解析器解析: %s" % exc)
            continue

        root = parsed[path]
        xclass = root.get(XAML_NS + "Class")
        ns, cls = expected_class_for(path)

        # x:Class 与文件路径/命名空间一致性
        if xclass:
            if xclass != "%s.%s" % (ns, cls):
                issue("BLOCKER", "XAML-XCLASS", r, 0,
                      "x:Class=%s 与文件路径推导的 %s.%s 不一致" % (xclass, ns, cls))
            # partial class 配套
            code_file = os.path.splitext(path)[0] + ".xaml.cs"
            if not os.path.isfile(code_file):
                issue("BLOCKER", "XAML-CODEBEHIND", r, 0, "缺配套的 .xaml.cs（InitializeComponent 无法生成挂接）")
            else:
                code = read_text(code_file)
                if not re.search(r"\bpartial\s+class\s+%s\b" % re.escape(cls), code):
                    issue("BLOCKER", "XAML-PARTIAL", r, 0,
                          "%s 缺少 partial class %s（无法生成 InitializeComponent）" % (rel(code_file), cls))
                if ("namespace %s" % ns) not in code:
                    issue("BLOCKER", "XAML-NAMESPACE", r, 0,
                          "%s 的命名空间不是 %s（与 x:Class 不符）" % (rel(code_file), ns))
        else:
            kind = root.tag.split("}")[-1]
            if kind not in ("ResourceDictionary", "Application"):
                issue("MAJOR", "XAML-NOCLASS", r, 0, "根节点 %s 没有 x:Class（若是控件则 InitializeComponent 无法生成）" % kind)

        # x:Name 同文件不重复（ControlTemplate 内部的 x:Name 属于独立 namescope，
        # 不会互相冲突 —— 全部在模板内时降级为 MINOR）
        names = {}
        name_scopes = {}

        def walk(elem, in_template):
            n = elem.get(XAML_NS + "Name")
            if n:
                names.setdefault(n, 0)
                names[n] += 1
                name_scopes.setdefault(n, []).append(in_template)
            for child in elem:
                child_in_template = in_template or child.tag.split("}")[-1] == "ControlTemplate"
                walk(child, child_in_template)

        walk(root, False)
        for n, count in names.items():
            if count > 1:
                all_in_templates = all(name_scopes[n])
                sev = "MINOR" if all_in_templates else "MAJOR"
                extra = "（均在 ControlTemplate 内，独立 namescope 不冲突）" if all_in_templates else ""
                issue(sev, "XAML-DUPNAME", r, 0,
                      "x:Name=\"%s\" 在同文件出现 %d 次%s" % (n, count, extra))
    return parsed


# ════════════════════ B. 事件处理器存在性 ════════════════════

def check_event_handlers(parsed):
    for path, root in parsed.items():
        code_file = os.path.splitext(path)[0] + ".xaml.cs"
        code = read_text(code_file) if os.path.isfile(code_file) else ""
        for elem in root.iter():
            for attr_name, attr_value in elem.attrib.items():
                short = attr_name.split("}")[-1]
                if short not in EVENT_ATTRS.split("|"):
                    continue
                if attr_name.startswith("{"):
                    continue
                handler = attr_value.split(".")[-1].strip()
                if not handler:
                    continue
                if not code:
                    issue("BLOCKER", "EVENT-NOCODE", rel(path), 0,
                          "XAML 引用事件 %s=\"%s\" 但没有 .xaml.cs" % (short, handler))
                    continue
                if not re.search(r"\b(?:void|private|public|internal|protected)[\w\s<>,\[\]]*\s+%s\s*\(" % re.escape(handler), code):
                    issue("BLOCKER", "EVENT-MISSING", rel(path), 0,
                          "事件 %s=\"%s\" 在对应 .xaml.cs 中找不到同名方法（XAML 编译失败）" % (short, handler))


# ════════════════════ C. 资源解析 ════════════════════

def collect_resource_keys():
    defined = {}
    for path in XAML_FILES:
        text = read_text(path)
        r = rel(path)
        keys = {}
        for m in re.finditer(r'x:Key="([^"]+)"', text):
            keys.setdefault(m.group(1), 0)
            keys[m.group(1)] += 1
        defined[r] = keys
    theme_keys = set()
    for r, keys in defined.items():
        if r.lower().startswith("theme/") or r.lower().endswith("app.xaml"):
            theme_keys |= set(keys.keys())
    return defined, theme_keys


def check_resources():
    defined, theme_keys = collect_resource_keys()
    used = {}

    refs = re.compile(r"\{(?:Static|Dynamic)Resource\s+([^\}]+?)\}")
    for path in XAML_FILES + [p for p in CS_FILES if "Controls" in p or "Views" in p or "App.xaml.cs" in rel(p)]:
        text = read_text(path)
        r = rel(path)
        same_file_keys = set(defined.get(r, {}).keys())
        for m in refs.finditer(text):
            key = m.group(1).strip()
            if key.startswith("{"):
                continue  # 嵌套标记扩展，跳过
            used.setdefault(key, r)
            if key not in theme_keys and key not in same_file_keys:
                issue("MAJOR", "RES-UNRESOLVED", r, 0,
                      "资源键 \"%s\" 在 Theme/*.xaml 与本文件中都找不到定义" % key)

    code_refs = re.compile(r"(?:TryFindResource|FindResource)\(\s*\"([^\"]+)\"\s*\)")
    # ThemeLookup.GetBrush/GetDouble/GetColor/GetFontFamily/GetCornerRadius(owner, "Key", ...)
    lookup_refs = re.compile(r"\"((?:Brush|Color|FontSize|FontFamily|Corner|Space|Text)\.[\w.]+)\"")
    for path in CS_FILES:
        text = read_text(path)
        r = rel(path)
        keys = [m.group(1) for m in code_refs.finditer(text)]
        keys += [m.group(1) for m in lookup_refs.finditer(text)]
        for key in keys:
            used.setdefault(key, r)
            if key not in theme_keys:
                issue("MAJOR", "RES-UNRESOLVED-CODE", r, 0, "代码里引用资源键 \"%s\" 在 Theme/*.xaml 中无定义" % key)

    # 反向：定义了但从未使用（MINOR）
    all_keys = set()
    for keys in defined.values():
        all_keys |= set(keys.keys())
    for key in sorted(all_keys - set(used.keys())):
        issue("MINOR", "RES-UNUSED", "Theme", 0, "资源键 \"%s\" 定义后未被任何文件引用（死令牌）" % key)
    return theme_keys


# ════════════════════ I. x:Static 引用 ════════════════════

def namespace_to_file_map():
    mapping = {}
    for path in CS_FILES:
        r = rel(path).replace("\\", "/")
        text = read_text(path)
        m = re.search(r"namespace\s+([\w.]+)", text)
        if m:
            mapping.setdefault(m.group(1), []).append(r)
    return mapping


def check_xstatic(parsed, ns_files):
    for path, root in parsed.items():
        text = read_text(path)
        for m in re.finditer(r"\{x:Static\s+(\w+):([\w.]+)\.(\w+)\s*\}", text):
            prefix, typename, member = m.group(1), m.group(2), m.group(3)
            ns_match = re.search(r"xmlns:%s=\"clr-namespace:([\w.]+)" % re.escape(prefix), text)
            if not ns_match:
                issue("MAJOR", "XSTATIC-NS", rel(path), 0, "x:Static 前缀 %s: 没有对应的 clr-namespace" % prefix)
                continue
            ns = ns_match.group(1)
            files = ns_files.get(ns, [])
            found = False
            for f in files:
                full = os.path.join(PROJ, f.replace("/", os.sep))
                src = read_text(full)
                if re.search(r"\b%s\b\s*(=|\{|\()" % re.escape(member), src) and re.search(r"\b(const|static)\b", src):
                    found = True
                    break
            if not found:
                issue("MAJOR", "XSTATIC-MISSING", rel(path), 0,
                      "x:Static %s:%s.%s —— 在 %s 中找不到成员 %s（或不是 const/static）"
                      % (prefix, typename, member, files or ["<该命名空间无文件>"], member))


# ════════════════════ D. 绑定路径粗查 ════════════════════

def collect_vm_properties():
    props = set()
    for path in CS_FILES:
        r = rel(path).replace("\\", "/")
        if not (r.startswith("ViewModels/") or r.startswith("Controls/") or r.startswith("Views/")):
            continue
        text = read_text(path)
        for m in re.finditer(r"\bpublic\s+[\w<>\[\]?,.\s]+?\s+(\w+)\s*\{\s*get", text):
            props.add(m.group(1))
        for m in re.finditer(r"public\s+static\s+readonly\s+DependencyProperty\s+(\w+)Property", text):
            props.add(m.group(1))
    return props


def check_bindings(props):
    binding_re = re.compile(r"\{Binding\s+(?:Path=)?([A-Za-z_]\w*)")
    for path in XAML_FILES:
        text = read_text(path)
        for m in binding_re.finditer(text):
            name = m.group(1)
            if name not in props:
                issue("MINOR", "BINDING-PROP", rel(path), 0,
                      "{Binding %s} 未在 ViewModels/Controls 中找到同名属性（粗查，允许误报）" % name)
    # 代码里 new Binding("Xxx")
    for path in CS_FILES:
        text = read_text(path)
        for m in re.finditer(r'new\s+Binding\(\s*"([A-Za-z_]\w*)"\s*\)', text):
            name = m.group(1)
            if name not in props:
                issue("MINOR", "BINDING-PROP", rel(path), 0,
                      "new Binding(\"%s\") 未在 ViewModel 属性中找到（粗查）" % name)


# ════════════════════ F. C# 语法风险 ════════════════════

def strip_code(text):
    """状态机：移除注释 / 字符串 / 字符字面量，保留结构（换行保留以便报行号）。"""
    out = []
    i, n = 0, len(text)
    state = "code"
    line = 1
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if ch == "\n":
            line += 1
            out.append("\n")
            i += 1
            if state in ("string", "char", "verbatim", "linecomment"):
                state = "code"
            continue
        if state == "code":
            if ch == "/" and nxt == "/":
                state = "linecomment"
                i += 2
                continue
            if ch == "/" and nxt == "*":
                state = "blockcomment"
                i += 2
                continue
            if ch == "@" and nxt == '"':
                state = "verbatim"
                out.append(" ")
                i += 2
                continue
            if ch == '"':
                state = "string"
                out.append(" ")
                i += 1
                continue
            if ch == "'":
                state = "char"
                out.append(" ")
                i += 1
                continue
            out.append(ch)
            i += 1
        elif state == "linecomment":
            i += 1  # 遇到 \n 会回到 code
        elif state == "blockcomment":
            if ch == "*" and nxt == "/":
                state = "code"
                i += 2
            else:
                i += 1
        elif state == "string":
            if ch == "\\":
                i += 2
            elif ch == '"':
                state = "code"
                i += 1
            else:
                i += 1
        elif state == "char":
            if ch == "\\":
                i += 2
            elif ch == "'":
                state = "code"
                i += 1
            else:
                i += 1
        elif state == "verbatim":
            if ch == '"' and nxt == '"':
                i += 2
            elif ch == '"':
                state = "code"
                i += 1
            else:
                i += 1
    return "".join(out)


def check_csharp_syntax():
    for path in CS_FILES:
        r = rel(path)
        text = read_text(path)
        code = strip_code(text)

        # 括号配平
        pairs = {")": "(", "]": "[", "}": "{"}
        stack = []
        bad = False
        for idx, ch in enumerate(code):
            if ch in "([{":
                stack.append((ch, idx))
            elif ch in ")]}":
                if not stack or stack[-1][0] != pairs[ch]:
                    line = code.count("\n", 0, idx) + 1
                    issue("BLOCKER", "CS-BRACKETS", r, line,
                          "括号不配平：出现多余的 %r" % ch)
                    bad = True
                    break
                stack.pop()
        if not bad and stack:
            ch, idx = stack[-1]
            line = code.count("\n", 0, idx) + 1
            issue("BLOCKER", "CS-BRACKETS", r, line, "括号不配平：%r 未闭合" % ch)

        # #if 配平
        ifs = len(re.findall(r"^\s*#if\b", text, re.M))
        endifs = len(re.findall(r"^\s*#endif\b", text, re.M))
        if ifs != endifs:
            issue("MAJOR", "CS-IF", r, 0, "#if(%d) 与 #endif(%d) 数量不一致" % (ifs, endifs))

        # <summary> 闭合
        s_open = len(re.findall(r"<summary>", text))
        s_close = len(re.findall(r"</summary>", text))
        if s_open != s_close:
            issue("MAJOR", "CS-SUMMARY", r, 0, "<summary>(%d) 与 </summary>(%d) 数量不一致" % (s_open, s_close))

        # 遗留占位符
        for m in re.finditer(r"\?\?\?", code):
            line = code.count("\n", 0, m.start()) + 1
            issue("MAJOR", "CS-TODO", r, line, "代码中含 ??? 占位符（未完成的实现）")

        # 中文出现在标识符里（仅检查剥离后的代码）
        for m in re.finditer(r"[\w][\u4e00-\u9fff]|[\u4e00-\u9fff][\w]", code):
            line = code.count("\n", 0, m.start()) + 1
            issue("MAJOR", "CS-CJK-IDENT", r, line, "中文字符出现在标识符中: %r" % m.group(0))

        # async void 非事件处理器
        for m in re.finditer(r"\basync\s+void\s+(\w+)\s*\(", code):
            name = m.group(1)
            if not re.match(r"(On[A-Z]|.*_(Click|Tick|Changed|Requested|Closed))", name):
                line = code.count("\n", 0, m.start()) + 1
                issue("MINOR", "CS-ASYNCVOID", r, line,
                      "async void 出现在疑似非事件处理器的方法 %s（异常会崩进程）" % name)

        # await 出现在无 async 的方法（粗查：同方法签名行内无 async）
        for m in re.finditer(r"\bawait\s+\w", code):
            head = code[:m.start()]
            sig = re.findall(r"(?:public|private|protected|internal)[^\n{;]*\b(?:async\s+)?[\w<>\[\],\s]+\s+(\w+)\s*\([^\n{;]*\)", head)
            last_sig = None
            for m2 in re.finditer(r"(?:public|private|protected|internal)[^\n{;]*\b(?:async\s+)?[\w<>\[\],\s]+\s+(\w+)\s*\([^\n{;]*\)", head):
                last_sig = m2
            if last_sig and "async" not in last_sig.group(0):
                line = code.count("\n", 0, m.start()) + 1
                issue("MAJOR", "CS-AWAIT-NONASYNC", r, line,
                      "await 出现在疑似无 async 的方法 %s 中（编译错误）" % last_sig.group(1))

        # using 命名空间白名单（本项目自身命名空间豁免）
        for m in re.finditer(r"^\s*using\s+([\w.]+)\s*;", text, re.M):
            ns = m.group(1)
            if ns.startswith(ROOT_NS + ".") or ns == ROOT_NS:
                continue
            if ns not in ALLOWED_USINGS and not any(ns.startswith(a + ".") for a in ALLOWED_USINGS):
                line = text[:m.start()].count("\n") + 1
                issue("MAJOR", "CS-USING", r, line, "using %s 不在 .NET Framework 4.8 白名单（可能需要 NuGet/不存在）" % ns)

        # 分层纪律
        layer = r.split("/")[0].split("\\")[0] if ("/" in r or "\\" in r) else ""
        for m in re.finditer(r"^\s*using\s+(System\.Windows[\w.]*)\s*;", text, re.M):
            line = text[:m.start()].count("\n") + 1
            if layer in ("Core", "Models", "Services"):
                issue("MAJOR", "LAYER-WPF", r, line,
                      "%s 层出现 using %s（该层不得引用 WPF）" % (layer, m.group(1)))
            elif layer == "ViewModels" and m.group(1) not in ("System.Windows.Input",):
                issue("MAJOR", "LAYER-WPF", r, line,
                      "ViewModels 层出现 using %s（仅允许 System.Windows.Input）" % m.group(1))


ALLOWED_USINGS = {
    "System", "System.Collections", "System.Collections.Generic", "System.Collections.ObjectModel",
    "System.ComponentModel", "System.Configuration", "System.Diagnostics", "System.Drawing",
    "System.Globalization", "System.IO", "System.Linq", "System.Linq.Expressions",
    "System.Net", "System.Net.Cache", "System.Net.Sockets", "System.Reflection",
    "System.Resources", "System.Runtime", "System.Runtime.CompilerServices",
    "System.Runtime.InteropServices", "System.Runtime.Serialization", "System.Security",
    "System.Text", "System.Text.RegularExpressions", "System.Threading", "System.Threading.Tasks",
    "System.Windows", "System.Windows.Controls", "System.Windows.Controls.Primitives",
    "System.Windows.Data", "System.Windows.Documents", "System.Windows.Input",
    "System.Windows.Interop", "System.Windows.Markup", "System.Windows.Media",
    "System.Windows.Media.Animation", "System.Windows.Media.Effects", "System.Windows.Shapes",
    "System.Windows.Threading", "System.Xml", "System.Xml.Linq", "System.Xml.Serialization",
    "Microsoft.Win32", "Microsoft.VisualBasic", "System.Net.NetworkInformation",
    "System.Runtime.ExceptionServices", "System.Collections.Specialized", "System.Collections.Concurrent",
}


# ════════════════════ G. 4.8 API 禁用清单 ════════════════════

BANNED = [
    (r"WaitForExitAsync", "BLOCKER", "WaitForExitAsync（.NET Core 才有）"),
    (r"\.Kill\s*\(\s*(true|entireProcessTree)", "BLOCKER", "Kill(entireProcessTree:true)（.NET Core 才有）"),
    (r"\.ArgumentList\b", "BLOCKER", "ProcessStartInfo.ArgumentList（.NET Core 才有）"),
    (r"System\.Text\.Json", "BLOCKER", "System.Text.Json（4.8 需要 NuGet）"),
    (r"\bSpan<", "BLOCKER", "Span<T>（需要 System.Memory NuGet）"),
    (r"\bReadOnlySpan<", "BLOCKER", "ReadOnlySpan<T>"),
    (r"\bValueTask\b", "BLOCKER", "ValueTask（需要 NuGet）"),
    (r"\bIAsyncEnumerable\b", "BLOCKER", "IAsyncEnumerable（.NET Core 才有）"),
    (r"\?\?=", "BLOCKER", "??= 运算符（C# 8）"),
    (r"\brecord\s+\w+", "BLOCKER", "record 类型（C# 9）"),
    (r"\busing\s+var\b", "BLOCKER", "using var 声明（C# 8）"),
    (r"\bswitch\s*\{", "BLOCKER", "switch 表达式（C# 8）"),
    (r"\bnew\s*\(\s*\)", "BLOCKER", "目标类型 new()（C# 9；where 约束除外，已人工复核）"),
    (r"\{\s*get;\s*init;", "BLOCKER", "init 访问器（C# 9）"),
    (r"\[\s*\^\s*\d", "MAJOR", "index-from-end ^（C# 8 Index/Range）"),
    (r"\[\s*\w+\s*\.\.\s*\w*\s*\]", "MAJOR", "范围运算符 [x..y]（C# 8）"),
    (r"\bHttpClient\b", "MINOR", "HttpClient（本项目声明不需要，出现即多余依赖）"),
]


def check_banned_apis():
    for path in CS_FILES:
        r = rel(path)
        code = strip_code(read_text(path))
        # 排除 where 约束里的 new()
        code_no_constraint = re.sub(r"\bwhere\s+[\w<>,\s]+:\s*new\(\)", " ", code)
        for pattern, severity, label in BANNED:
            for m in re.finditer(pattern, code_no_constraint):
                line = code.count("\n", 0, m.start()) + 1
                issue(severity, "BANNED-API", r, line, "命中 4.8/C#7.3 禁用项：%s" % label)


# ════════════════════ J. 文案一致性 ════════════════════

TEXT_CHECKS = [
    ("该分组下暂无可重装的软件", "qt_app.py", 1503),
    ("可尝试搜索软件名或包 ID，多个词用空格分隔", "qt_app.py", 1501),
    ("正在扫描已安装软件…", "qt_app.py", 329),
    ("搜索名称或包 ID（空格分隔多个词）", "qt_app.py", 361),
    ("扫描进行中，请点「取消扫描」中断", "qt_app.py", 288),
    ("已取消扫描，后台 winget 进程会在数秒内退出，本次结果不再采用。", "qt_app.py", 1058),
    ("已恢复上次的分组筛选「", "qt_app.py", 1098),
    ("（没有这类软件，全部都能自动重装）", "services/profiles.py", 165),
    ("chcp 936 >nul", "services/profiles.py", 103),
    ("title 恢复软件清单", "services/profiles.py", 104),
    ("cd /d \"%~dp0\"", "services/profiles.py", 105),
    ("echo   Winget 软件管家 · 恢复脚本", "services/profiles.py", 126),
    ("echo   源里已下架的包会被自动跳过，不影响其余安装。", "services/profiles.py", 132),
    ("echo [x] 未找到 winget，请先安装「应用安装程序 / App Installer」。", "services/profiles.py", 110),
    # 「无法自动重装清单」已由 txt 改成**单文件 HTML 待办页**（有意改版），
    # 不再逐字对齐 profiles.py:161 的旧那两行；这里改成校验 HTML 抬头里的核心承诺。
    ("恢复脚本不会处理它们", "ExportProfiles.cs（改版后）", 0),
    ("required to run", "services/metadata.py", 27),
    ("个来自微软商店 msstore", "services/profiles.py", 99),
    # v3 起导出按钮移到底部居中浮动条，「右下角」的说法已不成立；
    # 现在这句只说「做了什么之后能导出」，位置无关，不会再过期。
    (" · 勾选后可导出重装配置", "qt_app.py", 1599),
]


def check_texts():
    cs_all = "\n".join(read_text(p) for p in CS_FILES)
    xaml_all = "\n".join(read_text(p) for p in XAML_FILES)
    corpus = cs_all + xaml_all
    for text, ref_file, ref_line in TEXT_CHECKS:
        # C# 里转义的引号按原样找不到：对 cd /d "%~dp0" 特判两种形态
        variants = [text]
        if "\\\"" in text or '"%~dp0"' in text:
            variants.append(text.replace('\\"', '"'))
            variants.append(text.replace('"', '\\"'))
        ok = any(v in corpus for v in variants)
        if not ok:
            issue("MAJOR", "TEXT-MISMATCH", "文案", 0,
                  "文案「%s」未在 C#/XAML 中逐字出现（原版 %s:%d）" % (text, ref_file, ref_line))


# ════════════════════ 重点怀疑区 ════════════════════

def check_risk_areas(parsed):
    # DropShadowEffect / GradientStop 的 Color 必须是 Color 资源
    color_keys = set()
    for path in XAML_FILES:
        text = read_text(path)
        for m in re.finditer(r'<Color\s+x:Key="([^"]+)"', text):
            color_keys.add(m.group(1))
    for path in XAML_FILES:
        text = read_text(path)
        r = rel(path)
        for m in re.finditer(r"<(DropShadowEffect|GradientStop)\b[^>]*?Color=\"\{StaticResource\s+([^}]+?)\}\">?", text):
            key = m.group(2).strip()
            if key not in color_keys:
                issue("MAJOR", "XAML-COLOR-BRUSH", r, 0,
                      "<%s Color=\"{StaticResource %s}\"> —— 该键不是 <Color> 资源（Brush 给 Color 属性会抛异常）"
                      % (m.group(1), key))

    # SizeToContent 与 WindowStyle=None 组合
    for path, root in parsed.items():
        r = rel(path)
        tag = root.tag.split("}")[-1]
        if tag == "Window":
            style_none = root.get("WindowStyle") == "None"
            stc = root.get("SizeToContent", "Manual")
            if style_none and stc not in ("Manual", "WidthAndHeight") and stc != "Manual":
                pass
            if style_none and stc != "Manual":
                issue("MAJOR", "XAML-SIZETOCONTENT", r, 0,
                      "WindowStyle=None 与 SizeToContent=%s 组合（尺寸抖动/布局循环的经典坑）" % stc)

    # DispatcherTimer 是否都有 Stop（泄漏怀疑区）
    for path in CS_FILES:
        r = rel(path)
        code = read_text(path)
        starts = len(re.findall(r"new\s+DispatcherTimer\(", code))
        stops = len(re.findall(r"\.Stop\(\)", code))
        if starts > 0 and stops == 0:
            issue("MINOR", "TIMER-NO-STOP", r, 0,
                  "创建了 %d 个 DispatcherTimer 但本文件没有任何 .Stop()（需人工确认是否泄漏）" % starts)

    # RichTextBox 的 UI 更新是否有 Dispatcher 保护（只看剥离注释后的代码）
    for path in CS_FILES:
        code = strip_code(read_text(path))
        r = rel(path)
        if "RichTextBox" in code and "CheckAccess" not in code:
            issue("MINOR", "RTB-THREAD", r, 0, "RichTextBox 出现但无 Dispatcher.CheckAccess 保护（后台线程追加会崩）")


# ════════════════════ 主流程 ════════════════════

def main():
    if not os.path.isdir(PROJ):
        print("找不到被测工程: %s" % PROJ)
        sys.exit(2)

    compiles, pages, appdef = check_csproj()
    parsed = check_xaml_structure()
    check_event_handlers(parsed)
    check_resources()
    ns_files = namespace_to_file_map()
    check_xstatic(parsed, ns_files)
    props = collect_vm_properties()
    check_bindings(props)
    check_csharp_syntax()
    check_banned_apis()
    check_texts()
    check_risk_areas(parsed)

    # ── 统计与输出 ──
    order = {"BLOCKER": 0, "MAJOR": 1, "MINOR": 2, "INFO": 3}
    ISSUES.sort(key=lambda x: (order.get(x["severity"], 9), x["file"], x["code"]))
    counts = {}
    for i in ISSUES:
        counts[i["severity"]] = counts.get(i["severity"], 0) + 1

    print("=" * 72)
    print("QA 静态验证（qa_verify.py）  被测工程: %s" % rel(PROJ))
    print("文件: .xaml=%d  .cs=%d" % (len(XAML_FILES), len(CS_FILES)))
    print("问题: BLOCKER=%d  MAJOR=%d  MINOR=%d  其他=%d"
          % (counts.get("BLOCKER", 0), counts.get("MAJOR", 0), counts.get("MINOR", 0),
             len(ISSUES) - sum(counts.values())))
    print("=" * 72)
    for i in ISSUES:
        loc = "%s:%s" % (i["file"], i["line"]) if i["line"] else i["file"]
        print("[%s] %s %s — %s" % (i["severity"], loc, i["code"], i["message"]))
    if not ISSUES:
        print("（未发现问题）")

    # 原始数据落盘
    lines = ["# QA-REPORT 原始数据（由 tools/qa_verify.py 生成，请勿手工编辑）", ""]
    lines.append("- 被测工程: %s" % rel(PROJ))
    lines.append("- 文件数: .xaml=%d, .cs=%d" % (len(XAML_FILES), len(CS_FILES)))
    lines.append("- 问题统计: BLOCKER=%d, MAJOR=%d, MINOR=%d" % (
        counts.get("BLOCKER", 0), counts.get("MAJOR", 0), counts.get("MINOR", 0)))
    lines.append("")
    lines.append("| severity | code | file:line | message |")
    lines.append("| --- | --- | --- | --- |")
    for i in ISSUES:
        msg = i["message"].replace("|", "\\|")
        lines.append("| %s | %s | %s:%s | %s |" % (i["severity"], i["code"], i["file"], i["line"], msg))
    lines.append("")
    lines.append("## 文案一致性抽查（%d 条，PASS 的不列）" % len(TEXT_CHECKS))
    lines.append("")
    ok_texts = []
    for text, ref_file, ref_line in TEXT_CHECKS:
        found = not any(i["code"] == "TEXT-MISMATCH" and text in i["message"] for i in ISSUES)
        (ok_texts if found else ok_texts).append((text, ref_file, ref_line, found))
    for text, ref_file, ref_line, found in ok_texts:
        lines.append("- [%s] %r  ← 原版 %s:%d" % ("PASS" if found else "FAIL", text, ref_file, ref_line))
    with io.open(OUT_RAW, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print()
    print("原始数据已写入: %s" % OUT_RAW)
    sys.exit(0)


if __name__ == "__main__":
    main()

"""性能体检：真实 98 包数据下测重建/切换/滚动的实际耗时。"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import qt_app as Q
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication, QWidget

app = QApplication(sys.argv)
app.setOrganizationName(Q.SETTINGS_ORG)
app.setApplicationName(Q.SETTINGS_APP)
Q.settings().setValue("view/sort", "group")

win = Q.MainWindow(Q.load_config())
win.resize(1240, 880)
win.show()
out = {}


def bench():
    if win.state in ("BOOT", "SCANNING"):
        QTimer.singleShot(500, bench)
        return
    print(f"包数: {len(win.packages)} · 分组: {len(win.sections)} · 可见行: {len(win.rows)}")

    def t(fn, label, n=1):
        app.processEvents()
        t0 = time.perf_counter()
        for _ in range(n):
            fn()
            app.processEvents()
        dt = (time.perf_counter() - t0) / n
        print(f"{label}: {dt * 1000:.1f} ms")
        return dt

    t(win._rebuild_list, "全量重建列表（切分组/搜索时）")
    t(lambda: win._rebuild_chips(), "重建分组胶囊")

    win.search.setText("py")
    win._rebuild_list()
    t(win._rebuild_list, "带搜索词的重建")
    win.search.clear()
    win._rebuild_list()

    # 滚动
    sb = win.scroll.verticalScrollBar()
    sb.setValue(0)
    app.processEvents()
    t0 = time.perf_counter()
    for i in range(0, sb.maximum(), 40):
        sb.setValue(i)
        app.processEvents()
    print(f"连续滚动 {sb.maximum() // 40} 帧: {(time.perf_counter() - t0) * 1000:.0f} ms "
          f"(每帧 {(time.perf_counter() - t0) * 1000 / max(1, sb.maximum() // 40):.1f} ms)")

    # 滚动到底再展开「winget 不支持」组
    sb.setValue(sb.maximum())
    app.processEvents()
    other = win.sections[-1]
    t(other.toggle, "展开「winget 不支持」组", 1)

    all_widgets = len(win.findChildren(QWidget))
    print(f"控件总数: {all_widgets}")
    print(f"日志: {Q.LOG_FILE} 存在={Q.LOG_FILE.is_file()}")
    app.quit()


QTimer.singleShot(1200, bench)
sys.exit(app.exec())

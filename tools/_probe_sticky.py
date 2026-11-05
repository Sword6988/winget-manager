"""真实窗口验证：吸顶分组头 + 关于对话框。"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import qt_app as Q
from PySide6.QtCore import QTimer
from PySide6.QtWidgets import QApplication

app = QApplication(sys.argv)
app.setOrganizationName(Q.SETTINGS_ORG)
app.setApplicationName(Q.SETTINGS_APP)
Q.settings().setValue("view/sort", "group")

win = Q.MainWindow(Q.load_config())
win.resize(1240, 880)
win.show()
out = {"step": 0}


def scroll_to(idx: int) -> None:
    secs = win.sections
    if idx >= len(secs):
        return
    from PySide6.QtCore import QPoint
    y = secs[idx].mapTo(win.rows_host, QPoint(0, 0)).y()
    win.scroll.verticalScrollBar().setValue(max(0, y + 40))
    app.processEvents()
    print(f"scroll→{y + 40} sticky={win.sticky.isVisible()} "
          f"title={win.sticky._title!r} y={win.sticky.y()} "
          f"h={win.sticky.height()} w={win.sticky.width()}")


def step():
    if win.state in ("BOOT", "SCANNING"):
        QTimer.singleShot(600, step)
        return
    s = out["step"]
    if s == 0:
        print("sections:", [x.title for x in win.sections])
        scroll_to(2)
        win.grab().save("docs/sticky_light.png")
        out["step"] = 1
        QTimer.singleShot(400, step)
    elif s == 1:
        scroll_to(4)
        win.grab().save("docs/sticky_light2.png")
        out["step"] = 2
        QTimer.singleShot(400, step)
    else:
        dlg = Q.AboutDialog(win)
        dlg.show()
        app.processEvents()
        dlg.grab().save("docs/about.png")
        print("about:", dlg.width(), "x", dlg.height())
        app.quit()


QTimer.singleShot(1500, step)
sys.exit(app.exec())

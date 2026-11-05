"""真实窗口验证：默认外观、搜索高亮、排序平铺。"""
import sys

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


def step():
    if win.state in ("BOOT", "SCANNING"):
        QTimer.singleShot(600, step)
        return
    s = out["step"]
    if s == 0:
        win.grab().save("docs/final_default.png")
        print("default:", len(win.packages))
        win.search.setText("python")
        win._rebuild_list()
        QTimer.singleShot(500, lambda: out.update(step=1) or step())
    elif s == 1:
        win.grab().save("docs/final_search.png")
        print("search done")
        win.search.clear()
        win._rebuild_list()
        win._set_sort("name")
        QTimer.singleShot(500, lambda: out.update(step=2) or step())
    elif s == 2:
        win.grab().save("docs/final_flat.png")
        print("flat done")
        win._set_sort("group")
        app.quit()


QTimer.singleShot(1500, step)
sys.exit(app.exec())

# legacy · 历史实现归档

这里存放本项目早期/并行的实现，**不再维护，也不参与打包**。保留只为回溯参考。

| 文件 | 说明 |
|---|---|
| `gui_app.py` | Tkinter 桌面版 v2.0（最早的独立 exe，已被 PySide6 版取代） |
| `app.py` + `server.py` + `static/` | 网页版后端与前端（浏览器访问的单文件服务） |
| `web_exe.py` | 网页版打包入口（pywebview 套壳） |
| `dist/WingetSoftwareManager.exe` | Tk 版旧 exe |
| `dist/WingetSoftwareManagerWeb.exe` | 网页版旧 exe |
| `build_exe.bat` | Tk 版构建脚本 |
| `WingetSoftwareManager.spec` / `WingetSoftwareManagerWeb.spec` | 旧版打包配置 |

## 当前主线

根目录只有一条线：

- `qt_app.py` — PySide6 桌面版（唯一在维护的界面）
- `WingetSoftwareManagerQt.spec` + `version_info.txt` — 打包配置
- `core/` `services/` — 三套实现共用的业务层

打包：`pyinstaller WingetSoftwareManagerQt.spec -y`，产物 `dist/WingetSoftwareManagerQt.exe`。

> 注意：`core/` 与 `services/` 仍被 legacy 里的旧代码 import。归档只是挪位置，
> 业务层不要跟着挪，否则旧 exe 的源码引用会断。

# -*- mode: python ; coding: utf-8 -*-
# Winget 软件管家 · Qt 桌面版打包（单文件，无控制台）

a = Analysis(
    ['qt_app.py'],
    pathex=[],
    binaries=[],
    datas=[('assets/app.ico', 'assets')],
    hiddenimports=[],
    excludes=[
        'tkinter',
        'PySide6.QtQml', 'PySide6.QtQuick', 'PySide6.QtQuickWidgets',
        'PySide6.QtQuickControls2', 'PySide6.QtQuick3D',
        'PySide6.QtPdf', 'PySide6.QtPdfWidgets',
        'PySide6.QtMultimedia', 'PySide6.QtMultimediaWidgets',
        'PySide6.QtSql', 'PySide6.QtTest', 'PySide6.QtUiTools',
        'PySide6.QtDesigner', 'PySide6.QtHelp',
        'PySide6.QtSerialPort', 'PySide6.QtPositioning', 'PySide6.QtSensors',
        'PySide6.QtWebSockets', 'PySide6.QtWebChannel',
    ],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='WingetManager',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    icon='assets/app.ico',
    version='version_info.txt',
)

"""截取指定标题窗口为 BMP（仅用于 UI 验证，ctypes + GDI，无第三方依赖）。"""
import ctypes, struct, sys, time

u32 = ctypes.windll.user32
g32 = ctypes.windll.gdi32


def capture(title: str, out: str) -> str:
    hwnd = u32.FindWindowW(None, title)
    if not hwnd:
        return "window not found"
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.6)
    rect = ctypes.wintypes.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    hdc_win = u32.GetWindowDC(hwnd)
    hdc_mem = g32.CreateCompatibleDC(hdc_win)
    hbmp = g32.CreateCompatibleBitmap(hdc_win, w, h)
    g32.SelectObject(hdc_mem, hbmp)
    u32.PrintWindow(hwnd, hdc_mem, 2)  # PW_RENDERFULLCONTENT
    bmi = ctypes.create_string_buffer(40)
    struct.pack_into("<IiiHHIIiiII", bmi, 0, 40, w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * h * 4)
    g32.GetDIBits(hdc_mem, hbmp, 0, h, buf, bmi, 0)
    row = w * 4
    data = bytearray(buf.raw)
    # BGRA -> BMP(BGRX, bottom-up already handled by negative height)
    hdr = (struct.pack("<2sIHHI", b"BM", 14 + 40 + len(data), 0, 0, 14 + 40)
           + struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 32, 0, len(data), 0, 0, 0, 0))
    with open(out, "wb") as f:
        f.write(hdr)
        f.write(data)
    g32.DeleteObject(hbmp); g32.DeleteDC(hdc_mem); u32.ReleaseDC(hwnd, hdc_win)
    return f"captured {w}x{h}"


if __name__ == "__main__":
    import ctypes.wintypes  # noqa: F401
    print(capture(sys.argv[1], sys.argv[2]))

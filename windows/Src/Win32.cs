using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace QScreen
{
    public static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint nFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] public struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
        public const int WCA_ACCENT_POLICY = 19;
        public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        /// <summary>Акриловый блюр «за окном» (Win10 1803+ / Win11). tintAbgr — цвет подложки 0xAABBGGRR.</summary>
        public static void EnableAcrylicBlur(IntPtr hwnd, uint tintAbgr)
        {
            var accent = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND, AccentFlags = 2, GradientColor = unchecked((int)tintAbgr) };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint PW_RENDERFULLCONTENT = 0x00000002;
        public const uint SRCCOPY = 0x00CC0020;
        public const uint CAPTUREBLT = 0x40000000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_FRAMECHANGED = 0x0020;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020, WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000, WS_EX_NOACTIVATE = 0x08000000;

        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_DPICHANGED = 0x02E0;

        // Низкоуровневый хук клавиатуры: только он надёжно видит нажатие PrintScreen
        // (обычному окну Windows шлёт для VK_SNAPSHOT лишь отпускание, а Win11 перехватывает клавишу под «Ножницы»)
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? lpModuleName);
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        public const uint VK_SNAPSHOT = 0x2C;

        // Синтетическое колесо для авто-прокрутки скролл-захвата
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        public const uint INPUT_MOUSE = 0, MOUSEEVENTF_WHEEL = 0x0800;

        /// <summary>Курсор в точку (физ. пиксели) и колесо: notches &lt; 0 — вниз по странице</summary>
        public static void Wheel(System.Drawing.Point at, int notches)
        {
            SetCursorPos(at.X, at.Y);   // колесо получает окно под курсором
            var input = new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches * 120)), dwFlags = MOUSEEVENTF_WHEEL } } };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        public const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>Колесо прямо окну под точкой сообщением WM_MOUSEWHEEL — курсор не двигается, мышью можно пользоваться</summary>
        public static bool WheelTo(System.Drawing.Point at, int notches)
        {
            var h = WindowFromPoint(new POINT { X = at.X, Y = at.Y });
            if (h == IntPtr.Zero) return false;
            int delta = notches * 120;
            var wParam = new IntPtr(unchecked((int)(((uint)delta & 0xFFFF) << 16)));
            var lParam = new IntPtr(unchecked((int)((((uint)at.Y & 0xFFFF) << 16) | ((uint)at.X & 0xFFFF))));   // экранные координаты
            return PostMessage(h, WM_MOUSEWHEEL, wParam, lParam);
        }

        /// <summary>Запасной путь для окон, которые не берут WM_MOUSEWHEEL: курсор на миг в точку, колесо, курсор обратно</summary>
        public static async System.Threading.Tasks.Task WheelViaCursor(System.Drawing.Point at, int notches)
        {
            GetCursorPos(out var old);
            Wheel(at, notches);
            await System.Threading.Tasks.Task.Delay(40);   // колесо уходит окну под курсором в момент обработки — не возвращать раньше
            SetCursorPos(old.X, old.Y);
        }

        /// <summary>Сделать активным окно верхнего уровня под точкой (чтобы ему ушли Ctrl+= / Ctrl+-)</summary>
        public static IntPtr ActivateWindowAt(System.Drawing.Point p)
        {
            var h = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
            if (h == IntPtr.Zero) return IntPtr.Zero;
            var root = GetAncestor(h, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) return IntPtr.Zero;
            SetForegroundWindow(root);
            return root;
        }

        /// <summary>Ctrl+= (масштаб +) или Ctrl+- (масштаб −) активному окну</summary>
        public static void ZoomKey(bool plus)
        {
            ushort vk = plus ? (ushort)0xBB /* VK_OEM_PLUS */ : (ushort)0xBD /* VK_OEM_MINUS */;
            static INPUT K(ushort v, bool up) => new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = v, dwFlags = up ? 2u : 0u } } };
            var seq = new[] { K(0x11, false), K(vk, false), K(vk, true), K(0x11, true) };
            SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
        }

        public static void ApplyDarkMode(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int one = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref one, sizeof(int));
            };
        }

        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // Windows 10 2004+: окна нет ни в BitBlt, ни в WGC, на экране оно видно
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

        /// <summary>Окно, которое никогда не забирает фокус (аналог nonactivatingPanel).</summary>
        public static void MakeNonActivating(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };
        }

        public static System.Drawing.Rectangle MonitorRectFromPoint(int x, int y)
        {
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            return scr.Bounds;
        }

        public static System.Drawing.Rectangle MonitorWorkAreaFromPoint(int x, int y)
        {
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            return scr.WorkingArea;
        }

        public static System.Drawing.Point CursorPos()
        {
            GetCursorPos(out var p);
            return new System.Drawing.Point(p.X, p.Y);
        }

        public static double ScaleForPoint(int x, int y)
        {
            var hmon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(hmon, 0, out uint dx, out _) == 0 && dx > 0) return dx / 96.0;
            return 1.0;
        }

        /// <summary>Ставит окно в физические пиксели экрана (обход DIP-пересчёта WPF при PerMonitorV2).</summary>
        public static void PlaceWindowPhysical(Window w, System.Drawing.Rectangle rect, bool topmost, bool activate = true)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            uint flags = SWP_SHOWWINDOW | (activate ? 0 : SWP_NOACTIVATE);
            SetWindowPos(hwnd, topmost ? HWND_TOPMOST : IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, flags);
        }
    }
}

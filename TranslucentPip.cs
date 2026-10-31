using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("Translucent Pip")]
[assembly: AssemblyProduct("Translucent Pip")]

static class TranslucentPip
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_LAYERED = 0x00080000;
    const uint LWA_ALPHA = 0x2;
    const int VK_CONTROL = 0x11;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr val);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "whale", "brave" };

    class PipWin { public IntPtr Hwnd; public long OrigExStyle; public bool Ghost, ClickThrough; }

    static readonly Dictionary<IntPtr, PipWin> wins = new Dictionary<IntPtr, PipWin>();
    static readonly Dictionary<uint, bool> pidIsBrowser = new Dictionary<uint, bool>();
    static byte alpha = 77;
    static bool enabled = true;
    static int tick;

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "TranslucentPip_SingleInstance", out created))
        {
            if (!created) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            Application.EnableVisualStyles();
            var tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Translucent Pip", Visible = true };
            tray.ContextMenuStrip = BuildMenu(tray);

            var timer = new System.Windows.Forms.Timer { Interval = 30 };
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            Application.ApplicationExit += delegate { RestoreAll(); tray.Visible = false; };
            Application.Run();
        }
    }

    static ContextMenuStrip BuildMenu(NotifyIcon tray)
    {
        var menu = new ContextMenuStrip();
        var toggle = new ToolStripMenuItem("사용") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += delegate { enabled = toggle.Checked; if (!enabled) RestoreAll(); };
        menu.Items.Add(toggle);

        var opacity = new ToolStripMenuItem("투명도");
        foreach (int pct in new[] { 10, 20, 30, 50, 70 })
        {
            int p = pct;
            var item = new ToolStripMenuItem(p + "% 보이기") { Checked = p == 30 };
            item.Click += delegate
            {
                alpha = (byte)(255 * p / 100);
                foreach (ToolStripMenuItem o in opacity.DropDownItems) o.Checked = o == item;
                foreach (var w in wins.Values) if (w.Ghost) SetLayeredWindowAttributes(w.Hwnd, 0, alpha, LWA_ALPHA);
            };
            opacity.DropDownItems.Add(item);
        }
        menu.Items.Add(opacity);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ctrl 누른 채로 PiP 조작").Enabled = false;
        menu.Items.Add("종료", null, delegate { Application.Exit(); });
        return menu;
    }

    static void OnTick()
    {
        if (!enabled) return;
        if (tick++ % 15 == 0) Rescan(); // 창 목록 갱신

        POINT p;
        GetCursorPos(out p);
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        foreach (var w in wins.Values)
        {
            RECT r;
            if (!GetWindowRect(w.Hwnd, out r)) continue;
            bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

            SetClickThrough(w, !ctrl);
            bool wantGhost = inside && !ctrl;
            if (wantGhost != w.Ghost)
            {
                SetLayeredWindowAttributes(w.Hwnd, 0, wantGhost ? alpha : (byte)255, LWA_ALPHA);
                w.Ghost = wantGhost;
            }
        }
    }

    static void SetClickThrough(PipWin w, bool on)
    {
        if (on == w.ClickThrough) return;
        long ex = GetWindowLongPtr(w.Hwnd, GWL_EXSTYLE).ToInt64();
        if (on)
        {
            SetWindowLongPtr(w.Hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
            if ((ex & WS_EX_LAYERED) == 0) SetLayeredWindowAttributes(w.Hwnd, 0, 255, LWA_ALPHA);
        }
        else
        {
            SetWindowLongPtr(w.Hwnd, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_TRANSPARENT));
        }
        w.ClickThrough = on;
    }

    static void Rescan()
    {
        var found = new HashSet<IntPtr>();
        var cls = new StringBuilder(64);
        EnumWindows(delegate (IntPtr h, IntPtr _)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOPMOST) == 0) return true;
            cls.Length = 0;
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;
            RECT r;
            GetWindowRect(h, out r);
            if (r.Right - r.Left < 100 || r.Bottom - r.Top < 60) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (!IsBrowser(pid)) return true;

            found.Add(h);
            if (!wins.ContainsKey(h)) wins[h] = new PipWin { Hwnd = h, OrigExStyle = ex };
            return true;
        }, IntPtr.Zero);

        var gone = new List<IntPtr>();
        foreach (var h in wins.Keys) if (!found.Contains(h)) gone.Add(h);
        foreach (var h in gone)
        {
            if (IsWindow(h)) Restore(wins[h]);
            wins.Remove(h);
        }
    }

    static bool IsBrowser(uint pid)
    {
        bool v;
        if (pidIsBrowser.TryGetValue(pid, out v)) return v;
        try { v = Browsers.Contains(Process.GetProcessById((int)pid).ProcessName); } catch { v = false; }
        pidIsBrowser[pid] = v;
        return v;
    }

    static void Restore(PipWin w)
    {
        SetLayeredWindowAttributes(w.Hwnd, 0, 255, LWA_ALPHA);
        SetWindowLongPtr(w.Hwnd, GWL_EXSTYLE, new IntPtr(w.OrigExStyle));
        w.Ghost = false;
        w.ClickThrough = false;
    }

    static void RestoreAll()
    {
        foreach (var w in wins.Values) if (IsWindow(w.Hwnd)) Restore(w);
        wins.Clear();
    }
}

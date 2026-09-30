using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

class AlwaysOnTop : Form
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLong(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    const uint MOD_ALT = 1, MOD_CONTROL = 2;
    const int HotPin = 1, HotMore = 2, HotLess = 3;

    ListView list;
    Label statusLabel;
    NotifyIcon tray;
    bool updating;
    readonly HashSet<IntPtr> pinnedByUs = new HashSet<IntPtr>();

    AlwaysOnTop()
    {
        Text = "AlwaysOnTop";
        ClientSize = new Size(640, 430);
        MinimumSize = new Size(480, 300);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        var hint = new Label
        {
            Location = new Point(12, 10), Size = new Size(616, 44), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "Ctrl + Alt + T — закрепить или открепить активное окно поверх остальных.\nCtrl + Alt + PgUp / PgDn — сделать активное окно плотнее или прозрачнее"
        };
        Controls.Add(hint);

        list = new ListView
        {
            Location = new Point(12, 60), Size = new Size(616, 318), View = View.Details, CheckBoxes = true, FullRowSelect = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        list.Columns.Add("Окно", 360);
        list.Columns.Add("Программа", 130);
        list.Columns.Add("Прозрачность", 100, HorizontalAlignment.Right);
        list.ItemChecked += (s, e) => { if (!updating) { SetTopMost((IntPtr)e.Item.Tag, e.Item.Checked); Refresh(); } };
        var menu = new ContextMenuStrip();
        foreach (int p in new[] { 100, 90, 80, 70, 50, 30 })
        {
            int percent = p;
            menu.Items.Add("Прозрачность: " + (100 - p) + "%", null, (s, e) => { foreach (ListViewItem i in list.SelectedItems) SetOpacity((IntPtr)i.Tag, percent); Refresh(); });
        }
        list.ContextMenuStrip = menu;
        Controls.Add(list);

        statusLabel = new Label { Location = new Point(12, 392), AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Text = "Галочка — поверх всех окон. Правый клик — прозрачность" };
        Controls.Add(statusLabel);
        var refresh = new Button { Text = "Обновить", Location = new Point(528, 386), Size = new Size(100, 32), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        refresh.Click += (s, e) => Refresh();
        Controls.Add(refresh);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Показать окно", null, (s, e) => ShowWindow());
        trayMenu.Items.Add("Открепить все", null, (s, e) => { foreach (var h in pinnedByUs.ToList()) SetTopMost(h, false); Refresh(); });
        trayMenu.Items.Add("Выход", null, (s, e) => Close());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = trayMenu, Text = "AlwaysOnTop: Ctrl+Alt+T" };
        tray.DoubleClick += (s, e) => ShowWindow();

        Activated += (s, e) => Refresh();
        Resize += (s, e) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        bool ok = RegisterHotKey(Handle, HotPin, MOD_CONTROL | MOD_ALT, (uint)Keys.T);
        RegisterHotKey(Handle, HotMore, MOD_CONTROL | MOD_ALT, (uint)Keys.PageUp);
        RegisterHotKey(Handle, HotLess, MOD_CONTROL | MOD_ALT, (uint)Keys.PageDown);
        if (!ok) BeginInvoke(new Action(() => statusLabel.Text = "Ctrl + Alt + T занято другой программой. Галочки в списке работают"));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            IntPtr h = GetForegroundWindow();
            if (h != IntPtr.Zero && h != Handle)
            {
                int id = (int)m.WParam;
                if (id == HotPin)
                {
                    bool now = !IsTopMost(h);
                    SetTopMost(h, now);
                    tray.ShowBalloonTip(1500, now ? "📌 Закреплено поверх всех" : "Откреплено", Title(h), ToolTipIcon.None);
                }
                else SetOpacity(h, Math.Max(20, Math.Min(100, GetOpacity(h) + (id == HotMore ? 10 : -10))));
                if (Visible) Refresh();
            }
        }
        base.WndProc(ref m);
    }

    static long ExStyle(IntPtr h) { return GetWindowLong(h, GWL_EXSTYLE).ToInt64(); }
    static bool IsTopMost(IntPtr h) { return (ExStyle(h) & WS_EX_TOPMOST) != 0; }

    void SetTopMost(IntPtr h, bool on)
    {
        SetWindowPos(h, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (on) pinnedByUs.Add(h); else pinnedByUs.Remove(h);
    }

    static int GetOpacity(IntPtr h)
    {
        uint key, flags; byte alpha;
        if ((ExStyle(h) & WS_EX_LAYERED) == 0 || !GetLayeredWindowAttributes(h, out key, out alpha, out flags) || (flags & 2) == 0) return 100;
        return (int)Math.Round(alpha * 100 / 255.0);
    }

    static void SetOpacity(IntPtr h, int percent)
    {
        long style = ExStyle(h);
        if (percent >= 100)
        {
            if ((style & WS_EX_LAYERED) != 0) SetLayeredWindowAttributes(h, 0, 255, 2);
            return;
        }
        if ((style & WS_EX_LAYERED) == 0) SetWindowLong(h, GWL_EXSTYLE, new IntPtr(style | WS_EX_LAYERED));
        SetLayeredWindowAttributes(h, 0, (byte)(percent * 255 / 100), 2);
    }

    static string Title(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsRealWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return false;
        if ((ExStyle(h) & WS_EX_TOOLWINDOW) != 0 || GetWindow(h, 4) != IntPtr.Zero) return false; // tool windows and owned popups
        int cloaked;
        return DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0 || cloaked == 0; // hidden UWP / other desktops
    }

    new void Refresh()
    {
        var windows = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsRealWindow(h) && h != Handle) windows.Add(h); return true; }, IntPtr.Zero);
        updating = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var h in windows)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string process = "";
            try { process = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (process == "explorer" && Title(h) == "Program Manager") continue;
            int opacity = GetOpacity(h);
            var item = new ListViewItem(new[] { Title(h), process, opacity < 100 ? (100 - opacity) + "%" : "" }) { Tag = h, Checked = IsTopMost(h) };
            if (item.Checked) item.Font = new Font(list.Font, FontStyle.Bold);
            list.Items.Add(item);
        }
        list.EndUpdate();
        updating = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var h in pinnedByUs.ToList()) SetTopMost(h, false);
        UnregisterHotKey(Handle, HotPin);
        UnregisterHotKey(Handle, HotMore);
        UnregisterHotKey(Handle, HotLess);
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new AlwaysOnTop());
    }
}

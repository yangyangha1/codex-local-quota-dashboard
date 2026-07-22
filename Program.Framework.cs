using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.Win32;

[assembly: AssemblyTitle("Codex Local Quota Dashboard")]
[assembly: AssemblyDescription("Offline Windows dashboard for locally cached Codex quota and token usage.")]
[assembly: AssemblyProduct("Codex Local Quota Dashboard")]
[assembly: AssemblyCompany("yangyangha1")]
[assembly: AssemblyCopyright("Copyright © 2026 yangyangha1")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CodexLocalDashboard
{
    internal enum ThemeMode { Dark, Light, Transparent }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        private static void Main()
        {
            bool first;
            using (var activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CodexLocalDashboard.Activate", out first))
            {
                if (!first)
                {
                    activateSignal.Set();
                    return;
                }
                try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch { try { SetProcessDPIAware(); } catch { } }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(true);
                Application.Run(new DashboardForm(activateSignal));
            }
        }
    }

    internal sealed class DashboardForm : Form
    {
        private const int DesignWidth = 320;
        private const int DesignHeight = 225;
        private readonly UsageScanner scanner = new UsageScanner();
        private readonly System.Windows.Forms.Timer countdownTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer followTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolTip tips = new ToolTip();
        private readonly Panel canvas = new Panel();
        private readonly QuotaStripPanel stripPanel = new QuotaStripPanel();
        private readonly Dictionary<Control, LayoutSpec> layout = new Dictionary<Control, LayoutSpec>();
        private readonly Label quotaTitle = Ui.Label("最近限额快照", 9, FontStyle.Regular, Color.FromArgb(142, 153, 169));
        private readonly Label quotaValue = Ui.Label("读取中…", 26, FontStyle.Bold, Color.White);
        private readonly Label quotaSub = Ui.Label("正在扫描本地日志", 8, FontStyle.Regular, Color.FromArgb(142, 153, 169));
        private readonly Label todayValue = Ui.Metric("—");
        private readonly Label weekValue = Ui.Metric("—");
        private readonly Label monthValue = Ui.Metric("—");
        private readonly Label inputValue = Ui.Detail("—");
        private readonly Label outputValue = Ui.Detail("—");
        private readonly Label cacheValue = Ui.Detail("—");
        private readonly ProgressBar quotaBar = new ProgressBar();
        private Point dragOrigin;
        private bool dragging;
        private bool exiting;
        private bool refreshing;
        private int secondsRemaining = 30;
        private float lastScale;
        private float dpiScale = 1f;
        private bool stripMode;
        private bool dashboardTopMost = true;
        private Rectangle dashboardBounds;
        private IntPtr codexWindow;
        private IntPtr ownedCodexWindow;
        private UsageSnapshot latestSnapshot;
        private ToolStripMenuItem switchModeItem;
        private ToolStripMenuItem topmostMenuItem;
        private ToolStripMenuItem darkThemeItem;
        private ToolStripMenuItem lightThemeItem;
        private ToolStripMenuItem transparentThemeItem;
        private ThemeMode themeMode = ThemeMode.Dark;

        public DashboardForm(EventWaitHandle activateSignal)
        {
            Text = "Codex 本地用量";
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = Math.Max(1f, graphics.DpiX / 96f);
            ClientSize = DpiSize(DesignWidth, DesignHeight);
            MinimumSize = DpiSize(256, 180);
            MaximumSize = DpiSize(576, 405);
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(18, 21, 28);
            ForeColor = Color.White;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9f);

            canvas.Size = new Size(DesignWidth, DesignHeight);
            canvas.BackColor = BackColor;
            Controls.Add(canvas);
            stripPanel.Dock = DockStyle.Fill;
            stripPanel.BackColor = BackColor;
            stripPanel.Visible = false;
            stripPanel.DpiScale = dpiScale;
            Controls.Add(stripPanel);

            Add(quotaTitle, 14, 3, 292, 18);
            Add(quotaValue, 12, 20, 296, 38);
            quotaBar.Maximum = 100; quotaBar.Style = ProgressBarStyle.Continuous;
            Add(quotaBar, 14, 60, 292, 6);
            Add(quotaSub, 14, 68, 292, 18);
            AddSeparator(14, 93, 292);

            AddMetric("今日", todayValue, 14);
            AddMetric("近 7 天", weekValue, 113);
            AddMetric("近 30 天", monthValue, 212);
            AddSeparator(14, 160, 292);

            AddDetail("输入", inputValue, 14, 171);
            AddDetail("输出", outputValue, 113, 171);
            AddDetail("缓存复用", cacheValue, 212, 171);

            CaptureLayout();
            AttachDrag(canvas);
            ConfigureTray();
            ApplyTheme(LoadTheme());
            SetSavedPosition();
            ScaleCanvas();

            FormClosing += OnClosing;
            Resize += delegate { if (!stripMode) ScaleCanvas(); };
            ResizeEnd += delegate { if (!stripMode) SavePosition(); };
            Shown += delegate { RefreshData(); };
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { if (!refreshing && --secondsRemaining <= 0) RefreshData(); };
            countdownTimer.Start();
            followTimer.Interval = 250;
            followTimer.Tick += delegate { FollowCodex(); };
            ThreadPool.RegisterWaitForSingleObject(activateSignal, delegate
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(ShowCurrentMode));
            }, null, Timeout.Infinite, false);
        }

        private Size DpiSize(int width, int height) { return new Size((int)Math.Round(width * dpiScale), (int)Math.Round(height * dpiScale)); }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                var doNotRound = 1;
                DwmSetWindowAttribute(Handle, 33, ref doNotRound, sizeof(int));
                var noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            }
            catch { }
        }

        private void Add(Control control, int x, int y, int width, int height)
        {
            control.Bounds = new Rectangle(x, y, width, height);
            canvas.Controls.Add(control);
        }

        private void AddSeparator(int x, int y, int width)
        {
            var line = new Panel { BackColor = Color.FromArgb(42, 47, 58) };
            Add(line, x, y, width, 1);
        }

        private void AddMetric(string caption, Label value, int x)
        {
            var label = Ui.Label(caption, 8, FontStyle.Regular, Color.FromArgb(126, 137, 153));
            Add(label, x, 102, 94, 18);
            Add(value, x, 120, 94, 32);
        }

        private void AddDetail(string caption, Label value, int x, int y)
        {
            var label = Ui.Label(caption, 8, FontStyle.Regular, Color.FromArgb(126, 137, 153));
            Add(label, x, y, 94, 18);
            Add(value, x, y + 19, 94, 27);
        }

        private void CaptureLayout()
        {
            foreach (Control control in canvas.Controls)
                layout[control] = new LayoutSpec(control.Bounds, control.Font.Size, control.Font.Style, control.Font.FontFamily.Name);
        }

        private void ScaleCanvas()
        {
            if (layout.Count == 0) return;
            var resizeMargin = 4f * dpiScale;
            var userScale = Math.Min((ClientSize.Width - resizeMargin * 2) / (DesignWidth * dpiScale), (ClientSize.Height - resizeMargin * 2) / (DesignHeight * dpiScale));
            userScale = Math.Max(.75f, userScale);
            var layoutScale = dpiScale * userScale;
            canvas.Size = new Size((int)Math.Round(DesignWidth * layoutScale), (int)Math.Round(DesignHeight * layoutScale));
            canvas.Location = new Point((ClientSize.Width - canvas.Width) / 2, (ClientSize.Height - canvas.Height) / 2);
            foreach (var item in layout)
            {
                var b = item.Value.Bounds;
                item.Key.Bounds = new Rectangle((int)(b.X * layoutScale), (int)(b.Y * layoutScale), Math.Max(1, (int)(b.Width * layoutScale)), Math.Max(1, (int)(b.Height * layoutScale)));
                if (Math.Abs(userScale - lastScale) > .01f)
                {
                    var old = item.Key.Font;
                    item.Key.Font = new Font(item.Value.FontName, Math.Max(6, item.Value.FontSize * userScale), item.Value.FontStyle);
                    old.Dispose();
                }
            }
            lastScale = userScale;
        }

        private void AttachDrag(Control parent)
        {
            if (!(parent is Button)) { parent.MouseDown += BeginDrag; parent.MouseMove += ContinueDrag; parent.MouseUp += EndDrag; }
            foreach (Control child in parent.Controls) AttachDrag(child);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (!stripMode && m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    var value = m.LParam.ToInt64();
                    var p = PointToClient(new Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff)));
                    var edge = (int)Math.Round(7 * dpiScale);
                    var left = p.X <= edge; var right = p.X >= ClientSize.Width - edge;
                    var top = p.Y <= edge; var bottom = p.Y >= ClientSize.Height - edge;
                    if (left && top) m.Result = (IntPtr)13;
                    else if (right && top) m.Result = (IntPtr)14;
                    else if (left && bottom) m.Result = (IntPtr)16;
                    else if (right && bottom) m.Result = (IntPtr)17;
                    else if (left) m.Result = (IntPtr)10;
                    else if (right) m.Result = (IntPtr)11;
                    else if (top) m.Result = (IntPtr)12;
                    else if (bottom) m.Result = (IntPtr)15;
                }
                return;
            }
            base.WndProc(ref m);
        }

        private async void RefreshData()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var snapshot = await Task.Run(new Func<UsageSnapshot>(scanner.Scan));
                ApplySnapshot(snapshot);
                secondsRemaining = 30;
            }
            catch (Exception ex)
            {
                quotaSub.Text = ex.Message; secondsRemaining = 30;
            }
            finally { refreshing = false; }
        }

        private void ApplySnapshot(UsageSnapshot s)
        {
            latestSnapshot = s;
            stripPanel.Snapshot = s;
            stripPanel.Invalidate();
            todayValue.Text = Ui.Compact(s.Today.Total);
            weekValue.Text = Ui.Compact(s.Week.Total);
            monthValue.Text = Ui.Compact(s.Month.Total);
            inputValue.Text = Ui.Compact(s.Week.Input);
            outputValue.Text = Ui.Compact(s.Week.Output);
            cacheValue.Text = Ui.Compact(s.Week.Cached);

            var q = s.Quotas.OrderBy(x => x.WindowMinutes).FirstOrDefault();
            if (q == null)
            {
                quotaTitle.Text = "最近限额快照"; quotaValue.Text = "暂无缓存";
                quotaSub.Text = "等待 Codex 写入限额信息"; quotaBar.Value = 0; return;
            }
            quotaTitle.Text = Ui.WindowName(q.WindowMinutes) + " · 缓存快照";
            quotaValue.Text = string.Format("剩余 {0:0.#}%", Math.Max(0, 100 - q.UsedPercent));
            quotaBar.Value = Math.Max(0, Math.Min(100, (int)Math.Round(q.UsedPercent)));
            var reset = q.ResetsAt.HasValue ? q.ResetsAt.Value.ToLocalTime().ToString("M月d日 HH:mm") : "未知";
            quotaSub.Text = string.Format("已用 {0:0.#}% · 重置 {1} · {2:HH:mm:ss}", q.UsedPercent, reset, s.QuotaAt.ToLocalTime());
            tips.SetToolTip(quotaTitle, string.Join("\n", s.Quotas.OrderBy(x => x.WindowMinutes).Select(x => Ui.WindowName(x.WindowMinutes) + "：已用 " + x.UsedPercent.ToString("0.#") + "%")));
        }

        private void ConfigureTray()
        {
            tray.Text = "Codex 本地用量";
            tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowCurrentMode(); };
            var menu = new ContextMenuStrip();
            switchModeItem = new ToolStripMenuItem("切换为 Codex 顶部横条");
            switchModeItem.Click += delegate { ToggleDisplayMode(); };
            menu.Items.Add(switchModeItem);
            darkThemeItem = new ToolStripMenuItem("深色");
            lightThemeItem = new ToolStripMenuItem("浅色");
            transparentThemeItem = new ToolStripMenuItem("透明");
            darkThemeItem.Click += delegate { ApplyTheme(ThemeMode.Dark); };
            lightThemeItem.Click += delegate { ApplyTheme(ThemeMode.Light); };
            transparentThemeItem.Click += delegate { ApplyTheme(ThemeMode.Transparent); };
            menu.Items.Add(darkThemeItem);
            menu.Items.Add(lightThemeItem);
            menu.Items.Add(transparentThemeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("恢复仪表盘默认大小", null, delegate { if (stripMode) ExitStripMode(); ClientSize = DpiSize(DesignWidth, DesignHeight); ScaleCanvas(); SavePosition(); });
            topmostMenuItem = new ToolStripMenuItem("窗口置顶") { Checked = TopMost, CheckOnClick = true };
            topmostMenuItem.CheckedChanged += delegate { if (!stripMode) TopMost = topmostMenuItem.Checked; };
            menu.Items.Add(topmostMenuItem);
            var startup = new ToolStripMenuItem("开机自动启动") { Checked = IsStartupEnabled(), CheckOnClick = true };
            startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
            menu.Items.Add(startup); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("隐藏", null, delegate { Hide(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
            ApplyContextMenu(canvas, menu);
            ApplyContextMenu(stripPanel, menu);
        }

        private void ApplyTheme(ThemeMode mode)
        {
            themeMode = mode;
            var light = mode == ThemeMode.Light || mode == ThemeMode.Transparent;
            var transparent = mode == ThemeMode.Transparent;
            var background = transparent ? Color.Magenta : (light ? Color.FromArgb(244, 246, 249) : Color.FromArgb(18, 21, 28));
            var primary = light ? Color.FromArgb(31, 36, 45) : Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) : Color.FromArgb(142, 153, 169);
            var divider = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(42, 47, 58);
            BackColor = background;
            canvas.BackColor = background;
            stripPanel.BackColor = background;
            stripPanel.Theme = mode;
            Opacity = 1.0;
            TransparencyKey = transparent ? Color.Magenta : Color.Empty;

            foreach (Control control in canvas.Controls)
            {
                var label = control as SmoothLabel;
                if (label != null) label.ForeColor = label.Font.Bold ? primary : muted;
                var line = control as Panel;
                if (line != null) line.BackColor = divider;
            }
            quotaTitle.ForeColor = muted;
            quotaSub.ForeColor = muted;
            quotaValue.ForeColor = primary;
            stripPanel.Invalidate();
            if (darkThemeItem != null) darkThemeItem.Checked = mode == ThemeMode.Dark;
            if (lightThemeItem != null) lightThemeItem.Checked = mode == ThemeMode.Light;
            if (transparentThemeItem != null) transparentThemeItem.Checked = mode == ThemeMode.Transparent;
            SaveTheme(mode);
        }

        private string ThemePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLocalDashboard", "theme.txt"); } }
        private ThemeMode LoadTheme()
        {
            try { ThemeMode value; if (Enum.TryParse(File.ReadAllText(ThemePath), out value)) return value; } catch { }
            return ThemeMode.Dark;
        }
        private void SaveTheme(ThemeMode mode)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)); File.WriteAllText(ThemePath, mode.ToString()); } catch { }
        }

        private static void ApplyContextMenu(Control parent, ContextMenuStrip menu)
        {
            parent.ContextMenuStrip = menu;
            foreach (Control child in parent.Controls) ApplyContextMenu(child, menu);
        }

        private void ShowCurrentMode() { if (stripMode) { codexWindow = IntPtr.Zero; FollowCodex(); } else ShowDashboard(); }
        private void ShowDashboard() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        private void ToggleDisplayMode()
        {
            if (stripMode) ExitStripMode(); else EnterStripMode();
        }

        private void EnterStripMode()
        {
            dashboardBounds = Bounds;
            dashboardTopMost = TopMost;
            SavePosition();
            stripMode = true;
            TopMost = false;
            topmostMenuItem.Enabled = false;
            MinimumSize = new Size(1, 1);
            MaximumSize = Size.Empty;
            canvas.Visible = false;
            stripPanel.Visible = true;
            stripPanel.Snapshot = latestSnapshot;
            stripPanel.Theme = themeMode;
            switchModeItem.Text = "切换为桌面仪表盘";
            codexWindow = IntPtr.Zero;
            followTimer.Start();
            FollowCodex();
        }

        private void ExitStripMode()
        {
            followTimer.Stop();
            stripMode = false;
            if (ownedCodexWindow != IntPtr.Zero) SetWindowLongPtr(Handle, GWL_HWNDPARENT, IntPtr.Zero);
            ownedCodexWindow = IntPtr.Zero;
            codexWindow = IntPtr.Zero;
            stripPanel.Visible = false;
            canvas.Visible = true;
            MinimumSize = DpiSize(256, 180);
            MaximumSize = DpiSize(576, 405);
            if (!dashboardBounds.IsEmpty) Bounds = dashboardBounds;
            TopMost = dashboardTopMost;
            topmostMenuItem.Enabled = true;
            topmostMenuItem.Checked = dashboardTopMost;
            switchModeItem.Text = "切换为 Codex 顶部横条";
            ShowDashboard();
            ScaleCanvas();
        }

        private void FollowCodex()
        {
            if (!stripMode) return;
            if (codexWindow == IntPtr.Zero || !IsWindow(codexWindow)) codexWindow = FindCodexWindow();
            if (codexWindow == IntPtr.Zero || !IsWindowVisible(codexWindow) || IsIconic(codexWindow))
            {
                if (Visible) Hide();
                return;
            }

            uint codexProcessId;
            GetWindowThreadProcessId(codexWindow, out codexProcessId);
            var foreground = GetForegroundWindow();
            uint foregroundProcessId;
            GetWindowThreadProcessId(foreground, out foregroundProcessId);
            var ownProcessId = (uint)Process.GetCurrentProcess().Id;
            if (foregroundProcessId != codexProcessId && foregroundProcessId != ownProcessId)
            {
                if (Visible) Hide();
                return;
            }

            if (ownedCodexWindow != codexWindow)
            {
                SetWindowLongPtr(Handle, GWL_HWNDPARENT, codexWindow);
                ownedCodexWindow = codexWindow;
            }

            RECT rect;
            if (DwmGetWindowAttributeRect(codexWindow, 9, out rect, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(codexWindow, out rect)) return;
            var targetWidth = rect.Right - rect.Left;
            var logicalWidth = Math.Max(280, Math.Min(360, targetWidth / dpiScale - 320));
            var width = (int)Math.Round(logicalWidth * dpiScale);
            var height = (int)Math.Round(24 * dpiScale);
            var x = rect.Left + (targetWidth - width) / 2;
            var y = rect.Top + (int)Math.Round(2 * dpiScale);
            if (!Visible) Show();
            SetWindowPos(Handle, HWND_TOP, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private static IntPtr FindCodexWindow()
        {
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = process.MainModule.FileName;
                    if (path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0) return process.MainWindowHandle;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        private void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
        private void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; var p = Cursor.Position; Location = new Point(Location.X + p.X - dragOrigin.X, Location.Y + p.Y - dragOrigin.Y); dragOrigin = p; }
        private void EndDrag(object sender, MouseEventArgs e) { dragging = false; SavePosition(); }

        private string SettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLocalDashboard", "position-v6.txt"); } }
        private void SetSavedPosition()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var p = File.ReadAllText(SettingsPath).Split(',');
                    if (p.Length >= 4) ClientSize = new Size(Math.Max(MinimumSize.Width, Math.Min(MaximumSize.Width, int.Parse(p[2]))), Math.Max(MinimumSize.Height, Math.Min(MaximumSize.Height, int.Parse(p[3]))));
                    var point = new Point(int.Parse(p[0]), int.Parse(p[1]));
                    if (Screen.AllScreens.Any(s => s.WorkingArea.Contains(point))) { Location = point; return; }
                }
            }
            catch { }
            var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - Width - 24, area.Top + 42);
        }
        private void SavePosition() { try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, Left + "," + Top + "," + ClientSize.Width + "," + ClientSize.Height); } catch { } }
        private static bool IsStartupEnabled() { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue("CodexLocalDashboard") != null; }
        private static void SetStartup(bool enabled) { using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (enabled) key.SetValue("CodexLocalDashboard", "\"" + Application.ExecutablePath + "\""); else key.DeleteValue("CodexLocalDashboard", false); } }
        private void OnClosing(object sender, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; } tray.Visible = false; if (!stripMode) SavePosition(); }

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int GWL_HWNDPARENT = -8;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out RECT value, int size);
    }

    internal sealed class LayoutSpec
    {
        public Rectangle Bounds; public float FontSize; public FontStyle FontStyle; public string FontName;
        public LayoutSpec(Rectangle bounds, float size, FontStyle style, string name) { Bounds = bounds; FontSize = size; FontStyle = style; FontName = name; }
    }

    internal static class Ui
    {
        public static Label Label(string text, float size, FontStyle style, Color color) { return new SmoothLabel { Text = text, Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft }; }
        public static Label Metric(string text) { return Label(text, 13, FontStyle.Bold, Color.White); }
        public static Label Detail(string text) { return Label(text, 9.5f, FontStyle.Bold, Color.FromArgb(224, 228, 236)); }
        public static string Compact(long value) { if (value >= 1000000000) return (value / 1000000000d).ToString("0.##") + "B"; if (value >= 1000000) return (value / 1000000d).ToString("0.##") + "M"; if (value >= 1000) return (value / 1000d).ToString("0.#") + "K"; return value.ToString("N0"); }
        public static string WindowName(int minutes) { if (minutes <= 360) return Math.Max(1, minutes / 60) + " 小时额度"; if (minutes == 10080) return "7 天额度"; return (minutes / 1440d).ToString("0.#") + " 天额度"; }
    }

    internal sealed class SmoothLabel : Label
    {
        public SmoothLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var form = FindForm();
            e.Graphics.TextRenderingHint = form != null && form.TransparencyKey != Color.Empty ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(ForeColor))
            using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None })
                e.Graphics.DrawString(Text, Font, brush, ClientRectangle, format);
        }
    }

    internal sealed class QuotaStripPanel : Panel
    {
        public UsageSnapshot Snapshot { get; set; }
        public float DpiScale { get; set; }
        public ThemeMode Theme { get; set; }

        public QuotaStripPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = Theme == ThemeMode.Transparent ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
            e.Graphics.Clear(BackColor);
            var scale = Math.Max(1f, DpiScale);
            var light = Theme == ThemeMode.Light || Theme == ThemeMode.Transparent;
            var primaryColor = light ? Color.FromArgb(31, 36, 45) : Color.FromArgb(242, 245, 249);
            var mutedColor = light ? Color.FromArgb(91, 101, 116) : Color.FromArgb(151, 161, 177);
            var trackColor = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(55, 61, 73);
            var data = Snapshot;
            if (data == null || data.Quotas.Count == 0)
            {
                using (var font = new Font("Microsoft YaHei UI", 8, FontStyle.Regular))
                using (var brush = new SolidBrush(mutedColor))
                    e.Graphics.DrawString("等待本地限额快照", font, brush, 8 * scale, 3 * scale);
                return;
            }

            var quota = data.Quotas.OrderBy(x => x.WindowMinutes).First();
            var remaining = Math.Max(0, 100 - quota.UsedPercent);
            var reset = quota.ResetsAt.HasValue ? quota.ResetsAt.Value.ToLocalTime().ToString("M-d HH:mm") : "未知";
            var resetWidth = 96 * scale;
            var progressX = 150 * scale;
            var progressRight = ClientSize.Width - resetWidth;
            var progressWidth = Math.Max(20 * scale, progressRight - progressX - 7 * scale);
            var progressHeight = Math.Max(3f, 4 * scale);
            var progressY = (ClientSize.Height - progressHeight) / 2f;

            using (var normal = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular))
            using (var bold = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold))
            using (var muted = new SolidBrush(mutedColor))
            using (var white = new SolidBrush(primaryColor))
            using (var track = new SolidBrush(trackColor))
            using (var accent = new SolidBrush(Color.FromArgb(81, 201, 142)))
            using (var centered = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                e.Graphics.DrawString(ShortWindowName(quota.WindowMinutes), normal, muted, new RectangleF(7 * scale, 0, 48 * scale, ClientSize.Height), centered);
                e.Graphics.DrawString(string.Format("剩余 {0:0.#}%", remaining), bold, white, new RectangleF(57 * scale, 0, 91 * scale, ClientSize.Height), centered);
                e.Graphics.FillRectangle(track, progressX, progressY, progressWidth, progressHeight);
                e.Graphics.FillRectangle(accent, progressX, progressY, (float)(progressWidth * remaining / 100d), progressHeight);
                e.Graphics.DrawString(reset, normal, muted, new RectangleF(ClientSize.Width - resetWidth + 5 * scale, 0, resetWidth - 8 * scale, ClientSize.Height), centered);
            }
        }

        private static string ShortWindowName(int minutes)
        {
            if (minutes <= 360) return Math.Max(1, minutes / 60) + " 小时";
            if (minutes == 10080) return "7 天";
            return (minutes / 1440d).ToString("0.#") + " 天";
        }
    }

    internal sealed class UsageScanner
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, FileState> states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DateTime, TokenTotals> daily = new Dictionary<DateTime, TokenTotals>();
        private QuotaSnapshot latestQuota;
        private static readonly Regex Timestamp = new Regex("\\\"timestamp\\\":\\\"(?<v>[^\\\"]+)", RegexOptions.Compiled);
        private static readonly Regex Usage = new Regex("\\\"total_token_usage\\\":\\{(?<v>[^}]*)\\}", RegexOptions.Compiled);

        public UsageSnapshot Scan()
        {
            lock (gate)
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                foreach (var folder in new[] { Path.Combine(root, "sessions"), Path.Combine(root, "archived_sessions") })
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
                    {
                        var info = new FileInfo(file); if (info.LastWriteTime < DateTime.Now.AddDays(-35)) continue; ProcessFile(file, info.Length);
                    }
                }
                return BuildSnapshot();
            }
        }

        private void ProcessFile(string path, long length)
        {
            FileState state;
            if (!states.TryGetValue(path, out state)) { state = new FileState(); states[path] = state; }
            if (length < state.Offset) { RemoveContribution(state); state = new FileState(); states[path] = state; }
            if (length == state.Offset) return;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(state.Offset, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 65536, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) >= 0) ParseLine(line, state);
                    }
                }
            }
            state.Offset = length;
        }

        private void ParseLine(string line, FileState state)
        {
            var tm = Timestamp.Match(line); DateTimeOffset at;
            if (!tm.Success || !DateTimeOffset.TryParse(tm.Groups["v"].Value, out at)) return;
            var um = Usage.Match(line);
            if (um.Success)
            {
                var body = um.Groups["v"].Value;
                var current = new TokenTotals(Number(body, "input_tokens"), Number(body, "output_tokens"), Number(body, "cached_input_tokens"), Number(body, "reasoning_output_tokens"));
                var delta = current.DeltaFrom(state.LastTotal); var date = at.LocalDateTime.Date;
                TokenTotals total; if (!daily.TryGetValue(date, out total)) total = new TokenTotals(); daily[date] = total + delta;
                TokenTotals own; if (!state.ByDay.TryGetValue(date, out own)) own = new TokenTotals(); state.ByDay[date] = own + delta;
                state.LastTotal = current; state.LastActivity = at; state.HasUsage = true;
            }
            if (at > (latestQuota == null ? DateTimeOffset.MinValue : latestQuota.At) && line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) >= 0)
            {
                var windows = new List<QuotaWindow>(); AddQuota(line, "primary", windows); AddQuota(line, "secondary", windows);
                if (windows.Count > 0) latestQuota = new QuotaSnapshot(at, windows);
            }
        }

        private static long Number(string body, string name)
        {
            var m = Regex.Match(body, "\\\"" + name + "\\\"\\s*:\\s*(?<n>\\d+)"); long n; return m.Success && long.TryParse(m.Groups["n"].Value, out n) ? n : 0;
        }
        private static void AddQuota(string line, string name, List<QuotaWindow> list)
        {
            var m = Regex.Match(line, "\\\"" + name + "\\\"\\s*:\\s*\\{(?<v>[^}]*)\\}"); if (!m.Success) return; var body = m.Groups["v"].Value;
            var minutes = (int)Number(body, "window_minutes"); if (minutes <= 0) return;
            var usedMatch = Regex.Match(body, "\\\"used_percent\\\"\\s*:\\s*(?<n>[0-9.]+)"); double used = 0; if (usedMatch.Success) double.TryParse(usedMatch.Groups["n"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out used);
            var unix = Number(body, "resets_at"); DateTimeOffset? reset = unix > 0 ? (DateTimeOffset?)new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(unix) : null;
            list.Add(new QuotaWindow(minutes, used, reset));
        }
        private void RemoveContribution(FileState state) { foreach (var item in state.ByDay) { TokenTotals total; if (daily.TryGetValue(item.Key, out total)) daily[item.Key] = total - item.Value; } }
        private UsageSnapshot BuildSnapshot()
        {
            var today = DateTime.Now.Date;
            Func<int, TokenTotals> sum = days => daily.Where(x => x.Key >= today.AddDays(-(days - 1)) && x.Key <= today).Aggregate(new TokenTotals(), (a, x) => a + x.Value);
            var weekStart = DateTimeOffset.Now.AddDays(-7);
            return new UsageSnapshot(sum(1), sum(7), sum(30), states.Values.Count(s => s.HasUsage && s.LastActivity >= weekStart), latestQuota == null ? DateTimeOffset.MinValue : latestQuota.At, latestQuota == null ? new List<QuotaWindow>() : latestQuota.Windows);
        }
    }

    internal sealed class FileState
    {
        public long Offset; public TokenTotals LastTotal = new TokenTotals(); public readonly Dictionary<DateTime, TokenTotals> ByDay = new Dictionary<DateTime, TokenTotals>(); public DateTimeOffset LastActivity; public bool HasUsage;
    }
    internal sealed class TokenTotals
    {
        public long Input, Output, Cached, Reasoning; public long Total { get { return Input + Output; } }
        public TokenTotals(long input = 0, long output = 0, long cached = 0, long reasoning = 0) { Input = input; Output = output; Cached = cached; Reasoning = reasoning; }
        public TokenTotals DeltaFrom(TokenTotals p) { return new TokenTotals(Math.Max(0, Input - p.Input), Math.Max(0, Output - p.Output), Math.Max(0, Cached - p.Cached), Math.Max(0, Reasoning - p.Reasoning)); }
        public static TokenTotals operator +(TokenTotals a, TokenTotals b) { return new TokenTotals(a.Input + b.Input, a.Output + b.Output, a.Cached + b.Cached, a.Reasoning + b.Reasoning); }
        public static TokenTotals operator -(TokenTotals a, TokenTotals b) { return new TokenTotals(a.Input - b.Input, a.Output - b.Output, a.Cached - b.Cached, a.Reasoning - b.Reasoning); }
    }
    internal sealed class QuotaWindow { public int WindowMinutes; public double UsedPercent; public DateTimeOffset? ResetsAt; public QuotaWindow(int m, double u, DateTimeOffset? r) { WindowMinutes = m; UsedPercent = u; ResetsAt = r; } }
    internal sealed class QuotaSnapshot { public DateTimeOffset At; public List<QuotaWindow> Windows; public QuotaSnapshot(DateTimeOffset a, List<QuotaWindow> w) { At = a; Windows = w; } }
    internal sealed class UsageSnapshot
    {
        public TokenTotals Today, Week, Month; public int WeekSessions; public DateTimeOffset QuotaAt; public List<QuotaWindow> Quotas;
        public UsageSnapshot(TokenTotals t, TokenTotals w, TokenTotals m, int s, DateTimeOffset q, List<QuotaWindow> l) { Today = t; Week = w; Month = m; WeekSessions = s; QuotaAt = q; Quotas = l; }
    }
}

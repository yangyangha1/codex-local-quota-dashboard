using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Globalization;
using System.Security;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("Codex Local Quota Dashboard")]
[assembly: AssemblyDescription("Offline Windows dashboard for locally cached Codex quota and token usage.")]
[assembly: AssemblyProduct("Codex Local Quota Dashboard")]
[assembly: AssemblyCompany("yangyangha1")]
[assembly: AssemblyCopyright("Copyright © 2026 yangyangha1")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

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
                Application.SetCompatibleTextRenderingDefault(false);
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
        private RegisteredWaitHandle activationRegistration;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolTip tips = new ToolTip();
        private readonly ContextMenuStrip contextMenu = new ContextMenuStrip();
        private readonly CancellationTokenSource refreshCancellation = new CancellationTokenSource();
        private readonly Panel canvas = new Panel();
        private readonly QuotaStripPanel stripPanel = new QuotaStripPanel();
        private readonly StripBackdropForm stripBackdrop = new StripBackdropForm();
        private readonly Form taskbarOwner = new Form();
        private readonly Dictionary<Control, LayoutSpec> layout = new Dictionary<Control, LayoutSpec>();
        private readonly Label quotaTitle = Ui.Label("最近限额快照", 9, FontStyle.Bold, Color.FromArgb(142, 153, 169));
        private readonly Label quotaValue = Ui.Label("读取中…", 22, FontStyle.Bold, Color.White);
        private readonly Label quotaSub = Ui.Label("正在扫描本地日志", 8, FontStyle.Bold, Color.FromArgb(142, 153, 169));
        private readonly Label todayValue = Ui.Metric("—");
        private readonly Label weekValue = Ui.Metric("—");
        private readonly Label monthValue = Ui.Metric("—");
        private readonly Label inputValue = Ui.Detail("—");
        private readonly Label outputValue = Ui.Detail("—");
        private readonly Label cacheValue = Ui.Detail("—");
        private readonly QuotaProgressBar quotaBar = new QuotaProgressBar();
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
        private Icon trayIcon;
        private Rectangle lastStripBounds = Rectangle.Empty;
        private Rectangle lastBackdropBounds = Rectangle.Empty;
        private int codexMissCount;
        private bool changingStartup;
        private bool lastCodexForeground;
        private bool initialMemoryTrimDone;
        private int backgroundTransparency = 10;
        private static readonly uint OwnProcessId = unchecked((uint)Process.GetCurrentProcess().Id);

        public DashboardForm(EventWaitHandle activateSignal)
        {
            Text = "Codex 本地用量";
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = Math.Max(1f, graphics.DpiX / 96f);
            ClientSize = DpiSize(256, 180);
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
            Font = new Font(Ui.FontFamilyName, 9f);
            taskbarOwner.ShowInTaskbar = false;
            taskbarOwner.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            taskbarOwner.Opacity = 0;
            taskbarOwner.Size = new Size(1, 1);
            taskbarOwner.StartPosition = FormStartPosition.Manual;
            taskbarOwner.Location = new Point(-32000, -32000);
            stripBackdrop.BackColor = Color.FromArgb(244, 244, 242);
            stripBackdrop.Opacity = 0.90;
            HandleCreated += delegate { EnsureHiddenFromTaskbar(); };

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
            backgroundTransparency = LoadBackgroundTransparency();
            ApplyTheme(LoadTheme(), false);
            SetSavedPosition();
            ScaleCanvas();

            FormClosing += OnClosing;
            Resize += delegate { if (!stripMode) ScaleCanvas(); };
            ResizeEnd += delegate { if (!stripMode) SavePosition(); };
            Shown += delegate
            {
                SetPerPixelLayered(true);
                RenderLayeredSurface();
                RefreshData();
            };
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { if (!refreshing && --secondsRemaining <= 0) RefreshData(); };
            countdownTimer.Start();
            followTimer.Interval = 250;
            followTimer.Tick += delegate { FollowCodex(); };
            activationRegistration = ThreadPool.RegisterWaitForSingleObject(activateSignal, delegate
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
            ApplyCornerPreference();
            try
            {
                var noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            }
            catch { }
            SetPerPixelLayered(true);
        }

        private void ApplyCornerPreference()
        {
            if (!IsHandleCreated) return;
            try
            {
                var cornerPreference = !stripMode && Environment.OSVersion.Version.Build >= 22000 ? 3 : 1;
                DwmSetWindowAttribute(Handle, 33, ref cornerPreference, sizeof(int));
            }
            catch { }
        }

        protected override bool ShowWithoutActivation { get { return stripMode; } }

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
            var label = Ui.Label(caption, 8, FontStyle.Bold, Color.FromArgb(126, 137, 153));
            Add(label, x, 102, 94, 18);
            Add(value, x, 120, 94, 32);
        }

        private void AddDetail(string caption, Label value, int x, int y)
        {
            var label = Ui.Label(caption, 8, FontStyle.Bold, Color.FromArgb(126, 137, 153));
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
                var left = (int)Math.Round(b.Left * layoutScale);
                var top = (int)Math.Round(b.Top * layoutScale);
                var right = (int)Math.Round(b.Right * layoutScale);
                var bottom = (int)Math.Round(b.Bottom * layoutScale);
                item.Key.Bounds = Rectangle.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
                if (Math.Abs(userScale - lastScale) > .01f)
                {
                    var old = item.Key.Font;
                    item.Key.Font = new Font(item.Value.FontName, Math.Max(6, item.Value.FontSize * userScale), item.Value.FontStyle);
                    old.Dispose();
                }
            }
            lastScale = userScale;
            RenderLayeredSurface();
        }

        private void AttachDrag(Control parent)
        {
            if (!(parent is Button)) { parent.MouseDown += BeginDrag; parent.MouseMove += ContinueDrag; parent.MouseUp += EndDrag; }
            foreach (Control child in parent.Controls) AttachDrag(child);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                var newDpi = (int)(m.WParam.ToInt64() & 0xffff);
                if (newDpi >= 96)
                {
                    dpiScale = newDpi / 96f;
                    stripPanel.DpiScale = dpiScale;
                    if (!stripMode)
                    {
                        var suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        MinimumSize = DpiSize(256, 180);
                        MaximumSize = DpiSize(576, 405);
                        Bounds = Rectangle.FromLTRB(suggested.Left, suggested.Top, suggested.Right, suggested.Bottom);
                        ScaleCanvas();
                    }
                    lastStripBounds = Rectangle.Empty;
                    lastBackdropBounds = Rectangle.Empty;
                }
                return;
            }
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
                var snapshot = await Task.Run(new Func<UsageSnapshot>(scanner.Scan), refreshCancellation.Token);
                if (refreshCancellation.IsCancellationRequested || IsDisposed) return;
                ApplySnapshot(snapshot);
                secondsRemaining = 30;
                TrimInitialWorkingSet();
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException) && !IsDisposed) quotaSub.Text = "部分日志暂时无法读取";
                secondsRemaining = 30;
            }
            finally { refreshing = false; }
        }

        private void TrimInitialWorkingSet()
        {
            if (!initialMemoryTrimDone)
            {
                initialMemoryTrimDone = true;
                GC.Collect(2, GCCollectionMode.Optimized, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Optimized, true);
            }
            try
            {
                using (var process = Process.GetCurrentProcess()) EmptyWorkingSet(process.Handle);
            }
            catch { }
        }

        internal void ApplySnapshot(UsageSnapshot s)
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
            var remaining = Math.Max(0, 100 - q.UsedPercent);
            quotaValue.Text = string.Format("剩余 {0:0.#}%", remaining);
            quotaBar.Value = Math.Max(0, Math.Min(100, (int)Math.Round(remaining)));
            quotaBar.FillColor = Ui.QuotaColor(remaining);
            var reset = q.ResetsAt.HasValue ? q.ResetsAt.Value.ToLocalTime().ToString("M月d日 HH:mm") : "未知";
            quotaSub.Text = string.Format("已用 {0:0.#}% · 重置 {1} · {2:HH:mm:ss}", q.UsedPercent, reset, s.QuotaAt.ToLocalTime());
            tips.SetToolTip(quotaTitle, string.Join("\n", s.Quotas.OrderBy(x => x.WindowMinutes).Select(x => Ui.WindowName(x.WindowMinutes) + "：已用 " + x.UsedPercent.ToString("0.#") + "%")));
            RenderLayeredSurface();
        }

        private void ConfigureTray()
        {
            tray.Text = "Codex 本地用量";
            trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = trayIcon ?? SystemIcons.Application;
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (stripMode) ExitStripMode(); else ShowDashboard();
            };
            var menu = contextMenu;
            switchModeItem = new ToolStripMenuItem("切换为 Codex 顶部横条");
            switchModeItem.Click += delegate { ToggleDisplayMode(); };
            menu.Items.Add(switchModeItem);
            darkThemeItem = new ToolStripMenuItem("深色");
            lightThemeItem = new ToolStripMenuItem("浅色");
            transparentThemeItem = new ToolStripMenuItem("透明");
            darkThemeItem.Click += delegate { ApplyTheme(ThemeMode.Dark, true); };
            lightThemeItem.Click += delegate { ApplyTheme(ThemeMode.Light, true); };
            transparentThemeItem.Click += delegate { ApplyTheme(ThemeMode.Transparent, true); };
            menu.Items.Add(darkThemeItem);
            menu.Items.Add(lightThemeItem);
            menu.Items.Add(transparentThemeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("调整背景透明度…", null, delegate { ShowTransparencyDialog(); });
            topmostMenuItem = new ToolStripMenuItem("窗口置顶") { Checked = TopMost, CheckOnClick = true };
            topmostMenuItem.CheckedChanged += delegate { if (!stripMode) TopMost = topmostMenuItem.Checked; };
            menu.Items.Add(topmostMenuItem);
            var startup = new ToolStripMenuItem("开机自动启动") { Checked = IsStartupEnabled(), CheckOnClick = true };
            startup.CheckedChanged += delegate
            {
                if (changingStartup) return;
                var wanted = startup.Checked;
                if (SetStartup(wanted)) return;
                changingStartup = true;
                startup.Checked = !wanted;
                changingStartup = false;
                tray.ShowBalloonTip(2500, "Codex 本地用量", "无法更改开机启动设置。", ToolTipIcon.Warning);
            };
            menu.Items.Add(startup); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("隐藏", null, delegate { Hide(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
            stripBackdrop.ContextMenuStrip = menu;
            ApplyContextMenu(canvas, menu);
            ApplyContextMenu(stripPanel, menu);
        }

        internal void ApplyTheme(ThemeMode mode, bool savePreference)
        {
            themeMode = mode;
            var light = mode == ThemeMode.Light || mode == ThemeMode.Transparent;
            var transparent = mode == ThemeMode.Transparent;
            var transparentKey = Color.FromArgb(1, 1, 1);
            var dashboardBackground = transparent ? transparentKey : (light ? Color.FromArgb(236, 245, 250) : Color.FromArgb(26, 34, 37));
            var stripBackground = light ? Color.FromArgb(244, 244, 242) : Color.FromArgb(20, 20, 20);
            var activeColorKey = transparent ? transparentKey : stripBackground;
            var primary = transparent ? Color.FromArgb(68, 75, 86) : (light ? Color.Black : Color.FromArgb(242, 245, 249));
            var muted = transparent ? Color.FromArgb(101, 111, 124) : (light ? Color.FromArgb(91, 101, 116) : Color.FromArgb(142, 153, 169));
            var divider = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(42, 47, 58);
            BackColor = stripMode ? activeColorKey : dashboardBackground;
            canvas.BackColor = dashboardBackground;
            stripPanel.BackColor = stripMode ? activeColorKey : dashboardBackground;
            stripBackdrop.BackColor = stripBackground;
            stripBackdrop.Opacity = 0.90;
            stripPanel.Theme = mode;
            Opacity = 1.0;
            TransparencyKey = Color.Empty;
            SetPerPixelLayered(true);
            stripBackdrop.Hide();

            foreach (Control control in canvas.Controls)
            {
                var label = control as SmoothLabel;
                if (label != null) label.ForeColor = label.Role == TextRole.Muted ? muted : primary;
                var line = control as Panel;
                if (line != null) line.BackColor = divider;
            }
            quotaTitle.ForeColor = muted;
            quotaSub.ForeColor = muted;
            quotaValue.ForeColor = primary;
            quotaBar.TrackColor = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(55, 61, 73);
            stripPanel.Invalidate();
            if (darkThemeItem != null) darkThemeItem.Checked = mode == ThemeMode.Dark;
            if (lightThemeItem != null) lightThemeItem.Checked = mode == ThemeMode.Light;
            if (transparentThemeItem != null) transparentThemeItem.Checked = mode == ThemeMode.Transparent;
            if (savePreference) SaveTheme(mode);
            RenderLayeredSurface();
        }

        private string ThemePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLocalDashboard", "theme.txt"); } }
        private string BackgroundTransparencyPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLocalDashboard", "background-transparency.txt"); } }
        private int BackgroundAlpha
        {
            get
            {
                if (backgroundTransparency >= 100) return 1;
                return Math.Max(1, Math.Min(255, (int)Math.Round(255d * (100 - backgroundTransparency) / 100d)));
            }
        }
        private ThemeMode LoadTheme()
        {
            try { ThemeMode value; if (Enum.TryParse(File.ReadAllText(ThemePath), out value)) return value; } catch { }
            return ThemeMode.Dark;
        }
        private void SaveTheme(ThemeMode mode)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)); File.WriteAllText(ThemePath, mode.ToString()); } catch { }
        }

        private int LoadBackgroundTransparency()
        {
            try
            {
                int value;
                if (int.TryParse(File.ReadAllText(BackgroundTransparencyPath), out value)) return Math.Max(0, Math.Min(100, value));
            }
            catch { }
            return 10;
        }

        private void SaveBackgroundTransparency()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BackgroundTransparencyPath));
                File.WriteAllText(BackgroundTransparencyPath, backgroundTransparency.ToString());
            }
            catch { }
        }

        private void ShowTransparencyDialog()
        {
            var original = backgroundTransparency;
            using (var dialog = new TransparencyDialog(backgroundTransparency, dpiScale, delegate(int value)
            {
                backgroundTransparency = value;
                RenderLayeredSurface();
            }))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    backgroundTransparency = dialog.TransparencyValue;
                    SaveBackgroundTransparency();
                }
                else backgroundTransparency = original;
            }
            RenderLayeredSurface();
        }

        private static void ApplyContextMenu(Control parent, ContextMenuStrip menu)
        {
            parent.ContextMenuStrip = menu;
            foreach (Control child in parent.Controls) ApplyContextMenu(child, menu);
        }

        private void ShowCurrentMode() { if (stripMode) { codexWindow = IntPtr.Zero; FollowCodex(); } else ShowDashboard(); }
        private void ShowDashboard()
        {
            EnsureHiddenFromTaskbar();
            Show();
            WindowState = FormWindowState.Normal;
            EnsureHiddenFromTaskbar();
            Activate();
        }

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
            ApplyCornerPreference();
            TopMost = false;
            topmostMenuItem.Enabled = false;
            MinimumSize = new Size(1, 1);
            MaximumSize = Size.Empty;
            canvas.Visible = false;
            stripPanel.Visible = true;
            stripPanel.Snapshot = latestSnapshot;
            stripPanel.Theme = themeMode;
            ApplyTheme(themeMode, false);
            switchModeItem.Text = "切换为桌面仪表盘";
            codexWindow = IntPtr.Zero;
            codexMissCount = 0;
            lastStripBounds = Rectangle.Empty;
            lastBackdropBounds = Rectangle.Empty;
            EnsureHiddenFromTaskbar();
            followTimer.Start();
            FollowCodex();
        }

        private void ExitStripMode()
        {
            followTimer.Stop();
            stripMode = false;
            ApplyCornerPreference();
            stripBackdrop.Hide();
            if (ownedCodexWindow != IntPtr.Zero) SetWindowLongPtr(Handle, GWL_HWNDPARENT, taskbarOwner.Handle);
            ownedCodexWindow = IntPtr.Zero;
            codexWindow = IntPtr.Zero;
            lastStripBounds = Rectangle.Empty;
            lastBackdropBounds = Rectangle.Empty;
            stripPanel.Visible = false;
            canvas.Visible = true;
            ApplyTheme(themeMode, false);
            ShowInTaskbar = false;
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
                codexMissCount++;
                followTimer.Interval = codexMissCount < 5 ? 1000 : 3000;
                HideStripWindows();
                return;
            }
            codexMissCount = 0;

            uint codexProcessId;
            GetWindowThreadProcessId(codexWindow, out codexProcessId);
            var foreground = GetForegroundWindow();
            uint foregroundProcessId;
            GetWindowThreadProcessId(foreground, out foregroundProcessId);
            var codexForeground = foregroundProcessId == codexProcessId || foregroundProcessId == OwnProcessId;
            followTimer.Interval = codexForeground ? 250 : 500;

            if (ownedCodexWindow != codexWindow)
            {
                SetWindowLongPtr(Handle, GWL_HWNDPARENT, codexWindow);
                SetWindowLongPtr(stripBackdrop.Handle, GWL_HWNDPARENT, codexWindow);
                ownedCodexWindow = codexWindow;
                EnsureHiddenFromTaskbar();
            }

            RECT rect;
            if (DwmGetWindowAttributeRect(codexWindow, 9, out rect, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(codexWindow, out rect)) return;
            var needsRender = false;
            var targetDpiScale = GetWindowDpiScale(codexWindow);
            if (Math.Abs(targetDpiScale - dpiScale) > .01f)
            {
                dpiScale = targetDpiScale;
                stripPanel.DpiScale = dpiScale;
                stripPanel.InvalidatePreferredWidth();
                lastStripBounds = Rectangle.Empty;
                lastBackdropBounds = Rectangle.Empty;
                needsRender = true;
            }
            var targetWidth = rect.Right - rect.Left;
            var availableLogicalWidth = Math.Max(280, targetWidth / dpiScale - 220);
            var preferredLogicalWidth = stripPanel.GetPreferredLogicalWidth();
            var logicalWidth = Math.Max(280, Math.Min(Math.Min(520, availableLogicalWidth), preferredLogicalWidth));
            var width = (int)Math.Round(logicalWidth * dpiScale);
            var height = (int)Math.Round(24 * dpiScale);
            var x = rect.Left + (targetWidth - width) / 2;
            var y = rect.Top + (int)Math.Round(7 * dpiScale);
            var targetBounds = new Rectangle(x, y, width, height);
            if (lastStripBounds.Size != targetBounds.Size) needsRender = true;
            if (stripBackdrop.Visible) stripBackdrop.Hide();
            lastBackdropBounds = Rectangle.Empty;
            var wasVisible = Visible;
            if (!wasVisible) Show();
            if (lastStripBounds != targetBounds || !wasVisible || (codexForeground && !lastCodexForeground))
            {
                var flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
                if (!codexForeground) flags |= SWP_NOZORDER;
                SetWindowPos(Handle, codexForeground ? HWND_TOP : IntPtr.Zero, x, y, width, height, flags);
                lastStripBounds = targetBounds;
            }
            lastCodexForeground = codexForeground;
            if (needsRender) RenderLayeredSurface();
        }

        private void SetPerPixelLayered(bool enabled)
        {
            if (!IsHandleCreated) return;
            var style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            var updated = enabled ? style | WS_EX_LAYERED : style & ~WS_EX_LAYERED;
            if (updated == style) return;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(updated));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private void RenderLayeredSurface()
        {
            if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (var bitmap = CreateLayeredSurfacePreview()) ApplyLayeredBitmap(bitmap);
        }

        internal Bitmap CreateLayeredSurfacePreview()
        {
            var bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                var darkBackground = themeMode == ThemeMode.Dark;
                var layeredBackground = stripMode
                    ? (darkBackground ? Color.FromArgb(20, 20, 20) : Color.FromArgb(244, 244, 242))
                    : (darkBackground ? Color.FromArgb(26, 34, 37) : Color.FromArgb(236, 245, 250));
                using (var backgroundLayer = new SolidBrush(Color.FromArgb(BackgroundAlpha, layeredBackground))) graphics.FillRectangle(backgroundLayer, 0, 0, bitmap.Width, bitmap.Height);
                if (stripMode)
                {
                    stripPanel.DrawLayered(graphics);
                }
                else DrawLayeredDashboard(graphics);
            }
            return bitmap;
        }

        private void DrawLayeredDashboard(Graphics graphics)
        {
            foreach (Control control in canvas.Controls)
            {
                var bounds = new Rectangle(canvas.Left + control.Left, canvas.Top + control.Top, control.Width, control.Height);
                var label = control as SmoothLabel;
                if (label != null)
                {
                    using (var brush = new SolidBrush(label.ForeColor))
                    using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None })
                        graphics.DrawString(label.Text, label.Font, brush, bounds, format);
                    continue;
                }
                var progress = control as QuotaProgressBar;
                if (progress != null)
                {
                    using (var track = new SolidBrush(Color.FromArgb(150, progress.TrackColor))) graphics.FillRectangle(track, bounds);
                    var fillWidth = (int)Math.Round(bounds.Width * progress.Value / 100d);
                    if (fillWidth > 0) using (var fill = new SolidBrush(progress.FillColor)) graphics.FillRectangle(fill, bounds.X, bounds.Y, fillWidth, bounds.Height);
                    continue;
                }
                if (control is Panel)
                    using (var divider = new SolidBrush(Color.FromArgb(110, control.BackColor))) graphics.FillRectangle(divider, bounds);
            }
        }

        private void ApplyLayeredBitmap(Bitmap bitmap)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            var info = new BITMAPINFO();
            info.Header.Size = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.Header.Width = bitmap.Width;
            info.Header.Height = -bitmap.Height;
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            IntPtr bits;
            var bitmapHandle = CreateDIBSection(memoryDc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bitmapHandle == IntPtr.Zero || bits == IntPtr.Zero)
            {
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                return;
            }
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                    CopyMemory(IntPtr.Add(bits, y * rowBytes), IntPtr.Add(data.Scan0, y * data.Stride), new UIntPtr((uint)rowBytes));
            }
            finally { bitmap.UnlockBits(data); }
            var previous = SelectObject(memoryDc, bitmapHandle);
            try
            {
                var destination = Location;
                var size = bitmap.Size;
                var source = Point.Empty;
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                var updated = UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2);
#if RENDER_DIAGNOSTICS
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "CodexLocalDashboard-layered-status.txt"), "updated=" + updated + ";error=" + Marshal.GetLastWin32Error() + ";size=" + size.Width + "x" + size.Height); } catch { }
#endif
            }
            finally
            {
                SelectObject(memoryDc, previous);
                DeleteObject(bitmapHandle);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void HideStripWindows()
        {
            if (Visible) Hide();
            if (stripBackdrop.Visible) stripBackdrop.Hide();
            lastStripBounds = Rectangle.Empty;
            lastBackdropBounds = Rectangle.Empty;
            lastCodexForeground = false;
        }

        private static float GetWindowDpiScale(IntPtr window)
        {
            try
            {
                var dpi = GetDpiForWindow(window);
                if (dpi >= 96) return dpi / 96f;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            return 1f;
        }

        private static IntPtr FindCodexWindow()
        {
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle == IntPtr.Zero) continue;
                        var path = process.MainModule.FileName;
                        if (path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0) return process.MainWindowHandle;
                    }
                    catch { }
                }
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
                    var candidate = new Rectangle(point, Size);
                    if (Screen.AllScreens.Any(s => VisibleFraction(candidate, s.WorkingArea) >= .30)) { Location = point; return; }
                }
            }
            catch { }
            var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - Width - 24, area.Top + 42);
        }
        private static double VisibleFraction(Rectangle window, Rectangle workArea)
        {
            if (window.Width <= 0 || window.Height <= 0) return 0;
            var visible = Rectangle.Intersect(window, workArea);
            return (double)visible.Width * visible.Height / ((double)window.Width * window.Height);
        }
        private void SavePosition() { try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, Left + "," + Top + "," + ClientSize.Width + "," + ClientSize.Height); } catch { } }
        private void EnsureHiddenFromTaskbar()
        {
            ShowInTaskbar = false;
            if (!IsHandleCreated) return;
            var style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            style = (style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            if (stripMode) style |= WS_EX_NOACTIVATE;
            else style &= ~WS_EX_NOACTIVATE;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style));
            if (!stripMode) SetWindowLongPtr(Handle, GWL_HWNDPARENT, taskbarOwner.Handle);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        private static bool IsStartupEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    var configured = key == null ? null : key.GetValue("CodexLocalDashboard") as string;
                    return string.Equals(configured, "\"" + Application.ExecutablePath + "\"", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) { if (!(ex is UnauthorizedAccessException) && !(ex is SecurityException) && !(ex is IOException)) throw; return false; }
        }
        private static bool SetStartup(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null) return false;
                    if (enabled) key.SetValue("CodexLocalDashboard", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("CodexLocalDashboard", false);
                    return true;
                }
            }
            catch (Exception ex) { if (!(ex is UnauthorizedAccessException) && !(ex is SecurityException) && !(ex is IOException)) throw; return false; }
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                stripBackdrop.Hide();
                return;
            }
            if (!stripMode) SavePosition();
            refreshCancellation.Cancel();
            if (activationRegistration != null) activationRegistration.Unregister(null);
            countdownTimer.Stop();
            followTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            tips.Dispose();
            contextMenu.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            refreshCancellation.Dispose();
            countdownTimer.Dispose();
            followTimer.Dispose();
            stripBackdrop.Dispose();
            taskbarOwner.Dispose();
        }

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const int GWL_HWNDPARENT = -8;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_APPWINDOW = 0x00040000L;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_LAYERED = 0x00080000L;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO { public BITMAPINFOHEADER Header; public int Colors; }
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
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out RECT value, int size);
    }

    internal sealed class StripBackdropForm : Form
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public StripBackdropForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return value;
            }
        }
    }

    internal sealed class TransparencyDialog : Form
    {
        private readonly TrackBar slider = new TrackBar();
        private readonly Label valueLabel = new Label();
        public int TransparencyValue { get { return slider.Value; } }

        public TransparencyDialog(int initialValue, float dpiScale, Action<int> changed)
        {
            var scale = Math.Max(1f, dpiScale);
            Func<int, int> s = value => (int)Math.Round(value * scale);

            Text = "调整背景透明度";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(s(360), s(168));
            Font = new Font(Ui.FontFamilyName, 9f);

            valueLabel.Text = "背景透明度：" + initialValue + "%";
            valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            valueLabel.Bounds = new Rectangle(s(20), s(14), s(320), s(24));
            Controls.Add(valueLabel);

            slider.Minimum = 0;
            slider.Maximum = 100;
            slider.TickFrequency = 10;
            slider.SmallChange = 1;
            slider.LargeChange = 10;
            slider.Value = Math.Max(0, Math.Min(100, initialValue));
            slider.Bounds = new Rectangle(s(14), s(42), s(332), s(45));
            slider.ValueChanged += delegate
            {
                valueLabel.Text = "背景透明度：" + slider.Value + "%";
                if (changed != null) changed(slider.Value);
            };
            Controls.Add(slider);

            var opaqueLabel = new Label { Text = "0  完全不透明", AutoSize = true, Location = new Point(s(20), s(91)) };
            var transparentLabel = new Label { Text = "100  完全透明", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            transparentLabel.Location = new Point(s(244), s(91));
            Controls.Add(opaqueLabel);
            Controls.Add(transparentLabel);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(s(188), s(126), s(72), s(29)) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(s(270), s(126), s(72), s(29)) };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class LayoutSpec
    {
        public Rectangle Bounds; public float FontSize; public FontStyle FontStyle; public string FontName;
        public LayoutSpec(Rectangle bounds, float size, FontStyle style, string name) { Bounds = bounds; FontSize = size; FontStyle = style; FontName = name; }
    }

    internal static class Ui
    {
        private static readonly string resolvedUiFont = ResolveUiFont();
        public static string FontFamilyName { get { return resolvedUiFont; } }
        private static string ResolveUiFont()
        {
            try
            {
                using (var installed = new InstalledFontCollection())
                {
                    var names = new HashSet<string>(installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    if (names.Contains("Segoe UI Variable Text")) return "Segoe UI Variable Text";
                    if (names.Contains("Segoe UI")) return "Segoe UI";
                }
            }
            catch { }
            return "Microsoft YaHei UI";
        }
        public static Label Label(string text, float size, FontStyle style, Color color) { return new SmoothLabel { Text = text, Font = new Font(FontFamilyName, size, style), ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, Role = size <= 9f ? TextRole.Muted : TextRole.Primary }; }
        public static Label Metric(string text) { return Label(text, 13, FontStyle.Bold, Color.White); }
        public static Label Detail(string text) { return Label(text, 9.5f, FontStyle.Bold, Color.FromArgb(224, 228, 236)); }
        public static Color QuotaColor(double remaining)
        {
            remaining = Math.Max(0, Math.Min(100, remaining));
            var stops = new[] { 0d, 10d, 30d, 35d, 50d, 65d, 80d, 100d };
            var colors = new[]
            {
                Color.FromArgb(211, 61, 61),
                Color.FromArgb(224, 75, 68),
                Color.FromArgb(229, 103, 58),
                Color.FromArgb(232, 145, 53),
                Color.FromArgb(224, 174, 57),
                Color.FromArgb(164, 197, 72),
                Color.FromArgb(91, 201, 117),
                Color.FromArgb(73, 205, 143)
            };
            for (var i = 0; i < stops.Length - 1; i++)
            {
                if (remaining <= stops[i + 1])
                    return Blend(colors[i], colors[i + 1], (remaining - stops[i]) / (stops[i + 1] - stops[i]));
            }
            return colors[colors.Length - 1];
        }
        private static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }
        public static string Compact(long value) { if (value >= 1000000000) return (value / 1000000000d).ToString("0.##") + "B"; if (value >= 1000000) return (value / 1000000d).ToString("0.##") + "M"; if (value >= 1000) return (value / 1000d).ToString("0.#") + "K"; return value.ToString("N0"); }
        public static string WindowName(int minutes)
        {
            if (minutes < 60) return minutes + " 分钟额度";
            if (minutes < 1440) return FormatDuration(minutes / 60d) + " 小时额度";
            return FormatDuration(minutes / 1440d) + " 天额度";
        }
        private static string FormatDuration(double value) { return Math.Abs(value - Math.Round(value)) < .001 ? Math.Round(value).ToString("0") : value.ToString("0.#"); }
    }

    internal enum TextRole { Primary, Muted }

    internal sealed class SmoothLabel : Label
    {
        public TextRole Role { get; set; }
        public SmoothLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(ForeColor))
            using (var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None })
                e.Graphics.DrawString(Text, Font, brush, ClientRectangle, format);
        }
    }

    internal sealed class QuotaProgressBar : Control
    {
        private int currentValue;
        private Color fillColor = Color.FromArgb(81, 201, 142);
        private Color trackColor = Color.FromArgb(55, 61, 73);

        public int Value { get { return currentValue; } set { currentValue = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public Color FillColor { get { return fillColor; } set { fillColor = value; Invalidate(); } }
        public Color TrackColor { get { return trackColor; } set { trackColor = value; Invalidate(); } }

        public QuotaProgressBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var track = new SolidBrush(trackColor))
            using (var fill = new SolidBrush(fillColor))
            {
                e.Graphics.FillRectangle(track, ClientRectangle);
                e.Graphics.FillRectangle(fill, 0, 0, (int)Math.Round(ClientSize.Width * currentValue / 100d), ClientSize.Height);
            }
        }
    }

    internal sealed class QuotaStripPanel : Panel
    {
        private const string StripFontFamily = "Microsoft YaHei UI";
        private UsageSnapshot snapshot;
        private float dpiScale = 1f;
        private int preferredLogicalWidth = 280;
        private bool preferredWidthDirty = true;
        public UsageSnapshot Snapshot { get { return snapshot; } set { snapshot = value; InvalidatePreferredWidth(); } }
        public float DpiScale { get { return dpiScale; } set { if (Math.Abs(dpiScale - value) > .01f) { dpiScale = value; InvalidatePreferredWidth(); } } }
        public ThemeMode Theme { get; set; }

        public QuotaStripPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public int GetPreferredLogicalWidth()
        {
            if (!preferredWidthDirty) return preferredLogicalWidth;
            var scale = Math.Max(1f, DpiScale);
            using (var font = new Font(StripFontFamily, 10f, FontStyle.Regular))
            {
                var data = Snapshot;
                var leftText = "等待本地限额快照";
                var resetText = "重置日期：未知";
                if (data != null && data.Quotas.Count > 0)
                {
                    var quota = data.Quotas.OrderBy(x => x.WindowMinutes).First();
                    leftText = string.Format("{0}剩余：{1:0.#}%", ShortWindowName(quota.WindowMinutes), Math.Max(0, 100 - quota.UsedPercent));
                    resetText = quota.ResetsAt.HasValue ? "重置日期：" + quota.ResetsAt.Value.ToLocalTime().ToString("M月d日") : "重置日期：未知";
                }
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var leftWidth = TextRenderer.MeasureText(leftText, font, Size.Empty, flags).Width;
                var resetWidth = TextRenderer.MeasureText(resetText, font, Size.Empty, flags).Width;
                preferredLogicalWidth = Math.Max(280, (int)Math.Ceiling((7 * scale + leftWidth + 5 * scale + 110 * scale + 4 * scale + resetWidth + 6 * scale) / scale));
                preferredWidthDirty = false;
                return preferredLogicalWidth;
            }
        }

        public void InvalidatePreferredWidth() { preferredWidthDirty = true; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawContent(e.Graphics, false);
        }

        public void DrawLayered(Graphics graphics) { DrawContent(graphics, true); }

        private void DrawContent(Graphics graphics, bool layered)
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            if (!layered) graphics.Clear(BackColor);
            var scale = Math.Max(1f, DpiScale);
            var light = Theme == ThemeMode.Light || Theme == ThemeMode.Transparent;
            var menuTextColor = light ? Color.FromArgb(117, 117, 117) : Color.FromArgb(174, 174, 174);
            var trackColor = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(55, 61, 73);
            var data = Snapshot;
            if (data == null || data.Quotas.Count == 0)
            {
                using (var font = new Font(StripFontFamily, 10f, FontStyle.Regular))
                using (var brush = new SolidBrush(menuTextColor))
                using (var waitingFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    graphics.DrawString("等待本地限额快照", font, brush, new RectangleF(8 * scale, 0, ClientSize.Width - 16 * scale, ClientSize.Height), waitingFormat);
                return;
            }

            var quota = data.Quotas.OrderBy(x => x.WindowMinutes).First();
            var remaining = Math.Max(0, 100 - quota.UsedPercent);
            var reset = quota.ResetsAt.HasValue ? "重置日期：" + quota.ResetsAt.Value.ToLocalTime().ToString("M月d日") : "重置日期：未知";
            var progressHeight = Math.Max(3f, 4 * scale);
            var progressY = (ClientSize.Height - progressHeight) / 2f;

            using (var normal = new Font(StripFontFamily, 10f, FontStyle.Regular))
            using (var menuText = new SolidBrush(menuTextColor))
            using (var track = new SolidBrush(layered ? Color.FromArgb(170, trackColor) : trackColor))
            using (var accent = new SolidBrush(Ui.QuotaColor(remaining)))
            using (var centered = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                var leftText = string.Format("{0}剩余：{1:0.#}%", ShortWindowName(quota.WindowMinutes), remaining);
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var leftWidth = TextRenderer.MeasureText(leftText, normal, Size.Empty, flags).Width;
                var resetTextWidth = TextRenderer.MeasureText(reset, normal, Size.Empty, flags).Width;
                var leftX = 7 * scale;
                var progressX = leftX + leftWidth + 5 * scale;
                var resetX = ClientSize.Width - resetTextWidth - 5 * scale;
                var progressWidth = Math.Max(20 * scale, resetX - progressX - 4 * scale);
                graphics.DrawString(leftText, normal, menuText, new RectangleF(leftX, 0, leftWidth + 2 * scale, ClientSize.Height), centered);
                graphics.FillRectangle(track, progressX, progressY, progressWidth, progressHeight);
                graphics.FillRectangle(accent, progressX, progressY, (float)(progressWidth * remaining / 100d), progressHeight);
                graphics.DrawString(reset, normal, menuText, new RectangleF(resetX, 0, resetTextWidth + 2 * scale, ClientSize.Height), centered);
            }
        }

        private static string ShortWindowName(int minutes)
        {
            if (minutes < 60) return minutes + "分钟";
            if (minutes < 1440) return FormatDuration(minutes / 60d) + "小时";
            return FormatDuration(minutes / 1440d) + "天";
        }
        private static string FormatDuration(double value) { return Math.Abs(value - Math.Round(value)) < .001 ? Math.Round(value).ToString("0") : value.ToString("0.#"); }
    }

    internal sealed class UsageScanner
    {
        private const int ReadBufferSize = 64 * 1024;
        private const int MaxLineBytes = 4 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, FileState> states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DateTime, TokenTotals> daily = new Dictionary<DateTime, TokenTotals>();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        private readonly string codexRoot;

        public UsageScanner() : this(null) { }
        internal UsageScanner(string rootOverride)
        {
            codexRoot = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        public UsageSnapshot Scan()
        {
            lock (gate)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var discoveryComplete = true;
                foreach (var folder in new[] { Path.Combine(codexRoot, "sessions"), Path.Combine(codexRoot, "archived_sessions") })
                {
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var info = new FileInfo(file);
                                if (info.LastWriteTime < DateTime.Now.AddDays(-35)) continue;
                                seen.Add(file);
                                ProcessFile(file, info.Length);
                            }
                            catch (FileNotFoundException) { }
                            catch (DirectoryNotFoundException) { }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    catch (IOException) { discoveryComplete = false; }
                    catch (UnauthorizedAccessException) { discoveryComplete = false; }
                }
                if (discoveryComplete) foreach (var stalePath in states.Keys.Where(path => !seen.Contains(path)).ToList())
                {
                    RemoveContribution(states[stalePath]);
                    states.Remove(stalePath);
                }
                var oldest = DateTime.Now.Date.AddDays(-35);
                foreach (var date in daily.Keys.Where(date => date < oldest).ToList()) daily.Remove(date);
                return BuildSnapshot();
            }
        }

        private void ProcessFile(string path, long length)
        {
            FileState state;
            if (!states.TryGetValue(path, out state)) { state = new FileState(); states[path] = state; }
            if (length < state.Offset) { RemoveContribution(state); state = new FileState(); states[path] = state; }
            if (length == state.Offset) return;
            var buffer = new byte[ReadBufferSize];
            var completeOffset = state.Offset;
            using (var pending = new MemoryStream())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferSize, FileOptions.SequentialScan))
            {
                fs.Seek(state.Offset, SeekOrigin.Begin);
                var discardLine = false;
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var chunkOffset = fs.Position - read;
                    var segmentStart = 0;
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] != (byte)'\n') continue;
                        var segmentLength = i - segmentStart;
                        if (!discardLine)
                        {
                            if (pending.Length == 0)
                            {
                                if (segmentLength <= MaxLineBytes) ParseUtf8Line(buffer, segmentStart, segmentLength, state);
                            }
                            else if (pending.Length + segmentLength <= MaxLineBytes)
                            {
                                pending.Write(buffer, segmentStart, segmentLength);
                                ParseUtf8Line(pending.GetBuffer(), 0, (int)pending.Length, state);
                            }
                        }
                        completeOffset = chunkOffset + i + 1;
                        pending.SetLength(0);
                        discardLine = false;
                        segmentStart = i + 1;
                    }
                    var trailingLength = read - segmentStart;
                    if (trailingLength <= 0 || discardLine) continue;
                    if (pending.Length + trailingLength > MaxLineBytes)
                    {
                        pending.SetLength(0);
                        discardLine = true;
                    }
                    else pending.Write(buffer, segmentStart, trailingLength);
                }
            }
            state.Offset = completeOffset;
        }

        private void ParseUtf8Line(byte[] bytes, int offset, int count, FileState state)
        {
            if (count > 0 && bytes[offset + count - 1] == (byte)'\r') count--;
            if (count <= 0) return;
            var line = Encoding.UTF8.GetString(bytes, offset, count);
            if (line.Length == 0) return;
            if (line[0] == '\uFEFF') line = line.TrimStart('\uFEFF');
            if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) return;
            try { ParseLine(line, state); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private void ParseLine(string line, FileState state)
        {
            var root = json.DeserializeObject(line) as IDictionary<string, object>;
            if (root == null) return;
            var payload = Object(root, "payload");
            string eventType;
            if (payload == null || !String(payload, "type", out eventType) || !string.Equals(eventType, "token_count", StringComparison.Ordinal)) return;
            string timestamp;
            DateTimeOffset at;
            if (!String(root, "timestamp", out timestamp) || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) return;

            var info = Object(payload, "info");
            var usage = info == null ? null : Object(info, "total_token_usage");
            if (usage != null)
            {
                long input, output, cached, reasoning;
                if (Long(usage, "input_tokens", out input) && Long(usage, "output_tokens", out output))
                {
                    Long(usage, "cached_input_tokens", out cached);
                    Long(usage, "reasoning_output_tokens", out reasoning);
                    var current = new TokenTotals(input, output, cached, reasoning);
                    var delta = current.DeltaFrom(state.LastTotal); var date = at.LocalDateTime.Date;
                    TokenTotals total; if (!daily.TryGetValue(date, out total)) total = new TokenTotals(); daily[date] = total + delta;
                    TokenTotals own; if (!state.ByDay.TryGetValue(date, out own)) own = new TokenTotals(); state.ByDay[date] = own + delta;
                    state.LastTotal = current; state.LastActivity = at; state.HasUsage = true;
                }
            }
            var rateLimits = Object(payload, "rate_limits");
            if (rateLimits != null && at > (state.LatestQuota == null ? DateTimeOffset.MinValue : state.LatestQuota.At))
            {
                var windows = new List<QuotaWindow>(); AddQuota(rateLimits, "primary", windows); AddQuota(rateLimits, "secondary", windows);
                if (windows.Count > 0) state.LatestQuota = new QuotaSnapshot(at, windows);
            }
        }

        private static IDictionary<string, object> Object(IDictionary<string, object> source, string name)
        {
            object value;
            return source.TryGetValue(name, out value) ? value as IDictionary<string, object> : null;
        }
        private static bool String(IDictionary<string, object> source, string name, out string result)
        {
            object value;
            result = null;
            if (!source.TryGetValue(name, out value) || value == null) return false;
            result = value as string;
            return result != null;
        }
        private static bool Long(IDictionary<string, object> source, string name, out long result)
        {
            object value;
            result = 0;
            if (!source.TryGetValue(name, out value) || value == null) return false;
            try { result = Convert.ToInt64(value, CultureInfo.InvariantCulture); return result >= 0; }
            catch (Exception ex) { if (!(ex is FormatException) && !(ex is InvalidCastException) && !(ex is OverflowException)) throw; result = 0; return false; }
        }
        private static bool Double(IDictionary<string, object> source, string name, out double result)
        {
            object value;
            result = 0;
            if (!source.TryGetValue(name, out value) || value == null) return false;
            try { result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return !double.IsNaN(result) && !double.IsInfinity(result); }
            catch (Exception ex) { if (!(ex is FormatException) && !(ex is InvalidCastException) && !(ex is OverflowException)) throw; result = 0; return false; }
        }
        private static void AddQuota(IDictionary<string, object> rateLimits, string name, List<QuotaWindow> list)
        {
            var window = Object(rateLimits, name);
            if (window == null) return;
            long rawMinutes;
            double used;
            if (!Long(window, "window_minutes", out rawMinutes) || rawMinutes <= 0 || rawMinutes > 525600) return;
            if (!Double(window, "used_percent", out used)) return;
            used = Math.Max(0d, Math.Min(100d, used));
            long unix;
            DateTimeOffset? reset = Long(window, "resets_at", out unix) && unix > 0
                ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
            list.Add(new QuotaWindow((int)rawMinutes, used, reset));
        }
        private void RemoveContribution(FileState state)
        {
            foreach (var item in state.ByDay)
            {
                TokenTotals total;
                if (!daily.TryGetValue(item.Key, out total)) continue;
                var updated = total - item.Value;
                if (updated.Total <= 0 && updated.Cached <= 0 && updated.Reasoning <= 0) daily.Remove(item.Key);
                else daily[item.Key] = updated;
            }
        }
        private UsageSnapshot BuildSnapshot()
        {
            var today = DateTime.Now.Date;
            Func<int, TokenTotals> sum = days => daily.Where(x => x.Key >= today.AddDays(-(days - 1)) && x.Key <= today).Aggregate(new TokenTotals(), (a, x) => a + x.Value);
            var weekStart = DateTimeOffset.Now.AddDays(-7);
            var latestQuota = states.Values.Where(state => state.LatestQuota != null).Select(state => state.LatestQuota).OrderByDescending(item => item.At).FirstOrDefault();
            return new UsageSnapshot(sum(1), sum(7), sum(30), states.Values.Count(s => s.HasUsage && s.LastActivity >= weekStart), latestQuota == null ? DateTimeOffset.MinValue : latestQuota.At, latestQuota == null ? new List<QuotaWindow>() : latestQuota.Windows);
        }
    }

    internal sealed class FileState
    {
        public long Offset; public TokenTotals LastTotal = new TokenTotals(); public readonly Dictionary<DateTime, TokenTotals> ByDay = new Dictionary<DateTime, TokenTotals>(); public DateTimeOffset LastActivity; public bool HasUsage; public QuotaSnapshot LatestQuota;
    }
    internal sealed class TokenTotals
    {
        public long Input, Output, Cached, Reasoning; public long Total { get { return Input + Output; } }
        public TokenTotals(long input = 0, long output = 0, long cached = 0, long reasoning = 0) { Input = input; Output = output; Cached = cached; Reasoning = reasoning; }
        public TokenTotals DeltaFrom(TokenTotals p)
        {
            var reset = Input < p.Input || Output < p.Output || Cached < p.Cached || Reasoning < p.Reasoning;
            return reset ? new TokenTotals(Input, Output, Cached, Reasoning) : new TokenTotals(Input - p.Input, Output - p.Output, Cached - p.Cached, Reasoning - p.Reasoning);
        }
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Codex Local Quota Dashboard")]
[assembly: AssemblyDescription("Offline Windows dashboard for locally cached Codex quota and token usage.")]
[assembly: AssemblyProduct("Codex Local Quota Dashboard")]
[assembly: AssemblyCompany("yangyangha1")]
[assembly: AssemblyCopyright("Copyright © 2026 yangyangha1")]
[assembly: AssemblyVersion("1.6.2.0")]
[assembly: AssemblyFileVersion("1.6.2.0")]

namespace CodexLocalDashboard
{
    internal enum ThemeMode { Dark, Light }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        private static void Main()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DashboardForm());
        }
    }

    internal sealed class DashboardForm : Form
    {
        private const string DisplayVersion = "v1.6.2";
        private const int DesignWidth = 320;
        private const int DesignHeight = 347;
        private readonly UsageScanner scanner = new UsageScanner();
        private readonly HistoryStore historyStore = new HistoryStore();
        private readonly TokenRateChart tokenRateChart = new TokenRateChart();
        private readonly ProjectDetailChart projectDetailChart =
            new ProjectDetailChart();
        private readonly HistoryPanelChart historyPanelChart =
            new HistoryPanelChart();
        private readonly System.Windows.Forms.Timer countdownTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer followTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer renderThrottleTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolTip tips = new ToolTip();
        private readonly ContextMenuStrip contextMenu = new ContextMenuStrip();
        private readonly CancellationTokenSource refreshCancellation = new CancellationTokenSource();
        private readonly Panel canvas = new Panel();
        private readonly QuotaStripPanel stripPanel = new QuotaStripPanel();
        private readonly StripBackdropForm stripBackdrop = new StripBackdropForm();
        private readonly Form taskbarOwner = new Form();
        private readonly Dictionary<Control, LayoutSpec> layout = new Dictionary<Control, LayoutSpec>();
        private readonly Label quotaTitle = Ui.Label(string.Empty, 9, FontStyle.Bold, Color.FromArgb(142, 153, 169));
        private readonly QuotaHeadlineLabel quotaValue = new QuotaHeadlineLabel();
        private readonly Label quotaSub = Ui.Label("正在扫描本地日志", 8, FontStyle.Bold, Color.FromArgb(142, 153, 169));
        private readonly Label todayValue = Ui.Metric("—");
        private readonly Label weekValue = Ui.Metric("—");
        private readonly Label monthValue = Ui.Metric("—");
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
        private Rectangle sizingReferenceBounds = Rectangle.Empty;
        private IntPtr codexWindow;
        private IntPtr ownedCodexWindow;
        private UsageSnapshot latestSnapshot;
        private ToolStripMenuItem switchModeItem;
        private ToolStripMenuItem topmostMenuItem;
        private ToolStripMenuItem darkThemeItem;
        private ToolStripMenuItem lightThemeItem;
        private byte themeModeValue = (byte)ThemeMode.Dark;
        private Icon trayIcon;
        private Rectangle lastStripBounds = Rectangle.Empty;
        private Rectangle lastBackdropBounds = Rectangle.Empty;
        private int codexMissCount;
        private bool lastCodexForeground;
        private bool initialMemoryTrimDone;
        private byte backgroundTransparency = 10;
        private bool layeredRenderPending;
        private bool chartClickPending;
        private bool detailClickPending;
        private bool historyClickPending;
        private bool historySelectionPending;
        private bool detailMode;
        private bool historyMode;
        private CancellationTokenSource detailLoadCancellation;
        private CancellationTokenSource historyLoadCancellation;
        private ProjectDetailPointerHint lastDetailPointerHint;
        private HistoryPanelPointerHint lastHistoryPanelPointerHint;
        private bool lastHistoryButtonPointer;
        private ThemeMode CurrentTheme { get { return (ThemeMode)themeModeValue; } }
        private static readonly uint OwnProcessId = unchecked((uint)Process.GetCurrentProcess().Id);
        private static int detailMemoryCleanupPending;

        public DashboardForm()
            : this(null)
        {
        }

        internal DashboardForm(EventWaitHandle unusedActivateSignal)
        {
            Text = "Codex 本地用量";
            using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = Math.Max(1f, graphics.DpiX / 96f);
            ClientSize = DpiSize(360, 390);
            MinimumSize = DpiSize(256, 278);
            MaximumSize = DpiSize(576, 625);
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

            Add(quotaTitle, 14, 3, 132, 18);
            // Reserve enough horizontal room for the full remaining-quota
            // text at every supported dashboard scale.
            Add(quotaValue, 12, 3, 178, 38);
            Add(quotaBar, 14, 43, 292, 6);
            Add(quotaSub, 14, 51, 292, 18);
            AddSeparator(14, 72, 292);

            AddMetric("今日", todayValue, 14, 79);
            AddMetric("近 7 天", weekValue, 113, 79);
            AddMetric("近 30 天", monthValue, 212, 79);

            CaptureLayout();
            AttachDrag(canvas);
            canvas.MouseLeave += delegate
            {
                canvas.Cursor = Cursors.Default;
                lastDetailPointerHint = ProjectDetailPointerHint.None;
                lastHistoryButtonPointer = false;
                lastHistoryPanelPointerHint =
                    HistoryPanelPointerHint.None;
                tips.SetToolTip(canvas, null);
            };
            MouseWheel += HandleChartWheel;
            ConfigureTray();
            renderThrottleTimer.Interval = 33;
            renderThrottleTimer.Tick += delegate
            {
                renderThrottleTimer.Stop();
                if (!layeredRenderPending || IsDisposed) return;
                layeredRenderPending = false;
                RenderLayeredSurface();
            };
            ApplyTheme(CurrentTheme);
            SetDefaultPosition();
            ScaleCanvas();

            FormClosing += OnClosing;
            Resize += delegate { if (!stripMode) ScaleCanvas(); };
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

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= unchecked((int)WS_EX_TOOLWINDOW);
                value.ExStyle &= ~unchecked((int)WS_EX_APPWINDOW);
                return value;
            }
        }

        private void ApplyCornerPreference()
        {
            if (!IsHandleCreated) return;
            try
            {
                var cornerPreference = !stripMode &&
                    Environment.OSVersion.Version.Build >= 22000 ? 2 : 1;
                DwmSetWindowAttribute(Handle, 33, ref cornerPreference,
                    sizeof(int));
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

        private void AddMetric(string caption, Label value, int x, int top)
        {
            var label = Ui.Label(caption, 8, FontStyle.Bold, Color.FromArgb(126, 137, 153));
            Add(label, x, top, 94, 18);
            Add(value, x, top + 18, 94, 32);
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
            RequestLayeredRender();
        }

        private void RequestLayeredRender()
        {
            if (!IsHandleCreated || IsDisposed) return;
            layeredRenderPending = true;
            if (!renderThrottleTimer.Enabled) renderThrottleTimer.Start();
        }

        private void AttachDrag(Control parent)
        {
            if (!(parent is Button)) { parent.MouseDown += BeginDrag; parent.MouseMove += ContinueDrag; parent.MouseUp += EndDrag; }
            parent.MouseWheel += HandleChartWheel;
            foreach (Control child in parent.Controls) AttachDrag(child);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int WM_SIZING = 0x0214;
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;
            const int WM_DPICHANGED = 0x02E0;
            if (!stripMode && m.Msg == WM_ENTERSIZEMOVE)
            {
                sizingReferenceBounds = Bounds;
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                sizingReferenceBounds = Rectangle.Empty;
                base.WndProc(ref m);
                return;
            }
            if (!stripMode && m.Msg == WM_SIZING)
            {
                var proposed = (RECT)Marshal.PtrToStructure(
                    m.LParam, typeof(RECT));
                var constrained = ConstrainAspectRatio(
                    Rectangle.FromLTRB(proposed.Left, proposed.Top,
                        proposed.Right, proposed.Bottom),
                    sizingReferenceBounds.IsEmpty
                        ? Bounds : sizingReferenceBounds,
                    m.WParam.ToInt32(), MinimumSize, MaximumSize);
                proposed.Left = constrained.Left;
                proposed.Top = constrained.Top;
                proposed.Right = constrained.Right;
                proposed.Bottom = constrained.Bottom;
                Marshal.StructureToPtr(proposed, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
            }
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
                        MinimumSize = DpiSize(256, 278);
                        MaximumSize = DpiSize(576, 625);
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
                    var edge = (int)Math.Round(12 * dpiScale);
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

        internal static Rectangle ConstrainAspectRatio(Rectangle proposed,
            Rectangle current, int sizingEdge, Size minimum, Size maximum)
        {
            const int WmszLeft = 1;
            const int WmszRight = 2;
            const int WmszTop = 3;
            const int WmszTopLeft = 4;
            const int WmszTopRight = 5;
            const int WmszBottom = 6;
            const int WmszBottomLeft = 7;
            const int WmszBottomRight = 8;

            var widthDriven = sizingEdge == WmszLeft ||
                sizingEdge == WmszRight;
            if (!widthDriven && sizingEdge != WmszTop &&
                sizingEdge != WmszBottom)
            {
                var widthChange = Math.Abs(proposed.Width - current.Width) /
                    (double)Math.Max(1, current.Width);
                var heightChange = Math.Abs(proposed.Height - current.Height) /
                    (double)Math.Max(1, current.Height);
                widthDriven = widthChange >= heightChange;
            }

            var aspect = DesignWidth / (double)DesignHeight;
            var targetWidth = widthDriven
                ? proposed.Width
                : (int)Math.Round(proposed.Height * aspect,
                    MidpointRounding.AwayFromZero);
            targetWidth = Math.Max(minimum.Width,
                Math.Min(maximum.Width, targetWidth));
            var targetHeight = (int)Math.Round(targetWidth / aspect,
                MidpointRounding.AwayFromZero);
            if (targetHeight < minimum.Height)
            {
                targetHeight = minimum.Height;
                targetWidth = (int)Math.Round(targetHeight * aspect,
                    MidpointRounding.AwayFromZero);
            }
            if (targetHeight > maximum.Height)
            {
                targetHeight = maximum.Height;
                targetWidth = (int)Math.Round(targetHeight * aspect,
                    MidpointRounding.AwayFromZero);
            }

            var left = proposed.Left;
            var top = proposed.Top;
            var right = proposed.Right;
            var bottom = proposed.Bottom;
            switch (sizingEdge)
            {
                case WmszLeft:
                    left = right - targetWidth;
                    top = current.Top + (current.Height - targetHeight) / 2;
                    bottom = top + targetHeight;
                    break;
                case WmszRight:
                    right = left + targetWidth;
                    top = current.Top + (current.Height - targetHeight) / 2;
                    bottom = top + targetHeight;
                    break;
                case WmszTop:
                    top = bottom - targetHeight;
                    left = current.Left + (current.Width - targetWidth) / 2;
                    right = left + targetWidth;
                    break;
                case WmszBottom:
                    bottom = top + targetHeight;
                    left = current.Left + (current.Width - targetWidth) / 2;
                    right = left + targetWidth;
                    break;
                case WmszTopLeft:
                    left = right - targetWidth;
                    top = bottom - targetHeight;
                    break;
                case WmszTopRight:
                    right = left + targetWidth;
                    top = bottom - targetHeight;
                    break;
                case WmszBottomLeft:
                    left = right - targetWidth;
                    bottom = top + targetHeight;
                    break;
                case WmszBottomRight:
                default:
                    right = left + targetWidth;
                    bottom = top + targetHeight;
                    break;
            }
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private async void RefreshData()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var snapshot = await Task.Run(
                    () =>
                    {
                        var value = scanner.Scan(refreshCancellation.Token);
                        try
                        {
                            historyStore.Record(value, DateTimeOffset.Now);
                        }
                        catch
                        {
                            // History I/O must never degrade the live dashboard.
                        }
                        return value;
                    }, refreshCancellation.Token);
                if (refreshCancellation.IsCancellationRequested || IsDisposed) return;
                ApplySnapshot(snapshot);
                secondsRemaining = TokenRateChart.CaptureIntervalSeconds;
                TrimInitialWorkingSet();
            }
            catch (OperationCanceledException)
            {
                secondsRemaining = TokenRateChart.CaptureIntervalSeconds;
            }
            catch (Exception)
            {
                if (!IsDisposed)
                {
                    tokenRateChart.CaptureFailure(DateTimeOffset.Now);
                    quotaSub.Text = "部分日志暂时无法读取";
                    tips.SetToolTip(quotaTitle, string.Empty);
                    RenderLayeredSurface();
                }
                secondsRemaining = TokenRateChart.CaptureIntervalSeconds;
            }
            finally { refreshing = false; }
        }

        private void TrimInitialWorkingSet()
        {
            // Only trim after the first full scan. Repeating EmptyWorkingSet every
            // 30 seconds reduces the displayed working set but creates page faults.
            if (initialMemoryTrimDone) return;
            initialMemoryTrimDone = true;
            GC.Collect(2, GCCollectionMode.Optimized, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, true);
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
            var weeklyQuota = s.WeeklyQuota;
            var fiveHourQuota = s.FiveHourQuota;
            var chartCaptureAt = DateTimeOffset.Now;
            tokenRateChart.Capture(
                chartCaptureAt,
                s.Today.Total,
                weeklyQuota == null ? (double?)null :
                    100d - weeklyQuota.UsedPercent,
                weeklyQuota == null ? 0 : weeklyQuota.WindowMinutes,
                weeklyQuota == null ? (DateTimeOffset?)null :
                    weeklyQuota.ResetsAt,
                fiveHourQuota == null ? (double?)null :
                    100d - fiveHourQuota.UsedPercent,
                fiveHourQuota == null ? 0 :
                    fiveHourQuota.WindowMinutes,
                fiveHourQuota == null ? (DateTimeOffset?)null :
                    fiveHourQuota.ResetsAt);
            if (weeklyQuota == null && fiveHourQuota == null)
            {
                quotaTitle.Text = string.Empty;
                quotaValue.SetQuotaValues(null, null);
                quotaSub.Text = "等待 Codex 写入限额信息";
                quotaBar.SetQuotaValues(null, null);
                tips.SetToolTip(quotaTitle, string.Empty);
                RenderLayeredSurface();
                return;
            }
            quotaTitle.Text = string.Empty;
            var weeklyRemaining = weeklyQuota == null ? (double?)null :
                Math.Max(0d, 100d - weeklyQuota.UsedPercent);
            var fiveHourRemaining = fiveHourQuota == null ? (double?)null :
                Math.Max(0d, 100d - fiveHourQuota.UsedPercent);
            quotaValue.SetQuotaValues(fiveHourRemaining, weeklyRemaining);
            quotaBar.SetQuotaValues(weeklyRemaining, fiveHourRemaining);
            quotaSub.Text = "5H重置 " + FormatQuotaReset(fiveHourQuota) +
                " · 周重置 " + FormatQuotaReset(weeklyQuota);
            tips.SetToolTip(quotaTitle, string.Join("\n", s.Quotas.OrderBy(x => x.WindowMinutes).Select(x => Ui.WindowName(x.WindowMinutes) + "：已用 " + x.UsedPercent.ToString("0.#") + "%")));
            RenderLayeredSurface();
        }

        private static string FormatQuotaReset(QuotaWindow quota)
        {
            return quota != null && quota.ResetsAt.HasValue
                ? quota.ResetsAt.Value.ToLocalTime().ToString("M月d日 HH:mm")
                : "—";
        }

        private void ConfigureTray()
        {
            // Windows only exposes short text for a tray icon.  Keeping the
            // version in its hover label makes the lower-right icon identify
            // the currently running build without changing the app artwork.
            tray.Text = "Codex 本地用量 " + DisplayVersion;
            trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = trayIcon ?? SystemIcons.Application;
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (stripMode) ExitStripMode(); else ShowDashboard();
            };
            var menu = contextMenu;
            menu.Items.Add(new ToolStripMenuItem("版本 " + DisplayVersion)
            {
                Enabled = false
            });
            menu.Items.Add(new ToolStripSeparator());
            switchModeItem = new ToolStripMenuItem("切换为 Codex 顶部横条");
            switchModeItem.Click += delegate { ToggleDisplayMode(); };
            menu.Items.Add(switchModeItem);
            darkThemeItem = new ToolStripMenuItem("深色");
            lightThemeItem = new ToolStripMenuItem("浅色");
            darkThemeItem.Click += delegate { ApplyTheme(ThemeMode.Dark); };
            lightThemeItem.Click += delegate { ApplyTheme(ThemeMode.Light); };
            menu.Items.Add(darkThemeItem);
            menu.Items.Add(lightThemeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("调整背景透明度…", null, delegate { ShowTransparencyDialog(); });
            topmostMenuItem = new ToolStripMenuItem("窗口置顶") { Checked = TopMost, CheckOnClick = true };
            topmostMenuItem.CheckedChanged += delegate { if (!stripMode) TopMost = topmostMenuItem.Checked; };
            menu.Items.Add(topmostMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("隐藏", null, delegate { Hide(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
            stripBackdrop.ContextMenuStrip = menu;
            ApplyContextMenu(canvas, menu);
            ApplyContextMenu(stripPanel, menu);
        }

        internal void ApplyTheme(ThemeMode mode)
        {
            themeModeValue = (byte)mode;
            var light = mode == ThemeMode.Light;
            var dashboardBackground = light ? Color.FromArgb(236, 245, 250) : Color.FromArgb(26, 34, 37);
            var stripBackground = light ? Color.FromArgb(244, 244, 242) : Color.FromArgb(20, 20, 20);
            var activeColorKey = stripBackground;
            var primary = light ? Color.Black : Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) : Color.FromArgb(142, 153, 169);
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
            RenderLayeredSurface();
        }

        private int BackgroundAlpha
        {
            get
            {
                if (backgroundTransparency >= 100) return 1;
                return Math.Max(1, Math.Min(255, (int)Math.Round(255d * (100 - backgroundTransparency) / 100d)));
            }
        }
        private void ShowTransparencyDialog()
        {
            var original = backgroundTransparency;
            using (var dialog = new TransparencyDialog(backgroundTransparency, dpiScale, delegate(int value)
            {
                backgroundTransparency = (byte)Math.Max(0, Math.Min(100, value));
                RequestLayeredRender();
            }))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    backgroundTransparency = (byte)dialog.TransparencyValue;
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
            stripMode = true;
            ApplyCornerPreference();
            TopMost = false;
            topmostMenuItem.Enabled = false;
            MinimumSize = new Size(1, 1);
            MaximumSize = Size.Empty;
            canvas.Visible = false;
            stripPanel.Visible = true;
            stripPanel.Snapshot = latestSnapshot;
            stripPanel.Theme = CurrentTheme;
            ApplyTheme(CurrentTheme);
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
            ApplyTheme(CurrentTheme);
            ShowInTaskbar = false;
            MinimumSize = DpiSize(256, 278);
            MaximumSize = DpiSize(576, 625);
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
            if (codexWindow == IntPtr.Zero || !IsWindow(codexWindow))
                codexWindow = FindCodexWindow(codexMissCount == 0 || codexMissCount % 5 == 0);
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
            renderThrottleTimer.Stop();
            layeredRenderPending = false;
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
                var darkBackground = CurrentTheme == ThemeMode.Dark;
                var layeredBackground = stripMode
                    ? (darkBackground ? Color.FromArgb(20, 20, 20) : Color.FromArgb(244, 244, 242))
                    : (darkBackground ? Color.FromArgb(26, 34, 37) : Color.FromArgb(236, 245, 250));
                using (var backgroundLayer = new SolidBrush(
                    Color.FromArgb(BackgroundAlpha, layeredBackground)))
                {
                    if (stripMode)
                        graphics.FillRectangle(backgroundLayer, 0, 0,
                            bitmap.Width, bitmap.Height);
                    else
                        FillHighQualityRoundedBackground(graphics,
                            new RectangleF(0, 0, bitmap.Width, bitmap.Height),
                            Math.Max(6f, 9f * dpiScale), backgroundLayer);
                }
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
                LayoutSpec original;
                if ((detailMode || historyMode) &&
                    layout.TryGetValue(control, out original) &&
                    original.Bounds.Top >= 79)
                    continue;
                var bounds = new Rectangle(canvas.Left + control.Left, canvas.Top + control.Top, control.Width, control.Height);
                var headline = control as QuotaHeadlineLabel;
                if (headline != null)
                {
                    headline.DrawLayered(graphics, bounds);
                    continue;
                }
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
                    progress.DrawLayered(graphics, bounds);
                    continue;
                }
                if (control is Panel)
                {
                    using (var divider = new SolidBrush(Color.FromArgb(110, control.BackColor))) graphics.FillRectangle(divider, bounds);
                }
            }

            var detailBounds = DetailButtonBounds();
            var historyBounds = HistoryButtonBounds();
            var light = CurrentTheme == ThemeMode.Light;
            var chartScale = canvas.Width / (float)DesignWidth;
            using (var border = new Pen(light
                ? Color.FromArgb(88, 130, 162)
                : Color.FromArgb(104, 162, 201)))
            using (var textBrush = new SolidBrush(light
                ? Color.FromArgb(48, 91, 121)
                : Color.FromArgb(176, 213, 235)))
            using (var activeBrush = new SolidBrush(light
                ? Color.FromArgb(205, 228, 240)
                : Color.FromArgb(48, 72, 84)))
            using (var font = new Font(Ui.FontFamilyName,
                Math.Max(6f, 7.2f * lastScale), FontStyle.Bold))
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                var buttonRadius = Math.Max(2f, 3f * chartScale);
                if (historyMode)
                    Ui.FillRoundedRectangle(graphics, activeBrush,
                        historyBounds, buttonRadius);
                Ui.DrawRoundedRectangle(graphics, border,
                    new RectangleF(historyBounds.X, historyBounds.Y,
                        Math.Max(1f, historyBounds.Width - 1),
                        Math.Max(1f, historyBounds.Height - 1)),
                    buttonRadius);
                graphics.DrawString("历史", font, textBrush,
                    historyBounds, format);
                if (detailMode)
                    Ui.FillRoundedRectangle(graphics, activeBrush,
                        detailBounds, buttonRadius);
                Ui.DrawRoundedRectangle(graphics, border,
                    new RectangleF(detailBounds.X, detailBounds.Y,
                        Math.Max(1f, detailBounds.Width - 1),
                        Math.Max(1f, detailBounds.Height - 1)),
                    buttonRadius);
                graphics.DrawString("明细", font, textBrush,
                    detailBounds, format);
            }

            // Real-time, History and Detail share the same lower chart edge.
            // Background capture and five-minute history writes continue while
            // either embedded view is visible.
            var chartVisualScale = Math.Max(.75f, chartScale / Math.Max(1f, dpiScale));
            if (detailMode)
            {
                var detailViewBounds = new RectangleF(
                    canvas.Left + 14f * chartScale,
                    canvas.Top + 79f * chartScale,
                    292f * chartScale,
                    260f * chartScale);
                projectDetailChart.Draw(graphics, detailViewBounds,
                    CurrentTheme,
                    chartVisualScale);
            }
            else if (historyMode)
            {
                var historyViewBounds = new RectangleF(
                    canvas.Left + 14f * chartScale,
                    canvas.Top + 84f * chartScale,
                    292f * chartScale,
                    255f * chartScale);
                historyPanelChart.Draw(graphics, historyViewBounds,
                    CurrentTheme, chartVisualScale);
            }
            else
            {
                var chartBounds = new RectangleF(
                    canvas.Left + 14f * chartScale,
                    canvas.Top + 133f * chartScale,
                    292f * chartScale,
                    206f * chartScale);
                tokenRateChart.Draw(graphics, chartBounds, CurrentTheme,
                    DateTimeOffset.Now, chartVisualScale, true);
            }
        }

        private static void FillHighQualityRoundedBackground(Graphics graphics,
            RectangleF bounds, float radius, Brush brush)
        {
            var inset = 0.5f;
            var rect = RectangleF.FromLTRB(bounds.Left + inset,
                bounds.Top + inset, bounds.Right - inset,
                bounds.Bottom - inset);
            var diameter = Math.Min(radius * 2f,
                Math.Min(rect.Width, rect.Height));
            if (diameter < 2f)
            {
                graphics.FillRectangle(brush, rect);
                return;
            }

            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.StartFigure();
                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Top, diameter,
                    diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter,
                    diameter, diameter, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - diameter, diameter,
                    diameter, 90, 90);
                path.CloseFigure();
                graphics.FillPath(brush, path);
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
                if (data.Stride == rowBytes)
                    CopyMemory(bits, data.Scan0,
                        new UIntPtr((uint)(rowBytes * bitmap.Height)));
                else
                    for (var y = 0; y < bitmap.Height; y++)
                        CopyMemory(IntPtr.Add(bits, y * rowBytes),
                            IntPtr.Add(data.Scan0, y * data.Stride),
                            new UIntPtr((uint)rowBytes));
            }
            finally { bitmap.UnlockBits(data); }
            var previous = SelectObject(memoryDc, bitmapHandle);
            try
            {
                var destination = Location;
                var size = bitmap.Size;
                var source = Point.Empty;
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref destination, ref size,
                    memoryDc, ref source, 0, ref blend, 2);
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

        private static IntPtr FindCodexWindow(bool includeBroadScan)
        {
            var candidates = new List<Process>();
            var seen = new HashSet<int>();
            foreach (var name in new[] { "Codex", "ChatGPT" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (seen.Add(process.Id)) candidates.Add(process);
                    else process.Dispose();
                }
            }
            if (includeBroadScan)
            {
                foreach (var process in Process.GetProcesses())
                {
                    if (unchecked((uint)process.Id) != OwnProcessId && seen.Add(process.Id)) candidates.Add(process);
                    else process.Dispose();
                }
            }

            var bestWindow = IntPtr.Zero;
            var bestScore = 0;
            foreach (var process in candidates)
            {
                using (process)
                {
                    try
                    {
                        var window = process.MainWindowHandle;
                        if (window == IntPtr.Zero || !IsWindowVisible(window)) continue;
                        var score = ScoreCodexProcess(process);
                        if (score <= bestScore) continue;
                        bestScore = score;
                        bestWindow = window;
                    }
                    catch { }
                }
            }
            return bestScore >= 80 ? bestWindow : IntPtr.Zero;
        }

        private static int ScoreCodexProcess(Process process)
        {
            var score = 0;
            var name = process.ProcessName ?? string.Empty;
            if (name.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0) score += 80;

            string path = null;
            try { path = process.MainModule.FileName; } catch { }
            if (!string.IsNullOrEmpty(path) && path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;

            try
            {
                var version = process.MainModule.FileVersionInfo;
                var identity = (version.ProductName ?? string.Empty) + " " + (version.FileDescription ?? string.Empty);
                if (identity.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0) score += 80;
            }
            catch { }

            var title = process.MainWindowTitle ?? string.Empty;
            if (title.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0) score += 60;
            return score;
        }

        private bool IsChartPoint(Point point)
        {
            if (stripMode || canvas.Width <= 0) return false;
            var scale = canvas.Width / (float)DesignWidth;
            if (detailMode)
                return new RectangleF(14f * scale, 79f * scale,
                    292f * scale, 260f * scale).Contains(point);
            if (historyMode)
                return new RectangleF(14f * scale, 84f * scale,
                    292f * scale, 255f * scale).Contains(point);
            return new RectangleF(14f * scale, 133f * scale,
                292f * scale, 206f * scale).Contains(point);
        }

        private void SetDetailMode(bool value)
        {
            if (detailMode == value) return;
            if (value && historyMode) SetHistoryMode(false);
            detailMode = value;
            if (value)
                BeginLoadProjectDetails();
            else
            {
                var releaseAfterCancellation = CancelDetailLoad();
                projectDetailChart.Clear();
                lastDetailPointerHint = ProjectDetailPointerHint.None;
                canvas.Cursor = Cursors.Default;
                tips.SetToolTip(canvas, null);
                if (!releaseAfterCancellation)
                    ReleaseDetailMemoryInBackground();
            }
            foreach (Control control in canvas.Controls)
            {
                LayoutSpec original;
                if (layout.TryGetValue(control, out original) &&
                    original.Bounds.Top >= 79)
                    control.Visible = !(detailMode || historyMode);
            }
        }

        private void SetHistoryMode(bool value)
        {
            if (historyMode == value) return;
            if (value && detailMode) SetDetailMode(false);
            historyMode = value;
            if (value)
            {
                historyPanelChart.SetDate(DateTime.Today);
                BeginLoadHistory();
            }
            else
            {
                CancelHistoryLoad();
                historyPanelChart.Clear();
                historySelectionPending = false;
                lastHistoryPanelPointerHint =
                    HistoryPanelPointerHint.None;
                canvas.Cursor = Cursors.Default;
                tips.SetToolTip(canvas, null);
            }
            foreach (Control control in canvas.Controls)
            {
                LayoutSpec original;
                if (layout.TryGetValue(control, out original) &&
                    original.Bounds.Top >= 79)
                    control.Visible = !(detailMode || historyMode);
            }
        }

        private async void BeginLoadHistory()
        {
            CancelHistoryLoad();
            if (!historyMode) return;
            var cancellation = new CancellationTokenSource();
            historyLoadCancellation = cancellation;
            historyPanelChart.SetLoading(true);
            RenderLayeredSurface();
            var selectedDate = historyPanelChart.SelectedDate;
            var visibleWeek = historyPanelChart.VisibleWeekStart;
            var chartFrom = historyPanelChart.RequiredReadFrom;
            var chartTo = historyPanelChart.RequiredReadTo;
            var statusFrom = historyPanelChart.StatusReadFrom;
            var statusTo = historyPanelChart.StatusReadTo;
            var from = chartFrom < statusFrom ? chartFrom : statusFrom;
            var to = chartTo > statusTo ? chartTo : statusTo;
            try
            {
                var samples = await Task.Run(() => historyStore.ReadRange(
                    from, to, cancellation.Token), cancellation.Token);
                if (cancellation.IsCancellationRequested || IsDisposed ||
                    !historyMode ||
                    !ReferenceEquals(historyLoadCancellation, cancellation) ||
                    historyPanelChart.SelectedDate != selectedDate ||
                    historyPanelChart.VisibleWeekStart != visibleWeek)
                    return;
                historyPanelChart.SetAvailableDates(samples.Select(value =>
                    value.At.ToLocalTime().Date));
                historyPanelChart.SetSamples(samples.Where(value =>
                    value.At >= chartFrom.ToUniversalTime() &&
                    value.At < chartTo.ToUniversalTime()).ToList(),
                    historyStore.FileSize);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!cancellation.IsCancellationRequested && !IsDisposed &&
                    historyMode &&
                    ReferenceEquals(historyLoadCancellation, cancellation))
                    historyPanelChart.SetLoadError();
            }
            finally
            {
                if (ReferenceEquals(historyLoadCancellation, cancellation))
                {
                    historyLoadCancellation = null;
                    if (!IsDisposed && historyMode)
                    {
                        historyPanelChart.SetLoading(false);
                        RenderLayeredSurface();
                    }
                }
                cancellation.Dispose();
            }
        }

        private void LoadHistoryDate(DateTime value)
        {
            historyPanelChart.SetDate(value);
            BeginLoadHistory();
        }

        private void CancelHistoryLoad()
        {
            var cancellation = historyLoadCancellation;
            historyLoadCancellation = null;
            if (cancellation == null) return;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async void BeginLoadProjectDetails()
        {
            CancelDetailLoad();
            var cancellation = new CancellationTokenSource();
            detailLoadCancellation = cancellation;
            projectDetailChart.SetLoading(true);
            RenderLayeredSurface();
            try
            {
                var snapshot = await Task.Run(() =>
                {
                    using (var detailScanner =
                        new UsageScanner(null, true))
                        return detailScanner.Scan(cancellation.Token);
                }, cancellation.Token);
                if (cancellation.IsCancellationRequested || IsDisposed ||
                    !detailMode ||
                    !ReferenceEquals(detailLoadCancellation, cancellation))
                    return;
                projectDetailChart.SetProjects(snapshot.Projects);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!cancellation.IsCancellationRequested && !IsDisposed &&
                    detailMode &&
                    ReferenceEquals(detailLoadCancellation, cancellation))
                    projectDetailChart.SetLoadError();
            }
            finally
            {
                if (ReferenceEquals(detailLoadCancellation, cancellation))
                {
                    detailLoadCancellation = null;
                    if (!IsDisposed && detailMode)
                    {
                        projectDetailChart.SetLoading(false);
                        RenderLayeredSurface();
                    }
                }
                if (cancellation.IsCancellationRequested && !detailMode)
                    ReleaseDetailMemoryInBackground();
                cancellation.Dispose();
            }
        }

        private bool CancelDetailLoad()
        {
            var cancellation = detailLoadCancellation;
            detailLoadCancellation = null;
            if (cancellation == null) return false;
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            return true;
        }

        private static void ReleaseDetailMemoryInBackground()
        {
            if (Interlocked.Exchange(ref detailMemoryCleanupPending, 1) != 0)
                return;
            Task.Run(delegate
            {
                try
                {
                    GC.Collect(2, GCCollectionMode.Optimized, false);
                    using (var process = Process.GetCurrentProcess())
                        EmptyWorkingSet(process.Handle);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref detailMemoryCleanupPending, 0);
                }
            });
        }

        private Rectangle DetailButtonBounds()
        {
            var scale = canvas.Width / (float)DesignWidth;
            return Rectangle.Round(new RectangleF(
                canvas.Left + 249f * scale,
                canvas.Top + 13f * scale,
                57f * scale,
                18f * scale));
        }

        private bool IsDetailPoint(Point point)
        {
            if (stripMode || canvas.Width <= 0) return false;
            var scale = canvas.Width / (float)DesignWidth;
            return new RectangleF(249f * scale, 13f * scale,
                57f * scale, 18f * scale).Contains(point);
        }

        private Rectangle HistoryButtonBounds()
        {
            var scale = canvas.Width / (float)DesignWidth;
            return Rectangle.Round(new RectangleF(
                canvas.Left + 194f * scale,
                canvas.Top + 13f * scale,
                50f * scale,
                18f * scale));
        }

        private bool IsHistoryPoint(Point point)
        {
            if (stripMode || canvas.Width <= 0) return false;
            var scale = canvas.Width / (float)DesignWidth;
            return new RectangleF(194f * scale, 13f * scale,
                50f * scale, 18f * scale).Contains(point);
        }

        private void HandleHistoryAction(HistoryPanelClickResult result)
        {
            switch (result)
            {
                case HistoryPanelClickResult.Close:
                    SetHistoryMode(false);
                    break;
                case HistoryPanelClickResult.PreviousWeek:
                    if (historyPanelChart.ShiftWeek(-1)) BeginLoadHistory();
                    break;
                case HistoryPanelClickResult.NextWeek:
                    if (historyPanelChart.ShiftWeek(1)) BeginLoadHistory();
                    break;
                case HistoryPanelClickResult.SelectDate:
                    LoadHistoryDate(historyPanelChart.ClickedDate);
                    break;
                case HistoryPanelClickResult.OpenStorage:
                    OpenHistoryStorage();
                    break;
            }
        }

        private static string HistoryPointerText(
            HistoryPanelPointerHint hint)
        {
            switch (hint)
            {
                case HistoryPanelPointerHint.Close:
                    return "关闭历史数据";
                case HistoryPanelPointerHint.PreviousWeek:
                    return "显示前 7 天数据状态";
                case HistoryPanelPointerHint.SelectDate:
                    return "显示当天历史数据";
                case HistoryPanelPointerHint.NextWeek:
                    return "显示后 7 天数据状态";
                case HistoryPanelPointerHint.OpenStorage:
                    return "打开历史数据保存位置";
                case HistoryPanelPointerHint.SelectRange:
                    return "左键框选、鼠标滚轮放大";
                default:
                    return null;
            }
        }


        private void OpenHistoryStorage()
        {
            try
            {
                var folder = historyStore.StorageDirectory;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var arguments = File.Exists(historyStore.StoragePath)
                    ? "/select,\"" + historyStore.StoragePath + "\""
                    : "\"" + folder + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments)
                { UseShellExecute = true });
            }
            catch { }
        }

        private void HandleChartWheel(object sender, MouseEventArgs e)
        {
            if (stripMode) return;
            var source = sender as Control;
            if (source == null) return;
            var point = canvas.PointToClient(
                source.PointToScreen(e.Location));
            if (!IsChartPoint(point)) return;
            if (historyMode)
            {
                var absolute = new PointF(canvas.Left + point.X,
                    canvas.Top + point.Y);
                if (!historyPanelChart.ChartBounds.Contains(absolute)) return;
                var previousHours = historyPanelChart.DisplayHours;
                if (historyPanelChart.ZoomByWheel(e.Delta))
                {
                    if (previousHours != 48 &&
                        historyPanelChart.DisplayHours == 48)
                        BeginLoadHistory();
                    RenderLayeredSurface();
                }
                return;
            }
            if (detailMode)
            {
                if (projectDetailChart.Scroll(e.Delta))
                    RenderLayeredSurface();
                return;
            }
            if (tokenRateChart.ZoomByWheel(e.Delta))
                RenderLayeredSurface();
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (ReferenceEquals(sender, canvas) &&
                IsHistoryPoint(e.Location))
            {
                historyClickPending = true;
                detailClickPending = false;
                chartClickPending = false;
                dragging = false;
                canvas.Capture = true;
                return;
            }
            if (ReferenceEquals(sender, canvas) && IsDetailPoint(e.Location))
            {
                detailClickPending = true;
                historyClickPending = false;
                chartClickPending = false;
                dragging = false;
                canvas.Capture = true;
                return;
            }
            if (ReferenceEquals(sender, canvas) && IsChartPoint(e.Location))
            {
                if (detailMode || historyMode)
                {
                    chartClickPending = true;
                    historySelectionPending = historyMode &&
                        historyPanelChart.BeginSelection(new PointF(
                            canvas.Left + e.X, canvas.Top + e.Y));
                    dragging = false;
                    canvas.Capture = true;
                }
                else
                {
                    chartClickPending = false;
                    dragging = true;
                    dragOrigin = Cursor.Position;
                }
                return;
            }
            detailClickPending = false;
            historyClickPending = false;
            chartClickPending = false;
            dragging = true;
            dragOrigin = Cursor.Position;
        }

        private void ContinueDrag(object sender, MouseEventArgs e)
        {
            if (chartClickPending && historyMode &&
                historySelectionPending)
            {
                var sourceControl = sender as Control;
                if (sourceControl != null)
                {
                    var localPoint = canvas.PointToClient(
                        sourceControl.PointToScreen(e.Location));
                    if (historyPanelChart.UpdateSelection(new PointF(
                        canvas.Left + localPoint.X,
                        canvas.Top + localPoint.Y)))
                        RenderLayeredSurface();
                }
                return;
            }
            if (!dragging)
            {
                var source = sender as Control;
                if (source != null)
                {
                    var local = canvas.PointToClient(
                        source.PointToScreen(e.Location));
                    var historyButtonHint = IsHistoryPoint(local);
                    var hint = historyButtonHint
                        ? ProjectDetailPointerHint.DetailButton
                        : IsDetailPoint(local)
                        ? ProjectDetailPointerHint.DetailButton
                        : detailMode
                            ? projectDetailChart.PointerHint(
                                new PointF(canvas.Left + local.X,
                                    canvas.Top + local.Y))
                            : ProjectDetailPointerHint.None;
                    var panelHint = historyMode
                        ? historyPanelChart.PointerHint(new PointF(
                            canvas.Left + local.X,
                            canvas.Top + local.Y))
                        : HistoryPanelPointerHint.None;
                    if (hint != lastDetailPointerHint ||
                        historyButtonHint != lastHistoryButtonPointer ||
                        panelHint != lastHistoryPanelPointerHint)
                    {
                        lastDetailPointerHint = hint;
                        lastHistoryButtonPointer = historyButtonHint;
                        lastHistoryPanelPointerHint = panelHint;
                        canvas.Cursor = panelHint ==
                            HistoryPanelPointerHint.SelectRange
                            ? Cursors.Cross
                            : panelHint != HistoryPanelPointerHint.None ||
                                hint != ProjectDetailPointerHint.None
                                ? Cursors.Hand : Cursors.Default;
                        tips.SetToolTip(canvas, panelHint !=
                            HistoryPanelPointerHint.None
                            ? HistoryPointerText(panelHint)
                            : historyButtonHint
                                ? (historyMode
                                    ? "关闭历史数据"
                                    : "查看历史数据") :
                            hint == ProjectDetailPointerHint.Close
                                ? "关闭用量明细"
                                : hint ==
                                    ProjectDetailPointerHint.OpenProjectLocation
                                    ? "打开项目位置"
                                    : hint ==
                                        ProjectDetailPointerHint.OpenSessionLocation
                                        ? "打开 session 文件位置"
                                        : hint ==
                                            ProjectDetailPointerHint.DetailButton
                                        ? (detailMode
                                            ? "关闭用量明细"
                                            : "查看用量明细")
                                        : hint ==
                                            ProjectDetailPointerHint.ShowAllButton
                                        ? "切换明细范围"
                                        : null);
                    }
                }
            }
            if (!dragging) return;
            var p = Cursor.Position;
            Location = new Point(Location.X + p.X - dragOrigin.X,
                Location.Y + p.Y - dragOrigin.Y);
            dragOrigin = p;
        }

        private void EndDrag(object sender, MouseEventArgs e)
        {
            if (historyClickPending)
            {
                var showHistory = ReferenceEquals(sender, canvas) &&
                    IsHistoryPoint(e.Location);
                historyClickPending = false;
                dragging = false;
                canvas.Capture = false;
                if (showHistory)
                {
                    SetHistoryMode(!historyMode);
                    RenderLayeredSurface();
                }
                return;
            }
            if (detailClickPending)
            {
                var showDetail = ReferenceEquals(sender, canvas) &&
                    IsDetailPoint(e.Location);
                detailClickPending = false;
                dragging = false;
                canvas.Capture = false;
                if (showDetail)
                {
                    SetDetailMode(!detailMode);
                    RenderLayeredSurface();
                }
                return;
            }
            if (chartClickPending)
            {
                var switchMode = ReferenceEquals(sender, canvas) &&
                    IsChartPoint(e.Location);
                chartClickPending = false;
                dragging = false;
                canvas.Capture = false;
                var historyAbsolute = new PointF(canvas.Left + e.X,
                    canvas.Top + e.Y);
                if (historyMode && historySelectionPending)
                {
                    historyPanelChart.EndSelection(historyAbsolute);
                    RenderLayeredSurface();
                }
                if (switchMode)
                {
                    if (historyMode)
                    {
                        if (!historySelectionPending)
                            HandleHistoryAction(
                                historyPanelChart.HandleClick(
                                    historyAbsolute));
                        RenderLayeredSurface();
                    }
                    else if (detailMode)
                    {
                        var result = projectDetailChart.HandleClick(
                            new PointF(canvas.Left + e.X,
                                canvas.Top + e.Y));
                        if (result == ProjectDetailClickResult.Close)
                            SetDetailMode(false);
                        RenderLayeredSurface();
                    }
                }
                historySelectionPending = false;
                return;
            }
            dragging = false;
        }

        private void SetDefaultPosition()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 24, area.Top + 42);
        }

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
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                stripBackdrop.Hide();
                return;
            }
            refreshCancellation.Cancel();
            CancelDetailLoad();
            CancelHistoryLoad();
            projectDetailChart.Clear();
            historyPanelChart.Clear();
            scanner.Dispose();
            historyStore.Dispose();
            countdownTimer.Stop();
            followTimer.Stop();
            renderThrottleTimer.Stop();
            layeredRenderPending = false;
            tray.Visible = false;
            tray.Dispose();
            tips.Dispose();
            contextMenu.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            refreshCancellation.Dispose();
            countdownTimer.Dispose();
            followTimer.Dispose();
            renderThrottleTimer.Dispose();
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
        public static void DrawEmbeddedClose(Graphics graphics,
            RectangleF bounds, Color color, float scale)
        {
            using (var pen = new Pen(color, Math.Max(.8f, scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                var x = bounds.Left + bounds.Width / 2f;
                var y = bounds.Top + bounds.Height / 2f;
                var radius = 4f * scale;
                graphics.DrawLine(pen, x - radius, y - radius,
                    x + radius, y + radius);
                graphics.DrawLine(pen, x + radius, y - radius,
                    x - radius, y + radius);
            }
        }
        public static void FillRoundedRectangle(Graphics graphics,
            Brush brush, RectangleF bounds, float radius)
        {
            using (var path = CreateRoundedPath(bounds, radius))
                graphics.FillPath(brush, path);
        }
        public static void DrawRoundedRectangle(Graphics graphics, Pen pen,
            RectangleF bounds, float radius)
        {
            using (var path = CreateRoundedPath(bounds, radius))
                graphics.DrawPath(pen, path);
        }
        private static System.Drawing.Drawing2D.GraphicsPath CreateRoundedPath(
            RectangleF bounds, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var diameter = Math.Min(Math.Max(0f, radius * 2f),
                Math.Min(bounds.Width, bounds.Height));
            if (diameter < 2f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.StartFigure();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter,
                diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter,
                diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
        public static void DrawLocationAction(Graphics graphics,
            RectangleF bounds, string text, Font font, Color color,
            float scale, StringFormat format)
        {
            using (var pen = new Pen(Color.FromArgb(125, color),
                Math.Max(.65f, .8f * scale)))
            using (var brush = new SolidBrush(Color.FromArgb(205, color)))
            {
                DrawRoundedRectangle(graphics, pen, bounds,
                    Math.Max(2f, 3f * scale));
                graphics.DrawString(text, font, brush, bounds, format);
            }
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

    internal sealed class QuotaHeadlineLabel : Control
    {
        private double? fiveHourRemaining;
        private double? weeklyRemaining;

        public QuotaHeadlineLabel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint,
                true);
            Font = new Font(Ui.FontFamilyName, 15f, FontStyle.Bold);
            ForeColor = Color.White;
            Text = "GPT·暂无缓存";
        }

        public void SetQuotaValues(double? fiveHour, double? weekly)
        {
            fiveHourRemaining = fiveHour;
            weeklyRemaining = weekly;
            Text = fiveHour.HasValue || weekly.HasValue
                ? string.Empty : "GPT·暂无缓存";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DrawHeadline(e.Graphics, ClientRectangle);
        }

        internal void DrawLayered(Graphics graphics, Rectangle bounds)
        {
            DrawHeadline(graphics, bounds);
        }

        private void DrawHeadline(Graphics graphics, Rectangle bounds)
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(ForeColor))
            {
                if (!fiveHourRemaining.HasValue && !weeklyRemaining.HasValue)
                {
                    using (var format = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                        graphics.DrawString(Text, Font, brush, bounds, format);
                    return;
                }

                using (var tagFont = new Font(Font.FontFamily,
                    Math.Max(5.5f, Font.Size * .5f), FontStyle.Bold,
                    GraphicsUnit.Point))
                {
                    float x = bounds.Left;
                    DrawBase(graphics, brush, "GPT·", ref x, bounds);
                    if (fiveHourRemaining.HasValue)
                    {
                        DrawBase(graphics, brush,
                            fiveHourRemaining.Value.ToString("0") + "%",
                            ref x, bounds);
                        DrawTag(graphics, brush, tagFont, "5H", ref x, bounds);
                    }
                    if (weeklyRemaining.HasValue)
                    {
                        if (fiveHourRemaining.HasValue)
                            DrawBase(graphics, brush, "/", ref x, bounds);
                        DrawBase(graphics, brush,
                            weeklyRemaining.Value.ToString("0") + "%",
                            ref x, bounds);
                        DrawTag(graphics, brush, tagFont, "周", ref x, bounds);
                    }
                }
            }
        }

        private void DrawBase(Graphics graphics, Brush brush, string text,
            ref float x, Rectangle bounds)
        {
            var size = graphics.MeasureString(text, Font);
            graphics.DrawString(text, Font, brush, new PointF(x,
                bounds.Top + (bounds.Height - Font.Height) / 2f));
            x += size.Width;
        }

        private static void DrawTag(Graphics graphics, Brush brush, Font font,
            string text, ref float x, Rectangle bounds)
        {
            var size = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, brush, new PointF(x,
                bounds.Bottom - font.Height));
            x += size.Width;
        }
    }

    internal sealed class QuotaProgressBar : Control
    {
        private double? weeklyValue;
        private double? fiveHourValue;
        private Color trackColor = Color.FromArgb(55, 61, 73);

        // Retain the original single-value surface for callers that have not
        // yet switched to dual quotas; it represents the weekly/base value.
        public int Value { get { return (int)Math.Round(weeklyValue ?? 0d); } set { weeklyValue = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public Color FillColor { get; set; }
        public Color TrackColor { get { return trackColor; } set { trackColor = value; Invalidate(); } }

        public void SetQuotaValues(double? weeklyRemaining,
            double? fiveHourRemaining)
        {
            weeklyValue = ClampPercent(weeklyRemaining);
            fiveHourValue = ClampPercent(fiveHourRemaining);
            Invalidate();
        }

        public QuotaProgressBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawQuotaProgress(e.Graphics, ClientRectangle, false);
        }

        internal void DrawLayered(Graphics graphics, Rectangle bounds)
        {
            DrawQuotaProgress(graphics, bounds, true);
        }

        private void DrawQuotaProgress(Graphics graphics, Rectangle bounds,
            bool layered)
        {
            if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0)
                return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var trackPath = Capsule(bounds))
            using (var track = new SolidBrush(layered
                ? Color.FromArgb(150, trackColor) : trackColor))
            {
                graphics.FillPath(track, trackPath);
                var hasWeekly = weeklyValue.HasValue;
                var baseFraction = hasWeekly ? weeklyValue.Value / 100d :
                    fiveHourValue.HasValue ? 1d : 0d;
                var overlayFraction = hasWeekly
                    ? baseFraction * (fiveHourValue ?? 0d) / 100d
                    : (fiveHourValue ?? 0d) / 100d;
                var baseValue = weeklyValue ?? fiveHourValue ?? 0d;
                FillCapsule(graphics, trackPath, bounds, baseFraction,
                    Ui.QuotaColor(baseValue), false);
                // The 5H fill is normalized inside the weekly remaining
                // width.  A full 5H allowance therefore never paints beyond
                // the weekly allowance that is still available underneath.
                FillCapsule(graphics, trackPath, bounds, overlayFraction,
                    Color.FromArgb(204, 242, 125, 43), true);
            }
        }

        private static double? ClampPercent(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) ||
                double.IsInfinity(value.Value)) return null;
            return Math.Max(0d, Math.Min(100d, value.Value));
        }

        private static void FillCapsule(Graphics graphics,
            GraphicsPath fullPath, Rectangle bounds, double fraction,
            Color color, bool patterned)
        {
            if (fraction <= 0d) return;
            var width = (float)(bounds.Width * Math.Min(1d, fraction));
            var state = graphics.Save();
            try
            {
                graphics.SetClip(fullPath, CombineMode.Intersect);
                graphics.SetClip(new RectangleF(bounds.Left, bounds.Top, width,
                    bounds.Height), CombineMode.Intersect);
                using (var fillPath = Capsule(new RectangleF(bounds.Left,
                    bounds.Top, width, bounds.Height)))
                using (var fill = new SolidBrush(color))
                    graphics.FillPath(fill, fillPath);
                if (!patterned) return;
                using (var stripes = new Pen(Color.FromArgb(100, Color.White),
                    Math.Max(.65f, bounds.Height / 10f)))
                {
                    var step = Math.Max(4f, bounds.Height * .75f);
                    for (var offset = (float)(bounds.Left - bounds.Height);
                        offset < bounds.Left + width + bounds.Height;
                        offset += step)
                        graphics.DrawLine(stripes, offset, bounds.Bottom,
                            offset + bounds.Height, bounds.Top);
                }
            }
            finally { graphics.Restore(state); }
        }

        private static GraphicsPath Capsule(Rectangle bounds)
        {
            return Capsule(new RectangleF(bounds.Left, bounds.Top,
                bounds.Width, bounds.Height));
        }

        private static GraphicsPath Capsule(RectangleF bounds)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(1f, Math.Min(bounds.Width, bounds.Height));
            if (bounds.Width <= bounds.Height)
            {
                path.AddEllipse(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter,
                diameter, 270, 180);
            path.CloseFigure();
            return path;
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
            using (var font = new Font(StripFontFamily, 11f,
                FontStyle.Regular))
            {
                var data = Snapshot;
                var leftText = "等待本地限额快照";
                var resetText = "重置日期：未知";
                if (data != null && data.Quotas.Count > 0)
                {
                    leftText = QuotaSummary(data);
                    resetText = QuotaResetSummary(data);
                }
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var leftWidth = TextRenderer.MeasureText(leftText, font, Size.Empty, flags).Width;
                var resetWidth = TextRenderer.MeasureText(resetText, font, Size.Empty, flags).Width;
                preferredLogicalWidth = Math.Max(360, (int)Math.Ceiling((7 * scale + leftWidth + 5 * scale + 110 * scale + 4 * scale + resetWidth + 6 * scale) / scale));
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
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            if (!layered) graphics.Clear(BackColor);
            var scale = Math.Max(1f, DpiScale);
            var light = Theme == ThemeMode.Light;
            var menuTextColor = light ? Color.FromArgb(117, 117, 117) : Color.FromArgb(174, 174, 174);
            var trackColor = light ? Color.FromArgb(211, 216, 224) : Color.FromArgb(55, 61, 73);
            var data = Snapshot;
            if (data == null || data.Quotas.Count == 0)
            {
                using (var font = new Font(StripFontFamily, 11f,
                    FontStyle.Regular))
                using (var brush = new SolidBrush(menuTextColor))
                using (var waitingFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    graphics.DrawString("等待本地限额快照", font, brush, new RectangleF(8 * scale, 0, ClientSize.Width - 16 * scale, ClientSize.Height), waitingFormat);
                return;
            }

            var weeklyQuota = data.WeeklyQuota;
            var fiveHourQuota = data.FiveHourQuota;
            var weeklyRemaining = Remaining(weeklyQuota);
            var fiveHourRemaining = Remaining(fiveHourQuota);
            var reset = QuotaResetSummary(data);
            var progressHeight = Math.Max(3f, 4 * scale);
            var progressY = (ClientSize.Height - progressHeight) / 2f;

            using (var normal = new Font(StripFontFamily, 11f,
                FontStyle.Regular))
            using (var menuText = new SolidBrush(menuTextColor))
            using (var track = new SolidBrush(layered ? Color.FromArgb(170, trackColor) : trackColor))
            using (var centered = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
            {
                var leftText = QuotaSummary(data);
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var leftWidth = TextRenderer.MeasureText(leftText, normal, Size.Empty, flags).Width;
                var resetTextWidth = TextRenderer.MeasureText(reset, normal, Size.Empty, flags).Width;
                var leftX = 7 * scale;
                var progressX = leftX + leftWidth + 5 * scale;
                var resetX = ClientSize.Width - resetTextWidth - 5 * scale;
                var progressWidth = Math.Max(20 * scale, resetX - progressX - 4 * scale);
                graphics.DrawString(leftText, normal, menuText, new RectangleF(leftX, 0, leftWidth + 2 * scale, ClientSize.Height), centered);
                graphics.FillRectangle(track, progressX, progressY, progressWidth, progressHeight);
                DrawDualQuotaProgress(graphics, new RectangleF(progressX,
                    progressY, progressWidth, progressHeight), weeklyRemaining,
                    fiveHourRemaining);
                graphics.DrawString(reset, normal, menuText, new RectangleF(resetX, 0, resetTextWidth + 2 * scale, ClientSize.Height), centered);
            }
        }

        private static double? Remaining(QuotaWindow quota)
        {
            return quota == null ? (double?)null : Math.Max(0d,
                Math.Min(100d, 100d - quota.UsedPercent));
        }

        private static string QuotaSummary(UsageSnapshot data)
        {
            var fiveHour = Remaining(data.FiveHourQuota);
            var weekly = Remaining(data.WeeklyQuota);
            if (fiveHour.HasValue && weekly.HasValue)
                return string.Format("5H {0:0.#}% / 周 {1:0.#}%",
                    fiveHour.Value, weekly.Value);
            if (fiveHour.HasValue)
                return string.Format("5H {0:0.#}%", fiveHour.Value);
            if (weekly.HasValue)
                return string.Format("周 {0:0.#}%", weekly.Value);
            return "等待本地限额快照";
        }

        private static string QuotaResetSummary(UsageSnapshot data)
        {
            // The compact Windows top strip is intentionally 5H-focused.
            // The full dashboard retains both reset times below its quota bar.
            return "5H重置 " + ResetText(data.FiveHourQuota);
        }

        private static string ResetText(QuotaWindow quota)
        {
            return quota != null && quota.ResetsAt.HasValue
                ? quota.ResetsAt.Value.ToLocalTime().ToString("M月d日 HH:mm")
                : "—";
        }

        private static void DrawDualQuotaProgress(Graphics graphics,
            RectangleF bounds, double? weeklyRemaining,
            double? fiveHourRemaining)
        {
            var hasWeekly = weeklyRemaining.HasValue;
            var baseFraction = hasWeekly ? weeklyRemaining.Value / 100d :
                fiveHourRemaining.HasValue ? 1d : 0d;
            var overlayFraction = hasWeekly
                ? baseFraction * (fiveHourRemaining ?? 0d) / 100d
                : (fiveHourRemaining ?? 0d) / 100d;
            var baseValue = weeklyRemaining ?? fiveHourRemaining ?? 0d;
            FillProgress(graphics, bounds, baseFraction,
                Ui.QuotaColor(baseValue), false);
            FillProgress(graphics, bounds, overlayFraction,
                Color.FromArgb(204, 242, 125, 43), true);
        }

        private static void FillProgress(Graphics graphics, RectangleF bounds,
            double fraction, Color color, bool patterned)
        {
            if (fraction <= 0d) return;
            var width = (float)(bounds.Width * Math.Min(1d, fraction));
            using (var fill = new SolidBrush(color))
                graphics.FillRectangle(fill, bounds.Left, bounds.Top, width,
                    bounds.Height);
            if (!patterned) return;
            var state = graphics.Save();
            try
            {
                graphics.SetClip(new RectangleF(bounds.Left, bounds.Top,
                    width, bounds.Height), CombineMode.Intersect);
                using (var stripes = new Pen(Color.FromArgb(100, Color.White),
                    Math.Max(.65f, bounds.Height / 4f)))
                {
                    var step = Math.Max(4f, bounds.Height * .75f);
                    for (var offset = bounds.Left - bounds.Height;
                        offset < bounds.Left + width + bounds.Height;
                        offset += step)
                        graphics.DrawLine(stripes, offset, bounds.Bottom,
                            offset + bounds.Height, bounds.Top);
                }
            }
            finally { graphics.Restore(state); }
        }

        private static string ShortWindowName(int minutes)
        {
            if (minutes < 60) return minutes + "分钟";
            if (minutes < 1440) return FormatDuration(minutes / 60d) + "小时";
            return FormatDuration(minutes / 1440d) + "天";
        }
        private static string FormatDuration(double value) { return Math.Abs(value - Math.Round(value)) < .001 ? Math.Round(value).ToString("0") : value.ToString("0.#"); }
    }

    internal sealed class UsageScanner : IDisposable
    {
        private const int ReadBufferSize = 64 * 1024;
        private const int MaxLineBytes = 4 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, FileState> states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DateTime, TokenTotals> daily = new Dictionary<DateTime, TokenTotals>();
        private readonly byte[] readBuffer = new byte[ReadBufferSize];
        private readonly object watcherGate = new object();
        private readonly HashSet<string> changedSessionPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTimeOffset nextArchivedDiscovery = DateTimeOffset.MinValue;
        private DateTimeOffset nextSessionDiscovery = DateTimeOffset.MinValue;
        private static readonly TimeSpan ArchivedDiscoveryInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SessionDiscoveryInterval =
            TimeSpan.FromMinutes(5);
        private static readonly byte[] TokenCountMarker =
            Encoding.ASCII.GetBytes("\"token_count\"");
        private static readonly byte[][] SessionDetailMarkers =
        {
            Encoding.ASCII.GetBytes("\"session_meta\""),
            Encoding.ASCII.GetBytes("\"user_message\""),
            Encoding.ASCII.GetBytes("\"turn_context\""),
            Encoding.ASCII.GetBytes("\"task_started\""),
            Encoding.ASCII.GetBytes("\"task_complete\""),
            Encoding.ASCII.GetBytes("\"turn_aborted\""),
            Encoding.ASCII.GetBytes("\"function_call\""),
            Encoding.ASCII.GetBytes("\"custom_tool_call\""),
            Encoding.ASCII.GetBytes("\"tool_search_call\""),
            Encoding.ASCII.GetBytes("\"mcp_tool_call_end\""),
            Encoding.ASCII.GetBytes("\"web_search_end\"")
        };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        private readonly string codexRoot;
        private readonly bool includeSessionDetails;
        private readonly bool enableSessionWatcher;
        private FileSystemWatcher sessionWatcher;
        private int forceSessionDiscovery;
        private int disposed;

        public UsageScanner() : this(null, false) { }
        internal UsageScanner(string rootOverride) : this(rootOverride, false)
        {
        }
        internal UsageScanner(string rootOverride, bool includeDetails)
        {
            codexRoot = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            includeSessionDetails = includeDetails;
            enableSessionWatcher = rootOverride == null && !includeDetails;
            if (enableSessionWatcher)
                TryStartSessionWatcher();
        }

        public UsageSnapshot Scan()
        {
            return Scan(CancellationToken.None);
        }

        public UsageSnapshot Scan(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanAt = DateTimeOffset.Now;
                var scanArchived = scanAt >= nextArchivedDiscovery;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sessionsFolder = Path.Combine(codexRoot, "sessions");
                var archivedFolder = Path.Combine(codexRoot, "archived_sessions");
                var scanAllSessions = includeSessionDetails ||
                    sessionWatcher == null ||
                    scanAt >= nextSessionDiscovery ||
                    Interlocked.Exchange(ref forceSessionDiscovery, 0) != 0;
                var sessionsComplete = false;
                if (scanAllSessions)
                {
                    lock (watcherGate) changedSessionPaths.Clear();
                    sessionsComplete = DiscoverFolder(sessionsFolder, seen,
                        cancellationToken);
                    if (sessionsComplete)
                    {
                        nextSessionDiscovery =
                            scanAt + SessionDiscoveryInterval;
                        if (enableSessionWatcher &&
                            sessionWatcher == null)
                            TryStartSessionWatcher();
                    }
                }
                else
                {
                    RefreshChangedSessionFiles(sessionsFolder, seen,
                        cancellationToken);
                }
                var archivedComplete = true;
                if (scanArchived)
                {
                    archivedComplete = DiscoverFolder(archivedFolder, seen,
                        cancellationToken);
                    if (archivedComplete)
                        nextArchivedDiscovery = scanAt + ArchivedDiscoveryInterval;
                }
                else
                {
                    RefreshKnownArchivedFiles(archivedFolder, seen,
                        cancellationToken);
                }

                var sessionsPrefix = sessionsFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var staleSessionPaths = sessionsComplete
                    ? states.Keys.Where(path =>
                        path.StartsWith(sessionsPrefix, StringComparison.OrdinalIgnoreCase)
                        && !seen.Contains(path)).ToList()
                    : new List<string>();
                if (!scanArchived && staleSessionPaths.Count > 0)
                {
                    archivedComplete = DiscoverFolder(archivedFolder, seen,
                        cancellationToken);
                    if (archivedComplete)
                        nextArchivedDiscovery = scanAt + ArchivedDiscoveryInterval;
                }
                if (sessionsComplete)
                {
                    foreach (var stalePath in staleSessionPaths)
                    {
                        RemoveContribution(states[stalePath]);
                        states.Remove(stalePath);
                    }
                }

                var archivedPrefix = archivedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (archivedComplete)
                {
                    foreach (var stalePath in states.Keys.Where(path =>
                        path.StartsWith(archivedPrefix, StringComparison.OrdinalIgnoreCase)
                        && !seen.Contains(path)).ToList())
                    {
                        RemoveContribution(states[stalePath]);
                        states.Remove(stalePath);
                    }
                }
                var oldest = DateTime.Now.Date.AddDays(-35);
                foreach (var date in daily.Keys.Where(date => date < oldest).ToList()) daily.Remove(date);
                foreach (var state in states.Values)
                    foreach (var date in state.ByDay.Keys.Where(date => date < oldest).ToList())
                        state.ByDay.Remove(date);
                cancellationToken.ThrowIfCancellationRequested();
                return BuildSnapshot();
            }
        }

        private void TryStartSessionWatcher()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var folder = Path.Combine(codexRoot, "sessions");
            if (!Directory.Exists(folder)) return;
            try
            {
                sessionWatcher = new FileSystemWatcher(folder, "*.jsonl");
                sessionWatcher.IncludeSubdirectories = true;
                sessionWatcher.NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite | NotifyFilters.Size;
                sessionWatcher.Changed += OnSessionFileChanged;
                sessionWatcher.Created += OnSessionFileChanged;
                sessionWatcher.Deleted += OnSessionFileChanged;
                sessionWatcher.Renamed += OnSessionFileRenamed;
                sessionWatcher.Error += delegate
                {
                    Interlocked.Exchange(ref forceSessionDiscovery, 1);
                };
                sessionWatcher.EnableRaisingEvents = true;
            }
            catch (ArgumentException) { DisposeWatcher(); }
            catch (IOException) { DisposeWatcher(); }
        }

        private void OnSessionFileChanged(object sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.FullPath)) return;
            lock (watcherGate) changedSessionPaths.Add(e.FullPath);
        }

        private void OnSessionFileRenamed(object sender,
            RenamedEventArgs e)
        {
            lock (watcherGate)
            {
                if (!string.IsNullOrWhiteSpace(e.OldFullPath))
                    changedSessionPaths.Add(e.OldFullPath);
                if (!string.IsNullOrWhiteSpace(e.FullPath))
                    changedSessionPaths.Add(e.FullPath);
            }
        }

        private void RefreshChangedSessionFiles(string folder,
            HashSet<string> seen, CancellationToken cancellationToken)
        {
            List<string> changed;
            lock (watcherGate)
            {
                changed = changedSessionPaths.ToList();
                changedSessionPaths.Clear();
            }
            var prefix = folder.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var path in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!path.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;
                    seen.Add(path);
                    ProcessFile(path, info.Length, cancellationToken);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref disposed, 1);
            DisposeWatcher();
        }

        private void DisposeWatcher()
        {
            var watcher = sessionWatcher;
            sessionWatcher = null;
            if (watcher == null) return;
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        private bool DiscoverFolder(string folder, HashSet<string> seen,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(folder)) return true;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        seen.Add(file);
                        var info = new FileInfo(file);
                        ProcessFile(file, info.Length, cancellationToken);
                    }
                    catch (FileNotFoundException) { seen.Remove(file); }
                    catch (DirectoryNotFoundException) { seen.Remove(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void RefreshKnownArchivedFiles(string folder,
            HashSet<string> seen, CancellationToken cancellationToken)
        {
            var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var path in states.Keys.Where(item =>
                item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) continue;
                    seen.Add(path);
                    ProcessFile(path, info.Length, cancellationToken);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { seen.Add(path); }
                catch (UnauthorizedAccessException) { seen.Add(path); }
            }
        }

        private void ProcessFile(string path, long length,
            CancellationToken cancellationToken)
        {
            FileState state;
            if (!states.TryGetValue(path, out state)) { state = new FileState(path, includeSessionDetails); states[path] = state; }
            if (length < state.Offset) { RemoveContribution(state); state = new FileState(path, includeSessionDetails); states[path] = state; }
            if (length == state.Offset) return;
            var completeOffset = state.Offset;
            using (var pending = new MemoryStream())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferSize, FileOptions.SequentialScan))
            {
                fs.Seek(state.Offset, SeekOrigin.Begin);
                var discardLine = false;
                int read;
                while ((read = fs.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunkOffset = fs.Position - read;
                    var segmentStart = 0;
                    for (var i = 0; i < read; i++)
                    {
                        if (readBuffer[i] != (byte)'\n') continue;
                        var segmentLength = i - segmentStart;
                        if (!discardLine)
                        {
                            if (pending.Length == 0)
                            {
                                if (segmentLength <= MaxLineBytes) ParseUtf8Line(readBuffer, segmentStart, segmentLength, state);
                            }
                            else if (pending.Length + segmentLength <= MaxLineBytes)
                            {
                                pending.Write(readBuffer, segmentStart, segmentLength);
                                ParseUtf8Line(pending.GetBuffer(), 0, (int)pending.Length, state);
                            }
                        }
                        completeOffset = chunkOffset + i + 1;
                        state.Offset = completeOffset;
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
                    else pending.Write(readBuffer, segmentStart, trailingLength);
                }
            }
            state.Offset = completeOffset;
        }

        private void ParseUtf8Line(byte[] bytes, int offset, int count, FileState state)
        {
            if (count > 0 && bytes[offset + count - 1] == (byte)'\r') count--;
            if (count <= 0) return;
            var tokenLine = ContainsBytes(bytes, offset, count,
                TokenCountMarker);
            if (!tokenLine && (!includeSessionDetails ||
                !ContainsAnyBytes(bytes, offset, count,
                    SessionDetailMarkers)))
                return;
            var line = Encoding.UTF8.GetString(bytes, offset, count);
            if (line.Length == 0) return;
            if (line[0] == '\uFEFF') line = line.TrimStart('\uFEFF');
            try { ParseLine(line, state); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private static bool ContainsAnyBytes(byte[] bytes, int offset,
            int count, byte[][] markers)
        {
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                for (var markerIndex = 0;
                    markerIndex < markers.Length; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    if (marker == null || marker.Length == 0 ||
                        i + marker.Length > end ||
                        bytes[i] != marker[0]) continue;
                    var matched = true;
                    for (var j = 1; j < marker.Length; j++)
                    {
                        if (bytes[i + j] == marker[j]) continue;
                        matched = false;
                        break;
                    }
                    if (matched) return true;
                }
            }
            return false;
        }

        private static bool ContainsBytes(byte[] bytes, int offset,
            int count, byte[] marker)
        {
            if (marker == null || marker.Length == 0 ||
                count < marker.Length) return false;
            var last = offset + count - marker.Length;
            for (var i = offset; i <= last; i++)
            {
                if (bytes[i] != marker[0]) continue;
                var matched = true;
                for (var j = 1; j < marker.Length; j++)
                {
                    if (bytes[i + j] == marker[j]) continue;
                    matched = false;
                    break;
                }
                if (matched) return true;
            }
            return false;
        }

        private void ParseLine(string line, FileState state)
        {
            var root = json.DeserializeObject(line) as IDictionary<string, object>;
            if (root == null) return;
            var payload = Object(root, "payload");
            string rootType;
            String(root, "type", out rootType);
            string eventType;
            if (payload == null || !String(payload, "type", out eventType))
                eventType = rootType;
            string timestamp;
            var at = DateTimeOffset.MinValue;
            var hasTimestamp = String(root, "timestamp", out timestamp) &&
                DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out at);
            if (hasTimestamp) state.ObserveActivity(at);

            if (payload != null &&
                string.Equals(rootType, "session_meta", StringComparison.Ordinal))
            {
                ParseSessionMeta(payload, state, at);
                return;
            }
            if (string.Equals(eventType, "session_meta", StringComparison.Ordinal))
            {
                ParseSessionMeta(payload, state, at);
                return;
            }
            if (includeSessionDetails)
                ParseSessionDetailEvent(eventType, payload ?? root, root,
                    state);
            if (!string.Equals(eventType, "token_count", StringComparison.Ordinal)) return;
            if (!hasTimestamp || payload == null) return;

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
                    if (includeSessionDetails)
                        state.AggregateTotal = state.AggregateTotal + delta;
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

        private static void ParseSessionMeta(IDictionary<string, object> payload,
            FileState state, DateTimeOffset at)
        {
            string cwd;
            if (String(payload, "cwd", out cwd) && !string.IsNullOrWhiteSpace(cwd))
                state.ProjectPath = cwd.Trim();
            string id;
            if (String(payload, "id", out id) && !string.IsNullOrWhiteSpace(id))
                state.SessionId = id.Trim();
            if (at != DateTimeOffset.MinValue) state.ObserveActivity(at);
        }

        private static void ParseSessionDetailEvent(string eventType,
            IDictionary<string, object> source,
            IDictionary<string, object> root, FileState state)
        {
            if (string.IsNullOrEmpty(eventType) || source == null) return;
            if (string.Equals(eventType, "user_message",
                StringComparison.Ordinal))
                state.TurnCount++;
            else if (string.Equals(eventType, "task_started",
                StringComparison.Ordinal))
                state.Status = "进行中";
            else if (string.Equals(eventType, "task_complete",
                StringComparison.Ordinal))
                state.Status = "已完成";
            else if (string.Equals(eventType, "turn_aborted",
                StringComparison.Ordinal))
                state.Status = "已中止";
            else if (string.Equals(eventType, "turn_context",
                StringComparison.Ordinal))
            {
                string value;
                if (String(source, "model", out value) &&
                    !string.IsNullOrWhiteSpace(value))
                    state.Model = value.Trim();
                if (String(source, "effort", out value) &&
                    !string.IsNullOrWhiteSpace(value))
                    state.Effort = value.Trim();
            }

            if (!IsToolCallEvent(eventType)) return;
            string callId;
            if ((!String(source, "call_id", out callId) ||
                string.IsNullOrWhiteSpace(callId)) &&
                (!String(root, "call_id", out callId) ||
                string.IsNullOrWhiteSpace(callId)))
            {
                state.ToolCallsWithoutId++;
                return;
            }
            if (state.ToolCallIds == null)
                state.ToolCallIds = new HashSet<string>(
                    StringComparer.Ordinal);
            state.ToolCallIds.Add(callId);
        }

        private static bool IsToolCallEvent(string eventType)
        {
            return string.Equals(eventType, "function_call",
                    StringComparison.Ordinal) ||
                string.Equals(eventType, "custom_tool_call",
                    StringComparison.Ordinal) ||
                string.Equals(eventType, "tool_search_call",
                    StringComparison.Ordinal) ||
                string.Equals(eventType, "mcp_tool_call_end",
                    StringComparison.Ordinal) ||
                string.Equals(eventType, "web_search_end",
                    StringComparison.Ordinal);
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
            var projects = includeSessionDetails ? states.Values
                .Where(state => state.HasUsage)
                .GroupBy(state => string.IsNullOrWhiteSpace(state.ProjectPath) ? "未识别项目" : state.ProjectPath, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var sessions = group
                        .Select(state =>
                        {
                            var session = new SessionUsage(state.SessionId,
                                state.AggregateTotal, state.StartedAt,
                                state.LastActivity, state.TurnCount,
                                (state.ToolCallIds == null ? 0 :
                                    state.ToolCallIds.Count) +
                                    state.ToolCallsWithoutId,
                                state.Model, state.Effort, state.Status);
                            session.SessionFilePath = state.SessionFilePath;
                            return session;
                        })
                        .Where(session => session.TotalTokens > 0)
                        .OrderByDescending(session => session.TotalTokens)
                        .ToList();
                    return new ProjectUsage(group.Key, ProjectDisplayName(group.Key), sessions);
                })
                .Where(project => project.TotalTokens > 0)
                .OrderByDescending(project => project.TotalTokens)
                .ToList() : new List<ProjectUsage>();
            return new UsageSnapshot(sum(1), sum(7), sum(30), states.Values.Count(s => s.HasUsage && s.LastActivity >= weekStart), latestQuota == null ? DateTimeOffset.MinValue : latestQuota.At, latestQuota == null ? new List<QuotaWindow>() : latestQuota.Windows, projects);
        }

        private static string ProjectDisplayName(string path)
        {
            if (string.Equals(path, "未识别项目", StringComparison.Ordinal)) return path;
            try
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(trimmed);
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
    }

    internal sealed class FileState
    {
        public long Offset; public TokenTotals LastTotal = new TokenTotals(); public TokenTotals AggregateTotal = new TokenTotals(); public readonly Dictionary<DateTime, TokenTotals> ByDay = new Dictionary<DateTime, TokenTotals>(); public DateTimeOffset StartedAt; public DateTimeOffset LastActivity; public bool HasUsage; public QuotaSnapshot LatestQuota; public string ProjectPath; public string SessionId; public string SessionFilePath; public int TurnCount; public int ToolCallsWithoutId; public HashSet<string> ToolCallIds; public string Model; public string Effort; public string Status;
        public FileState(string path, bool includeSessionDetails)
        {
            if (includeSessionDetails)
            {
                SessionId = Path.GetFileNameWithoutExtension(path) ??
                    "未知 session";
                SessionFilePath = path;
            }
        }
        public void ObserveActivity(DateTimeOffset at)
        {
            if (at == DateTimeOffset.MinValue) return;
            if (StartedAt == DateTimeOffset.MinValue || at < StartedAt)
                StartedAt = at;
            if (at > LastActivity) LastActivity = at;
        }
    }
    internal struct TokenTotals
    {
        public long Input, Output, Cached, Reasoning; public long Total { get { return Input + Output; } }
        public TokenTotals(long input, long output, long cached = 0,
            long reasoning = 0) { Input = input; Output = output; Cached = cached; Reasoning = reasoning; }
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
    internal sealed class SessionUsage
    {
        public string SessionId; public string SessionFilePath; public long TotalTokens; public DateTimeOffset StartedAt; public DateTimeOffset LastActivity; public int TurnCount; public int ToolCallCount; public long InputTokens; public long OutputTokens; public long CachedTokens; public long ReasoningTokens; public string Model; public string Effort; public string Status;
        public SessionUsage(string id, long total, DateTimeOffset lastActivity)
            : this(id, new TokenTotals(total, 0), lastActivity,
                lastActivity, 0, 0, null, null, null) { }
        public SessionUsage(string id, TokenTotals totals,
            DateTimeOffset startedAt, DateTimeOffset lastActivity,
            int turnCount, int toolCallCount, string model, string effort,
            string status)
        {
            SessionId = id; InputTokens = totals.Input;
            OutputTokens = totals.Output;
            CachedTokens = totals.Cached;
            ReasoningTokens = totals.Reasoning;
            TotalTokens = InputTokens + OutputTokens; StartedAt = startedAt;
            LastActivity = lastActivity; TurnCount = turnCount;
            ToolCallCount = toolCallCount; Model = model; Effort = effort;
            Status = status;
        }
    }
    internal sealed class ProjectUsage
    {
        public string ProjectPath; public string DisplayName; public List<SessionUsage> Sessions;
        public long TotalTokens { get { return Sessions.Sum(session => session.TotalTokens); } }
        public ProjectUsage(string path, string displayName, List<SessionUsage> sessions) { ProjectPath = path; DisplayName = displayName; Sessions = sessions ?? new List<SessionUsage>(); }
    }
    internal sealed class UsageSnapshot
    {
        public TokenTotals Today, Week, Month; public int WeekSessions; public DateTimeOffset QuotaAt; public List<QuotaWindow> Quotas; public List<ProjectUsage> Projects;
        public UsageSnapshot(TokenTotals t, TokenTotals w, TokenTotals m, int s, DateTimeOffset q, List<QuotaWindow> l) : this(t, w, m, s, q, l, new List<ProjectUsage>()) { }
        public UsageSnapshot(TokenTotals t, TokenTotals w, TokenTotals m, int s, DateTimeOffset q, List<QuotaWindow> l, List<ProjectUsage> projects) { Today = t; Week = w; Month = m; WeekSessions = s; QuotaAt = q; Quotas = l; Projects = projects ?? new List<ProjectUsage>(); }

        // Select the two quota windows explicitly.  Codex may emit them in
        // either order, so selecting the first/shortest value is no longer
        // reliable once 5H and weekly allowances are both present.
        public QuotaWindow FiveHourQuota
        {
            get
            {
                return (Quotas ?? new List<QuotaWindow>())
                    .Where(value => value != null &&
                        value.WindowMinutes > 0 &&
                        value.WindowMinutes < 24 * 60)
                    .OrderBy(value => Math.Abs(value.WindowMinutes - 5 * 60))
                    .FirstOrDefault();
            }
        }

        public QuotaWindow WeeklyQuota
        {
            get
            {
                return (Quotas ?? new List<QuotaWindow>())
                    .Where(value => value != null &&
                        value.WindowMinutes >= 24 * 60)
                    .OrderBy(value => Math.Abs(value.WindowMinutes -
                        7 * 24 * 60))
                    .FirstOrDefault();
            }
        }

        // Compatibility surface for any existing caller that expects one
        // visible quota.  New UI, history, and chart code use the two
        // explicit properties above.
        public QuotaWindow PrimaryQuota
        {
            get
            {
                return FiveHourQuota ?? WeeklyQuota ??
                    (Quotas ?? new List<QuotaWindow>())
                        .Where(value => value != null)
                        .OrderBy(value => value.WindowMinutes)
                        .FirstOrDefault();
            }
        }
    }
}

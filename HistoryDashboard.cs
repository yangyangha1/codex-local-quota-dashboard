using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal sealed class HistoryDashboardForm : Form
    {
        private readonly HistoryStore store;
        private readonly Label titleLabel = Ui.Label("历史数据", 13f,
            FontStyle.Bold, Color.White);
        private readonly LinkLabel openLocation = new LinkLabel
        {
            Text = "打开保存位置",
            AutoSize = true,
            TabStop = true
        };
        private readonly Label fromLabel = Ui.Label("开始日期", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly Label toLabel = Ui.Label("结束日期", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly DateTimePicker fromDate = new DateTimePicker();
        private readonly DateTimePicker toDate = new DateTimePicker();
        private readonly Button applyButton = new Button { Text = "查看" };
        private readonly Button resetZoomButton = new Button
            { Text = "重置缩放" };
        private readonly Label quotaTitle = Ui.Label("最近限额快照", 9f,
            FontStyle.Bold, Color.Gray);
        private readonly Label quotaValue = Ui.Label("暂无历史", 17f,
            FontStyle.Bold, Color.White);
        private readonly Label quotaSub = Ui.Label("等待历史记录", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly QuotaProgressBar quotaBar = new QuotaProgressBar();
        private readonly Label todayCaption = Ui.Label("今日", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly Label weekCaption = Ui.Label("近 7 天", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly Label monthCaption = Ui.Label("近 30 天", 8f,
            FontStyle.Bold, Color.Gray);
        private readonly Label todayValue = Ui.Metric("—");
        private readonly Label weekValue = Ui.Metric("—");
        private readonly Label monthValue = Ui.Metric("—");
        private readonly HistoryChartControl chart =
            new HistoryChartControl();
        private readonly Label statusLabel = Ui.Label("准备读取历史数据", 8f,
            FontStyle.Regular, Color.Gray);
        private ThemeMode currentTheme;
        private bool loading;
        private bool previewSamplesApplied;

        public HistoryDashboardForm(HistoryStore historyStore,
            ThemeMode theme)
        {
            store = historyStore;
            currentTheme = theme;
            Text = "Codex 历史数据";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 560);
            MinimumSize = new Size(660, 500);
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(Ui.FontFamilyName, 9f);
            DoubleBuffered = true;
            KeyPreview = true;

            fromDate.Format = DateTimePickerFormat.Custom;
            toDate.Format = DateTimePickerFormat.Custom;
            fromDate.CustomFormat = "yyyy年MM月dd日";
            toDate.CustomFormat = "yyyy年MM月dd日";
            fromDate.MaxDate = DateTime.Today;
            toDate.MaxDate = DateTime.Today;
            fromDate.Value = DateTime.Today.AddDays(-6);
            toDate.Value = DateTime.Today;
            fromDate.TabIndex = 0;
            toDate.TabIndex = 1;
            applyButton.TabIndex = 2;
            resetZoomButton.TabIndex = 3;
            openLocation.TabIndex = 4;
            chart.TabIndex = 5;

            ConfigureButton(applyButton, true);
            ConfigureButton(resetZoomButton, false);
            applyButton.Click += async delegate { await LoadSelectedRange(); };
            resetZoomButton.Click += delegate { chart.ResetZoom(); };
            openLocation.LinkClicked += delegate { OpenStorageLocation(); };

            Controls.AddRange(new Control[]
            {
                titleLabel, openLocation, fromLabel, fromDate, toLabel,
                toDate, applyButton, resetZoomButton, quotaTitle, quotaValue,
                quotaBar, quotaSub, todayCaption, todayValue, weekCaption,
                weekValue, monthCaption, monthValue, chart, statusLabel
            });
            Resize += delegate { LayoutControls(); };
            Shown += async delegate
            {
                if (!previewSamplesApplied) await LoadSelectedRange();
            };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Close();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.D0)
                {
                    chart.ResetZoom();
                    e.Handled = true;
                }
            };
            ApplyTheme(theme);
            LayoutControls();
        }

        private static void ConfigureButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.Height = 30;
            button.AccessibleName = button.Text;
            if (primary) button.Font = new Font(Ui.FontFamilyName, 9f,
                FontStyle.Bold);
        }

        private void LayoutControls()
        {
            var width = ClientSize.Width;
            var bottom = ClientSize.Height;
            titleLabel.Bounds = new Rectangle(18, 10, 250, 30);
            openLocation.Location = new Point(
                Math.Max(18, width - openLocation.PreferredWidth - 20), 18);
            fromLabel.Bounds = new Rectangle(18, 48, 68, 20);
            fromDate.Bounds = new Rectangle(18, 68, 154, 30);
            toLabel.Bounds = new Rectangle(184, 48, 68, 20);
            toDate.Bounds = new Rectangle(184, 68, 154, 30);
            applyButton.Bounds = new Rectangle(350, 68, 72, 30);
            resetZoomButton.Bounds = new Rectangle(432, 68, 92, 30);

            quotaTitle.Bounds = new Rectangle(18, 108, width - 36, 20);
            quotaValue.Bounds = new Rectangle(18, 128, width - 36, 34);
            quotaBar.Bounds = new Rectangle(18, 164, width - 36, 6);
            quotaSub.Bounds = new Rectangle(18, 172, width - 36, 20);

            var metricWidth = Math.Max(120, (width - 52) / 3);
            todayCaption.Bounds = new Rectangle(18, 199, metricWidth, 18);
            todayValue.Bounds = new Rectangle(18, 217, metricWidth, 30);
            weekCaption.Bounds = new Rectangle(26 + metricWidth, 199,
                metricWidth, 18);
            weekValue.Bounds = new Rectangle(26 + metricWidth, 217,
                metricWidth, 30);
            monthCaption.Bounds = new Rectangle(34 + metricWidth * 2, 199,
                metricWidth, 18);
            monthValue.Bounds = new Rectangle(34 + metricWidth * 2, 217,
                metricWidth, 30);

            chart.Bounds = new Rectangle(18, 253, width - 36,
                Math.Max(180, bottom - 286));
            statusLabel.Bounds = new Rectangle(18, bottom - 28,
                width - 36, 20);
        }

        public void ApplyTheme(ThemeMode theme)
        {
            currentTheme = theme;
            var light = theme == ThemeMode.Light;
            var background = light ? Color.FromArgb(236, 245, 250) :
                Color.FromArgb(26, 34, 37);
            var primary = light ? Color.FromArgb(12, 19, 26) :
                Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) :
                Color.FromArgb(160, 171, 186);
            var border = light ? Color.FromArgb(183, 195, 207) :
                Color.FromArgb(67, 78, 88);
            BackColor = background;
            ForeColor = primary;
            foreach (var label in new[] { titleLabel, quotaValue,
                todayValue, weekValue, monthValue })
                label.ForeColor = primary;
            foreach (var label in new[] { fromLabel, toLabel, quotaTitle,
                quotaSub, todayCaption, weekCaption, monthCaption,
                statusLabel })
                label.ForeColor = muted;
            openLocation.LinkColor = light ? Color.FromArgb(23, 104, 160) :
                Color.FromArgb(117, 190, 235);
            openLocation.ActiveLinkColor = light ?
                Color.FromArgb(16, 82, 128) : Color.FromArgb(170, 218, 246);
            openLocation.VisitedLinkColor = openLocation.LinkColor;
            foreach (var picker in new[] { fromDate, toDate })
            {
                picker.CalendarForeColor = primary;
                picker.CalendarMonthBackground = background;
            }
            applyButton.BackColor = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(65, 142, 196);
            applyButton.ForeColor = Color.White;
            applyButton.FlatAppearance.BorderColor = applyButton.BackColor;
            resetZoomButton.BackColor = background;
            resetZoomButton.ForeColor = primary;
            resetZoomButton.FlatAppearance.BorderColor = border;
            quotaBar.TrackColor = light ? Color.FromArgb(211, 216, 224) :
                Color.FromArgb(55, 61, 73);
            chart.Theme = theme;
            Invalidate(true);
        }

        private async Task LoadSelectedRange()
        {
            if (loading) return;
            var from = fromDate.Value.Date;
            var through = toDate.Value.Date;
            if (from > through)
            {
                statusLabel.Text = "开始日期不能晚于结束日期。";
                return;
            }
            loading = true;
            applyButton.Enabled = false;
            applyButton.Text = "读取中";
            statusLabel.Text = "正在读取本地历史数据…";
            try
            {
                var fromOffset = new DateTimeOffset(from);
                var toExclusive = new DateTimeOffset(through.AddDays(1));
                var samples = await Task.Run(() =>
                    store.ReadRange(fromOffset, toExclusive));
                if (IsDisposed) return;
                ApplySamples(samples, fromOffset, toExclusive);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    chart.SetError("历史数据读取失败");
                    statusLabel.Text = "读取失败：" + ex.Message;
                }
            }
            finally
            {
                loading = false;
                if (!IsDisposed)
                {
                    applyButton.Enabled = true;
                    applyButton.Text = "查看";
                }
            }
        }

        internal void ApplySamplesForPreview(List<HistorySample> samples,
            DateTimeOffset from, DateTimeOffset toExclusive)
        {
            previewSamplesApplied = true;
            ApplySamples(samples, from, toExclusive);
        }

        private void ApplySamples(List<HistorySample> samples,
            DateTimeOffset from, DateTimeOffset toExclusive)
        {
            chart.SetSamples(samples, from, toExclusive);
            if (samples.Count == 0)
            {
                quotaTitle.Text = "最近限额快照";
                quotaValue.Text = "暂无历史";
                quotaSub.Text = "打开软件并完成一次扫描后开始记录";
                quotaBar.Value = 0;
                todayValue.Text = weekValue.Text = monthValue.Text = "—";
                statusLabel.Text = string.Format(CultureInfo.CurrentCulture,
                    "{0:yyyy年M月d日}—{1:yyyy年M月d日} · 暂无记录 · {2}",
                    from.LocalDateTime, toExclusive.AddDays(-1).LocalDateTime,
                    FormatFileSize(store.FileSize));
                return;
            }
            var latest = samples[samples.Count - 1];
            todayValue.Text = Ui.Compact(latest.TodayTokens);
            weekValue.Text = Ui.Compact(latest.WeekTokens);
            monthValue.Text = Ui.Compact(latest.MonthTokens);
            if (latest.RemainingPercent.HasValue)
            {
                var remaining = latest.RemainingPercent.Value;
                quotaTitle.Text = Ui.WindowName(latest.WindowMinutes) +
                    " · 历史快照";
                quotaValue.Text = string.Format(CultureInfo.CurrentCulture,
                    "剩余 {0:0.#}%", remaining);
                quotaBar.Value = Math.Max(0, Math.Min(100,
                    (int)Math.Round(remaining)));
                quotaBar.FillColor = Ui.QuotaColor(remaining);
                var reset = latest.ResetsAt.HasValue
                    ? latest.ResetsAt.Value.ToLocalTime().ToString(
                        "M月d日 HH:mm", CultureInfo.CurrentCulture)
                    : "未知";
                quotaSub.Text = string.Format(CultureInfo.CurrentCulture,
                    "已用 {0:0.#}% · 重置 {1}", 100d - remaining, reset);
            }
            else
            {
                quotaTitle.Text = "最近限额快照";
                quotaValue.Text = "暂无缓存";
                quotaSub.Text = "该历史范围内没有额度记录";
                quotaBar.Value = 0;
            }
            statusLabel.Text = string.Format(CultureInfo.CurrentCulture,
                "{0:yyyy年M月d日}—{1:yyyy年M月d日} · {2:N0} 条记录 · {3}",
                from.LocalDateTime, toExclusive.AddDays(-1).LocalDateTime,
                samples.Count, FormatFileSize(store.FileSize));
        }

        private void OpenStorageLocation()
        {
            try
            {
                var folder = store.StorageDirectory;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                if (File.Exists(store.StoragePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "/select,\"" + store.StoragePath + "\"")
                    { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "\"" + folder + "\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = "无法打开保存位置：" + ex.Message;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024d).ToString("0.#",
                    CultureInfo.CurrentCulture) + " KB";
            return (bytes / (1024d * 1024d)).ToString("0.##",
                CultureInfo.CurrentCulture) + " MB";
        }
    }

    internal sealed class HistoryChartControl : Control
    {
        private readonly ToolTip toolTip = new ToolTip();
        private List<HistorySample> samples = new List<HistorySample>();
        private DateTimeOffset fullFrom;
        private DateTimeOffset fullTo;
        private DateTimeOffset viewFrom;
        private DateTimeOffset viewTo;
        private Point selectionStart;
        private Point selectionCurrent;
        private bool selecting;
        private string error;
        private ThemeMode theme;

        public ThemeMode Theme
        {
            get { return theme; }
            set { theme = value; Invalidate(); }
        }

        public HistoryChartControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                ControlStyles.Selectable |
                ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Cross;
            AccessibleName = "历史用量图表";
            AccessibleDescription =
                "左键框选时间范围进行局部放大，滚轮围绕鼠标位置缩放时间轴。";
        }

        public void SetSamples(List<HistorySample> values,
            DateTimeOffset from, DateTimeOffset toExclusive)
        {
            samples = values == null ? new List<HistorySample>() :
                values.OrderBy(value => value.At).ToList();
            fullFrom = from;
            fullTo = toExclusive;
            error = null;
            ResetZoom();
        }

        public void SetError(string message)
        {
            error = message;
            samples.Clear();
            Invalidate();
        }

        public void ResetZoom()
        {
            viewFrom = fullFrom;
            viewTo = fullTo;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !PlotBounds.Contains(e.Location))
                return;
            Focus();
            selecting = true;
            selectionStart = selectionCurrent = e.Location;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (selecting)
            {
                selectionCurrent = new Point(
                    Math.Max(PlotBounds.Left,
                        Math.Min(PlotBounds.Right, e.X)), e.Y);
                Invalidate();
                return;
            }
            if (!PlotBounds.Contains(e.Location) || samples.Count == 0)
            {
                toolTip.SetToolTip(this, null);
                return;
            }
            var at = XToTime(e.X);
            var nearest = samples.Where(value => value.At >= viewFrom &&
                value.At <= viewTo).OrderBy(value =>
                    Math.Abs((value.At - at).Ticks)).FirstOrDefault();
            if (nearest == null) return;
            var quota = nearest.RemainingPercent.HasValue
                ? " · 剩余 " + nearest.RemainingPercent.Value.ToString(
                    "0.#", CultureInfo.CurrentCulture) + "%" : string.Empty;
            toolTip.SetToolTip(this,
                nearest.At.ToLocalTime().ToString("yyyy年M月d日 HH:mm",
                    CultureInfo.CurrentCulture) + "\n今日 " +
                Ui.Compact(nearest.TodayTokens) + quota);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!selecting) return;
            selecting = false;
            Capture = false;
            var left = Math.Min(selectionStart.X, selectionCurrent.X);
            var right = Math.Max(selectionStart.X, selectionCurrent.X);
            if (right - left >= 8)
            {
                var newFrom = XToTime(left);
                var newTo = XToTime(right);
                if (newTo - newFrom >= TimeSpan.FromMinutes(5))
                {
                    viewFrom = newFrom;
                    viewTo = newTo;
                }
            }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!PlotBounds.Contains(e.Location) || fullTo <= fullFrom) return;
            var current = viewTo - viewFrom;
            var factor = e.Delta > 0 ? 0.80d : 1.25d;
            var nextTicks = (long)(current.Ticks * factor);
            var minimum = TimeSpan.FromMinutes(5).Ticks;
            var maximum = (fullTo - fullFrom).Ticks;
            nextTicks = Math.Max(minimum, Math.Min(maximum, nextTicks));
            var anchor = XToTime(e.X);
            var ratio = (e.X - PlotBounds.Left) /
                (double)Math.Max(1, PlotBounds.Width);
            var nextFrom = anchor.AddTicks(-(long)(nextTicks * ratio));
            var nextTo = nextFrom.AddTicks(nextTicks);
            if (nextFrom < fullFrom)
            {
                nextFrom = fullFrom;
                nextTo = nextFrom.AddTicks(nextTicks);
            }
            if (nextTo > fullTo)
            {
                nextTo = fullTo;
                nextFrom = nextTo.AddTicks(-nextTicks);
            }
            viewFrom = nextFrom;
            viewTo = nextTo;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Home ||
                (e.Control && e.KeyCode == Keys.D0))
            {
                ResetZoom();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
            {
                ZoomFromKeyboard(0.8d);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
            {
                ZoomFromKeyboard(1.25d);
                e.Handled = true;
            }
        }

        private void ZoomFromKeyboard(double factor)
        {
            if (fullTo <= fullFrom) return;
            var middle = viewFrom.AddTicks((viewTo - viewFrom).Ticks / 2);
            var ticks = Math.Max(TimeSpan.FromMinutes(5).Ticks,
                Math.Min((fullTo - fullFrom).Ticks,
                    (long)((viewTo - viewFrom).Ticks * factor)));
            viewFrom = middle.AddTicks(-ticks / 2);
            viewTo = viewFrom.AddTicks(ticks);
            if (viewFrom < fullFrom)
            {
                viewFrom = fullFrom;
                viewTo = viewFrom.AddTicks(ticks);
            }
            if (viewTo > fullTo)
            {
                viewTo = fullTo;
                viewFrom = viewTo.AddTicks(-ticks);
            }
            Invalidate();
        }

        private Rectangle PlotBounds
        {
            get
            {
                return Rectangle.FromLTRB(48, 34,
                    Math.Max(70, ClientSize.Width - 45),
                    Math.Max(70, ClientSize.Height - 32));
            }
        }

        private DateTimeOffset XToTime(int x)
        {
            var plot = PlotBounds;
            var ratio = (x - plot.Left) /
                (double)Math.Max(1, plot.Width);
            ratio = Math.Max(0d, Math.Min(1d, ratio));
            return viewFrom.AddTicks(
                (long)((viewTo - viewFrom).Ticks * ratio));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var light = theme == ThemeMode.Light;
            var primary = light ? Color.FromArgb(12, 19, 26) :
                Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) :
                Color.FromArgb(160, 171, 186);
            var grid = light ? Color.FromArgb(55, 118, 130, 143) :
                Color.FromArgb(52, 176, 188, 201);
            var tokenColor = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(92, 175, 232);
            var quotaColor = light ? Color.FromArgb(27, 151, 101) :
                Color.FromArgb(75, 205, 143);
            var plot = PlotBounds;
            using (var primaryBrush = new SolidBrush(primary))
            using (var mutedBrush = new SolidBrush(muted))
            using (var tokenBrush = new SolidBrush(tokenColor))
            using (var quotaBrush = new SolidBrush(quotaColor))
            using (var headerFont = new Font(Ui.FontFamilyName, 8f,
                FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName, 7.5f))
            {
                var visible = GetVisibleSamples();
                var increase = CalculateIncrease(visible);
                var quotaText = visible.Where(value =>
                    value.RemainingPercent.HasValue).Select(value =>
                        value.RemainingPercent.Value).DefaultIfEmpty().Min();
                e.Graphics.DrawString("累计 Token：" + Ui.Compact(increase),
                    headerFont, tokenBrush, new PointF(plot.Left, 6));
                var spanText = FormatSpan(viewTo - viewFrom);
                e.Graphics.DrawString("时间范围 " + spanText, headerFont,
                    mutedBrush, new PointF(plot.Left + plot.Width * .38f, 6));
                var right = quotaText > 0d ? "最低剩余 " +
                    quotaText.ToString("0.#", CultureInfo.CurrentCulture) + "%"
                    : "剩余额度 —";
                using (var rightFormat = new StringFormat
                {
                    Alignment = StringAlignment.Far
                })
                    e.Graphics.DrawString(right, headerFont, quotaBrush,
                        new RectangleF(plot.Left, 6, plot.Width, 20),
                        rightFormat);

                DrawGrid(e.Graphics, plot, grid);
                if (!string.IsNullOrEmpty(error))
                {
                    DrawCentered(e.Graphics, error, smallFont, mutedBrush, plot);
                    return;
                }
                if (visible.Count < 2)
                {
                    DrawCentered(e.Graphics,
                        visible.Count == 0 ? "该日期范围暂无历史数据" :
                            "等待更多历史记录", smallFont, mutedBrush, plot);
                    DrawTimeLabels(e.Graphics, plot, smallFont, mutedBrush);
                    return;
                }

                var points = Downsample(visible, 1000);
                DrawTokenSeries(e.Graphics, plot, points, tokenColor);
                DrawQuotaSeries(e.Graphics, plot, points, quotaColor);
                DrawTimeLabels(e.Graphics, plot, smallFont, mutedBrush);
                e.Graphics.DrawString("Token", smallFont, tokenBrush,
                    new PointF(2, plot.Top + 2));
                e.Graphics.DrawString("额度%", smallFont, quotaBrush,
                    new PointF(plot.Right + 3, plot.Top + 2));

                if (selecting)
                {
                    var left = Math.Min(selectionStart.X, selectionCurrent.X);
                    var rightX = Math.Max(selectionStart.X,
                        selectionCurrent.X);
                    using (var selectionBrush = new SolidBrush(
                        Color.FromArgb(light ? 48 : 58, tokenColor)))
                    using (var selectionPen = new Pen(tokenColor, 1f))
                    {
                        var selection = Rectangle.FromLTRB(left, plot.Top,
                            rightX, plot.Bottom);
                        e.Graphics.FillRectangle(selectionBrush, selection);
                        e.Graphics.DrawRectangle(selectionPen, selection);
                    }
                }
            }
        }

        private List<HistorySample> GetVisibleSamples()
        {
            return samples.Where(value => value.At >= viewFrom &&
                value.At <= viewTo).ToList();
        }

        private static List<HistorySample> Downsample(
            List<HistorySample> source, int maximum)
        {
            if (source.Count <= maximum) return source;
            var step = (int)Math.Ceiling(source.Count / (double)maximum);
            var output = new List<HistorySample>(maximum + 1);
            for (var index = 0; index < source.Count; index += step)
                output.Add(source[index]);
            if (!ReferenceEquals(output[output.Count - 1],
                source[source.Count - 1])) output.Add(source[source.Count - 1]);
            return output;
        }

        private static long CalculateIncrease(List<HistorySample> source)
        {
            if (source.Count < 2) return 0L;
            long total = 0;
            var previous = source[0].TodayTokens;
            for (var index = 1; index < source.Count; index++)
            {
                var current = source[index].TodayTokens;
                total += current >= previous ? current - previous : current;
                previous = current;
            }
            return Math.Max(0L, total);
        }

        private void DrawTokenSeries(Graphics graphics, Rectangle plot,
            List<HistorySample> points, Color color)
        {
            var cumulative = new long[points.Count];
            long total = 0;
            var previous = points[0].TodayTokens;
            for (var index = 1; index < points.Count; index++)
            {
                var current = points[index].TodayTokens;
                total += current >= previous ? current - previous : current;
                cumulative[index] = total;
                previous = current;
            }
            var maximum = Math.Max(1L, cumulative.Max());
            using (var pen = new Pen(color, 2f))
            {
                PointF? prior = null;
                for (var index = 0; index < points.Count; index++)
                {
                    var point = points[index];
                    var x = TimeToX(point.At, plot);
                    var y = plot.Bottom - plot.Height *
                        cumulative[index] / (float)maximum;
                    var current = new PointF(x, y);
                    if (prior.HasValue &&
                        point.At - points[index - 1].At < MaxGap())
                        graphics.DrawLine(pen, prior.Value, current);
                    prior = current;
                }
            }
        }

        private void DrawQuotaSeries(Graphics graphics, Rectangle plot,
            List<HistorySample> points, Color color)
        {
            using (var pen = new Pen(color, 1.7f))
            {
                pen.DashStyle = DashStyle.Dash;
                PointF? prior = null;
                HistorySample priorSample = null;
                foreach (var point in points)
                {
                    if (!point.RemainingPercent.HasValue)
                    {
                        prior = null;
                        priorSample = null;
                        continue;
                    }
                    var current = new PointF(TimeToX(point.At, plot),
                        plot.Bottom - plot.Height * (float)Math.Max(0d,
                            Math.Min(100d, point.RemainingPercent.Value)) / 100f);
                    if (prior.HasValue && priorSample != null &&
                        point.At - priorSample.At < MaxGap())
                        graphics.DrawLine(pen, prior.Value, current);
                    prior = current;
                    priorSample = point;
                }
            }
        }

        private TimeSpan MaxGap()
        {
            var span = viewTo - viewFrom;
            return TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(3).Ticks,
                span.Ticks / 120));
        }

        private float TimeToX(DateTimeOffset at, Rectangle plot)
        {
            var duration = Math.Max(1L, (viewTo - viewFrom).Ticks);
            return plot.Left + plot.Width * (float)
                ((at - viewFrom).Ticks / (double)duration);
        }

        private void DrawTimeLabels(Graphics graphics, Rectangle plot,
            Font font, Brush brush)
        {
            for (var index = 0; index <= 4; index++)
            {
                var at = viewFrom.AddTicks((viewTo - viewFrom).Ticks *
                    index / 4);
                if (index == 4 && viewTo - viewFrom >= TimeSpan.FromDays(2))
                    at = at.AddTicks(-1);
                var local = at.ToLocalTime();
                var text = viewTo - viewFrom >= TimeSpan.FromDays(2)
                    ? local.ToString("M月d日", CultureInfo.CurrentCulture)
                    : local.ToString("HH:mm", CultureInfo.CurrentCulture);
                var x = plot.Left + plot.Width * index / 4f;
                var labelBounds = index == 0
                    ? new RectangleF(x, plot.Bottom + 4, 100, 18)
                    : index == 4
                        ? new RectangleF(x - 100, plot.Bottom + 4, 100, 18)
                        : new RectangleF(x - 50, plot.Bottom + 4, 100, 18);
                using (var format = new StringFormat
                {
                    Alignment = index == 0 ? StringAlignment.Near :
                        index == 4 ? StringAlignment.Far :
                            StringAlignment.Center
                })
                    graphics.DrawString(text, font, brush,
                        labelBounds, format);
            }
        }

        private static void DrawGrid(Graphics graphics, Rectangle plot,
            Color color)
        {
            using (var pen = new Pen(color, 1f))
            {
                pen.DashStyle = DashStyle.Dot;
                for (var index = 0; index <= 5; index++)
                {
                    var y = plot.Top + plot.Height * index / 5f;
                    graphics.DrawLine(pen, plot.Left, y, plot.Right, y);
                }
                for (var index = 0; index <= 4; index++)
                {
                    var x = plot.Left + plot.Width * index / 4f;
                    graphics.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                }
            }
        }

        private static void DrawCentered(Graphics graphics, string text,
            Font font, Brush brush, Rectangle bounds)
        {
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            }) graphics.DrawString(text, font, brush, bounds, format);
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalDays >= 2)
                return span.TotalDays.ToString("0.#",
                    CultureInfo.CurrentCulture) + " 天";
            if (span.TotalHours >= 1)
                return span.TotalHours.ToString("0.#",
                    CultureInfo.CurrentCulture) + " 小时";
            return Math.Max(1d, span.TotalMinutes).ToString("0",
                CultureInfo.CurrentCulture) + " 分钟";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) toolTip.Dispose();
            base.Dispose(disposing);
        }
    }
}

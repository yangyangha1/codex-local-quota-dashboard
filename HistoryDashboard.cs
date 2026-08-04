using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal enum HistorySeriesMode : byte
    {
        Total = 0,
        Input = 1,
        Output = 2,
        Cached = 3,
        Reasoning = 4
    }

    internal enum HistoryPanelClickResult : byte
    {
        None = 0,
        Redraw = 1,
        Close = 2,
        PreviousDay = 3,
        PickDate = 4,
        NextDay = 5,
        OpenStorage = 6
    }

    internal enum HistoryPanelPointerHint : byte
    {
        None = 0,
        Close = 1,
        PreviousDay = 2,
        PickDate = 3,
        NextDay = 4,
        OpenStorage = 5,
        SelectRange = 6,
        SelectSeries = 7
    }

    /// <summary>
    /// 与 ProjectDetailChart 相同的主界面内嵌视图：不创建历史窗口，
    /// 只负责绘制当天历史和处理日期、框选及缩放交互。
    /// </summary>
    internal sealed class HistoryPanelChart
    {
        private List<HistorySample> samples = new List<HistorySample>();
        private DateTime selectedDate = DateTime.Today;
        private DateTimeOffset fullFrom;
        private DateTimeOffset fullTo;
        private DateTimeOffset viewFrom;
        private DateTimeOffset viewTo;
        private RectangleF closeBounds;
        private RectangleF previousBounds;
        private RectangleF dateBounds;
        private RectangleF nextBounds;
        private RectangleF storageBounds;
        private RectangleF[] seriesBounds = new RectangleF[5];
        private RectangleF plotBounds;
        private bool selecting;
        private float selectionStartX;
        private float selectionCurrentX;
        private bool loading;
        private bool loadError;
        private string storageStatus = "0 B";
        private HistorySeriesMode seriesMode = HistorySeriesMode.Total;

        public DateTime SelectedDate { get { return selectedDate; } }
        internal TimeSpan ViewDuration { get { return viewTo - viewFrom; } }
        internal RectangleF PlotBounds { get { return plotBounds; } }

        public void SetDate(DateTime value)
        {
            selectedDate = value.Date > DateTime.Today
                ? DateTime.Today : value.Date;
            var localFrom = new DateTimeOffset(selectedDate);
            fullFrom = localFrom.ToUniversalTime();
            fullTo = localFrom.AddDays(1).ToUniversalTime();
            viewFrom = fullFrom;
            viewTo = fullTo;
            samples = new List<HistorySample>();
            selecting = false;
            loadError = false;
        }

        public void SetLoading(bool value)
        {
            loading = value;
            if (value) loadError = false;
        }

        public void SetLoadError()
        {
            loading = false;
            loadError = true;
        }

        public void SetSamples(List<HistorySample> values, long fileSize)
        {
            loading = false;
            loadError = false;
            samples = values == null ? new List<HistorySample>() :
                values.OrderBy(value => value.At).ToList();
            storageStatus = FormatFileSize(fileSize);
            ResetZoom();
        }

        public void Clear()
        {
            samples = new List<HistorySample>();
            selecting = false;
            loading = false;
            loadError = false;
            closeBounds = RectangleF.Empty;
            previousBounds = RectangleF.Empty;
            dateBounds = RectangleF.Empty;
            nextBounds = RectangleF.Empty;
            storageBounds = RectangleF.Empty;
            seriesBounds = new RectangleF[5];
            plotBounds = RectangleF.Empty;
        }

        public void ResetZoom()
        {
            viewFrom = fullFrom;
            viewTo = fullTo;
            selecting = false;
        }

        public HistoryPanelPointerHint PointerHint(PointF point)
        {
            if (closeBounds.Contains(point)) return HistoryPanelPointerHint.Close;
            if (previousBounds.Contains(point))
                return HistoryPanelPointerHint.PreviousDay;
            if (dateBounds.Contains(point)) return HistoryPanelPointerHint.PickDate;
            if (nextBounds.Contains(point) && selectedDate < DateTime.Today)
                return HistoryPanelPointerHint.NextDay;
            if (storageBounds.Contains(point))
                return HistoryPanelPointerHint.OpenStorage;
            for (var index = 0; index < seriesBounds.Length; index++)
                if (seriesBounds[index].Contains(point))
                    return HistoryPanelPointerHint.SelectSeries;
            if (plotBounds.Contains(point))
                return HistoryPanelPointerHint.SelectRange;
            return HistoryPanelPointerHint.None;
        }

        public HistoryPanelClickResult HandleClick(PointF point)
        {
            if (closeBounds.Contains(point)) return HistoryPanelClickResult.Close;
            if (previousBounds.Contains(point))
                return HistoryPanelClickResult.PreviousDay;
            if (dateBounds.Contains(point)) return HistoryPanelClickResult.PickDate;
            if (nextBounds.Contains(point) && selectedDate < DateTime.Today)
                return HistoryPanelClickResult.NextDay;
            if (storageBounds.Contains(point))
                return HistoryPanelClickResult.OpenStorage;
            for (var index = 0; index < seriesBounds.Length; index++)
            {
                if (!seriesBounds[index].Contains(point)) continue;
                seriesMode = (HistorySeriesMode)index;
                ResetZoom();
                return HistoryPanelClickResult.Redraw;
            }
            return HistoryPanelClickResult.None;
        }

        public bool BeginSelection(PointF point)
        {
            if (!plotBounds.Contains(point)) return false;
            selecting = true;
            selectionStartX = selectionCurrentX = point.X;
            return true;
        }

        public bool UpdateSelection(PointF point)
        {
            if (!selecting) return false;
            selectionCurrentX = Math.Max(plotBounds.Left,
                Math.Min(plotBounds.Right, point.X));
            return true;
        }

        public bool EndSelection(PointF point)
        {
            if (!selecting) return false;
            selectionCurrentX = Math.Max(plotBounds.Left,
                Math.Min(plotBounds.Right, point.X));
            selecting = false;
            var left = Math.Min(selectionStartX, selectionCurrentX);
            var right = Math.Max(selectionStartX, selectionCurrentX);
            if (right - left < Math.Max(6f, plotBounds.Width * .025f))
                return true;
            var newFrom = XToTime(left);
            var newTo = XToTime(right);
            if (newTo - newFrom < TimeSpan.FromMinutes(5)) return true;
            viewFrom = newFrom;
            viewTo = newTo;
            return true;
        }

        public bool Zoom(int delta, PointF point)
        {
            if (delta == 0 || !plotBounds.Contains(point) || fullTo <= fullFrom)
                return false;
            var current = viewTo - viewFrom;
            var nextTicks = (long)(current.Ticks *
                (delta > 0 ? .80d : 1.25d));
            nextTicks = Math.Max(TimeSpan.FromMinutes(5).Ticks,
                Math.Min((fullTo - fullFrom).Ticks, nextTicks));
            var anchor = XToTime(point.X);
            var ratio = (point.X - plotBounds.Left) /
                Math.Max(1d, plotBounds.Width);
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
            if (nextFrom == viewFrom && nextTo == viewTo) return false;
            viewFrom = nextFrom;
            viewTo = nextTo;
            return true;
        }

        public void Draw(Graphics graphics, RectangleF bounds,
            ThemeMode theme, float visualScale)
        {
            var light = theme == ThemeMode.Light;
            var primary = light ? Color.FromArgb(12, 19, 26) :
                Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) :
                Color.FromArgb(160, 171, 186);
            var border = light ? Color.FromArgb(153, 169, 184) :
                Color.FromArgb(72, 88, 101);
            var buttonFill = light ? Color.FromArgb(224, 236, 243) :
                Color.FromArgb(37, 49, 54);
            var grid = light ? Color.FromArgb(55, 118, 130, 143) :
                Color.FromArgb(52, 176, 188, 201);
            var tokenColor = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(92, 175, 232);
            var scale = Math.Max(.65f, bounds.Width / 292f);
            closeBounds = new RectangleF(bounds.Right - 18f * scale,
                bounds.Top, 18f * scale, 18f * scale);
            previousBounds = new RectangleF(bounds.Left,
                bounds.Top + 24f * scale, 28f * scale, 24f * scale);
            dateBounds = new RectangleF(bounds.Left + 34f * scale,
                bounds.Top + 24f * scale, 116f * scale, 24f * scale);
            nextBounds = new RectangleF(bounds.Left + 156f * scale,
                bounds.Top + 24f * scale, 28f * scale, 24f * scale);
            storageBounds = new RectangleF(bounds.Right - 102f * scale,
                bounds.Top + 24f * scale, 102f * scale, 24f * scale);
            var seriesTop = bounds.Top + 54f * scale;
            var seriesGap = 4f * scale;
            var seriesWidth = (bounds.Width - seriesGap * 4f) / 5f;
            for (var index = 0; index < seriesBounds.Length; index++)
                seriesBounds[index] = new RectangleF(bounds.Left +
                    index * (seriesWidth + seriesGap), seriesTop,
                    seriesWidth, 22f * scale);
            plotBounds = RectangleF.FromLTRB(bounds.Left,
                bounds.Top + 102f * scale, bounds.Right,
                bounds.Bottom - 18f * scale);

            using (var titleFont = new Font(Ui.FontFamilyName,
                Math.Max(6.5f, 8.2f * visualScale), FontStyle.Bold))
            using (var buttonFont = new Font(Ui.FontFamilyName,
                Math.Max(6f, 7.4f * visualScale), FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName,
                Math.Max(5.6f, 7f * visualScale), FontStyle.Regular))
            using (var primaryBrush = new SolidBrush(primary))
            using (var mutedBrush = new SolidBrush(muted))
            using (var tokenBrush = new SolidBrush(tokenColor))
            using (var borderPen = new Pen(border, Math.Max(1f, scale * .7f)))
            using (var fillBrush = new SolidBrush(buttonFill))
            using (var activeFillBrush = new SolidBrush(light
                ? Color.FromArgb(197, 224, 239)
                : Color.FromArgb(49, 77, 91)))
            using (var center = CenterFormat())
            {
                graphics.DrawString("历史数据", titleFont, primaryBrush,
                    new RectangleF(bounds.Left, bounds.Top,
                        bounds.Width - 22f * scale, 18f * scale));
                graphics.DrawString("×", titleFont, mutedBrush,
                    closeBounds, center);
                DrawButton(graphics, previousBounds, "‹", buttonFont,
                    primaryBrush, borderPen, fillBrush, center);
                DrawButton(graphics, dateBounds,
                    selectedDate.ToString("yyyy年M月d日",
                        CultureInfo.CurrentCulture), buttonFont,
                    primaryBrush, borderPen, fillBrush, center);
                DrawButton(graphics, nextBounds, "›", buttonFont,
                    selectedDate < DateTime.Today ? primaryBrush : mutedBrush,
                    borderPen, fillBrush, center);
                DrawButton(graphics, storageBounds,
                    "保存位置 · " + storageStatus, buttonFont,
                    primaryBrush, borderPen, fillBrush, center);

                var seriesNames = new[]
                    { "总量", "输入", "输出", "缓存", "推理" };
                for (var index = 0; index < seriesBounds.Length; index++)
                    DrawButton(graphics, seriesBounds[index],
                        seriesNames[index], buttonFont,
                        index == (int)seriesMode ? tokenBrush : primaryBrush,
                        borderPen, index == (int)seriesMode
                            ? activeFillBrush : fillBrush, center);

                var visible = VisibleSamples();
                var selectedTotal = visible.Aggregate(0L,
                    (total, sample) => total + ValueForSeries(sample));
                var tokenText = visible.Count == 0 ? "已记录：—" :
                    "已记录：" + Ui.Compact(selectedTotal);
                graphics.DrawString(tokenText, smallFont, tokenBrush,
                    new RectangleF(bounds.Left, bounds.Top + 82f * scale,
                        bounds.Width * .44f, 16f * scale));
                using (var middle = CenterFormat())
                    graphics.DrawString(FormatSpan(viewTo - viewFrom),
                        smallFont, mutedBrush,
                        new RectangleF(bounds.Left + bounds.Width * .38f,
                            bounds.Top + 82f * scale,
                            bounds.Width * .24f, 16f * scale), middle);
                using (var far = FarCenterFormat())
                    graphics.DrawString(visible.Count.ToString(
                            "N0", CultureInfo.CurrentCulture) + " 条记录",
                        smallFont, mutedBrush,
                        new RectangleF(bounds.Left + bounds.Width * .62f,
                            bounds.Top + 82f * scale,
                            bounds.Width * .38f, 16f * scale), far);

                DrawGrid(graphics, plotBounds, grid, scale);
                if (loading)
                {
                    DrawCentered(graphics, "正在读取当天历史…", smallFont,
                        mutedBrush, plotBounds);
                }
                else if (loadError)
                {
                    DrawCentered(graphics, "历史数据读取失败", smallFont,
                        mutedBrush, plotBounds);
                }
                else if (visible.Count < 2)
                {
                    DrawCentered(graphics, visible.Count == 0
                        ? "当天暂无历史数据" : "等待更多历史记录",
                        smallFont, mutedBrush, plotBounds);
                }
                else
                {
                    var points = Downsample(visible, 700);
                    DrawSeries(graphics, plotBounds, points, tokenColor,
                        scale);
                }
                DrawTimeLabels(graphics, plotBounds, smallFont, mutedBrush,
                    scale);
                if (selecting)
                {
                    var left = Math.Min(selectionStartX, selectionCurrentX);
                    var right = Math.Max(selectionStartX, selectionCurrentX);
                    using (var selectionBrush = new SolidBrush(
                        Color.FromArgb(light ? 42 : 55, tokenColor)))
                    using (var selectionPen = new Pen(tokenColor,
                        Math.Max(1f, scale)))
                    {
                        var selected = RectangleF.FromLTRB(left,
                            plotBounds.Top, right, plotBounds.Bottom);
                        graphics.FillRectangle(selectionBrush, selected);
                        graphics.DrawRectangle(selectionPen, selected.X,
                            selected.Y, selected.Width, selected.Height);
                    }
                }
            }
        }

        private List<HistorySample> VisibleSamples()
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

        private void DrawSeries(Graphics graphics, RectangleF plot,
            List<HistorySample> points, Color color, float scale)
        {
            var cumulative = new long[points.Count];
            long total = 0;
            for (var index = 0; index < points.Count; index++)
            {
                total += Math.Max(0L, ValueForSeries(points[index]));
                cumulative[index] = total;
            }
            var maximum = Math.Max(1L, cumulative.Max());
            using (var pen = new Pen(color, Math.Max(1.2f, 1.8f * scale)))
            {
                PointF? prior = null;
                HistorySample priorSample = null;
                for (var index = 0; index < points.Count; index++)
                {
                    var point = points[index];
                    var current = new PointF(TimeToX(point.At),
                        plot.Bottom - plot.Height *
                        cumulative[index] / (float)maximum);
                    if (prior.HasValue && priorSample != null &&
                        point.At - priorSample.At < MaxGap())
                        graphics.DrawLine(pen, prior.Value, current);
                    prior = current;
                    priorSample = point;
                }
            }
        }

        private long ValueForSeries(HistorySample sample)
        {
            switch (seriesMode)
            {
                case HistorySeriesMode.Input: return sample.DeltaInput;
                case HistorySeriesMode.Output: return sample.DeltaOutput;
                case HistorySeriesMode.Cached: return sample.DeltaCached;
                case HistorySeriesMode.Reasoning:
                    return sample.DeltaReasoning;
                default: return sample.DeltaTokens;
            }
        }

        private TimeSpan MaxGap()
        {
            return TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(3).Ticks,
                (viewTo - viewFrom).Ticks / 120));
        }

        private float TimeToX(DateTimeOffset at)
        {
            return plotBounds.Left + plotBounds.Width * (float)
                ((at - viewFrom).Ticks /
                    (double)Math.Max(1L, (viewTo - viewFrom).Ticks));
        }

        private DateTimeOffset XToTime(float x)
        {
            var ratio = (x - plotBounds.Left) /
                Math.Max(1d, plotBounds.Width);
            ratio = Math.Max(0d, Math.Min(1d, ratio));
            return viewFrom.AddTicks((long)((viewTo - viewFrom).Ticks * ratio));
        }

        private void DrawTimeLabels(Graphics graphics, RectangleF plot,
            Font font, Brush brush, float scale)
        {
            for (var index = 0; index <= 4; index++)
            {
                var at = viewFrom.AddTicks((viewTo - viewFrom).Ticks *
                    index / 4).ToLocalTime();
                var text = at.ToString("HH:mm", CultureInfo.CurrentCulture);
                var x = plot.Left + plot.Width * index / 4f;
                var width = 58f * scale;
                var label = index == 0
                    ? new RectangleF(x, plot.Bottom + 2f * scale,
                        width, 15f * scale)
                    : index == 4
                        ? new RectangleF(x - width,
                            plot.Bottom + 2f * scale, width, 15f * scale)
                        : new RectangleF(x - width / 2f,
                            plot.Bottom + 2f * scale, width, 15f * scale);
                using (var format = new StringFormat
                {
                    Alignment = index == 0 ? StringAlignment.Near :
                        index == 4 ? StringAlignment.Far :
                            StringAlignment.Center,
                    LineAlignment = StringAlignment.Near
                }) graphics.DrawString(text, font, brush, label, format);
            }
        }

        private static void DrawGrid(Graphics graphics, RectangleF plot,
            Color color, float scale)
        {
            using (var pen = new Pen(color, Math.Max(.7f, scale * .7f)))
            {
                pen.DashStyle = DashStyle.Dot;
                for (var index = 0; index <= 4; index++)
                {
                    var y = plot.Top + plot.Height * index / 4f;
                    graphics.DrawLine(pen, plot.Left, y, plot.Right, y);
                    var x = plot.Left + plot.Width * index / 4f;
                    graphics.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                }
            }
        }

        private static void DrawButton(Graphics graphics, RectangleF bounds,
            string text, Font font, Brush textBrush, Pen borderPen,
            Brush fillBrush, StringFormat format)
        {
            graphics.FillRectangle(fillBrush, bounds);
            graphics.DrawRectangle(borderPen, bounds.X, bounds.Y,
                bounds.Width, bounds.Height);
            graphics.DrawString(text, font, textBrush, bounds, format);
        }

        private static void DrawCentered(Graphics graphics, string text,
            Font font, Brush brush, RectangleF bounds)
        {
            using (var format = CenterFormat())
                graphics.DrawString(text, font, brush, bounds, format);
        }

        private static StringFormat CenterFormat()
        {
            return new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        private static StringFormat FarCenterFormat()
        {
            return new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        private static string FormatSpan(TimeSpan span)
        {
            if (span.TotalHours >= 1d)
                return span.TotalHours.ToString("0.#",
                    CultureInfo.CurrentCulture) + " 小时";
            return Math.Max(1d, span.TotalMinutes).ToString("0",
                CultureInfo.CurrentCulture) + " 分钟";
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

    /// <summary>History 内嵌视图使用的单日选择弹层，不承载历史内容。</summary>
    internal sealed class HistoryDatePickerPopup : Form
    {
        private readonly MonthCalendar calendar = new MonthCalendar();

        public HistoryDatePickerPopup(DateTime selected,
            Action<DateTime> onSelected)
        {
            FormBorderStyle = FormBorderStyle.FixedSingle;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimizeBox = false;
            MaximizeBox = false;
            Text = "选择日期";
            calendar.MaxSelectionCount = 1;
            calendar.MaxDate = DateTime.Today;
            calendar.SetDate(selected.Date > DateTime.Today
                ? DateTime.Today : selected.Date);
            calendar.Dock = DockStyle.Fill;
            Controls.Add(calendar);
            ClientSize = calendar.Size;
            calendar.DateSelected += delegate(object sender,
                DateRangeEventArgs e)
            {
                onSelected(e.Start.Date);
                Close();
            };
            Deactivate += delegate { Close(); };
        }
    }
}

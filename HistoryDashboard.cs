using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace CodexLocalDashboard
{
    internal enum HistoryPanelClickResult : byte
    {
        None = 0,
        Close = 1,
        PreviousWeek = 2,
        SelectDate = 3,
        NextWeek = 4,
        OpenStorage = 5
    }

    internal enum HistoryPanelPointerHint : byte
    {
        None = 0,
        Close = 1,
        PreviousWeek = 2,
        SelectDate = 3,
        NextWeek = 4,
        OpenStorage = 5,
        SelectRange = 6
    }

    /// <summary>
    /// History 只增加七日数据状态条；曲线、坐标、滚轮档位和框选放大
    /// 均直接复用 TokenRateChart。标题文字本身是打开历史目录的入口，
    /// 不增加额外按钮或改变标题样式。
    /// </summary>
    internal sealed class HistoryPanelChart
    {
        private readonly TokenRateChart chart = new TokenRateChart();
        private readonly HashSet<DateTime> availableDates =
            new HashSet<DateTime>();
        private readonly RectangleF[] dayBounds = new RectangleF[7];
        private DateTime selectedDate = DateTime.Today;
        private DateTime weekStart = StartOfWeek(DateTime.Today);
        private DateTime clickedDate = DateTime.Today;
        private RectangleF closeBounds;
        private RectangleF titleBounds;
        private RectangleF previousBounds;
        private RectangleF nextBounds;
        private RectangleF chartBounds;
        private bool loading;
        private bool loadError;
        private bool hasSamples;

        public HistoryPanelChart()
        {
            chart.SetDisplayHours(24);
        }

        public DateTime SelectedDate { get { return selectedDate; } }
        public DateTime ClickedDate { get { return clickedDate; } }
        public DateTime VisibleWeekStart { get { return weekStart; } }
        public int DisplayHours { get { return chart.DisplayHours; } }
        internal TimeSpan ViewDuration { get { return chart.ViewDuration; } }
        internal RectangleF PlotBounds { get { return chart.PlotBounds; } }
        internal RectangleF ChartBounds { get { return chartBounds; } }
        internal RectangleF DayBounds(int index) { return dayBounds[index]; }
        internal RectangleF PreviousWeekBounds { get { return previousBounds; } }
        internal RectangleF NextWeekBounds { get { return nextBounds; } }

        internal DateTimeOffset RequiredReadFrom
        {
            get
            {
                var from = new DateTimeOffset(selectedDate);
                return DisplayHours == 48 ? from.AddDays(-1) : from;
            }
        }

        internal DateTimeOffset RequiredReadTo
        {
            get { return new DateTimeOffset(selectedDate).AddDays(1); }
        }

        internal DateTimeOffset StatusReadFrom
        {
            get { return new DateTimeOffset(weekStart); }
        }

        internal DateTimeOffset StatusReadTo
        {
            get { return new DateTimeOffset(weekStart.AddDays(7)); }
        }

        public void SetDate(DateTime value)
        {
            selectedDate = value.Date > DateTime.Today
                ? DateTime.Today : value.Date;
            clickedDate = selectedDate;
            if (selectedDate < weekStart || selectedDate >= weekStart.AddDays(7))
                weekStart = StartOfWeek(selectedDate);
            chart.Clear();
            hasSamples = false;
            loading = false;
            loadError = false;
        }

        public bool ShiftWeek(int weeks)
        {
            var next = weekStart.AddDays(weeks * 7);
            var current = StartOfWeek(DateTime.Today);
            if (next > current) next = current;
            if (next == weekStart) return false;
            weekStart = next;
            availableDates.Clear();
            return true;
        }

        public void SetAvailableDates(IEnumerable<DateTime> values)
        {
            availableDates.Clear();
            if (values == null) return;
            foreach (var value in values) availableDates.Add(value.Date);
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

        public void SetSamples(List<HistorySample> values, long unusedFileSize)
        {
            loading = false;
            loadError = false;
            hasSamples = values != null && values.Count > 0;
            chart.LoadHistoricalSamples(values,
                RequiredReadFrom.ToUniversalTime());
        }

        public void Clear()
        {
            chart.Clear();
            availableDates.Clear();
            hasSamples = false;
            loading = false;
            loadError = false;
            closeBounds = RectangleF.Empty;
            titleBounds = RectangleF.Empty;
            previousBounds = RectangleF.Empty;
            nextBounds = RectangleF.Empty;
            chartBounds = RectangleF.Empty;
            for (var i = 0; i < dayBounds.Length; i++)
                dayBounds[i] = RectangleF.Empty;
        }

        public bool ZoomByWheel(int delta) { return chart.ZoomByWheel(delta); }

        internal bool ZoomByWheel(int delta, int tickCount)
        {
            return chart.ZoomByWheel(delta, tickCount);
        }

        public bool BeginSelection(PointF point)
        {
            return chart.BeginSelection(point);
        }

        public bool UpdateSelection(PointF point)
        {
            return chart.UpdateSelection(point);
        }

        public bool EndSelection(PointF point)
        {
            return chart.EndSelection(point);
        }

        public HistoryPanelPointerHint PointerHint(PointF point)
        {
            if (closeBounds.Contains(point)) return HistoryPanelPointerHint.Close;
            if (titleBounds.Contains(point))
                return HistoryPanelPointerHint.OpenStorage;
            if (previousBounds.Contains(point))
                return HistoryPanelPointerHint.PreviousWeek;
            if (nextBounds.Contains(point) &&
                weekStart < StartOfWeek(DateTime.Today))
                return HistoryPanelPointerHint.NextWeek;
            for (var i = 0; i < dayBounds.Length; i++)
                if (dayBounds[i].Contains(point) &&
                    availableDates.Contains(weekStart.AddDays(i)))
                    return HistoryPanelPointerHint.SelectDate;
            if (chart.PlotBounds.Contains(point))
                return HistoryPanelPointerHint.SelectRange;
            return HistoryPanelPointerHint.None;
        }

        public HistoryPanelClickResult HandleClick(PointF point)
        {
            if (closeBounds.Contains(point)) return HistoryPanelClickResult.Close;
            if (titleBounds.Contains(point))
                return HistoryPanelClickResult.OpenStorage;
            if (previousBounds.Contains(point))
                return HistoryPanelClickResult.PreviousWeek;
            if (nextBounds.Contains(point) &&
                weekStart < StartOfWeek(DateTime.Today))
                return HistoryPanelClickResult.NextWeek;
            for (var i = 0; i < dayBounds.Length; i++)
            {
                var date = weekStart.AddDays(i);
                if (!dayBounds[i].Contains(point) ||
                    !availableDates.Contains(date)) continue;
                clickedDate = date;
                return HistoryPanelClickResult.SelectDate;
            }
            return HistoryPanelClickResult.None;
        }

        public void Draw(Graphics graphics, RectangleF bounds,
            ThemeMode theme, float visualScale)
        {
            var light = theme == ThemeMode.Light;
            var primary = light ? Color.FromArgb(24, 31, 41) :
                Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) :
                Color.FromArgb(142, 153, 169);
            var blue = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(92, 175, 232);
            var disabled = light ? Color.FromArgb(213, 220, 226) :
                Color.FromArgb(55, 65, 72);
            var surface = light ? Color.FromArgb(244, 247, 249) :
                Color.FromArgb(25, 34, 37);
            var border = light ? Color.FromArgb(153, 169, 184) :
                Color.FromArgb(72, 88, 101);
            var scale = Math.Max(.65f, bounds.Width / 292f);

            closeBounds = new RectangleF(bounds.Right - 23f * scale,
                bounds.Top, 23f * scale, 20f * scale);
            previousBounds = new RectangleF(bounds.Left,
                bounds.Top + 23f * scale, 22f * scale, 20f * scale);
            nextBounds = new RectangleF(bounds.Right - 22f * scale,
                bounds.Top + 23f * scale, 22f * scale, 20f * scale);
            var daysLeft = previousBounds.Right + 4f * scale;
            var daysRight = nextBounds.Left - 4f * scale;
            var gap = 3f * scale;
            var width = (daysRight - daysLeft - gap * 6f) / 7f;
            for (var i = 0; i < dayBounds.Length; i++)
                dayBounds[i] = new RectangleF(daysLeft + i * (width + gap),
                    bounds.Top + 23f * scale, width, 20f * scale);
            chartBounds = RectangleF.FromLTRB(bounds.Left,
                bounds.Top + 49f * scale, bounds.Right, bounds.Bottom);

            using (var titleFont = new Font(Ui.FontFamilyName,
                Math.Max(5.8f, 7.6f * visualScale), FontStyle.Bold))
            using (var bodyFont = new Font(Ui.FontFamilyName,
                Math.Max(5.6f, 7.1f * visualScale), FontStyle.Bold))
            using (var dateFont = new Font(Ui.FontFamilyName,
                Math.Max(6.2f, 8.1f * visualScale), FontStyle.Regular))
            using (var dateAvailableFont = new Font(Ui.FontFamilyName,
                Math.Max(6.2f, 8.1f * visualScale), FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName,
                Math.Max(5.0f, 6.2f * visualScale)))
            using (var primaryBrush = new SolidBrush(primary))
            using (var mutedBrush = new SolidBrush(muted))
            using (var surfaceBrush = new SolidBrush(surface))
            using (var blueBrush = new SolidBrush(blue))
            using (var disabledBrush = new SolidBrush(disabled))
            using (var borderPen = new Pen(border,
                Math.Max(.65f, .8f * scale)))
            using (var bluePen = new Pen(blue,
                Math.Max(1f, 1.2f * scale)))
            using (var center = CenterFormat())
            {
                var titleSize = graphics.MeasureString("历史数据", titleFont);
                titleBounds = new RectangleF(bounds.Left, bounds.Top,
                    titleSize.Width + 2f * scale, 18f * scale);
                graphics.DrawString("历史数据", titleFont, primaryBrush,
                    new PointF(bounds.Left, bounds.Top));
                Ui.DrawEmbeddedClose(graphics, closeBounds, muted, scale);
                DrawBox(graphics, previousBounds, "‹", bodyFont,
                    primaryBrush, surfaceBrush, borderPen, center);
                DrawBox(graphics, nextBounds, "›", bodyFont,
                    weekStart < StartOfWeek(DateTime.Today)
                        ? primaryBrush : mutedBrush,
                    surfaceBrush, borderPen, center);

                for (var i = 0; i < dayBounds.Length; i++)
                {
                    var date = weekStart.AddDays(i);
                    var available = availableDates.Contains(date);
                    if (date == selectedDate)
                        graphics.DrawRectangle(bluePen, dayBounds[i].X - 1f,
                            dayBounds[i].Y - 1f, dayBounds[i].Width + 2f,
                            dayBounds[i].Height + 2f);
                    graphics.DrawString(date.ToString("M/d",
                        CultureInfo.InvariantCulture),
                        available ? dateAvailableFont : dateFont,
                        available ? blueBrush : disabledBrush,
                        dayBounds[i], center);
                }

                chart.Draw(graphics, chartBounds, theme, ChartEnd(),
                    visualScale, true);
                if (loading || loadError || !hasSamples)
                {
                    var plot = chart.PlotBounds;
                    if (!plot.IsEmpty)
                    {
                        using (var overlay = new SolidBrush(Color.FromArgb(
                            232, surface))) graphics.FillRectangle(overlay,
                                plot);
                        var message = loading
                            ? "正在加载本地历史数据…"
                            : loadError
                                ? "历史数据读取失败，请切换日期重试"
                                : "当天暂无历史数据";
                        graphics.DrawString(message, smallFont, mutedBrush,
                            plot, center);
                    }
                }
            }
        }

        private DateTimeOffset ChartEnd()
        {
            if (selectedDate == DateTime.Today) return DateTimeOffset.Now;
            return new DateTimeOffset(selectedDate).AddDays(1);
        }

        private static DateTime StartOfWeek(DateTime value)
        {
            var offset = ((int)value.DayOfWeek + 6) % 7;
            return value.Date.AddDays(-offset);
        }

        private static void DrawBox(Graphics graphics, RectangleF bounds,
            string text, Font font, Brush textBrush, Brush fillBrush,
            Pen borderPen, StringFormat format)
        {
            if (fillBrush != null) graphics.FillRectangle(fillBrush, bounds);
            graphics.DrawRectangle(borderPen, bounds.X, bounds.Y,
                bounds.Width, bounds.Height);
            graphics.DrawString(text, font, textBrush, bounds, format);
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
    }
}

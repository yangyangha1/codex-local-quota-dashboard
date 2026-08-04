using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal enum HistoryPanelClickResult : byte
    {
        None = 0,
        Close = 1,
        PreviousDay = 2,
        EditDate = 3,
        NextDay = 4,
        OpenStorage = 5
    }

    internal enum HistoryPanelPointerHint : byte
    {
        None = 0,
        Close = 1,
        PreviousDay = 2,
        EditDate = 3,
        NextDay = 4,
        OpenStorage = 5,
        SelectRange = 6
    }

    /// <summary>
    /// History 只保留日期、保存位置和加载状态。曲线、坐标、滚轮档位、
    /// 框选放大全部直接复用 TokenRateChart。
    /// </summary>
    internal sealed class HistoryPanelChart
    {
        private readonly TokenRateChart chart = new TokenRateChart();
        private DateTime selectedDate = DateTime.Today;
        private RectangleF closeBounds;
        private RectangleF previousBounds;
        private RectangleF dateBounds;
        private RectangleF nextBounds;
        private RectangleF storageBounds;
        private RectangleF chartBounds;
        private bool loading;
        private bool loadError;
        private bool hasSamples;
        private bool editingDate;
        private bool replaceDateOnType;
        private bool invalidDate;
        private string dateEditText = string.Empty;

        public HistoryPanelChart()
        {
            chart.SetDisplayHours(24);
        }

        public DateTime SelectedDate { get { return selectedDate; } }
        public int DisplayHours { get { return chart.DisplayHours; } }
        internal TimeSpan ViewDuration { get { return chart.ViewDuration; } }
        internal RectangleF PlotBounds { get { return chart.PlotBounds; } }
        internal RectangleF ChartBounds { get { return chartBounds; } }
        internal bool IsEditingDate { get { return editingDate; } }

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

        public void SetDate(DateTime value)
        {
            selectedDate = value.Date > DateTime.Today
                ? DateTime.Today : value.Date;
            chart.Clear();
            hasSamples = false;
            loading = false;
            loadError = false;
            CancelDateEdit();
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
            if (!hasSamples)
            {
                chart.LoadHistoricalIncrements(values);
                return;
            }
            var prepared = new List<HistorySample>(values);
            prepared.Sort(delegate(HistorySample left, HistorySample right)
            {
                return left.At.CompareTo(right.At);
            });
            var origin = RequiredReadFrom.ToUniversalTime();
            if (prepared[0].At > origin)
                prepared.Insert(0, new HistorySample(origin, 0, 0, 0, 0,
                    true));
            chart.LoadHistoricalIncrements(prepared);
        }

        public void Clear()
        {
            chart.Clear();
            hasSamples = false;
            loading = false;
            loadError = false;
            closeBounds = RectangleF.Empty;
            previousBounds = RectangleF.Empty;
            dateBounds = RectangleF.Empty;
            nextBounds = RectangleF.Empty;
            storageBounds = RectangleF.Empty;
            chartBounds = RectangleF.Empty;
            CancelDateEdit();
        }

        public bool ZoomByWheel(int delta)
        {
            return chart.ZoomByWheel(delta);
        }

        internal bool ZoomByWheel(int delta, int tickCount)
        {
            return chart.ZoomByWheel(delta, tickCount);
        }

        public bool BeginSelection(PointF point)
        {
            CancelDateEdit();
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
            if (previousBounds.Contains(point))
                return HistoryPanelPointerHint.PreviousDay;
            if (dateBounds.Contains(point)) return HistoryPanelPointerHint.EditDate;
            if (nextBounds.Contains(point) && selectedDate < DateTime.Today)
                return HistoryPanelPointerHint.NextDay;
            if (storageBounds.Contains(point))
                return HistoryPanelPointerHint.OpenStorage;
            if (chart.PlotBounds.Contains(point))
                return HistoryPanelPointerHint.SelectRange;
            return HistoryPanelPointerHint.None;
        }

        public HistoryPanelClickResult HandleClick(PointF point)
        {
            if (closeBounds.Contains(point)) return HistoryPanelClickResult.Close;
            if (previousBounds.Contains(point))
                return HistoryPanelClickResult.PreviousDay;
            if (dateBounds.Contains(point)) return HistoryPanelClickResult.EditDate;
            if (nextBounds.Contains(point) && selectedDate < DateTime.Today)
                return HistoryPanelClickResult.NextDay;
            if (storageBounds.Contains(point))
                return HistoryPanelClickResult.OpenStorage;
            return HistoryPanelClickResult.None;
        }

        public void BeginDateEdit()
        {
            editingDate = true;
            replaceDateOnType = true;
            invalidDate = false;
            dateEditText = selectedDate.ToString("yyyy-MM-dd",
                CultureInfo.InvariantCulture);
        }

        public void CancelDateEdit()
        {
            editingDate = false;
            replaceDateOnType = false;
            invalidDate = false;
            dateEditText = string.Empty;
        }

        public bool HandleDateCharacter(char value)
        {
            if (!editingDate) return false;
            if (value == '\b')
            {
                if (replaceDateOnType) dateEditText = string.Empty;
                else if (dateEditText.Length > 0)
                    dateEditText = dateEditText.Substring(0,
                        dateEditText.Length - 1);
                replaceDateOnType = false;
                invalidDate = false;
                return true;
            }
            if (!char.IsDigit(value) && value != '-' && value != '/')
                return false;
            if (replaceDateOnType) dateEditText = string.Empty;
            replaceDateOnType = false;
            if (dateEditText.Length < 10) dateEditText += value;
            invalidDate = false;
            return true;
        }

        public bool PasteDateText(string value)
        {
            if (!editingDate || string.IsNullOrWhiteSpace(value)) return false;
            dateEditText = value.Trim();
            replaceDateOnType = false;
            invalidDate = false;
            return true;
        }

        public bool TryCommitDate(out DateTime value)
        {
            value = selectedDate;
            if (!editingDate) return false;
            var formats = new[] { "yyyy-MM-dd", "yyyy/M/d", "yyyyMMdd" };
            DateTime parsed;
            if (!DateTime.TryParseExact(dateEditText, formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None,
                out parsed) || parsed.Date > DateTime.Today)
            {
                invalidDate = true;
                return false;
            }
            value = parsed.Date;
            CancelDateEdit();
            return true;
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
            var surface = light ? Color.FromArgb(244, 247, 249) :
                Color.FromArgb(25, 34, 37);
            var border = light ? Color.FromArgb(153, 169, 184) :
                Color.FromArgb(72, 88, 101);
            var error = Color.FromArgb(220, 76, 74);
            var scale = Math.Max(.65f, bounds.Width / 292f);

            closeBounds = new RectangleF(bounds.Right - 23f * scale,
                bounds.Top, 23f * scale, 20f * scale);
            previousBounds = new RectangleF(bounds.Left,
                bounds.Top + 25f * scale, 24f * scale, 22f * scale);
            dateBounds = new RectangleF(bounds.Left + 29f * scale,
                bounds.Top + 25f * scale, 94f * scale, 22f * scale);
            nextBounds = new RectangleF(bounds.Left + 128f * scale,
                bounds.Top + 25f * scale, 24f * scale, 22f * scale);
            storageBounds = new RectangleF(bounds.Right - 64f * scale,
                bounds.Top + 25f * scale, 64f * scale, 22f * scale);
            chartBounds = RectangleF.FromLTRB(bounds.Left,
                bounds.Top + 56f * scale, bounds.Right, bounds.Bottom);

            using (var titleFont = new Font(Ui.FontFamilyName,
                Math.Max(5.8f, 7.6f * visualScale), FontStyle.Bold))
            using (var bodyFont = new Font(Ui.FontFamilyName,
                Math.Max(5.6f, 7.1f * visualScale), FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName,
                Math.Max(5.2f, 6.5f * visualScale)))
            using (var primaryBrush = new SolidBrush(primary))
            using (var mutedBrush = new SolidBrush(muted))
            using (var errorBrush = new SolidBrush(error))
            using (var surfaceBrush = new SolidBrush(surface))
            using (var borderPen = new Pen(border,
                Math.Max(.65f, .8f * scale)))
            using (var center = CenterFormat())
            {
                graphics.DrawString("历史数据", titleFont, primaryBrush,
                    new PointF(bounds.Left, bounds.Top));
                Ui.DrawEmbeddedClose(graphics, closeBounds, muted, scale);
                DrawBox(graphics, previousBounds, "‹", bodyFont,
                    primaryBrush, surfaceBrush, borderPen, center);
                var dateText = editingDate ? dateEditText + "|" :
                    selectedDate.ToString("yyyy-MM-dd",
                        CultureInfo.InvariantCulture);
                using (var datePen = new Pen(invalidDate ? error : border,
                    Math.Max(.65f, .8f * scale)))
                    DrawBox(graphics, dateBounds, dateText, smallFont,
                        invalidDate ? errorBrush : primaryBrush,
                        surfaceBrush, datePen, center);
                if (invalidDate)
                    graphics.DrawString("日期无效", smallFont, errorBrush,
                        new RectangleF(bounds.Left + 158f * scale,
                            bounds.Top + 25f * scale,
                            Math.Max(1f, storageBounds.Left - bounds.Left -
                                163f * scale), 22f * scale), center);
                DrawBox(graphics, nextBounds, "›", bodyFont,
                    selectedDate < DateTime.Today ? primaryBrush : mutedBrush,
                    surfaceBrush, borderPen, center);
                graphics.FillRectangle(surfaceBrush, storageBounds);
                Ui.DrawLocationAction(graphics, storageBounds, "保存位置",
                    smallFont, blue, scale, center);

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
                                : "当前时间范围暂无历史数据";
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

        private static void DrawBox(Graphics graphics, RectangleF bounds,
            string text, Font font, Brush textBrush, Brush fillBrush,
            Pen borderPen, StringFormat format)
        {
            graphics.FillRectangle(fillBrush, bounds);
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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexLocalDashboard
{
    internal enum UsageChartMode : byte
    {
        CumulativeToken = 0,
        RemainingQuota = 1
    }

    /// <summary>
    /// 任务管理器风格的双模式内存折线图。
    /// 模式一：当前本地扫描得到的今日累计 Token。
    /// 模式二：最短额度窗口的剩余额度百分比。
    /// 左键点击图表切换模式；固定显示最近 6 小时；不读写配置或历史文件。
    /// </summary>
    internal sealed class TokenRateChart
    {
        private const double MinimumTokenAxisMaximum = 1000d;
        private const double AxisGrowThreshold = 0.90d;
        private const double AxisHeadroomRatio = 0.75d;
        private const double AxisShrinkThreshold = 0.35d;
        private const double AxisShrinkHeadroomRatio = 0.70d;
        private const double QuotaJitterTolerance = 0.35d;
        private const double QuotaResetRiseThreshold = 2d;

        private static readonly TimeSpan TargetWindow = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan RawSampleSlack = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan AxisShrinkDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MinimumPointSpacing = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DisplayDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan RetentionDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan RetentionSlack = TimeSpan.FromMinutes(2);

        private readonly object gate = new object();
        private readonly List<QuotaRawSample> quotaSamples =
            new List<QuotaRawSample>(8);
        private readonly List<HistoryPoint> tokenPoints =
            new List<HistoryPoint>(736);
        private readonly List<HistoryPoint> quotaPoints =
            new List<HistoryPoint>(736);

        private byte displayModeValue = (byte)UsageChartMode.CumulativeToken;
        private double tokenAxisMaximum = MinimumTokenAxisMaximum;
        private DateTimeOffset? lowTokenUsageSince;
        private DateTimeOffset? lastCaptureAt;

        private double? lastCumulativeToken;
        private bool breakBeforeNextTokenPoint;
        private bool hasTokenSource;
        private long lastTokenSource;

        private double? lastQuotaRemaining;
        private bool breakBeforeNextQuotaPoint;
        private bool hasQuotaSource;
        private double lastQuotaSource;
        private int quotaWindowMinutes;
        private DateTimeOffset? quotaResetsAt;

        public UsageChartMode DisplayMode
        {
            get
            {
                lock (gate) return (UsageChartMode)displayModeValue;
            }
            set
            {
                lock (gate) displayModeValue = (byte)value;
            }
        }

        /// <summary>左键点击图表时调用；模式只保存在当前进程内。</summary>
        public void ToggleMode()
        {
            lock (gate)
            {
                displayModeValue = displayModeValue == (byte)UsageChartMode.CumulativeToken
                    ? (byte)UsageChartMode.RemainingQuota
                    : (byte)UsageChartMode.CumulativeToken;
            }
        }

        /// <summary>
        /// 录入一次成功扫描。
        /// cumulativeTokens 应为 input_tokens + output_tokens。
        /// remainingPercent 为最短额度窗口剩余百分比；无有效额度时传 null。
        /// </summary>
        public void Capture(DateTimeOffset capturedAt, long cumulativeTokens,
            double? remainingPercent, int windowMinutes, DateTimeOffset? resetsAt)
        {
            lock (gate)
            {
                PruneLocked(capturedAt);

                if (lastCaptureAt.HasValue && capturedAt <= lastCaptureAt.Value)
                {
                    if (capturedAt == lastCaptureAt.Value) return;
                    ResetAllLocked(capturedAt);
                }

                if (lastCaptureAt.HasValue &&
                    capturedAt - lastCaptureAt.Value > MaximumContinuousGap)
                {
                    quotaSamples.Clear();
                    lastCumulativeToken = null;
                    hasTokenSource = false;
                    lastQuotaRemaining = null;
                    hasQuotaSource = false;
                    breakBeforeNextTokenPoint = true;
                    breakBeforeNextQuotaPoint = true;
                }
                lastCaptureAt = capturedAt;

                CaptureTokenLocked(capturedAt, cumulativeTokens);
                CaptureQuotaLocked(capturedAt, remainingPercent, windowMinutes,
                    resetsAt);
            }
        }

        /// <summary>
        /// 扫描失败时调用。两种曲线都保持上一有效值；长时间断档则断开曲线。
        /// </summary>
        public void CaptureFailure(DateTimeOffset capturedAt)
        {
            lock (gate)
            {
                PruneLocked(capturedAt);
                if (lastCaptureAt.HasValue && capturedAt <= lastCaptureAt.Value)
                {
                    if (capturedAt == lastCaptureAt.Value) return;
                    ResetAllLocked(capturedAt);
                    return;
                }

                if (lastCaptureAt.HasValue &&
                    capturedAt - lastCaptureAt.Value > MaximumContinuousGap)
                {
                    quotaSamples.Clear();
                    lastCumulativeToken = null;
                    hasTokenSource = false;
                    lastQuotaRemaining = null;
                    hasQuotaSource = false;
                    breakBeforeNextTokenPoint = true;
                    breakBeforeNextQuotaPoint = true;
                    lastCaptureAt = capturedAt;
                    return;
                }

                lastCaptureAt = capturedAt;
                AppendTokenHoldLocked(capturedAt);
                AppendQuotaHoldLocked(capturedAt);
                PruneRawSamplesLocked(capturedAt);
            }
        }

        public void Clear()
        {
            lock (gate) ResetAllLocked(null);
        }

        public void Draw(Graphics graphics, RectangleF bounds, ThemeMode theme,
            DateTimeOffset now, float visualScale)
        {
            if (graphics == null || bounds.Width < 40f || bounds.Height < 50f) return;

            RenderSnapshot snapshot;
            lock (gate)
            {
                PruneLocked(now);
                RecalculateTokenAxisLocked(now, false);
                snapshot = BuildRenderSnapshotLocked(now);
            }

            DrawSnapshot(graphics, bounds, theme, snapshot,
                Math.Max(0.65f, Math.Min(2.5f, visualScale)));
        }

        public void Draw(Graphics graphics, RectangleF bounds, ThemeMode theme,
            DateTimeOffset now)
        {
            Draw(graphics, bounds, theme, now, 1f);
        }

        private void CaptureTokenLocked(DateTimeOffset at, long cumulativeTokens)
        {
            if (cumulativeTokens < 0)
            {
                AppendTokenHoldLocked(at);
                return;
            }

            if (!hasTokenSource)
            {
                hasTokenSource = true;
                lastTokenSource = cumulativeTokens;
                AppendTokenPointLocked(at, cumulativeTokens, true);
                return;
            }

            var sourceWentBackwards = cumulativeTokens < lastTokenSource;
            lastTokenSource = cumulativeTokens;

            if (sourceWentBackwards)
            {
                // 跨午夜或本地统计源回退：断开旧线，从新值开始。
                AppendTokenPointLocked(at, cumulativeTokens, true);
                return;
            }

            AppendTokenPointLocked(at, cumulativeTokens, false);
        }

        private void CaptureQuotaLocked(DateTimeOffset at, double? remainingPercent,
            int windowMinutes, DateTimeOffset? resetsAt)
        {
            if (!remainingPercent.HasValue || windowMinutes <= 0)
            {
                AppendQuotaHoldLocked(at);
                return;
            }

            var remaining = remainingPercent.Value;
            if (double.IsNaN(remaining) || double.IsInfinity(remaining) ||
                remaining < -0.01d || remaining > 100.01d)
            {
                AppendQuotaHoldLocked(at);
                return;
            }
            remaining = Math.Max(0d, Math.Min(100d, remaining));

            if (!hasQuotaSource)
            {
                StartQuotaWindowLocked(at, remaining, windowMinutes, resetsAt,
                    false);
                return;
            }

            var identityChanged = windowMinutes != quotaWindowMinutes ||
                !NullableDateEquals(resetsAt, quotaResetsAt);
            var rise = remaining - lastQuotaSource;

            if (identityChanged || rise > QuotaResetRiseThreshold)
            {
                // 额度重置、窗口切换，或没有可靠重置时间但剩余额度明显回升：
                // 断开旧线并重新收集 60 秒，避免误画成消耗反向尖峰。
                StartQuotaWindowLocked(at, remaining, windowMinutes, resetsAt,
                    true);
                return;
            }

            if (rise > QuotaJitterTolerance)
            {
                // 同一重置周期内的小幅回升通常是缓存切换或舍入抖动。
                // 更新源基线但保持图形水平，避免多次小抖动累计成假重置。
                lastQuotaSource = remaining;
                AppendQuotaHoldLocked(at);
                return;
            }

            lastQuotaSource = remaining;
            quotaSamples.Add(new QuotaRawSample(at, remaining));
            PruneQuotaSamplesLocked(at);

            QuotaRawSample baseline;
            if (!TryFindQuotaBaselineLocked(at, out baseline))
            {
                AppendQuotaHoldLocked(at);
                return;
            }

            AppendQuotaPointLocked(at, remaining, false);
        }

        private void StartQuotaWindowLocked(DateTimeOffset at, double remaining,
            int windowMinutes, DateTimeOffset? resetsAt, bool breakBefore)
        {
            quotaSamples.Clear();
            quotaSamples.Add(new QuotaRawSample(at, remaining));
            hasQuotaSource = true;
            lastQuotaSource = remaining;
            quotaWindowMinutes = windowMinutes;
            quotaResetsAt = resetsAt;
            lastQuotaRemaining = null;
            if (breakBefore) breakBeforeNextQuotaPoint = true;
        }

        private void ResetAllLocked(DateTimeOffset? captureAt)
        {
            quotaSamples.Clear();
            tokenPoints.Clear();
            quotaPoints.Clear();
            tokenAxisMaximum = MinimumTokenAxisMaximum;
            lowTokenUsageSince = null;
            lastCaptureAt = captureAt;

            lastCumulativeToken = null;
            breakBeforeNextTokenPoint = false;
            hasTokenSource = false;
            lastTokenSource = 0;

            lastQuotaRemaining = null;
            breakBeforeNextQuotaPoint = false;
            hasQuotaSource = false;
            lastQuotaSource = 0d;
            quotaWindowMinutes = 0;
            quotaResetsAt = null;
        }

        private void AppendTokenHoldLocked(DateTimeOffset at)
        {
            if (!lastCumulativeToken.HasValue) return;
            if (HasLongGap(tokenPoints, at))
            {
                breakBeforeNextTokenPoint = true;
                return;
            }
            AppendTokenPointLocked(at, lastCumulativeToken.Value, false);
        }

        private void AppendQuotaHoldLocked(DateTimeOffset at)
        {
            if (!lastQuotaRemaining.HasValue) return;
            if (HasLongGap(quotaPoints, at))
            {
                breakBeforeNextQuotaPoint = true;
                return;
            }
            AppendQuotaPointLocked(at, lastQuotaRemaining.Value, false);
        }

        private static bool HasLongGap(List<HistoryPoint> points, DateTimeOffset at)
        {
            return points.Count > 0 &&
                at - points[points.Count - 1].At > MaximumContinuousGap;
        }

        private void AppendTokenPointLocked(DateTimeOffset at, double value,
            bool forceBreakBefore)
        {
            AppendPointLocked(tokenPoints, at, value,
                forceBreakBefore || breakBeforeNextTokenPoint);
            lastCumulativeToken = value;
            breakBeforeNextTokenPoint = false;
            RecalculateTokenAxisLocked(at, false);
        }

        private void AppendQuotaPointLocked(DateTimeOffset at, double value,
            bool forceBreakBefore)
        {
            AppendPointLocked(quotaPoints, at, value,
                forceBreakBefore || breakBeforeNextQuotaPoint);
            lastQuotaRemaining = value;
            breakBeforeNextQuotaPoint = false;
        }

        private static void AppendPointLocked(List<HistoryPoint> points,
            DateTimeOffset at, double value, bool breakBefore)
        {
            if (points.Count > 0)
            {
                var previous = points[points.Count - 1];
                if (at <= previous.At) return;
                if (at - previous.At > MaximumContinuousGap) breakBefore = true;

                if (at - previous.At < MinimumPointSpacing)
                {
                    points[points.Count - 1] = new HistoryPoint(at, value,
                        previous.BreakBefore || breakBefore);
                    return;
                }
            }
            points.Add(new HistoryPoint(at, value, breakBefore));
        }

        private bool TryFindQuotaBaselineLocked(DateTimeOffset currentAt,
            out QuotaRawSample baseline)
        {
            baseline = default(QuotaRawSample);
            var found = false;
            var bestDifference = double.MaxValue;
            for (var i = 0; i < quotaSamples.Count - 1; i++)
            {
                var candidate = quotaSamples[i];
                var elapsed = currentAt - candidate.At;
                if (elapsed < MinimumWindow || elapsed > MaximumWindow) continue;
                var difference = Math.Abs(elapsed.TotalSeconds -
                    TargetWindow.TotalSeconds);
                if (difference >= bestDifference) continue;
                bestDifference = difference;
                baseline = candidate;
                found = true;
            }
            return found;
        }

        private void PruneRawSamplesLocked(DateTimeOffset now)
        {
            PruneQuotaSamplesLocked(now);
        }

        private void PruneQuotaSamplesLocked(DateTimeOffset now)
        {
            var oldest = now - MaximumWindow - RawSampleSlack;
            var count = 0;
            while (count < quotaSamples.Count && quotaSamples[count].At < oldest)
                count++;
            if (count > 0) quotaSamples.RemoveRange(0, count);
        }

        private void PruneLocked(DateTimeOffset now)
        {
            var oldest = now - RetentionDuration - RetentionSlack;
            PrunePoints(tokenPoints, oldest);
            PrunePoints(quotaPoints, oldest);
            PruneRawSamplesLocked(now);
        }

        private static void PrunePoints(List<HistoryPoint> points,
            DateTimeOffset oldest)
        {
            var count = 0;
            while (count < points.Count && points[count].At < oldest) count++;
            if (count > 0) points.RemoveRange(0, count);
        }

        private void RecalculateTokenAxisLocked(DateTimeOffset now, bool force)
        {
            var visibleFrom = now - DisplayDuration;
            var peak = 0d;
            for (var i = 0; i < tokenPoints.Count; i++)
            {
                var point = tokenPoints[i];
                if (point.At < visibleFrom || point.At > now) continue;
                if (point.Value > peak) peak = point.Value;
            }

            if (force && peak <= 0d)
            {
                tokenAxisMaximum = MinimumTokenAxisMaximum;
                lowTokenUsageSince = null;
                return;
            }

            if (peak > tokenAxisMaximum * AxisGrowThreshold)
            {
                tokenAxisMaximum = NiceCeiling(Math.Max(MinimumTokenAxisMaximum,
                    peak / AxisHeadroomRatio));
                lowTokenUsageSince = null;
                return;
            }

            if (peak < tokenAxisMaximum * AxisShrinkThreshold)
            {
                if (!lowTokenUsageSince.HasValue || now < lowTokenUsageSince.Value)
                {
                    lowTokenUsageSince = now;
                    return;
                }
                if (now - lowTokenUsageSince.Value < AxisShrinkDelay) return;

                var target = NiceCeiling(Math.Max(MinimumTokenAxisMaximum,
                    peak / AxisShrinkHeadroomRatio));
                var reduced = Math.Max(target,
                    PreviousNiceStep(tokenAxisMaximum));
                if (reduced < tokenAxisMaximum) tokenAxisMaximum = reduced;
                lowTokenUsageSince = now;
                return;
            }

            lowTokenUsageSince = null;
        }

        private RenderSnapshot BuildRenderSnapshotLocked(DateTimeOffset now)
        {
            var mode = (UsageChartMode)displayModeValue;
            var source = mode == UsageChartMode.RemainingQuota
                ? quotaPoints : tokenPoints;
            var selected = new List<HistoryPoint>();
            var visibleFrom = now - DisplayDuration;
            double? current = null;
            var segmentStart = 0d;
            var hasSegmentStart = false;

            for (var i = 0; i < source.Count; i++)
            {
                var point = source[i];
                if (point.At < visibleFrom || point.At > now) continue;
                selected.Add(point);
            }

            selected.Sort(delegate(HistoryPoint left, HistoryPoint right)
            {
                return left.At.CompareTo(right.At);
            });
            for (var i = 0; i < selected.Count; i++)
            {
                var point = selected[i];
                current = point.Value;
                if (!hasSegmentStart || point.BreakBefore)
                {
                    segmentStart = point.Value;
                    hasSegmentStart = true;
                }
            }

            var axisMinimum = 0d;
            var axisMaximum = mode == UsageChartMode.RemainingQuota
                ? 100d : Math.Max(MinimumTokenAxisMaximum, tokenAxisMaximum);
            var secondary = 0d;
            if (current.HasValue && hasSegmentStart)
            {
                if (mode == UsageChartMode.RemainingQuota)
                    secondary = Math.Max(0d, segmentStart - current.Value);
                else
                    secondary = Math.Max(0d, current.Value - segmentStart);
            }

            return new RenderSnapshot(mode, selected, axisMinimum,
                axisMaximum, current, secondary, now);
        }

        private static void DrawSnapshot(Graphics graphics, RectangleF bounds,
            ThemeMode theme, RenderSnapshot snapshot, float visualScale)
        {
            var light = theme == ThemeMode.Light;
            var primary = light ? Color.FromArgb(32, 38, 45) :
                Color.FromArgb(238, 243, 248);
            var muted = light ? Color.FromArgb(94, 105, 117) :
                Color.FromArgb(142, 153, 169);
            var grid = light ? Color.FromArgb(45, 118, 130, 143) :
                Color.FromArgb(42, 176, 188, 201);
            var separator = light ? Color.FromArgb(211, 216, 224) :
                Color.FromArgb(42, 47, 58);

            var currentQuota = snapshot.CurrentValue.HasValue
                ? snapshot.CurrentValue.Value : 100d;
            var line = snapshot.Mode == UsageChartMode.RemainingQuota
                ? Ui.QuotaColor(currentQuota)
                : (light ? Color.FromArgb(27, 151, 101) :
                    Color.FromArgb(75, 205, 143));
            var fill = Color.FromArgb(light ? 34 : 46, line);

            var geometryScale = Math.Max(0.65f, bounds.Width / 292f);
            var headerHeight = 22f * geometryScale;
            var plot = RectangleF.FromLTRB(bounds.Left,
                bounds.Top + headerHeight + 3f * geometryScale,
                bounds.Right, bounds.Bottom);
            if (plot.Width < 20f || plot.Height < 20f) return;

            using (var separatorPen = new Pen(separator,
                Math.Max(1f, geometryScale)))
                graphics.DrawLine(separatorPen, bounds.Left, bounds.Top,
                    bounds.Right, bounds.Top);

            using (var titleFont = new Font(Ui.FontFamilyName,
                Math.Max(6f, 8.5f * visualScale), FontStyle.Bold))
            using (var valueFont = new Font(Ui.FontFamilyName,
                Math.Max(6f, 8.5f * visualScale), FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName,
                Math.Max(5.5f, 7f * visualScale), FontStyle.Regular))
            using (var primaryBrush = new SolidBrush(primary))
            using (var mutedBrush = new SolidBrush(muted))
            using (var nearCenter = Format(StringAlignment.Near,
                StringAlignment.Center))
            using (var farCenter = Format(StringAlignment.Far,
                StringAlignment.Center))
            using (var center = Format(StringAlignment.Center,
                StringAlignment.Center))
            using (var farNear = Format(StringAlignment.Far,
                StringAlignment.Near))
            using (var farFar = Format(StringAlignment.Far,
                StringAlignment.Far))
            {
                var inset = 2f * geometryScale;
                var title = snapshot.Mode == UsageChartMode.RemainingQuota
                    ? "剩余额度" : "累计 Token";

                string currentText;
                if (!snapshot.CurrentValue.HasValue) currentText = "收集中";
                else if (snapshot.Mode == UsageChartMode.RemainingQuota)
                    currentText = FormatPercent(snapshot.CurrentValue.Value);
                else
                    currentText = FormatTokenCount(snapshot.CurrentValue.Value);
                DrawHeaderPair(graphics, bounds, inset, headerHeight,
                    title, currentText, titleFont, valueFont,
                    primaryBrush, geometryScale);

                DrawGrid(graphics, plot, grid, geometryScale);
                var curveDrawn = snapshot.Points.Count >= 2 &&
                    DrawCurve(graphics, plot, snapshot, line, fill,
                        geometryScale);

                if (snapshot.Mode == UsageChartMode.RemainingQuota)
                {
                    if (snapshot.SecondaryValue > 0d)
                    {
                        graphics.DrawString("当前段下降 " +
                            snapshot.SecondaryValue.ToString("0.#",
                                CultureInfo.InvariantCulture) + " 个百分点",
                            smallFont, mutedBrush,
                            new RectangleF(plot.Left + 3f * geometryScale,
                                plot.Top + geometryScale, plot.Width * 0.70f,
                                13f * geometryScale), nearCenter);
                    }
                    graphics.DrawString("100%", smallFont, mutedBrush,
                        new RectangleF(plot.Left, plot.Top + geometryScale,
                            plot.Width - 3f * geometryScale,
                            13f * geometryScale), farNear);
                    graphics.DrawString("0%", smallFont, mutedBrush,
                        new RectangleF(plot.Left, plot.Bottom -
                            13f * geometryScale, plot.Width -
                            3f * geometryScale, 13f * geometryScale), farFar);
                }
                else
                {
                    if (snapshot.SecondaryValue > 0d)
                    {
                        graphics.DrawString("当前段增加 " +
                            FormatTokenCount(snapshot.SecondaryValue),
                            smallFont, mutedBrush,
                            new RectangleF(plot.Left + 3f * geometryScale,
                                plot.Top + geometryScale, plot.Width * 0.55f,
                                13f * geometryScale), nearCenter);
                    }
                    graphics.DrawString("上限 " +
                        FormatTokenCount(snapshot.AxisMaximum),
                        smallFont, mutedBrush,
                        new RectangleF(plot.Left, plot.Top + geometryScale,
                            plot.Width - 3f * geometryScale,
                            13f * geometryScale), farNear);
                }

                if (!curveDrawn)
                {
                    string status;
                    if (snapshot.Mode == UsageChartMode.RemainingQuota)
                    {
                        status = snapshot.Points.Count == 0
                            ? "等待首个 60 秒有效窗口"
                            : "等待第二个连续点位";
                    }
                    else
                    {
                        status = snapshot.Points.Count == 0
                            ? "等待累计数据"
                            : "等待第二个连续点位";
                    }
                    graphics.DrawString(status, smallFont, mutedBrush, plot,
                        center);
                }
            }
        }

        private static void DrawHeaderPair(Graphics graphics, RectangleF bounds,
            float inset, float headerHeight, string title, string value,
            Font titleFont, Font valueFont, Brush brush, float geometryScale)
        {
            var gap = Math.Max(4f, 6f * geometryScale);
            var valueMeasured = graphics.MeasureString(value, valueFont).Width;
            var minimumValueWidth = bounds.Width * 0.24f;
            var maximumValueWidth = bounds.Width * 0.58f;
            var valueWidth = Math.Max(minimumValueWidth,
                Math.Min(maximumValueWidth,
                    valueMeasured + Math.Max(4f, 6f * geometryScale)));
            var titleWidth = Math.Max(1f, bounds.Width - valueWidth - gap);

            using (var titleFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            using (var valueFormat = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            })
            {
                graphics.DrawString(title, titleFont, brush,
                    new RectangleF(bounds.Left, bounds.Top + inset,
                        titleWidth, headerHeight), titleFormat);
                graphics.DrawString(value, valueFont, brush,
                    new RectangleF(bounds.Right - valueWidth,
                        bounds.Top + inset, valueWidth, headerHeight),
                    valueFormat);
            }
        }

        private static void DrawGrid(Graphics graphics, RectangleF plot,
            Color color, float geometryScale)
        {
            using (var pen = new Pen(color,
                Math.Max(1f, geometryScale * 0.75f)))
            {
                pen.DashStyle = DashStyle.Dot;
                for (var i = 0; i <= 5; i++)
                {
                    var y = plot.Top + plot.Height * i / 5f;
                    graphics.DrawLine(pen, plot.Left, y, plot.Right, y);
                }
                for (var i = 0; i <= 6; i++)
                {
                    var x = plot.Left + plot.Width * i / 6f;
                    graphics.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                }
            }
        }

        private static bool DrawCurve(Graphics graphics, RectangleF plot,
            RenderSnapshot snapshot, Color lineColor, Color fillColor,
            float geometryScale)
        {
            var from = snapshot.Now - DisplayDuration;
            var seconds = DisplayDuration.TotalSeconds;
            var segments = new List<List<PlotPoint>>();
            List<PlotPoint> segment = null;
            var axisSpan = snapshot.AxisMaximum - snapshot.AxisMinimum;
            if (axisSpan <= 0d) return false;

            for (var i = 0; i < snapshot.Points.Count; i++)
            {
                var source = snapshot.Points[i];
                var ratioX = (source.At - from).TotalSeconds / seconds;
                if (ratioX < 0d || ratioX > 1d) continue;
                if (segment == null || source.BreakBefore)
                {
                    segment = new List<PlotPoint>();
                    segments.Add(segment);
                }

                var x = plot.Left + (float)(ratioX * plot.Width);
                var ratioY = (source.Value - snapshot.AxisMinimum) / axisSpan;
                ratioY = Math.Max(0d, Math.Min(1d, ratioY));
                var y = plot.Bottom - (float)(ratioY * plot.Height);
                segment.Add(new PlotPoint(x, y, source.At));
            }

            if (segments.Count == 0) return false;

            var state = graphics.Save();
            var drew = false;
            try
            {
                graphics.SetClip(plot);
                for (var i = 0; i < segments.Count; i++)
                {
                    if (segments[i].Count < 2) continue;
                    var reduced = ReduceByPixelColumn(segments[i], plot);
                    reduced = EnsureStrictlyIncreasingX(reduced, plot);
                    if (reduced.Count < 2) continue;

                    if (snapshot.Mode == UsageChartMode.RemainingQuota)
                    {
                        // 额度是离散缓存快照，使用真实折线，不做平滑和面积填充，
                        // 避免制造不存在的中间值或过冲。
                        var linePoints = new PointF[reduced.Count];
                        for (var j = 0; j < reduced.Count; j++)
                            linePoints[j] = new PointF(reduced[j].X, reduced[j].Y);
                        using (var pen = new Pen(lineColor,
                            Math.Max(1.25f, 1.65f * geometryScale)))
                        {
                            pen.LineJoin = LineJoin.Round;
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            graphics.DrawLines(pen, linePoints);
                        }
                        drew = true;
                        continue;
                    }

                    using (var curve = BuildMonotoneCurve(reduced))
                    {
                        if (curve.PointCount < 2) continue;
                        using (var area = (GraphicsPath)curve.Clone())
                        {
                            var first = reduced[0];
                            var last = reduced[reduced.Count - 1];
                            area.AddLine(last.X, last.Y, last.X, plot.Bottom);
                            area.AddLine(last.X, plot.Bottom, first.X,
                                plot.Bottom);
                            area.CloseFigure();
                            using (var brush = new SolidBrush(fillColor))
                                graphics.FillPath(brush, area);
                        }

                        using (var pen = new Pen(lineColor,
                            Math.Max(1.25f, 1.65f * geometryScale)))
                        {
                            pen.LineJoin = LineJoin.Round;
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            graphics.DrawPath(pen, curve);
                        }
                        drew = true;
                    }
                }
            }
            finally
            {
                graphics.Restore(state);
            }
            return drew;
        }

        private static List<PlotPoint> ReduceByPixelColumn(
            List<PlotPoint> source, RectangleF plot)
        {
            if (source.Count <= Math.Max(32,
                (int)Math.Ceiling(plot.Width * 1.8f))) return source;

            var output = new List<PlotPoint>(
                (int)Math.Ceiling(plot.Width * 4f) + 4);
            var index = 0;
            while (index < source.Count)
            {
                var bucket = (int)Math.Floor(source[index].X - plot.Left);
                var end = index + 1;
                while (end < source.Count &&
                    (int)Math.Floor(source[end].X - plot.Left) == bucket) end++;

                var minIndex = index;
                var maxIndex = index;
                for (var i = index + 1; i < end; i++)
                {
                    if (source[i].Y < source[minIndex].Y) minIndex = i;
                    if (source[i].Y > source[maxIndex].Y) maxIndex = i;
                }

                var indices = new List<int>(4);
                AddIndex(indices, index);
                AddIndex(indices, minIndex);
                AddIndex(indices, maxIndex);
                AddIndex(indices, end - 1);
                indices.Sort();
                for (var i = 0; i < indices.Count; i++)
                    AddPoint(output, source[indices[i]]);
                index = end;
            }
            AddPoint(output, source[source.Count - 1]);
            return output;
        }

        private static void AddIndex(List<int> values, int value)
        {
            for (var i = 0; i < values.Count; i++)
                if (values[i] == value) return;
            values.Add(value);
        }

        private static void AddPoint(List<PlotPoint> output, PlotPoint point)
        {
            if (output.Count == 0)
            {
                output.Add(point);
                return;
            }
            var last = output[output.Count - 1];
            if (point.At == last.At) output[output.Count - 1] = point;
            else output.Add(point);
        }

        private static List<PlotPoint> EnsureStrictlyIncreasingX(
            List<PlotPoint> source, RectangleF plot)
        {
            if (source.Count < 2) return source;
            var output = new List<PlotPoint>(source.Count);
            var minimumStep = Math.Max(0.01f, plot.Width / 100000f);
            for (var i = 0; i < source.Count; i++)
            {
                var point = source[i];
                if (output.Count == 0)
                {
                    output.Add(point);
                    continue;
                }
                var previous = output[output.Count - 1];
                if (point.X <= previous.X)
                {
                    if (point.At <= previous.At)
                    {
                        output[output.Count - 1] = point;
                        continue;
                    }
                    point = new PlotPoint(previous.X + minimumStep, point.Y,
                        point.At);
                }
                if (point.X <= plot.Right + minimumStep) output.Add(point);
            }
            return output;
        }

        private static GraphicsPath BuildMonotoneCurve(List<PlotPoint> points)
        {
            var path = new GraphicsPath();
            var count = points.Count;
            if (count < 2) return path;

            var h = new double[count - 1];
            var delta = new double[count - 1];
            var tangent = new double[count];
            for (var i = 0; i < count - 1; i++)
            {
                h[i] = points[i + 1].X - points[i].X;
                if (h[i] <= 0d) h[i] = 0.001d;
                delta[i] = (points[i + 1].Y - points[i].Y) / h[i];
            }

            if (count == 2)
            {
                tangent[0] = delta[0];
                tangent[1] = delta[0];
            }
            else
            {
                tangent[0] = EndpointTangent(h[0], h[1], delta[0],
                    delta[1]);
                tangent[count - 1] = EndpointTangent(h[count - 2],
                    h[count - 3], delta[count - 2], delta[count - 3]);

                for (var i = 1; i < count - 1; i++)
                {
                    if (delta[i - 1] == 0d || delta[i] == 0d ||
                        Math.Sign(delta[i - 1]) != Math.Sign(delta[i]))
                    {
                        tangent[i] = 0d;
                        continue;
                    }
                    var w1 = 2d * h[i] + h[i - 1];
                    var w2 = h[i] + 2d * h[i - 1];
                    tangent[i] = (w1 + w2) /
                        (w1 / delta[i - 1] + w2 / delta[i]);
                }
            }

            path.StartFigure();
            for (var i = 0; i < count - 1; i++)
            {
                var width = h[i];
                var p0 = points[i];
                var p3 = points[i + 1];
                var c1 = new PointF(p0.X + (float)(width / 3d),
                    p0.Y + (float)(tangent[i] * width / 3d));
                var c2 = new PointF(p3.X - (float)(width / 3d),
                    p3.Y - (float)(tangent[i + 1] * width / 3d));
                path.AddBezier(new PointF(p0.X, p0.Y), c1, c2,
                    new PointF(p3.X, p3.Y));
            }
            return path;
        }

        private static double EndpointTangent(double firstWidth,
            double secondWidth, double firstSlope, double secondSlope)
        {
            var tangent = ((2d * firstWidth + secondWidth) * firstSlope -
                firstWidth * secondSlope) / (firstWidth + secondWidth);
            if (Math.Sign(tangent) != Math.Sign(firstSlope)) return 0d;
            if (Math.Sign(firstSlope) != Math.Sign(secondSlope) &&
                Math.Abs(tangent) > Math.Abs(3d * firstSlope))
                return 3d * firstSlope;
            return tangent;
        }

        private static double NiceCeiling(double value)
        {
            if (value <= MinimumTokenAxisMaximum)
                return MinimumTokenAxisMaximum;
            var exponent = Math.Floor(Math.Log10(value));
            var scale = Math.Pow(10d, exponent);
            var normalized = value / scale;
            double step;
            if (normalized <= 1d) step = 1d;
            else if (normalized <= 2.5d) step = 2.5d;
            else if (normalized <= 5d) step = 5d;
            else step = 10d;
            return step * scale;
        }

        private static double PreviousNiceStep(double current)
        {
            if (current <= MinimumTokenAxisMaximum)
                return MinimumTokenAxisMaximum;
            var exponent = Math.Floor(Math.Log10(current));
            var scale = Math.Pow(10d, exponent);
            var normalized = current / scale;
            double previous;
            if (normalized > 5d + 0.0001d) previous = 5d * scale;
            else if (normalized > 2.5d + 0.0001d) previous = 2.5d * scale;
            else if (normalized > 1d + 0.0001d) previous = 1d * scale;
            else previous = 5d * scale / 10d;
            return Math.Max(MinimumTokenAxisMaximum, previous);
        }

        private static bool NullableDateEquals(DateTimeOffset? left,
            DateTimeOffset? right)
        {
            if (!left.HasValue && !right.HasValue) return true;
            if (!left.HasValue || !right.HasValue) return false;
            return left.Value.Equals(right.Value);
        }

        private static string FormatTokenCount(double value)
        {
            var absolute = Math.Abs(value);
            if (absolute >= 1000000000000000d)
                return (value / 1000000000000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "P";
            if (absolute >= 1000000000000d)
                return (value / 1000000000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "T";
            if (absolute >= 1000000000d)
                return (value / 1000000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "B";
            if (absolute >= 1000000d)
                return (value / 1000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "M";
            if (absolute >= 1000d)
                return (value / 1000d).ToString("0.#",
                    CultureInfo.InvariantCulture) + "K";
            return Math.Round(value).ToString("N0",
                CultureInfo.InvariantCulture);
        }

        private static string FormatPercent(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static StringFormat Format(StringAlignment horizontal,
            StringAlignment vertical)
        {
            return new StringFormat
            {
                Alignment = horizontal,
                LineAlignment = vertical,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };
        }

        private struct QuotaRawSample
        {
            public readonly DateTimeOffset At;
            public readonly double RemainingPercent;
            public QuotaRawSample(DateTimeOffset at, double remainingPercent)
            {
                At = at;
                RemainingPercent = remainingPercent;
            }
        }

        private struct HistoryPoint
        {
            public readonly DateTimeOffset At;
            public readonly double Value;
            public readonly bool BreakBefore;
            public HistoryPoint(DateTimeOffset at, double value,
                bool breakBefore)
            {
                At = at;
                Value = value;
                BreakBefore = breakBefore;
            }
        }

        private sealed class RenderSnapshot
        {
            public readonly UsageChartMode Mode;
            public readonly List<HistoryPoint> Points;
            public readonly double AxisMinimum;
            public readonly double AxisMaximum;
            public readonly double? CurrentValue;
            public readonly double SecondaryValue;
            public readonly DateTimeOffset Now;

            public RenderSnapshot(UsageChartMode mode,
                List<HistoryPoint> points, double axisMinimum,
                double axisMaximum, double? currentValue,
                double secondaryValue, DateTimeOffset now)
            {
                Mode = mode;
                Points = points;
                AxisMinimum = axisMinimum;
                AxisMaximum = axisMaximum;
                CurrentValue = currentValue;
                SecondaryValue = secondaryValue;
                Now = now;
            }
        }

        private struct PlotPoint
        {
            public readonly float X;
            public readonly float Y;
            public readonly DateTimeOffset At;
            public PlotPoint(float x, float y, DateTimeOffset at)
            {
                X = x;
                Y = y;
                At = at;
            }
        }
    }
}

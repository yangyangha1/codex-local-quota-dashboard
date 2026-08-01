using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace CodexLocalDashboard
{
    internal enum UsageChartMode : byte
    {
        CumulativeAndQuota = 0,
        TokenRate = 1
    }

    /// <summary>
    /// 同一画布上的双尺度内存折线图。
    /// 剩余额度固定按 0-100% 映射，累计 Token 独立按当前时段峰值自动缩放。
    /// 取点始终由后台扫描驱动，与图表是否可见无关；不读写配置或历史文件。
    /// </summary>
    internal sealed class TokenRateChart
    {
        internal const int CaptureIntervalSeconds = 30;

        private const double MinimumTokenAxisMaximum = 1000d;
        private const double TargetPeakAxisRatio = 0.80d;
        private const double TokenAxisRoundStep = 100000d;
        private const double CumulativeTargetPeakAxisRatio = 0.80d;
        private const double QuotaJitterTolerance = 0.35d;
        private const double QuotaResetRiseThreshold = 2d;
        private const double QuotaConsumptionEpsilon = 0.01d;

        private static readonly TimeSpan TargetWindow = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinimumWindow =
            TimeSpan.FromSeconds(CaptureIntervalSeconds - 5);
        private static readonly TimeSpan MaximumWindow = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan RawSampleSlack = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RateSmoothingTime =
            TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QuotaSmoothingTime =
            TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PointBucketDuration =
            TimeSpan.FromSeconds(CaptureIntervalSeconds);
        private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan MaximumDisplayDuration =
            TimeSpan.FromHours(48);
        private static readonly TimeSpan RetentionSlack = TimeSpan.FromMinutes(2);
        private const int WheelStepDelta = 120;
        private const int WheelDebounceMilliseconds = 200;
        private const int HistoryPruneBatchSize = 64;
        private static readonly byte[] ZoomLevels = { 1, 2, 3, 6, 12, 24, 48 };

        private readonly object gate = new object();
        private readonly List<TokenCounterSample> rateSamples =
            new List<TokenCounterSample>(8);
        private readonly List<TokenCounterSample> counterHistory =
            new List<TokenCounterSample>(5776);
        private readonly List<HistoryPoint> tokenPoints =
            new List<HistoryPoint>(5776);
        private readonly List<HistoryPoint> quotaPoints =
            new List<HistoryPoint>(5776);
        private readonly List<HistoryPoint> quotaSourcePoints =
            new List<HistoryPoint>(5776);

        private byte displayModeValue =
            (byte)UsageChartMode.CumulativeAndQuota;
        private byte displayHoursValue = 2;
        private int wheelDeltaAccumulator;
        private int wheelDirection;
        private int lastWheelStepTick;
        private bool hasWheelStepTick;
        private DateTimeOffset? lastCaptureAt;
        private DateTimeOffset? chartOriginAt;

        private double? lastTokenRate;
        private DateTimeOffset? lastRateCalculationAt;
        private bool breakBeforeNextTokenPoint;
        private bool hasSourceCounter;
        private long lastSourceCounter;
        private DateTime lastSourceDay;
        private long normalizedCounter;
        private bool breakBeforeNextCounterSample;

        private double? lastQuotaRemaining;
        private DateTimeOffset? lastQuotaCalculationAt;
        private bool breakBeforeNextQuotaPoint;
        private bool hasQuotaSource;
        private double lastQuotaSource;
        private double quotaConsumptionReferenceRemaining;
        private double quotaConsumedDuringRuntime;
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

        public void ToggleMode()
        {
            lock (gate)
            {
                displayModeValue =
                    displayModeValue == (byte)UsageChartMode.CumulativeAndQuota
                    ? (byte)UsageChartMode.TokenRate
                    : (byte)UsageChartMode.CumulativeAndQuota;
            }
        }

        public int DisplayHours
        {
            get
            {
                lock (gate) return displayHoursValue;
            }
        }

        /// <summary>
        /// 滚轮向上缩短时间轴，向下扩大时间轴。
        /// 在 1h～48h 档位间切换，共享同一份纯内存历史。
        /// </summary>
        public bool ZoomByWheel(int delta)
        {
            return ZoomByWheel(delta, Environment.TickCount);
        }

        internal bool ZoomByWheel(int delta, int tickCount)
        {
            if (delta == 0) return false;
            lock (gate)
            {
                var direction = Math.Sign(delta);
                if (wheelDirection != direction)
                {
                    wheelDirection = direction;
                    wheelDeltaAccumulator = 0;
                }
                wheelDeltaAccumulator += delta;
                if (Math.Abs(wheelDeltaAccumulator) < WheelStepDelta)
                    return false;

                if (hasWheelStepTick &&
                    unchecked(tickCount - lastWheelStepTick) <
                        WheelDebounceMilliseconds)
                {
                    wheelDeltaAccumulator = direction *
                        (WheelStepDelta - 1);
                    return false;
                }

                var index = Array.IndexOf(ZoomLevels, displayHoursValue);
                if (index < 0) index = 0;
                var next = direction > 0
                    ? Math.Max(0, index - 1)
                    : Math.Min(ZoomLevels.Length - 1, index + 1);
                wheelDeltaAccumulator = 0;
                if (next == index) return false;
                displayHoursValue = ZoomLevels[next];
                lastWheelStepTick = tickCount;
                hasWheelStepTick = true;
                return true;
            }
        }

        /// <summary>
        /// 后台录入一次成功扫描。即使图表当前不可见，也会持续积累两条曲线。
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

                if (!chartOriginAt.HasValue) chartOriginAt = capturedAt;

                if (lastCaptureAt.HasValue &&
                    capturedAt - lastCaptureAt.Value > MaximumContinuousGap)
                {
                    rateSamples.Clear();
                    lastTokenRate = null;
                    lastRateCalculationAt = null;
                    lastQuotaRemaining = null;
                    lastQuotaCalculationAt = null;
                    hasQuotaSource = false;
                    breakBeforeNextTokenPoint = true;
                    breakBeforeNextQuotaPoint = true;
                    breakBeforeNextCounterSample = true;
                }
                lastCaptureAt = capturedAt;

                CaptureTokenLocked(capturedAt, cumulativeTokens);
                CaptureQuotaLocked(capturedAt, remainingPercent, windowMinutes,
                    resetsAt);
            }
        }

        /// <summary>
        /// 扫描失败时保持上一有效值；额度曲线会跨缺失采样连接。
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

                if (!chartOriginAt.HasValue) chartOriginAt = capturedAt;

                if (lastCaptureAt.HasValue &&
                    capturedAt - lastCaptureAt.Value > MaximumContinuousGap)
                {
                    rateSamples.Clear();
                    lastTokenRate = null;
                    lastRateCalculationAt = null;
                    lastQuotaRemaining = null;
                    lastQuotaCalculationAt = null;
                    hasQuotaSource = false;
                    breakBeforeNextTokenPoint = true;
                    breakBeforeNextQuotaPoint = true;
                    breakBeforeNextCounterSample = true;
                    lastCaptureAt = capturedAt;
                    return;
                }

                lastCaptureAt = capturedAt;
                AppendTokenHoldLocked(capturedAt);
                AppendQuotaHoldLocked(capturedAt);
                PruneRateSamplesLocked(capturedAt);
            }
        }

        public void Clear()
        {
            lock (gate) ResetAllLocked(null);
        }

        public void Draw(Graphics graphics, RectangleF bounds, ThemeMode theme,
            DateTimeOffset now, float visualScale)
        {
            if (graphics == null || bounds.Width < 40f || bounds.Height < 50f)
                return;

            RenderSnapshot snapshot;
            lock (gate)
            {
                PruneLocked(now);
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

            var sourceDay = at.LocalDateTime.Date;
            if (!hasSourceCounter)
            {
                hasSourceCounter = true;
                lastSourceCounter = cumulativeTokens;
                lastSourceDay = sourceDay;
                normalizedCounter = 0;
                AddCounterSampleLocked(at, true);
                return;
            }

            if (sourceDay != lastSourceDay)
            {
                // 今日计数器跨本地日期归零时，只切换源基线，不制造速率尖峰。
                lastSourceDay = sourceDay;
                lastSourceCounter = cumulativeTokens;
                AddCounterSampleLocked(at, breakBeforeNextCounterSample);
                AppendTokenHoldLocked(at);
                return;
            }

            if (cumulativeTokens < lastSourceCounter)
            {
                // 同日回退通常是日志写入、移动或枚举交界的短暂不完整快照。
                // 不移动有效基线，避免下一次恢复时产生假峰值。
                AppendTokenHoldLocked(at);
                return;
            }

            var sourceDelta = cumulativeTokens - lastSourceCounter;
            lastSourceCounter = cumulativeTokens;
            if (sourceDelta > long.MaxValue - normalizedCounter)
            {
                rateSamples.Clear();
                hasSourceCounter = false;
                breakBeforeNextTokenPoint = true;
                AppendTokenHoldLocked(at);
                return;
            }

            normalizedCounter += sourceDelta;
            AddCounterSampleLocked(at, breakBeforeNextCounterSample);
            rateSamples.Add(new TokenCounterSample(at, normalizedCounter,
                false));
            PruneRateSamplesLocked(at);

            TokenCounterSample baseline;
            if (!TryFindTokenBaselineLocked(at, out baseline))
            {
                AppendTokenHoldLocked(at);
                return;
            }

            var elapsedSeconds = (at - baseline.At).TotalSeconds;
            var delta = normalizedCounter - baseline.CumulativeTokens;
            if (elapsedSeconds <= 0d || delta < 0)
            {
                AppendTokenHoldLocked(at);
                return;
            }

            var rate = delta * 60d / elapsedSeconds;
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0d)
            {
                AppendTokenHoldLocked(at);
                return;
            }

            var firstCalculatedRate = !lastTokenRate.HasValue;
            var stabilizedRate = StabilizeRateLocked(at, rate);
            if (firstCalculatedRate)
                AppendTokenPointLocked(baseline.At, stabilizedRate, false);
            AppendTokenPointLocked(at, stabilizedRate, false);
        }

        private double StabilizeRateLocked(DateTimeOffset at, double rawRate)
        {
            if (!lastTokenRate.HasValue ||
                !lastRateCalculationAt.HasValue ||
                at <= lastRateCalculationAt.Value)
            {
                lastRateCalculationAt = at;
                return rawRate;
            }

            var elapsedSeconds = (at - lastRateCalculationAt.Value)
                .TotalSeconds;
            var alpha = 1d - Math.Exp(-elapsedSeconds /
                RateSmoothingTime.TotalSeconds);
            alpha = Math.Max(0.35d, Math.Min(0.80d, alpha));
            lastRateCalculationAt = at;
            return lastTokenRate.Value +
                alpha * (rawRate - lastTokenRate.Value);
        }

        private void AddCounterSampleLocked(DateTimeOffset at,
            bool breakBefore)
        {
            var sample = new TokenCounterSample(at, normalizedCounter,
                breakBefore);
            // Counter history keeps every background scan so the displayed
            // period increase starts at the real first sample, not at a
            // minute-bucket replacement used only for plotted points.
            counterHistory.Add(sample);
            breakBeforeNextCounterSample = false;

            if (rateSamples.Count == 0)
                rateSamples.Add(new TokenCounterSample(at, normalizedCounter,
                    false));
        }

        private void CaptureQuotaLocked(DateTimeOffset at,
            double? remainingPercent, int windowMinutes,
            DateTimeOffset? resetsAt)
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
                    breakBeforeNextQuotaPoint);
                return;
            }

            var identityChanged = windowMinutes != quotaWindowMinutes ||
                !NullableDateEquals(resetsAt, quotaResetsAt);
            var rise = remaining - lastQuotaSource;

            if (identityChanged || rise > QuotaResetRiseThreshold)
            {
                StartQuotaWindowLocked(at, remaining, windowMinutes, resetsAt,
                    true);
                return;
            }

            if (remaining <
                quotaConsumptionReferenceRemaining -
                QuotaConsumptionEpsilon)
            {
                quotaConsumedDuringRuntime +=
                    quotaConsumptionReferenceRemaining - remaining;
                quotaConsumptionReferenceRemaining = remaining;
            }

            if (rise > QuotaJitterTolerance)
            {
                lastQuotaSource = remaining;
                lastQuotaCalculationAt = at;
                AppendQuotaHoldLocked(at);
                return;
            }

            lastQuotaSource = remaining;
            AppendQuotaSourcePointLocked(at, remaining, false);
            AppendQuotaPointLocked(at,
                StabilizeQuotaLocked(at, remaining), false);
        }

        private void StartQuotaWindowLocked(DateTimeOffset at, double remaining,
            int windowMinutes, DateTimeOffset? resetsAt, bool breakBefore)
        {
            hasQuotaSource = true;
            lastQuotaSource = remaining;
            quotaConsumptionReferenceRemaining = remaining;
            quotaWindowMinutes = windowMinutes;
            quotaResetsAt = resetsAt;
            lastQuotaCalculationAt = at;
            AppendQuotaSourcePointLocked(at, remaining,
                breakBefore || quotaSourcePoints.Count == 0);
            AppendQuotaPointLocked(at, remaining,
                breakBefore || quotaPoints.Count == 0);
        }

        private double StabilizeQuotaLocked(DateTimeOffset at,
            double rawRemaining)
        {
            if (!lastQuotaRemaining.HasValue ||
                !lastQuotaCalculationAt.HasValue ||
                at <= lastQuotaCalculationAt.Value)
            {
                lastQuotaCalculationAt = at;
                return rawRemaining;
            }

            var elapsedSeconds = (at - lastQuotaCalculationAt.Value)
                .TotalSeconds;
            var alpha = 1d - Math.Exp(-elapsedSeconds /
                QuotaSmoothingTime.TotalSeconds);
            alpha = Math.Max(0.35d, Math.Min(0.80d, alpha));
            lastQuotaCalculationAt = at;
            return lastQuotaRemaining.Value +
                alpha * (rawRemaining - lastQuotaRemaining.Value);
        }

        private void ResetAllLocked(DateTimeOffset? captureAt)
        {
            rateSamples.Clear();
            counterHistory.Clear();
            tokenPoints.Clear();
            quotaPoints.Clear();
            quotaSourcePoints.Clear();
            lastCaptureAt = captureAt;
            chartOriginAt = captureAt;

            lastTokenRate = null;
            lastRateCalculationAt = null;
            breakBeforeNextTokenPoint = false;
            hasSourceCounter = false;
            lastSourceCounter = 0;
            lastSourceDay = DateTime.MinValue;
            normalizedCounter = 0;
            breakBeforeNextCounterSample = false;

            lastQuotaRemaining = null;
            lastQuotaCalculationAt = null;
            breakBeforeNextQuotaPoint = false;
            hasQuotaSource = false;
            lastQuotaSource = 0d;
            quotaConsumptionReferenceRemaining = 0d;
            quotaConsumedDuringRuntime = 0d;
            quotaWindowMinutes = 0;
            quotaResetsAt = null;
        }

        private void AppendTokenHoldLocked(DateTimeOffset at)
        {
            if (!lastTokenRate.HasValue) return;
            if (HasLongGap(tokenPoints, at))
            {
                breakBeforeNextTokenPoint = true;
                return;
            }
            AppendTokenPointLocked(at, lastTokenRate.Value, false);
        }

        private void AppendQuotaHoldLocked(DateTimeOffset at)
        {
            if (!lastQuotaRemaining.HasValue) return;
            if (HasLongGap(quotaPoints, at))
            {
                return;
            }
            AppendQuotaPointLocked(at, lastQuotaRemaining.Value, false);
        }

        private static bool HasLongGap(List<HistoryPoint> points,
            DateTimeOffset at)
        {
            return points.Count > 0 &&
                at - points[points.Count - 1].At > MaximumContinuousGap;
        }

        private void AppendTokenPointLocked(DateTimeOffset at, double value,
            bool forceBreakBefore)
        {
            AppendRatePointLocked(tokenPoints, at, value,
                forceBreakBefore || breakBeforeNextTokenPoint);
            lastTokenRate = value;
            breakBeforeNextTokenPoint = false;
        }

        private static void AppendRatePointLocked(List<HistoryPoint> points,
            DateTimeOffset at, double value, bool breakBefore)
        {
            if (points.Count > 0)
            {
                var previous = points[points.Count - 1];
                if (at <= previous.At) return;
                if (at - previous.At > MaximumContinuousGap)
                    breakBefore = true;

                if (!breakBefore && !previous.BreakBefore &&
                    InSamePointBucket(previous.At, at))
                {
                    var sampleCount = previous.SampleCount + 1;
                    var average = (previous.Value * previous.SampleCount +
                        value) / sampleCount;
                    points[points.Count - 1] = new HistoryPoint(at, average,
                        previous.BreakBefore, sampleCount);
                    return;
                }
            }
            points.Add(new HistoryPoint(at, value, breakBefore, 1));
        }

        private void AppendQuotaPointLocked(DateTimeOffset at, double value,
            bool forceBreakBefore)
        {
            // Quota is a slowly changing percentage. Missing samples must not
            // split the visible curve; the renderer connects the nearest valid
            // points across a gap or a quota-window reset.
            AppendContinuousPointLocked(quotaPoints, at, value);
            lastQuotaRemaining = value;
            breakBeforeNextQuotaPoint = false;
        }

        private static void AppendContinuousPointLocked(
            List<HistoryPoint> points, DateTimeOffset at, double value)
        {
            if (points.Count > 0)
            {
                var previous = points[points.Count - 1];
                if (at <= previous.At) return;
                if (InSamePointBucket(previous.At, at))
                {
                    points[points.Count - 1] = new HistoryPoint(at, value,
                        previous.BreakBefore);
                    return;
                }
            }
            points.Add(new HistoryPoint(at, value, points.Count == 0));
        }

        private void AppendQuotaSourcePointLocked(DateTimeOffset at,
            double value, bool breakBefore)
        {
            AppendPointLocked(quotaSourcePoints, at, value, breakBefore);
        }

        private static void AppendPointLocked(List<HistoryPoint> points,
            DateTimeOffset at, double value, bool breakBefore)
        {
            if (points.Count > 0)
            {
                var previous = points[points.Count - 1];
                if (at <= previous.At) return;
                if (at - previous.At > MaximumContinuousGap) breakBefore = true;

                if (!breakBefore && !previous.BreakBefore &&
                    InSamePointBucket(previous.At, at))
                {
                    points[points.Count - 1] = new HistoryPoint(at, value,
                        previous.BreakBefore);
                    return;
                }
            }
            points.Add(new HistoryPoint(at, value, breakBefore));
        }

        private static bool InSamePointBucket(DateTimeOffset left,
            DateTimeOffset right)
        {
            var bucketTicks = PointBucketDuration.Ticks;
            return left.UtcDateTime.Ticks / bucketTicks ==
                right.UtcDateTime.Ticks / bucketTicks;
        }

        private bool TryFindTokenBaselineLocked(DateTimeOffset currentAt,
            out TokenCounterSample baseline)
        {
            baseline = default(TokenCounterSample);
            var found = false;
            var bestDifference = double.MaxValue;
            for (var i = 0; i < rateSamples.Count - 1; i++)
            {
                var candidate = rateSamples[i];
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

        private void PruneRateSamplesLocked(DateTimeOffset now)
        {
            var oldest = now - MaximumWindow - RawSampleSlack;
            var count = 0;
            while (count < rateSamples.Count && rateSamples[count].At < oldest)
                count++;
            if (count > 0) rateSamples.RemoveRange(0, count);
        }

        private void PruneLocked(DateTimeOffset now)
        {
            var oldest = now - MaximumDisplayDuration - RetentionSlack;
            PrunePoints(tokenPoints, oldest);
            PrunePoints(quotaPoints, oldest);
            PrunePoints(quotaSourcePoints, oldest);
            PruneCounterSamples(counterHistory, oldest);
            PruneRateSamplesLocked(now);
        }

        private static void PrunePoints(List<HistoryPoint> points,
            DateTimeOffset oldest)
        {
            var count = 0;
            while (count < points.Count && points[count].At < oldest) count++;
            if (count >= HistoryPruneBatchSize || count == points.Count)
                points.RemoveRange(0, count);
        }

        private static void PruneCounterSamples(
            List<TokenCounterSample> samples, DateTimeOffset oldest)
        {
            var count = 0;
            while (count + 1 < samples.Count && samples[count + 1].At < oldest)
                count++;
            if (count >= HistoryPruneBatchSize ||
                count == samples.Count - 1)
                samples.RemoveRange(0, count);
        }

        private DateTimeOffset TimelineStartLocked(DateTimeOffset now)
        {
            var slidingStart = now -
                TimeSpan.FromHours(displayHoursValue);
            if (!chartOriginAt.HasValue || chartOriginAt.Value < slidingStart)
                return slidingStart;
            if (chartOriginAt.Value > now) return now;
            return chartOriginAt.Value;
        }

        private RenderSnapshot BuildRenderSnapshotLocked(DateTimeOffset now)
        {
            var timelineStart = TimelineStartLocked(now);
            var selectedTokens = SelectPoints(tokenPoints, timelineStart, now);
            var selectedQuota = SelectPoints(quotaPoints, timelineStart, now);
            var selectedQuotaSource = SelectPointsWithBaseline(
                quotaSourcePoints, timelineStart, now);
            var cumulativePoints = BuildCumulativePointsLocked(timelineStart,
                now);

            double? currentQuota = hasQuotaSource
                ? (double?)lastQuotaSource : null;
            if (!currentQuota.HasValue && selectedQuota.Count > 0)
                currentQuota = selectedQuota[selectedQuota.Count - 1].Value;

            var cumulativeIncrease = CalculatePeriodIncreaseLocked(
                timelineStart, now);
            var cumulativeAxisMaximum = CalculateCumulativeAxis(
                cumulativePoints);
            var currentRate = lastTokenRate;
            var peakRate = 0d;
            for (var i = 0; i < selectedTokens.Count; i++)
            {
                if (selectedTokens[i].Value > peakRate)
                    peakRate = selectedTokens[i].Value;
            }
            var tokenAxisMaximum =
                CalculateRoundedTokenAxisMaximum(peakRate);

            return new RenderSnapshot((UsageChartMode)displayModeValue,
                selectedTokens, selectedQuota, cumulativePoints,
                tokenAxisMaximum,
                cumulativeAxisMaximum, currentQuota, currentRate, peakRate,
                cumulativeIncrease,
                CalculateQuotaConsumption(selectedQuotaSource),
                timelineStart, now,
                TimeSpan.FromHours(displayHoursValue));
        }

        private List<HistoryPoint> BuildCumulativePointsLocked(
            DateTimeOffset from, DateTimeOffset to)
        {
            var output = new List<HistoryPoint>();
            if (counterHistory.Count == 0) return output;

            var baseline = FindPeriodBaselineLocked(from);
            if (!baseline.HasValue) return output;
            for (var i = 0; i < counterHistory.Count; i++)
            {
                var sample = counterHistory[i];
                if (sample.At < from || sample.At > to) continue;
                var value = Math.Max(0d, sample.CumulativeTokens -
                    baseline.Value.CumulativeTokens);
                AppendPointLocked(output, sample.At, value,
                    sample.BreakBefore);
            }
            return SmoothCumulativePoints(output);
        }

        private static List<HistoryPoint> SmoothCumulativePoints(
            List<HistoryPoint> source)
        {
            if (source.Count < 4) return source;
            var output = new List<HistoryPoint>(source.Count);
            var segmentStart = 0;
            while (segmentStart < source.Count)
            {
                var segmentEnd = segmentStart + 1;
                while (segmentEnd < source.Count &&
                    !source[segmentEnd].BreakBefore) segmentEnd++;
                SmoothCumulativeSegment(source, segmentStart, segmentEnd,
                    output);
                segmentStart = segmentEnd;
            }
            return output;
        }

        private static void SmoothCumulativeSegment(
            List<HistoryPoint> source, int start, int end,
            List<HistoryPoint> output)
        {
            var count = end - start;
            if (count < 4)
            {
                for (var i = start; i < end; i++) output.Add(source[i]);
                return;
            }

            var increments = new double[count - 1];
            for (var i = 0; i < increments.Length; i++)
                increments[i] = Math.Max(0d,
                    source[start + i + 1].Value - source[start + i].Value);

            var smoothed = new double[increments.Length];
            var smoothedTotal = 0d;
            for (var i = 0; i < increments.Length; i++)
            {
                var weighted = 0d;
                var weightTotal = 0d;
                for (var offset = -1; offset <= 1; offset++)
                {
                    var index = i + offset;
                    if (index < 0 || index >= increments.Length) continue;
                    var weight = 2 - Math.Abs(offset);
                    weighted += increments[index] * weight;
                    weightTotal += weight;
                }
                smoothed[i] = weightTotal <= 0d ? increments[i] :
                    weighted / weightTotal;
                smoothedTotal += smoothed[i];
            }

            var first = source[start];
            var exactIncrease = Math.Max(0d,
                source[end - 1].Value - first.Value);
            var scale = smoothedTotal <= 0d ? 0d :
                exactIncrease / smoothedTotal;
            var value = first.Value;
            output.Add(first);
            for (var i = 1; i < count; i++)
            {
                value += smoothed[i - 1] * scale;
                if (i == count - 1) value = source[end - 1].Value;
                var point = source[start + i];
                output.Add(new HistoryPoint(point.At, value,
                    point.BreakBefore, point.SampleCount));
            }
        }

        private TokenCounterSample? FindPeriodBaselineLocked(
            DateTimeOffset from)
        {
            TokenCounterSample? baseline = null;
            for (var i = 0; i < counterHistory.Count; i++)
            {
                var sample = counterHistory[i];
                if (sample.At <= from) baseline = sample;
                else
                {
                    if (!baseline.HasValue) baseline = sample;
                    break;
                }
            }
            return baseline;
        }

        private static double CalculateCumulativeAxis(
            List<HistoryPoint> points)
        {
            var peak = 0d;
            for (var i = 0; i < points.Count; i++)
                if (points[i].Value > peak) peak = points[i].Value;
            return CalculateRoundedCumulativeAxisMaximum(peak);
        }

        private static double CalculateRoundedCumulativeAxisMaximum(
            double peak)
        {
            if (double.IsNaN(peak) || double.IsInfinity(peak) || peak <= 0d)
                return MinimumTokenAxisMaximum;
            var target = Math.Max(MinimumTokenAxisMaximum,
                peak / CumulativeTargetPeakAxisRatio);
            var step = NiceStep(target / 40d);
            return Math.Max(MinimumTokenAxisMaximum,
                Math.Ceiling(target / step) * step);
        }

        private static double NiceStep(double value)
        {
            if (value <= 0d) return 1d;
            var exponent = Math.Floor(Math.Log10(value));
            var scale = Math.Pow(10d, exponent);
            var normalized = value / scale;
            if (normalized <= 1d) return scale;
            if (normalized <= 2d) return 2d * scale;
            if (normalized <= 5d) return 5d * scale;
            return 10d * scale;
        }

        private static double CalculateRoundedTokenAxisMaximum(double peak)
        {
            if (double.IsNaN(peak) || double.IsInfinity(peak) || peak <= 0d)
                return MinimumTokenAxisMaximum;

            var target = Math.Max(MinimumTokenAxisMaximum,
                peak / TargetPeakAxisRatio);
            var roundedTarget = Math.Round(
                target / TokenAxisRoundStep,
                MidpointRounding.AwayFromZero) * TokenAxisRoundStep;
            var peakCeiling = Math.Ceiling(
                peak / TokenAxisRoundStep) * TokenAxisRoundStep;
            return Math.Max(TokenAxisRoundStep,
                Math.Max(roundedTarget, peakCeiling));
        }

        private static List<HistoryPoint> SelectPoints(
            List<HistoryPoint> source, DateTimeOffset from, DateTimeOffset to)
        {
            var selected = new List<HistoryPoint>();
            for (var i = 0; i < source.Count; i++)
            {
                var point = source[i];
                if (point.At < from || point.At > to) continue;
                selected.Add(point);
            }
            selected.Sort(delegate(HistoryPoint left, HistoryPoint right)
            {
                return left.At.CompareTo(right.At);
            });
            return selected;
        }

        private static List<HistoryPoint> SelectPointsWithBaseline(
            List<HistoryPoint> source, DateTimeOffset from, DateTimeOffset to)
        {
            var selected = new List<HistoryPoint>();
            HistoryPoint? baseline = null;
            for (var i = 0; i < source.Count; i++)
            {
                var point = source[i];
                if (point.At <= from) baseline = point;
                if (point.At > from && point.At <= to) selected.Add(point);
            }
            if (baseline.HasValue) selected.Insert(0, baseline.Value);
            return selected;
        }

        private static double CalculateQuotaConsumption(
            List<HistoryPoint> points)
        {
            if (points.Count < 2) return 0d;
            var total = 0d;
            var reference = points[0].Value;
            for (var i = 1; i < points.Count; i++)
            {
                var point = points[i];
                if (point.BreakBefore)
                {
                    reference = point.Value;
                    continue;
                }
                if (point.Value <
                    reference - QuotaConsumptionEpsilon)
                {
                    total += reference - point.Value;
                    reference = point.Value;
                }
            }
            return total;
        }

        private double CalculatePeriodIncreaseLocked(DateTimeOffset from,
            DateTimeOffset to)
        {
            if (counterHistory.Count < 2) return 0d;
            var baseline = FindPeriodBaselineLocked(from);
            TokenCounterSample? latest = null;

            for (var i = 0; i < counterHistory.Count; i++)
            {
                var sample = counterHistory[i];
                if (sample.At <= to) latest = sample;
            }

            if (!baseline.HasValue || !latest.HasValue ||
                latest.Value.At <= baseline.Value.At)
                return 0d;
            return Math.Max(0d, latest.Value.CumulativeTokens -
                baseline.Value.CumulativeTokens);
        }

        private static void DrawSnapshot(Graphics graphics, RectangleF bounds,
            ThemeMode theme, RenderSnapshot snapshot, float visualScale)
        {
            var light = theme == ThemeMode.Light;
            var muted = light ? Color.FromArgb(94, 105, 117) :
                Color.FromArgb(142, 153, 169);
            var grid = light ? Color.FromArgb(45, 118, 130, 143) :
                Color.FromArgb(42, 176, 188, 201);
            var quotaColor = light ? Color.FromArgb(27, 151, 101) :
                Color.FromArgb(75, 205, 143);
            var tokenColor = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(92, 175, 232);
            var rateBaseColor = muted;
            var rateLineColor = Color.FromArgb(128, rateBaseColor);
            var rateFillBaseColor = light
                ? Color.FromArgb(119, 108, 174)
                : Color.FromArgb(158, 149, 207);
            var rateFillColor = Color.FromArgb(light ? 16 : 14,
                rateFillBaseColor);

            var geometryScale = Math.Max(0.65f, bounds.Width / 292f);
            var headerHeight = 18f * geometryScale;
            var plot = RectangleF.FromLTRB(bounds.Left,
                bounds.Top + headerHeight + geometryScale,
                bounds.Right, bounds.Bottom);
            if (plot.Width < 20f || plot.Height < 20f) return;

            using (var headerFont = new Font(Ui.FontFamilyName,
                Math.Max(5.75f, 7.5f * visualScale), FontStyle.Bold))
            using (var smallFont = new Font(Ui.FontFamilyName,
                Math.Max(5.5f, 7f * visualScale), FontStyle.Regular))
            using (var quotaBrush = new SolidBrush(quotaColor))
            using (var tokenBrush = new SolidBrush(tokenColor))
            using (var rateBrush = new SolidBrush(rateBaseColor))
            using (var mutedBrush = new SolidBrush(muted))
            using (var center = Format(StringAlignment.Center,
                StringAlignment.Center))
            {
                DrawGrid(graphics, plot, grid, geometryScale);
                bool primaryDrawn;
                bool secondaryDrawn;
                if (snapshot.Mode == UsageChartMode.CumulativeAndQuota)
                {
                    var leftText = "消耗额度 " +
                        FormatPercent(
                            snapshot.QuotaConsumedDuringRuntime) + " · " +
                        ((int)snapshot.DisplayDuration.TotalHours)
                            .ToString(CultureInfo.InvariantCulture) + "h";
                    var rateText = snapshot.CurrentTokenRate.HasValue
                        ? "速率 " +
                            FormatTokenCount(
                                snapshot.CurrentTokenRate.Value) + "/分"
                        : "速率 收集中";
                    var rightText = "累计 Token：" +
                        FormatTokenCount(snapshot.CumulativeIncrease);
                    DrawHeaderTriple(graphics, bounds, headerHeight,
                        leftText, rateText, rightText, headerFont,
                        quotaBrush, rateBrush, tokenBrush, geometryScale);

                    var rateDrawn = DrawSeries(graphics, plot,
                        snapshot.TokenPoints, snapshot.TokenAxisMaximum,
                        snapshot.TimelineStart, snapshot.DisplayDuration,
                        rateLineColor, geometryScale, true, false,
                        rateFillColor);
                    primaryDrawn = DrawSeries(graphics, plot,
                        snapshot.QuotaPoints, 100d, snapshot.TimelineStart,
                        snapshot.DisplayDuration, quotaColor, geometryScale,
                        true, false) || rateDrawn;
                    secondaryDrawn = DrawSeries(graphics, plot,
                        snapshot.CumulativePoints,
                        snapshot.CumulativeAxisMaximum,
                        snapshot.TimelineStart, snapshot.DisplayDuration,
                        tokenColor, geometryScale, true, false);
                    if (snapshot.PeakTokenRate > 0d ||
                        snapshot.CumulativeIncrease > 0d)
                    {
                        using (var nearTop = Format(StringAlignment.Near,
                            StringAlignment.Near))
                        using (var farTop = Format(StringAlignment.Far,
                            StringAlignment.Near))
                        {
                            if (snapshot.PeakTokenRate > 0d)
                                graphics.DrawString("峰值 " +
                                    FormatTokenCount(
                                        snapshot.PeakTokenRate) +
                                    " · 速率上限 " +
                                    FormatTokenCount(
                                        snapshot.TokenAxisMaximum),
                                    smallFont, mutedBrush,
                                    new RectangleF(plot.Left +
                                        3f * geometryScale,
                                        plot.Top + geometryScale,
                                        plot.Width * 0.64f,
                                        13f * geometryScale), nearTop);
                            if (snapshot.CumulativeIncrease > 0d)
                                graphics.DrawString("累计上限 " +
                                    FormatTokenCount(
                                        snapshot.CumulativeAxisMaximum),
                                    smallFont, mutedBrush,
                                    new RectangleF(plot.Left, plot.Top +
                                        geometryScale, plot.Width -
                                        3f * geometryScale,
                                        13f * geometryScale), farTop);
                        }
                    }
                }
                else
                {
                    var currentText = snapshot.CurrentTokenRate.HasValue
                        ? "当前：" +
                            FormatTokenCount(snapshot.CurrentTokenRate.Value) +
                            "/分钟"
                        : "当前：收集中";
                    DrawHeaderPair(graphics, bounds, headerHeight,
                        "Token 速率 · " +
                            ((int)snapshot.DisplayDuration.TotalHours)
                                .ToString(CultureInfo.InvariantCulture) + "h",
                        currentText, headerFont,
                        tokenBrush, tokenBrush, geometryScale);
                    primaryDrawn = DrawSeries(graphics, plot,
                        snapshot.TokenPoints, snapshot.TokenAxisMaximum,
                        snapshot.TimelineStart, snapshot.DisplayDuration,
                        tokenColor, geometryScale, true, false);
                    secondaryDrawn = false;

                    if (snapshot.PeakTokenRate > 0d)
                    {
                        using (var nearTop = Format(StringAlignment.Near,
                            StringAlignment.Near))
                        using (var farTop = Format(StringAlignment.Far,
                            StringAlignment.Near))
                        {
                            graphics.DrawString("峰值 " +
                                FormatTokenCount(snapshot.PeakTokenRate) +
                                "/分钟", smallFont, mutedBrush,
                                new RectangleF(plot.Left +
                                    3f * geometryScale, plot.Top +
                                    geometryScale, plot.Width * 0.52f,
                                    13f * geometryScale), nearTop);
                            graphics.DrawString("上限 " +
                                FormatTokenCount(
                                    snapshot.TokenAxisMaximum) +
                                "/分钟", smallFont, mutedBrush,
                                new RectangleF(plot.Left, plot.Top +
                                    geometryScale, plot.Width -
                                    3f * geometryScale,
                                    13f * geometryScale), farTop);
                        }
                    }
                }

                if (!primaryDrawn && !secondaryDrawn)
                    graphics.DrawString("等待连续数据", smallFont, mutedBrush,
                        plot, center);
            }
        }

        private static void DrawHeaderPair(Graphics graphics, RectangleF bounds,
            float headerHeight, string leftText, string rightText,
            Font font, Brush leftBrush, Brush rightBrush, float geometryScale)
        {
            var gap = Math.Max(4f, 6f * geometryScale);
            var leftMeasured = graphics.MeasureString(leftText, font).Width;
            var rightMeasured = graphics.MeasureString(rightText, font).Width;
            var available = Math.Max(1f, bounds.Width - gap);
            var totalMeasured = Math.Max(1f, leftMeasured + rightMeasured);
            var leftWidth = Math.Min(available * 0.48f,
                Math.Max(1f, available * leftMeasured / totalMeasured));
            var rightWidth = Math.Max(1f, available - leftWidth);

            using (var leftFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            })
            using (var rightFormat = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            })
            {
                graphics.DrawString(leftText, font, leftBrush,
                    new RectangleF(bounds.Left, bounds.Top, leftWidth,
                        headerHeight), leftFormat);
                graphics.DrawString(rightText, font, rightBrush,
                    new RectangleF(bounds.Right - rightWidth, bounds.Top,
                        rightWidth, headerHeight), rightFormat);
            }
        }

        private static void DrawHeaderTriple(Graphics graphics,
            RectangleF bounds, float headerHeight, string leftText,
            string centerText, string rightText, Font font, Brush leftBrush,
            Brush centerBrush, Brush rightBrush, float geometryScale)
        {
            var gap = Math.Max(2f, 3f * geometryScale);
            var usable = Math.Max(1f, bounds.Width - gap * 2f);
            var leftWidth = usable * 0.38f;
            var centerWidth = usable * 0.28f;
            var rightWidth = usable - leftWidth - centerWidth;
            using (var leftFormat = Format(StringAlignment.Near,
                StringAlignment.Center))
            using (var centerFormat = Format(StringAlignment.Center,
                StringAlignment.Center))
            using (var rightFormat = Format(StringAlignment.Far,
                StringAlignment.Center))
            {
                graphics.DrawString(leftText, font, leftBrush,
                    new RectangleF(bounds.Left, bounds.Top, leftWidth,
                        headerHeight), leftFormat);
                graphics.DrawString(centerText, font, centerBrush,
                    new RectangleF(bounds.Left + leftWidth + gap,
                        bounds.Top, centerWidth, headerHeight), centerFormat);
                graphics.DrawString(rightText, font, rightBrush,
                    new RectangleF(bounds.Right - rightWidth, bounds.Top,
                        rightWidth, headerHeight), rightFormat);
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

        private static bool DrawSeries(Graphics graphics, RectangleF plot,
            List<HistoryPoint> points, double axisMaximum,
            DateTimeOffset timelineStart, TimeSpan displayDuration,
            Color lineColor,
            float geometryScale, bool smooth, bool dashed,
            Color? fillColor = null)
        {
            if (axisMaximum <= 0d || points.Count < 2) return false;
            var seconds = Math.Max(1d, displayDuration.TotalSeconds);
            var segments = new List<List<PlotPoint>>();
            List<PlotPoint> segment = null;

            for (var i = 0; i < points.Count; i++)
            {
                var source = points[i];
                var ratioX = (source.At - timelineStart).TotalSeconds / seconds;
                if (ratioX < 0d || ratioX > 1d) continue;
                if (segment == null || source.BreakBefore)
                {
                    segment = new List<PlotPoint>();
                    segments.Add(segment);
                }

                var x = plot.Left + (float)(ratioX * plot.Width);
                var ratioY = Math.Max(0d, Math.Min(1d,
                    source.Value / axisMaximum));
                var y = plot.Bottom - (float)(ratioY * plot.Height);
                segment.Add(new PlotPoint(x, y, source.At));
            }

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

                    using (var pen = new Pen(lineColor,
                        Math.Max(1.25f, 1.65f * geometryScale)))
                    {
                        pen.LineJoin = LineJoin.Round;
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        if (dashed) pen.DashStyle = DashStyle.Dash;

                        if (smooth)
                        {
                            using (var curve = BuildMonotoneCurve(reduced))
                            {
                                if (curve.PointCount < 2) continue;
                                if (fillColor.HasValue)
                                    FillUnderSeries(graphics, curve,
                                        reduced, plot, fillColor.Value);
                                graphics.DrawPath(pen, curve);
                            }
                        }
                        else
                        {
                            var linePoints = new PointF[reduced.Count];
                            for (var j = 0; j < reduced.Count; j++)
                                linePoints[j] = new PointF(reduced[j].X,
                                    reduced[j].Y);
                            graphics.DrawLines(pen, linePoints);
                        }
                    }
                    drew = true;
                }
            }
            finally
            {
                graphics.Restore(state);
            }
            return drew;
        }

        private static void FillUnderSeries(Graphics graphics,
            GraphicsPath curve, List<PlotPoint> points, RectangleF plot,
            Color color)
        {
            if (curve == null || points == null || points.Count < 2) return;
            using (var area = (GraphicsPath)curve.Clone())
            using (var brush = new SolidBrush(color))
            {
                var first = points[0];
                var last = points[points.Count - 1];
                area.AddLine(last.X, last.Y, last.X, plot.Bottom);
                area.AddLine(last.X, plot.Bottom, first.X, plot.Bottom);
                area.CloseFigure();
                graphics.FillPath(brush, area);
            }
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
                Trimming = StringTrimming.None
            };
        }

        private struct TokenCounterSample
        {
            public readonly DateTimeOffset At;
            public readonly long CumulativeTokens;
            public readonly bool BreakBefore;
            public TokenCounterSample(DateTimeOffset at, long cumulativeTokens,
                bool breakBefore)
            {
                At = at;
                CumulativeTokens = cumulativeTokens;
                BreakBefore = breakBefore;
            }
        }

        private struct HistoryPoint
        {
            public readonly DateTimeOffset At;
            public readonly double Value;
            public readonly bool BreakBefore;
            public readonly int SampleCount;
            public HistoryPoint(DateTimeOffset at, double value,
                bool breakBefore)
                : this(at, value, breakBefore, 1)
            {
            }
            public HistoryPoint(DateTimeOffset at, double value,
                bool breakBefore, int sampleCount)
            {
                At = at;
                Value = value;
                BreakBefore = breakBefore;
                SampleCount = Math.Max(1, sampleCount);
            }
        }

        private sealed class RenderSnapshot
        {
            public readonly UsageChartMode Mode;
            public readonly List<HistoryPoint> TokenPoints;
            public readonly List<HistoryPoint> QuotaPoints;
            public readonly List<HistoryPoint> CumulativePoints;
            public readonly double TokenAxisMaximum;
            public readonly double CumulativeAxisMaximum;
            public readonly double? CurrentQuota;
            public readonly double? CurrentTokenRate;
            public readonly double PeakTokenRate;
            public readonly double CumulativeIncrease;
            public readonly double QuotaConsumedDuringRuntime;
            public readonly DateTimeOffset TimelineStart;
            public readonly DateTimeOffset Now;
            public readonly TimeSpan DisplayDuration;

            public RenderSnapshot(UsageChartMode mode,
                List<HistoryPoint> tokenPoints,
                List<HistoryPoint> quotaPoints,
                List<HistoryPoint> cumulativePoints,
                double tokenAxisMaximum, double cumulativeAxisMaximum,
                double? currentQuota, double? currentTokenRate,
                double peakTokenRate, double cumulativeIncrease,
                double quotaConsumedDuringRuntime,
                DateTimeOffset timelineStart, DateTimeOffset now,
                TimeSpan displayDuration)
            {
                Mode = mode;
                TokenPoints = tokenPoints;
                QuotaPoints = quotaPoints;
                CumulativePoints = cumulativePoints;
                TokenAxisMaximum = tokenAxisMaximum;
                CumulativeAxisMaximum = cumulativeAxisMaximum;
                CurrentQuota = currentQuota;
                CurrentTokenRate = currentTokenRate;
                PeakTokenRate = peakTokenRate;
                CumulativeIncrease = cumulativeIncrease;
                QuotaConsumedDuringRuntime = quotaConsumedDuringRuntime;
                TimelineStart = timelineStart;
                Now = now;
                DisplayDuration = displayDuration;
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

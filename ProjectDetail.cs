using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.IO;

namespace CodexLocalDashboard
{
    internal enum ProjectDetailClickResult : byte
    {
        None = 0,
        Redraw = 1,
        Close = 2
    }

    internal enum ProjectDetailPointerHint : byte
    {
        None = 0,
        Close = 1,
        OpenFolder = 2,
        DetailButton = 3
    }

    /// <summary>
    /// 仪表盘图表区域内的本地项目用量视图。
    /// 只保留内存状态，不创建窗口、控件或额外数据文件。
    /// </summary>
    internal sealed class ProjectDetailChart
    {
        private List<ProjectHitArea> projectHitAreas =
            new List<ProjectHitArea>();
        private List<SessionHitArea> sessionHitAreas =
            new List<SessionHitArea>();
        private List<FolderHitArea> folderHitAreas =
            new List<FolderHitArea>();
        private HashSet<string> expandedProjects =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> expandedSessions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly StringFormat NearFormat = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        private static readonly StringFormat FarFormat = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.None
        };
        private static readonly StringFormat CenterFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        private List<ProjectUsage> projects = new List<ProjectUsage>();
        private RectangleF closeBounds;
        private float scrollOffset;
        private float maximumScroll;
        private float scrollStep = 34f;
        private bool loading;
        private bool loadError;

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

        public void Clear()
        {
            projects = new List<ProjectUsage>();
            projectHitAreas = new List<ProjectHitArea>();
            sessionHitAreas = new List<SessionHitArea>();
            folderHitAreas = new List<FolderHitArea>();
            expandedProjects = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            expandedSessions = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            closeBounds = RectangleF.Empty;
            scrollOffset = 0f;
            maximumScroll = 0f;
            loading = false;
            loadError = false;
        }

        public void SetProjects(List<ProjectUsage> value)
        {
            loading = false;
            loadError = false;
            projects = value == null
                ? new List<ProjectUsage>()
                : value.OrderByDescending(item => item.TotalTokens).ToList();
            var known = new HashSet<string>(
                projects.Select(ProjectKey),
                StringComparer.OrdinalIgnoreCase);
            expandedProjects.RemoveWhere(key => !known.Contains(key));
            scrollOffset = Math.Min(scrollOffset, maximumScroll);
        }

        public ProjectDetailClickResult HandleClick(PointF point)
        {
            if (closeBounds.Contains(point))
                return ProjectDetailClickResult.Close;
            for (var i = 0; i < folderHitAreas.Count; i++)
            {
                if (!folderHitAreas[i].Bounds.Contains(point)) continue;
                OpenFolder(folderHitAreas[i].Path);
                return ProjectDetailClickResult.Redraw;
            }
            for (var i = 0; i < sessionHitAreas.Count; i++)
            {
                var area = sessionHitAreas[i];
                if (!area.Bounds.Contains(point)) continue;
                if (!expandedSessions.Add(area.Key))
                    expandedSessions.Remove(area.Key);
                return ProjectDetailClickResult.Redraw;
            }
            for (var i = 0; i < projectHitAreas.Count; i++)
            {
                var area = projectHitAreas[i];
                if (!area.Bounds.Contains(point)) continue;
                var key = ProjectKey(projects[area.ProjectIndex]);
                if (!expandedProjects.Add(key))
                    expandedProjects.Remove(key);
                return ProjectDetailClickResult.Redraw;
            }
            return ProjectDetailClickResult.None;
        }

        public ProjectDetailPointerHint PointerHint(PointF point)
        {
            if (closeBounds.Contains(point))
                return ProjectDetailPointerHint.Close;
            if (folderHitAreas.Any(area => area.Bounds.Contains(point)))
                return ProjectDetailPointerHint.OpenFolder;
            return ProjectDetailPointerHint.None;
        }

        public bool Scroll(int delta)
        {
            if (delta == 0 || maximumScroll <= 0f) return false;
            var next = scrollOffset +
                (delta > 0 ? -scrollStep : scrollStep);
            next = Math.Max(0f, Math.Min(maximumScroll, next));
            if (Math.Abs(next - scrollOffset) < 0.1f) return false;
            scrollOffset = next;
            return true;
        }

        public void Draw(Graphics graphics, RectangleF bounds,
            ThemeMode theme, float visualScale)
        {
            if (graphics == null || bounds.Width < 80f || bounds.Height < 60f)
                return;
            var light = theme == ThemeMode.Light;
            var geometryScale = Math.Max(0.65f, bounds.Width / 292f);
            scrollStep = 34f * geometryScale;
            var headerHeight = 34f * geometryScale;
            var projectHeight = 35f * geometryScale;
            var sessionHeight = 19f * geometryScale;
            var sessionDetailHeight = 47f * geometryScale;
            var contentTop = bounds.Top + headerHeight;
            var contentHeight = projects.Count * projectHeight;
            for (var i = 0; i < projects.Count; i++)
                if (expandedProjects.Contains(ProjectKey(projects[i])))
                {
                    contentHeight += projects[i].Sessions.Count *
                        sessionHeight;
                    for (var j = 0; j < projects[i].Sessions.Count; j++)
                        if (expandedSessions.Contains(SessionKey(projects[i],
                            projects[i].Sessions[j])))
                            contentHeight += sessionDetailHeight;
                }
            var viewportHeight = Math.Max(1f, bounds.Bottom - contentTop);
            maximumScroll = Math.Max(0f, contentHeight - viewportHeight);
            scrollOffset = Math.Max(0f,
                Math.Min(maximumScroll, scrollOffset));

            var primary = light ? Color.FromArgb(24, 31, 41) :
                Color.FromArgb(242, 245, 249);
            var muted = light ? Color.FromArgb(91, 101, 116) :
                Color.FromArgb(142, 153, 169);
            var grid = light ? Color.FromArgb(45, 118, 130, 143) :
                Color.FromArgb(42, 176, 188, 201);
            var blue = light ? Color.FromArgb(32, 117, 178) :
                Color.FromArgb(92, 175, 232);
            var track = light ? Color.FromArgb(42, 118, 130, 143) :
                Color.FromArgb(50, 176, 188, 201);
            var expanded = light ? Color.FromArgb(22, 32, 117, 178) :
                Color.FromArgb(24, 92, 175, 232);
            var sessionTrack = Color.FromArgb(light ? 22 : 28, muted);
            var sessionFillEven = Color.FromArgb(light ? 42 : 48, blue);
            var sessionFillOdd = Color.FromArgb(light ? 32 : 38, blue);
            var alternateSession = Color.FromArgb(light ? 12 : 15, muted);
            var total = projects.Sum(item => item.TotalTokens);
            var maximum = Math.Max(1L,
                projects.Count == 0 ? 1L :
                projects.Max(item => item.TotalTokens));
            projectHitAreas.Clear();
            sessionHitAreas.Clear();
            folderHitAreas.Clear();

            var state = graphics.Save();
            try
            {
                graphics.SetClip(bounds);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var titleFont = new Font(Ui.FontFamilyName,
                    Math.Max(5.8f, 7.6f * visualScale), FontStyle.Bold))
                using (var bodyFont = new Font(Ui.FontFamilyName,
                    Math.Max(5.6f, 7.1f * visualScale), FontStyle.Bold))
                using (var smallFont = new Font(Ui.FontFamilyName,
                    Math.Max(5.2f, 6.5f * visualScale)))
                using (var primaryBrush = new SolidBrush(primary))
                using (var mutedBrush = new SolidBrush(muted))
                using (var blueBrush = new SolidBrush(blue))
                using (var trackBrush = new SolidBrush(track))
                using (var expandedBrush = new SolidBrush(expanded))
                using (var sessionTrackBrush =
                    new SolidBrush(sessionTrack))
                using (var sessionFillEvenBrush =
                    new SolidBrush(sessionFillEven))
                using (var sessionFillOddBrush =
                    new SolidBrush(sessionFillOdd))
                using (var alternateSessionBrush =
                    new SolidBrush(alternateSession))
                using (var gridPen = new Pen(grid,
                    Math.Max(0.55f, 0.65f * geometryScale)))
                {
                    DrawHeader(graphics, bounds, total, titleFont, smallFont,
                        primaryBrush, mutedBrush, muted, gridPen,
                        geometryScale);
                    if (loading || loadError || projects.Count == 0)
                    {
                        var message = loading
                            ? "正在加载本地项目与会话明细…"
                            : loadError
                                ? "明细读取失败，请关闭后重试"
                                : "尚未识别到本地项目用量";
                        graphics.DrawString(message,
                            bodyFont, mutedBrush,
                            new RectangleF(bounds.Left +
                                3f * geometryScale,
                                contentTop + 12f * geometryScale,
                                bounds.Width - 6f * geometryScale,
                                22f * geometryScale));
                        return;
                    }

                    var y = contentTop - scrollOffset;
                    for (var index = 0; index < projects.Count; index++)
                    {
                        var project = projects[index];
                        var isExpanded = expandedProjects.Contains(
                            ProjectKey(project));
                        var projectBounds = new RectangleF(bounds.Left, y,
                            bounds.Width, projectHeight);
                        projectHitAreas.Add(new ProjectHitArea(index,
                            projectBounds));
                        if (isExpanded &&
                            projectBounds.Bottom > contentTop &&
                            projectBounds.Top < bounds.Bottom)
                            graphics.FillRectangle(expandedBrush,
                                projectBounds);
                        var folderBounds = DrawProject(graphics, project,
                            index, maximum,
                            projectBounds, isExpanded, bodyFont, smallFont,
                            primaryBrush, mutedBrush, trackBrush, blueBrush,
                            blue, geometryScale);
                        if (isExpanded && !folderBounds.IsEmpty)
                            folderHitAreas.Add(new FolderHitArea(
                                project.ProjectPath, folderBounds));
                        y += projectHeight;

                        if (!isExpanded) continue;
                        var sessions = SortSessionsByActivity(
                            project.Sessions);
                        var sessionMaximum = Math.Max(1L,
                            sessions.Count == 0 ? 1L :
                            sessions.Max(item => item.TotalTokens));
                        for (var sessionIndex = 0;
                            sessionIndex < sessions.Count; sessionIndex++)
                        {
                            var sessionBounds = new RectangleF(bounds.Left,
                                y, bounds.Width, sessionHeight);
                            var sessionKey = SessionKey(project,
                                sessions[sessionIndex]);
                            sessionHitAreas.Add(new SessionHitArea(sessionKey,
                                sessionBounds));
                            DrawSession(graphics, sessions[sessionIndex],
                                sessionIndex, sessionMaximum, sessionBounds,
                                smallFont, primaryBrush, mutedBrush,
                                sessionTrackBrush,
                                sessionIndex % 2 == 0
                                    ? sessionFillEvenBrush
                                    : sessionFillOddBrush,
                                alternateSessionBrush, gridPen,
                                geometryScale);
                            y += sessionHeight;
                            if (expandedSessions.Contains(sessionKey))
                            {
                                var detailBounds = new RectangleF(
                                    bounds.Left, y, bounds.Width,
                                    sessionDetailHeight);
                                DrawSessionDetails(graphics,
                                    sessions[sessionIndex],
                                    detailBounds, smallFont, primaryBrush,
                                    mutedBrush, gridPen,
                                    geometryScale);
                                y += sessionDetailHeight;
                            }
                        }
                    }
                    DrawScrollbar(graphics, bounds, contentTop,
                        viewportHeight, contentHeight, muted, geometryScale);
                }
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private void DrawHeader(Graphics graphics, RectangleF bounds,
            long total, Font titleFont, Font smallFont, Brush primaryBrush,
            Brush mutedBrush, Color muted, Pen gridPen, float scale)
        {
            closeBounds = new RectangleF(bounds.Right - 23f * scale,
                bounds.Top, 23f * scale, 20f * scale);
            graphics.DrawString("项目用量明细", titleFont, primaryBrush,
                new PointF(bounds.Left, bounds.Top));
            var summary = FormatTokens(total) + " · " + projects.Count +
                " 个项目 · " +
                projects.Sum(item => item.Sessions.Count) + " 个会话";
            graphics.DrawString(summary, smallFont, mutedBrush,
                new RectangleF(bounds.Left, bounds.Top + 15f * scale,
                    bounds.Width - 18f * scale, 14f * scale),
                NearFormat);
            using (var closePen = new Pen(muted,
                Math.Max(0.8f, scale)))
            {
                closePen.StartCap = LineCap.Round;
                closePen.EndCap = LineCap.Round;
                var centerX = closeBounds.Left + closeBounds.Width / 2f;
                var centerY = closeBounds.Top + closeBounds.Height / 2f;
                var radius = 4f * scale;
                graphics.DrawLine(closePen, centerX - radius,
                    centerY - radius, centerX + radius, centerY + radius);
                graphics.DrawLine(closePen, centerX + radius,
                    centerY - radius, centerX - radius, centerY + radius);
            }
            graphics.DrawLine(gridPen, bounds.Left,
                bounds.Top + 32f * scale, bounds.Right,
                bounds.Top + 32f * scale);
        }

        private static RectangleF DrawProject(Graphics graphics,
            ProjectUsage project, int index, long maximum,
            RectangleF bounds, bool expanded, Font bodyFont, Font smallFont,
            Brush primaryBrush, Brush mutedBrush, Brush trackBrush,
            Brush blueBrush, Color blue, float scale)
        {
            var left = bounds.Left + 2f * scale;
            graphics.DrawString((index + 1).ToString(
                CultureInfo.InvariantCulture), smallFont, mutedBrush,
                new PointF(left, bounds.Top + 2f * scale));
            var valueWidth = 61f * scale;
            var nameLeft = bounds.Left + 17f * scale;
            var nameWidth = Math.Max(28f, bounds.Width -
                22f * scale - valueWidth);
            graphics.DrawString(TrimName(graphics, project.DisplayName,
                bodyFont, nameWidth), bodyFont, primaryBrush,
                new RectangleF(nameLeft, bounds.Top + 1f * scale,
                    nameWidth, 14f * scale), NearFormat);
            graphics.DrawString(FormatTokens(project.TotalTokens),
                bodyFont, primaryBrush,
                new RectangleF(bounds.Right - valueWidth -
                    3f * scale, bounds.Top + 1f * scale,
                    valueWidth, 14f * scale), FarFormat);

            var barLeft = nameLeft;
            var barWidth = Math.Max(30f, bounds.Width - 100f * scale);
            var barTop = bounds.Top + 17f * scale;
            var barHeight = Math.Max(2.5f, 4f * scale);
            graphics.FillRectangle(trackBrush, barLeft, barTop,
                barWidth, barHeight);
            graphics.FillRectangle(blueBrush, barLeft, barTop,
                Math.Max(2f, (float)(barWidth * project.TotalTokens /
                    (double)maximum)), barHeight);
            graphics.DrawString(project.Sessions.Count + " 个会话",
                smallFont, mutedBrush,
                new RectangleF(nameLeft, bounds.Top + 23f * scale,
                    85f * scale, 12f * scale), NearFormat);
            var actionBounds = new RectangleF(bounds.Right - 48f * scale,
                bounds.Top + 22f * scale, 45f * scale, 12f * scale);
            if (!expanded)
            {
                graphics.DrawString("展开⌄", smallFont, mutedBrush,
                    actionBounds, FarFormat);
                return RectangleF.Empty;
            }
            using (var buttonPen = new Pen(Color.FromArgb(125, blue),
                Math.Max(0.65f, 0.8f * scale)))
            using (var buttonBrush = new SolidBrush(
                Color.FromArgb(205, blue)))
            {
                graphics.DrawRectangle(buttonPen, actionBounds.X,
                    actionBounds.Y, actionBounds.Width,
                    actionBounds.Height);
                graphics.DrawString("文件夹", smallFont, buttonBrush,
                    actionBounds, CenterFormat);
            }
            return actionBounds;
        }

        private static void DrawSession(Graphics graphics,
            SessionUsage session, int index, long maximum,
            RectangleF bounds, Font font, Brush primaryBrush,
            Brush mutedBrush, Brush trackBrush, Brush fillBrush,
            Brush alternateBrush, Pen gridPen, float scale)
        {
            if (bounds.Bottom < graphics.ClipBounds.Top ||
                bounds.Top > graphics.ClipBounds.Bottom) return;
            var progressBounds = new RectangleF(
                bounds.Left + 17f * scale, bounds.Top + 1f * scale,
                bounds.Width - 21f * scale,
                Math.Max(1f, bounds.Height - 2f * scale));
            if (index % 2 == 1)
                graphics.FillRectangle(alternateBrush, progressBounds);
            graphics.FillRectangle(trackBrush, progressBounds);
            var fillWidth = (float)(progressBounds.Width *
                session.TotalTokens / (double)Math.Max(1L, maximum));
            graphics.FillRectangle(fillBrush, progressBounds.Left,
                progressBounds.Top, Math.Max(1f, fillWidth),
                progressBounds.Height);
            graphics.DrawLine(gridPen, bounds.Left + 17f * scale,
                bounds.Top, bounds.Right - 4f * scale, bounds.Top);
            graphics.DrawString("会话 " + (index + 1).ToString(
                CultureInfo.InvariantCulture), font, primaryBrush,
                new RectangleF(bounds.Left + 21f * scale,
                    bounds.Top + 3f * scale, 47f * scale,
                    13f * scale), NearFormat);
            graphics.DrawString(FormatSessionTime(session),
                font, mutedBrush,
                new RectangleF(bounds.Left + 67f * scale,
                    bounds.Top + 3f * scale, 145f * scale,
                    13f * scale), NearFormat);
            graphics.DrawString(FormatTokens(session.TotalTokens),
                font, primaryBrush,
                new RectangleF(bounds.Right - 66f * scale,
                    bounds.Top + 3f * scale, 62f * scale,
                    13f * scale), FarFormat);
        }

        private static void DrawSessionDetails(Graphics graphics,
            SessionUsage session, RectangleF bounds,
            Font font, Brush primaryBrush, Brush mutedBrush,
            Pen gridPen, float scale)
        {
            var left = bounds.Left + 21f * scale;
            var width = bounds.Width - 25f * scale;
            graphics.DrawString(FormatDuration(session) + " · " +
                session.TurnCount + "轮 · 工具" +
                session.ToolCallCount + "次 · " +
                (string.IsNullOrWhiteSpace(session.Status)
                    ? "状态未知" : session.Status),
                font, primaryBrush,
                new RectangleF(left, bounds.Top + 2f * scale,
                    width, 13f * scale), NearFormat);
            var cacheRatio = session.InputTokens <= 0 ? 0d :
                session.CachedTokens * 100d / session.InputTokens;
            graphics.DrawString("输入 " + FormatTokens(session.InputTokens) +
                " · 输出 " + FormatTokens(session.OutputTokens) +
                " · 缓存 " + FormatTokens(session.CachedTokens) +
                " (" + cacheRatio.ToString("0",
                    CultureInfo.InvariantCulture) + "%)",
                font, mutedBrush,
                new RectangleF(left, bounds.Top + 16f * scale,
                    width, 13f * scale), NearFormat);
            var modelText = string.IsNullOrWhiteSpace(session.Model)
                ? "模型未知" : session.Model;
            if (!string.IsNullOrWhiteSpace(session.Effort))
                modelText += " · " + session.Effort;
            graphics.DrawString(modelText, font, mutedBrush,
                new RectangleF(left, bounds.Top + 30f * scale,
                    width, 13f * scale), NearFormat);
            graphics.DrawLine(gridPen, left, bounds.Bottom - scale,
                bounds.Right - 4f * scale, bounds.Bottom - scale);
        }

        private void DrawScrollbar(Graphics graphics, RectangleF bounds,
            float contentTop, float viewportHeight, float contentHeight,
            Color muted, float scale)
        {
            if (maximumScroll <= 0f || contentHeight <= 0f) return;
            var width = Math.Max(1.2f, 2f * scale);
            var trackX = bounds.Right - width;
            using (var trackPen = new Pen(Color.FromArgb(42, muted), width))
                graphics.DrawLine(trackPen, trackX, contentTop, trackX,
                    bounds.Bottom);
            var thumbHeight = Math.Max(14f * scale,
                viewportHeight * viewportHeight / contentHeight);
            thumbHeight = Math.Min(viewportHeight, thumbHeight);
            var available = viewportHeight - thumbHeight;
            var thumbTop = contentTop + (maximumScroll <= 0f ? 0f :
                available * scrollOffset / maximumScroll);
            using (var thumbPen = new Pen(Color.FromArgb(145, muted), width))
            {
                thumbPen.StartCap = LineCap.Round;
                thumbPen.EndCap = LineCap.Round;
                graphics.DrawLine(thumbPen, trackX, thumbTop, trackX,
                    thumbTop + thumbHeight);
            }
        }

        private static string ProjectKey(ProjectUsage project)
        {
            return string.IsNullOrWhiteSpace(project.ProjectPath)
                ? project.DisplayName : project.ProjectPath;
        }

        internal static List<SessionUsage> SortSessionsByActivity(
            IEnumerable<SessionUsage> sessions)
        {
            return (sessions ?? Enumerable.Empty<SessionUsage>())
                .OrderByDescending(item => item.LastActivity)
                .ThenBy(item => item.SessionId,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatSessionTime(SessionUsage session)
        {
            var end = session.LastActivity;
            var start = session.StartedAt;
            if (end == DateTimeOffset.MinValue) return "时间未知";
            if (start == DateTimeOffset.MinValue || start >= end)
                return end.ToLocalTime().ToString("MM-dd HH:mm",
                    CultureInfo.CurrentCulture);
            var localStart = start.ToLocalTime();
            var localEnd = end.ToLocalTime();
            return localStart.Date == localEnd.Date
                ? localStart.ToString("MM-dd HH:mm",
                    CultureInfo.CurrentCulture) + "–" +
                    localEnd.ToString("HH:mm", CultureInfo.CurrentCulture)
                : localStart.ToString("MM-dd HH:mm",
                    CultureInfo.CurrentCulture) + "–" +
                    localEnd.ToString("MM-dd HH:mm",
                        CultureInfo.CurrentCulture);
        }

        private static string FormatDuration(SessionUsage session)
        {
            if (session.StartedAt == DateTimeOffset.MinValue ||
                session.LastActivity <= session.StartedAt)
                return "时长未知";
            var duration = session.LastActivity - session.StartedAt;
            if (duration.TotalHours >= 24d)
                return ((int)duration.TotalDays) + "天" +
                    duration.Hours + "小时";
            if (duration.TotalHours >= 1d)
                return ((int)duration.TotalHours) + "小时" +
                    duration.Minutes + "分钟";
            return Math.Max(1, (int)Math.Round(duration.TotalMinutes)) +
                "分钟";
        }

        private static string SessionKey(ProjectUsage project,
            SessionUsage session)
        {
            return ProjectKey(project) + "\n" +
                (session.SessionId ?? string.Empty) + "\n" +
                session.StartedAt.UtcDateTime.Ticks.ToString(
                    CultureInfo.InvariantCulture);
        }

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !Directory.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static string TrimName(Graphics graphics, string value,
            Font font, float width)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未识别项目";
            if (graphics.MeasureString(value, font).Width <= width)
                return value;
            var text = value;
            while (text.Length > 2 &&
                graphics.MeasureString(text + "…", font).Width > width)
                text = text.Substring(0, text.Length - 1);
            return text + "…";
        }

        private static string FormatTokens(long value)
        {
            var absolute = Math.Abs((double)value);
            if (absolute >= 1000000000d)
                return (value / 1000000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "B";
            if (absolute >= 1000000d)
                return (value / 1000000d).ToString("0.##",
                    CultureInfo.InvariantCulture) + "M";
            if (absolute >= 1000d)
                return (value / 1000d).ToString("0.#",
                    CultureInfo.InvariantCulture) + "K";
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private sealed class ProjectHitArea
        {
            public readonly int ProjectIndex;
            public readonly RectangleF Bounds;
            public ProjectHitArea(int projectIndex, RectangleF bounds)
            {
                ProjectIndex = projectIndex;
                Bounds = bounds;
            }
        }

        private sealed class SessionHitArea
        {
            public readonly string Key;
            public readonly RectangleF Bounds;
            public SessionHitArea(string key, RectangleF bounds)
            {
                Key = key;
                Bounds = bounds;
            }
        }

        private sealed class FolderHitArea
        {
            public readonly string Path;
            public readonly RectangleF Bounds;
            public FolderHitArea(string path, RectangleF bounds)
            {
                Path = path;
                Bounds = bounds;
            }
        }
    }
}

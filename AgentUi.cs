using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AgentTrafficLightNative {
  sealed class PixelPoleControl : Control {
    public static readonly Color KeyColor = Color.Fuchsia;
    readonly List<AgentTask> agents = new List<AgentTask>();
    readonly System.Windows.Forms.Timer blinkTimer = new System.Windows.Forms.Timer(); bool blinkOn = true; float scaleFactor = 1f;
    public float ScaleFactor { get { return scaleFactor; } set { scaleFactor = Math.Max(1f, Math.Min(2f, value)); Invalidate(); } }
    public Rectangle CenterRect, SettingsRect, CloseRect;
    public event EventHandler CenterClicked, SettingsClicked, CloseClicked;
    public event Action<string> AgentActivated;
    public PixelPoleControl() { Dock = DockStyle.Fill; BackColor = KeyColor; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); blinkTimer.Interval = 500; blinkTimer.Tick += delegate { blinkOn = !blinkOn; Invalidate(); }; }
    public void SetAgents(List<AgentTask> value) { agents.Clear(); agents.AddRange(value); bool attention = agents.Exists(delegate(AgentTask task) { return task.Status == State.Attention; }); if (attention) blinkTimer.Start(); else { blinkTimer.Stop(); blinkOn = true; } Invalidate(); }
    Point ToLogical(Point point) { return new Point((int)Math.Round(point.X * 4f / scaleFactor), (int)Math.Round(point.Y * 4f / scaleFactor)); }
    public bool IsButton(Point point) { Point logical = ToLogical(point); return CenterRect.Contains(logical) || SettingsRect.Contains(logical) || CloseRect.Contains(logical); }
    public string AgentAt(Point point) {
      if (agents.Count == 0) return null; Point logical = ToLogical(point); if (logical.Y < 8 || logical.Y > 130) return null;
      int count = Math.Min(4, agents.Count), center = (int)Math.Round(Width * 2f / scaleFactor), best = Int32.MaxValue, index = -1; int[] xs = HeadCenters(count, center);
      for (int i = 0; i < count; i++) { int distance = Math.Abs(logical.X - xs[i]); if (distance < best) { best = distance; index = i; } }
      return index >= 0 && best <= 32 ? agents[index].Source : null;
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = IsButton(e.Location) || AgentAt(e.Location) != null ? Cursors.Hand : Cursors.SizeAll; }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Point logical = ToLogical(e.Location); if (CenterRect.Contains(logical) && CenterClicked != null) CenterClicked(this, EventArgs.Empty); else if (SettingsRect.Contains(logical) && SettingsClicked != null) SettingsClicked(this, EventArgs.Empty); else if (CloseRect.Contains(logical) && CloseClicked != null) CloseClicked(this, EventArgs.Empty); else { string source = AgentAt(e.Location); if (source != null && AgentActivated != null) AgentActivated(source); } }
    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g.Clear(KeyColor); g.ScaleTransform(0.25f * scaleFactor, 0.25f * scaleFactor);
      int center = (int)Math.Round(Width * 2f / scaleFactor), headY = 8, barY = 168, baseY = (int)Math.Round(Height * 4f / scaleFactor) - 18;
      if (agents.Count == 0) {
        DrawPole(g, new Point(center, 70), new Point(center, baseY)); DrawLoginDisplay(g, center, headY); DrawBase(g, center, baseY); DrawControls(g, center, baseY - 106); return;
      }
      int count = Math.Min(4, agents.Count);
      int[] xs = HeadCenters(count, center);
      if (count == 1) DrawPole(g, new Point(center, headY + 122), new Point(center, baseY));
      else {
        DrawPole(g, new Point(xs[0], barY), new Point(xs[count - 1], barY));
        for (int i = 0; i < count; i++) DrawPole(g, new Point(xs[i], headY + 122), new Point(xs[i], barY));
        DrawPole(g, new Point(center, barY), new Point(center, baseY));
      }
      for (int i = 0; i < count; i++) DrawHead(g, xs[i] - 27, headY, agents[i]);
      DrawBase(g, center, baseY); DrawControls(g, center, baseY - 106);
    }
    int[] HeadCenters(int count, int center) {
      if (count == 1) return new[] { center };
      if (count == 2) return new[] { center - 50, center + 50 };
      if (count == 3) return new[] { center - 78, center, center + 78 };
      return new[] { center - 102, center - 34, center + 34, center + 102 };
    }
    void DrawPole(Graphics g, Point from, Point to) {
      using (var border = new Pen(Color.FromArgb(8, 11, 17), 18)) { border.StartCap = System.Drawing.Drawing2D.LineCap.Square; border.EndCap = System.Drawing.Drawing2D.LineCap.Square; g.DrawLine(border, from, to); }
      using (var fill = new Pen(Color.FromArgb(42, 50, 62), 12)) { fill.StartCap = System.Drawing.Drawing2D.LineCap.Square; fill.EndCap = System.Drawing.Drawing2D.LineCap.Square; g.DrawLine(fill, from, to); }
      using (var shine = new Pen(Color.FromArgb(71, 82, 98), 3)) {
        if (from.Y == to.Y) g.DrawLine(shine, from.X, from.Y - 3, to.X, to.Y - 3);
        else g.DrawLine(shine, from.X - 3, from.Y, to.X - 3, to.Y);
      }
    }
    void DrawHead(Graphics g, int x, int y, AgentTask task) {
      string status = task == null ? State.Complete : task.Status;
      Point[] body = { new Point(x + 7, y), new Point(x + 47, y), new Point(x + 54, y + 7), new Point(x + 54, y + 115), new Point(x + 47, y + 122), new Point(x + 7, y + 122), new Point(x, y + 115), new Point(x, y + 7) };
      using (var edge = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillPolygon(edge, body);
      Point[] inner = { new Point(x + 9, y + 5), new Point(x + 45, y + 5), new Point(x + 49, y + 9), new Point(x + 49, y + 113), new Point(x + 44, y + 117), new Point(x + 10, y + 117), new Point(x + 5, y + 112), new Point(x + 5, y + 10) };
      using (var shell = new SolidBrush(Color.FromArgb(47, 56, 68))) g.FillPolygon(shell, inner);
      using (var highlight = new SolidBrush(Color.FromArgb(79, 91, 108))) g.FillRectangle(highlight, x + 9, y + 7, 4, 106);
      DrawLamp(g, x + 14, y + 12, Color.FromArgb(255, 48, 60), status == State.Complete);
      DrawLamp(g, x + 14, y + 46, Color.FromArgb(255, 205, 32), status == State.Attention && blinkOn);
      DrawLamp(g, x + 14, y + 80, Color.FromArgb(38, 231, 103), status == State.Running);
      if (task != null && (task.HealthState == "disconnected" || task.HealthState == "unconfigured" || task.HealthState == "stale")) {
        using (var edge = new SolidBrush(Color.FromArgb(5, 7, 11))) g.FillRectangle(edge, x + 40, y + 103, 12, 12);
        using (var mark = new SolidBrush(Color.FromArgb(145, 156, 171))) { g.FillRectangle(mark, x + 43, y + 106, 6, 2); g.FillRectangle(mark, x + 45, y + 108, 2, 4); }
      }
    }
    void DrawLamp(Graphics g, int x, int y, Color color, bool active) {
      Point[] lens = { new Point(x + 8, y), new Point(x + 20, y), new Point(x + 28, y + 8), new Point(x + 28, y + 20), new Point(x + 20, y + 28), new Point(x + 8, y + 28), new Point(x, y + 20), new Point(x, y + 8) };
      using (var rim = new SolidBrush(Color.FromArgb(5, 7, 11))) g.FillPolygon(rim, lens);
      Point[] face = { new Point(x + 9, y + 4), new Point(x + 19, y + 4), new Point(x + 24, y + 9), new Point(x + 24, y + 19), new Point(x + 19, y + 24), new Point(x + 9, y + 24), new Point(x + 4, y + 19), new Point(x + 4, y + 9) };
      Color faceColor = active ? color : Color.FromArgb(20 + color.R / 14, 22 + color.G / 18, 24 + color.B / 14);
      using (var brush = new SolidBrush(faceColor)) g.FillPolygon(brush, face);
      if (active) { using (var shine = new SolidBrush(Color.White)) g.FillRectangle(shine, x + 8, y + 7, 5, 5); using (var glow = new Pen(Color.FromArgb(185, color), 2)) g.DrawPolygon(glow, face); }
    }
    void DrawControls(Graphics g, int center, int y) {
      CenterRect = new Rectangle(center - 16, y, 32, 26); SettingsRect = new Rectangle(center - 16, y + 36, 32, 26); CloseRect = new Rectangle(center - 16, y + 72, 32, 26);
      DrawButton(g, CenterRect, false); DrawButton(g, SettingsRect, false); DrawButton(g, CloseRect, true);
      int cx = CenterRect.Left + 16, cy = CenterRect.Top + 13;
      using (var list = new SolidBrush(Color.FromArgb(184, 194, 207))) {
        g.FillRectangle(list, cx - 8, cy - 7, 4, 4); g.FillRectangle(list, cx - 1, cy - 7, 10, 4);
        g.FillRectangle(list, cx - 8, cy - 1, 4, 4); g.FillRectangle(list, cx - 1, cy - 1, 10, 4);
        g.FillRectangle(list, cx - 8, cy + 5, 4, 4); g.FillRectangle(list, cx - 1, cy + 5, 10, 4);
      }
      int sx = SettingsRect.Left + 16, sy = SettingsRect.Top + 13;
      using (var gear = new SolidBrush(Color.FromArgb(184, 194, 207))) {
        g.FillRectangle(gear, sx - 7, sy - 7, 14, 14); g.FillRectangle(gear, sx - 10, sy - 3, 20, 6); g.FillRectangle(gear, sx - 3, sy - 10, 6, 20);
      }
      using (var cutout = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(cutout, sx - 3, sy - 3, 6, 6);
      using (var cross = new Pen(Color.FromArgb(255, 82, 82), 4)) { g.DrawLine(cross, CloseRect.Left + 9, CloseRect.Top + 7, CloseRect.Right - 9, CloseRect.Bottom - 7); g.DrawLine(cross, CloseRect.Right - 9, CloseRect.Top + 7, CloseRect.Left + 9, CloseRect.Bottom - 7); }
    }
    void DrawButton(Graphics g, Rectangle rect, bool close) { using (var border = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillRectangle(border, rect); using (var fill = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(fill, rect.Left + 4, rect.Top + 4, rect.Width - 8, rect.Height - 8); using (var top = new SolidBrush(Color.FromArgb(82, 94, 111))) g.FillRectangle(top, rect.Left + 6, rect.Top + 6, rect.Width - 12, 3); }
    void DrawBase(Graphics g, int center, int y) { using (var dark = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillRectangle(dark, center - 43, y - 2, 86, 16); using (var fill = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(fill, center - 36, y + 2, 72, 8); using (var shine = new SolidBrush(Color.FromArgb(80, 92, 108))) g.FillRectangle(shine, center - 30, y + 2, 60, 3); }
    void DrawLoginDisplay(Graphics g, int center, int y) {
      using (var edge = new SolidBrush(Color.FromArgb(6, 9, 14))) g.FillRectangle(edge, center - 70, y, 140, 62);
      using (var shell = new SolidBrush(Color.FromArgb(47, 56, 68))) g.FillRectangle(shell, center - 64, y + 6, 128, 50);
      using (var screen = new SolidBrush(Color.FromArgb(7, 22, 19))) g.FillRectangle(screen, center - 58, y + 12, 116, 38);
      DrawPixelLogin(g, center - 62, y + 20);
    }
    void DrawPixelLogin(Graphics g, int x, int y) {
      const string text = "LOGIN..."; int cursor = x;
      using (var brush = new SolidBrush(Color.FromArgb(67, 255, 155))) foreach (char c in text) {
        string[] pattern = PixelPattern(c); for (int row = 0; row < pattern.Length; row++) for (int col = 0; col < pattern[row].Length; col++) if (pattern[row][col] == '1') g.FillRectangle(brush, cursor + col * 4, y + row * 4, 4, 4);
        cursor += 16;
      }
    }
    string[] PixelPattern(char c) {
      if (c == 'L') return new[] { "100", "100", "100", "100", "111" };
      if (c == 'O') return new[] { "111", "101", "101", "101", "111" };
      if (c == 'G') return new[] { "111", "100", "101", "101", "111" };
      if (c == 'I') return new[] { "111", "010", "010", "010", "111" };
      if (c == 'N') return new[] { "101", "111", "111", "111", "101" };
      return new[] { "000", "000", "000", "000", "010" };
    }
    protected override void Dispose(bool disposing) { if (disposing) blinkTimer.Dispose(); base.Dispose(disposing); }
  }

  sealed class SettingsForm : Form {
    bool dragging; Point dragOrigin;
    public SettingsForm(SettingsData settings, Action<int> intervalChanged, Action<bool> modeChanged, Action<int> scaleChanged, Action updateCheck) {
      Text = "Agent Beacon 设置"; Icon = PixelTheme.AppIcon; ClientSize = new Size(760, 438); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1"; StartPosition = FormStartPosition.CenterParent; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var title = new Label { Text = "AGENT BEACON v" + AppInfo.Version + " // 设置", AutoSize = false, Location = new Point(60, 8), Size = new Size(610, 32), ForeColor = PixelTheme.Ink, BackColor = Color.Transparent, Font = PixelTheme.TitleFont, TextAlign = ContentAlignment.MiddleCenter }; Controls.Add(title);
      var close = new PixelButton { Text = "X", Location = new Point(719, 9), Size = new Size(28, 27), Danger = true }; close.Click += delegate { Close(); }; Controls.Add(close);
      var auto = new PixelToggle { Text = "开机自启动", Checked = settings.AutoStart, Location = new Point(22, 57), Width = 380 }; auto.CheckedChanged += delegate { settings.AutoStart = auto.Checked; Program.SetAutoStart(auto.Checked); Program.SaveSettings(settings); }; Controls.Add(auto);
      var compact = new PixelToggle { Text = "任务栏紧凑模式（只显示红绿灯和短灯杆）", Checked = settings.TaskbarMode, Location = new Point(22, 88), Width = 390 }; compact.CheckedChanged += delegate { settings.TaskbarMode = compact.Checked; Program.SaveSettings(settings); modeChanged(settings.TaskbarMode); }; Controls.Add(compact);
      Controls.Add(new Label { Text = "基础刷新", AutoSize = false, Location = new Point(22, 126), Size = new Size(72, 26), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      int[] values = { 1500, 2000, 3000, 5000, 10000 }; string[] labels = { "1.5S", "2S", "3S", "5S", "10S" }; var intervalButtons = new List<PixelButton>();
      for (int i = 0; i < values.Length; i++) {
        int index = i; var choice = new PixelButton { Text = labels[i], Location = new Point(100 + i * 61, 124), Size = new Size(55, 30), Active = settings.RefreshMs == values[i] }; intervalButtons.Add(choice);
        choice.Click += delegate { settings.RefreshMs = values[index]; foreach (var item in intervalButtons) item.Active = false; choice.Active = true; Program.SaveSettings(settings); intervalChanged(settings.RefreshMs); }; Controls.Add(choice);
      }
      Controls.Add(new Label { Text = "桌面灯大小", AutoSize = false, Location = new Point(22, 177), Size = new Size(72, 26), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      int[] scales = { 100, 150, 200 }; string[] scaleLabels = { "小 1X", "中 1.5X", "大 2X" }; var scaleButtons = new List<PixelButton>();
      for (int i = 0; i < scales.Length; i++) {
        int index = i; var choice = new PixelButton { Text = scaleLabels[i], Location = new Point(100 + i * 91, 175), Size = new Size(83, 30), Active = settings.LampScale == scales[i] }; scaleButtons.Add(choice);
        choice.Click += delegate { settings.LampScale = scales[index]; foreach (var item in scaleButtons) item.Active = false; choice.Active = true; Program.SaveSettings(settings); scaleChanged(settings.LampScale); }; Controls.Add(choice);
      }
      var traeState = StatusLabel(22, 260, 188); var ruleState = StatusLabel(220, 260, 188); var claudeState = StatusLabel(22, 322, 188); var openCodeState = StatusLabel(220, 322, 188); Controls.Add(traeState); Controls.Add(ruleState); Controls.Add(claudeState); Controls.Add(openCodeState);
      Action refresh = delegate { SetTraeStatus(traeState); ruleState.Text = "■ 粘贴到全局规则"; ruleState.ForeColor = PixelTheme.Muted; SetStatus(claudeState, Integration.IsClaudeInstalled()); SetStatus(openCodeState, Integration.IsOpenCodeInstalled()); }; refresh();
      var trae = new PixelButton { Text = "复制 TRAE MCP", Location = new Point(22, 226), Size = new Size(188, 32) }; trae.Click += delegate { string message = Integration.InstallTraeMcp(); refresh(); PixelDialog.Show(this, message, "TRAE MCP", PixelDialogButtons.Ok); }; Controls.Add(trae);
      var rule = new PixelButton { Text = "复制 TRAE 规则", Location = new Point(220, 226), Size = new Size(188, 32) }; rule.Click += delegate { string message = Integration.CopyTraeRule(); PixelDialog.Show(this, message, "TRAE 状态规则", PixelDialogButtons.Ok); }; Controls.Add(rule);
      var claude = new PixelButton { Text = "安装 CLAUDE HOOKS", Location = new Point(22, 288), Size = new Size(188, 32) }; claude.Click += delegate { string message = Integration.InstallClaude(); refresh(); PixelDialog.Show(this, message, "CLAUDE HOOKS", PixelDialogButtons.Ok); }; Controls.Add(claude);
      var openCode = new PixelButton { Text = "安装 OPENCODE 插件", Location = new Point(220, 288), Size = new Size(188, 32) }; openCode.Click += delegate { string message = Integration.InstallOpenCode(); refresh(); PixelDialog.Show(this, message, "OPENCODE 插件", PixelDialogButtons.Ok); }; Controls.Add(openCode);
      var repair = new PixelButton { Text = "检查 / 修复集成", Location = new Point(22, 350), Size = new Size(188, 34) }; repair.Click += delegate { string message = Integration.RepairConfiguredIntegrations(); refresh(); PixelDialog.Show(this, message, "集成健康检查", PixelDialogButtons.Ok); }; Controls.Add(repair);
      var history = new PixelButton { Text = "状态中心", Location = new Point(220, 350), Size = new Size(188, 34) }; history.Click += delegate { using (var form = new HistoryForm()) form.ShowDialog(this); }; Controls.Add(history);
      UsageSnapshot stats = UsageStatistics.Snapshot();
      Controls.Add(new Label { Text = "今日统计", AutoSize = false, Location = new Point(24, 398), Size = new Size(82, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      Controls.Add(new Label { Text = "完成 " + stats.CompletedTasks, AutoSize = false, Location = new Point(106, 398), Size = new Size(92, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Red, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      Controls.Add(new Label { Text = "运行 " + UsageStatistics.Duration(stats.RunningMs), AutoSize = false, Location = new Point(198, 398), Size = new Size(104, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Green, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      Controls.Add(new Label { Text = "等待 " + UsageStatistics.Duration(stats.AttentionMs), AutoSize = false, Location = new Point(302, 398), Size = new Size(104, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Yellow, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      Controls.Add(new Label { Text = "更新与提醒", AutoSize = false, Location = new Point(454, 57), Size = new Size(238, 24), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      var autoUpdate = new PixelToggle { Text = "启动时自动检查更新", Checked = settings.AutoCheckUpdates, Location = new Point(454, 88), Width = 280 }; autoUpdate.CheckedChanged += delegate { settings.AutoCheckUpdates = autoUpdate.Checked; Program.SaveSettings(settings); }; Controls.Add(autoUpdate);
      var checkUpdate = new PixelButton { Text = "立即检查 GitHub 更新", Location = new Point(454, 124), Size = new Size(238, 36) }; checkUpdate.Click += delegate { updateCheck(); }; Controls.Add(checkUpdate);
      var notifications = new PixelToggle { Text = "启用状态通知", Checked = settings.NotificationsEnabled, Location = new Point(454, 180), Width = 280 }; notifications.CheckedChanged += delegate { settings.NotificationsEnabled = notifications.Checked; Program.SaveSettings(settings); }; Controls.Add(notifications);
      var attentionNotify = new PixelToggle { Text = "黄灯：通知和提示音", Checked = settings.NotifyAttention, Location = new Point(454, 211), Width = 280 }; attentionNotify.CheckedChanged += delegate { settings.NotifyAttention = attentionNotify.Checked; Program.SaveSettings(settings); }; Controls.Add(attentionNotify);
      var completeNotify = new PixelToggle { Text = "红灯：完成通知", Checked = settings.NotifyComplete, Location = new Point(454, 242), Width = 280 }; completeNotify.CheckedChanged += delegate { settings.NotifyComplete = completeNotify.Checked; Program.SaveSettings(settings); }; Controls.Add(completeNotify);
      var quiet = new PixelToggle { Text = "免打扰 22-08", Checked = settings.QuietHoursEnabled, Location = new Point(454, 273), Width = 145 }; quiet.CheckedChanged += delegate { settings.QuietHoursEnabled = quiet.Checked; Program.SaveSettings(settings); }; Controls.Add(quiet);
      var strategy = new PixelButton { Text = "通知策略", Location = new Point(600, 273), Size = new Size(92, 30) }; strategy.Click += delegate { using (var form = new NotificationSettingsForm(settings)) form.ShowDialog(this); }; Controls.Add(strategy);
      Controls.Add(new Label { Text = "接收通知的 Agent", AutoSize = false, Location = new Point(454, 318), Size = new Size(238, 22), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      for (int i = 0; i < NotificationPolicy.Sources.Length; i++) {
        string source = NotificationPolicy.Sources[i]; var choice = new PixelButton { Text = source, Active = NotificationPolicy.AgentEnabled(settings, source), Location = new Point(454 + (i % 2) * 125, 348 + (i / 2) * 40), Size = new Size(115, 32) };
        choice.Click += delegate { choice.Active = !choice.Active; NotificationPolicy.SetAgent(settings, source, choice.Active); Program.SaveSettings(settings); }; Controls.Add(choice);
      }
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag; Shown += delegate { DpiSupport.KeepOnScreen(this); };
    }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    Label StatusLabel(int x, int y, int width) { return new Label { AutoSize = false, Location = new Point(x, y), Size = new Size(width, 24), BackColor = Color.Transparent, Font = PixelTheme.StrongFont, TextAlign = ContentAlignment.MiddleCenter }; }
    void SetStatus(Label label, bool installed) { label.Text = installed ? "■ 已安装" : "■ 未安装"; label.ForeColor = installed ? PixelTheme.Green : PixelTheme.Yellow; }
    void SetTraeStatus(Label label) { label.Text = Integration.TraeMcpStatus(); label.ForeColor = Integration.IsTraeMcpReadyAndConnected() ? PixelTheme.Green : PixelTheme.Yellow; }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; PixelTheme.PaintWindow(g, Width, Height, 430);
      using (var rule = new SolidBrush(PixelTheme.Ink)) { g.FillRectangle(rule, 14, 112, 402, 3); g.FillRectangle(rule, 14, 166, 402, 3); g.FillRectangle(rule, 14, 217, 402, 3); g.FillRectangle(rule, 14, 342, 402, 3); }
      using (var frame = new Pen(PixelTheme.Ink, 3)) g.DrawRectangle(frame, 14, 390, 402, 34);
      base.OnPaint(e);
    }
  }

  sealed class NotificationSettingsForm : Form {
    bool dragging; Point dragOrigin;
    public NotificationSettingsForm(SettingsData settings) {
      Text = "Agent Beacon 通知策略"; Icon = PixelTheme.AppIcon; ClientSize = new Size(500, 290); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var title = new Label { Text = "AGENT BEACON // 通知策略", AutoSize = false, Location = new Point(48, 8), Size = new Size(400, 32), ForeColor = PixelTheme.Ink, BackColor = Color.Transparent, Font = PixelTheme.TitleFont, TextAlign = ContentAlignment.MiddleCenter }; Controls.Add(title);
      var close = new PixelButton { Text = "X", Location = new Point(459, 9), Size = new Size(28, 27), Danger = true }; close.Click += delegate { Close(); }; Controls.Add(close);
      Controls.Add(new Label { Text = "黄灯通知延迟", AutoSize = false, Location = new Point(24, 62), Size = new Size(120, 28), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      int[] delays = { 0, 3, 10, 30 }; string[] delayLabels = { "立即", "3S", "10S", "30S" }; var delayButtons = new List<PixelButton>();
      for (int i = 0; i < delays.Length; i++) {
        int index = i; var choice = new PixelButton { Text = delayLabels[i], Location = new Point(152 + i * 76, 60), Size = new Size(68, 32), Active = settings.AttentionNotifyDelaySeconds == delays[i] }; delayButtons.Add(choice);
        choice.Click += delegate { settings.AttentionNotifyDelaySeconds = delays[index]; foreach (var button in delayButtons) button.Active = false; choice.Active = true; Program.SaveSettings(settings); }; Controls.Add(choice);
      }
      Controls.Add(new Label { Text = "长任务提醒", AutoSize = false, Location = new Point(24, 118), Size = new Size(120, 28), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      int[] minutes = { 0, 10, 30, 60 }; string[] minuteLabels = { "关闭", "10M", "30M", "60M" }; var minuteButtons = new List<PixelButton>();
      for (int i = 0; i < minutes.Length; i++) {
        int index = i; var choice = new PixelButton { Text = minuteLabels[i], Location = new Point(152 + i * 76, 116), Size = new Size(68, 32), Active = settings.LongRunningReminderMinutes == minutes[i] }; minuteButtons.Add(choice);
        choice.Click += delegate { settings.LongRunningReminderMinutes = minutes[index]; foreach (var button in minuteButtons) button.Active = false; choice.Active = true; Program.SaveSettings(settings); }; Controls.Add(choice);
      }
      Controls.Add(new Label { Text = "同一任务的同一次确认只提醒一次；状态恢复后不会重复弹出。", AutoSize = false, Location = new Point(32, 177), Size = new Size(436, 34), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.TextFont });
      var done = new PixelButton { Text = "完成", Active = true, Location = new Point(184, 230), Size = new Size(132, 36) }; done.Click += delegate { Close(); }; Controls.Add(done);
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag; Shown += delegate { DpiSupport.KeepOnScreen(this); };
    }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); using (var pen = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(pen, 18, 50, 464, 166); base.OnPaint(e); }
  }

  sealed class MainForm : Form {
    readonly MonitorEngine engine = new MonitorEngine(); readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(); readonly SettingsData settings; readonly PixelPoleControl widget = new PixelPoleControl(); readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer taskbarBlinkTimer = new System.Windows.Forms.Timer(), eventDebounceTimer = new System.Windows.Forms.Timer(); readonly Dictionary<string, NotifyIcon> taskbarLights = new Dictionary<string, NotifyIcon>(); readonly NotifyIcon taskbarLogin = new NotifyIcon(); readonly ContextMenuStrip trayMenu = new ContextMenuStrip(), taskbarMenu = new ContextMenuStrip();
    readonly Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>(); readonly Dictionary<string, long> sourceSeenAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); readonly Dictionary<string, AgentTask> resolvedStates = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase); readonly Dictionary<string, string> transitionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> pendingAttentionNotifications = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); readonly HashSet<string> sentAttentionNotifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase), sentLongRunningNotifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long lastScanStartedAt; MonitorWatchers watchers; List<AgentTask> currentAgents = new List<AgentTask>(), currentTasks = new List<AgentTask>(), lastGoodTasks = new List<AgentTask>(); List<TaskSourceHealth> currentHealth = new List<TaskSourceHealth>(); TaskQueuePopup queuePopup; bool quitting, dragging, scanning, pendingRescan, taskbarBlinkOn = true, transitionBaselineReady, updateChecking, startupUpdateChecked; Point dragOrigin; string lastSignature = null, taskbarLayoutSignature = null;
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public MainForm(SettingsData loaded) {
      settings = loaded; if (settings.LampScale != 150 && settings.LampScale != 200) settings.LampScale = 100; float initialScale = settings.LampScale / 100f;
      Text = "Agent Beacon v" + AppInfo.Version; Name = "AgentBeaconWindow"; Icon = PixelTheme.AppIcon; Width = (int)Math.Round(38 * initialScale); Height = (int)Math.Round(88 * initialScale); BackColor = PixelPoleControl.KeyColor; TransparencyKey = PixelPoleControl.KeyColor; ForeColor = Color.White; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1"; StartPosition = FormStartPosition.Manual; Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 20, Screen.PrimaryScreen.WorkingArea.Top + 20); TopMost = true; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      BuildUi(); BuildTray(); BuildTaskbarMenu(); timer.Interval = settings.RefreshMs; timer.Tick += delegate { RefreshTasks(); }; timer.Start(); taskbarBlinkTimer.Interval = 500; taskbarBlinkTimer.Tick += delegate { taskbarBlinkOn = !taskbarBlinkOn; UpdateTaskbarBlink(); }; eventDebounceTimer.Interval = 900; eventDebounceTimer.Tick += delegate { eventDebounceTimer.Stop(); RefreshTasks(); };
      Shown += delegate { if (watchers == null) watchers = new MonitorWatchers(delegate(bool layoutChanged) { if (layoutChanged) engine.InvalidateDiscovery(); try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { if (!eventDebounceTimer.Enabled) eventDebounceTimer.Start(); })); } catch { } }); if (settings.TaskbarMode) Hide(); RefreshTasks(); if (!startupUpdateChecked && settings.AutoCheckUpdates && Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") != "1") { startupUpdateChecked = true; BeginUpdateCheck(true); } };
      DpiChanged += delegate { BeginInvoke(new Action(delegate { DpiSupport.KeepOnScreen(this); widget.Invalidate(); })); }; SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;
      FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); tray.ShowBalloonTip(900, "Agent Beacon", "仍在托盘监控，双击灯标可恢复。", ToolTipIcon.None); } };
    }
    void BuildUi() {
      Controls.Add(widget); widget.ScaleFactor = settings.LampScale / 100f; widget.SetAgents(new List<AgentTask>());
      widget.CenterClicked += delegate { ShowFullStatusCenter(); };
      widget.SettingsClicked += delegate { ShowSettings(); };
      widget.CloseClicked += delegate { Hide(); };
      widget.AgentActivated += delegate(string source) { AgentWindowActivator.Focus(source); };
      widget.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !widget.IsButton(e.Location) && widget.AgentAt(e.Location) == null) { dragging = true; dragOrigin = e.Location; } };
      widget.MouseMove += delegate(object s, MouseEventArgs e) { if (dragging) Location = new Point(Left + e.X - dragOrigin.X, Top + e.Y - dragOrigin.Y); };
      widget.MouseUp += delegate { dragging = false; };
    }
    void ToggleTaskCenter() {
      if (queuePopup != null && !queuePopup.IsDisposed) { queuePopup.Close(); queuePopup = null; return; }
      queuePopup = new TaskQueuePopup(delegate(AgentTask task) { AgentWindowActivator.Focus(task); }, delegate { ShowFullStatusCenter(); });
      queuePopup.FormClosed += delegate { queuePopup = null; };
      queuePopup.UpdateData(currentTasks, currentHealth); queuePopup.ShowNear(Bounds);
    }
    void ShowFullStatusCenter() { using (var form = new HistoryForm()) { if (settings.TaskbarMode) form.ShowDialog(); else form.ShowDialog(this); } }
    void HandleDisplaySettingsChanged(object sender, EventArgs e) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { DpiSupport.KeepOnScreen(this); })); } catch { } }
    void BuildTray() {
      tray.Icon = CachedCircleIcon("idle", Color.FromArgb(100, 116, 139)); tray.Visible = true; tray.Text = "Agent Beacon"; tray.DoubleClick += delegate { Show(); Activate(); };
      trayMenu.Items.Add("显示红绿灯", null, delegate { Show(); Activate(); }); trayMenu.Items.Add("立即刷新", null, delegate { RefreshTasks(); }); trayMenu.Items.Add("检查更新", null, delegate { BeginUpdateCheck(false); }); trayMenu.Items.Add("状态中心", null, delegate { ShowFullStatusCenter(); }); trayMenu.Items.Add("设置", null, delegate { ShowSettings(); }); trayMenu.Items.Add("退出", null, delegate { ExitApplication(); }); PixelTheme.StyleMenu(trayMenu); tray.ContextMenuStrip = trayMenu; ContextMenuStrip = trayMenu; widget.ContextMenuStrip = trayMenu;
    }
    void BuildTaskbarMenu() {
      taskbarMenu.Items.Add("切换到桌面灯杆", null, delegate { settings.TaskbarMode = false; Program.SaveSettings(settings); ApplyDisplayMode(); });
      taskbarMenu.Items.Add("立即刷新", null, delegate { RefreshTasks(); }); taskbarMenu.Items.Add("检查更新", null, delegate { BeginUpdateCheck(false); }); taskbarMenu.Items.Add("状态中心", null, delegate { ShowFullStatusCenter(); }); taskbarMenu.Items.Add("设置", null, delegate { ShowSettings(); }); taskbarMenu.Items.Add("退出", null, delegate { ExitApplication(); }); PixelTheme.StyleMenu(taskbarMenu);
    }
    void ShowSettings() { using (var dialog = new SettingsForm(settings, delegate(int ms) { timer.Interval = ms; }, delegate(bool enabled) { settings.TaskbarMode = enabled; ApplyDisplayMode(); }, delegate(int scale) { ApplyLampScale(); }, delegate { BeginUpdateCheck(false); })) { if (settings.TaskbarMode) dialog.ShowDialog(); else dialog.ShowDialog(this); } }
    public void OpenSettings() { ShowSettings(); }
    int BaseWidthForCount(int count) { count = Math.Max(1, Math.Min(4, count)); return count == 1 ? 38 : count == 2 ? 53 : count == 3 ? 68 : 75; }
    void ResizeWidgetForCount(int count) {
      float scale = settings.LampScale / 100f; int right = Right;
      widget.ScaleFactor = scale; Size = new Size((int)Math.Round(BaseWidthForCount(count) * scale), (int)Math.Round(88 * scale)); Left = right - Width;
    }
    void ApplyLampScale() { ResizeWidgetForCount(currentAgents.Count); widget.SetAgents(currentAgents); }
    void ApplyDisplayMode() {
      if (settings.TaskbarMode) { tray.Visible = false; Hide(); UpdateTaskbarLights(); }
      else { taskbarBlinkTimer.Stop(); ClearTaskbarLights(); tray.Visible = true; Show(); Activate(); }
    }
    Icon MakeIcon(Color color) { using (var bmp = new Bitmap(32, 32)) { using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using (var b = new SolidBrush(color)) g.FillEllipse(b, 4, 4, 24, 24); } IntPtr handle = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon; } }
    Icon CachedCircleIcon(string key, Color color) { Icon icon; string cacheKey = "circle:" + key; if (!iconCache.TryGetValue(cacheKey, out icon)) { icon = MakeIcon(color); iconCache[cacheKey] = icon; } return icon; }
    Icon MakeTaskbarIcon(string status, bool blink) {
      using (var bmp = new Bitmap(32, 32)) { using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.Clear(Color.Transparent);
        using (var body = new SolidBrush(Color.FromArgb(27, 34, 43))) g.FillRectangle(body, 8, 1, 16, 25);
        using (var edge = new Pen(Color.FromArgb(5, 8, 12), 2)) g.DrawRectangle(edge, 8, 1, 16, 25);
        DrawTaskbarLamp(g, 4, Color.FromArgb(255, 48, 60), status == State.Complete);
        DrawTaskbarLamp(g, 11, Color.FromArgb(255, 205, 32), status == State.Attention && blink);
        DrawTaskbarLamp(g, 18, Color.FromArgb(38, 231, 103), status == State.Running);
        using (var pole = new SolidBrush(Color.FromArgb(55, 65, 78))) g.FillRectangle(pole, 14, 27, 4, 5);
      } IntPtr handle = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon; }
    }
    void DrawTaskbarLamp(Graphics g, int y, Color color, bool active) { using (var brush = new SolidBrush(active ? color : Color.FromArgb(31, 38, 39))) g.FillRectangle(brush, 13, y, 6, 6); }
    string StatusText(string status) { return status == State.Running ? "进行中" : status == State.Attention ? "需要手动处理" : status == State.Complete ? "已结束" : "等待任务"; }
    string TaskTooltip(AgentTask task) { string value = task.Source + " · " + StatusText(task.Status) + " · " + (task.Detail ?? ""); return value.Length > 63 ? value.Substring(0, 63) : value; }
    Icon CachedTaskbarIcon(string status, bool blink) { string key = "taskbar:" + (status ?? "") + ":" + blink; Icon icon; if (!iconCache.TryGetValue(key, out icon)) { icon = MakeTaskbarIcon(status, blink); iconCache[key] = icon; } return icon; }
    void SetTaskbarIcon(NotifyIcon light, string status, bool blink) { light.Icon = CachedTaskbarIcon(status, blink); }
    void UpdateTaskbarLights() {
      if (!settings.TaskbarMode) return; bool attention = false;
      string layout = String.Join("|", currentAgents.ConvertAll(delegate(AgentTask task) { return task.Source; }).ToArray()); if (layout != taskbarLayoutSignature) { ClearTaskbarLights(); taskbarLayoutSignature = layout; }
      var wanted = new HashSet<string>(currentAgents.ConvertAll(delegate(AgentTask task) { return task.Source; }), StringComparer.OrdinalIgnoreCase);
      foreach (string source in new List<string>(taskbarLights.Keys)) if (!wanted.Contains(source)) { var stale = taskbarLights[source]; stale.Visible = false; stale.Icon = null; stale.Dispose(); taskbarLights.Remove(source); }
      if (currentAgents.Count == 0) {
        foreach (var light in taskbarLights.Values) light.Visible = false;
        if (taskbarLogin.Icon == null) SetTaskbarIcon(taskbarLogin, "", true); taskbarLogin.Text = "Agent Beacon · LOGIN..."; taskbarLogin.ContextMenuStrip = taskbarMenu; taskbarLogin.Visible = true;
      } else {
        taskbarLogin.Visible = false; taskbarLogin.Icon = null;
        foreach (var task in currentAgents) { NotifyIcon light; if (!taskbarLights.TryGetValue(task.Source, out light)) { string source = task.Source; light = new NotifyIcon(); light.ContextMenuStrip = taskbarMenu; light.DoubleClick += delegate { AgentWindowActivator.Focus(source); }; taskbarLights[task.Source] = light; } SetTaskbarIcon(light, task.Status, taskbarBlinkOn); light.Text = TaskTooltip(task); light.Visible = true; if (task.Status == State.Attention) attention = true; }
      }
      if (attention) taskbarBlinkTimer.Start(); else { taskbarBlinkTimer.Stop(); taskbarBlinkOn = true; }
    }
    void UpdateTaskbarBlink() { if (!settings.TaskbarMode) return; foreach (var task in currentAgents) if (task.Status == State.Attention) { NotifyIcon light; if (taskbarLights.TryGetValue(task.Source, out light)) SetTaskbarIcon(light, task.Status, taskbarBlinkOn); } }
    void ClearTaskbarLights() {
      foreach (var light in taskbarLights.Values) { light.Visible = false; light.Icon = null; light.Dispose(); } taskbarLights.Clear();
      taskbarLogin.Visible = false; taskbarLogin.Icon = null;
    }
    void RefreshTasks() {
      if (scanning) { pendingRescan = true; return; } long now = Util.Now(); if (lastScanStartedAt != 0 && now - lastScanStartedAt < 800) return; lastScanStartedAt = now; scanning = true;
      ThreadPool.QueueUserWorkItem(delegate {
        var cycle = new ScanCycle(); var watch = Stopwatch.StartNew();
        try {
          cycle.Runtime = AgentProcesses.Snapshot(); cycle.Tasks = engine.Scan(out cycle.FilesRead);
          var codexTasks = cycle.Tasks.FindAll(delegate(AgentTask task) { return task.Source == "Codex"; });
          AgentTask codexUiTarget = AgentStateRules.SelectCodexUiAttentionTarget(codexTasks);
          bool codexAlreadyAttention = codexTasks.Exists(delegate(AgentTask task) { return task.Status == State.Attention; });
          cycle.CodexUiAttention = codexUiTarget != null && !codexAlreadyAttention && AgentProcesses.CodexNeedsUserAttention(cycle.Runtime);
          if (cycle.CodexUiAttention) {
            long eventAt = Util.Now();
            cycle.Tasks.Add(new AgentTask { Id = "codex-ui-attention:" + codexUiTarget.Id, Source = "Codex", SessionId = codexUiTarget.SessionId, Title = codexUiTarget.Title, Cwd = codexUiTarget.Cwd, Status = State.Attention, Detail = "Codex 正在等待你的确认", Phase = "等待确认", Evidence = "Codex 当前可见审批卡", InteractionId = "ui:" + codexUiTarget.Id, StartedAt = codexUiTarget.StartedAt > 0 ? codexUiTarget.StartedAt : eventAt, UpdatedAt = eventAt, LastActivityAt = codexUiTarget.LastActivityAt });
          }
        } catch (Exception ex) { cycle.Error = ex.GetType().Name + ": " + ex.Message; }
        watch.Stop(); cycle.DurationMs = watch.ElapsedMilliseconds;
        try { using (var current = Process.GetCurrentProcess()) cycle.PrivateMemoryMb = Math.Max(1, current.PrivateMemorySize64 / (1024 * 1024)); } catch { }
        try { if (!IsDisposed) BeginInvoke(new Action<ScanCycle>(FinishCycle), cycle); else scanning = false; } catch { scanning = false; }
      });
    }
    void FinishCycle(ScanCycle cycle) {
      scanning = false;
      if (!String.IsNullOrWhiteSpace(cycle.Error)) DiagnosticsHub.RecordError(cycle.Error); else { lastGoodTasks = cycle.Tasks; ApplyTasks(cycle); }
      if (pendingRescan) { pendingRescan = false; eventDebounceTimer.Stop(); eventDebounceTimer.Start(); }
    }
    void ApplyTasks(ScanCycle cycle) {
      var tasks = cycle.Tasks ?? lastGoodTasks; var detected = LatestPerAgent(tasks); var runtime = cycle.Runtime ?? new AgentRuntimeSnapshot();
      var activeTasks = ActiveTaskRules.Active(tasks, runtime);
      bool allowClaudeToolOverride = ActiveTaskRules.AllowGlobalClaudeToolOverride(activeTasks);
      foreach (var task in activeTasks) if (allowClaudeToolOverride && task.Source == "Claude Code" && task.Status == State.Attention && AgentProcesses.ClaudeHasActiveToolProcess(task.UpdatedAt)) { task.Status = State.Running; task.Detail = "Shell 或工具正在执行"; task.Phase = "执行工具"; }
      var lifecycleTasks = ActiveTaskRules.Relevant(tasks, runtime, true); var lifecycleById = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);
      foreach (var task in lifecycleTasks) lifecycleById[task.Id] = task; foreach (var task in activeTasks) lifecycleById[task.Id] = task;
      lifecycleTasks = new List<AgentTask>(lifecycleById.Values);
      var health = ActiveTaskRules.Health(runtime, tasks, activeTasks); var agents = new List<AgentTask>();
      foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) if (runtime.Sources.Contains(source)) {
        long seenAt; if (!sourceSeenAt.TryGetValue(source, out seenAt)) { seenAt = runtime.CapturedAt > 0 ? runtime.CapturedAt : Util.Now(); sourceSeenAt[source] = seenAt; }
        long runtimeStarted = 0; runtime.StartedAt.TryGetValue(source, out runtimeStarted);
        var candidate = detected.Find(delegate(AgentTask t) { return t.Source == source; }); AgentTask previous = null; resolvedStates.TryGetValue(source, out previous);
        var resolved = ActiveTaskRules.Aggregate(source, activeTasks, candidate, runtimeStarted, seenAt, previous);
        var sourceHealth = health.Find(delegate(TaskSourceHealth item) { return item.Source == source; }); if (sourceHealth != null) { resolved.HealthState = sourceHealth.State; resolved.HealthDetail = sourceHealth.Detail; }
        agents.Add(resolved); resolvedStates[source] = AgentStateRules.Clone(resolved);
      }
      foreach (string source in new List<string>(sourceSeenAt.Keys)) if (!runtime.Sources.Contains(source)) { sourceSeenAt.Remove(source); resolvedStates.Remove(source); }
      currentAgents = agents; currentTasks = activeTasks; currentHealth = health; TaskCenterState.Update(activeTasks, health);
      if (queuePopup != null && !queuePopup.IsDisposed) queuePopup.UpdateData(activeTasks, health);
      ProcessStateTransitions(lifecycleTasks);
      UsageStatistics.Update(lifecycleTasks, Util.Now());
      cycle.EffectiveIntervalMs = AdaptiveScanPolicy.Interval(settings, agents); if (timer.Interval != cycle.EffectiveIntervalMs) timer.Interval = cycle.EffectiveIntervalMs;
      int red = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Complete; }).Count, yellow = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Attention; }).Count, green = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Running; }).Count;
      if (yellow > 0) tray.Icon = CachedCircleIcon("attention", Color.FromArgb(255, 199, 35)); else if (green > 0) tray.Icon = CachedCircleIcon("running", Color.FromArgb(35, 220, 105)); else if (red > 0) tray.Icon = CachedCircleIcon("complete", Color.FromArgb(255, 56, 72)); else tray.Icon = CachedCircleIcon("idle", Color.FromArgb(100, 116, 139));
      tray.Text = agents.Count == 0 ? "未检测到 Agent · LOGIN..." : String.Format("结束/空闲(红) {0} · 手动(黄) {1} · 进行(绿) {2}", red, yellow, green); DiagnosticsHub.Update(agents, cycle);
      var signature = new StringBuilder(); foreach (var task in agents) signature.Append(task.Source).Append(':').Append(task.Status).Append('|');
      string next = signature.ToString(); if (next == lastSignature) return; lastSignature = next;
      ResizeWidgetForCount(agents.Count); widget.SetAgents(agents);
      if (settings.TaskbarMode) { tray.Visible = false; Hide(); UpdateTaskbarLights(); }
    }
    void ProcessStateTransitions(List<AgentTask> agents) {
      var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var task in agents) {
        string transitionKey = String.IsNullOrWhiteSpace(task.Id) ? task.Source : task.Id; active.Add(transitionKey); string previous; bool existed = transitionStates.TryGetValue(transitionKey, out previous);
        if (!existed || !String.Equals(previous, task.Status, StringComparison.OrdinalIgnoreCase)) {
          transitionStates[transitionKey] = task.Status; StateHistory.Record(task);
          if ((transitionBaselineReady || task.Restored) && task.Status == State.Attention && NotificationPolicy.ShouldNotify(settings, task)) {
            string key = NotificationKey(task, "attention"); if (!sentAttentionNotifications.Contains(key)) pendingAttentionNotifications[key] = Util.Now() + NotificationPolicy.AttentionDelayMs(settings);
          } else if (transitionBaselineReady && existed && task.Status == State.Complete && NotificationPolicy.ShouldNotify(settings, task)) ShowStateNotification(task);
        }
      }
      foreach (string key in new List<string>(transitionStates.Keys)) if (!active.Contains(key)) transitionStates.Remove(key);
      FlushNotificationPolicy(currentTasks);
      transitionBaselineReady = true;
    }
    string NotificationKey(AgentTask task, string kind) { string interaction = kind == "attention" && !String.IsNullOrWhiteSpace(task.InteractionId) ? task.InteractionId : (task.Id ?? ""); return (task.Source ?? "") + "|" + interaction + "|" + kind; }
    void FlushNotificationPolicy(List<AgentTask> agents) {
      long now = Util.Now(); var activeAttention = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var task in agents) {
        if (task.Status == State.Attention) {
          string key = NotificationKey(task, "attention"); activeAttention.Add(key); long due;
          if (pendingAttentionNotifications.TryGetValue(key, out due) && now >= due && !sentAttentionNotifications.Contains(key) && NotificationPolicy.ShouldNotify(settings, task)) {
            ShowStateNotification(task); sentAttentionNotifications.Add(key); pendingAttentionNotifications.Remove(key);
          }
        } else if (NotificationPolicy.ShouldRemindLongRunning(settings, task, now)) {
          string key = NotificationKey(task, "long"); if (!sentLongRunningNotifications.Contains(key)) { ShowLongRunningNotification(task); sentLongRunningNotifications.Add(key); }
        }
      }
      foreach (string key in new List<string>(pendingAttentionNotifications.Keys)) if (!activeAttention.Contains(key)) pendingAttentionNotifications.Remove(key);
      if (sentAttentionNotifications.Count > 256) sentAttentionNotifications.Clear(); if (sentLongRunningNotifications.Count > 256) sentLongRunningNotifications.Clear();
    }
    void ShowStateNotification(AgentTask task) {
      string title = task.Status == State.Attention ? task.Source + " 需要你的处理" : task.Source + " 任务已结束";
      string body = String.IsNullOrWhiteSpace(task.Detail) ? (task.Status == State.Attention ? "请切换到对应 Agent 处理确认或输入。" : "任务已完成、失败或取消。") : task.Detail;
      NotifyIcon target = tray; NotifyIcon compact; if (settings.TaskbarMode && taskbarLights.TryGetValue(task.Source, out compact)) target = compact;
      try { target.ShowBalloonTip(5000, title, body, task.Status == State.Attention ? ToolTipIcon.Warning : ToolTipIcon.Info); if (task.Status == State.Attention) System.Media.SystemSounds.Exclamation.Play(); } catch { }
    }
    void ShowLongRunningNotification(AgentTask task) {
      NotifyIcon target = tray; NotifyIcon compact; if (settings.TaskbarMode && taskbarLights.TryGetValue(task.Source, out compact)) target = compact;
      try { target.ShowBalloonTip(5000, task.Source + " 仍在运行", "该任务已持续运行 " + UsageStatistics.Duration(Util.Now() - task.StartedAt) + "。", ToolTipIcon.Info); } catch { }
    }
    void BeginUpdateCheck(bool silent) {
      if (updateChecking) { if (!silent) PixelDialog.Show(this, "正在检查更新。", "检查更新", PixelDialogButtons.Ok); return; } updateChecking = true;
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          UpdateInfo info = UpdateService.CheckLatest();
          if (!IsDisposed) BeginInvoke(new Action(delegate { updateChecking = false; if (info == null) { if (!silent) PixelDialog.Show(this, "已是最新版本 v" + AppInfo.Version + "。", "检查更新", PixelDialogButtons.Ok); return; } if (PixelDialog.Show(this, "发现 v" + info.Version + "，是否立即更新？", "发现新版本", PixelDialogButtons.YesNo) == DialogResult.Yes) DownloadAndApplyUpdate(info); }));
        } catch (Exception ex) { if (!IsDisposed) BeginInvoke(new Action(delegate { updateChecking = false; if (!silent) PixelDialog.Show(this, "检查更新失败：\n" + ex.Message, "检查更新", PixelDialogButtons.Ok); })); }
      });
    }
    void DownloadAndApplyUpdate(UpdateInfo info) {
      updateChecking = true; string downloaded = null; Exception failure = null; IWin32Window owner = Form.ActiveForm; if (owner == null) owner = this;
      using (var progress = new PixelProgressForm("自动更新 v" + info.Version, false)) {
        progress.Shown += delegate { ThreadPool.QueueUserWorkItem(delegate { try { downloaded = UpdateService.Download(info, progress.Report); Thread.Sleep(350); } catch (Exception ex) { failure = ex; } finally { progress.Complete(); } }); };
        progress.ShowDialog(owner);
      }
      updateChecking = false;
      if (failure != null) { PixelDialog.Show(owner, "更新失败：\n" + failure.Message, "自动更新", PixelDialogButtons.Ok); return; }
      if (!String.IsNullOrWhiteSpace(downloaded)) { UpdateService.LaunchApply(downloaded, Application.ExecutablePath); ExitApplication(); }
    }
    List<AgentTask> LatestPerAgent(List<AgentTask> tasks) {
      var result = new List<AgentTask>();
      foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) {
        var sourceTasks = tasks.FindAll(delegate(AgentTask t) { return t.Source == source; }); if (sourceTasks.Count == 0) continue;
        result.Add(AgentStateRules.LatestForSource(source, sourceTasks));
      }
      return result;
    }
    public void ExitApplication() { quitting = true; UsageStatistics.Flush(); tray.Visible = false; taskbarBlinkTimer.Stop(); eventDebounceTimer.Stop(); ClearTaskbarLights(); Application.Exit(); }
    protected override void Dispose(bool disposing) { if (disposing) { SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged; tray.Visible = false; tray.Icon = null; taskbarBlinkTimer.Stop(); eventDebounceTimer.Stop(); if (watchers != null) watchers.Dispose(); ClearTaskbarLights(); taskbarLogin.Dispose(); trayMenu.Dispose(); taskbarMenu.Dispose(); tray.Dispose(); timer.Dispose(); taskbarBlinkTimer.Dispose(); eventDebounceTimer.Dispose(); foreach (var icon in iconCache.Values) icon.Dispose(); iconCache.Clear(); } base.Dispose(disposing); }
  }
}

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
}

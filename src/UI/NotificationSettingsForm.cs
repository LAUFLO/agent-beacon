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
}

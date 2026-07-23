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
    const int ControlButtonWidth = 48, ControlButtonHeight = 34, ControlButtonGap = 10, ControlButtonBaseGap = 10;
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
        DrawPole(g, new Point(center, 70), new Point(center, baseY)); DrawLoginDisplay(g, center, headY); DrawBase(g, center, baseY); DrawControls(g, center, baseY); return;
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
      DrawBase(g, center, baseY); DrawControls(g, center, baseY);
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
    void DrawControls(Graphics g, int center, int baseY) {
      int stackHeight = ControlButtonHeight * 3 + ControlButtonGap * 2;
      int y = baseY - ControlButtonBaseGap - stackHeight;
      CenterRect = new Rectangle(center - ControlButtonWidth / 2, y, ControlButtonWidth, ControlButtonHeight);
      SettingsRect = new Rectangle(center - ControlButtonWidth / 2, y + ControlButtonHeight + ControlButtonGap, ControlButtonWidth, ControlButtonHeight);
      CloseRect = new Rectangle(center - ControlButtonWidth / 2, y + (ControlButtonHeight + ControlButtonGap) * 2, ControlButtonWidth, ControlButtonHeight);
      DrawButton(g, CenterRect, false); DrawButton(g, SettingsRect, false); DrawButton(g, CloseRect, true);
      int cx = CenterRect.Left + CenterRect.Width / 2, cy = CenterRect.Top + CenterRect.Height / 2;
      using (var list = new SolidBrush(Color.FromArgb(184, 194, 207))) {
        g.FillRectangle(list, cx - 8, cy - 7, 4, 4); g.FillRectangle(list, cx - 1, cy - 7, 10, 4);
        g.FillRectangle(list, cx - 8, cy - 1, 4, 4); g.FillRectangle(list, cx - 1, cy - 1, 10, 4);
        g.FillRectangle(list, cx - 8, cy + 5, 4, 4); g.FillRectangle(list, cx - 1, cy + 5, 10, 4);
      }
      int sx = SettingsRect.Left + SettingsRect.Width / 2, sy = SettingsRect.Top + SettingsRect.Height / 2;
      using (var gear = new SolidBrush(Color.FromArgb(184, 194, 207))) {
        g.FillRectangle(gear, sx - 7, sy - 7, 14, 14); g.FillRectangle(gear, sx - 10, sy - 3, 20, 6); g.FillRectangle(gear, sx - 3, sy - 10, 6, 20);
      }
      using (var cutout = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(cutout, sx - 3, sy - 3, 6, 6);
      using (var cross = new Pen(Color.FromArgb(255, 82, 82), 4)) { g.DrawLine(cross, CloseRect.Left + 11, CloseRect.Top + 9, CloseRect.Right - 11, CloseRect.Bottom - 9); g.DrawLine(cross, CloseRect.Right - 11, CloseRect.Top + 9, CloseRect.Left + 11, CloseRect.Bottom - 9); }
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
}

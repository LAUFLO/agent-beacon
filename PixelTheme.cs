using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  static class PixelTheme {
    public const string FontName = "SimSun";
    public const string MonoFontName = "NSimSun";
    public static readonly Font TextFont = new Font(FontName, 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font StrongFont = new Font(FontName, 9f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font TitleFont = new Font(FontName, 10f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font SmallFont = new Font(FontName, 8f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font MonoFont = new Font(MonoFontName, 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Color Paper = Color.FromArgb(255, 255, 255);
    public static readonly Color Panel = Color.FromArgb(246, 247, 249);
    public static readonly Color Ink = Color.FromArgb(18, 22, 28);
    public static readonly Color Muted = Color.FromArgb(82, 91, 103);
    public static readonly Color Grid = Color.FromArgb(182, 188, 197);
    public static readonly Color Blue = Color.FromArgb(32, 107, 214);
    public static readonly Color Red = Color.FromArgb(226, 45, 59);
    public static readonly Color Yellow = Color.FromArgb(240, 177, 0);
    public static readonly Color Green = Color.FromArgb(16, 157, 88);
    public static readonly Color PaleBlue = Color.FromArgb(226, 239, 255);
    public static readonly Color PaleGreen = Color.FromArgb(220, 249, 232);
    public static readonly Color PaleRed = Color.FromArgb(255, 226, 230);
    public static readonly Icon AppIcon = LoadAppIcon();

    static Icon LoadAppIcon() {
      try {
        Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (icon != null) return icon;
      } catch { }
      return SystemIcons.Application;
    }

    public static Label Label(string text, Point location, Size size, bool heading) {
      return new Label { Text = text, AutoSize = false, Location = location, Size = size, BackColor = Color.Transparent, ForeColor = heading ? Ink : Muted, Font = heading ? StrongFont : SmallFont, TextAlign = ContentAlignment.MiddleCenter };
    }

    public static void PaintWindow(Graphics g, int width, int height, int dividerX) {
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.Clear(Paper);
      using (var ink = new SolidBrush(Ink)) {
        g.FillRectangle(ink, 0, 0, width, 6); g.FillRectangle(ink, 0, height - 6, width, 6); g.FillRectangle(ink, 0, 0, 6, height); g.FillRectangle(ink, width - 6, 0, 6, height);
        g.FillRectangle(ink, 8, 43, width - 16, 4); if (dividerX > 0) g.FillRectangle(ink, dividerX, 51, 4, height - 65);
      }
      using (var header = new SolidBrush(Panel)) g.FillRectangle(header, 6, 6, width - 12, 37);
      using (var grid = new Pen(Grid, 2)) g.DrawRectangle(grid, 8, 49, width - 17, height - 58);
      using (var red = new SolidBrush(Red)) g.FillRectangle(red, 16, 17, 9, 9);
      using (var yellow = new SolidBrush(Yellow)) g.FillRectangle(yellow, 29, 17, 9, 9);
      using (var green = new SolidBrush(Green)) g.FillRectangle(green, 42, 17, 9, 9);
    }

    public static void StyleMenu(ContextMenuStrip menu) {
      menu.Renderer = new PixelMenuRenderer(); menu.ShowImageMargin = false; menu.BackColor = Paper; menu.ForeColor = Ink; menu.DropShadowEnabled = false;
      menu.Font = StrongFont; menu.Padding = new Padding(3); menu.MinimumSize = Size.Empty;
      foreach (ToolStripItem item in menu.Items) { item.AutoSize = false; item.Size = new Size(136, 29); item.BackColor = Paper; item.ForeColor = Ink; item.Padding = new Padding(8, 3, 8, 3); item.Margin = Padding.Empty; }
    }
  }

  sealed class PixelButton : Control {
    bool hover, pressed, active, danger;
    public bool Active { get { return active; } set { active = value; Invalidate(); } }
    public bool Danger { get { return danger; } set { danger = value; Invalidate(); } }
    public PixelButton() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
      Cursor = Cursors.Hand; TabStop = true; Font = PixelTheme.StrongFont; ForeColor = PixelTheme.Ink; BackColor = PixelTheme.Paper;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      Color fill = pressed ? PixelTheme.Grid : active ? PixelTheme.PaleGreen : danger && hover ? PixelTheme.PaleRed : hover ? PixelTheme.PaleBlue : PixelTheme.Paper;
      g.Clear(Parent == null ? PixelTheme.Paper : Parent.BackColor);
      using (var shadow = new SolidBrush(PixelTheme.Grid)) g.FillRectangle(shadow, 4, 4, Width - 4, Height - 4);
      using (var black = new SolidBrush(PixelTheme.Ink)) g.FillRectangle(black, 0, 0, Width - 4, Height - 4);
      using (var body = new SolidBrush(fill)) g.FillRectangle(body, 3, 3, Width - 10, Height - 10);
      if (active) using (var mark = new SolidBrush(PixelTheme.Green)) g.FillRectangle(mark, 6, 6, 5, Height - 16);
      if (danger && hover) using (var mark = new SolidBrush(PixelTheme.Red)) g.FillRectangle(mark, 6, 6, 5, Height - 16);
      TextRenderer.DrawText(g, Text, Font, new Rectangle(5, 2, Width - 14, Height - 10), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
      if (Focused) using (var focus = new Pen(PixelTheme.Blue, 2)) g.DrawRectangle(focus, 5, 5, Width - 15, Height - 15);
    }
  }

  sealed class PixelToggle : Control {
    bool isChecked, hover;
    public bool Checked { get { return isChecked; } set { if (isChecked == value) return; isChecked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
    public event EventHandler CheckedChanged;
    public PixelToggle() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; Height = 24; Font = PixelTheme.TextFont; ForeColor = PixelTheme.Ink; BackColor = PixelTheme.Paper; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      g.Clear(Parent == null ? PixelTheme.Paper : Parent.BackColor);
      using (var shadow = new SolidBrush(PixelTheme.Grid)) g.FillRectangle(shadow, 3, 6, 18, 18);
      using (var outer = new SolidBrush(hover ? PixelTheme.Blue : PixelTheme.Ink)) g.FillRectangle(outer, 0, 3, 19, 19);
      using (var well = new SolidBrush(PixelTheme.Paper)) g.FillRectangle(well, 3, 6, 13, 13);
      if (Checked) using (var on = new SolidBrush(PixelTheme.Green)) { g.FillRectangle(on, 6, 9, 7, 7); g.FillRectangle(on, 8, 7, 3, 11); }
      TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 28, Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
      if (Focused) using (var focus = new Pen(PixelTheme.Blue, 2)) g.DrawRectangle(focus, 25, 2, Width - 27, Height - 5);
    }
  }

  sealed class PixelMenuRenderer : ToolStripProfessionalRenderer {
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(PixelTheme.Paper); }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var pen = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(pen, 1, 1, e.ToolStrip.Width - 3, e.ToolStrip.Height - 3); }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
      Rectangle area = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
      using (var fill = new SolidBrush(e.Item.Selected ? PixelTheme.PaleGreen : PixelTheme.Paper)) e.Graphics.FillRectangle(fill, area);
      if (e.Item.Selected) using (var pen = new Pen(PixelTheme.Ink, 2)) e.Graphics.DrawRectangle(pen, area.X, area.Y, area.Width - 1, area.Height - 1);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = PixelTheme.Ink; base.OnRenderItemText(e); }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var pen = new Pen(PixelTheme.Ink, 2)) e.Graphics.DrawLine(pen, 7, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2); }
  }

  enum PixelDialogButtons { Ok, YesNo }

  sealed class PixelDialog : Form {
    bool dragging; Point dragOrigin; readonly PixelDialogButtons buttons; readonly bool spacious;
    PixelDialog(string message, string title, PixelDialogButtons options) {
      buttons = options; spacious = (message ?? "").Length > 90 || (message ?? "").Split('\n').Length > 2; int width = spacious ? 500 : 400, height = spacious ? 230 : 152;
      Text = title; Icon = PixelTheme.AppIcon; ClientSize = new Size(width, height); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false;
      ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1"; StartPosition = FormStartPosition.CenterParent; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true; KeyPreview = true;
      var heading = PixelTheme.Label(String.IsNullOrWhiteSpace(title) ? "AGENT BEACON" : title.ToUpperInvariant(), new Point(60, 8), new Size(width - 120, 32), true); Controls.Add(heading);
      var close = new PixelButton { Text = "X", Danger = true, Location = new Point(width - 44, 9), Size = new Size(31, 29) }; close.Click += delegate { DialogResult = options == PixelDialogButtons.YesNo ? DialogResult.No : DialogResult.OK; Close(); }; Controls.Add(close);
      if (spacious) {
        var text = new TextBox { Text = message ?? "", Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = PixelTheme.Paper, ForeColor = PixelTheme.Ink, Location = new Point(28, 68), Size = new Size(width - 56, 92), Font = PixelTheme.TextFont, ScrollBars = ScrollBars.Vertical, TabStop = false }; Controls.Add(text);
      } else {
        var text = PixelTheme.Label(message ?? "", new Point(28, 60), new Size(width - 56, 34), false); text.ForeColor = PixelTheme.Ink; text.Font = PixelTheme.TextFont; text.TextAlign = ContentAlignment.MiddleCenter; Controls.Add(text);
      }
      int buttonY = spacious ? height - 50 : height - 44;
      if (options == PixelDialogButtons.YesNo) {
        var no = new PixelButton { Text = "取消", Location = new Point(width - 226, buttonY), Size = new Size(96, 34) }; no.Click += delegate { DialogResult = DialogResult.No; Close(); }; Controls.Add(no);
        var yes = new PixelButton { Text = "确认", Active = true, Location = new Point(width - 118, buttonY), Size = new Size(96, 34) }; yes.Click += delegate { DialogResult = DialogResult.Yes; Close(); }; Controls.Add(yes);
      } else {
        var ok = new PixelButton { Text = "确定", Active = true, Location = new Point(width - 118, buttonY), Size = new Size(96, 34) }; ok.Click += delegate { DialogResult = DialogResult.OK; Close(); }; Controls.Add(ok);
      }
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; heading.MouseDown += BeginDrag; heading.MouseMove += ContinueDrag; heading.MouseUp += EndDrag; Shown += delegate { DpiSupport.KeepOnScreen(this); };
    }

    public static DialogResult Show(IWin32Window owner, string message, string title, PixelDialogButtons buttons) { using (var dialog = new PixelDialog(message, title, buttons)) return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner); }
    public static DialogResult Show(string message, string title) { return Show(null, message, title, PixelDialogButtons.Ok); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { DialogResult = buttons == PixelDialogButtons.YesNo ? DialogResult.No : DialogResult.OK; Close(); e.Handled = true; } else if (e.KeyCode == Keys.Enter) { DialogResult = buttons == PixelDialogButtons.YesNo ? DialogResult.Yes : DialogResult.OK; Close(); e.Handled = true; } base.OnKeyDown(e); }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 23, spacious ? 62 : 56, Width - 47, spacious ? 103 : 42); base.OnPaint(e); }
  }

  sealed class PixelProgressBar : Control {
    int value;
    public int Value { get { return value; } set { int next = Math.Max(0, Math.Min(100, value)); if (next == this.value) return; this.value = next; Invalidate(); } }
    public PixelProgressBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); BackColor = PixelTheme.Paper; AccessibleName = "更新进度"; }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.Clear(PixelTheme.Paper);
      using (var ink = new SolidBrush(PixelTheme.Ink)) { g.FillRectangle(ink, 0, 0, Width, 4); g.FillRectangle(ink, 0, Height - 4, Width, 4); g.FillRectangle(ink, 0, 0, 4, Height); g.FillRectangle(ink, Width - 4, 0, 4, Height); }
      int segments = 10, gap = 3, innerX = 7, innerY = 7, innerWidth = Width - 14, innerHeight = Height - 14, segmentWidth = (innerWidth - gap * (segments - 1)) / segments;
      int filled = value == 0 ? 0 : Math.Min(segments, (value + 9) / 10);
      for (int i = 0; i < segments; i++) { Rectangle block = new Rectangle(innerX + i * (segmentWidth + gap), innerY, segmentWidth, innerHeight); using (var brush = new SolidBrush(i < filled ? PixelTheme.Green : PixelTheme.Panel)) g.FillRectangle(brush, block); }
      base.OnPaint(e);
    }
  }

  sealed class PixelProgressForm : Form {
    readonly PixelProgressBar progress = new PixelProgressBar(); readonly Label status, percent; bool allowClose, dragging; Point dragOrigin;
    public PixelProgressForm(string title, bool preview) {
      Text = title; Icon = PixelTheme.AppIcon; ClientSize = new Size(440, 170); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1";
      StartPosition = FormStartPosition.CenterParent; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var heading = PixelTheme.Label(String.IsNullOrWhiteSpace(title) ? "自动更新" : title, new Point(60, 8), new Size(320, 32), true); Controls.Add(heading);
      if (preview) { allowClose = true; var close = new PixelButton { Text = "X", Danger = true, Location = new Point(396, 9), Size = new Size(31, 29) }; close.Click += delegate { Close(); }; Controls.Add(close); }
      status = PixelTheme.Label("准备下载…", new Point(24, 55), new Size(392, 28), true); Controls.Add(status);
      progress.Location = new Point(28, 88); progress.Size = new Size(384, 34); Controls.Add(progress);
      percent = PixelTheme.Label("0%", new Point(24, 128), new Size(392, 24), true); Controls.Add(percent);
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; heading.MouseDown += BeginDrag; heading.MouseMove += ContinueDrag; heading.MouseUp += EndDrag;
      FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; }; Shown += delegate { DpiSupport.KeepOnScreen(this); };
    }
    public void Report(int value, string message) { if (IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(new Action<int, string>(Report), value, message); } catch { } return; } progress.Value = value; status.Text = String.IsNullOrWhiteSpace(message) ? "正在更新…" : message; percent.Text = value + "%"; }
    public void Complete() { if (IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(new Action(Complete)); } catch { } return; } allowClose = true; Close(); }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); base.OnPaint(e); }
  }
}

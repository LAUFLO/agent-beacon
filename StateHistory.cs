using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  sealed class StateHistoryItem {
    public long At;
    public string Source;
    public string Status;
    public string Detail;
    public string Evidence;
  }

  static class StateHistory {
    static readonly object Sync = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string FilePath { get { string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_HISTORY_PATH"); return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "state-history.jsonl") : Path.GetFullPath(configured); } }

    public static void Record(AgentTask task) {
      if (task == null || String.IsNullOrWhiteSpace(task.Source)) return;
      var item = new StateHistoryItem {
        At = Util.Now(), Source = task.Source, Status = task.Status,
        Detail = Safe(task.Detail, 80), Evidence = Safe(task.Evidence, 100)
      };
      lock (Sync) {
        try {
          Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
          File.AppendAllText(FilePath, Json.Serialize(item) + Environment.NewLine, new UTF8Encoding(false));
          var info = new FileInfo(FilePath); if (info.Length > 512 * 1024) Compact();
        } catch { }
      }
    }

    public static List<StateHistoryItem> Recent(int maximum) {
      lock (Sync) {
        var result = new List<StateHistoryItem>();
        try {
          if (!File.Exists(FilePath)) return result;
          string text = Util.Tail(FilePath, 256 * 1024);
          foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
            try { var item = Json.Deserialize<StateHistoryItem>(line); if (item != null) result.Add(item); } catch { }
          }
        } catch { }
        if (result.Count > maximum) result.RemoveRange(0, result.Count - maximum);
        result.Reverse(); return result;
      }
    }

    public static void Clear() { lock (Sync) { try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { } } }

    static void Compact() {
      var rows = Recent(300); rows.Reverse(); var text = new StringBuilder();
      foreach (var row in rows) text.AppendLine(Json.Serialize(row));
      File.WriteAllText(FilePath, text.ToString(), new UTF8Encoding(false));
    }

    static string Safe(string value, int maximum) {
      if (String.IsNullOrWhiteSpace(value)) return "";
      string clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
      return clean.Length > maximum ? clean.Substring(0, maximum) : clean;
    }
  }

  sealed class PixelScrollBar : Control {
    int maximum, largeChange = 1, value, dragOffset; bool dragging;
    public event EventHandler ValueChanged;
    public int Maximum { get { return maximum; } set { maximum = Math.Max(0, value); Value = this.value; Invalidate(); } }
    public int LargeChange { get { return largeChange; } set { largeChange = Math.Max(1, value); Invalidate(); } }
    public int Value { get { return value; } set { int next = Math.Max(0, Math.Min(maximum, value)); if (next == this.value) return; this.value = next; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } }
    public PixelScrollBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true); Width = 18; BackColor = PixelTheme.Paper; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ScrollBar; AccessibleName = "像素滚动条"; }
    Rectangle TrackRect { get { return new Rectangle(2, 20, Width - 4, Math.Max(1, Height - 40)); } }
    Rectangle ThumbRect {
      get { Rectangle track = TrackRect; int total = Math.Max(1, maximum + largeChange), thumbHeight = Math.Max(20, track.Height * largeChange / total); thumbHeight = Math.Min(track.Height, thumbHeight); int travel = Math.Max(0, track.Height - thumbHeight), y = track.Y + (maximum == 0 ? 0 : travel * value / maximum); return new Rectangle(3, y, Width - 6, thumbHeight); }
    }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.Clear(PixelTheme.Paper);
      using (var track = new SolidBrush(PixelTheme.Panel)) g.FillRectangle(track, TrackRect);
      Rectangle thumb = ThumbRect; using (var ink = new SolidBrush(PixelTheme.Ink)) { g.FillRectangle(ink, 0, 0, Width, 3); g.FillRectangle(ink, 0, Height - 3, Width, 3); g.FillRectangle(ink, 0, 0, 3, Height); g.FillRectangle(ink, Width - 3, 0, 3, Height); g.FillRectangle(ink, 2, 18, Width - 4, 3); g.FillRectangle(ink, 2, Height - 21, Width - 4, 3); DrawArrow(g, true); DrawArrow(g, false); }
      if (maximum > 0) { using (var ink = new SolidBrush(PixelTheme.Ink)) g.FillRectangle(ink, thumb); using (var fill = new SolidBrush(PixelTheme.Green)) g.FillRectangle(fill, thumb.X + 3, thumb.Y + 3, Math.Max(1, thumb.Width - 6), Math.Max(1, thumb.Height - 6)); }
      base.OnPaint(e);
    }
    void DrawArrow(Graphics g, bool up) { int center = Width / 2, baseY = up ? 11 : Height - 12, direction = up ? -1 : 1; using (var ink = new SolidBrush(PixelTheme.Ink)) { g.FillRectangle(ink, center - 3, baseY, 7, 2); g.FillRectangle(ink, center - 2, baseY + direction * 2, 5, 2); g.FillRectangle(ink, center - 1, baseY + direction * 4, 3, 2); } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button != MouseButtons.Left) return; Rectangle thumb = ThumbRect; if (e.Y < 20) Value--; else if (e.Y >= Height - 20) Value++; else if (thumb.Contains(e.Location)) { dragging = true; dragOffset = e.Y - thumb.Y; Capture = true; } else Value += e.Y < thumb.Y ? -largeChange : largeChange; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!dragging || maximum == 0) return; Rectangle track = TrackRect, thumb = ThumbRect; int travel = Math.Max(1, track.Height - thumb.Height), offset = Math.Max(0, Math.Min(travel, e.Y - track.Y - dragOffset)); Value = (int)Math.Round(offset * (double)maximum / travel); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
    protected override void OnMouseWheel(MouseEventArgs e) { Value -= Math.Sign(e.Delta) * 3; base.OnMouseWheel(e); }
  }

  sealed class PixelLogBox : Control {
    const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_LINESCROLL = 0xB6;
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    readonly TextBox editor = new TextBox(); readonly PixelScrollBar scroll = new PixelScrollBar(); bool syncing;
    public PixelLogBox() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ContainerControl, true); BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.MonoFont;
      editor.Multiline = true; editor.ReadOnly = true; editor.ScrollBars = ScrollBars.None; editor.WordWrap = false; editor.BorderStyle = BorderStyle.None; editor.TabStop = false; editor.BackColor = PixelTheme.Paper; editor.ForeColor = PixelTheme.Ink; editor.Font = PixelTheme.MonoFont;
      scroll.ValueChanged += delegate { if (!syncing) ScrollTo(scroll.Value); }; editor.TextChanged += delegate { RefreshRange(); }; editor.MouseWheel += delegate(object sender, MouseEventArgs e) { var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; scroll.Value -= Math.Sign(e.Delta) * 3; }; editor.KeyUp += delegate { BeginSync(); }; editor.MouseUp += delegate { BeginSync(); };
      Controls.Add(editor); Controls.Add(scroll);
    }
    public override string Text { get { return editor.Text; } set { editor.Text = value ?? ""; editor.SelectionStart = 0; ScrollTo(0); RefreshRange(); } }
    protected override void OnResize(EventArgs e) { base.OnResize(e); editor.Location = Point.Empty; editor.Size = new Size(Math.Max(1, Width - 22), Height); scroll.Location = new Point(Math.Max(0, Width - 18), 0); scroll.Size = new Size(18, Height); RefreshRange(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); editor.Font = Font; RefreshRange(); }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); editor.ForeColor = ForeColor; }
    protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); editor.BackColor = BackColor; scroll.BackColor = BackColor; }
    void RefreshRange() { if (!editor.IsHandleCreated) return; int lines = Math.Max(1, editor.GetLineFromCharIndex(editor.TextLength) + 1), visible = Math.Max(1, editor.ClientSize.Height / Math.Max(1, editor.Font.Height)); scroll.LargeChange = visible; scroll.Maximum = Math.Max(0, lines - visible); SyncFromEditor(); }
    void ScrollTo(int line) { if (!editor.IsHandleCreated) return; int current = FirstVisibleLine(), target = Math.Max(0, Math.Min(scroll.Maximum, line)); if (target != current) SendMessage(editor.Handle, EM_LINESCROLL, IntPtr.Zero, new IntPtr(target - current)); SyncFromEditor(); }
    int FirstVisibleLine() { return editor.IsHandleCreated ? SendMessage(editor.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero).ToInt32() : 0; }
    void BeginSync() { if (!IsHandleCreated) return; try { BeginInvoke(new Action(SyncFromEditor)); } catch { } }
    void SyncFromEditor() { if (!editor.IsHandleCreated) return; syncing = true; scroll.Value = FirstVisibleLine(); syncing = false; }
  }

  sealed class HistoryForm : Form {
    readonly PixelLogBox diagnostic = new PixelLogBox(), content = new PixelLogBox(); bool dragging; Point dragOrigin;
    public HistoryForm() {
      Text = "Agent Beacon 状态中心"; Icon = PixelTheme.AppIcon; ClientSize = new Size(700, 520); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false;
      ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1"; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var title = PixelTheme.Label("AGENT BEACON // 状态中心", new Point(60, 8), new Size(550, 32), true); Controls.Add(title);
      var close = new PixelButton { Text = "X", Danger = true, Location = new Point(659, 9), Size = new Size(28, 27) }; close.Click += delegate { Close(); }; Controls.Add(close);
      Controls.Add(PixelTheme.Label("实时状态诊断", new Point(24, 58), new Size(200, 24), true));
      Controls.Add(PixelTheme.Label("显示当前颜色、判定原因、扫描耗时和异常。", new Point(180, 58), new Size(420, 24), false));
      diagnostic.Location = new Point(30, 88); diagnostic.Size = new Size(640, 82); diagnostic.BackColor = PixelTheme.Paper; diagnostic.ForeColor = PixelTheme.Ink; diagnostic.Font = PixelTheme.MonoFont; Controls.Add(diagnostic);
      Controls.Add(PixelTheme.Label("状态变化历史", new Point(24, 190), new Size(200, 24), true));
      Controls.Add(PixelTheme.Label("仅保存时间、Agent、颜色、说明与判定来源，不保存聊天正文。", new Point(180, 190), new Size(485, 24), false));
      content.Location = new Point(30, 220); content.Size = new Size(640, 222); content.BackColor = PixelTheme.Paper; content.ForeColor = PixelTheme.Ink; content.Font = PixelTheme.MonoFont; Controls.Add(content);
      var refresh = new PixelButton { Text = "刷新全部", Active = true, Location = new Point(24, 462), Size = new Size(108, 34) }; refresh.Click += delegate { Reload(); }; Controls.Add(refresh);
      var copyDiagnostic = new PixelButton { Text = "复制诊断", Location = new Point(148, 462), Size = new Size(108, 34) }; copyDiagnostic.Click += delegate { try { Clipboard.SetText(diagnostic.Text); copyDiagnostic.Text = "已复制"; } catch { copyDiagnostic.Text = "复制失败"; } }; Controls.Add(copyDiagnostic);
      var copyHistory = new PixelButton { Text = "复制历史", Location = new Point(272, 462), Size = new Size(108, 34) }; copyHistory.Click += delegate { try { Clipboard.SetText(content.Text); copyHistory.Text = "已复制"; } catch { copyHistory.Text = "复制失败"; } }; Controls.Add(copyHistory);
      var clear = new PixelButton { Text = "清空历史", Danger = true, Location = new Point(568, 462), Size = new Size(108, 34) }; clear.Click += delegate { if (PixelDialog.Show(this, "确定清空本机状态变化历史吗？\n\n实时诊断不会被清除。", "清空状态历史", PixelDialogButtons.YesNo) == DialogResult.Yes) { StateHistory.Clear(); Reload(); } }; Controls.Add(clear);
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag;
      Shown += delegate { DpiSupport.KeepOnScreen(this); Reload(); };
    }
    void Reload() {
      diagnostic.Text = DiagnosticsHub.Report();
      var text = new StringBuilder();
      foreach (var item in StateHistory.Recent(300)) {
        string at = DateTimeOffset.FromUnixTimeMilliseconds(item.At).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        text.Append(at).Append("  ").Append(item.Source).Append("  ").Append(Display(item.Status)).Append("  ").Append(item.Detail).Append("  [").Append(item.Evidence).AppendLine("]");
      }
      content.Text = text.Length == 0 ? "暂无状态变化记录。" : text.ToString();
    }
    static string Display(string status) { return status == State.Attention ? "黄/需处理" : status == State.Running ? "绿/进行中" : "红/结束"; }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); using (var ink = new Pen(PixelTheme.Ink, 4)) { e.Graphics.DrawRectangle(ink, 24, 82, 652, 94); e.Graphics.DrawRectangle(ink, 24, 214, 652, 234); } base.OnPaint(e); }
  }
}

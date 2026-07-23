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

  sealed class PixelPage : Panel {
    public PixelPage() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); DoubleBuffered = true; }
  }

  sealed class HistoryForm : Form {
    const int WM_SETREDRAW = 0x000B;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    readonly PixelLogBox diagnostic = new PixelLogBox(), content = new PixelLogBox();
    readonly PixelPage overviewPage = new PixelPage(), morePage = new PixelPage();
    readonly Label title;
    readonly PixelButton moreButton, backButton;
    readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    bool dragging, reloading;
    Point dragOrigin;
    string overviewSignature = "", moreSignature = "";

    public HistoryForm() {
      Text = "Agent Beacon 状态中心"; Icon = PixelTheme.AppIcon; ClientSize = new Size(760, 438); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false;
      ShowInTaskbar = Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1"; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      title = PixelTheme.Label("AGENT BEACON v" + AppInfo.Version + " // 状态中心", new Point(95, 8), new Size(550, 32), true); Controls.Add(title);
      backButton = new PixelButton { Text = "返回", Location = new Point(15, 9), Size = new Size(70, 27), Visible = false }; backButton.Click += delegate { ShowOverview(); }; Controls.Add(backButton);
      moreButton = new PixelButton { Text = "更多", Location = new Point(650, 9), Size = new Size(58, 27) }; moreButton.Click += delegate { ShowMore(); }; Controls.Add(moreButton);
      var close = new PixelButton { Text = "X", Danger = true, Location = new Point(719, 9), Size = new Size(28, 27) }; close.Click += delegate { Close(); }; Controls.Add(close);
      overviewPage.Location = new Point(6, 46); overviewPage.Size = new Size(748, 386); overviewPage.BackColor = PixelTheme.Paper; overviewPage.Paint += delegate(object sender, PaintEventArgs e) { using (var line = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawLine(line, 0, 350, 748, 350); }; Controls.Add(overviewPage);
      morePage.Location = overviewPage.Location; morePage.Size = overviewPage.Size; morePage.BackColor = PixelTheme.Paper; morePage.Visible = false; Controls.Add(morePage);
      BuildMorePage();
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag;
      refreshTimer.Interval = 1500; refreshTimer.Tick += delegate { Reload(); };
      Shown += delegate { DpiSupport.KeepOnScreen(this); ShowOverview(); refreshTimer.Start(); };
      FormClosed += delegate { refreshTimer.Stop(); };
    }

    void ShowOverview() {
      morePage.Visible = false; overviewPage.Visible = true; backButton.Visible = false; moreButton.Visible = true;
      title.Text = "AGENT BEACON v" + AppInfo.Version + " // 状态中心"; overviewSignature = ""; Reload();
    }

    void ShowMore() {
      overviewPage.Visible = false; morePage.Visible = true; moreButton.Visible = false; backButton.Visible = true;
      title.Text = "AGENT BEACON v" + AppInfo.Version + " // 更多诊断"; moreSignature = ""; Reload();
    }

    void Reload() {
      if (reloading || IsDisposed || Disposing) return;
      reloading = true;
      try { if (overviewPage.Visible) ReloadOverview(); else ReloadMore(); }
      finally { reloading = false; }
    }

    void ReloadOverview() {
      var tasks = ActiveTaskRules.Collapse(TaskCenterState.Tasks()); var history = StateHistory.Recent(300);
      string signature = OverviewSignature(tasks, TaskCenterState.Health(), history); if (String.Equals(signature, overviewSignature, StringComparison.Ordinal)) return; overviewSignature = signature;
      RenderAtomically(overviewPage, delegate {
        overviewPage.Controls.Clear();
        string[] sources = { "Codex", "TRAE", "Claude Code", "OpenCode" };
        for (int i = 0; i < sources.Length; i++) AddSummaryCard(sources[i], 10 + i * 182, tasks, history);
        var waiting = tasks.FindAll(delegate(AgentTask task) { return task.Status == State.Attention; });
        var running = tasks.FindAll(delegate(AgentTask task) { return task.Status == State.Running; });
        int waitingRows = Math.Min(3, waiting.Count);
        AddSectionHeader("待我处理", waiting.Count, PixelTheme.Yellow, 88);
        if (waiting.Count > 0) AddDismissAllButton(waiting, 88);
        if (waiting.Count == 0) overviewPage.Controls.Add(PixelTheme.Label("当前没有需要处理的任务", new Point(20, 112), new Size(708, 36), false));
        else for (int i = 0; i < waitingRows; i++) AddTaskRow(waiting[i], 112 + i * 40, true);
        int runningHeaderY = waiting.Count == 0 ? 158 : 118 + waitingRows * 40;
        int runningRowsY = runningHeaderY + 24;
        int runningRows = Math.Min(running.Count, Math.Min(3, Math.Max(0, 5 - waitingRows)));
        AddSectionHeader("正在运行", running.Count, PixelTheme.Green, runningHeaderY);
        if (running.Count == 0) overviewPage.Controls.Add(PixelTheme.Label("当前没有正在运行的任务", new Point(20, runningRowsY), new Size(708, 36), false));
        else for (int i = 0; i < runningRows; i++) AddTaskRow(running[i], runningRowsY + i * 40, false);
        string at = DateTime.Now.ToString("HH:mm:ss");
        overviewPage.Controls.Add(new Label { Text = "最后更新  " + at, Location = new Point(18, 357), Size = new Size(190, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = PixelTheme.Muted, Font = PixelTheme.SmallFont });
        overviewPage.Controls.Add(new Label { Text = "仅显示项目简称、状态和时间，不保存聊天正文", Location = new Point(350, 357), Size = new Size(378, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent, ForeColor = PixelTheme.Muted, Font = PixelTheme.SmallFont });
      });
    }

    void AddSummaryCard(string source, int x, List<AgentTask> tasks, List<StateHistoryItem> history) {
      int waiting = tasks.FindAll(delegate(AgentTask task) { return task.Source == source && task.Status == State.Attention; }).Count;
      int running = tasks.FindAll(delegate(AgentTask task) { return task.Source == source && task.Status == State.Running; }).Count;
      long dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
      int completed = history.FindAll(delegate(StateHistoryItem item) { return item.Source == source && item.Status == State.Complete && item.At >= dayStart; }).Count;
      Color color = waiting > 0 ? PixelTheme.Yellow : running > 0 ? PixelTheme.Green : completed > 0 ? PixelTheme.Red : PixelTheme.Muted;
      var panel = new Panel { Location = new Point(x, 6), Size = new Size(172, 76), BackColor = PixelTheme.Paper };
      panel.Paint += delegate(object sender, PaintEventArgs e) { using (var shadow = new SolidBrush(PixelTheme.Grid)) e.Graphics.FillRectangle(shadow, 6, 6, panel.Width - 6, panel.Height - 6); using (var paper = new SolidBrush(PixelTheme.Paper)) e.Graphics.FillRectangle(paper, 1, 1, panel.Width - 8, panel.Height - 8); using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 1, 1, panel.Width - 8, panel.Height - 8); using (var lamp = new SolidBrush(color)) e.Graphics.FillRectangle(lamp, 15, 20, 14, 14); using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 12, 17, 20, 20); };
      panel.Controls.Add(new Label { Text = source, Location = new Point(43, 10), Size = new Size(118, 26), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = PixelTheme.Ink, Font = PixelTheme.StrongFont });
      string state = waiting > 0 ? "等待 " + waiting + (running > 0 ? "   运行 " + running : "") : running > 0 ? "运行 " + running : completed > 0 ? "完成 " + completed : "空闲";
      panel.Controls.Add(new Label { Text = state, Location = new Point(14, 39), Size = new Size(146, 24), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = color, Font = PixelTheme.StrongFont });
      overviewPage.Controls.Add(panel);
    }

    void AddSectionHeader(string name, int count, Color color, int y) {
      overviewPage.Controls.Add(new Label { Text = name + "  " + count, Location = new Point(14, y), Size = new Size(220, 24), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = color, Font = PixelTheme.TitleFont });
    }

    void AddDismissAllButton(List<AgentTask> waiting, int y) {
      var dismissAll = new PixelButton { Text = "清除全部", Danger = true, Location = new Point(624, y - 1), Size = new Size(104, 26) };
      dismissAll.Click += delegate {
        if (PixelDialog.Show(this, "清除当前全部等待状态吗？\n\n同一会话有新的 MCP 或 Agent 事件时会自动恢复监控。", "清除全部等待", PixelDialogButtons.YesNo) == DialogResult.Yes) {
          TaskCenterState.DismissAll(waiting); overviewSignature = ""; ReloadOverview();
        }
      };
      overviewPage.Controls.Add(dismissAll);
    }

    void AddTaskRow(AgentTask task, int y, bool actionable) {
      var row = new Panel { Location = new Point(14, y), Size = new Size(720, 38), BackColor = PixelTheme.Paper };
      Color accent = task.Status == State.Attention ? PixelTheme.Yellow : PixelTheme.Green;
      row.Paint += delegate(object sender, PaintEventArgs e) { using (var shadow = new SolidBrush(PixelTheme.Grid)) e.Graphics.FillRectangle(shadow, 5, 5, row.Width - 5, row.Height - 5); using (var paper = new SolidBrush(PixelTheme.Paper)) e.Graphics.FillRectangle(paper, 1, 1, row.Width - 7, row.Height - 7); using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 1, 1, row.Width - 7, row.Height - 7); using (var bar = new SolidBrush(accent)) e.Graphics.FillRectangle(bar, 6, 7, 6, row.Height - 18); };
      row.Controls.Add(new Label { Text = task.Source, Location = new Point(18, 3), Size = new Size(82, 28), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = PixelTheme.Ink, Font = PixelTheme.StrongFont });
      row.Controls.Add(new Label { Text = ProjectName(task), Location = new Point(105, 3), Size = new Size(135, 28), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = PixelTheme.Ink, Font = PixelTheme.StrongFont });
      string phase = String.IsNullOrWhiteSpace(task.Phase) ? task.Detail : task.Phase; if (task.Progress >= 0) phase += " " + task.Progress + "%";
      row.Controls.Add(new Label { Text = phase, Location = new Point(245, 3), Size = new Size(180, 28), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = accent, Font = PixelTheme.TextFont });
      row.Controls.Add(new Label { Text = Duration(Util.Now() - Math.Max(1, task.StartedAt)), Location = new Point(435, 3), Size = new Size(70, 28), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent, ForeColor = PixelTheme.Ink, Font = PixelTheme.MonoFont });
      if (actionable) {
        var dismiss = new PixelButton { Text = "清除", Danger = true, Location = new Point(520, 3), Size = new Size(70, 28) };
        dismiss.Click += delegate {
          if (PixelDialog.Show(this, "仅清除这条失效等待状态吗？\n\n同一会话有新的 MCP 或 Agent 事件时会自动恢复监控。", "清除等待状态", PixelDialogButtons.YesNo) == DialogResult.Yes) {
            TaskCenterState.Dismiss(task); overviewSignature = ""; ReloadOverview();
          }
        };
        row.Controls.Add(dismiss);
        var open = new PixelButton { Text = "打开任务", Active = true, Location = new Point(600, 3), Size = new Size(100, 28) }; open.Click += delegate { AgentWindowActivator.Focus(task); }; row.Controls.Add(open);
      }
      overviewPage.Controls.Add(row);
    }

    void BuildMorePage() {
      morePage.Controls.Add(PixelTheme.Label("当前任务与状态源", new Point(14, 4), new Size(180, 22), true));
      morePage.Controls.Add(PixelTheme.Label("黄灯置顶；灰色表示状态源可能不可信。", new Point(190, 4), new Size(530, 22), false));
      diagnostic.Location = new Point(20, 32); diagnostic.Size = new Size(708, 112); diagnostic.BackColor = PixelTheme.Paper; diagnostic.ForeColor = PixelTheme.Ink; diagnostic.Font = PixelTheme.MonoFont; morePage.Controls.Add(diagnostic);
      morePage.Controls.Add(PixelTheme.Label("状态变化历史", new Point(14, 154), new Size(180, 22), true));
      morePage.Controls.Add(PixelTheme.Label("仅保存状态与判定来源，不保存聊天正文。", new Point(190, 154), new Size(530, 22), false));
      content.Location = new Point(20, 182); content.Size = new Size(708, 126); content.BackColor = PixelTheme.Paper; content.ForeColor = PixelTheme.Ink; content.Font = PixelTheme.MonoFont; morePage.Controls.Add(content);
      var refresh = new PixelButton { Text = "刷新全部", Active = true, Location = new Point(14, 334), Size = new Size(100, 30) }; refresh.Click += delegate { ReloadMore(); }; morePage.Controls.Add(refresh);
      var copyDiagnostic = new PixelButton { Text = "复制诊断", Location = new Point(126, 334), Size = new Size(100, 30) }; copyDiagnostic.Click += delegate { try { Clipboard.SetText(diagnostic.Text); copyDiagnostic.Text = "已复制"; } catch { copyDiagnostic.Text = "复制失败"; } }; morePage.Controls.Add(copyDiagnostic);
      var copyHistory = new PixelButton { Text = "复制历史", Location = new Point(238, 334), Size = new Size(100, 30) }; copyHistory.Click += delegate { try { Clipboard.SetText(content.Text); copyHistory.Text = "已复制"; } catch { copyHistory.Text = "复制失败"; } }; morePage.Controls.Add(copyHistory);
      var clear = new PixelButton { Text = "清空历史", Danger = true, Location = new Point(634, 334), Size = new Size(100, 30) }; clear.Click += delegate { if (PixelDialog.Show(this, "确定清空本机状态变化历史吗？\n\n实时诊断不会被清除。", "清空状态历史", PixelDialogButtons.YesNo) == DialogResult.Yes) { StateHistory.Clear(); ReloadMore(); } }; morePage.Controls.Add(clear);
      morePage.Paint += delegate(object sender, PaintEventArgs e) { using (var ink = new Pen(PixelTheme.Ink, 3)) { e.Graphics.DrawRectangle(ink, 14, 27, 720, 122); e.Graphics.DrawRectangle(ink, 14, 177, 720, 136); } };
    }

    void ReloadMore() {
      string report = CurrentReport();
      var text = new StringBuilder();
      foreach (var item in DisplayHistory(StateHistory.Recent(300))) {
        string at = DateTimeOffset.FromUnixTimeMilliseconds(item.At).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        text.Append(at).Append("  ").Append(item.Source).Append("  ").Append(Display(item.Status)).Append("  ").Append(item.Detail).Append("  [").Append(item.Evidence).AppendLine("]");
      }
      string history = text.Length == 0 ? "暂无状态变化记录。" : text.ToString();
      string signature = report + "\n--history--\n" + history;
      if (String.Equals(signature, moreSignature, StringComparison.Ordinal)) return;
      moreSignature = signature;
      RenderAtomically(morePage, delegate { if (!String.Equals(diagnostic.Text, report, StringComparison.Ordinal)) diagnostic.Text = report; if (!String.Equals(content.Text, history, StringComparison.Ordinal)) content.Text = history; });
    }

    static void RenderAtomically(Control target, Action render) {
      bool handle = target != null && target.IsHandleCreated;
      if (handle) SendMessage(target.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
      if (target != null) target.SuspendLayout();
      try { render(); }
      finally {
        if (target != null) target.ResumeLayout(false);
        if (handle) {
          SendMessage(target.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
          target.Invalidate(true); target.Update();
        }
      }
    }

    static List<StateHistoryItem> DisplayHistory(List<StateHistoryItem> source) {
      var result = new List<StateHistoryItem>(); string previousKey = ""; long previousAt = 0;
      foreach (var item in source) {
        bool uiApproval = item.Source == "Codex" && (item.Evidence ?? "").IndexOf("Codex 当前可见审批卡", StringComparison.OrdinalIgnoreCase) >= 0;
        if (uiApproval && ((item.Evidence ?? "").IndexOf("重启恢复", StringComparison.OrdinalIgnoreCase) >= 0 || (item.Detail ?? "").StartsWith("重启后仍在等待", StringComparison.OrdinalIgnoreCase))) continue;
        string evidence = uiApproval ? "Codex 当前可见审批卡" : item.Evidence ?? "";
        string key = item.Source + "|" + item.Status + "|" + (item.Detail ?? "") + "|" + evidence;
        if (key == previousKey && previousAt - item.At <= 60000) continue;
        result.Add(item); previousKey = key; previousAt = item.At;
      }
      return result;
    }

    static string OverviewSignature(List<AgentTask> tasks, List<TaskSourceHealth> health, List<StateHistoryItem> history) {
      var text = new StringBuilder(); foreach (var task in tasks) text.Append(task.Id).Append('|').Append(task.Status).Append('|').Append(task.Phase).Append('|').Append(task.Progress).Append(';');
      foreach (var item in health) text.Append(item.Source).Append('|').Append(item.State).Append(';');
      long start = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds(); foreach (var item in history) if (item.At >= start && item.Status == State.Complete) text.Append(item.Source).Append('|').Append(item.At).Append(';');
      text.Append("minute=").Append(Util.Now() / 60000);
      return text.ToString();
    }

    static string ProjectName(AgentTask task) {
      if (!String.IsNullOrWhiteSpace(task.Cwd)) try { string name = Path.GetFileName(task.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (!String.IsNullOrWhiteSpace(name)) return name; } catch { }
      string session = task.SessionId ?? ""; if (session.Length > 8) session = session.Substring(session.Length - 8); return String.IsNullOrWhiteSpace(session) ? "未命名任务" : "会话 " + session;
    }

    static string Duration(long milliseconds) { long seconds = Math.Max(0, milliseconds / 1000); return String.Format("{0:00}:{1:00}", seconds / 60, seconds % 60); }
    static string CurrentReport() {
      var text = new StringBuilder(); var tasks = TaskCenterState.Tasks(); var health = TaskCenterState.Health();
      if (tasks.Count == 0) text.AppendLine("当前任务：无等待或运行中的任务");
      else {
        text.AppendLine("当前任务（黄灯优先）");
        foreach (var task in tasks) {
          string session = task.SessionId ?? ""; if (session.Length > 8) session = session.Substring(session.Length - 8);
          string project = ""; if (!String.IsNullOrWhiteSpace(task.Cwd)) try { project = Path.GetFileName(task.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); } catch { }
          if (String.IsNullOrWhiteSpace(project)) project = String.IsNullOrWhiteSpace(session) ? "未命名" : "会话 " + session;
          string phase = String.IsNullOrWhiteSpace(task.Phase) ? task.Detail : task.Phase; if (task.Progress >= 0) phase += " " + task.Progress + "%";
          text.Append("  ").Append(Display(task.Status)).Append("  ").Append(task.Source).Append("  ").Append(project).Append("  ").AppendLine(phase);
        }
      }
      text.AppendLine("状态源");
      foreach (var item in health) {
        string at = item.LastEventAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(item.LastEventAt).ToLocalTime().ToString("HH:mm:ss") : "--";
        text.Append("  ").Append(item.Trusted ? "■" : "□").Append(" ").Append(item.Source).Append("  ").Append(item.Detail).Append("  ").AppendLine(at);
      }
      text.AppendLine("扫描诊断"); text.Append(DiagnosticsHub.Report()); return text.ToString();
    }
    static string Display(string status) { return status == State.Attention ? "黄/需处理" : status == State.Running ? "绿/进行中" : "红/结束"; }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); base.OnPaint(e); }
  }
}

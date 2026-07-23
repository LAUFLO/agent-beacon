using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  sealed class TaskSourceHealth {
    public string Source, State, Detail;
    public long LastEventAt;
    public bool Trusted;
  }

  static class TaskCenterState {
    static readonly object Sync = new object();
    static List<AgentTask> tasks = new List<AgentTask>();
    static List<TaskSourceHealth> health = new List<TaskSourceHealth>();

    public static void Update(List<AgentTask> activeTasks, List<TaskSourceHealth> sourceHealth) {
      lock (Sync) {
        tasks = CloneTasks(activeTasks);
        health = CloneHealth(sourceHealth);
      }
    }
    public static List<AgentTask> Tasks() { lock (Sync) return CloneTasks(tasks); }
    public static List<TaskSourceHealth> Health() { lock (Sync) return CloneHealth(health); }
    public static void Dismiss(AgentTask task) {
      if (task == null) return; TaskDismissalStore.Dismiss(task);
      lock (Sync) tasks.RemoveAll(delegate(AgentTask item) { return String.Equals(item.Id, task.Id, StringComparison.OrdinalIgnoreCase); });
    }
    public static void DismissAll(List<AgentTask> selected) {
      var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var task in selected ?? new List<AgentTask>()) {
        if (task == null || String.IsNullOrWhiteSpace(task.Id)) continue;
        TaskDismissalStore.Dismiss(task); ids.Add(task.Id);
      }
      if (ids.Count == 0) return;
      lock (Sync) tasks.RemoveAll(delegate(AgentTask item) { return item != null && ids.Contains(item.Id); });
    }
    static List<AgentTask> CloneTasks(List<AgentTask> source) {
      var result = new List<AgentTask>(); foreach (var task in source ?? new List<AgentTask>()) result.Add(AgentStateRules.Clone(task)); return result;
    }
    static List<TaskSourceHealth> CloneHealth(List<TaskSourceHealth> source) {
      var result = new List<TaskSourceHealth>(); foreach (var item in source ?? new List<TaskSourceHealth>()) result.Add(new TaskSourceHealth { Source = item.Source, State = item.State, Detail = item.Detail, LastEventAt = item.LastEventAt, Trusted = item.Trusted }); return result;
    }
  }

  static class TaskDismissalStore {
    static readonly object Sync = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static bool loaded;
    static Dictionary<string, long> dismissed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    static string FilePath {
      get {
        string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_DISMISSED_TASKS_PATH");
        return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "dismissed-tasks.json") : Path.GetFullPath(configured);
      }
    }

    public static void Dismiss(AgentTask task) {
      if (task == null || String.IsNullOrWhiteSpace(task.Id)) return;
      lock (Sync) { EnsureLoaded(); dismissed[task.Id] = Math.Max(task.UpdatedAt, task.LastActivityAt); Persist(); }
    }

    public static bool IsSuppressed(AgentTask task) {
      if (task == null || String.IsNullOrWhiteSpace(task.Id)) return false;
      lock (Sync) {
        EnsureLoaded(); long at;
        if (!dismissed.TryGetValue(task.Id, out at)) return false;
        long current = Math.Max(task.UpdatedAt, task.LastActivityAt);
        if (current <= at) return true;
        dismissed.Remove(task.Id); Persist(); return false;
      }
    }

    static void EnsureLoaded() {
      if (loaded) return; loaded = true;
      try {
        var source = Json.Deserialize<Dictionary<string, long>>(File.ReadAllText(FilePath, Encoding.UTF8));
        dismissed = source == null ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, long>(source, StringComparer.OrdinalIgnoreCase);
        long cutoff = Util.Now() - 30L * 24 * 60 * 60 * 1000;
        foreach (string id in new List<string>(dismissed.Keys)) if (dismissed[id] < cutoff) dismissed.Remove(id);
      } catch { dismissed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); }
    }

    static void Persist() {
      try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, Json.Serialize(dismissed), new UTF8Encoding(false)); } catch { }
    }
  }

  static class PendingTaskStore {
    static readonly object Sync = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static bool loaded, firstMerge = true;
    static List<AgentTask> persisted = new List<AgentTask>();
    static string FilePath {
      get {
        string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_ACTIVE_TASKS_PATH");
        return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "active-tasks.json") : Path.GetFullPath(configured);
      }
    }

    public static List<AgentTask> Merge(List<AgentTask> active, AgentRuntimeSnapshot runtime, long now) {
      lock (Sync) {
        EnsureLoaded();
        var result = new List<AgentTask>(); var byId = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in active ?? new List<AgentTask>()) {
          var copy = AgentStateRules.Clone(task); result.Add(copy);
          if (!String.IsNullOrWhiteSpace(copy.Id)) byId[copy.Id] = copy;
        }
        foreach (var old in persisted) {
          if (old == null || IsTransient(old) || TaskDismissalStore.IsSuppressed(old) || old.Status != State.Attention || String.IsNullOrWhiteSpace(old.Id) || now - old.UpdatedAt > 24L * 60 * 60 * 1000) continue;
          if (runtime == null || !runtime.Sources.Contains(old.Source)) continue;
          AgentTask current;
          if (byId.TryGetValue(old.Id, out current)) {
            if (firstMerge && current.Status == State.Attention) MarkRestored(current, now);
            continue;
          }
          var restored = AgentStateRules.Clone(old); MarkRestored(restored, now); result.Add(restored); byId[restored.Id] = restored;
        }
        result = ActiveTaskRules.Collapse(result);
        firstMerge = false; Persist(result); return result;
      }
    }

    static bool IsTransient(AgentTask task) { return task != null && (task.Id ?? "").StartsWith("codex-ui-attention:", StringComparison.OrdinalIgnoreCase); }

    static void MarkRestored(AgentTask task, long now) {
      task.Restored = true; task.Stalled = false; task.Phase = "等待确认";
      long waited = Math.Max(0, now - Math.Max(task.StartedAt, task.UpdatedAt));
      task.Detail = "重启后仍在等待 · " + Duration(waited);
      task.Evidence = String.IsNullOrWhiteSpace(task.Evidence) ? "重启恢复快照" : task.Evidence + " · 重启恢复";
    }
    static string Duration(long milliseconds) {
      long minutes = Math.Max(0, milliseconds / 60000); return minutes < 1 ? "不足 1 分钟" : minutes < 60 ? "已等待 " + minutes + " 分钟" : "已等待 " + (minutes / 60) + " 小时 " + (minutes % 60) + " 分钟";
    }
    static void EnsureLoaded() {
      if (loaded) return; loaded = true;
      try { if (File.Exists(FilePath)) persisted = Json.Deserialize<List<AgentTask>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new List<AgentTask>(); } catch { persisted = new List<AgentTask>(); }
    }
    static void Persist(List<AgentTask> active) {
      try {
        persisted = new List<AgentTask>();
        foreach (var task in active) if (task != null && !IsTransient(task) && task.Status == State.Attention && !(task.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase)) {
          var safe = AgentStateRules.Clone(task); safe.Title = safe.Source + " 任务"; safe.Cwd = ""; persisted.Add(safe);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, Json.Serialize(persisted), new UTF8Encoding(false));
      } catch { }
    }
  }

  static class ActiveTaskRules {
    public const long StalledAfterMs = 10L * 60 * 1000;

    public static List<AgentTask> Relevant(List<AgentTask> scanned, AgentRuntimeSnapshot runtime, bool includeRecentComplete) {
      var result = new List<AgentTask>(); if (runtime == null) return result; long now = Util.Now();
      foreach (var task in Collapse(scanned)) {
        if (task == null || !runtime.Sources.Contains(task.Source) || String.IsNullOrWhiteSpace(task.Id)) continue;
        if (TaskDismissalStore.IsSuppressed(task)) continue;
        long baseline = 0; runtime.StartedAt.TryGetValue(task.Source, out baseline);
        bool traeMcp = task.Source == "TRAE" && task.Id.StartsWith("trae-mcp:", StringComparison.OrdinalIgnoreCase);
        bool afterRuntime = traeMcp || baseline <= 0 || task.UpdatedAt >= baseline - 5000;
        bool active = task.Status == State.Attention || task.Status == State.Running;
        bool recentComplete = includeRecentComplete && task.Status == State.Complete && now - task.UpdatedAt <= 5 * 60 * 1000;
        if (!afterRuntime || (!active && !recentComplete)) continue;
        var copy = AgentStateRules.Clone(task); if (copy.LastActivityAt <= 0) copy.LastActivityAt = copy.UpdatedAt;
        if (copy.Status == State.Running && now - copy.LastActivityAt >= StalledAfterMs) {
          copy.Stalled = true; copy.HealthState = "stale"; copy.HealthDetail = "超过 10 分钟无进展事件";
          copy.Detail = "可能卡住 · " + Math.Max(10, (now - copy.LastActivityAt) / 60000) + " 分钟无进展";
        }
        result.Add(copy);
      }
      result.Sort(Compare); return result;
    }

    public static List<AgentTask> Active(List<AgentTask> scanned, AgentRuntimeSnapshot runtime) {
      var active = Relevant(scanned, runtime, false);
      active = PendingTaskStore.Merge(active, runtime, Util.Now());
      active = SuppressSupersededStaleSessions(active, Util.Now());
      active.Sort(Compare); return active;
    }

    public static List<AgentTask> SuppressSupersededStaleSessions(List<AgentTask> source, long now) {
      var result = new List<AgentTask>();
      foreach (var task in source ?? new List<AgentTask>()) {
        bool superseded = false;
        if (task != null && task.Status == State.Running && now - Math.Max(task.LastActivityAt, task.UpdatedAt) >= StalledAfterMs) {
          foreach (var newer in source) {
            if (newer == null || Object.ReferenceEquals(newer, task) || newer.Status == State.Complete) continue;
            if (!String.Equals(newer.Source, task.Source, StringComparison.OrdinalIgnoreCase)) continue;
            if (!SameProject(task, newer)) continue;
            long newerActivity = Math.Max(newer.LastActivityAt, newer.UpdatedAt);
            if (newerActivity > Math.Max(task.LastActivityAt, task.UpdatedAt) && now - newerActivity < StalledAfterMs) { superseded = true; break; }
          }
        }
        if (!superseded && task != null) result.Add(AgentStateRules.Clone(task));
      }
      return result;
    }

    static bool SameProject(AgentTask left, AgentTask right) {
      if (left == null || right == null) return false;
      if (!String.IsNullOrWhiteSpace(left.Cwd) && !String.IsNullOrWhiteSpace(right.Cwd))
        return String.Equals(left.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), right.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
      return false;
    }

    public static List<AgentTask> Collapse(List<AgentTask> source) {
      var byKey = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);
      foreach (var task in source ?? new List<AgentTask>()) {
        if (task == null) continue;
        string key = LogicalKey(task); AgentTask current;
        if (!byKey.TryGetValue(key, out current) || Prefer(task, current)) byKey[key] = AgentStateRules.Clone(task);
      }
      var result = new List<AgentTask>(byKey.Values); result.Sort(Compare); return result;
    }

    public static bool AllowGlobalClaudeToolOverride(List<AgentTask> active) {
      int count = 0;
      foreach (var task in active ?? new List<AgentTask>()) {
        if (task != null && String.Equals(task.Source, "Claude Code", StringComparison.OrdinalIgnoreCase)
          && (task.Status == State.Attention || task.Status == State.Running)) count++;
      }
      return count == 1;
    }

    static string LogicalKey(AgentTask task) {
      if (!String.IsNullOrWhiteSpace(task.SessionId) && !task.SessionId.Equals("codex-ui", StringComparison.OrdinalIgnoreCase)) return task.Source + "|session|" + task.SessionId;
      if (!String.IsNullOrWhiteSpace(task.Cwd)) return task.Source + "|cwd|" + task.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      return task.Source + "|id|" + (task.Id ?? "");
    }

    static bool Prefer(AgentTask candidate, AgentTask current) {
      if (candidate.UpdatedAt != current.UpdatedAt) return candidate.UpdatedAt > current.UpdatedAt;
      int cp = candidate.Status == State.Attention ? 0 : candidate.Status == State.Running ? 1 : 2;
      int op = current.Status == State.Attention ? 0 : current.Status == State.Running ? 1 : 2;
      return cp < op;
    }

    public static AgentTask Aggregate(string source, List<AgentTask> active, AgentTask fallback, long runtimeStartedAt, long sourceSeenAt, AgentTask previous) {
      var sourceTasks = new List<AgentTask>(); foreach (var task in active ?? new List<AgentTask>()) if (String.Equals(task.Source, source, StringComparison.OrdinalIgnoreCase)) sourceTasks.Add(task);
      AgentTask selected = null;
      foreach (var task in sourceTasks) if (task.Status == State.Attention && (selected == null || task.UpdatedAt > selected.UpdatedAt)) selected = task;
      if (selected == null) foreach (var task in sourceTasks) if (task.Status == State.Running && (selected == null || task.UpdatedAt > selected.UpdatedAt)) selected = task;
      if (selected != null) return AgentStateRules.Clone(selected);
      return AgentStateRules.ResolveForRuntime(source, fallback, runtimeStartedAt, sourceSeenAt, previous);
    }

    public static List<TaskSourceHealth> Health(AgentRuntimeSnapshot runtime, List<AgentTask> scanned, List<AgentTask> active) {
      var result = new List<TaskSourceHealth>(); long now = Util.Now();
      foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) {
        if (runtime == null || !runtime.Sources.Contains(source)) continue;
        long last = 0; foreach (var task in scanned ?? new List<AgentTask>()) if (task.Source == source) last = Math.Max(last, Math.Max(task.UpdatedAt, task.LastActivityAt));
        bool configured = source == "Codex" || source == "TRAE" && Integration.IsTraeMcpPrepared() || source == "Claude Code" && Integration.IsClaudeInstalled() || source == "OpenCode" && Integration.IsOpenCodeInstalled();
        var item = new TaskSourceHealth { Source = source, LastEventAt = last, Trusted = true, State = "healthy", Detail = last > 0 ? "事件源正常" : "已启动，等待任务事件" };
        if (!configured) { item.State = "unconfigured"; item.Detail = "状态集成未配置"; item.Trusted = false; }
        else if (source == "TRAE" && !Integration.IsTraeMcpConnected()) { item.State = "disconnected"; item.Detail = "TRAE MCP 已失联"; item.Trusted = false; }
        AgentTask stalled = null, waiting = null;
        foreach (var task in active ?? new List<AgentTask>()) if (task.Source == source) { if (task.Stalled) stalled = task; if (task.Status == State.Attention) waiting = task; }
        if (waiting != null && item.Trusted) { item.State = "attention"; item.Detail = "事件源正常，任务正在等待处理"; }
        else if (stalled != null && item.Trusted) { item.State = "stale"; item.Detail = "任务仍为运行状态，但超过 10 分钟无进展"; item.Trusted = false; }
        else if (last > 0 && now - last > 30L * 60 * 1000 && item.Trusted) { item.State = "idle"; item.Detail = "最近无任务事件"; }
        result.Add(item);
      }
      return result;
    }

    static int Compare(AgentTask left, AgentTask right) {
      int lp = left.Status == State.Attention ? 0 : left.Status == State.Running ? 1 : 2;
      int rp = right.Status == State.Attention ? 0 : right.Status == State.Running ? 1 : 2;
      if (lp != rp) return lp.CompareTo(rp); return right.UpdatedAt.CompareTo(left.UpdatedAt);
    }
  }

  sealed class TaskQueuePopup : Form {
    readonly Action<AgentTask> openTask;
    readonly Action openFullCenter;
    List<AgentTask> tasks = new List<AgentTask>();
    List<TaskSourceHealth> health = new List<TaskSourceHealth>();
    bool dragging;
    Point dragOrigin;
    string renderedSignature = "";
    public bool AutoCloseOnDeactivate = true;

    public TaskQueuePopup(Action<AgentTask> taskAction, Action fullCenterAction) {
      openTask = taskAction; openFullCenter = fullCenterAction;
      Text = "Agent Beacon 待处理队列"; AccessibleDescription = "黄灯优先显示的当前任务队列"; Icon = PixelTheme.AppIcon; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      Deactivate += delegate { if (AutoCloseOnDeactivate && !IsDisposed) Close(); };
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag;
    }

    public void UpdateData(List<AgentTask> activeTasks, List<TaskSourceHealth> sourceHealth) {
      tasks = ActiveTaskRules.Collapse(activeTasks);
      health = new List<TaskSourceHealth>(); foreach (var item in sourceHealth ?? new List<TaskSourceHealth>()) health.Add(new TaskSourceHealth { Source = item.Source, State = item.State, Detail = item.Detail, LastEventAt = item.LastEventAt, Trusted = item.Trusted });
      string next = Signature(); if (String.Equals(next, renderedSignature, StringComparison.Ordinal)) return; renderedSignature = next;
      Build();
    }

    string Signature() {
      var text = new StringBuilder();
      foreach (var task in tasks) text.Append(task.Id).Append('|').Append(task.Status).Append('|').Append(task.Detail).Append('|').Append(task.Phase).Append('|').Append(task.Progress).Append(';');
      foreach (var item in health) text.Append(item.Source).Append('|').Append(item.State).Append('|').Append(item.Detail).Append(';');
      return text.ToString();
    }

    public void ShowNear(Rectangle anchor) {
      Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
      int x = anchor.Left - Width - 8; if (x < area.Left) x = anchor.Right + 8;
      int y = Math.Max(area.Top, Math.Min(anchor.Top, area.Bottom - Height));
      Location = new Point(Math.Max(area.Left, Math.Min(x, area.Right - Width)), y); Show(); Activate();
    }

    void Build() {
      SuspendLayout(); Controls.Clear();
      int shown = Math.Min(6, tasks.Count), degraded = health.FindAll(delegate(TaskSourceHealth item) { return !item.Trusted; }).Count;
      int height = 64 + Math.Max(1, shown) * 58 + 58 + (degraded > 0 ? 30 : 0);
      ClientSize = new Size(430, Math.Min(438, height));
      var title = PixelTheme.Label("AGENT BEACON // 当前任务", new Point(38, 8), new Size(350, 32), true); Controls.Add(title);
      title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag;
      var close = new PixelButton { Text = "X", Danger = true, Location = new Point(392, 9), Size = new Size(26, 27) }; close.Click += delegate { Close(); }; Controls.Add(close);
      int y = 50;
      if (tasks.Count == 0) {
        Controls.Add(PixelTheme.Label("当前没有等待或运行中的任务", new Point(20, y), new Size(390, 48), false)); y += 58;
      } else {
        for (int i = 0; i < shown; i++) { AddTaskRow(tasks[i], y); y += 58; }
      }
      if (degraded > 0) {
        Controls.Add(new Label { Text = "■ " + degraded + " 个状态源需要检查", Location = new Point(22, y), Size = new Size(386, 24), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.StrongFont }); y += 30;
      }
      var full = new PixelButton { Text = tasks.Count > shown ? "状态中心 · 另有 " + (tasks.Count - shown) + " 项" : "打开完整状态中心", Location = new Point(115, y + 8), Size = new Size(200, 34) };
      full.Click += delegate { Close(); if (openFullCenter != null) openFullCenter(); }; Controls.Add(full);
      ResumeLayout(false); Invalidate();
    }

    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; DpiSupport.KeepOnScreen(this); }

    void AddTaskRow(AgentTask task, int y) {
      var row = new Panel { Location = new Point(16, y), Size = new Size(398, 52), BackColor = PixelTheme.Paper };
      Color accent = task.Status == State.Attention ? PixelTheme.Yellow : task.Stalled ? PixelTheme.Muted : PixelTheme.Green;
      row.Paint += delegate(object sender, PaintEventArgs e) {
        using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 1, 1, row.Width - 3, row.Height - 3);
        using (var bar = new SolidBrush(accent)) e.Graphics.FillRectangle(bar, 5, 6, 7, row.Height - 12);
      };
      string project = ProjectName(task), phase = String.IsNullOrWhiteSpace(task.Phase) ? task.Detail : task.Phase;
      if (task.Progress >= 0) phase += "  " + task.Progress + "%";
      row.Controls.Add(new Label { Text = task.Source + "  ·  " + project, Location = new Point(20, 5), Size = new Size(246, 20), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = PixelTheme.Ink, Font = PixelTheme.StrongFont });
      row.Controls.Add(new Label { Text = phase, Location = new Point(20, 26), Size = new Size(246, 18), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = task.Status == State.Attention ? Color.FromArgb(182, 122, 0) : task.Stalled ? PixelTheme.Muted : PixelTheme.Green, Font = PixelTheme.TextFont });
      var open = new PixelButton { Text = "打开任务", Active = task.Status == State.Attention, Location = new Point(280, 9), Size = new Size(104, 34) };
      open.Click += delegate { Close(); if (openTask != null) openTask(task); }; row.Controls.Add(open); Controls.Add(row);
    }

    static string ProjectName(AgentTask task) {
      if (!String.IsNullOrWhiteSpace(task.Cwd)) {
        try { string name = Path.GetFileName(task.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); if (!String.IsNullOrWhiteSpace(name)) return name; } catch { }
      }
      string session = task.SessionId ?? ""; if (session.Length > 8) session = session.Substring(session.Length - 8);
      return String.IsNullOrWhiteSpace(session) ? "未命名任务" : "会话 " + session;
    }

    protected override void OnPaint(PaintEventArgs e) {
      PixelTheme.PaintWindow(e.Graphics, Width, Height, 0);
      using (var ink = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(ink, 10, 44, Width - 20, Height - 54);
      base.OnPaint(e);
    }
  }
}

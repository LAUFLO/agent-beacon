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
          cycle.CodexUiAttention = codexUiTarget != null && !codexAlreadyAttention && AgentProcesses.CodexNeedsUserAttention(cycle.Runtime, codexUiTarget.PendingExec);
          if (cycle.CodexUiAttention) {
            long eventAt = Util.Now();
            cycle.Tasks.Add(new AgentTask { Id = "codex-ui-attention:" + codexUiTarget.Id, Source = "Codex", SessionId = codexUiTarget.SessionId, Title = codexUiTarget.Title, Cwd = codexUiTarget.Cwd, Status = State.Attention, Detail = "Codex 正在等待你的确认", Phase = "等待确认", Evidence = "Codex 当前可见审批卡", InteractionId = "ui:" + codexUiTarget.Id, StartedAt = codexUiTarget.StartedAt > 0 ? codexUiTarget.StartedAt : eventAt, UpdatedAt = eventAt, LastActivityAt = codexUiTarget.LastActivityAt });
          }
        } catch (Exception ex) { cycle.Error = ex.GetType().Name + ": " + ex.Message; }
        watch.Stop(); cycle.DurationMs = watch.ElapsedMilliseconds;
        cycle.ManagedMemoryMb = Math.Max(1, GC.GetTotalMemory(false) / (1024 * 1024));
        try { using (var current = Process.GetCurrentProcess()) { current.Refresh(); cycle.PrivateMemoryMb = Math.Max(1, current.PrivateMemorySize64 / (1024 * 1024)); cycle.WorkingSetMb = Math.Max(1, current.WorkingSet64 / (1024 * 1024)); cycle.HandleCount = Math.Max(0, current.HandleCount); } } catch { }
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AgentTrafficLightNative {
  static class AgentProcesses {
    const int ProcessQueryLimitedInformation = 0x1000, ProcessCommandLineInformation = 60;
    const int RuntimeSnapshotCacheMs = 3000;
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int informationLength, out int returnLength);
    static readonly object SnapshotSync = new object(); static AgentRuntimeSnapshot cachedSnapshot;
    public static AgentRuntimeSnapshot Snapshot() {
      long capturedAt = Util.Now();
      string forced = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_TEST_AGENTS"), forcedFile = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_TEST_AGENTS_FILE");
      if (!String.IsNullOrWhiteSpace(forcedFile)) { try { forced = File.ReadAllText(forcedFile, Encoding.UTF8).Trim(); } catch { forced = ""; } }
      if (forced != null) { var forcedSnapshot = new AgentRuntimeSnapshot { CapturedAt = capturedAt }; foreach (string item in forced.Split(',')) AddCanonical(forcedSnapshot.Sources, item.Trim()); foreach (string source in forcedSnapshot.Sources) forcedSnapshot.StartedAt[source] = forcedSnapshot.CapturedAt; return forcedSnapshot; }
      lock (SnapshotSync) if (cachedSnapshot != null && capturedAt - cachedSnapshot.CapturedAt < RuntimeSnapshotCacheMs) return cachedSnapshot;
      var snapshot = new AgentRuntimeSnapshot { CapturedAt = capturedAt };
      foreach (var process in Process.GetProcesses()) {
        try {
          string name = process.ProcessName; var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); MatchName(matched, name);
          string lowerName = (name ?? "").Trim().ToLowerInvariant(); if (lowerName == "chatgpt" || lowerName == "codex") snapshot.CodexUiProcessIds.Add(process.Id);
          if (name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.Equals("bun", StringComparison.OrdinalIgnoreCase)) MatchCommandLine(matched, QueryCommandLine(process.Id));
          if (matched.Count == 0) continue;
          long started = 0; IntPtr probe = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
          if (probe != IntPtr.Zero) try { FileTime creation, exit, kernel, user; if (GetProcessTimes(probe, out creation, out exit, out kernel, out user)) { long ticks = ((long)creation.High << 32) | creation.Low; started = DateTimeOffset.FromFileTime(ticks).ToUnixTimeMilliseconds(); } } finally { CloseHandle(probe); }
          foreach (string source in matched) {
            snapshot.Sources.Add(source); long old; if (started > 0 && (!snapshot.StartedAt.TryGetValue(source, out old) || old == 0 || started < old)) snapshot.StartedAt[source] = started;
          }
        } catch { } finally { process.Dispose(); }
      }
      lock (SnapshotSync) cachedSnapshot = snapshot; return snapshot;
    }
    public static HashSet<string> RunningSources() { return Snapshot().Sources; }
    static void MatchName(HashSet<string> result, string name) {
      string lower = (name ?? "").Trim().ToLowerInvariant(); if (lower.Length == 0 || lower.Contains("agent-traffic-light")) return;
      if (lower == "codex" || lower.StartsWith("codex.")) result.Add("Codex");
      if (lower == "trae" || lower.StartsWith("trae ") || lower.StartsWith("trae-") || lower.Contains("trae solo")) result.Add("TRAE");
      if (lower == "claude" || lower.Contains("claude-code")) result.Add("Claude Code");
      if (lower == "opencode" || lower.StartsWith("opencode-")) result.Add("OpenCode");
    }
    static void MatchCommandLine(HashSet<string> result, string commandLine) { string cmd = commandLine ?? ""; if (Regex.IsMatch(cmd, "@anthropic-ai[\\\\/]claude-code|claude-code[\\\\/](?:cli|bin)|[\\\\/]claude(?:\\.js)?(?:\"|\\s|$)", RegexOptions.IgnoreCase)) result.Add("Claude Code"); if (Regex.IsMatch(cmd, "(?:^|[\\\\/\\s])opencode(?:\\.cmd|\\.exe|\\.js)?(?:\\s|$)|@opencode-ai[\\\\/]", RegexOptions.IgnoreCase)) result.Add("OpenCode"); }
    static string QueryCommandLine(int processId) {
      IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId); if (process == IntPtr.Zero) return "";
      try {
        int needed; NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed); if (needed <= 0 || needed > 1024 * 1024) return "";
        IntPtr buffer = Marshal.AllocHGlobal(needed); try { if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out needed) != 0) return ""; int length = Marshal.ReadInt16(buffer); IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4); return text == IntPtr.Zero || length <= 0 ? "" : Marshal.PtrToStringUni(text, length / 2); } finally { Marshal.FreeHGlobal(buffer); }
      } finally { CloseHandle(process); }
    }
    static int QueryParent(int processId) {
      IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId); if (process == IntPtr.Zero) return 0;
      try { int returned; IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size * 6); try { if (NtQueryInformationProcess(process, 0, buffer, IntPtr.Size * 6, out returned) != 0) return 0; return Marshal.ReadIntPtr(buffer, IntPtr.Size * 5).ToInt32(); } finally { Marshal.FreeHGlobal(buffer); } } finally { CloseHandle(process); }
    }
    public static bool ClaudeHasActiveToolProcess(long since) {
      var parents = new Dictionary<int, int>(); var names = new Dictionary<int, string>(); var started = new Dictionary<int, long>(); var commands = new Dictionary<int, string>(); var claude = new HashSet<int>();
      foreach (var process in Process.GetProcesses()) { try { int id = process.Id; string name = process.ProcessName; parents[id] = QueryParent(id); names[id] = name; try { started[id] = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); } catch { started[id] = 0; } if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0) claude.Add(id); } catch { } finally { process.Dispose(); } }
      if (claude.Count == 0) return false;
      foreach (int id in new List<int>(parents.Keys)) {
        string name; if (!names.TryGetValue(id, out name) || name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.IndexOf("conhost", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("crashpad", StringComparison.OrdinalIgnoreCase) >= 0) continue;
        long at; if (!started.TryGetValue(id, out at) || at < since - 3000) continue; int ancestor = id;
        for (int depth = 0; depth < 6; depth++) { int parent; if (!parents.TryGetValue(ancestor, out parent) || parent == 0) break; if (claude.Contains(parent)) { string command; if (!commands.TryGetValue(id, out command)) { command = QueryCommandLine(id); commands[id] = command; } if (!Regex.IsMatch(command ?? "", "playwright-mcp|chrome-devtools-mcp|alibabacloud-devops-mcp-server", RegexOptions.IgnoreCase)) return true; break; } ancestor = parent; }
      }
      return false;
    }
    static class CodexAutomationConditions {
      public static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
      public static readonly Condition Prompts = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
      );
    }
    public static bool IsCodexApprovalPromptText(string text) {
      string value = (text ?? "").Trim(); if (value.Length == 0 || value.Length > 4096) return false;
      value = Regex.Replace(value, "\\s+", " ");
      bool chineseApproval = value.IndexOf("允许", StringComparison.Ordinal) >= 0
        && (value.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0)
        && Regex.IsMatch(value, "编辑|运行|执行|访问|安装|写入|修改|删除");
      return chineseApproval || Regex.IsMatch(value, "(?:^|(?:终端|terminal)\\s+)(?:是否|要不要)允许|需要(?:你|您).{0,12}(?:确认|批准|选择)|(?:^|terminal\\s+)do you want to allow|allow\\s+.{1,80}\\s+to\\s+.{1,120}\\??$|approval required|requires your approval", RegexOptions.IgnoreCase);
    }
    public static bool IsCodexApprovalActionText(string text) {
      string value = (text ?? "").Trim();
      return Regex.IsMatch(value, "^(?:允许一次|本次允许|始终允许|允许|批准|拒绝|不允许|取消|allow once|allow this time|always allow|approve|deny|reject|cancel)(?:\\s*(?:[⌄▼▾˅v]|展开|更多选项|菜单|menu))*$", RegexOptions.IgnoreCase);
    }
    public static bool ShouldConsiderCodexApprovalElement(bool isOffscreen, bool isEnabled) {
      // Electron marks the whole accessibility tree off-screen while Codex is backgrounded or minimized.
      // Enabled approval controls are still live in that state and must remain detectable.
      return isEnabled;
    }
    public static bool ShouldConsiderCodexApprovalPromptElement(bool isOffscreen, bool isEnabled) {
      // Static Electron text can be disabled even while its paired approval button is live.
      // Safety comes from requiring a separate enabled affirmative action in the same Codex root.
      return true;
    }
    public static int CodexAutomationScanStart(int count, int limit) { return Math.Max(0, count - Math.Max(0, limit)); }
    static bool CodexAutomationRootNeedsAttention(AutomationElement root) {
      if (root == null) return false;
      var request = new CacheRequest();
      request.TreeScope = TreeScope.Descendants; request.AutomationElementMode = AutomationElementMode.None;
      request.Add(AutomationElement.NameProperty); request.Add(AutomationElement.HelpTextProperty); request.Add(AutomationElement.ItemStatusProperty); request.Add(AutomationElement.IsEnabledProperty); request.Add(AutomationElement.IsOffscreenProperty);
      using (request.Activate()) {
        bool allow = false;
        var buttons = root.FindAll(TreeScope.Descendants, CodexAutomationConditions.Buttons); int buttonStart = CodexAutomationScanStart(buttons.Count, 200);
        for (int i = buttons.Count - 1; i >= buttonStart; i--) try {
          var cached = buttons[i].Cached; if (!ShouldConsiderCodexApprovalElement(cached.IsOffscreen, cached.IsEnabled)) continue;
          if (IsCodexApprovalActionText(cached.Name) && !Regex.IsMatch(cached.Name ?? "", "拒绝|不允许|取消|deny|reject|cancel", RegexOptions.IgnoreCase)) { allow = true; break; }
        } catch { }
        if (!allow) return false;
        var prompts = root.FindAll(TreeScope.Descendants, CodexAutomationConditions.Prompts); int promptStart = CodexAutomationScanStart(prompts.Count, 600);
        for (int i = prompts.Count - 1; i >= promptStart; i--) try {
          var cached = prompts[i].Cached; if (!ShouldConsiderCodexApprovalPromptElement(cached.IsOffscreen, cached.IsEnabled)) continue;
          if (IsCodexApprovalPromptText(String.Join(" ", new[] { cached.Name ?? "", cached.HelpText ?? "", cached.ItemStatus ?? "" }))) return true;
        } catch { }
        return false;
      }
    }
    static long nextCodexUiScan; static int lastCodexUiAttention;
    public static int CodexUiScanDelay(bool found, bool urgent) { return found ? 750 : urgent ? 900 : 3000; }
    static bool FinishCodexUiScan(bool found, bool urgent, long now) {
      Interlocked.Exchange(ref lastCodexUiAttention, found ? 1 : 0);
      Interlocked.Exchange(ref nextCodexUiScan, now + CodexUiScanDelay(found, urgent));
      return found;
    }
    public static bool CodexNeedsUserAttention(AgentRuntimeSnapshot snapshot, bool urgent) {
      long now = Util.Now();
      if (snapshot == null || snapshot.CodexUiProcessIds.Count == 0) { Interlocked.Exchange(ref lastCodexUiAttention, 0); return false; }
      if (now < Interlocked.Read(ref nextCodexUiScan)) return Interlocked.CompareExchange(ref lastCodexUiAttention, 0, 0) == 1;
      var conditions = new List<Condition>(); var directRoots = new List<AutomationElement>();
      foreach (int processId in snapshot.CodexUiProcessIds) {
        Process process = null;
        try {
          process = Process.GetProcessById(processId);
          if (process.MainWindowHandle != IntPtr.Zero) { var direct = AutomationElement.FromHandle(process.MainWindowHandle); if (direct != null) directRoots.Add(direct); }
          conditions.Add(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        } catch { } finally { if (process != null) process.Dispose(); }
      }
      foreach (AutomationElement root in directRoots) try { if (CodexAutomationRootNeedsAttention(root)) return FinishCodexUiScan(true, urgent, now); } catch { }
      if (conditions.Count > 0) try {
        Condition windowCondition = conditions.Count == 1 ? conditions[0] : new OrCondition(conditions.ToArray());
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, windowCondition);
        for (int i = 0; i < windows.Count; i++) try { if (CodexAutomationRootNeedsAttention(windows[i])) return FinishCodexUiScan(true, urgent, now); } catch { }
      } catch { }
      return FinishCodexUiScan(false, urgent, now);
    }
    /// <summary>
    /// Detects a PowerShell/pwsh approval window for Codex terminal confirmations.
    /// This is a fallback layer when Codex Hook is not installed or trusted.
    /// Only matches windows whose title contains "Codex" and "确认" simultaneously
    /// to avoid false positives from ordinary PowerShell windows.
    /// </summary>
    public static bool CodexHasPowerShellApprovalWindow() {
      try {
        foreach (var process in Process.GetProcesses()) {
          try {
            string name = process.ProcessName;
            if (!"powershell".Equals(name, StringComparison.OrdinalIgnoreCase)
                && !"pwsh".Equals(name, StringComparison.OrdinalIgnoreCase))
              continue;
            string title = process.MainWindowTitle;
            if (!String.IsNullOrWhiteSpace(title)
                && title.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0
                && title.IndexOf("确认", StringComparison.Ordinal) >= 0) {
              return true;
            }
          } finally { process.Dispose(); }
        }
      } catch { }
      return false;
    }
    static void AddCanonical(HashSet<string> result, string name) {
      if (name.Equals("trae", StringComparison.OrdinalIgnoreCase)) result.Add("TRAE");
      else if (name.Equals("codex", StringComparison.OrdinalIgnoreCase)) result.Add("Codex");
      else if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("claude code", StringComparison.OrdinalIgnoreCase)) result.Add("Claude Code");
      else if (name.Equals("opencode", StringComparison.OrdinalIgnoreCase)) result.Add("OpenCode");
    }
  }
}

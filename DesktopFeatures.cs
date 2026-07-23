using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  static class AdaptiveScanPolicy {
    public static int Interval(SettingsData settings, List<AgentTask> agents) {
      int baseline = settings == null ? 1500 : Math.Max(800, Math.Min(30000, settings.RefreshMs));
      if (settings != null && !settings.AdaptiveScanning) return baseline;
      bool any = agents != null && agents.Count > 0;
      bool attention = any && agents.Exists(delegate(AgentTask task) { return task != null && task.Status == State.Attention; });
      bool running = any && agents.Exists(delegate(AgentTask task) { return task != null && task.Status == State.Running; });
      if (attention) return Math.Min(baseline, 800);
      if (running) return Math.Min(baseline, 1500);
      return Math.Max(baseline, any ? 5000 : 10000);
    }
  }

  static class NotificationPolicy {
    public static readonly string[] Sources = { "TRAE", "Codex", "Claude Code", "OpenCode" };
    public static bool AgentEnabled(SettingsData settings, string source) {
      string configured = settings.NotificationAgents ?? "";
      foreach (string item in configured.Split('|')) if (String.Equals(item, source, StringComparison.OrdinalIgnoreCase)) return true;
      return false;
    }
    public static void SetAgent(SettingsData settings, string source, bool enabled) {
      var list = new List<string>(); foreach (string candidate in Sources) if ((String.Equals(candidate, source, StringComparison.OrdinalIgnoreCase) && enabled) || (!String.Equals(candidate, source, StringComparison.OrdinalIgnoreCase) && AgentEnabled(settings, candidate))) list.Add(candidate);
      settings.NotificationAgents = String.Join("|", list.ToArray());
    }
    public static bool IsQuiet(SettingsData settings, DateTime now) {
      if (!settings.QuietHoursEnabled) return false; int hour = now.Hour, start = Math.Max(0, Math.Min(23, settings.QuietStartHour)), end = Math.Max(0, Math.Min(23, settings.QuietEndHour));
      return start == end || (start < end ? hour >= start && hour < end : hour >= start || hour < end);
    }
    public static bool CanNotify(SettingsData settings, string source) {
      return settings != null && settings.NotificationsEnabled && AgentEnabled(settings, source) && !IsQuiet(settings, DateTime.Now);
    }
    public static long AttentionDelayMs(SettingsData settings) { return Math.Max(0, settings == null ? 0 : settings.AttentionNotifyDelaySeconds) * 1000L; }
    public static bool ShouldRemindLongRunning(SettingsData settings, AgentTask task, long now) {
      return settings != null && task != null && task.Status == State.Running && settings.LongRunningReminderMinutes > 0 && task.StartedAt > 0 && now - task.StartedAt >= settings.LongRunningReminderMinutes * 60000L && CanNotify(settings, task.Source);
    }
    public static bool ShouldNotify(SettingsData settings, AgentTask task) {
      if (task == null || !CanNotify(settings, task.Source)) return false;
      return (task.Status == State.Attention && settings.NotifyAttention) || (task.Status == State.Complete && settings.NotifyComplete);
    }
  }

  static class AgentWindowActivator {
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);

    public static bool Focus(string source) {
      return Focus(new AgentTask { Source = source });
    }

    public static bool Focus(AgentTask task) {
      string source = task == null ? "" : task.Source; IntPtr match = IntPtr.Zero; int bestScore = -1;
      string project = ProjectName(task), taskTitle = task == null ? "" : (task.Title ?? ""), session = task == null ? "" : (task.SessionId ?? "");
      if (session.Length > 8) session = session.Substring(session.Length - 8);
      EnumWindows(delegate(IntPtr window, IntPtr unused) {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
        uint pid; GetWindowThreadProcessId(window, out pid); string process = "", title = "";
        try { using (var owner = Process.GetProcessById((int)pid)) process = owner.ProcessName; } catch { return true; }
        var text = new StringBuilder(GetWindowTextLength(window) + 1); GetWindowText(window, text, text.Capacity); title = text.ToString();
        if (!Matches(source, process, title)) return true;
        int score = 10;
        if (!String.IsNullOrWhiteSpace(project) && Contains(title, project)) score += 8;
        if (!String.IsNullOrWhiteSpace(session) && Contains(title, session)) score += 6;
        if (!String.IsNullOrWhiteSpace(taskTitle) && taskTitle.Length >= 4 && Contains(title, taskTitle.Length > 32 ? taskTitle.Substring(0, 32) : taskTitle)) score += 4;
        if (score > bestScore) { bestScore = score; match = window; }
        return true;
      }, IntPtr.Zero);
      if (match == IntPtr.Zero) return false; ShowWindow(match, 9); return SetForegroundWindow(match);
    }

    static string ProjectName(AgentTask task) {
      if (task == null || String.IsNullOrWhiteSpace(task.Cwd)) return "";
      try { return System.IO.Path.GetFileName(task.Cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)); } catch { return ""; }
    }

    static bool Matches(string source, string process, string title) {
      if (source == "TRAE") return Contains(process, "trae") || Contains(title, "trae");
      if (source == "Codex") return Contains(process, "chatgpt") || Contains(process, "codex") || Contains(title, "codex");
      if (source == "Claude Code") return Contains(process, "claude") || Contains(title, "claude code");
      if (source == "OpenCode") return Contains(process, "opencode") || Contains(title, "opencode");
      return false;
    }
    static bool Contains(string value, string token) { return value != null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0; }
  }
}

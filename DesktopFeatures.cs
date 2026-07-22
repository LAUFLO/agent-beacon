using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
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
    public static bool ShouldNotify(SettingsData settings, AgentTask task) {
      if (!settings.NotificationsEnabled || task == null || !AgentEnabled(settings, task.Source) || IsQuiet(settings, DateTime.Now)) return false;
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
      IntPtr match = IntPtr.Zero; EnumWindows(delegate(IntPtr window, IntPtr unused) {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return true;
        uint pid; GetWindowThreadProcessId(window, out pid); string process = "", title = "";
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
        var text = new StringBuilder(GetWindowTextLength(window) + 1); GetWindowText(window, text, text.Capacity); title = text.ToString();
        if (Matches(source, process, title)) { match = window; return false; } return true;
      }, IntPtr.Zero);
      if (match == IntPtr.Zero) return false; ShowWindow(match, 9); return SetForegroundWindow(match);
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

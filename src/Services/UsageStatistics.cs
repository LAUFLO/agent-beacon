using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentTrafficLightNative {
  sealed class DailyUsageData {
    public string Date = "";
    public int CompletedTasks;
    public long RunningMs;
    public long AttentionMs;
    public List<string> CompletedKeys = new List<string>();
  }

  sealed class UsageSnapshot {
    public string Date;
    public int CompletedTasks;
    public long RunningMs;
    public long AttentionMs;
  }

  sealed class UsageTracker {
    public string TaskId, Status;
    public long UpdatedAt;
  }

  static class UsageStatistics {
    static readonly object Sync = new object();
    static readonly Dictionary<string, UsageTracker> Trackers = new Dictionary<string, UsageTracker>(StringComparer.OrdinalIgnoreCase);
    static DailyUsageData data;
    static long lastPersistAt;

    static string FilePath() {
      string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_STATS_PATH");
      return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "usage-stats.json") : configured;
    }
    static string Day(long at) { return DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd"); }
    static void EnsureLoaded(long now) {
      if (data == null) {
        try { if (File.Exists(FilePath())) data = Util.Json.Deserialize<DailyUsageData>(File.ReadAllText(FilePath(), Encoding.UTF8)); } catch { }
        if (data == null) data = new DailyUsageData();
      }
      string today = Day(now);
      if (!String.Equals(data.Date, today, StringComparison.Ordinal)) {
        data = new DailyUsageData { Date = today }; Trackers.Clear(); lastPersistAt = 0;
      }
      if (data.CompletedKeys == null) data.CompletedKeys = new List<string>();
    }
    static void Accrue(UsageTracker tracker, long now) {
      if (tracker == null || tracker.UpdatedAt <= 0 || now <= tracker.UpdatedAt) return;
      long elapsed = Math.Min(now - tracker.UpdatedAt, 60000);
      if (tracker.Status == State.Running) data.RunningMs += elapsed;
      else if (tracker.Status == State.Attention) data.AttentionMs += elapsed;
      tracker.UpdatedAt = now;
    }
    static string AnonymousKey(string source, string taskId) {
      using (var sha = SHA256.Create()) {
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((source ?? "") + "|" + (taskId ?? "")));
        return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
      }
    }
    public static void Update(List<AgentTask> agents, long now) {
      lock (Sync) {
        EnsureLoaded(now); var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in agents ?? new List<AgentTask>()) {
          if (task == null || String.IsNullOrWhiteSpace(task.Source)) continue;
          string trackerKey = String.IsNullOrWhiteSpace(task.Id) ? task.Source : task.Source + "|" + task.Id;
          active.Add(trackerKey); UsageTracker tracker;
          if (!Trackers.TryGetValue(trackerKey, out tracker)) {
            Trackers[trackerKey] = new UsageTracker { TaskId = task.Id ?? "", Status = task.Status ?? "", UpdatedAt = now }; continue;
          }
          Accrue(tracker, now);
          bool completed = task.Status == State.Complete && tracker.Status != State.Complete && !(task.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase);
          if (completed) {
            string key = AnonymousKey(task.Source, task.Id); if (!data.CompletedKeys.Contains(key)) { data.CompletedKeys.Add(key); data.CompletedTasks++; if (data.CompletedKeys.Count > 2048) data.CompletedKeys.RemoveRange(0, data.CompletedKeys.Count - 1024); }
          }
          tracker.TaskId = task.Id ?? ""; tracker.Status = task.Status ?? ""; tracker.UpdatedAt = now;
        }
        foreach (string key in new List<string>(Trackers.Keys)) if (!active.Contains(key)) { Accrue(Trackers[key], now); Trackers.Remove(key); }
        if (lastPersistAt == 0 || now - lastPersistAt >= 30000) Persist(now);
      }
    }
    public static UsageSnapshot Snapshot() {
      lock (Sync) {
        long now = Util.Now(); EnsureLoaded(now);
        return new UsageSnapshot { Date = data.Date, CompletedTasks = data.CompletedTasks, RunningMs = data.RunningMs, AttentionMs = data.AttentionMs };
      }
    }
    public static string Duration(long milliseconds) {
      long minutes = Math.Max(0, milliseconds / 60000); return minutes >= 60 ? (minutes / 60) + "H " + (minutes % 60) + "M" : minutes + "M";
    }
    public static void Flush() { lock (Sync) { EnsureLoaded(Util.Now()); Persist(Util.Now()); } }
    static void Persist(long now) {
      try {
        string path = FilePath(); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, Util.Json.Serialize(data), new UTF8Encoding(false)); lastPersistAt = now;
      } catch { }
    }
  }
}

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
  static class State {
    public const string Complete = "complete";
    public const string Running = "running";
    public const string Attention = "attention";
  }

  sealed class AgentTask {
    public string Id, Source, SessionId, Title, Status, Detail, Evidence, InteractionId;
    public string Cwd, Phase, HealthState, HealthDetail;
    public long StartedAt, UpdatedAt, LastActivityAt;
    public int Progress = -1;
    public bool ExplicitStart, ReliableStart, PendingExec, Restored, Stalled;
  }

  sealed class AgentRuntimeSnapshot {
    public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> StartedAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<int> CodexUiProcessIds = new HashSet<int>();
    public long CapturedAt;
  }

  sealed class ScanCycle {
    public List<AgentTask> Tasks;
    public AgentRuntimeSnapshot Runtime;
    public int FilesRead;
    public long DurationMs, ManagedMemoryMb, PrivateMemoryMb, WorkingSetMb;
    public int HandleCount, EffectiveIntervalMs;
    public bool CodexUiAttention;
    public string Error;
  }

  sealed class SettingsData {
    public int RefreshMs = 1500;
    public int MaxTasks = 80;
    public bool AutoStart = false;
    public bool TaskbarMode = false;
    public int LampScale = 100;
    public bool AutoCheckUpdates = true;
    public bool NotificationsEnabled = true;
    public bool NotifyAttention = true;
    public bool NotifyComplete = true;
    public bool QuietHoursEnabled = false;
    public int QuietStartHour = 22;
    public int QuietEndHour = 8;
    public string NotificationAgents = "TRAE|Codex|Claude Code|OpenCode";
    public bool AdaptiveScanning = true;
    public int AttentionNotifyDelaySeconds = 0;
    public int LongRunningReminderMinutes = 0;
  }
}

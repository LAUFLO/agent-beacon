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
  sealed class MonitorWatchers : IDisposable {
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>(); readonly Action<bool> changed;
    public MonitorWatchers(Action<bool> onChanged) { changed = onChanged; Start(); }
    void Start() {
      var roots = new List<string> {
        Path.Combine(Util.Home, ".codex", "sessions"), Path.Combine(Util.Home, ".claude", "projects"), Util.BridgeDir
      };
      var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (string root in roots) if (unique.Add(root) && Directory.Exists(root)) try {
        var watcher = new FileSystemWatcher(root); watcher.IncludeSubdirectories = true; watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size; watcher.InternalBufferSize = 8192;
        FileSystemEventHandler contentEvent = delegate(object sender, FileSystemEventArgs e) { if (Relevant(e.FullPath)) changed(false); };
        FileSystemEventHandler layoutEvent = delegate(object sender, FileSystemEventArgs e) { if (Relevant(e.FullPath)) changed(true); };
        RenamedEventHandler renameEvent = delegate(object sender, RenamedEventArgs e) { if (Relevant(e.FullPath)) changed(true); };
        watcher.Changed += contentEvent; watcher.Created += layoutEvent; watcher.Deleted += layoutEvent; watcher.Renamed += renameEvent; watcher.Error += delegate { changed(true); }; watcher.EnableRaisingEvents = true; watchers.Add(watcher);
      } catch { }
    }
    static bool Relevant(string path) { string ext = Path.GetExtension(path ?? ""); return Regex.IsMatch(ext, "^\\.(?:json|jsonl|log|txt)$", RegexOptions.IgnoreCase); }
    public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); }
  }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using AgentTrafficLightNative;

namespace AgentBeaconInstaller {
  static class Program {
    const string Product = "Agent Beacon";
    const string Resource = "agent-beacon.exe";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string existing, string replacement, int flags);

    [STAThread] static int Main(string[] args) {
      try {
        if (Array.Exists(args, delegate(string value) { return String.Equals(value, "--uninstall", StringComparison.OrdinalIgnoreCase); })) { Uninstall(); return 0; }
        DpiSupport.Enable(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new InstallerForm()); return 0;
      } catch (Exception ex) {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "agent-beacon-installer.log"), DateTime.Now + Environment.NewLine + ex); } catch { }
        return 1;
      }
    }

    static string ShortcutPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product + ".lnk"); } }
    public static string DefaultInstallDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product); } }
    static string AppPath(string directory) { return Path.Combine(directory, "Agent-Beacon.exe"); }
    static string UninstallerPath(string directory) { return Path.Combine(directory, "Uninstall-Agent-Beacon.exe"); }

    public static string SuggestedInstallDirectory() {
      try {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentBeacon")) {
          string installed = key == null ? "" : key.GetValue("InstallLocation", "") as string;
          if (!String.IsNullOrWhiteSpace(installed)) return installed;
        }
      } catch { }
      return DefaultInstallDirectory;
    }

    public static string NormalizeInstallDirectory(string value) {
      if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("请选择安装位置。");
      string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      if (String.IsNullOrWhiteSpace(root) || String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能直接安装到磁盘根目录。");
      return full;
    }

    public static bool AutoStartEnabled() {
      try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return key != null && key.GetValue("AgentBeacon") != null; } catch { return false; }
    }
    public static void Install(bool autoStart, string requestedDirectory) {
      string installDirectory = NormalizeInstallDirectory(requestedDirectory);
      string appPath = AppPath(installDirectory), uninstallerPath = UninstallerPath(installDirectory);
      Directory.CreateDirectory(installDirectory); StopRunning(appPath);
      string pending = Path.Combine(installDirectory, "Agent-Beacon.pending.exe");
      using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)) {
        if (input == null) throw new InvalidOperationException("Embedded Agent Beacon package is missing.");
        using (var output = File.Create(pending)) input.CopyTo(output);
      }
      if (File.Exists(appPath)) File.Replace(pending, appPath, null, true); else File.Move(pending, appPath);
      StopRunning(appPath);
      string self = Assembly.GetExecutingAssembly().Location;
      if (!String.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase)) File.Copy(self, uninstallerPath, true);
      CreateShortcut(installDirectory, appPath); RegisterUninstaller(FileVersionInfo.GetVersionInfo(appPath).ProductVersion ?? "", installDirectory, appPath, uninstallerPath);
      using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (autoStart) run.SetValue("AgentBeacon", "\"" + appPath + "\" --hidden"); else run.DeleteValue("AgentBeacon", false); }
      Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true });
    }

    static void StopRunning(string appPath) {
      try { if (File.Exists(appPath)) Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = "--exit", UseShellExecute = false, CreateNoWindow = true }); } catch { }
      Thread.Sleep(900);
    }

    static void CreateShortcut(string installDirectory, string appPath) {
      Type type = Type.GetTypeFromProgID("WScript.Shell"); if (type == null) return;
      object shell = Activator.CreateInstance(type);
      object shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
      Type shortcutType = shortcut.GetType();
      shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { appPath });
      shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { installDirectory });
      shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { appPath + ",0" });
      shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "AI coding agent status lights" });
      shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
      if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }

    static void RegisterUninstaller(string version, string installDirectory, string appPath, string uninstallerPath) {
      using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentBeacon")) {
        key.SetValue("DisplayName", Product); key.SetValue("DisplayVersion", version); key.SetValue("Publisher", "LAUFLO");
        key.SetValue("InstallLocation", installDirectory); key.SetValue("DisplayIcon", appPath);
        key.SetValue("UninstallString", "\"" + uninstallerPath + "\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
      }
    }

    static void Uninstall() {
      string self = Assembly.GetExecutingAssembly().Location;
      if (!String.Equals(Path.GetFileName(self), "Uninstall-Agent-Beacon.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请从 Windows 已安装的应用中卸载 Agent Beacon。");
      string installDirectory = Path.GetDirectoryName(self), appPath = AppPath(installDirectory);
      StopRunning(appPath);
      try { if (File.Exists(appPath)) File.Delete(appPath); } catch { MoveFileEx(appPath, null, 4); }
      try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
      try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentBeacon", false); } catch { }
      try { using (RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.DeleteValue("AgentBeacon", false); } catch { }
      MoveFileEx(self, null, 4); MoveFileEx(installDirectory, null, 4);
    }
  }

  sealed class InstallerForm : Form {
    readonly InstallerToggle autoStart; readonly TextBox installPath; readonly Label status; bool dragging; Point dragOrigin;
    public InstallerForm() {
      Text = "Agent Beacon 安装"; Icon = PixelTheme.AppIcon; ClientSize = new Size(500, 300); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; BackColor = PixelTheme.Paper; ForeColor = PixelTheme.Ink; Font = PixelTheme.TextFont; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var title = new Label { Text = "AGENT BEACON v" + AppInfo.Version + " // 安装", AutoSize = false, Location = new Point(50, 8), Size = new Size(400, 32), ForeColor = PixelTheme.Ink, BackColor = Color.Transparent, Font = PixelTheme.TitleFont, TextAlign = ContentAlignment.MiddleCenter }; Controls.Add(title);
      var close = new PixelButton { Text = "X", Location = new Point(459, 9), Size = new Size(28, 27), Danger = true }; close.Click += delegate { Close(); }; Controls.Add(close);
      Controls.Add(new Label { Text = "安装位置（支持自定义）", AutoSize = false, Location = new Point(38, 61), Size = new Size(424, 26), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Ink, BackColor = Color.Transparent, Font = PixelTheme.StrongFont });
      installPath = new TextBox { Text = Program.SuggestedInstallDirectory(), Location = new Point(32, 92), Size = new Size(354, 28), BorderStyle = BorderStyle.FixedSingle, BackColor = PixelTheme.Paper, ForeColor = PixelTheme.Ink, Font = PixelTheme.MonoFont }; Controls.Add(installPath);
      var browse = new PixelButton { Text = "浏览", Location = new Point(394, 89), Size = new Size(74, 32) };
      browse.Click += delegate {
        using (var dialog = new FolderBrowserDialog { Description = "选择 Agent Beacon 安装位置", ShowNewFolderButton = true }) {
          if (Directory.Exists(installPath.Text)) dialog.SelectedPath = installPath.Text;
          if (dialog.ShowDialog(this) == DialogResult.OK) installPath.Text = Path.Combine(dialog.SelectedPath, ProductFolderName(dialog.SelectedPath));
        }
      }; Controls.Add(browse);
      autoStart = new InstallerToggle { Text = "安装后开机自启动", Checked = Program.AutoStartEnabled(), Location = new Point(142, 137), Width = 220 }; Controls.Add(autoStart);
      status = new Label { Text = "将创建开始菜单入口和卸载项", AutoSize = false, Location = new Point(38, 174), Size = new Size(424, 26), TextAlign = ContentAlignment.MiddleCenter, ForeColor = PixelTheme.Muted, BackColor = Color.Transparent, Font = PixelTheme.TextFont }; Controls.Add(status);
      var install = new PixelButton { Text = "安装 / 更新", Active = true, Location = new Point(165, 224), Size = new Size(170, 40) };
      install.Click += delegate {
        install.Enabled = false; status.Text = "正在安装…"; Refresh();
        try { Program.Install(autoStart.Checked, installPath.Text); status.Text = "安装完成"; PixelDialog.Show(this, "Agent Beacon 已安装并启动。", "安装完成", PixelDialogButtons.Ok); Close(); }
        catch (Exception ex) { install.Enabled = true; status.Text = "安装失败"; PixelDialog.Show(this, "安装失败：\n" + ex.Message, "安装失败", PixelDialogButtons.Ok); }
      }; Controls.Add(install);
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag; Shown += delegate { DpiSupport.KeepOnScreen(this); };
    }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    static string ProductFolderName(string selectedPath) { return String.Equals(Path.GetFileName(selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "Agent Beacon", StringComparison.OrdinalIgnoreCase) ? "" : "Agent Beacon"; }
    protected override void OnPaint(PaintEventArgs e) { PixelTheme.PaintWindow(e.Graphics, Width, Height, 0); using (var pen = new Pen(PixelTheme.Ink, 3)) e.Graphics.DrawRectangle(pen, 22, 55, 456, 158); base.OnPaint(e); }
  }

  sealed class InstallerToggle : Control {
    public bool Checked { get; set; }
    public InstallerToggle() { Height = 28; Font = PixelTheme.TextFont; ForeColor = PixelTheme.Ink; BackColor = PixelTheme.Paper; Cursor = Cursors.Hand; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.Clear(BackColor);
      using (var shadow = new SolidBrush(PixelTheme.Grid)) g.FillRectangle(shadow, 3, 6, 18, 18);
      using (var outer = new SolidBrush(PixelTheme.Ink)) g.FillRectangle(outer, 0, 3, 19, 19);
      using (var well = new SolidBrush(PixelTheme.Paper)) g.FillRectangle(well, 3, 6, 13, 13);
      if (Checked) using (var on = new SolidBrush(PixelTheme.Green)) { g.FillRectangle(on, 6, 9, 7, 7); g.FillRectangle(on, 8, 7, 3, 11); }
      TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 28, Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
      base.OnPaint(e);
    }
  }
}

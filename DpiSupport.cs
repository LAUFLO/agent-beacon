using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  static class DpiSupport {
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessDPIAware();

    public static void Enable() {
      try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
      try { SetProcessDPIAware(); } catch { }
    }
    public static void KeepOnScreen(Form form) {
      if (form == null || form.IsDisposed) return;
      Rectangle area = Screen.FromRectangle(form.Bounds).WorkingArea;
      int x = Math.Max(area.Left, Math.Min(form.Left, area.Right - Math.Min(form.Width, area.Width)));
      int y = Math.Max(area.Top, Math.Min(form.Top, area.Bottom - Math.Min(form.Height, area.Height)));
      if (form.Left != x || form.Top != y) form.Location = new Point(x, y);
    }
  }
}

// SPDX-FileCopyrightText: 2026 Hemingchu
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RecentWidget
{
    static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll")]
        public static extern bool IsTokenRestricted(IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int size, out int needed);

        [DllImport("advapi32.dll")]
        static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        public static bool RestrictedProcess()
        {
            IntPtr token;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                if (!OpenProcessToken(process.Handle, 8, out token)) return false;
                try { return IsTokenRestricted(token); }
                finally { CloseHandle(token); }
            }
        }

        public static int IntegrityLevel()
        {
            IntPtr token;
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                if (!OpenProcessToken(process.Handle, 8, out token))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    int needed;
                    GetTokenInformation(token, 25, IntPtr.Zero, 0, out needed);
                    if (needed <= 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    IntPtr info = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!GetTokenInformation(token, 25, info, needed, out needed))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        IntPtr sid = Marshal.ReadIntPtr(info);
                        byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
                finally { CloseHandle(token); }
            }
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_NOACTIVATE = 0x08000000;
    }

    sealed class RecentItem : IDisposable
    {
        public string Target;
        public string Name;
        public string Ext;
        public DateTime Used;
        public Bitmap Icon;
        public Bitmap Ghost;      // 按时间淡出的副本
        public float Alpha;       // 0.15 ~ 1
        public float Phase;       // 漂浮相位
        public float Speed;       // 漂浮速度
        public float Ox, Oy;      // 格位内的固定偏移，让排列不那么像网格
        public float AgeFrac;     // 0 新 → 1 旧，用来决定基础深度
        public float Zoff;        // 每颗图标固定的纵深偏移，让整片星群有前后层次
        public string DisplayName; // 两行以内、带省略号的显示名
        public string OrbitDisplayName;
        public Bitmap BmpNormal;   // 预渲染的"图标+名字"卡片
        public Bitmap BmpHover;
        public string AgeText
        {
            get
            {
                var span = DateTime.Now - Used;
                if (span.TotalMinutes < 1) return "刚刚";
                if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
                if (span.TotalHours < 24) return (int)span.TotalHours + " 小时前";
                if (span.TotalHours < 48) return "昨天 " + Used.ToString("HH:mm");
                return (int)span.TotalDays + " 天前";
            }
        }

        public void Dispose()
        {
            if (Icon != null) { Icon.Dispose(); Icon = null; }
            if (Ghost != null) { Ghost.Dispose(); Ghost = null; }
            if (BmpNormal != null) { BmpNormal.Dispose(); BmpNormal = null; }
            if (BmpHover != null) { BmpHover.Dispose(); BmpHover = null; }
        }
    }

    sealed class Proj
    {
        public RecentItem It;
        public float X, Y, Z, Scale, Size;
        public int Seq;
        public RectangleF LabelBounds;
    }

    sealed class LightParticle
    {
        public float X, Y, Phase, Radius;
        public int Color;
        public bool Flow;
    }

    static class DesktopShell
    {
        // Get the automation object hosted by the EXISTING desktop Explorer.
        // A newly created Shell.Application / explorer.exe can inherit our environment.
        internal static void Open(string target)
        {
            if (Native.IntegrityLevel() < 0x2000)
                throw new InvalidOperationException("程序文件带有 Low 完整性标签，当前以低权限运行，无法正常打开文档。"
                    + "\n请退出小工具，按 README 中的完整性标签排障说明处理后，从资源管理器启动。");
            if (Native.RestrictedProcess())
                throw new InvalidOperationException("小工具当前运行在受限环境中。请退出后，从资源管理器直接双击 RecentWidget.exe 启动。");
            object windows = null, desktop = null, view = null, shell = null;
            try
            {
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                var args = new object[] { 0, 0, 8, 0, 1 }; // SWC_DESKTOP, SWFO_NEEDDISPATCH
                var byRef = new ParameterModifier(5);
                byRef[0] = true; byRef[1] = true; byRef[3] = true;
                desktop = windows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod,
                    null, windows, args, new[] { byRef }, null, null);
                if (desktop == null)
                    throw new InvalidOperationException("未连接到桌面资源管理器，请从资源管理器打开此文件。");
                view = desktop.GetType().InvokeMember("Document", BindingFlags.GetProperty,
                    null, desktop, null);
                shell = view.GetType().InvokeMember("Application", BindingFlags.GetProperty,
                    null, view, null);
                shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod,
                    null, shell, new object[] { target, "", Path.GetDirectoryName(target), "open", 1 });
                Program.WriteDiagnostic("桌面资源管理器已接收打开请求：" + target);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException("小工具无权连接桌面资源管理器。请退出小工具，"
                    + "再从文件夹直接双击 RecentWidget.exe 启动；不要通过受限或隔离环境启动。", ex);
            }
            catch (TargetInvocationException ex)
            {
                throw new InvalidOperationException("资源管理器打开请求失败：" + (ex.InnerException ?? ex).Message,
                    ex.InnerException ?? ex);
            }
            finally
            {
                foreach (var obj in new[] { shell, view, desktop, windows })
                    if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
            }
        }
    }

    static class RecentStore
    {
        public static List<RecentItem> Load(int days, int max, out int total)
        {
            if (Program.DemoMode) return LoadDemo(max, out total);
            total = 0;
            var result = new List<RecentItem>();
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Recent");
            if (!Directory.Exists(dir)) return result;

            DateTime cutoff = DateTime.Now.AddDays(-days);
            var files = new DirectoryInfo(dir).GetFiles("*.lnk")
                .Where(f => f.LastWriteTime >= cutoff)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return result;
            object shell = Activator.CreateInstance(shellType);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                string target = null;
                try
                {
                    object sc = shellType.InvokeMember("CreateShortcut",
                        BindingFlags.InvokeMethod, null, shell, new object[] { f.FullName });
                    target = (string)sc.GetType().InvokeMember("TargetPath",
                        BindingFlags.GetProperty, null, sc, null);
                }
                catch { }
                if (string.IsNullOrEmpty(target)) continue;

                bool isDir = Directory.Exists(target);
                if (!File.Exists(target) && !isDir) continue;
                if (!seen.Add(target)) continue;

                total++;
                if (result.Count >= max) continue;

                var item = new RecentItem
                {
                    Target = target,
                    Name = Path.GetFileName(target.TrimEnd('\\')),
                    Ext = isDir ? "文件夹" : (Path.GetExtension(target).TrimStart('.').ToLowerInvariant()),
                    Used = f.LastWriteTime
                };
                if (item.Name.Length == 0) item.Name = target;
                item.Icon = LoadIcon(target);

                // 一周内越久越透明：刚打开接近实心，7 天前只剩一点影子
                double ageDays = Math.Max(0, (DateTime.Now - item.Used).TotalDays);
                double t = Math.Min(1, ageDays / 7.0);
                item.Alpha = (float)(0.08 + 0.92 * Math.Pow(1 - t, 2.0));
                item.AgeFrac = (float)t;
                var rnd = new Random(Guid.NewGuid().GetHashCode());
                item.Phase = (float)(rnd.NextDouble() * Math.PI * 2);
                item.Ox = (float)(rnd.NextDouble() * 18 - 9);
                item.Oy = (float)(rnd.NextDouble() * 14 - 7);
                item.Zoff = (float)(rnd.NextDouble() * 240);
                item.Speed = 0.35f + 0.8f * (float)(1 - t);
                // 越深越暗一点，强化前后层次
                item.Alpha *= 0.72f + 0.28f * (1f - item.Zoff / 240f);
                item.Ghost = FadeSized(item.Icon, item.Alpha, 32);

                result.Add(item);
            }
            return result;
        }

        static List<RecentItem> LoadDemo(int max, out int total)
        {
            string[] names = { "项目说明.txt", "会议记录.docx", "设计草图.png", "阅读清单.pdf",
                "演示数据.csv", "学习笔记.md", "旅行计划.docx", "灵感收集.txt",
                "示例照片.png", "产品手册.pdf", "待办事项.txt", "代码示例.cs" };
            total = names.Length;
            var result = new List<RecentItem>();
            for (int i = 0; i < Math.Min(max, names.Length); i++)
            {
                var icon = new Bitmap(40, 40, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(icon))
                using (var paper = new SolidBrush(Color.FromArgb(226, 240, 255)))
                using (var accent = new SolidBrush(Color.FromArgb(85 + i * 9, 126, 217)))
                using (var line = new Pen(Color.FromArgb(110, 144, 188), 2))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillRectangle(paper, 8, 3, 25, 34);
                    g.FillRectangle(accent, 5, 17, 31, 13);
                    g.DrawLine(line, 13, 9, 27, 9);
                    g.DrawLine(line, 13, 13, 25, 13);
                    using (var font = new Font(FontFamily.GenericSansSerif, 6.5f, FontStyle.Bold))
                    using (var text = new SolidBrush(Color.White))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(Path.GetExtension(names[i]).TrimStart('.').ToUpperInvariant(), font, text,
                            new RectangleF(5, 17, 31, 13), format);
                }
                var item = new RecentItem {
                    Target = "demo://item/" + i, Name = names[i], Ext = Path.GetExtension(names[i]),
                    Used = DateTime.Now.AddHours(-i * 5), Icon = icon,
                    Alpha = 0.5f + (11 - i) / 22f, Phase = i * 0.7f, Speed = 0.6f, Zoff = i * 12f
                };
                item.Ghost = FadeSized(icon, item.Alpha, 32);
                result.Add(item);
            }
            return result;
        }

        public static Bitmap FadeSized(Bitmap src, float alpha, int size)
        {
            if (src == null) return null;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, size, size),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return bmp;
        }

        public static Bitmap Fade(Bitmap src, float alpha)
        {
            if (src == null) return null;
            var bmp = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha };
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return bmp;
        }

        static Bitmap LoadIcon(string path)
        {
            try
            {
                using (var ico = Icon.ExtractAssociatedIcon(path))
                {
                    if (ico != null) return new Bitmap(ico.ToBitmap(), 40, 40);
                }
            }
            catch { }
            try
            {
                var app = SystemIcons.Application;
                if (app != null) return new Bitmap(app.ToBitmap(), 40, 40);
            }
            catch { }
            return null;
        }
    }

    sealed class WidgetForm : Form
    {
        const int DesignW = 500, DesignH = 584;
        int W { get { return ClientSize.Width; } }
        int H { get { return ClientSize.Height; } }
        const int HeaderH = 74;
        const int FooterH = 40;
        const int PadX = 14;
        const int Cols = 4;
        const int CellW = 84;
        const int CellH = 92;
        const int GridTop = HeaderH + 4;

        readonly List<RecentItem> items = new List<RecentItem>();
        int totalCount;
        int scrollRow;
        RecentItem hoverItem;
        RecentItem pressedItem;
        float anim;
        float parX, parY;          // 鼠标视差（平滑后）
        float parTX, parTY;        // 目标视差
        readonly System.Diagnostics.Stopwatch animClock = System.Diagnostics.Stopwatch.StartNew();
        readonly System.Drawing.Imaging.ImageAttributes dimAttr = new System.Drawing.Imaging.ImageAttributes();
        readonly System.Drawing.Imaging.ColorMatrix dimMatrix = new System.Drawing.Imaging.ColorMatrix();
        Point lastMouse;
        bool paused;
        ToolStripMenuItem pauseMenuItem;
        ToolStripMenuItem constMenuItem;
        double animAcc;
        double lastClockSec;
        System.Windows.Forms.Timer refresh;
        int refreshSeconds = 300;
        ToolStripMenuItem trayRefreshMenu;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        bool desktopMode = true;
        bool topMost = false;
        bool constellation = true;   // 粒子光柱：多层环绕，文件名常驻
        int page;                    // 星群模式的分页
        int PageSize { get { return 3 * Math.Min(4, Math.Max(1, (H - HeaderH - FooterH) / 100)); } }
        bool closeHover;
        Point dragOffset;
        bool dragging;
        bool pressedOnRow;
        Point pressedPoint;
        readonly List<PointF> stars = new List<PointF>();
        readonly float[] starAlpha;
        readonly List<LightParticle> lightParticles = new List<LightParticle>();
        readonly Dictionary<RecentItem, int> orbitLabelSlots = new Dictionary<RecentItem, int>();
        readonly List<PointF[]> lightBranches = new List<PointF[]>();
        readonly SolidBrush[,] particleBrushes = new SolidBrush[6, 12];
        static readonly Color[] lightColors = {
            Color.FromArgb(95, 235, 255), Color.FromArgb(124, 165, 255),
            Color.FromArgb(194, 143, 255), Color.FromArgb(255, 144, 201),
            Color.FromArgb(255, 220, 153), Color.FromArgb(131, 255, 210)
        };
        Bitmap pillarCache;
        float pillarSourceTop, pillarVerticalScale;
        readonly float[] pillarLeftScale = new float[2], pillarRightScale = new float[2];
        bool visualResourcesDisposed;
        bool sceneReady;

        Font fontTitle, fontName, fontMeta, fontFoot, fontLabel;
        readonly Color cBg = Color.FromArgb(10, 13, 22);
        readonly Color cBorder = Color.FromArgb(46, 110, 160, 220);
        readonly Color cTitle = Color.FromArgb(232, 240, 255);
        readonly Color cMeta = Color.FromArgb(123, 138, 163);
        readonly Color cAccent = Color.FromArgb(56, 189, 248);
        readonly Color cCardHover = Color.FromArgb(18, 27, 46);

        public WidgetForm()
        {
            Text = "最近一周";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(420, 380);
            MaximumSize = new Size(4000, 4000);
            ClientSize = new Size(DesignW, DesignH);
            DoubleBuffered = true;
            ShowInTaskbar = false;
            Opacity = 1.0;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            LoadFonts();
            LoadSettings();

            var rnd = new Random(20261030);
            for (int i = 0; i < 70; i++)
                stars.Add(new PointF((float)rnd.NextDouble() * DesignW, (float)rnd.NextDouble() * DesignH));
            starAlpha = new float[stars.Count];
            for (int i = 0; i < starAlpha.Length; i++) starAlpha[i] = 0.03f + (float)rnd.NextDouble() * 0.10f;

            BuildLightPillar();
            sceneReady = true;

            ApplyRegion();

            Reload();

            if (!Program.Headless)
            {
                StartRenderLoop();

                refresh = new System.Windows.Forms.Timer { Interval = refreshSeconds * 1000 };
                refresh.Tick += (s, e) => Reload();
                refresh.Start();

                if (!Program.DemoMode) BuildTray();
            }

            MouseWheel += (s, e) => WheelStep(e.Delta > 0 ? -1 : 1);
            MouseMove += OnMove;
            MouseDown += OnDown;
            MouseUp += OnUp;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        void LoadFonts()
        {
            try
            {
                fontTitle = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Bold);
                fontName = new Font("Microsoft YaHei UI", 9.75f, FontStyle.Regular);
                fontMeta = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Regular);
                fontFoot = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);
                fontLabel = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            }
            catch
            {
                fontTitle = new Font(FontFamily.GenericSansSerif, 12f, FontStyle.Bold);
                fontName = new Font(FontFamily.GenericSansSerif, 9.5f);
                fontMeta = new Font(FontFamily.GenericSansSerif, 8f);
                fontFoot = new Font(FontFamily.GenericSansSerif, 8f);
                fontLabel = new Font(FontFamily.GenericSansSerif, 9f);
            }
        }

        void LoadSettings()
        {
            if (Program.DemoMode) { Location = Point.Empty; return; }
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\RecentWidget"))
                {
                    if (key != null)
                    {
                        int ver = (int)key.GetValue("Ver", 0);
                        int x = (int)key.GetValue("X", -1);
                        int y = (int)key.GetValue("Y", -1);
                        refreshSeconds = Math.Max(1, Math.Min(86400, (int)key.GetValue("RefreshSeconds", 300)));
                        int width = Math.Max(420, Math.Min(4000, (int)key.GetValue("Width", DesignW)));
                        int height = Math.Max(380, Math.Min(4000, (int)key.GetValue("Height", DesignH)));
                        ClientSize = new Size(width, height);
                        if (ver >= 3)
                        {
                            desktopMode = (string)key.GetValue("DesktopMode", "1") == "1";
                            topMost = (string)key.GetValue("TopMost", "0") == "1";
                            constellation = (string)key.GetValue("Constellation", "1") == "1";
                        }
                        else
                        {
                            // 新版默认：贴在桌面层（不遮挡窗口）+ 星群模式
                            desktopMode = true;
                            topMost = false;
                            constellation = true;
                        }
                        Location = x >= 0 && y >= 0 ? new Point(x, y) : DefaultLocation();
                        return;
                    }
                }
            }
            catch { }
            Location = DefaultLocation();
        }

        Point DefaultLocation()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - W - 28, wa.Top + 72);
        }

        void SaveSettings()
        {
            if (Program.Headless || Program.DemoMode) return;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\RecentWidget"))
                {
                    if (key == null) return;
                    key.SetValue("X", Location.X);
                    key.SetValue("Y", Location.Y);
                    key.SetValue("Width", W);
                    key.SetValue("Height", H);
                    key.SetValue("DesktopMode", desktopMode ? "1" : "0");
                    key.SetValue("TopMost", topMost ? "1" : "0");
                    key.SetValue("Ver", 3);
                    key.SetValue("Constellation", constellation ? "1" : "0");
                    key.SetValue("RefreshSeconds", refreshSeconds);
                }
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;             // WS_EX_TOOLWINDOW
                if (desktopMode) cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDesktopMode();
        }

        void ApplyDesktopMode()
        {
            if (!IsHandleCreated) return;
            long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            if (desktopMode) ex |= Native.WS_EX_NOACTIVATE;
            else ex &= ~Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(ex));

            if (desktopMode)
            {
                TopMost = false;
                Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                TopMost = topMost;
                Native.SetWindowPos(Handle, topMost ? Native.HWND_TOPMOST : new IntPtr(-2), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            SaveSettings();
        }

        void ApplyRegion()
        {
            var old = Region;
            using (var path = RoundRect(new RectangleF(0, 0, W, H), 18f))
                Region = new Region(path);
            if (old != null) old.Dispose();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!sceneReady || visualResourcesDisposed || W <= 0 || H <= 0) return;
            if (bgCache != null) { bgCache.Dispose(); bgCache = null; }
            BuildLightPillar();
            ApplyRegion();
            orbitLabelSlots.Clear();
            hoverItem = null;
            if (scrollRow > MaxRow) scrollRow = MaxRow;
            if (constellation) BuildConstellation(false);
            else BuildProjection();
            Invalidate();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveSettings();
        }

        void ShowSizeDialog()
        {
            using (var dialog = new Form {
                Text = "设置卡片宽高", ClientSize = new Size(300, 164),
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, Font = fontName
            })
            using (var width = new NumericUpDown { Minimum = 420, Maximum = 4000, Value = Math.Min(4000, W), Increment = 10, Location = new Point(136, 22), Width = 140 })
            using (var height = new NumericUpDown { Minimum = 380, Maximum = 4000, Value = Math.Min(4000, H), Increment = 10, Location = new Point(136, 65), Width = 140 })
            using (var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(110, 117), Width = 78 })
            using (var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(198, 117), Width = 78 })
            {
                dialog.Controls.Add(new Label { Text = "宽度（像素）", AutoSize = true, Location = new Point(18, 25) });
                dialog.Controls.Add(new Label { Text = "高度（像素）", AutoSize = true, Location = new Point(18, 68) });
                dialog.Controls.AddRange(new Control[] { width, height, ok, cancel });
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ClientSize = new Size((int)width.Value, (int)height.Value);
                    SaveSettings();
                }
            }
        }

        internal void SetRefreshInterval(int seconds)
        {
            if (seconds < 1 || seconds > 86400) throw new ArgumentOutOfRangeException("seconds");
            refreshSeconds = seconds;
            if (refresh != null)
            {
                refresh.Stop();
                refresh.Interval = seconds * 1000;
                refresh.Start();
            }
            if (trayRefreshMenu != null) UpdateRefreshMenu(trayRefreshMenu);
            SaveSettings();
        }

        static string RefreshIntervalText(int seconds)
        {
            return seconds % 60 == 0 ? (seconds / 60) + " 分钟" : seconds + " 秒";
        }

        void UpdateRefreshMenu(ToolStripMenuItem menu)
        {
            menu.Text = "刷新间隔（" + RefreshIntervalText(refreshSeconds) + "）";
            bool preset = refreshSeconds == 30 || refreshSeconds == 60 || refreshSeconds == 180 || refreshSeconds == 300;
            foreach (ToolStripItem child in menu.DropDownItems)
            {
                var option = child as ToolStripMenuItem;
                if (option == null) continue;
                int value = (int)option.Tag;
                option.Checked = value == -1 ? !preset : value == refreshSeconds;
                if (value == -1) option.Text = preset ? "自定义…" : "自定义…（" + RefreshIntervalText(refreshSeconds) + "）";
            }
        }

        ToolStripMenuItem CreateRefreshMenu()
        {
            var menu = new ToolStripMenuItem();
            foreach (int seconds in new[] { 30, 60, 180, 300 })
            {
                var option = new ToolStripMenuItem(RefreshIntervalText(seconds)) { Tag = seconds };
                option.Click += (s, e) => {
                    SetRefreshInterval((int)((ToolStripMenuItem)s).Tag);
                    UpdateRefreshMenu(menu);
                };
                menu.DropDownItems.Add(option);
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem("自定义…", null, (s, e) => {
                ShowRefreshDialog();
                UpdateRefreshMenu(menu);
            }) { Tag = -1 });
            menu.DropDownOpening += (s, e) => UpdateRefreshMenu(menu);
            UpdateRefreshMenu(menu);
            return menu;
        }

        void ShowRefreshDialog()
        {
            using (var dialog = new Form {
                Text = "自定义刷新间隔", ClientSize = new Size(330, 160), Font = fontName,
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false
            })
            using (var seconds = new NumericUpDown {
                Minimum = 1, Maximum = 86400, Value = refreshSeconds,
                Location = new Point(135, 24), Width = 170
            })
            using (var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(140, 112), Width = 78 })
            using (var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(228, 112), Width = 78 })
            {
                dialog.Controls.Add(new Label { Text = "间隔（秒）", AutoSize = true, Location = new Point(20, 27) });
                dialog.Controls.Add(new Label { Text = "支持 1–86400 秒；60 秒 = 1 分钟", AutoSize = true, Location = new Point(20, 69) });
                dialog.Controls.AddRange(new Control[] { seconds, ok, cancel });
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) == DialogResult.OK) SetRefreshInterval((int)seconds.Value);
            }
        }

        void BuildTray()
        {
            try
            {
                trayMenu = new ContextMenuStrip();
                var showItem = new ToolStripMenuItem("显示 / 隐藏", null, (s, e) => Visible = !Visible);
                var refreshItem = new ToolStripMenuItem("立即刷新", null, (s, e) => Reload());
                var pauseItem = new ToolStripMenuItem("暂停动画", null, (s, e) =>
                {
                    bool wasPaused = paused;
                    paused = !paused;
                    ((ToolStripMenuItem)s).Checked = paused;
                    lastClockSec = animClock.Elapsed.TotalSeconds;
                    if (wasPaused && !paused) { /* 从暂停恢复，时间从当前位置继续 */ }
                    Invalidate();
                });
                pauseMenuItem = pauseItem;
                var constItem = new ToolStripMenuItem("粒子光柱（三维环绕）", null, (s, e) =>
                {
                    constellation = !constellation;
                    ((ToolStripMenuItem)s).Checked = constellation;
                    page = 0;
                    scrollRow = 0;
                    hoverItem = null;
                    SaveSettings();
                    Invalidate();
                });
                constItem.Checked = constellation;
                constMenuItem = constItem;
                var desktopItem = new ToolStripMenuItem("固定在桌面", null, (s, e) =>
                {
                    desktopMode = !desktopMode;
                    ((ToolStripMenuItem)s).Checked = desktopMode;
                    ApplyDesktopMode();
                });
                desktopItem.Checked = desktopMode;
                var topItem = new ToolStripMenuItem("窗口置顶", null, (s, e) =>
                {
                    topMost = !topMost;
                    ((ToolStripMenuItem)s).Checked = topMost;
                    TopMost = topMost;
                    SaveSettings();
                });
                topItem.Checked = topMost;
                var autoItem = new ToolStripMenuItem("开机自启", null, (s, e) =>
                {
                    bool on = !IsAutoStart();
                    SetAutoStart(on);
                    ((ToolStripMenuItem)s).Checked = on;
                });
                autoItem.Checked = IsAutoStart();
                trayMenu.Items.Add(showItem);
                trayMenu.Items.Add(refreshItem);
                trayRefreshMenu = CreateRefreshMenu();
                trayMenu.Items.Add(trayRefreshMenu);
                trayMenu.Items.Add(new ToolStripMenuItem("设置宽高…", null, (s, e) => ShowSizeDialog()));
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add(pauseItem);
                trayMenu.Items.Add(constItem);
                trayMenu.Items.Add(desktopItem);
                trayMenu.Items.Add(topItem);
                trayMenu.Items.Add(autoItem);
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Close()));

                tray = new NotifyIcon
                {
                    Icon = SystemIcons.Application,
                    Text = "最近一周 · 悬浮模块",
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };
                tray.DoubleClick += (s, e) => Visible = !Visible;

                bool tipShown;
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\RecentWidget"))
                    tipShown = key != null && (string)key.GetValue("TipShown", "0") == "1";
                if (!tipShown)
                {
                    tray.ShowBalloonTip(5000, "最近一周", "面板已悬浮在桌面右侧；点面板右上角的 × 退出。", ToolTipIcon.Info);
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\RecentWidget"))
                        if (key != null) key.SetValue("TipShown", "1");
                }
            }
            catch { /* 托盘不可用时仍保留面板 */ }
        }

        static bool IsAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    return key != null && key.GetValue("RecentWidget") != null;
                }
            }
            catch { return false; }
        }

        static void SetAutoStart(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null) return;
                    if (on) key.SetValue("RecentWidget", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("RecentWidget", false);
                }
            }
            catch { }
        }

        void Reload()
        {
            orbitLabelSlots.Clear();
            foreach (var it in items) it.Dispose();
            items.Clear();
            var fresh = RecentStore.Load(7, 120, out totalCount);
            items.AddRange(fresh);
            BuildDisplayNames();
            BuildBillboards();
            if (scrollRow > MaxRow) scrollRow = MaxRow;
            if (hoverItem != null && !items.Contains(hoverItem)) hoverItem = null;
            Invalidate();
        }

        // 预先把文件名折成最多两行，第二行放不下就加省略号
        void BuildDisplayNames()
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                foreach (var it in items)
                {
                    it.DisplayName = WrapName(g, it.Name, fontLabel, 84f);
                    it.OrbitDisplayName = WrapName(g, it.Name, fontLabel, 116f);
                }
            }
        }

        static string WrapName(Graphics g, string text, Font font, float maxW)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string l1 = "", l2 = "";
            int i = 0;
            while (i < text.Length)
            {
                string t = l1 + text[i];
                if (g.MeasureString(t, font).Width <= maxW) { l1 = t; i++; }
                else break;
            }
            // 第一行尽量在空格、点、下划线、连字符处断开，避免把单词劈开
            if (i < text.Length && text[i] != ' ')
            {
                int cut = -1;
                for (int k = l1.Length - 1; k > 2; k--)
                {
                    char ch = l1[k];
                    if (ch == ' ' || ch == '.' || ch == '_' || ch == '-') { cut = k; break; }
                }
                if (cut > l1.Length / 3)
                {
                    i = cut + 1;
                    l1 = text.Substring(0, cut + 1);
                }
            }

            while (i < text.Length)
            {
                string t = l2 + text[i];
                if (g.MeasureString(t, font).Width <= maxW - 10) { l2 = t; i++; }
                else break;
            }
            if (i < text.Length)
            {
                while (l2.Length > 0 && g.MeasureString(l2 + "…", font).Width > maxW - 4)
                    l2 = l2.Substring(0, l2.Length - 1);
                l2 += "…";
            }
            return l2.Length > 0 ? l1 + "\n" + l2 : l1;
        }

        public void SetPreviewHover(int index)
        {
            hoverItem = (index >= 0 && index < items.Count) ? items[index] : null;
        }

        public void SetPreviewAnim(float t)
        {
            anim = t;
        }

        // 预览用：让排斥偏移先收敛，避免截图里图标叠在一起
        public void SettleConstellation()
        {
            if (!constellation) BuildProjection();
            else BuildConstellation(false);
        }

        public int ItemCount
        {
            get { return items.Count; }
        }

        // 仅用于预览：把已有条目按 0→7 天铺开，直观看到"越久越透明"
        public void SpreadDemoAges()
        {
            int n = items.Count;
            for (int i = 0; i < n; i++)
            {
                var it = items[i];
                it.Used = DateTime.Now.AddDays(-6.9 * i / Math.Max(1, n - 1));
                double ageDays = Math.Max(0, (DateTime.Now - it.Used).TotalDays);
                double t = Math.Min(1, ageDays / 7.0);
                it.Alpha = (float)(0.08 + 0.92 * Math.Pow(1 - t, 2.0));
                it.AgeFrac = (float)t;
                it.Speed = 0.35f + 0.8f * (float)(1 - t);
                if (it.Ghost != null) it.Ghost.Dispose();
                it.Ghost = RecentStore.Fade(it.Icon, it.Alpha);
            }
            Invalidate();
        }

        int MaxRow
        {
            get
            {
                int rows = (items.Count + Cols - 1) / Cols;
                return Math.Max(0, rows - RowsVisible);
            }
        }

        int RowsVisible { get { return Math.Max(1, (H - GridTop - FooterH) / CellH); } }

        void WheelStep(int dir)
        {
            if (constellation)
            {
                int pages = Math.Max(1, (items.Count + PageSize - 1) / PageSize);
                int p = Math.Max(0, Math.Min(pages - 1, page + dir));
                if (p != page) { page = p; hoverItem = null; Invalidate(); }
            }
            else ScrollBy(dir);
        }

        void ScrollBy(int delta)
        {
            int v = Math.Max(0, Math.Min(MaxRow, scrollRow + delta));
            if (v != scrollRow) { scrollRow = v; hoverItem = null; Invalidate(); }
        }

        // 把三维投影结果缓存下来，供绘制与命中测试共用
        readonly List<Proj> projCache = new List<Proj>();

        void BuildProjection()
        {
            projCache.Clear();
            float yaw = (float)Math.Sin(anim * 0.10f) * 0.20f + parX;
            float pitch = (float)Math.Sin(anim * 0.07f + 1.2f) * 0.11f + parY;
            float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            const float focal = 1000f;
            float cx = W / 2f;
            float cyScreen = GridTop + (H - FooterH - GridTop) / 2f;
            float dolly = (float)Math.Sin(anim * 0.05f) * 30f;   // 整片星群缓慢前后呼吸

            int firstRow = scrollRow;
            for (int i = 0; i < items.Count; i++)
            {
                int row = i / Cols;
                if (row < firstRow || row >= firstRow + RowsVisible) continue;
                int col = i % Cols;
                var it = items[i];

                // 格位 + 各自的漂浮
                float bx = (col - 1.5f) * 116f;
                float by = (row - firstRow - (RowsVisible - 1) / 2f) * 100f;
                float ax = (float)Math.Sin(anim * it.Speed + it.Phase) * 9f;
                float ay = (float)Math.Cos(anim * it.Speed * 0.8f + it.Phase * 1.3f) * 8f;
                float az = (float)Math.Sin(anim * it.Speed * 0.55f + it.Phase * 0.9f) * 45f;

                float x = bx + ax;
                float y = by + ay;
                float z = 150f + it.AgeFrac * 170f + it.Zoff * 0.4f + az + dolly;   // 越旧越沉，同时保留各自的前后层次

                // 绕 Y 轴偏航，再绕 X 轴俯仰
                float x1 = x * cy - z * sy;
                float z1 = x * sy + z * cy;
                float y1 = y * cp - z1 * sp;
                float z2 = y * sp + z1 * cp;

                float persp = focal / (focal + z2);
                projCache.Add(new Proj
                {
                    It = it,
                    X = cx + x1 * persp,
                    Y = cyScreen + y1 * persp,
                    Z = z2,
                    Scale = persp,
                    Size = 38f * persp,
                    Seq = i
                });
            }

            // 远的先画，近的压在上面
            // 固定绘制顺序，避免深度交错时前后翻转
        }

        RecentItem TileAt(Point p)
        {
            if (p.Y < GridTop || p.Y > H - FooterH) return null;
            if (constellation)
                for (int i = projCache.Count - 1; i >= 0; i--)
                    if (projCache[i].LabelBounds.Contains(p)) return projCache[i].It;
            for (int i = projCache.Count - 1; i >= 0; i--)   // 从最近的一层往前找
            {
                var pr = projCache[i];
                float r = Math.Max(14f, pr.Size * 0.62f);
                if (Math.Abs(p.X - pr.X) < r && Math.Abs(p.Y - pr.Y) < r) return pr.It;
            }
            return null;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y);
                return;
            }

            lastMouse = e.Location;
            var hit = TileAt(e.Location);
            if (!ReferenceEquals(hit, hoverItem))
            {
                if (hit != null)
                {
                    var hp = projCache.FirstOrDefault(p => ReferenceEquals(p.It, hit));
                    bool inside = hp != null
                        && ((constellation && hp.LabelBounds.Contains(e.Location))
                            || (Math.Abs(e.X - hp.X) < hp.Size * 0.55f
                                && Math.Abs(e.Y - hp.Y) < hp.Size * 0.55f));
                    if (inside) { hoverItem = hit; Invalidate(); }
                }
                else if (hoverItem != null)
                {
                    var old = projCache.FirstOrDefault(p => ReferenceEquals(p.It, hoverItem));
                    if (old == null
                        || Math.Abs(e.X - old.X) > old.Size * 0.85f
                        || Math.Abs(e.Y - old.Y) > old.Size * 0.85f)
                    {
                        hoverItem = null;
                        Invalidate();
                    }
                }
            }

            // 鼠标在卡片上方时，整片星群轻微转向（视差）
            if (e.Y > GridTop && e.Y < H - FooterH)
            {
                parTX = (e.X / (float)W - 0.5f) * 0.12f;
                parTY = (e.Y / (float)H - 0.5f) * 0.07f;
            }
            else
            {
                parTX = 0; parTY = 0;
            }

            bool ch = CloseRect.Contains(e.Location);
            if (ch != closeHover) { closeHover = ch; Invalidate(); }
        }

        void OnDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location))
            {
                Close();
                return;
            }

            pressedPoint = e.Location;
            pressedItem = TileAt(e.Location);
            pressedOnRow = pressedItem != null;

            if (e.Button == MouseButtons.Left && (e.Y < HeaderH || !pressedOnRow))
            {
                dragging = true;
                dragOffset = new Point(Cursor.Position.X - Location.X, Cursor.Position.Y - Location.Y);
            }
            if (e.Button == MouseButtons.Right)
            {
                var hit = TileAt(e.Location);
                if (hit != null) ShowItemMenu(hit, e.Location);
                else ShowPanelMenu(e.Location);
            }
        }

        void OnUp(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                dragging = false;
                SaveSettings();
                return;
            }
            if (e.Button == MouseButtons.Left && pressedOnRow)
            {
                int dx = Math.Abs(e.X - pressedPoint.X), dy = Math.Abs(e.Y - pressedPoint.Y);
                var hit = TileAt(e.Location);
                if (dx < 5 && dy < 5 && ReferenceEquals(hit, pressedItem) && hit != null)
                    Open(hit);
            }
        }

        volatile bool loopRunning = true;
        Thread loopThread;
        int framePending;

        void StartRenderLoop()
        {
            lastClockSec = animClock.Elapsed.TotalSeconds;
            loopThread = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double next = 0;
                while (loopRunning)
                {
                    double t = sw.Elapsed.TotalSeconds;
                    if (t >= next)
                    {
                        next += 1.0 / 60.0;
                        if (next < t) next = t + 1.0 / 60.0;
                        PostFrame();
                    }
                    Thread.Sleep(1);
                }
            });
            loopThread.IsBackground = true;
            loopThread.Start();
        }

        void PostFrame()
        {
            if (Interlocked.Exchange(ref framePending, 1) != 0) return;
            try
            {
                if (IsHandleCreated) BeginInvoke((Action)FrameTick);
                else framePending = 0;
            }
            catch { framePending = 0; }
        }

        void FrameTick()
        {
            try
            {
                if (!loopRunning) return;
                double nowSec = animClock.Elapsed.TotalSeconds;
                if (!paused) animAcc += nowSec - lastClockSec;
                lastClockSec = nowSec;
                anim = (float)animAcc;
                parX += (parTX - parX) * 0.055f;
                parY += (parTY - parY) * 0.055f;

                if (constellation) BuildConstellation(true);

                if (hoverItem != null && projCache.Count > 0)
                {
                    var hp = projCache.FirstOrDefault(p => ReferenceEquals(p.It, hoverItem));
                    if (hp != null)
                    {
                        float dx = lastMouse.X - hp.X, dy = lastMouse.Y - hp.Y;
                        if (!(constellation && hp.LabelBounds.Contains(lastMouse))
                            && dx * dx + dy * dy > hp.Size * hp.Size * 2.6f) hoverItem = null;
                    }
                }

                if (Program.TickTestPath != null) RecordTick();

                Invalidate();
                // Complete this frame before allowing another animation update to queue.
                Update();
            }
            finally { framePending = 0; }
        }

        void RecordTick()
        {
            double now = animClock.Elapsed.TotalMilliseconds;
            if (Program.lastTickMs > 0) Program.TickDeltas.Add(now - Program.lastTickMs);
            Program.lastTickMs = now;
            if (Program.TickDeltas.Count < 120) return;

            var d = Program.TickDeltas;
            var sorted = d.OrderBy(x => x).ToList();
            string txt = string.Format("avg={0:F1} med={1:F1} min={2:F1} max={3:F1} p90={4:F1}",
                d.Average(), sorted[sorted.Count / 2], sorted[0], sorted[sorted.Count - 1],
                sorted[(int)(sorted.Count * 0.9)]);
            try { File.WriteAllText(Program.TickTestPath, txt); } catch { }
            Close();
        }

        void Open(RecentItem item)
        {
            try
            {
                OpenTarget(item.Target, DesktopShell.Open);
            }
            catch (Exception ex)
            {
                Program.LogError(new InvalidOperationException("打开文件失败: " + item.Target, ex));
                var nativeError = ex as System.ComponentModel.Win32Exception;
                string detail = nativeError == null ? "" : "\nWindows 错误码：" + nativeError.NativeErrorCode;
                MessageBox.Show("无法打开：" + item.Target + "\n\n" + ex.Message + detail
                    + "\n\n可右键选择“打开所在文件夹”，检查该文件能否从资源管理器打开。",
                    "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static void OpenTarget(string target, Action<string> shellOpen)
        {
            if (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target))
                throw new ArgumentException("文件路径无效。", "target");
            if (!File.Exists(target) && !Directory.Exists(target))
                throw new FileNotFoundException("文件不存在或当前账户无权访问，请刷新文件列表。", target);
            shellOpen(target);
        }
        void Reveal(RecentItem item)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    "/select,\"" + item.Target + "\"") { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        void ShowPanelMenu(Point at)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, (s, e) => Reload()));
            menu.Items.Add(CreateRefreshMenu());
            menu.Items.Add(new ToolStripMenuItem("设置宽高…", null, (s, e) => ShowSizeDialog()));
            menu.Items.Add(new ToolStripMenuItem(paused ? "继续动画" : "暂停动画", null, (s, e) =>
            {
                paused = !paused;
                lastClockSec = animClock.Elapsed.TotalSeconds;
                if (pauseMenuItem != null) pauseMenuItem.Checked = paused;
                Invalidate();
            }));
            var star = new ToolStripMenuItem("粒子光柱（三维环绕）", null, (s, e) =>
            {
                constellation = !constellation;
                page = 0;
                scrollRow = 0;
                hoverItem = null;
                if (constMenuItem != null) constMenuItem.Checked = constellation;
                SaveSettings();
                Invalidate();
            });
            star.Checked = constellation;
            menu.Items.Add(star);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Close()));
            menu.Show(this, at);
        }

        void ShowItemMenu(RecentItem item, Point at)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("打开", null, (s, e) => Open(item)));
            menu.Items.Add(new ToolStripMenuItem("打开所在文件夹", null, (s, e) => Reveal(item)));
            menu.Items.Add(new ToolStripMenuItem("复制路径", null, (s, e) =>
            {
                try { Clipboard.SetText(item.Target); } catch { }
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(CreateRefreshMenu());
            menu.Items.Add(new ToolStripMenuItem("设置宽高…", null, (s, e) => ShowSizeDialog()));
            menu.Show(this, at);
        }

        Bitmap hiBuf;
        Graphics hiG;

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics, false);
        }

        // 渲染整块面板：屏幕绘制与 --shot 预览共用
        public void Render(Graphics g, bool shot)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (bgCache == null) BuildBackgroundCache();
            g.DrawImage(bgCache, 0, 0);

            DrawList(g);
            DrawHeader(g);
            DrawFooter(g);
        }

        Bitmap bgCache;

        // 面板底色、冷光、星点、边框都是静止的，只画一次
        void BuildBackgroundCache()
        {
            bgCache = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bgCache))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundRect(new RectangleF(0, 0, W, H), 18f))
                {
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, W, H),
                        Color.FromArgb(255, 24, 45, 76), Color.FromArgb(255, 39, 28, 65), 110f))
                        g.FillPath(b, path);

                    g.SetClip(path, CombineMode.Intersect);

                    DrawSoftGlow(g, new RectangleF(-80, -110, 610, 540), Color.FromArgb(55, 65, 158, 228));
                    DrawSoftGlow(g, new RectangleF(80, 240, 500, 490), Color.FromArgb(58, 152, 79, 205));
                    DrawSoftGlow(g, new RectangleF(-180, 300, 480, 350), Color.FromArgb(28, 57, 217, 203));

                    for (int i = 0; i < stars.Count; i++)
                    {
                        using (var sb = new SolidBrush(Color.FromArgb((int)(255 * starAlpha[i]), 220, 235, 255)))
                            g.FillRectangle(sb, stars[i].X * W / DesignW, stars[i].Y * H / DesignH, 1.2f, 1.2f);
                    }

                    g.ResetClip();

                    using (var pen = new Pen(cBorder, 1f))
                        g.DrawPath(pen, path);
                }
            }
        }

        static void DrawSoftGlow(Graphics g, RectangleF bounds, Color color)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(bounds);
                using (var brush = new PathGradientBrush(path)
                {
                    CenterColor = color,
                    SurroundColors = new[] { Color.FromArgb(0, color) }
                }) g.FillPath(brush, path);
            }
        }

        void BuildLightPillar()
        {
            if (lightParticles.Count == 0)
            {
                var random = new Random(8317);
                for (int c = 0; c < 6; c++)
                    for (int a = 0; a < 12; a++)
                        particleBrushes[c, a] = new SolidBrush(Color.FromArgb(35 + a * 19, lightColors[c]));

                // A branching crown and an inverted root system share the luminous trunk.
                for (int end = 0; end < 2; end++)
                    for (int arm = 0; arm < 9; arm++)
                    {
                        float angle = (end == 0 ? -(float)Math.PI / 2 : (float)Math.PI / 2)
                            + (arm - 4) * 0.25f;
                        AddLightBranch(DesignW / 2f, end == 0 ? 234f : 386f, angle,
                            end == 0 ? 53f : 45f, 3, (arm + end * 2) % 6, random);
                    }
                for (int i = 0; i < 320; i++)
                    lightParticles.Add(new LightParticle {
                        X = (float)random.NextDouble(), Y = (float)random.NextDouble(),
                        Phase = (float)(random.NextDouble() * Math.PI * 2),
                        Radius = 0.65f + (float)random.NextDouble() * 1.1f,
                        Color = i % 6, Flow = true
                    });
                }

            // Fit the entire branching silhouette to the card instead of fixed scene coordinates.
            var branchParticles = lightParticles.Where(p => !p.Flow).ToList();
            pillarSourceTop = branchParticles.Min(p => p.Y);
            float sourceBottom = branchParticles.Max(p => p.Y);
            pillarVerticalScale = (H - 28f) / (sourceBottom - pillarSourceTop);
            // Fit crown and roots independently; keep their attachment on the column axis.
            for (int end = 0; end < 2; end++)
            {
                var branches = branchParticles.Where(p => (p.Y < 310f ? 0 : 1) == end).ToList();
                float halfWidth = W / 2f - 14f;
                pillarLeftScale[end] = halfWidth / (DesignW / 2f - branches.Min(p => p.X));
                pillarRightScale[end] = halfWidth / (branches.Max(p => p.X) - DesignW / 2f);
            }

            if (pillarCache != null) pillarCache.Dispose();
            pillarCache = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(pillarCache))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawSoftGlow(g, new RectangleF(W / 2f - 65, 60, 130, H - 120), Color.FromArgb(85, 69, 183, 255));
                DrawSoftGlow(g, new RectangleF(0, -40, W, 230), Color.FromArgb(58, 127, 139, 255));
                DrawSoftGlow(g, new RectangleF(0, H - 180, W, 230), Color.FromArgb(55, 208, 109, 212));
                for (int i = 0; i < lightBranches.Count; i++)
                {
                    var line = lightBranches[i];
                    using (var pen = new Pen(Color.FromArgb(29, lightColors[i % 6]), 0.7f))
                        g.DrawLine(pen, PillarX(line[0].X, line[0].Y), PillarY(line[0].Y),
                            PillarX(line[1].X, line[1].Y), PillarY(line[1].Y));
                }
                // Subtle orbital guides establish the column's cylindrical depth.
                for (int ring = 0; ring < PageSize / 3; ring++)
                {
                    using (var pen = new Pen(Color.FromArgb(25, 155, 204, 255), 0.65f))
                        g.DrawEllipse(pen, 88, OrbitY(ring) - 23, W - 176, 46);
                }
            }
        }

        float PillarY(float y)
        {
            return 14f + (y - pillarSourceTop) * pillarVerticalScale;
        }

        float PillarX(float x, float y)
        {
            int end = y < 310f ? 0 : 1;
            float offset = x - DesignW / 2f;
            return W / 2f + offset * (offset < 0 ? pillarLeftScale[end] : pillarRightScale[end]);
        }

        void AddLightBranch(float x, float y, float angle, float length, int depth, int color, Random random)
        {
            float nx = x + (float)Math.Cos(angle) * length;
            float ny = y + (float)Math.Sin(angle) * length;
            nx = Math.Max(72, Math.Min(DesignW - 72, nx));
            ny = Math.Max(GridTop + 16, Math.Min(DesignH - FooterH - 20, ny));
            lightBranches.Add(new[] { new PointF(x, y), new PointF(nx, ny) });
            int count = Math.Max(5, (int)(length * 0.42f));
            for (int i = 0; i < count; i++)
            {
                float t = (i + (float)random.NextDouble()) / count;
                lightParticles.Add(new LightParticle {
                    X = x + (nx - x) * t + (float)(random.NextDouble() - 0.5) * 5,
                    Y = y + (ny - y) * t + (float)(random.NextDouble() - 0.5) * 4,
                    Phase = (float)(random.NextDouble() * Math.PI * 2),
                    Radius = 0.55f + (float)random.NextDouble() * 0.9f,
                    Color = (color + (random.Next(4) == 0 ? 1 : 0)) % 6
                });
            }
            if (depth == 0) return;
            AddLightBranch(nx, ny, angle - 0.31f - (float)random.NextDouble() * 0.15f,
                length * 0.68f, depth - 1, color, random);
            AddLightBranch(nx, ny, angle + 0.31f + (float)random.NextDouble() * 0.15f,
                length * 0.68f, depth - 1, (color + 1) % 6, random);
        }

        void DrawLightPillar(Graphics g)
        {
            g.DrawImage(pillarCache, 0, 0, W, H);
            foreach (var p in lightParticles)
            {
                float x = p.X, y = p.Y, size = p.Radius;
                float brightness = 0.5f + 0.5f * (float)Math.Sin(anim * 1.1f + p.Phase);
                if (p.Flow)
                {
                    float t = (p.Y + anim * (0.035f + p.X * 0.018f)) % 1f;
                    float turn = t * 16f + p.Phase + anim * 0.3f;
                    float width = (7f + 15f * (float)Math.Sin(t * Math.PI)) * W / DesignW;
                    x = W / 2f + (float)Math.Cos(turn) * width;
                    y = 404f - t * 190f;
                    brightness *= 0.35f + 0.65f * (1f + (float)Math.Sin(turn)) / 2f;
                    size *= 0.85f + 0.35f * (float)Math.Sin(t * Math.PI);
                }
                else
                {
                    x = PillarX(x, y);
                    x += (float)Math.Sin(anim * 0.35f + p.Phase) * 0.75f;
                    y += (float)Math.Cos(anim * 0.28f + p.Phase) * 0.5f;
                }
                y = PillarY(y);
                int alpha = Math.Min(11, (int)(brightness * 11));
                g.FillEllipse(particleBrushes[p.Color, alpha], x - size, y - size, size * 2, size * 2);
                if (p.Radius > 1.32f && brightness > 0.7f)
                    g.FillEllipse(particleBrushes[p.Color, 0], x - size * 2.8f, y - size * 2.8f, size * 5.6f, size * 5.6f);
            }
        }

        void DrawOrbitCard(Graphics g, Proj pr, bool hover)
        {
            var icon = hover ? pr.It.Icon : (pr.It.Ghost ?? pr.It.Icon);
            if (icon == null) return;
            float scale = pr.Scale * (hover ? 44f / 38f : 1f);
            dimMatrix.Matrix33 = hover ? 1f : (pr.Z < 0 ? 0.65f : 1f);
            dimAttr.SetColorMatrix(dimMatrix);
            DrawMovingImage(g, icon, pr.X - 19f * scale, pr.Y - 19f * scale,
                38f * scale, 38f * scale, dimAttr);
        }

        void LayoutOrbitLabels()
        {
            var placed = new List<RectangleF>();
            for (int i = projCache.Count - 1; i >= 0; i--)
            {
                var pr = projCache[i];
                float half = pr.Size / 2f;
                var positions = new[] {
                    new PointF(pr.X - 64, pr.Y + half + 5),
                    new PointF(pr.X - 64, pr.Y - half - 41),
                    new PointF(pr.X + half + 8, pr.Y - 18),
                    new PointF(pr.X - half - 136, pr.Y - 18),
                    new PointF(pr.X - 112, pr.Y + half + 5),
                    new PointF(pr.X - 16, pr.Y + half + 5)
                };
                float best = float.MaxValue;
                RectangleF chosen = RectangleF.Empty;
                int previous, chosenSlot = 0;
                if (!orbitLabelSlots.TryGetValue(pr.It, out previous)) previous = 0;
                for (int k = 0; k < positions.Length; k++)
                {
                    var candidate = new RectangleF(
                        (float)Math.Round(Math.Max(8, Math.Min(W - 136, positions[k].X))),
                        (float)Math.Round(Math.Max(GridTop + 2, Math.Min(H - FooterH - 40, positions[k].Y))), 128, 36);
                    float score = k * 0.2f + (k == previous ? 0 : 240);
                    foreach (var used in placed)
                    {
                        var overlap = RectangleF.Intersect(candidate, RectangleF.Inflate(used, 3, 3));
                        score += overlap.Width * overlap.Height * 4;
                    }
                    foreach (var other in projCache)
                    {
                        var bounds = new RectangleF(other.X - other.Size / 2 - 3,
                            other.Y - other.Size / 2 - 3, other.Size + 6, other.Size + 6);
                        var overlap = RectangleF.Intersect(candidate, bounds);
                        score += overlap.Width * overlap.Height * 2;
                    }
                    if (score < best) { best = score; chosen = candidate; chosenSlot = k; }
                }
                pr.LabelBounds = chosen;
                orbitLabelSlots[pr.It] = chosenSlot;
                placed.Add(chosen);
            }
        }

        void DrawOrbitLabels(Graphics g)
        {
            // Names use native-size text, never the scaled/faded icon bitmap.
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var text = new SolidBrush(Color.FromArgb(248, 251, 255)))
            using (var fill = new SolidBrush(Color.FromArgb(238, 22, 36, 59)))
            using (var edge = new Pen(Color.FromArgb(95, 143, 181, 224), 0.7f))
            using (var leader = new Pen(Color.FromArgb(130, 163, 198, 237), 0.7f))
            using (var format = new StringFormat {
                Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
            })
            {
                foreach (var pr in projCache)
                {
                    var rect = pr.LabelBounds;
                    var point = new PointF(Math.Max(rect.Left, Math.Min(rect.Right, pr.X)),
                        Math.Max(rect.Top, Math.Min(rect.Bottom, pr.Y)));
                    g.DrawLine(leader, pr.X, pr.Y, point.X, point.Y);
                    using (var path = RoundRect(rect, 6))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                    var inner = RectangleF.Inflate(rect, -5, -1);
                    g.DrawString(pr.It.OrbitDisplayName ?? pr.It.Name, fontLabel, text, inner, format);
                }
            }
        }

        // 每个文件预渲染成"图标 + 两行文件名"的小卡片，每帧只做缩放贴图
        void BuildBillboards()
        {
            foreach (var it in items)
            {
                if (it.BmpNormal != null) it.BmpNormal.Dispose();
                if (it.BmpHover != null) it.BmpHover.Dispose();
                it.BmpNormal = RenderBillboard(it, false, 1f);
                it.BmpHover = RenderBillboard(it, true, 1f);
            }
        }

        Bitmap RenderBillboard(RecentItem it, bool hover, float dim)
        {
            const int bw = 92, bh = 76;
            var bmp = new Bitmap(bw, bh, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                var icon = hover ? it.Icon : (it.Ghost ?? it.Icon);
                if (icon != null) g.DrawImage(icon, (bw - 38) / 2f, 0, 38, 38);

                // Keep names readable even when an old file's icon has faded out.
                int a = hover ? 255 : (int)Math.Max(170, Math.Min(255, 245 * it.Alpha * dim));
                using (var f = new SolidBrush(Color.FromArgb(a, 222, 232, 246)))
                using (var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    string label = string.IsNullOrEmpty(it.DisplayName) ? it.Name : it.DisplayName;
                    g.DrawString(label, fontLabel, f, new RectangleF(0, 40, bw, 34), sf);
                }
            }
            return bmp;
        }

        void DrawHeader(Graphics g)
        {
            using (var shade = new SolidBrush(Color.FromArgb(105, 21, 38, 65)))
                g.FillRectangle(shade, 0, 0, W, HeaderH);
            using (var f = new SolidBrush(cTitle))
                g.DrawString("最近一周", fontTitle, f, PadX + 2, 20);

            using (var f = new SolidBrush(cMeta))
                g.DrawString(totalCount + " 个文件", fontMeta, f, PadX + 2, 46);

            // 右侧状态点
            float cx = W - PadX - 104, cy = 30;
            using (var b = new SolidBrush(Color.FromArgb(34, 197, 94)))
                g.FillEllipse(b, cx, cy, 6, 6);
            using (var f = new SolidBrush(cMeta))
                g.DrawString("7 天", fontMeta, f, cx + 10, cy - 6);

            // 关闭按钮：悬停变亮，点击直接退出
            var cr = CloseRect;
            if (closeHover)
            {
                using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                    g.FillEllipse(b, cr);
            }
            var penColor = closeHover
                ? Color.FromArgb(236, 242, 252)
                : Color.FromArgb(126, 146, 176);
            using (var pen = new Pen(penColor, 1.6f))
            {
                g.DrawLine(pen, cr.Left + 10, cr.Top + 10, cr.Right - 10, cr.Bottom - 10);
                g.DrawLine(pen, cr.Right - 10, cr.Top + 10, cr.Left + 10, cr.Bottom - 10);
            }
        }

        Rectangle CloseRect
        {
            get { return new Rectangle(W - 42, 12, 30, 30); }
        }

        void DrawList(Graphics g)
        {
            var state = g.Save();
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var listRect = new Rectangle(0, GridTop, W, H - GridTop - FooterH);
            g.SetClip(listRect);

            int viewH = listRect.Height;

            if (items.Count == 0)
            {
                using (var f = new SolidBrush(Color.FromArgb(110, 150, 170, 200)))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("这一周还没有打开过文件", fontName, f,
                        new RectangleF(0, GridTop, W, viewH), sf);
                }
            }

            if (constellation)
            {
                BuildConstellation(false);
                foreach (var pr in projCache)
                    if (pr.Z < 0 && !ReferenceEquals(pr.It, hoverItem)) DrawOrbitCard(g, pr, false);
                g.SetClip(new Rectangle(6, 6, W - 12, H - 12));
                DrawLightPillar(g);
                g.SetClip(listRect);
                foreach (var pr in projCache)
                    if (pr.Z >= 0 && !ReferenceEquals(pr.It, hoverItem)) DrawOrbitCard(g, pr, false);
                if (hoverItem != null)
                {
                    var hp = projCache.FirstOrDefault(p => ReferenceEquals(p.It, hoverItem));
                    if (hp != null)
                    {
                        DrawOrbitCard(g, hp, true);
                    }
                }
                DrawOrbitLabels(g);
                if (hoverItem != null)
                {
                    var hp = projCache.FirstOrDefault(p => ReferenceEquals(p.It, hoverItem));
                    if (hp != null) DrawHoverPill(g, hoverItem, hp);
                }
            }
            else
            {
                BuildProjection();
                foreach (var pr in projCache)
                {
                    bool hov = ReferenceEquals(pr.It, hoverItem);
                    float s = pr.Scale * (hov ? 1.15f : 1f);

                    if (hov)
                    {
                        float gr = 30f * s;
                        using (var glow = new GraphicsPath())
                        {
                            glow.AddEllipse(pr.X - gr, pr.Y - gr, gr * 2, gr * 2);
                            using (var gb = new PathGradientBrush(glow)
                            {
                                CenterColor = Color.FromArgb(52, 56, 189, 248),
                                SurroundColors = new[] { Color.FromArgb(0, 56, 189, 248) }
                            })
                                g.FillPath(gb, glow);
                        }
                    }

                    var bmp = hov ? pr.It.BmpHover : pr.It.BmpNormal;
                    if (bmp != null)
                    {
                        float w = bmp.Width * s;
                        float h = bmp.Height * s;
                        DrawMovingImage(g, bmp, pr.X - w / 2f, pr.Y - 19f * s, w, h);
                    }
                }

                int rows = (items.Count + Cols - 1) / Cols;
                if (rows > RowsVisible)
                {
                    float trackH = viewH - 8;
                    float thumbH = Math.Max(24f, trackH * RowsVisible / rows);
                    float thumbY = GridTop + 4 + (trackH - thumbH) * scrollRow / Math.Max(1, rows - RowsVisible);
                    using (var b = new SolidBrush(Color.FromArgb(46, 120, 170, 220)))
                        g.FillRectangle(b, W - 5, thumbY, 3, thumbH);
                }
            }

            g.Restore(state);
        }

        // GDI+ fractional destination rectangles can reverse the sampling phase between
        // pixel boundaries. Keep the destination integral and move the source sample
        // instead, so both translation and scaling remain continuous.
        internal static void DrawMovingImage(Graphics g, Bitmap image,
            float x, float y, float width, float height, System.Drawing.Imaging.ImageAttributes attributes = null)
        {
            int left = (int)Math.Floor(x) - 2;
            int top = (int)Math.Floor(y) - 2;
            int right = (int)Math.Ceiling(x + width) + 2;
            int bottom = (int)Math.Ceiling(y + height) + 2;
            float sx = image.Width / width;
            float sy = image.Height / height;
            g.DrawImage(image, new Rectangle(left, top, right - left, bottom - top),
                (left - x) * sx, (top - y) * sy,
                (right - left) * sx, (bottom - top) * sy, GraphicsUnit.Pixel, attributes);
        }

        // Files orbit the particle column in four rings, with matching depth and hit order.
        void BuildConstellation(bool simulate)
        {
            projCache.Clear();
            int total = items.Count;
            int pages = Math.Max(1, (total + PageSize - 1) / PageSize);
            if (page >= pages) page = pages - 1;
            int start = page * PageSize;
            int count = Math.Min(PageSize, total - start);

            float orbit = anim * 0.20f;
            for (int k = 0; k < count; k++)
            {
                var it = items[start + k];
                int ring = k / 3;
                int inRing = Math.Min(3, count - ring * 3);
                float theta = (k % 3) * (float)(Math.PI * 2) / inRing + ring * 0.72f + orbit;
                float depth = (float)Math.Sin(theta);
                float scale = 0.82f + (depth + 1f) * 0.09f;
                projCache.Add(new Proj {
                    It = it,
                    X = W / 2f + (float)Math.Cos(theta) * (W / 2f - 88f) + parX * 15f,
                    Y = OrbitY(ring) + depth * 23f + parY * 10f,
                    Z = depth,
                    Scale = scale,
                    Size = 38f * scale,
                    Seq = k
                });
            }
            // Back-to-front order is shared by rendering and hit testing.
            projCache.Sort((a, b) => {
                int order = a.Z.CompareTo(b.Z);
                return order != 0 ? order : a.Seq.CompareTo(b.Seq);
            });
            LayoutOrbitLabels();
        }

        float OrbitY(int ring)
        {
            int rings = PageSize / 3;
            float first = GridTop + 65f, last = H - FooterH - 100f;
            return rings == 1 ? (first + last) / 2f : first + ring * (last - first) / (rings - 1);
        }
        void DrawHoverPill(Graphics g, RecentItem it, Proj pr)
        {
            float maxW = 240f;
            float tw = g.MeasureString(it.Name, fontFoot).Width;
            float w = Math.Min(maxW, tw + 18);
            float h = 22;
            float x = Math.Max(8, Math.Min(W - 8 - w, pr.X - w / 2));
            float y = pr.Y - pr.Size / 2f - h - 10;
            if (y < GridTop + 4) y = pr.Y + pr.Size / 2f + 8;

            using (var path = RoundRect(new RectangleF(x, y, w, h), 11f))
            {
                using (var b = new SolidBrush(Color.FromArgb(228, 14, 19, 30)))
                    g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb(60, 120, 170, 220), 1f))
                    g.DrawPath(pen, path);
            }
            using (var f = new SolidBrush(Color.FromArgb(235, 240, 248)))
            using (var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
                g.DrawString(it.Name, fontFoot, f, new RectangleF(x + 4, y, w - 8, h), sf);
        }

        void DrawFooter(Graphics g)
        {
            using (var shade = new SolidBrush(Color.FromArgb(175, 24, 29, 53)))
                g.FillRectangle(shade, 0, H - FooterH, W, FooterH);
            using (var pen = new Pen(Color.FromArgb(22, 255, 255, 255), 1f))
                g.DrawLine(pen, PadX, H - FooterH + 1, W - PadX, H - FooterH + 1);
            using (var grip = new Pen(Color.FromArgb(130, 158, 188, 223), 1f))
                for (int i = 0; i < 3; i++)
                    g.DrawLine(grip, W - 10 - i * 4, H - 7, W - 7, H - 10 - i * 4);

            if (hoverItem != null)
            {
                var it = hoverItem;
                using (var f = new SolidBrush(Color.FromArgb(214, 226, 240)))
                {
                    var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                    g.DrawString(it.Name, fontFoot, f, new RectangleF(PadX + 2, H - FooterH + 12, W - PadX * 2 - 110, 16), sf);
                }
                using (var f = new SolidBrush(cMeta))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                    g.DrawString(it.AgeText, fontFoot, f, new RectangleF(W - PadX - 108, H - FooterH + 12, 104, 16), sf);
                }
            }
            else
            {
                using (var f = new SolidBrush(Color.FromArgb(96, 120, 140, 170)))
                    g.DrawString(constellation
                        ? "悬停查看名称 · 滚轮换一批 · × 关闭"
                        : "悬停查看名称 · 滚轮翻页 · × 关闭", fontFoot, f, PadX + 2, H - FooterH + 12);

                if (constellation)
                {
                    int pages = Math.Max(1, (items.Count + PageSize - 1) / PageSize);
                    using (var f = new SolidBrush(cMeta))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
                        g.DrawString((page + 1) + " / " + pages, fontFoot, f,
                            new RectangleF(W - PadX - 60, H - FooterH + 12, 56, 16), sf);
                    }
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            loopRunning = false;
            SaveSettings();
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            foreach (var it in items) it.Dispose();
            if (hiG != null) { hiG.Dispose(); hiG = null; }
            if (hiBuf != null) { hiBuf.Dispose(); hiBuf = null; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !visualResourcesDisposed)
            {
                visualResourcesDisposed = true;
                if (refresh != null) { refresh.Stop(); refresh.Dispose(); refresh = null; }
                if (pillarCache != null) { pillarCache.Dispose(); pillarCache = null; }
                if (bgCache != null) { bgCache.Dispose(); bgCache = null; }
                foreach (var brush in particleBrushes) if (brush != null) brush.Dispose();
                dimAttr.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                long position = m.LParam.ToInt64();
                var point = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
                const int grip = 9;
                bool left = point.X >= 0 && point.X < grip;
                bool right = point.X < W && point.X >= W - grip;
                bool top = point.Y >= 0 && point.Y < grip;
                bool bottom = point.Y < H && point.Y >= H - grip;
                int hit = top && left ? 13 : top && right ? 14 : bottom && left ? 16
                    : bottom && right ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
                if (hit != 0) m.Result = new IntPtr(hit);
                return;
            }
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MINIMIZE = 0xF020;
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
            {
                Hide();
                return;
            }
            base.WndProc(ref m);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    static class Program
    {
        internal static bool Headless;
        internal static bool DemoMode;
        internal static bool DiagnosticsEnabled;
        internal static string TickTestPath;
        internal static readonly System.Collections.Generic.List<double> TickDeltas = new System.Collections.Generic.List<double>();
        internal static double lastTickMs;

        [STAThread]
        static void Main(string[] args)
        {
            DemoMode = args.Contains("--demo");
            DiagnosticsEnabled = args.Contains("--diagnostics");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportError("界面运行失败", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            try
            {
                WriteDiagnostic("启动 " + Application.ExecutablePath);
                WriteDiagnostic("进程令牌受限=" + Native.RestrictedProcess()
                    + "; 完整性级别=0x" + Native.IntegrityLevel().ToString("X")
                    + "; TEMP=" + Environment.GetEnvironmentVariable("TEMP")
                    + "; TMP=" + Environment.GetEnvironmentVariable("TMP"));
                Run(args);
            }
            catch (Exception ex)
            {
                ReportError("模块启动失败", ex);
            }
            finally { Native.timeEndPeriod(1); }
        }

        static void Run(string[] args)
        {
            Native.SetProcessDPIAware();
            Native.timeBeginPeriod(1);

            int days = 7;
            string shotPath = null;
            string selfTestPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--shot" && i + 1 < args.Length) shotPath = args[i + 1];
                if (args[i] == "--selftest" && i + 1 < args.Length) selfTestPath = args[i + 1];
                if (args[i] == "--days" && i + 1 < args.Length) int.TryParse(args[i + 1], out days);
                if (args[i] == "--ticktest" && i + 1 < args.Length) TickTestPath = args[i + 1];
            }

            if (args.Contains("--bench"))
            {
                Headless = true;
                string benchPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recent-widget-bench.txt");
                try
                {
                    using (var form = new WidgetForm())
                    {
                        form.SetPreviewAnim(3f);
                        int bw = form.ClientSize.Width, bh = form.ClientSize.Height;
                        using (var bmp = new Bitmap(bw, bh))
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            for (int i = 0; i < 120; i++)
                            {
                                using (var g = Graphics.FromImage(bmp)) form.Render(g, false);
                            }
                            sw.Stop();
                            File.WriteAllText(benchPath,
                                (sw.Elapsed.TotalMilliseconds / 120.0).ToString("F2") + " ms/frame");
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(benchPath, "ERR " + ex); } catch { }
                }
                return;
            }

            if (selfTestPath != null)
            {
                RunSelfTest(selfTestPath);
                return;
            }

            if (shotPath != null)
            {
                Headless = true;
                bool demo = args.Contains("--demo");
                using (var form = new WidgetForm())
                {
                    if (demo) form.SpreadDemoAges();
                    form.SetPreviewHover(args.Contains("--no-hover") ? -1 : 1);
                    form.SetPreviewAnim(6.5f);
                    form.SettleConstellation();
                    ShowShot(form, shotPath, 2f);
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\RecentWidget.SingleInstance", out created))
            {
                if (!created)
                {
                    WriteDiagnostic("已有实例运行，未启动第二个窗口。");
                    MessageBox.Show("最近一周已经在运行。\n\n请在系统托盘中选择“显示 / 隐藏”查看窗口。"
                        + "\n如果刚更新程序，请先从旧程序的托盘菜单选择“退出”，再启动新版。",
                        "最近一周", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    var form = new WidgetForm();
                    form.Shown += (s, e) => WriteDiagnostic("窗口已显示，文件数=" + form.ItemCount);
                    Application.Run(form);
                    WriteDiagnostic("窗口正常关闭。");
                }
                catch (Exception ex)
                {
                    ReportError("模块启动失败", ex);
                }
            }
        }

        public static string ErrorLogPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecentWidget");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "error.log");
        }

        internal static void LogError(Exception ex)
        {
            if (!DiagnosticsEnabled) return;
            WriteDiagnostic(ex == null ? "(null)" : ex.ToString());
            try
            {
                File.AppendAllText(ErrorLogPath(),
                    DateTime.Now.ToString("o") + "  " + (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine);
            }
            catch { }
        }

        internal static void WriteDiagnostic(string message)
        {
            if (!DiagnosticsEnabled) return;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecentWidget-diagnostic.log"),
                    DateTime.Now.ToString("o") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        static void ReportError(string title, Exception ex)
        {
            LogError(ex);
            string help = DiagnosticsEnabled
                ? "\n\n日志可能含本地路径，分享前请脱敏。日志：" + ErrorLogPath()
                : "\n\n详细日志默认关闭。如需排查，请添加 --diagnostics 启动；分享日志前请脱敏。";
            MessageBox.Show(title + "：" + ex.Message + help,
                "最近一周", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void RunSelfTest(string path)
        {
            var log = new System.Text.StringBuilder();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new WidgetForm())
                {
                    var t = new System.Windows.Forms.Timer { Interval = 1600 };
                    t.Tick += (s, e) =>
                    {
                        t.Stop();
                        log.AppendLine("tick ok items=" + form.ItemCount + " visible=" + form.Visible);
                        form.Close();
                    };
                    form.Shown += (s, e) =>
                    {
                        log.AppendLine("shown ok bounds=" + form.Bounds);
                        t.Start();
                    };
                    Application.Run(form);
                }
                log.AppendLine("closed ok");
            }
            catch (Exception ex)
            {
                log.AppendLine("ERR " + ex);
            }
            File.WriteAllText(path, log.ToString());
        }

        static void ShowShot(WidgetForm form, string path, float scale)
        {
            int w = (int)(form.ClientSize.Width * scale);
            int h = (int)(form.ClientSize.Height * scale);
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(7, 9, 16));
                g.ScaleTransform(scale, scale);
                form.Render(g, true);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}

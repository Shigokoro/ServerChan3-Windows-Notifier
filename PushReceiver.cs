using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

internal static class Program
{
    private const string Version = "0.1.1";   // 版本号只在正式发版时修改（见本地 dev/DEVNOTES.md）
    private const string Sc3LoginUrl = "https://bot.ftqq.com/login/by/sendkey";
    private const string Sc3InboxUrl = "https://bot.ftqq.com/sc3/push/index";

    private static string _host = "127.0.0.1";
    private static int _port = 8765;
    private static string _token = "";
    private static string _appId = "Pusher.Win.Receiver";
    private static string _clickMode = "window";
    private static bool _logEnabled = true;
    private static string _logPath = LogFile;
    private static string _storePath = StoreFile;
    private static string _seenPath = SeenFile;
    private static string _readPath = ReadFile;
    private static long _count = 0;
    private static DateTime _startUtc = DateTime.UtcNow;

    private const string AppTitle = "Server酱3 推送通知";
    private const string StoreFile = "notifications.jsonl";
    private const string SeenFile = "seen.txt";
    private const string ReadFile = "read.txt";
    private const string NavFile = "nav.txt";
    private const string IniFile = "PushReceiver.ini";
    private const string LogFile = "PushReceiver.log";
    private const int PollMinSeconds = 5;
    private const int PollMaxSeconds = 3600;
    private const int BadgeHeight = 19;          // 详情行徽标高度（时间与徽标共用，保证同一竖直基准）

    private static string _sendKey = "";

    // ── 唯一一份字体表：所有界面字体都从这里取；禁止在各处 new Font(...) ──
    private static readonly Font FBase = MakeFont("Microsoft YaHei UI", 9F, FontStyle.Regular);
    private static readonly Font FSmall = MakeFont("Microsoft YaHei UI", 7.5F, FontStyle.Regular);
    private static readonly Font FChip = MakeFont("Microsoft YaHei UI", 8F, FontStyle.Regular);
    private static readonly Font FTag = MakeFont("Microsoft YaHei UI", 6.6F, FontStyle.Regular);
    private static readonly Font FTitle = MakeFont("Microsoft YaHei UI", 12F, FontStyle.Bold);
    private static readonly Font FHead = MakeFont("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
    private static readonly Font FIcon = MakeFont("Segoe MDL2 Assets", 10F, FontStyle.Regular);
    private static readonly Font FIconSmall = MakeFont("Segoe MDL2 Assets", 7F, FontStyle.Regular);
    private static readonly Font FIconBig = MakeFont("Segoe MDL2 Assets", 13F, FontStyle.Regular);

    private static Font MakeFont(string family, float size, FontStyle style)
    {
        try { return new Font(family, size, style); }
        catch { try { return new Font(family, size); } catch { return SystemFonts.DefaultFont; } }
    }

    // ── 唯一一份「字形墨迹框」测量：GDI 渲染一次并扫描，按 字形+字号 缓存 ──
    private static readonly Dictionary<string, Rectangle> _inkCache = new Dictionary<string, Rectangle>();

    // 唯一一份「把文字/字形放进盒子」的实现：先量墨迹框，再按墨迹居中。
    // 任何需要"居中/对齐到盒子里"的绘制都必须走这里，禁止各自用 VerticalCenter 估算。
    // align: 0=左对齐 1=居中 2=右对齐（一律按墨迹框对齐，返回墨迹宽度供排版续接）
    private static int DrawInk(Graphics g, string text, Font f, Rectangle box, Color c, int align)
    {
        Rectangle ink = IconInk(text, f);
        int x;
        if (align == 1) x = box.X + (box.Width - ink.Width) / 2 - ink.X;
        else if (align == 2) x = box.Right - ink.Width - ink.X;
        else x = box.X - ink.X;
        int y = box.Y + (box.Height - ink.Height) / 2 - ink.Y;
        TextRenderer.DrawText(g, text, f, new Point(x, y), c, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return ink.Width;
    }

    // 排版推进量：GDI 的推进宽（相邻元素排布用它，不能用墨迹宽 —— 墨迹不含左右边距）
    private static int TextAdvance(string text, Font f)
    {
        return TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
    }

    private static Rectangle IconInk(string glyph, Font f)
    {
        string key = glyph + "|" + f.SizeInPoints.ToString("0.0", CultureInfo.InvariantCulture);
        Rectangle cached;
        if (_inkCache.TryGetValue(key, out cached)) return cached;
        Rectangle box = new Rectangle(0, 0, 1, 1);
        try
        {
            // 位图必须按文字实际尺寸分配：写死尺寸会把长字符串裁掉，量出的墨迹宽会偏小
            Size need = TextRenderer.MeasureText(glyph, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            int bw = Math.Min(4096, Math.Max(32, need.Width + 32));
            int bh = Math.Min(512, Math.Max(32, need.Height + 32));
            using (Bitmap bm = new Bitmap(bw, bh))
            {
                using (Graphics g2 = Graphics.FromImage(bm))
                {
                    g2.Clear(Color.Black);
                    TextRenderer.DrawText(g2, glyph, f, new Point(12, 12), Color.White,
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                int minx = 9999, miny = 9999, maxx = -1, maxy = -1;
                for (int y = 0; y < bh; y++)
                {
                    for (int x = 0; x < bw; x++)
                    {
                        if (bm.GetPixel(x, y).R > 96)   // 只认亮核：含抗锯齿边缘会把细笔画撑大，视觉中心就偏了
                        {
                            if (x < minx) minx = x;
                            if (x > maxx) maxx = x;
                            if (y < miny) miny = y;
                            if (y > maxy) maxy = y;
                        }
                    }
                }
                if (maxx >= 0) box = new Rectangle(minx - 12, miny - 12, maxx - minx + 1, maxy - miny + 1);
            }
        }
        catch { }
        _inkCache[key] = box;
        return box;
    }

    // 首次运行自建 AUMID：通知里显示的名称与图标（不依赖任何手工注册表操作）
    private static void RegisterAumid(string baseDir)
    {
        try
        {
            string icon = Path.Combine(baseDir, "app-icon.png");
            if (!File.Exists(icon))
            {
                try
                {
                    using (Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    {
                        if (ic != null) ic.ToBitmap().Save(icon, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch { }
            }
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + _appId))
            {
                if (k == null) return;
                object cur = k.GetValue("DisplayName");
                if (cur == null || (string)cur != "推送通知") k.SetValue("DisplayName", "推送通知", Microsoft.Win32.RegistryValueKind.String);
                if (File.Exists(icon))
                {
                    object curIcon = k.GetValue("IconUri");
                    if (curIcon == null || (string)curIcon != icon) k.SetValue("IconUri", icon, Microsoft.Win32.RegistryValueKind.String);
                }
                object curBg = k.GetValue("IconBackgroundColor");
                if (curBg == null) k.SetValue("IconBackgroundColor", "FF6C7CE8", Microsoft.Win32.RegistryValueKind.String);
            }
        }
        catch (Exception ex) { Log("aumid register failed: " + ex.Message); }
    }
    private static bool _pollEnabled = true;
    private static int _pollInterval = 15;
    private static string _firstRunMode = "import";
    private static string _sc3Token = "";
    private static string _sc3LastSync = "";
    private static string _sc3LastError = "";
    private static int _sc3Count = 0;
    private static int _debugLevel = 1;
    private static bool _toastTag = true;
    private static bool _rawDumped = false;
    private static readonly HashSet<string> _seen = new HashSet<string>();
    private static readonly List<object> _keepAlive = new List<object>();
    private static readonly ManualResetEventSlim _syncNow = new ManualResetEventSlim(false);

    private sealed class Notice
    {
        public string Id = "";
        public string Time = "";
        public string Title = "";
        public string Body = "";
        public string Url = "";
        public string Src = "";
        public List<string> Tags = new List<string>();
    }

    private sealed class ScMsg
    {
        public string Id = "";
        public string Title = "";
        public string Body = "";
        public string Time = "";
        public List<string> Tags = new List<string>();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(baseDir, LogFile);
        RegisterAumid(baseDir);
        _storePath = Path.Combine(baseDir, StoreFile);
        _seenPath = Path.Combine(baseDir, SeenFile);
        _readPath = Path.Combine(baseDir, ReadFile);
        LoadConfig(Path.Combine(baseDir, IniFile));

        foreach (string a in args)
        {
            if (string.Equals(a, "--cleanup", StringComparison.OrdinalIgnoreCase))
            {
                RunCleanupOnly();
                return;
            }
            if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase))
            {
                bool settingsNew;
                using (Mutex sm = new Mutex(true, "Local\\WinPushReceiver_Settings", out settingsNew))
                {
                    if (!settingsNew)
                    {
                        ActivateExisting(AppTitle + " · 设置");
                        return;
                    }
                    RunSettings();
                }
                return;
            }
            if (!string.IsNullOrEmpty(a) && a.StartsWith("winpush://", StringComparison.OrdinalIgnoreCase))
            {
                bool viewerNew;
                using (Mutex vm = new Mutex(true, "Local\\WinPushReceiver_Viewer", out viewerNew))
                {
                    if (!viewerNew)
                    {
                        try
                        {
                            string req = QueryValue(a.Contains("?") ? a.Substring(a.IndexOf('?') + 1) : "", "id");
                            File.WriteAllText(Path.Combine(baseDir, NavFile), req == null ? "" : req, new UTF8Encoding(false));
                        }
                        catch { }
                        ActivateExisting(AppTitle);
                        return;
                    }
                    RunViewer(a);
                }
                return;
            }
        }

        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Local\\WinPushReceiver", out createdNew))
        {
            if (!createdNew)
            {
                Log("another instance is already running; exit");
                return;
            }
            try
            {
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TcpListener listener = new TcpListener(IPAddress.Parse(_host), _port);
                listener.Start();
                Log("started v" + Version + " on http://" + _host + ":" + _port + "/ appid=" + _appId +
                    " click=" + _clickMode + " sc3poll=" + (_pollEnabled && _sendKey.Length > 0 ? "on/" + _pollInterval + "s" : "off"));
                Thread pollThread = new Thread(PollLoop);
                pollThread.IsBackground = true;
                pollThread.Start();
                Thread listenThread = new Thread(delegate()
                {
                    while (true)
                    {
                        try
                        {
                            TcpClient client = listener.AcceptTcpClient();
                            ThreadPool.QueueUserWorkItem(HandleClient, client);
                        }
                        catch (Exception lex)
                        {
                            Log("accept error: " + lex.Message);
                        }
                    }
                });
                listenThread.IsBackground = true;
                listenThread.Start();

                NotifyIcon tray = new NotifyIcon();
                try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                tray.Text = AppTitle;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new DarkMenuRenderer();
                menu.ShowImageMargin = false;
                menu.Items.Add(MenuItem("打开详情窗口", delegate(object s2, EventArgs e2) { OpenViewer("latest"); }));
                menu.Items.Add(MenuItem("立即同步（含历史）", delegate(object s2, EventArgs e2) { ThreadPool.QueueUserWorkItem(delegate(object o2) { FullResync(); }); }));
                menu.Items.Add(MenuItem("全部标记为已读", delegate(object s2, EventArgs e2) { MarkAllReadGlobal(); }));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(MenuItem("设置 SendKey", delegate(object s2, EventArgs e2) { OpenSettings(); }));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(MenuItem("退出", delegate(object s2, EventArgs e2) { tray.Visible = false; Application.Exit(); }));
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate(object s2, EventArgs e2) { OpenViewer("latest"); };
                tray.Visible = true;
                _keepAlive.Add(tray);
                _keepAlive.Add(menu);
                _tray = tray;
                RefreshTrayIcon();
                Log("tray icon created");
                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.ToString());
            }
        }
    }

    // ── 托盘图标：有未读时在右上角画红点；红点几何与配色只在这里定义 ──
    private static NotifyIcon _tray = null;
    private static IntPtr _trayHicon = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon BuildTrayIcon(bool unread)
    {
        Bitmap bmp = new Bitmap(32, 32);
        try
        {
            using (Icon src = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                if (src != null) g.DrawIcon(src, new Rectangle(0, 0, 32, 32));
                if (unread)
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float d = 11f;                                // 红点直径（32px 图标的 ~34%，不再压住铃铛）
                    float ring = 2f;                              // 与图标同色的隔断环
                    float cx = 32f - 2f - d / 2f;                 // 圆心贴右上角，距边 2px
                    float cy = 2f + d / 2f;
                    using (SolidBrush rb = new SolidBrush(Color.FromArgb(0x4a, 0x5a, 0xc8)))
                        g.FillEllipse(rb, cx - d / 2f - ring, cy - d / 2f - ring, d + ring * 2f, d + ring * 2f);
                    using (SolidBrush db = new SolidBrush(Color.FromArgb(0xe5, 0x48, 0x4d)))
                        g.FillEllipse(db, cx - d / 2f, cy - d / 2f, d, d);
                }
            }
        }
        catch { }
        IntPtr h = bmp.GetHicon();
        Icon made = (Icon)Icon.FromHandle(h).Clone();
        if (_trayHicon != IntPtr.Zero) { try { DestroyIcon(_trayHicon); } catch { } }
        _trayHicon = h;
        bmp.Dispose();
        return made;
    }

    private static int UnreadCount()
    {
        HashSet<string> read = new HashSet<string>();
        try
        {
            string rf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ReadFile);
            if (File.Exists(rf)) foreach (string l in File.ReadAllLines(rf)) { string t = l.Trim(); if (t.Length > 0) read.Add(t); }
        }
        catch { }
        int n = 0;
        foreach (Notice x in LoadStore(500)) if (!read.Contains(x.Id)) n++;
        return n;
    }

    private static int _lastUnread = -1;   // 用于识别「未读归零」这一时刻

    // 清空 Windows 侧（操作中心）本应用的通知：清掉后任务栏图标上的未读红点随之消失
    private static void ClearWindowsToasts()
    {
        // 只清「本应用」的通知：必须用带 AUMID 的重载，且 AUMID 非空。
        // 严禁退化成无参 Clear()（那会以调用进程身份清理，语义不可控）；也绝不影响其它应用的通知。
        if (_appId == null || _appId.Length == 0) { Log("clear windows toasts skipped: empty appid"); return; }
        try
        {
            ToastNotificationManager.History.Clear(_appId);
            Log("cleared windows toasts (own appid only: " + _appId + ")");
        }
        catch (Exception ex) { Log("clear windows toasts failed: " + ex.Message); }
    }

    // 当前已无未读时才清（单个已读不会误清）
    private static void ClearToastsIfAllRead()
    {
        try { if (UnreadCount() == 0) ClearWindowsToasts(); }
        catch { }
    }

    private static void RefreshTrayIcon()
    {
        if (_tray == null) return;
        try
        {
            int unread = UnreadCount();
            if (unread == 0 && _lastUnread != 0) ClearWindowsToasts();   // 未读归零 → 清操作中心，任务栏红点消失
            _lastUnread = unread;
            Icon old = _tray.Icon;
            _tray.Icon = BuildTrayIcon(unread > 0);
            if (old != null) old.Dispose();
            string tip = unread > 0 ? (AppTitle + " · " + unread + " 条未读") : AppTitle;
            if (tip.Length > 60) tip = tip.Substring(0, 60);
            if (_tray.Text != tip) { _tray.Text = tip; Log("tray icon updated (unread=" + unread + ")"); }
        }
        catch { }
    }

    private static void LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultConfig(), new UTF8Encoding(false));
            return;
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "listen":
                    if (v.Length > 0) _host = v;
                    break;
                case "port":
                    int p;
                    if (int.TryParse(v, out p) && p > 0 && p < 65536) _port = p;
                    break;
                case "token":
                    _token = v;
                    break;
                case "appid":
                    if (v.Length > 0) _appId = v;
                    break;
                case "click":
                    if (v.Length > 0) _clickMode = v.ToLowerInvariant();
                    break;
                case "log":
                    _logEnabled = !(v == "0" || v.ToLowerInvariant() == "false");
                    break;
                case "sendkey":
                    _sendKey = v;
                    break;
                case "poll":
                    _pollEnabled = !(v == "0" || v.ToLowerInvariant() == "false");
                    break;
                case "poll_interval":
                    int pi;
                    if (int.TryParse(v, out pi)) _pollInterval = Math.Min(PollMaxSeconds, Math.Max(PollMinSeconds, pi));
                    break;
                case "firstrun":
                    if (v.Length > 0) _firstRunMode = v.ToLowerInvariant();
                    break;
                case "debug":
                    int dl;
                    if (int.TryParse(v, out dl) && dl >= 0 && dl <= 2) _debugLevel = dl;
                    break;
                case "toast_tag":
                    _toastTag = !(v == "0" || v.ToLowerInvariant() == "false");
                    break;
            }
        }
    }

    private static string DefaultConfig()
    {
        return string.Join(Environment.NewLine, new string[]
        {
            "# WinPushReceiver config",
            "# listen: 127.0.0.1 (local only) or 0.0.0.0 (LAN, set token!)",
            "listen=127.0.0.1",
            "port=" + _port,
            "token=",
            "appid=" + _appId,
            "# click: window = 点击通知打开本地详情窗口; url = 直接打开消息里的链接",
            "click=window",
            "log=1",
            "",
            "# ---- Server酱3 收件箱（官方服务器，非公开接口，见 README）----",
            "# sendkey: 官网 sc3.ft07.com 的 SendKey（留空=不启用）",
            "sendkey=",
            "poll=1",
            "poll_interval=15",
            "# firstrun: import=首次只弹最新一条(默认) / all=全部弹出 / skip=只入库不弹",
            "firstrun=import",
            "# debug: 0/1/2 —— 1=首次同步记录服务端字段名(默认), 2=另记首条原始JSON(含正文)",
            "debug=1",
            "# toast_tag: 1=通知标题带 [标签] 前缀（分类用）",
            "toast_tag=1",
            ""
        });
    }

    private static void Log(string msg)
    {
        if (!_logEnabled) return;
        try
        {
            FileInfo fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 1048576) File.Delete(_logPath);
            File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------------- Server酱3 收件箱轮询 ----------------

    private static void LoadSeen()
    {
        try
        {
            if (!File.Exists(_seenPath)) return;
            foreach (string l in File.ReadAllLines(_seenPath))
            {
                string t = l.Trim();
                if (t.Length > 0) _seen.Add(t);
            }
        }
        catch { }
    }

    private static void SaveSeen()
    {
        try
        {
            List<string> all = new List<string>(_seen);
            int start = all.Count > 2000 ? all.Count - 2000 : 0;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < all.Count; i++) sb.Append(all[i]).Append(Environment.NewLine);
            File.WriteAllText(_seenPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static void PollLoop()
    {
        LoadSeen();
        bool firstRun = !File.Exists(_seenPath);
        Log("sc3 poll loop started (firstRun=" + firstRun + ", interval=" + _pollInterval + "s, sendkey=" + (_sendKey.Length > 0 ? "set" : "empty") + ")");
        while (true)
        {
            try
            {
                if (!_pollEnabled || _sendKey.Length == 0)
                {
                    RefreshTrayIcon();      // 轮询关闭时也定期同步红点（被读/清理仍要反映）
                    _syncNow.Wait(30000);
                    _syncNow.Reset();
                    continue;
                }
                string token = EnsureSc3Token();
                List<ScMsg> msgs = FetchInbox(token, 1);
                msgs.Sort(delegate(ScMsg a, ScMsg b) { return string.CompareOrdinal(a.Time, b.Time); });

                List<ScMsg> fresh = new List<ScMsg>();
                foreach (ScMsg m in msgs)
                {
                    if (!_seen.Contains(m.Id)) fresh.Add(m);
                }

                int toasted = 0;
                for (int i = 0; i < fresh.Count; i++)
                {
                    ScMsg m = fresh[i];
                    string url = ExtractFirstLink(m.Body);
                    string recId = "sc3-" + m.Id;
                    AppendStore("sc3", m.Id, recId, m.Title, m.Body, url, m.Time, m.Tags);
                    bool isLast = (i == fresh.Count - 1);
                    bool doToast = true;
                    if (firstRun && _firstRunMode == "skip") doToast = false;
                    if (firstRun && _firstRunMode == "import" && !isLast) doToast = false;
                    if (doToast)
                    {
                        ShowToast(recId, m.Title, m.Body, url, m.Tags);
                        toasted++;
                    }
                    _seen.Add(m.Id);
                }
                if (fresh.Count > 0)
                {
                    SaveSeen();
                    _sc3Count += fresh.Count;
                    Log("sc3 sync: " + msgs.Count + " in inbox, " + fresh.Count + " new, " + toasted + " toasted");
                }
                firstRun = false;
                _sc3LastSync = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _sc3LastError = "";
                RefreshTrayIcon();          // 新消息入库后同步托盘红点
            }
            catch (Exception ex)
            {
                _sc3LastError = ex.Message;
                string msg = ex.Message;
                if (msg.Contains("401") || msg.Contains("token")) _sc3Token = "";
                Log("sc3 poll error: " + msg);
            }
            _syncNow.Wait(_pollInterval * 1000);
            _syncNow.Reset();
        }
    }

    private static void FullResync()
    {
        try
        {
            if (_sendKey.Length == 0) return;
            string token = EnsureSc3Token();
            HashSet<string> have = new HashSet<string>();
            foreach (Notice n in LoadStore(int.MaxValue))
            {
                if (n.Src.StartsWith("sc3:")) have.Add(n.Src.Substring(4));
            }
            int added = 0, pages = 0;
            for (int page = 1; page <= 5; page++)
            {
                List<ScMsg> msgs = FetchInbox(token, page);
                if (msgs.Count == 0) break;
                pages++;
                msgs.Sort(delegate(ScMsg a, ScMsg b) { return string.CompareOrdinal(a.Time, b.Time); });
                foreach (ScMsg m in msgs)
                {
                    if (have.Contains(m.Id)) continue;
                    AppendStore("sc3", m.Id, "sc3-" + m.Id, m.Title, m.Body, ExtractFirstLink(m.Body), m.Time, m.Tags);
                    have.Add(m.Id);
                    added++;
                }
                if (msgs.Count < 100) break;
            }
            _sc3LastSync = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _sc3LastError = "";
            Log("full resync: pages=" + pages + " added=" + added);
        }
        catch (Exception ex)
        {
            _sc3LastError = ex.Message;
            Log("full resync error: " + ex.Message);
        }
    }

    private static string EnsureSc3Token()
    {
        if (_sc3Token.Length > 0) return _sc3Token;
        string json = HttpRequest(Sc3LoginUrl, "POST", "sendkey=" + Uri.EscapeDataString(_sendKey), null);
        Dictionary<string, object> root = TryParseJson(json);
        string token = Field(root, "token");
        if (token == null)
        {
            Dictionary<string, object> data = AsMap(root != null && root.ContainsKey("data") ? root["data"] : null);
            token = Field(data, "token");
        }
        if (string.IsNullOrEmpty(token))
        {
            string err = Field(root, "error");
            throw new Exception("login failed: " + (err != null ? err : json.Substring(0, Math.Min(120, json.Length))));
        }
        _sc3Token = token;
        Log("sc3 login ok (token length " + token.Length + ")");
        return token;
    }

    private static List<ScMsg> FetchInbox(string token, int page)
    {
        string json = HttpRequest(page > 1 ? (Sc3InboxUrl + "?page=" + page.ToString(CultureInfo.InvariantCulture)) : Sc3InboxUrl, "GET", null, token);
        Dictionary<string, object> root = TryParseJson(json);
        if (root == null) throw new Exception("inbox returned non-JSON");
        object[] arr = null;
        Dictionary<string, object> pushes = AsMap(root.ContainsKey("pushes") ? root["pushes"] : null);
        if (pushes != null)
        {
            arr = AsArray(pushes.ContainsKey("data") ? pushes["data"] : null);
            if (arr == null) arr = AsArray(pushes.ContainsKey("list") ? pushes["list"] : null);
            if (arr == null) arr = AsArray(pushes.ContainsKey("rows") ? pushes["rows"] : null);
        }
        if (arr == null) arr = AsArray(root.ContainsKey("data") ? root["data"] : null);
        List<ScMsg> list = new List<ScMsg>();
        if (arr == null) return list;
        foreach (object o in arr)
        {
            Dictionary<string, object> m = AsMap(o);
            if (m == null) continue;
            ScMsg msg = new ScMsg();
            msg.Id = Field(m, "id");
            if (msg.Id == null) msg.Id = Field(m, "raw_id");
            if (msg.Id == null) continue;
            msg.Title = Field(m, "title");
            msg.Body = Field(m, "desp");
            if (msg.Body == null) msg.Body = Field(m, "body");
            msg.Time = Field(m, "created_at");
            if (msg.Time == null) msg.Time = Field(m, "updated_at");
            if (msg.Title == null) msg.Title = "(无标题)";
            if (msg.Body == null) msg.Body = "";
            if (msg.Time == null) msg.Time = "";
            msg.Tags = ParseTags(m);
            if (_debugLevel > 0 && !_rawDumped)
            {
                _rawDumped = true;
                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, object> kv in m) keys.Add(kv.Key);
                Log("sc3 raw item keys: " + string.Join(", ", keys.ToArray()));
                if (_debugLevel > 1)
                {
                    string raw = new JavaScriptSerializer().Serialize(m);
                    Log("sc3 raw item json: " + (raw.Length > 600 ? raw.Substring(0, 600) : raw));
                }
            }
            list.Add(msg);
        }
        return list;
    }

    private static List<string> ParseTags(Dictionary<string, object> m)
    {
        List<string> result = new List<string>();
        string[] keys = new string[] { "tags", "tag", "labels", "label", "tag_list", "tagList", "tag_names", "tagNames", "categories", "category" };
        foreach (string k in keys)
        {
            object v;
            if (m == null || !m.TryGetValue(k, out v) || v == null) continue;
            AddTags(result, v);
            if (result.Count > 0) break;
        }
        return result;
    }

    private static void AddTags(List<string> result, object v)
    {
        string s = v as string;
        if (s != null)
        {
            foreach (string part in s.Split(new char[] { (char)124, (char)44, (char)65292 }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim().TrimStart((char)35);
                if (t.Length > 0 && !result.Contains(t)) result.Add(t);
            }
            return;
        }
        object[] arr = AsArray(v);
        if (arr != null)
        {
            foreach (object o in arr)
            {
                if (o == null) continue;
                Dictionary<string, object> map = o as Dictionary<string, object>;
                if (map != null)
                {
                    string name = Field(map, "name");
                    if (name == null) name = Field(map, "title");
                    if (name == null) name = Field(map, "tag");
                    if (name == null) name = Field(map, "label");
                    if (name != null && !result.Contains(name)) result.Add(name);
                }
                else
                {
                    string t = Convert.ToString(o).Trim().TrimStart((char)35);
                    if (t.Length > 0 && !result.Contains(t)) result.Add(t);
                }
            }
        }
    }

    private static string ExtractFirstLink(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        int mark = body.IndexOf("](", StringComparison.Ordinal);
        if (mark < 0) return "";
        int start = mark + 2;
        int end = body.IndexOf(')', start);
        if (end <= start) return "";
        string url = body.Substring(start, end - start).Trim();
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        return "";
    }

    private static string HttpRequest(string url, string method, string formBody, string bearer)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Timeout = 20000;
        req.ReadWriteTimeout = 20000;
        req.UserAgent = "WinPushReceiver/" + Version;
        if (!string.IsNullOrEmpty(bearer)) req.Headers["Authorization"] = "Bearer " + bearer;
        if (formBody != null)
        {
            req.ContentType = "application/x-www-form-urlencoded";
            byte[] payload = Encoding.UTF8.GetBytes(formBody);
            req.ContentLength = payload.Length;
            using (Stream rs = req.GetRequestStream()) rs.Write(payload, 0, payload.Length);
        }
        try
        {
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }
        catch (WebException we)
        {
            if (we.Response != null)
            {
                string body = "";
                try
                {
                    using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
                }
                catch { }
                throw new Exception("HTTP " + (int)((HttpWebResponse)we.Response).StatusCode + " " + body);
            }
            throw;
        }
    }

    private static Dictionary<string, object> AsMap(object o)
    {
        return o as Dictionary<string, object>;
    }

    private static object[] AsArray(object o)
    {
        if (o == null) return null;
        object[] direct = o as object[];
        if (direct != null) return direct;
        if (o is string) return null;
        System.Collections.IEnumerable en = o as System.Collections.IEnumerable;
        if (en == null) return null;
        List<object> tmp = new List<object>();
        foreach (object item in en) tmp.Add(item);
        return tmp.ToArray();
    }

    // ---------------- 本地 HTTP 接口 ----------------

    private static void HandleClient(object state)
    {
        TcpClient client = (TcpClient)state;
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                NetworkStream stream = client.GetStream();

                string method, target, headers, body;
                if (!ReadRequest(stream, out method, out target, out headers, out body)) return;

                string pathOnly = target;
                string query = "";
                int qi = target.IndexOf('?');
                if (qi >= 0)
                {
                    pathOnly = target.Substring(0, qi);
                    query = target.Substring(qi + 1);
                }

                if (!OriginOk(headers))
                {
                    Log("rejected: cross-origin request");
                    Respond(stream, 403, "{\"ok\":false,\"error\":\"cross-origin rejected\"}");
                    return;
                }

                if (method == "GET" && pathOnly == "/health")
                {
                    long up = (long)(DateTime.UtcNow - _startUtc).TotalSeconds;
                    Respond(stream, 200, "{\"ok\":true,\"app\":\"WinPushReceiver\",\"version\":\"" + Version +
                        "\",\"uptimeSec\":" + up.ToString(CultureInfo.InvariantCulture) +
                        ",\"count\":" + _count.ToString(CultureInfo.InvariantCulture) +
                        ",\"sc3\":{\"enabled\":" + ((_pollEnabled && _sendKey.Length > 0) ? "true" : "false") +
                        ",\"lastSync\":\"" + _sc3LastSync + "\",\"newMessages\":" + _sc3Count.ToString(CultureInfo.InvariantCulture) +
                        ",\"lastError\":\"" + JsonEscape(_sc3LastError) + "\"}}");
                    return;
                }

                if (method == "GET" && pathOnly == "/sync")
                {
                    if (QueryValue(query, "full") == "1")
                    {
                        FullResync();
                        int total = LoadStore(int.MaxValue).Count;
                        Respond(stream, 200, "{\"ok\":true,\"message\":\"full resync done\",\"total\":" + total.ToString(CultureInfo.InvariantCulture) + "}");
                        return;
                    }
                    _syncNow.Set();
                    Respond(stream, 200, "{\"ok\":true,\"message\":\"sync triggered\"}");
                    return;
                }

                if (method == "GET" && pathOnly == "/notifications")
                {
                    int limit = 50;
                    string lim = QueryValue(query, "limit");
                    int parsed;
                    if (!string.IsNullOrEmpty(lim) && int.TryParse(lim, out parsed) && parsed > 0) limit = parsed;
                    List<Notice> all = LoadStore(limit);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"ok\":true,\"total\":").Append(all.Count.ToString(CultureInfo.InvariantCulture)).Append(",\"items\":[");
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(SerializeNotice(all[i]));
                    }
                    sb.Append("]}");
                    Respond(stream, 200, sb.ToString());
                    return;
                }

                if (method == "GET" && pathOnly == "/settings")
                {
                    OpenSettings();
                    Respond(stream, 200, "{\"ok\":true}");
                    return;
                }

                if (method == "GET" && pathOnly == "/reload")
                {
                    LoadConfig(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IniFile));
                    _sc3Token = "";
                    RefreshTrayIcon();
                    _syncNow.Set();
                    Respond(stream, 200, "{\"ok\":true,\"reloaded\":true,\"sendkeyConfigured\":" + (_sendKey.Length > 0 ? "true" : "false") + "}");
                    return;
                }

                if (method == "GET" && pathOnly == "/show")
                {
                    OpenViewer(QueryValue(query, "id"));
                    RefreshTrayIcon();
                    Respond(stream, 200, "{\"ok\":true}");
                    return;
                }

                if (pathOnly != "/notify")
                {
                    Respond(stream, 404, "{\"ok\":false,\"error\":\"not found\"}");
                    return;
                }
                if (method != "POST")
                {
                    Respond(stream, 405, "{\"ok\":false,\"error\":\"use POST\"}");
                    return;
                }
                if (_token.Length > 0 && !TokenOk(headers, query))
                {
                    Log("rejected: bad token");
                    Respond(stream, 403, "{\"ok\":false,\"error\":\"bad token\"}");
                    return;
                }

                string title = null, text = null, url = null;
                List<string> tags = new List<string>();
                Dictionary<string, object> map = TryParseJson(body);
                if (map != null)
                {
                    title = Field(map, "title");
                    text = Field(map, "body");
                    if (text == null) text = Field(map, "desp");
                    if (text == null) text = Field(map, "text");
                    url = Field(map, "url");
                    if (url == null) url = Field(map, "link");
                    tags = ParseTags(map);
                }
                else
                {
                    Dictionary<string, string> form = ParseForm(body);
                    string v;
                    if (form.TryGetValue("title", out v)) title = v;
                    if (form.TryGetValue("body", out v)) text = v;
                    if (text == null && form.TryGetValue("desp", out v)) text = v;
                    if (form.TryGetValue("url", out v)) url = v;
                    string tg;
                    if (form.TryGetValue("tags", out tg)) AddTags(tags, tg);
                    else if (form.TryGetValue("tag", out tg)) AddTags(tags, tg);
                }

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(text))
                {
                    Respond(stream, 400, "{\"ok\":false,\"error\":\"title or body required\"}");
                    return;
                }
                if (string.IsNullOrEmpty(title)) title = "通知";
                if (text == null) text = "";

                string id2 = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                AppendStore("http", "", id2, title, text, url, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), tags);
                ShowToast(id2, title, text, url, tags);
                _count++;
                Log("toast #" + _count + " id=" + id2 + " title=" + title);
                Respond(stream, 200, "{\"ok\":true,\"count\":" + _count.ToString(CultureInfo.InvariantCulture) + ",\"id\":\"" + id2 + "\"}");
            }
        }
        catch (Exception ex)
        {
            Log("handler error: " + ex.Message);
        }
    }

    private static bool OriginOk(string headers)
    {
        foreach (string line in headers.Split(new string[] { "\r\n" }, StringSplitOptions.None))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            if (line.Substring(0, c).Trim().ToLowerInvariant() != "origin") continue;
            string v = line.Substring(c + 1).Trim().ToLowerInvariant();
            if (v == "null") return false;
            return v.Contains("127.0.0.1") || v.Contains("localhost");
        }
        return true;
    }

    private static string QueryValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
        }
        return null;
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    private static string SerializeNotice(Notice n)
    {
        JavaScriptSerializer ser = new JavaScriptSerializer();
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["id"] = n.Id;
        d["t"] = n.Time;
        d["title"] = n.Title;
        d["body"] = n.Body;
        d["url"] = n.Url;
        d["src"] = n.Src;
        d["tags"] = n.Tags;
        return ser.Serialize(d);
    }

    private static void AppendStore(string src, string srcId, string id, string title, string body, string url, string time, List<string> tags)
    {
        try
        {
            Notice n = new Notice();
            n.Id = id;
            n.Time = string.IsNullOrEmpty(time) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : NormalizeTime(time);
            n.Title = title;
            n.Body = body;
            n.Url = url == null ? "" : url;
            n.Src = src + (string.IsNullOrEmpty(srcId) ? "" : ":" + srcId);
            if (tags != null) n.Tags = tags;
            File.AppendAllText(_storePath, SerializeNotice(n) + Environment.NewLine, new UTF8Encoding(false));
            FileInfo fi = new FileInfo(_storePath);
            if (fi.Exists && fi.Length > 524288) TrimStore(300);
        }
        catch (Exception ex)
        {
            Log("store write error: " + ex.Message);
        }
    }

    private static string NormalizeTime(string t)
    {
        string s = t.Replace('T', ' ').Trim();
        int plus = s.IndexOf('+');
        if (plus > 0) s = s.Substring(0, plus);
        int dot = s.IndexOf('.');
        if (dot > 0) s = s.Substring(0, dot);
        return s.Trim();
    }

    private static HashSet<string> LoadReadIds()
    {
        HashSet<string> ids = new HashSet<string>();
        try
        {
            if (File.Exists(_readPath))
            {
                foreach (string rl in File.ReadAllLines(_readPath))
                {
                    string rt = rl.Trim();
                    if (rt.Length > 0) ids.Add(rt);
                }
            }
        }
        catch { }
        return ids;
    }

    private static void SaveReadIds(HashSet<string> ids)
    {
        try
        {
            List<string> all2 = new List<string>(ids);
            int st = all2.Count > 3000 ? all2.Count - 3000 : 0;
            StringBuilder sb = new StringBuilder();
            for (int i = st; i < all2.Count; i++) sb.Append(all2[i]).Append(Environment.NewLine);
            File.WriteAllText(_readPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    private static void RunCleanupOnly()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            List<Notice> items = LoadStore(int.MaxValue);
            HashSet<string> reads = LoadReadIds();
            int rc = 0;
            foreach (Notice n in items) if (reads.Contains(n.Id)) rc++;
            string act = ShowCleanupDialog(null, items.Count, rc);
            if (act == null) return;
            if (act == "read") items.RemoveAll(delegate(Notice n) { return reads.Contains(n.Id); });
            else if (act == "all") items.Clear();
            WriteStore(items);
            ClearWindowsToasts();    // 清理后清空操作中心
            Log("cleanup(cli): " + act + " -> " + items.Count + " left");
        }
        catch (Exception ex)
        {
            Log("cleanup error: " + ex.ToString());
        }
    }

    private static void WriteStore(List<Notice> items)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (Notice n in items) sb.Append(SerializeNotice(n)).Append(Environment.NewLine);
            File.WriteAllText(_storePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("store rewrite error: " + ex.Message);
        }
    }

    private static string ShowCleanupDialog(IWin32Window owner, int total, int readCount)
    {
        Form d = new Form();
        try { d.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        d.Text = AppTitle + " · 清理";
        d.ClientSize = new Size(490, 200);
        d.StartPosition = FormStartPosition.CenterParent;
        d.FormBorderStyle = FormBorderStyle.FixedDialog;
        d.MaximizeBox = false;
        d.MinimizeBox = false;
        d.BackColor = CPanel;
        d.ForeColor = CText;
        d.Font = FBase;

        Label lbl = new Label();
        lbl.SetBounds(18, 18, 454, 70);
        lbl.ForeColor = CMuted;
        lbl.Text = "本机共 " + total + " 条通知，其中已读 " + readCount + " 条。" + Environment.NewLine +
                    "清理只删除本机列表记录，服务端不会重新推送回来。";

        Button bOne = FlatButton("删除当前这条", "\uE74D", 18, 146, 132, false);
        Button bRead = FlatButton("清理已读", "\uE74D", 158, 146, 110, false);
        Button bAll = FlatButton("全部清理", "\uE74D", 276, 146, 110, false);
        Button bCancel = FlatButton("取消", "\uE711", 394, 146, 78, false);

        d.Controls.Add(lbl);
        d.Controls.Add(bOne);
        d.Controls.Add(bRead);
        d.Controls.Add(bAll);
        d.Controls.Add(bCancel);

        string result = null;
        bOne.Click += delegate { result = "one"; d.Close(); };
        bRead.Click += delegate { result = "read"; d.Close(); };
        bAll.Click += delegate { result = "all"; d.Close(); };
        bCancel.Click += delegate { d.Close(); };
        d.Shown += delegate { try { d.Invalidate(true); d.Update(); } catch { } };
        ApplyDarkTitleBar(d.Handle);
        d.ShowDialog(owner);
        return result;
    }

    private static void TrimStore(int keep)
    {
        List<Notice> all = LoadStore(int.MaxValue);
        if (all.Count <= keep) return;
        StringBuilder sb = new StringBuilder();
        for (int i = all.Count - keep; i < all.Count; i++) sb.Append(SerializeNotice(all[i])).Append(Environment.NewLine);
        File.WriteAllText(_storePath, sb.ToString(), new UTF8Encoding(false));
    }

    private static List<Notice> LoadStore(int limit)
    {
        List<Notice> result = new List<Notice>();
        try
        {
            if (!File.Exists(_storePath)) return result;
            string[] lines = File.ReadAllLines(_storePath);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            int start = 0;
            if (limit > 0 && lines.Length > limit) start = lines.Length - limit;
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                try
                {
                    Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(line);
                    Notice n = new Notice();
                    n.Id = Nz(Field(d, "id"));
                    n.Time = Nz(Field(d, "t"));
                    n.Title = Nz(Field(d, "title"));
                    n.Body = Nz(Field(d, "body"));
                    n.Url = Nz(Field(d, "url"));
                    n.Src = Nz(Field(d, "src"));
                    n.Tags = ParseTags(d);
                    result.Add(n);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static string Nz(string s)
    {
        return s == null ? "" : s;
    }

    private static void OpenViewer(string id)
    {
        try
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinPushReceiver.exe");
            string uri = "winpush://show?id=" + (string.IsNullOrEmpty(id) ? "latest" : id);
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--show \"" + uri + "\"");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log("open viewer error: " + ex.Message);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(0x2f, 0x33, 0x3a); } }
        public override Color MenuItemBorder { get { return CBorderStrong; } }
        public override Color ToolStripDropDownBackground { get { return CPanel2; } }
        public override Color ImageMarginGradientBegin { get { return CPanel2; } }
        public override Color ImageMarginGradientMiddle { get { return CPanel2; } }
        public override Color ImageMarginGradientEnd { get { return CPanel2; } }
        public override Color MenuBorder { get { return CBorder; } }
        public override Color SeparatorDark { get { return CBorder; } }
        public override Color SeparatorLight { get { return CBorder; } }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : CText;
            base.OnRenderItemText(e);
        }
    }

    private static ToolStripMenuItem MenuItem(string text, EventHandler handler)
    {
        ToolStripMenuItem mi = new ToolStripMenuItem(text);
        mi.ForeColor = CText;
        mi.BackColor = CPanel2;
        mi.Click += handler;
        return mi;
    }

    private static void MarkAllReadGlobal()
    {
        try
        {
            List<Notice> items = LoadStore(int.MaxValue);
            HashSet<string> ids = LoadReadIds();
            foreach (Notice n in items) ids.Add(n.Id);
            SaveReadIds(ids);
            ClearWindowsToasts();    // 托盘菜单的「全部标记为已读」同样清空操作中心
            Log("tray: marked all read (" + items.Count + ")");
        }
        catch (Exception ex)
        {
            Log("mark all read error: " + ex.Message);
        }
    }

    private static void OpenSettings()
    {
        try
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinPushReceiver.exe");
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--settings");
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log("open settings error: " + ex.Message);
        }
    }

    private static void SaveConfigValues(string path, Dictionary<string, string> values)
    {
        List<string> lines = new List<string>();
        if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));
        List<string> done = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf((char)61);
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            if (values.ContainsKey(k))
            {
                lines[i] = k + "=" + values[k];
                done.Add(k);
            }
        }
        foreach (KeyValuePair<string, string> kv in values)
        {
            if (!done.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);
        }
        File.WriteAllText(path, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string DescribeCurrentState()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("SendKey：").Append(_sendKey.Length > 0 ? "已配置（" + _sendKey.Length + " 字符）" : "未配置");
        sb.Append("　轮询：").Append(_pollEnabled ? _pollInterval + " 秒" : "关闭");
        try
        {
            string json = HttpRequest("http://127.0.0.1:" + _port + "/health", "GET", null, null);
            Dictionary<string, object> root = TryParseJson(json);
            Dictionary<string, object> sc3 = AsMap(root != null && root.ContainsKey("sc3") ? root["sc3"] : null);
            if (sc3 != null)
            {
                sb.Append(Environment.NewLine).Append("守护进程：运行中　上次同步：").Append(Nz(Field(sc3, "lastSync")));
                sb.Append("　本次已收：").Append(Nz(Field(sc3, "newMessages")));
                string err = Nz(Field(sc3, "lastError"));
                if (err.Length > 0) sb.Append(Environment.NewLine).Append("上次错误：").Append(err);
            }
        }
        catch (Exception ex)
        {
            sb.Append(Environment.NewLine).Append("守护进程：未响应（").Append(ex.Message).Append("）");
        }
        return sb.ToString();
    }

    private static void RunSettings()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, IniFile);

            Font fontBase, fontSmall;
            fontBase = FBase;
            fontSmall = FSmall;

            Form f = new Form();
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            f.Text = AppTitle + " · 设置";
            f.ClientSize = new Size(644, 470);
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.BackColor = CPanel;
            f.ForeColor = CText;
            f.Font = fontBase;

            Label lblKey = new Label();
            lblKey.Text = "SendKey（sc3.ft07.com 登录后到 SendKey 页面复制账号主 SendKey）";
            lblKey.SetBounds(20, 16, 604, 20);
            lblKey.ForeColor = CMuted;
            lblKey.Font = fontSmall;

            Panel keyWrap = new Panel();
            keyWrap.SetBounds(20, 40, 424, 34);
            keyWrap.BackColor = CField;
            keyWrap.Paint += delegate(object s2, PaintEventArgs e2)
            {
                Graphics kg = e2.Graphics;
                using (SolidBrush pb = new SolidBrush(keyWrap.Parent != null ? keyWrap.Parent.BackColor : CPanel))
                    kg.FillRectangle(pb, keyWrap.ClientRectangle);
                kg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(new Rectangle(0, 0, keyWrap.Width - 1, keyWrap.Height - 1), 8))
                {
                    using (SolidBrush b = new SolidBrush(CField)) kg.FillPath(b, path);
                    using (Pen pen = new Pen(CBorder)) kg.DrawPath(pen, path);
                }
            };

            TextBox keyBox = new TextBox();
            keyBox.BorderStyle = BorderStyle.None;
            keyBox.BackColor = CField;
            keyBox.ForeColor = CText;
            keyBox.Font = fontBase;
            keyBox.SetBounds(12, 5, 372, 22);
            keyBox.Text = _sendKey;
            keyBox.UseSystemPasswordChar = true;
            keyBox.TabStop = false;
            keyWrap.Controls.Add(keyBox);

            EyeToggle eye = new EyeToggle();
            eye.SetBounds(keyWrap.Width - 34, 4, 28, keyWrap.Height - 8);   // 完全落在边框内侧，不覆盖圆角/描边
            eye.Click += delegate(object s2, EventArgs e2)
            {
                keyBox.UseSystemPasswordChar = !keyBox.UseSystemPasswordChar;
                eye.Revealed = !keyBox.UseSystemPasswordChar;
                eye.Invalidate();
            };
            keyWrap.Controls.Add(eye);

            Button getKey = FlatButton("打开 SendKey 页面", "\uE774", 20, 84, 194, false);

            Panel sep1 = new Panel();
            sep1.SetBounds(20, 132, 604, 1);
            sep1.BackColor = CBorder;

            CheckBox pollChk = FlatCheckBox("启用 Server酱³ 收件箱轮询", 20, 146, 260, _pollEnabled);

            CheckBox tagChk = FlatCheckBox("通知标题显示 [标签]", 300, 146, 260, _toastTag);

            Label verLabel = new Label();
            verLabel.Text = "版本 " + Version;
            verLabel.SetBounds(20, 400, 220, 18);
            verLabel.ForeColor = CDim;
            verLabel.Font = fontSmall;

            Label lblInt = new Label();
            lblInt.Text = "轮询间隔（秒）";
            lblInt.SetBounds(20, 186, 100, 22);
            lblInt.ForeColor = CMuted;
            lblInt.Font = fontSmall;

            NumberField interval = new NumberField();
            interval.SetBounds(128, 182, 100, 28);
            interval.Minimum = PollMinSeconds;
            interval.Maximum = PollMaxSeconds;
            interval.Value = Math.Min(PollMaxSeconds, Math.Max(PollMinSeconds, _pollInterval));

            string[] statusLines = new string[] { "", "", "" };
            Color[] statusColors = new Color[] { CText, CMuted, COk };

            Panel statusCard = new Panel();
            statusCard.SetBounds(20, 224, 604, 106);
            statusCard.BackColor = CPanel2;
            statusCard.Paint += delegate(object s2, PaintEventArgs e2)
            {
                using (SolidBrush pb = new SolidBrush(statusCard.Parent != null ? statusCard.Parent.BackColor : statusCard.BackColor))
                    e2.Graphics.FillRectangle(pb, statusCard.ClientRectangle);
                e2.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(new Rectangle(0, 0, statusCard.Width - 1, statusCard.Height - 1), 8))
                {
                    using (SolidBrush b = new SolidBrush(CPanel2)) e2.Graphics.FillPath(b, path);
                    using (Pen pen = new Pen(CBorder)) e2.Graphics.DrawPath(pen, path);
                }
                for (int i = 0; i < statusLines.Length; i++)
                {
                    if (string.IsNullOrEmpty(statusLines[i])) continue;
                    TextRenderer.DrawText(e2.Graphics, statusLines[i], i == 0 ? fontBase : fontSmall,
                        new Rectangle(14, 10 + i * 28, statusCard.Width - 28, 22), statusColors[i],
                        TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
            };

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 56;
            footer.BackColor = CPanel2;
            Button test = FlatButton("测试连接", "\uE945", 20, 12, 118, false);
            Button save = FlatButton("保存并同步", "\uE74E", 146, 12, 138, true);
            Button sync = FlatButton("立即同步", "\uE72C", 292, 12, 118, false);
            Button close = FlatButton("关闭", "\uE711", 418, 12, 96, false);
            footer.Controls.Add(test);
            footer.Controls.Add(save);
            footer.Controls.Add(sync);
            footer.Controls.Add(close);

            Action refreshStatus = null;
            refreshStatus = delegate
            {
                statusLines[0] = "SendKey：" + (_sendKey.Length > 0 ? "已配置（" + _sendKey.Length + " 字符）" : "未配置")
                    + "　　轮询：" + (_pollEnabled ? _pollInterval + " 秒" : "关闭")
                    + "　　标题标签前缀：" + (_toastTag ? "开" : "关");
                statusColors[0] = CText;
                try
                {
                    string json = HttpRequest("http://127.0.0.1:" + _port + "/health", "GET", null, null);
                    Dictionary<string, object> root = TryParseJson(json);
                    Dictionary<string, object> sc3 = AsMap(root != null && root.ContainsKey("sc3") ? root["sc3"] : null);
                    statusLines[1] = "守护进程：运行中　　上次同步：" + (sc3 != null ? Nz(Field(sc3, "lastSync")) : "")
                        + "　　本次已收：" + (sc3 != null ? Nz(Field(sc3, "newMessages")) : "0");
                    statusColors[1] = CMuted;
                    string err = sc3 != null ? Nz(Field(sc3, "lastError")) : "";
                    statusLines[2] = err.Length > 0 ? ("上次错误：" + err) : "状态正常";
                    statusColors[2] = err.Length > 0 ? CWarn : COk;
                }
                catch (Exception ex)
                {
                    statusLines[1] = "守护进程：未响应（" + ex.Message + "）";
                    statusColors[1] = CWarn;
                    statusLines[2] = "保存后配置仍会写入文件，下次启动生效。";
                    statusColors[2] = CMuted;
                }
                statusCard.Invalidate();
            };

            f.Controls.Add(lblKey);
            f.Controls.Add(keyWrap);
            f.Controls.Add(getKey);
            f.Controls.Add(sep1);
            f.Controls.Add(pollChk);
            f.Controls.Add(tagChk);
            f.Controls.Add(lblInt);
            f.Controls.Add(interval);
            f.Controls.Add(statusCard);
            f.Controls.Add(verLabel);
            f.Controls.Add(footer);

            getKey.Click += delegate(object s2, EventArgs e2) { try { Process.Start("https://sc3.ft07.com/sendkey"); } catch { } };

            test.Click += delegate(object s2, EventArgs e2)
            {
                string k = keyBox.Text.Trim();
                if (k.Length == 0) { statusLines[2] = "请先填入 SendKey。"; statusColors[2] = CWarn; statusCard.Invalidate(); return; }
                statusLines[2] = "正在测试连接…";
                statusColors[2] = CMuted;
                statusCard.Invalidate();
                Application.DoEvents();
                try
                {
                    string json = HttpRequest(Sc3LoginUrl, "POST", "sendkey=" + Uri.EscapeDataString(k), null);
                    Dictionary<string, object> root = TryParseJson(json);
                    string tk = Field(root, "token");
                    if (tk == null)
                    {
                        Dictionary<string, object> data = AsMap(root != null && root.ContainsKey("data") ? root["data"] : null);
                        tk = Field(data, "token");
                    }
                    if (!string.IsNullOrEmpty(tk))
                    {
                        statusLines[2] = "连接成功：已取得 token（长度 " + tk.Length + "）。点「保存并同步」生效。";
                        statusColors[2] = COk;
                    }
                    else
                    {
                        statusLines[2] = "登录被拒绝：" + (Field(root, "error") != null ? Field(root, "error") : json.Substring(0, Math.Min(140, json.Length)));
                        statusColors[2] = CErr;
                    }
                }
                catch (Exception ex)
                {
                    statusLines[2] = "测试失败：" + ex.Message;
                    statusColors[2] = CErr;
                }
                statusCard.Invalidate();
            };

            save.Click += delegate(object s2, EventArgs e2)
            {
                string k = keyBox.Text.Trim();
                interval.CommitNow();          // 先落定输入框里正在编辑的值，再读取（自定义按钮不会让输入框失焦）
                Dictionary<string, string> kv = new Dictionary<string, string>();
                kv["sendkey"] = k;
                kv["poll"] = pollChk.Checked ? "1" : "0";
                kv["poll_interval"] = ((int)interval.Value).ToString(CultureInfo.InvariantCulture);
                kv["toast_tag"] = tagChk.Checked ? "1" : "0";
                try
                {
                    SaveConfigValues(iniPath, kv);
                }
                catch (Exception ex)
                {
                    statusLines[2] = "写入配置失败：" + ex.Message;
                    statusColors[2] = CErr;
                    statusCard.Invalidate();
                    return;
                }
                string hint = (k.Length > 0 && !k.StartsWith("sctp", StringComparison.OrdinalIgnoreCase)) ? "（注意：SC3 的 SendKey 通常以 sctp 开头）" : "";
                try
                {
                    HttpRequest("http://127.0.0.1:" + _port + "/reload", "GET", null, null);
                    statusLines[2] = "已保存并已通知守护进程重载。" + hint;
                    statusColors[2] = COk;
                }
                catch (Exception ex)
                {
                    statusLines[2] = "已保存，但守护进程未响应（" + ex.Message + "），下次启动生效。" + hint;
                    statusColors[2] = CWarn;
                }
                statusCard.Invalidate();
            };

            sync.Click += delegate(object s2, EventArgs e2)
            {
                try
                {
                    string rj = HttpRequest("http://127.0.0.1:" + _port + "/sync?full=1", "GET", null, null);
                    statusLines[2] = "已重新拉取服务器历史通知：" + rj;
                    statusColors[2] = COk;
                }
                catch (Exception ex)
                {
                    statusLines[2] = "同步请求失败：" + ex.Message;
                    statusColors[2] = CErr;
                }
                statusCard.Invalidate();
            };

            close.Click += delegate(object s2, EventArgs e2) { f.Close(); };

            f.Shown += delegate { try { f.Invalidate(true); f.Update(); } catch { } };
            f.Shown += delegate { try { f.ActiveControl = null; } catch { } };
            ApplyDarkScrollbars(keyBox);
            ApplyDarkScrollbars(interval);
            refreshStatus();
            ApplyDarkTitleBar(f.Handle);
            Application.Run(f);
        }
        catch (Exception ex)
        {
            Log("settings error: " + ex.ToString());
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static void ActivateExisting(string title)
    {
        try
        {
            IntPtr h = FindWindow(null, title);
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, 9);
                SetForegroundWindow(h);
            }
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int GWL_STYLE = -16;
    private const int WS_VSCROLL = 0x00200000;
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_LINESCROLL = 0x00B6;
    private const int WM_MOUSEWHEEL = 0x020A;

    private static void HideNativeScrollbar(Control c)
    {
        try
        {
            if (!c.IsHandleCreated) return;
            int style = GetWindowLong(c.Handle, GWL_STYLE);
            if ((style & WS_VSCROLL) != 0)
            {
                SetWindowLong(c.Handle, GWL_STYLE, style & ~WS_VSCROLL);
                SetWindowPos(c.Handle, IntPtr.Zero, 0, 0, c.Width, c.Height,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                c.Invalidate();
                c.Update();
            }
        }
        catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

    private static void ApplyDarkScrollbars(Control c)
    {
        try
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            else c.HandleCreated += delegate { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static int ColorRef(Color c)
    {
        return c.R | (c.G << 8) | (c.B << 16);
    }

    private static void ApplyDarkTitleBar(IntPtr handle)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
            }
            int round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
            int caption = ColorRef(CPanel2);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int textColor = ColorRef(Color.FromArgb(0xe8, 0xea, 0xed));
            DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
            int borderColor = ColorRef(CBorder);
            DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
        }
        catch { }
    }

    private static readonly Color CBg = Color.FromArgb(0x15, 0x16, 0x1a);
    private static readonly Color CPanel = Color.FromArgb(0x1e, 0x20, 0x24);
    private static readonly Color CPanel2 = Color.FromArgb(0x1b, 0x1d, 0x21);
    private static readonly Color CField = Color.FromArgb(0x17, 0x19, 0x1d);
    private static readonly Color CBorder = Color.FromArgb(0x2c, 0x2f, 0x34);
    private static readonly Color CText = Color.FromArgb(0xe8, 0xea, 0xed);
    private static readonly Color CMuted = Color.FromArgb(0x86, 0x8c, 0x95);
    private static readonly Color CAccent = Color.FromArgb(0x7c, 0x8c, 0xf8);
    private static readonly Color CSel = Color.FromArgb(0x26, 0x2b, 0x3a);
    private static readonly Color CChipBg = Color.FromArgb(0x23, 0x2a, 0x44);
    private static readonly Color CChipLine = Color.FromArgb(0x3f, 0x4d, 0x80);
    private static readonly Color CChipText = Color.FromArgb(0xa9, 0xb8, 0xff);
    // 语义色（原先散落在各处内联，2026-10-07 收敛；同一语义只允许这一处定义）
    private static readonly Color CTextStrong = Color.FromArgb(0xdf, 0xe2, 0xe7);
    private static readonly Color CDim = Color.FromArgb(0x6b, 0x71, 0x7a);
    private static readonly Color COk = Color.FromArgb(0x6e, 0xcf, 0x8f);
    private static readonly Color CWarn = Color.FromArgb(0xe2, 0xb0, 0x4a);
    private static readonly Color CErr = Color.FromArgb(0xff, 0x8a, 0x8a);
    private static readonly Color CBorderStrong = Color.FromArgb(0x3a, 0x3e, 0x45);
    private static readonly Color CBadgeBg = Color.FromArgb(0x2a, 0x2d, 0x33);
    private static readonly Color CBadgeReadBg = Color.FromArgb(0x25, 0x28, 0x2d);
    private static readonly Color CBadgeReadLine = Color.FromArgb(0x38, 0x3c, 0x43);
    private static readonly Color CBadgeReadText = Color.FromArgb(0x8f, 0x96, 0x9f);
    private static readonly Color CAccentLine = Color.FromArgb(0x55, 0x68, 0xb8);
    private static readonly Color CChipSelBg = Color.FromArgb(0x2b, 0x33, 0x50);
    private static readonly Color CChipSelLine = Color.FromArgb(0x4a, 0x5a, 0x94);
    private static readonly Color CChipSelText = Color.FromArgb(0xb9, 0xc6, 0xff);
    private static readonly Color CChipUnselBg = Color.FromArgb(0x22, 0x25, 0x2a);
    private static readonly Color CChipUnselText = Color.FromArgb(0xa9, 0xb0, 0xb9);

    private static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
    {
        System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        if (d > r.Height) d = r.Height;
        if (d > r.Width) d = r.Width;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawChip(Graphics g, string text, int x, int y, Font font, out int usedWidth)
    {
        // 与筛选 chip 同一规则：宽度由墨迹派生（文字居中时左右内边距才对称）；高度保持原样
        int w = IconInk(text, font).Width + 18;
        int h = TextRenderer.MeasureText(text, font).Height + 4;
        Rectangle r = new Rectangle(x, y, w, h);
        using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(r, h / 2))
        {
            using (SolidBrush b = new SolidBrush(CChipBg)) g.FillPath(b, path);
            using (Pen pen = new Pen(CChipLine)) g.DrawPath(pen, path);
        }
        DrawInk(g, text, font, r, CChipText, 1);
        usedWidth = w;
    }

    // 密码框的“显示/隐藏”眼睛：只画字形，不画任何底框（灰度抗锯齿，避免 ClearType 彩边）
    // 无系统标题栏但仍可缩放/贴边（靠 WS_THICKFRAME），最大化时避开任务栏
    private sealed class SkinForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00010000;   // WS_MAXIMIZEBOX
                cp.Style |= 0x00020000;   // WS_MINIMIZEBOX
                cp.Style |= 0x00040000;   // WS_THICKFRAME
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; } catch { }
        }
    }

    // 标题栏按钮：最小化 / 最大化-还原 / 关闭（自绘，悬停高亮，关闭为红）
    private sealed class CaptionButton : Control
    {
        public int Kind = 0;            // 0=最小化 1=最大化/还原 2=关闭
        public bool Maximized = false;
        private bool _hover = false;
        private bool _down = false;
        private static Font _f = null;

        public CaptionButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Default;
        }

        private static Font Glyph()
        {
            if (_f == null) _f = FIcon;
            return _f;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : CPanel2)) e.Graphics.FillRectangle(pb, ClientRectangle);
            Color back = Color.Empty;
            if (Kind == 2 && (_hover || _down)) back = _down ? Color.FromArgb(0xa8, 0x24, 0x18) : Color.FromArgb(0xc4, 0x2b, 0x1c);
            else if (_down) back = Color.FromArgb(0x2a, 0x2e, 0x35);
            else if (_hover) back = Color.FromArgb(0x33, 0x37, 0x3e);
            if (back != Color.Empty) using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillRectangle(b, ClientRectangle);
            string g = Kind == 0 ? "\uE921" : (Kind == 1 ? (Maximized ? "\uE923" : "\uE922") : "\uE8BB");
            Font f = Glyph();
            DrawInk(e.Graphics, g, f, ClientRectangle, CTextStrong, 1);
        }
    }

    private sealed class EyeToggle : Control
    {
        public bool Revealed = false;
        private bool _hover = false;
        private static Font _f = null;

        public EyeToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        private static Font Glyph()
        {
            if (_f == null)
            {
                _f = FIconBig;
            }
            return _f;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : CField))
                e.Graphics.FillRectangle(pb, ClientRectangle);
            string g = Revealed ? "\uED1A" : "\uE7B3";
            Color c = _hover ? Color.White : Color.FromArgb(0xc2, 0xc8, 0xd1);
            Font f = Glyph();
            Rectangle ink = IconInk(g, f);
            TextRenderer.DrawText(e.Graphics, g, f,
                new Point((Width - ink.Width) / 2 - ink.X, (Height - ink.Height) / 2 - ink.Y), c,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    private sealed class NumberField : Control
    {
        public int Minimum = 1;
        public int Maximum = 9999;
        public event EventHandler ValueChanged;
        private int _value = 1;
        private string _text = "";
        private bool _editing;
        private bool _selectAll;   // 点击进入编辑时全选，输入即替换（给出可见的“选中”效果）
        private bool _focused;
        private int _hoverBtn = -1;

        public NumberField()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = FBase;
            Cursor = Cursors.IBeam;
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < Minimum) v = Minimum;
                if (v > Maximum) v = Maximum;
                bool changed = (v != _value);
                _value = v;
                _text = v.ToString(CultureInfo.InvariantCulture);
                Invalidate();
                if (changed && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        private Rectangle UpRect { get { return new Rectangle(Width - 26, 2, 22, Height / 2 - 2); } }
        private Rectangle DownRect { get { return new Rectangle(Width - 26, Height / 2, 22, Height / 2 - 2); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : CPanel)) g.FillRectangle(pb, ClientRectangle);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (SolidBrush b = new SolidBrush(CField)) g.FillPath(b, path);
                using (Pen pen = new Pen(_focused ? CAccent : CBorder)) g.DrawPath(pen, path);
            }
            string shown = _editing ? _text : _value.ToString(CultureInfo.InvariantCulture);
            Rectangle tb = new Rectangle(11, 0, Width - 44, Height);
            if (_editing && _selectAll && shown.Length > 0)
            {
                // 选中态：高亮框必须由「同一个墨迹框」派生（位置/尺寸都跟着墨迹走，不能用估算的偏移）
                Rectangle tInk = IconInk(shown, Font);
                int hx = tb.X - 3;      // 与 DrawInk(align=0) 的落位一致：墨迹左缘落在 tb.X
                int hy = tb.Y + (tb.Height - tInk.Height) / 2 - 3;
                using (System.Drawing.Drawing2D.GraphicsPath sp = RoundRect(new Rectangle(hx, hy, tInk.Width + 6, tInk.Height + 6), 4))
                using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(0x4a, 0x59, 0x9e))) g.FillPath(sb2, sp);
            }
            DrawInk(g, shown, Font, tb, CText, 0);
            if (_editing && !_selectAll)
            {
                // 光标：未全选时在文字右侧画一根竖线
                Rectangle cInk = IconInk(shown, Font);
                int cx2 = tb.X + cInk.Width + 3;
                using (Pen cp = new Pen(CText)) g.DrawLine(cp, cx2, Height / 2 - 8, cx2, Height / 2 + 8);
            }
            using (Pen pen = new Pen(CBorder)) g.DrawLine(pen, Width - 26, 5, Width - 26, Height - 6);
            Font f = FIconSmall;
            {
                DrawInk(g, "\uE70E", f, UpRect, _hoverBtn == 0 ? CText : CMuted, 1);
                DrawInk(g, "\uE70D", f, DownRect, _hoverBtn == 1 ? CText : CMuted, 1);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (UpRect.Contains(e.Location)) { Value = _value + 1; return; }
            if (DownRect.Contains(e.Location)) { Value = _value - 1; return; }
            _editing = true;
            _selectAll = true;          // 点进来先全选：直接输入即可替换，且有可见的选中高亮
            _text = _value.ToString(CultureInfo.InvariantCulture);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = UpRect.Contains(e.Location) ? 0 : (DownRect.Contains(e.Location) ? 1 : -1);
            // 光标按区域区分：箭头区用默认箭头，文本区才是输入光标（原来整控件都是 IBeam）
            Cursor want = h >= 0 ? Cursors.Default : Cursors.IBeam;
            if (Cursor != want) Cursor = want;
            if (h != _hoverBtn) { _hoverBtn = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverBtn = -1; Invalidate(); }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _focused = true; Invalidate(); }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _focused = false;
            Commit();
        }

        private void Commit()
        {
            int v;
            if (int.TryParse(_text, out v)) Value = v;
            _editing = false;
            _selectAll = false;
            _text = _value.ToString(CultureInfo.InvariantCulture);
            Invalidate();
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!_editing) { _editing = true; _text = ""; _selectAll = false; }
            if (_selectAll && (char.IsDigit(e.KeyChar) || e.KeyChar == (char)8)) { _text = ""; _selectAll = false; }
            if (e.KeyChar == (char)13) { Commit(); _selectAll = false; e.Handled = true; return; }
            if (e.KeyChar == (char)27) { _editing = false; _text = _value.ToString(CultureInfo.InvariantCulture); Invalidate(); e.Handled = true; return; }
            if (e.KeyChar == (char)8) { if (_text.Length > 0) _text = _text.Substring(0, _text.Length - 1); Invalidate(); e.Handled = true; return; }
            if (!char.IsDigit(e.KeyChar)) { e.Handled = true; return; }
            if (_text.Length >= 5) { e.Handled = true; return; }
            _text += e.KeyChar;
            ApplyText();          // 即时生效：不必等回车/失焦（自定义按钮不会让输入框失焦，否则输入会被丢掉）
            Invalidate();
        }

        // 输入框当前文本若合法则立即生效（供键盘输入与保存前调用）
        private void ApplyText()
        {
            int v;
            if (int.TryParse(_text, out v))
            {
                if (v < Minimum) v = Minimum;
                if (v > Maximum) v = Maximum;
                if (v != _value)
                {
                    _value = v;
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        // 供外部（如保存按钮）在读取 Value 之前强制落定当前输入
        public void CommitNow()
        {
            if (_editing) ApplyText();
            Commit();
        }
    }

    private sealed class CapsuleBar : Control
    {
        public Func<int> GetFirst;
        public Func<int> GetTotal;
        public Func<int> GetVisible;
        public Action<int> SetFirst;
        private bool _hover;
        private bool _drag;
        private int _grab;
        private Rectangle _thumb;
        private int _trackTop;
        private int _trackHeight;
        private int _maxFirst = 1;

        public CapsuleBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Width = 13;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _drag = true;
            if (_thumb.Contains(e.Location)) _grab = e.Y - _thumb.Y;
            else { _grab = _thumb.Height / 2; MoveThumbTo(e.Y - _grab); }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag) { MoveThumbTo(e.Y - _grab); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Invalidate(); base.OnMouseUp(e); }

        private void MoveThumbTo(int thumbY)
        {
            int span = _trackHeight - _thumb.Height;
            if (span <= 0 || _maxFirst <= 0 || SetFirst == null) return;
            int y = thumbY - _trackTop;
            if (y < 0) y = 0;
            if (y > span) y = span;
            SetFirst((int)Math.Round((double)y * _maxFirst / span));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : Color.FromArgb(0x1b, 0x1d, 0x21)))
                e.Graphics.FillRectangle(pb, ClientRectangle);
            int total = GetTotal != null ? GetTotal() : 0;
            int visible = GetVisible != null ? Math.Max(1, GetVisible()) : 1;
            if (total <= visible) { _thumb = Rectangle.Empty; return; }
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int w = 7;
            int x = (Width - w) / 2;
            _trackTop = 6;
            _trackHeight = Math.Max(24, Height - 12);
            using (System.Drawing.Drawing2D.GraphicsPath tp = RoundRect(new Rectangle(x, _trackTop, w, _trackHeight), w / 2))
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(0x14, 0x16, 0x1a)))
                e.Graphics.FillPath(tb, tp);
            int thumbH = Math.Max(34, (int)((long)_trackHeight * visible / total));
            if (thumbH > _trackHeight) thumbH = _trackHeight;
            _maxFirst = Math.Max(1, total - visible);
            int first = GetFirst != null ? GetFirst() : 0;
            if (first < 0) first = 0;
            if (first > _maxFirst) first = _maxFirst;
            int thumbY = _trackTop + (int)((long)(_trackHeight - thumbH) * first / _maxFirst);
            _thumb = new Rectangle(x, thumbY, w, thumbH);
            using (System.Drawing.Drawing2D.GraphicsPath hp = RoundRect(_thumb, w / 2))
            using (SolidBrush hb = new SolidBrush(_drag || _hover ? Color.FromArgb(0x7b, 0x84, 0x93) : Color.FromArgb(0x4d, 0x54, 0x5e)))
                e.Graphics.FillPath(hb, hp);
        }
    }

    private sealed class CustomList : Control
    {
        public int ItemHeight = 80;
        public Action<Graphics, Rectangle, int> DrawRow;
        public event EventHandler SelectedIndexChanged;
        public event EventHandler Scrolled;
        private int _count;
        private int _top;
        private int _sel = -1;

        public CustomList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int Count { get { return _count; } }
        public int VisibleCount { get { return Math.Max(1, ClientSize.Height / ItemHeight); } }

        public int TopIndex
        {
            get { return _top; }
            set
            {
                int v = value;
                int max = Math.Max(0, _count - VisibleCount);
                if (v < 0) v = 0;
                if (v > max) v = max;
                if (v != _top)
                {
                    _top = v;
                    Invalidate();
                    if (Scrolled != null) Scrolled(this, EventArgs.Empty);
                }
            }
        }

        public int SelectedIndex
        {
            get { return _sel; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= _count) v = _count - 1;
                if (v != _sel)
                {
                    _sel = v;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public void SetCount(int n)
        {
            _count = Math.Max(0, n);
            if (_sel >= _count) _sel = _count - 1;
            int max = Math.Max(0, _count - VisibleCount);
            if (_top > max) _top = max;
            Invalidate();
        }

        public void EnsureVisible(int index)
        {
            if (index < _top) TopIndex = index;
            else if (index >= _top + VisibleCount) TopIndex = index - VisibleCount + 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
            if (DrawRow == null) return;
            int visible = VisibleCount;
            for (int i = 0; i < visible; i++)
            {
                int idx = _top + i;
                if (idx >= _count) break;
                Rectangle row = new Rectangle(0, i * ItemHeight, ClientSize.Width, ItemHeight);
                bool sel = (idx == _sel);
                using (SolidBrush rb = new SolidBrush(sel ? CSel : BackColor)) e.Graphics.FillRectangle(rb, row);
                if (sel)
                {
                    using (SolidBrush bar = new SolidBrush(CAccent)) e.Graphics.FillRectangle(bar, new Rectangle(0, row.Y, 3, row.Height));
                }
                DrawRow(e.Graphics, row, idx);
            }
            int partIdx = _top + visible;
            if (partIdx < _count)
            {
                Rectangle partRow = new Rectangle(0, visible * ItemHeight, ClientSize.Width, ItemHeight);
                bool partSel = (partIdx == _sel);
                using (SolidBrush rb2 = new SolidBrush(partSel ? CSel : BackColor)) e.Graphics.FillRectangle(rb2, partRow);
                if (partSel)
                {
                    using (SolidBrush bar2 = new SolidBrush(CAccent)) e.Graphics.FillRectangle(bar2, new Rectangle(0, partRow.Y, 3, partRow.Height));
                }
                DrawRow(e.Graphics, partRow, partIdx);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int idx = _top + e.Y / ItemHeight;
            if (idx >= 0 && idx < _count) SelectedIndex = idx;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.PageUp || keyData == Keys.PageDown || keyData == Keys.Home || keyData == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_count == 0) return;
            int idx = _sel < 0 ? 0 : _sel;
            if (e.KeyCode == Keys.Up) idx--;
            else if (e.KeyCode == Keys.Down) idx++;
            else if (e.KeyCode == Keys.PageUp) idx -= VisibleCount;
            else if (e.KeyCode == Keys.PageDown) idx += VisibleCount;
            else if (e.KeyCode == Keys.Home) idx = 0;
            else if (e.KeyCode == Keys.End) idx = _count - 1;
            else return;
            if (idx < 0) idx = 0;
            if (idx > _count - 1) idx = _count - 1;
            SelectedIndex = idx;
            EnsureVisible(idx);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                TopIndex += (delta > 0 ? -3 : 3);
                return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed class WheelList : ListBox
    {
        public event EventHandler Wheeled;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                try
                {
                    int t = TopIndex + (delta > 0 ? -3 : 3);
                    if (t < 0) t = 0;
                    TopIndex = t;
                }
                catch { }
                HideNativeScrollbar(this);
                if (Wheeled != null) Wheeled(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x0115 || m.Msg == 0x0100 || m.Msg == 0x0101 || m.Msg == 0x0201 || m.Msg == 0x020A || m.Msg == 0x0114)
            {
                HideNativeScrollbar(this);
                if (Wheeled != null) Wheeled(this, EventArgs.Empty);
            }
        }
    }

    private sealed class WheelRich : RichTextBox
    {
        public event EventHandler Wheeled;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(delta > 0 ? -3 : 3));
                HideNativeScrollbar(this);
                if (Wheeled != null) Wheeled(this, EventArgs.Empty);
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x0115 || m.Msg == 0x0100 || m.Msg == 0x0101 || m.Msg == 0x0201 || m.Msg == 0x020A || m.Msg == 0x00B6)
            {
                HideNativeScrollbar(this);
                if (Wheeled != null) Wheeled(this, EventArgs.Empty);
            }
        }
    }

    private sealed class IconButton : Button
    {
        public string Glyph = "";
        public bool Primary = false;
        private bool _hover = false;
        private bool _down = false;
        private static Font _glyphFont = null;

        public IconButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
        }

        private static readonly Dictionary<string, Rectangle> _inkCache = new Dictionary<string, Rectangle>();

        private static Font GlyphFont()
        {
            if (_glyphFont == null)
            {
                _glyphFont = FIcon;
            }
            return _glyphFont;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
                e.Graphics.FillRectangle(pb, ClientRectangle);
            Color back, line, fore;
            if (Primary) { back = Color.FromArgb(0x3d, 0x4b, 0x86); line = CAccentLine; fore = Color.White; }
            else { back = Color.FromArgb(0x26, 0x29, 0x2e); line = CBorderStrong; fore = CTextStrong; }
            if (!Enabled) { back = Color.FromArgb(0x21, 0x23, 0x27); line = Color.FromArgb(0x2c, 0x2f, 0x34); fore = Color.FromArgb(0x60, 0x65, 0x6c); }
            else if (_down) back = Primary ? Color.FromArgb(0x33, 0x3f, 0x74) : Color.FromArgb(0x1f, 0x22, 0x27);
            else if (_hover) back = Primary ? Color.FromArgb(0x4a, 0x59, 0x9e) : Color.FromArgb(0x2f, 0x33, 0x3a);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(r, 6))
            {
                using (SolidBrush b = new SolidBrush(back)) e.Graphics.FillPath(b, path);
                using (Pen pen = new Pen(line)) e.Graphics.DrawPath(pen, path);
            }
            // 图标 + 文字作为「一整组」水平居中（原固定 x=11 左对齐 → 实测 dx = -2.5 ~ -11px）
            Font gf = string.IsNullOrEmpty(Glyph) ? null : GlyphFont();
            Rectangle gInk = gf != null ? IconInk(Glyph, gf) : Rectangle.Empty;
            // 度量与绘制必须同族：两者都按墨迹宽/墨迹落位（推进宽含左右边距，混用会留系统偏差）
            Rectangle tInk = string.IsNullOrEmpty(Text) ? Rectangle.Empty : IconInk(Text, Font);
            int gap = (gf != null && !string.IsNullOrEmpty(Text)) ? 4 : 0;
            int totalW = (gf != null ? gInk.Width : 0) + gap + tInk.Width;
            int cx = Math.Max(6, (Width - totalW) / 2);
            if (gf != null)
            {
                int gy = (Height - gInk.Height) / 2 - gInk.Y;
                TextRenderer.DrawText(e.Graphics, Glyph, gf, new Point(cx - gInk.X, gy), fore,
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                cx += gInk.Width + gap;
            }
            if (!string.IsNullOrEmpty(Text))
            {
                DrawInk(e.Graphics, Text, Font, new Rectangle(cx, 0, Math.Max(0, Width - cx), Height), fore, 0);
            }
        }
    }

    private sealed class FlatCheck : CheckBox
    {
        public FlatCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FlatStyle = FlatStyle.Flat;
            BackColor = CPanel;
            ForeColor = CText;
            Cursor = Cursors.Hand;
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { Invalidate(); base.OnMouseEnter(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush pb = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
                e.Graphics.FillRectangle(pb, ClientRectangle);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(1, (Height - 15) / 2, 15, 15);
            Color line = Checked ? CAccentLine : Color.FromArgb(0x4a, 0x50, 0x58);
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(box, 3))
            {
                using (SolidBrush b = new SolidBrush(Checked ? CAccentLine : CField)) e.Graphics.FillPath(b, path);
                using (Pen pen = new Pen(line)) e.Graphics.DrawPath(pen, path);
            }
            if (Checked)
            {
                using (Pen mark = new Pen(Color.White, 2f))
                {
                    mark.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    mark.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    e.Graphics.DrawLines(mark, new Point[] {
                        new Point(box.X + 3, box.Y + 8),
                        new Point(box.X + 6, box.Y + 11),
                        new Point(box.X + 12, box.Y + 4) });
                }
            }
            DrawInk(e.Graphics, Text, Font, new Rectangle(22, 0, Math.Max(0, Width - 24), Height), Enabled ? CText : CMuted, 0);
        }
    }

    private static CheckBox FlatCheckBox(string text, int x, int y, int w, bool isChecked)
    {
        FlatCheck c = new FlatCheck();
        c.Text = text;
        c.Checked = isChecked;
        c.SetBounds(x, y, w, 24);
        return c;
    }

    private static Button FlatButton(string text, string glyph, int x, int y, int w, bool primary)
    {
        IconButton b = new IconButton();
        b.Text = text;
        b.Glyph = glyph;
        b.Primary = primary;
        b.SetBounds(x, y, w, 32);
        b.ForeColor = primary ? Color.White : CTextStrong;
        b.BackColor = CPanel;
        return b;
    }

    private static readonly Regex RxLeadTime = new Regex("^\\s*\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}(:\\d{2})?\\s*", RegexOptions.Compiled);
    private static readonly Regex RxNoise = new Regex("[-=_*#`~]{2,}", RegexOptions.Compiled);
    private static readonly Regex RxSpace = new Regex("\\s+", RegexOptions.Compiled);

    private static string DisplayText(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        StringBuilder sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            int cp = c;
            if (char.IsHighSurrogate(c) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
            {
                cp = char.ConvertToUtf32(c, body[i + 1]);
                i++;
            }
            switch (cp)
            {
                case 0x2705:
                case 0x2714:
                    sb.Append((char)0x2713);
                    break;
                case 0x274C:
                case 0x2718:
                    sb.Append((char)0x2717);
                    break;
                case 0x2B50:
                    sb.Append((char)0x2605);
                    break;
                case 0xFE0F:
                case 0x200B:
                case 0x200C:
                case 0x200D:
                case 0xFEFF:
                    break;
                default:
                    if (cp >= 0x1F000 && cp <= 0x1FAFF) sb.Append((char)0x25A1);
                    else if (cp >= 0x1F1E6 && cp <= 0x1F1FF) sb.Append((char)0x25A1);
                    else sb.Append(char.ConvertFromUtf32(cp));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string PreviewText(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        string t = DisplayText(body).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        t = RxLeadTime.Replace(t, "");
        t = t.Replace("#", " ").Replace("*", " ").Replace("`", " ");
        t = RxNoise.Replace(t, " ");
        t = RxSpace.Replace(t, " ").Trim();
        if (t.Length > 160) t = t.Substring(0, 160);
        return t;
    }

    private static int DrawBadge(Graphics g, string text, int x, int y, Font font, Color bg, Color line, Color fgColor)
    {
        int w = TextAdvance(text, font) + 16;      // GDI 推进宽（与绘制同族），不用 GDI+ MeasureString
        int h = BadgeHeight;                        // 徽标高度唯一来源：时间行也用它
        Rectangle r = new Rectangle(x, y, w, h);
        using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(r, (int)Math.Round(h / 2.0)))
        {
            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, path);
            using (Pen pen = new Pen(line)) g.DrawPath(pen, path);
        }
        DrawInk(g, text, font, r, fgColor, 1);
        return w;
    }

    private static void RunViewer(string uri)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string wantId = QueryValue(uri.Contains("?") ? uri.Substring(uri.IndexOf('?') + 1) : "", "id");
            List<Notice> all = LoadStore(500);
            all.Sort(delegate(Notice x, Notice y) { return string.CompareOrdinal(y.Time, x.Time); });

            HashSet<string> readIds = new HashSet<string>();
            try
            {
                if (File.Exists(_readPath))
                {
                    foreach (string rl in File.ReadAllLines(_readPath))
                    {
                        string rt = rl.Trim();
                        if (rt.Length > 0) readIds.Add(rt);
                    }
                }
            }
            catch { }

            Font fontBase, fontSmall, fontChip, fontTitle, fontHead;
            fontBase = FBase;
            fontSmall = FSmall;
            fontChip = FChip;
            Font fontTag;
            fontTag = FTag;
            fontTitle = FTitle;
            fontHead = FHead;

            SkinForm form = new SkinForm();
            form.FormBorderStyle = FormBorderStyle.None;
            try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            form.Text = AppTitle;
            form.ClientSize = new Size(1000, 600);
            form.MinimumSize = new Size(720, 460);
            form.StartPosition = FormStartPosition.CenterScreen;
            form.BackColor = CPanel;
            form.ForeColor = CText;
            form.Font = fontBase;

            Panel main = new Panel();
            main.Dock = DockStyle.Fill;
            main.BackColor = CPanel;

            Panel sidebar = new Panel();
            sidebar.Dock = DockStyle.Left;
            sidebar.Width = 320;
            sidebar.BackColor = CPanel2;

            form.Controls.Add(main);
            form.Controls.Add(sidebar);

            // ── 自绘标题栏（无边框窗口）──
            Panel titleBar = new Panel();
            titleBar.Dock = DockStyle.Top;
            titleBar.Height = 38;
            titleBar.BackColor = CPanel2;

            Label titleLbl = new Label();
            titleLbl.Text = AppTitle;
            titleLbl.SetBounds(14, 10, 420, 20);
            titleLbl.ForeColor = CText;
            titleLbl.Font = fontBase;
            titleLbl.BackColor = CPanel2;

            CaptionButton btnMin = new CaptionButton();
            btnMin.Kind = 0;
            btnMin.SetBounds(1000 - 138, 0, 46, 38);
            CaptionButton btnMax = new CaptionButton();
            btnMax.Kind = 1;
            btnMax.SetBounds(1000 - 92, 0, 46, 38);
            CaptionButton btnClose = new CaptionButton();
            btnClose.Kind = 2;
            btnClose.SetBounds(1000 - 46, 0, 46, 38);

            EventHandler placeCaption = delegate
            {
                int right = titleBar.ClientSize.Width;
                btnClose.Left = right - 46;
                btnMax.Left = right - 92;
                btnMin.Left = right - 138;
            };
            titleBar.Resize += delegate { placeCaption(null, EventArgs.Empty); };
            titleBar.Controls.Add(titleLbl);
            titleBar.Controls.Add(btnMin);
            titleBar.Controls.Add(btnMax);
            titleBar.Controls.Add(btnClose);
            placeCaption(null, EventArgs.Empty);

            btnMin.Click += delegate(object s2, EventArgs e2) { form.WindowState = FormWindowState.Minimized; };
            btnMax.Click += delegate(object s2, EventArgs e2)
            {
                form.WindowState = (form.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                btnMax.Maximized = (form.WindowState == FormWindowState.Maximized);
                btnMax.Invalidate();
            };
            btnClose.Click += delegate(object s2, EventArgs e2) { form.Close(); };

            MouseEventHandler dragMove = delegate(object s2, MouseEventArgs e2)
            {
                if (e2.Button != MouseButtons.Left) return;
                try
                {
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
                catch { }
            };
            EventHandler toggleMax = delegate(object s2, EventArgs e2)
            {
                form.WindowState = (form.WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized;
                btnMax.Maximized = (form.WindowState == FormWindowState.Maximized);
                btnMax.Invalidate();
            };
            titleBar.MouseDown += dragMove;
            titleBar.DoubleClick += toggleMax;
            titleLbl.MouseDown += dragMove;
            titleLbl.DoubleClick += toggleMax;

            form.Controls.Add(titleBar);
            _keepAlive.Add(titleBar);

            Panel catHead = new Panel();
            catHead.Dock = DockStyle.Top;
            catHead.Height = 36;
            catHead.BackColor = CPanel2;

            Label catTitle = new Label();
            catTitle.Text = "标签分类";
            catTitle.SetBounds(12, 12, 150, 18);
            catTitle.ForeColor = CMuted;
            catTitle.Font = fontSmall;

            Button markAllBtn = FlatButton("", "\uE8FB", 222, 5, 28, false);
            markAllBtn.Height = 26;
            Button cleanBtn = FlatButton("", "\uE74D", 252, 5, 28, false);
            cleanBtn.Height = 26;
            // E115 Settings：与 E713 同尺寸(14x14)，但齿形抗锯齿落点更对称
            Button settingsBtn = FlatButton("", "\uE115", 282, 5, 28, true);
            settingsBtn.Height = 26;

            ToolTip tips = new ToolTip();
            tips.SetToolTip(markAllBtn, "全部标记为已读");
            tips.SetToolTip(cleanBtn, "清理通知");
            tips.SetToolTip(settingsBtn, "设置 SendKey / 轮询");

            catHead.Paint += delegate(object sender2, PaintEventArgs e2)
            {
                using (Pen sep = new Pen(CBorder)) e2.Graphics.DrawLine(sep, 0, catHead.Height - 1, catHead.Width, catHead.Height - 1);
            };
            catHead.Controls.Add(catTitle);
            catHead.Controls.Add(markAllBtn);
            catHead.Controls.Add(cleanBtn);
            catHead.Controls.Add(settingsBtn);

            Panel tagPanel = new Panel();
            tagPanel.Dock = DockStyle.Top;
            tagPanel.Height = 32;
            tagPanel.BackColor = Color.FromArgb(0x16, 0x18, 0x1c);

            CustomList list = new CustomList();
            list.Dock = DockStyle.Fill;
            list.BackColor = CPanel2;
            list.ForeColor = CText;
            list.ItemHeight = 80;
            list.Font = fontBase;

            Panel listHead = new Panel();
            listHead.Dock = DockStyle.Top;
            listHead.Height = 30;
            listHead.BackColor = CPanel2;

            Panel listWrap = new Panel();
            listWrap.Dock = DockStyle.Fill;
            listWrap.BackColor = CPanel2;

            CapsuleBar listBar = new CapsuleBar();
            listBar.Dock = DockStyle.Right;
            listBar.BackColor = CPanel2;
            listBar.GetTotal = delegate { return list.Count; };
            listBar.GetVisible = delegate { return list.VisibleCount; };
            listBar.GetFirst = delegate { return list.TopIndex; };
            listBar.SetFirst = delegate(int v) { try { list.TopIndex = v; } catch { } };

            listWrap.Controls.Add(list);
            listWrap.Controls.Add(listBar);

            sidebar.Controls.Add(listWrap);
            sidebar.Controls.Add(listHead);
            sidebar.Controls.Add(tagPanel);
            sidebar.Controls.Add(catHead);

            Label titleLabel = new Label();
            titleLabel.Dock = DockStyle.Top;
            titleLabel.Height = 44;
            titleLabel.Padding = new Padding(20, 16, 20, 0);
            titleLabel.Font = fontTitle;
            titleLabel.ForeColor = Color.White;
            titleLabel.AutoEllipsis = true;

            Panel metaPanel = new Panel();
            metaPanel.Dock = DockStyle.Top;
            metaPanel.Height = 30;
            metaPanel.BackColor = CPanel;

            WheelRich bodyBox = new WheelRich();
            bodyBox.Dock = DockStyle.Fill;
            bodyBox.BorderStyle = BorderStyle.None;
            bodyBox.BackColor = CPanel;
            bodyBox.ForeColor = Color.FromArgb(0xd7, 0xda, 0xe0);
            bodyBox.ReadOnly = true;
            bodyBox.WordWrap = true;
            bodyBox.DetectUrls = true;
            bodyBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            bodyBox.Font = fontBase;

            Panel bodyPad = new Panel();
            bodyPad.Dock = DockStyle.Fill;
            bodyPad.Padding = new Padding(20, 8, 16, 8);
            bodyPad.BackColor = CPanel;
            CapsuleBar bodyBar = new CapsuleBar();
            bodyBar.Dock = DockStyle.Right;
            bodyBar.BackColor = CPanel;
            bodyBar.GetTotal = delegate { return bodyBox.GetLineFromCharIndex(bodyBox.TextLength) + 1; };
            bodyBar.GetVisible = delegate { return Math.Max(1, bodyBox.ClientSize.Height / Math.Max(1, bodyBox.Font.Height)); };
            bodyBar.GetFirst = delegate { return (int)SendMessage(bodyBox.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero); };
            bodyBar.SetFirst = delegate(int v)
            {
                try
                {
                    int cur = (int)SendMessage(bodyBox.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(bodyBox.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(v - cur));
                }
                catch { }
            };

            bodyPad.Controls.Add(bodyBox);
            bodyPad.Controls.Add(bodyBar);
            bodyBox.HandleCreated += delegate { HideNativeScrollbar(bodyBox); };
            list.Scrolled += delegate { listBar.Invalidate(); };
            bodyBox.Wheeled += delegate { bodyBar.Invalidate(); };
            System.Windows.Forms.Timer barTimer = new System.Windows.Forms.Timer();
            barTimer.Interval = 80;
            barTimer.Tick += delegate { HideNativeScrollbar(bodyBox); listBar.Invalidate(); bodyBar.Invalidate(); };
            _keepAlive.Add(barTimer);
            barTimer.Start();

            Label hintLabel = new Label();
            hintLabel.Dock = DockStyle.Fill;
            hintLabel.TextAlign = ContentAlignment.MiddleCenter;
            hintLabel.ForeColor = CMuted;
            hintLabel.Text = "该分类下没有通知";
            hintLabel.Visible = false;
            bodyPad.Controls.Add(hintLabel);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 52;
            footer.BackColor = CPanel2;

            Button markBtn = FlatButton("标记未读", "\uE8FB", 0, 10, 110, false);
            Button closeBtn = FlatButton("关闭", "\uE711", 0, 10, 88, false);
            footer.Controls.Add(markBtn);
            footer.Controls.Add(closeBtn);

            EventHandler placeFooter = delegate
            {
                int right = footer.ClientSize.Width - 12;
                closeBtn.Left = Math.Max(0, right - closeBtn.Width);
                markBtn.Left = Math.Max(0, closeBtn.Left - 8 - markBtn.Width);
            };
            footer.Resize += delegate { placeFooter(null, EventArgs.Empty); };
            placeFooter(null, EventArgs.Empty);

            main.Controls.Add(bodyPad);
            main.Controls.Add(footer);
            main.Controls.Add(metaPanel);
            main.Controls.Add(titleLabel);

            List<Notice> view = new List<Notice>();
            string listHeadText = "";
            List<string> chipLabels = new List<string>();
            List<string> chipFilters = new List<string>();
            List<Rectangle> chipRects = new List<Rectangle>();
            int chipSel = 0;
            string chipFilter = null;
            string currentUrl = "";
            Notice current = null;
            bool loading = false;

            Action relayoutChips = null;
            Action rebuildChips = null;
            Action refreshList = null;
            Action showCurrent = null;

            relayoutChips = delegate
            {
                using (Graphics g = tagPanel.CreateGraphics())
                {
                    int padX = 12, padY = 5, gap = 6, x = padX, y = padY, rows = 1;
                    chipRects.Clear();
                    for (int i = 0; i < chipLabels.Count; i++)
                    {
                        // 宽度用「墨迹宽 + 对称内边距」——与文字的居中落位同一度量族
                        int chipW = IconInk(chipLabels[i], fontChip).Width;
                        int w = chipW + 22;
                        int h = 21;
                        if (x + w > tagPanel.Width - padX && x > padX)
                        {
                            x = padX;
                            y += h + gap;
                            rows++;
                        }
                        chipRects.Add(new Rectangle(x, y, w, h));
                        x += w + gap;
                    }
                    int need = padY * 2 + rows * 21 + (rows - 1) * gap;
                    if (tagPanel.Height != need) tagPanel.Height = need;
                }
                tagPanel.Invalidate();
            };

            rebuildChips = delegate
            {
                Dictionary<string, int> counts = new Dictionary<string, int>();
                int untagged = 0;
                foreach (Notice n in all)
                {
                    if (n.Tags.Count == 0) { untagged++; continue; }
                    foreach (string t in n.Tags)
                    {
                        if (counts.ContainsKey(t)) counts[t] = counts[t] + 1;
                        else counts.Add(t, 1);
                    }
                }
                List<string> tags = new List<string>(counts.Keys);
                tags.Sort(StringComparer.OrdinalIgnoreCase);
                chipLabels.Clear();
                chipFilters.Clear();
                chipLabels.Add("全部 (" + all.Count.ToString(CultureInfo.InvariantCulture) + ")");
                chipFilters.Add(null);
                int unreadCount = 0;
                foreach (Notice n2 in all) if (!readIds.Contains(n2.Id)) unreadCount++;
                chipLabels.Add("未读 (" + unreadCount.ToString(CultureInfo.InvariantCulture) + ")");
                chipFilters.Add("\u0001unread");
                if (untagged > 0)
                {
                    chipLabels.Add("未分类 (" + untagged.ToString(CultureInfo.InvariantCulture) + ")");
                    chipFilters.Add("");
                }
                foreach (string t in tags)
                {
                    chipLabels.Add("#" + t + " (" + counts[t].ToString(CultureInfo.InvariantCulture) + ")");
                    chipFilters.Add(t);
                }
                if (chipSel >= chipLabels.Count) chipSel = 0;
                chipFilter = chipFilters[chipSel];
                relayoutChips();
            };

            refreshList = delegate
            {
                view.Clear();
                foreach (Notice n in all)
                {
                    bool ok;
                    if (chipFilter == null) ok = true;
                    else if (chipFilter == "\u0001unread") ok = !readIds.Contains(n.Id);
                    else if (chipFilter.Length == 0) ok = (n.Tags.Count == 0);
                    else ok = n.Tags.Contains(chipFilter);
                    if (ok) view.Add(n);
                }
                string keepId = current != null ? current.Id : null;
                loading = true;
                list.SetCount(view.Count);
                int sel = 0;
                if (keepId != null)
                {
                    for (int i = 0; i < view.Count; i++) if (view[i].Id == keepId) { sel = i; break; }
                }
                if (view.Count > 0)
                {
                    list.SelectedIndex = sel;
                    try
                    {
                        int ih = Math.Max(1, list.ItemHeight);
                        int visible = Math.Max(1, list.ClientSize.Height / ih);
                        int top = sel - visible + 2;
                        if (top < 0) top = 0;
                        if (top > view.Count - visible) top = Math.Max(0, view.Count - visible);
                        list.TopIndex = top;
                    }
                    catch { }
                }
                loading = false;
                listHeadText = view.Count.ToString(CultureInfo.InvariantCulture) + " 条";
                listHead.Invalidate();
                showCurrent();
            };

            showCurrent = delegate
            {
                int idx = list.SelectedIndex;
                if (idx < 0 || idx >= view.Count)
                {
                    titleLabel.Text = view.Count == 0 ? "该分类下没有通知" : "请选择一条通知";
                    hintLabel.Text = view.Count == 0 ? "该分类下没有通知\n换个标签看看，或等待新的推送" : "点击左侧列表查看详情";
                    hintLabel.Visible = true;
                    bodyBox.Visible = false;
                    metaPanel.Invalidate();
                    bodyBox.Text = "";
                    currentUrl = "";
                    current = null;
                    return;
                }
                Notice n = view[idx];
                current = n;
                hintLabel.Visible = false;
                bodyBox.Visible = true;
                titleLabel.Text = n.Title;
                bodyBox.Text = DisplayText(n.Body).Replace("\n", Environment.NewLine);
                HideNativeScrollbar(bodyBox);
                currentUrl = n.Url == null ? "" : n.Url;
                markBtn.Text = readIds.Contains(n.Id) ? "标记未读" : "标记已读";
                markBtn.Invalidate();
                metaPanel.Invalidate();
            };

            metaPanel.Paint += delegate(object sender2, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush pb = new SolidBrush(metaPanel.BackColor)) e.Graphics.FillRectangle(pb, metaPanel.ClientRectangle);
                Notice n = current;
                int x = 20;
                int y = 4;
                string time = n != null ? n.Time : "";
                DrawInk(e.Graphics, time, fontSmall, new Rectangle(x, y, TextAdvance(time, fontSmall) + 6, BadgeHeight), CMuted, 0);
                x += TextAdvance(time, fontSmall) + 10;
                if (n != null)
                {
                    bool unreadMeta = !readIds.Contains(n.Id);
                    x += DrawBadge(e.Graphics, unreadMeta ? "未读" : "已读", x, y, fontSmall,
                        unreadMeta ? CChipBg : CBadgeReadBg,
                        unreadMeta ? CChipLine : CBadgeReadLine,
                        unreadMeta ? CChipText : CBadgeReadText) + 6;
                    if (n.Src.StartsWith("sc3")) x += DrawBadge(e.Graphics, "Server酱³", x, y, fontSmall, CBadgeBg, CBorderStrong, CChipUnselText) + 6;
                    else if (n.Src.Length > 0) x += DrawBadge(e.Graphics, "本地投递", x, y, fontSmall, CBadgeBg, CBorderStrong, CChipUnselText) + 6;
                    foreach (string t in n.Tags)
                    {
                        x += DrawBadge(e.Graphics, "#" + t, x, y, fontSmall, CChipBg, CChipLine, CChipText) + 6;
                    }
                }
            };

            listHead.Paint += delegate(object sender2, PaintEventArgs e)
            {
                using (SolidBrush pb = new SolidBrush(listHead.BackColor)) e.Graphics.FillRectangle(pb, listHead.ClientRectangle);
                DrawInk(e.Graphics, "通知", fontSmall, new Rectangle(12, 0, 80, listHead.Height), CMuted, 0);
                DrawInk(e.Graphics, listHeadText, fontSmall, new Rectangle(listHead.Width - 92, 0, 80, listHead.Height), CDim, 2);
                using (Pen sep = new Pen(CBorder)) e.Graphics.DrawLine(sep, 0, listHead.Height - 1, listHead.Width, listHead.Height - 1);
            };

            tagPanel.Paint += delegate(object sender2, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush pb = new SolidBrush(tagPanel.BackColor)) e.Graphics.FillRectangle(pb, tagPanel.ClientRectangle);
                for (int i = 0; i < chipRects.Count && i < chipLabels.Count; i++)
                {
                    bool sel = (i == chipSel);
                    Rectangle r = chipRects[i];
                    using (System.Drawing.Drawing2D.GraphicsPath path = RoundRect(r, 10))
                    {
                        using (SolidBrush b = new SolidBrush(sel ? CChipSelBg : CChipUnselBg)) e.Graphics.FillPath(b, path);
                        using (Pen pen = new Pen(sel ? CChipSelLine : CBorderStrong)) e.Graphics.DrawPath(pen, path);
                    }
                    DrawInk(e.Graphics, chipLabels[i], fontChip, r,
                        sel ? CChipSelText : CChipUnselText, 1);   // 水平居中（原来左对齐 + 手写 9/-12 内缩）
                }
                using (Pen sep = new Pen(Color.FromArgb(0x3a, 0x3f, 0x47))) e.Graphics.DrawLine(sep, 0, tagPanel.Height - 1, tagPanel.Width, tagPanel.Height - 1);
            };

            tagPanel.MouseClick += delegate(object sender2, MouseEventArgs e2)
            {
                for (int i = 0; i < chipRects.Count; i++)
                {
                    if (chipRects[i].Contains(e2.Location))
                    {
                        chipSel = i;
                        chipFilter = chipFilters[i];
                        relayoutChips();
                        refreshList();
                        break;
                    }
                }
            };

            tagPanel.Resize += delegate(object sender2, EventArgs e2) { relayoutChips(); };

            list.DrawRow = delegate(Graphics g, Rectangle r, int index)
            {
                if (index < 0 || index >= view.Count) return;
                Notice n = view[index];
                bool sel = (index == list.SelectedIndex);
                using (Pen line = new Pen(Color.FromArgb(0x23, 0x26, 0x2b))) g.DrawLine(line, r.X + 8, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
                bool unread = !readIds.Contains(n.Id);
                if (unread)
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (SolidBrush dot = new SolidBrush(CAccent)) g.FillEllipse(dot, r.X + 6, r.Y + 12, 6, 6);
                }
                Rectangle titleRect = new Rectangle(r.X + 16, r.Y + 7, r.Width - 32, 20);
                TextRenderer.DrawText(g, n.Title, list.Font, titleRect,
                    unread ? Color.White : Color.FromArgb(0x86, 0x8c, 0x95),
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

                string prevText = PreviewText(n.Body);
                if (prevText.Length > 0)
                {
                    Rectangle prevRect = new Rectangle(r.X + 16, r.Y + 29, r.Width - 32, 19);
                    TextRenderer.DrawText(g, prevText, fontSmall, prevRect,
                        sel ? Color.FromArgb(0x8b, 0x92, 0x9c) : Color.FromArgb(0x63, 0x69, 0x72),
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }

                int cx = r.X + 16;
                int shown = 0;
                foreach (string t in n.Tags)
                {
                    if (shown >= 3) break;
                    int used;
                    DrawChip(g, "#" + t, cx, r.Y + 51, fontTag, out used);
                    cx += used + 5;
                    shown++;
                }
                if (n.Tags.Count > 3)
                {
                    Size moreSize = TextRenderer.MeasureText("+" + (n.Tags.Count - 3).ToString(CultureInfo.InvariantCulture), fontTag, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, "+" + (n.Tags.Count - 3).ToString(CultureInfo.InvariantCulture), fontTag,
                        new Rectangle(cx + 2, r.Y + 52, moreSize.Width + 4, moreSize.Height), CMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    cx += moreSize.Width + 8;
                }

                string timeShort = n.Time.Length >= 16 ? n.Time.Substring(0, 16) : n.Time;
                int timeInkW = IconInk(timeShort, fontSmall).Width;
                int timeRight = r.X + r.Width - 14;
                if (timeRight - timeInkW > cx + 8)
                {
                    DrawInk(g, timeShort, fontSmall, new Rectangle(r.X, r.Y + 48, r.Width - 14, 20), CDim, 2);
                }            };

            Action persistRead = delegate
            {
                try
                {
                    List<string> rl2 = new List<string>(readIds);
                    int st = rl2.Count > 3000 ? rl2.Count - 3000 : 0;
                    StringBuilder sb2 = new StringBuilder();
                    for (int i = st; i < rl2.Count; i++) sb2.Append(rl2[i]).Append(Environment.NewLine);
                    File.WriteAllText(_readPath, sb2.ToString(), new UTF8Encoding(false));
                }
                catch { }
                ClearToastsIfAllRead();   // 标记已读后：若已无未读，顺手清掉操作中心残留
            };
            Action<string> markReadById = delegate(string rid)
            {
                try
                {
                    if (string.IsNullOrEmpty(rid) || readIds.Contains(rid)) return;
                    readIds.Add(rid);
                    persistRead();
                    markBtn.Text = "标记未读";
                    list.Invalidate();
                    metaPanel.Invalidate();
                    rebuildChips();
                }
                catch { }
            };

            list.SelectedIndexChanged += delegate(object sender2, EventArgs e2)
            {
                if (loading) return;
                showCurrent();
                if (current != null && !readIds.Contains(current.Id))
                {
                    readIds.Add(current.Id);
                    persistRead();
                    markBtn.Text = "标记未读";
                    list.Invalidate();
                    metaPanel.Invalidate();
                }
            };
            markBtn.Click += delegate(object sender2, EventArgs e2)
            {
                if (current == null) return;
                if (readIds.Contains(current.Id)) readIds.Remove(current.Id);
                else readIds.Add(current.Id);
                persistRead();
                markBtn.Text = readIds.Contains(current.Id) ? "标记未读" : "标记已读";
                markBtn.Invalidate();
                list.Invalidate();
                metaPanel.Invalidate();
                int ui = chipFilters.IndexOf("\u0001unread");
                if (ui >= 0)
                {
                    int uc = 0;
                    foreach (Notice n3 in all) if (!readIds.Contains(n3.Id)) uc++;
                    chipLabels[ui] = "未读 (" + uc.ToString(CultureInfo.InvariantCulture) + ")";
                    relayoutChips();
                }
            };
            markAllBtn.Click += delegate(object sender2, EventArgs e2)
            {
                int added = 0;
                foreach (Notice n8 in all)
                {
                    if (!readIds.Contains(n8.Id)) { readIds.Add(n8.Id); added++; }
                }
                persistRead();
                ClearWindowsToasts();    // 方案 A：全部已读时清空操作中心
                list.Invalidate();
                metaPanel.Invalidate();
                int ui2 = chipFilters.IndexOf("\u0001unread");
                if (ui2 >= 0) { chipLabels[ui2] = "未读 (0)"; relayoutChips(); }
                if (current != null) markBtn.Text = "标记未读";
                markBtn.Invalidate();
                Log("mark all read: +" + added);
            };
            cleanBtn.Click += delegate(object sender2, EventArgs e2)
            {
                int rc = 0;
                foreach (Notice n4 in all) if (readIds.Contains(n4.Id)) rc++;
                string act = ShowCleanupDialog(form, all.Count, rc);
                if (act == null) return;
                if (act == "one")
                {
                    if (current == null) return;
                    string delId = current.Id;
                    all.RemoveAll(delegate(Notice n5) { return n5.Id == delId; });
                }
                else if (act == "read")
                {
                    all.RemoveAll(delegate(Notice n6) { return readIds.Contains(n6.Id); });
                }
                else if (act == "all")
                {
                    all.Clear();
                }
                WriteStore(all);
                HashSet<string> keepRead = new HashSet<string>();
                foreach (Notice n7 in all) if (readIds.Contains(n7.Id)) keepRead.Add(n7.Id);
                readIds.Clear();
                foreach (string k7 in keepRead) readIds.Add(k7);
                persistRead();
                current = null;
                rebuildChips();
                refreshList();
                ClearWindowsToasts();    // 方案 A：清理时清空操作中心
                Log("cleanup: " + act + " -> " + all.Count + " left");
            };
            settingsBtn.Click += delegate(object sender2, EventArgs e2) { OpenSettings(); };
            closeBtn.Click += delegate(object sender2, EventArgs e2) { form.Close(); };

            string navPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NavFile);
            System.Windows.Forms.Timer navTimer = new System.Windows.Forms.Timer();
            navTimer.Interval = 700;
            string lastNav = "";
            navTimer.Tick += delegate
            {
                try
                {
                    if (!File.Exists(navPath)) return;
                    string req = File.ReadAllText(navPath, Encoding.UTF8).Trim();
                    File.Delete(navPath);
                    if (req.Length == 0 || req == lastNav) return;
                    lastNav = req;
                    int pos = -1;
                    for (int i = 0; i < all.Count; i++) if (all[i].Id == req) { pos = i; break; }
                    if (pos < 0) return;
                    if (all[pos].Tags.Count > 0)
                    {
                        int ci = chipFilters.IndexOf(all[pos].Tags[0]);
                        if (ci > 0) { chipSel = ci; chipFilter = chipFilters[ci]; relayoutChips(); refreshList(); }
                    }
                    for (int i = 0; i < view.Count; i++)
                    {
                        if (view[i].Id == req)
                        {
                            list.SelectedIndex = i;
                            try
                            {
                                int ih2 = Math.Max(1, list.ItemHeight);
                                int vis2 = Math.Max(1, list.ClientSize.Height / ih2);
                                int top2 = i - vis2 + 2;
                                if (top2 < 0) top2 = 0;
                                if (top2 > view.Count - vis2) top2 = Math.Max(0, view.Count - vis2);
                                list.TopIndex = top2;
                            }
                            catch { }
                            showCurrent();
                            markReadById(req);
                            break;
                        }
                    }
                }
                catch { }
            };
            _keepAlive.Add(navTimer);
            navTimer.Start();
            form.Shown += delegate
            {
                try
                {
                    Log("viewer shown: client=" + form.ClientSize.Width + "x" + form.ClientSize.Height + " dpi=" + form.DeviceDpi + " list=" + list.ClientSize.Width + "x" + list.ClientSize.Height + " itemH=" + list.ItemHeight + " tags=" + tagPanel.Height + " head=" + catHead.Height);
                    form.ClientSize = new Size(1000, 600);
                    form.Invalidate(true);
                    form.Update();
                }
                catch { }
            };
            ApplyDarkScrollbars(list);
            ApplyDarkScrollbars(bodyBox);
            rebuildChips();
            refreshList();
            if (wantId != null && wantId.Length > 0)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Id == wantId && all[i].Tags.Count > 0)
                    {
                        int pos = chipFilters.IndexOf(all[i].Tags[0]);
                        if (pos > 0) { chipSel = pos; chipFilter = chipFilters[pos]; relayoutChips(); refreshList(); }
                        break;
                    }
                }
                for (int i = 0; i < view.Count; i++)
                {
                    if (view[i].Id == wantId) { list.SelectedIndex = i; showCurrent(); markReadById(wantId); break; }
                }
            }

            ApplyDarkTitleBar(form.Handle);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log("viewer error: " + ex.ToString());
        }
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) return i;
        }
        return -1;
    }

    private static bool ReadRequest(NetworkStream stream, out string method, out string target, out string headers, out string body)
    {
        method = "";
        target = "";
        headers = "";
        body = "";
        MemoryStream ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int headerEnd = -1;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (stream.DataAvailable)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                headerEnd = FindHeaderEnd(ms.GetBuffer(), (int)ms.Length);
                if (headerEnd >= 0) break;
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        if (headerEnd < 0) return false;

        byte[] data = ms.ToArray();
        headers = Encoding.UTF8.GetString(data, 0, headerEnd);
        string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return false;
        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2) return false;
        method = parts[0].ToUpperInvariant();
        target = parts[1];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            if (lines[i].Substring(0, c).Trim().ToLowerInvariant() == "content-length")
            {
                int.TryParse(lines[i].Substring(c + 1).Trim(), out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        MemoryStream bodyMs = new MemoryStream();
        if (data.Length > bodyStart) bodyMs.Write(data, bodyStart, data.Length - bodyStart);
        while (bodyMs.Length < contentLength && DateTime.UtcNow < deadline)
        {
            if (stream.DataAvailable)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                bodyMs.Write(buf, 0, n);
            }
            else
            {
                Thread.Sleep(5);
            }
        }
        body = Encoding.UTF8.GetString(bodyMs.ToArray());
        return true;
    }

    private static bool TokenOk(string headers, string query)
    {
        foreach (string line in headers.Split(new string[] { "\r\n" }, StringSplitOptions.None))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            if (line.Substring(0, c).Trim().ToLowerInvariant() == "x-token")
            {
                if (line.Substring(c + 1).Trim() == _token) return true;
            }
        }
        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair.Substring(0, eq) == "token" && Uri.UnescapeDataString(pair.Substring(eq + 1)) == _token) return true;
        }
        return false;
    }

    private static Dictionary<string, object> TryParseJson(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        string t = body.TrimStart();
        if (t.Length == 0 || t[0] != '{') return null;
        try
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            return ser.Deserialize<Dictionary<string, object>>(body);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        Dictionary<string, string> map = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(body)) return map;
        foreach (string pair in body.Split('&'))
        {
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string k = eq < 0 ? pair : pair.Substring(0, eq);
            string v = eq < 0 ? "" : pair.Substring(eq + 1);
            map[Uri.UnescapeDataString(k.Replace("+", " "))] = Uri.UnescapeDataString(v.Replace("+", " "));
        }
        return map;
    }

    private static string Field(Dictionary<string, object> map, string key)
    {
        object v;
        if (map != null && map.TryGetValue(key, out v) && v != null)
        {
            string s = Convert.ToString(v);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private static string XmlEscape(string s)
    {
        if (s == null) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    private static void ShowToast(string id, string title, string text, string url, List<string> tags)
    {
        if (_toastTag && tags != null && tags.Count > 0) title = "[" + tags[0] + "] " + title;
        string launch = "winpush://show?id=" + id;
        if (_clickMode == "url" && !string.IsNullOrEmpty(url)) launch = url;

        StringBuilder sb = new StringBuilder();
        sb.Append("<toast activationType=\"protocol\" launch=\"").Append(XmlEscape(launch)).Append("\">");
        sb.Append("<visual><binding template=\"ToastGeneric\"><text>");
        sb.Append(XmlEscape(title));
        sb.Append("</text><text>");
        sb.Append(XmlEscape(Shorten(text, 200)));
        sb.Append("</text></binding></visual></toast>");

        XmlDocument doc = new XmlDocument();
        doc.LoadXml(sb.ToString());
        ToastNotification toast = new ToastNotification(doc);
        try
        {
            StringBuilder tagId = new StringBuilder();
            foreach (char c in id) if (char.IsLetterOrDigit(c)) tagId.Append(c);
            string t = tagId.ToString();
            if (t.Length > 16) t = t.Substring(0, 16);
            if (t.Length > 0) toast.Tag = t;
            string grp = (tags != null && tags.Count > 0) ? tags[0] : "default";
            if (grp.Length > 64) grp = grp.Substring(0, 64);
            toast.Group = grp;
        }
        catch { }
        ToastNotificationManager.CreateToastNotifier(_appId).Show(toast);
    }

    private static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }

    private static void Respond(NetworkStream stream, int status, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        string head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " +
            (status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : "Method Not Allowed") +
            "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) +
            "\r\nConnection: close\r\n\r\n";
        byte[] headBytes = Encoding.UTF8.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }
}


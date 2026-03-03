// ClaudePusher.cs — injects Telegram notifications into a running Claude Code CLI session
// Requires .NET 4.8 (pre-installed on Windows 10+), no external dependencies
//
// Compile:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//     /target:winexe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//     /out:ClaudePusher.exe ClaudePusher.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyProduct("ClaudePusher")]
[assembly: System.Reflection.AssemblyDescription("Inject Telegram notifications into Claude Code CLI")]
[assembly: System.Reflection.AssemblyCopyright("(c) 2026 sensboston")]

class ClaudePusher
{
    const string APP_NAME    = "ClaudePusher";
    const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string PIPE_NAME   = "claudepusher";

    static string appDir;
    static string configFile;

    // Config values loaded from config.json at startup
    static string tgToken;
    static string chatId;
    static int    pollInterval;
    static int    questionHours;
    static string windowTitle;

    #region Win32 API

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern int  GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("shcore.dll")]   static extern int  SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")]   static extern uint GetDpiForSystem();

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    const uint WM_CHAR    = 0x0102;
    const uint WM_KEYDOWN = 0x0100;
    const uint WM_KEYUP   = 0x0101;
    const int  SW_RESTORE = 9;

    #endregion

    #region State

    static long       offset       = 0;
    static DateTime   lastQuestion = DateTime.MinValue;
    static NotifyIcon tray;
    static Form       uiForm;
    static int        idleThreshold = 3; // minutes
    static bool       userAway      = false;
    static string     encoding      = "CP1251 (Cyrillic)";

    static readonly string[] encodings = {
        "CP1251  Cyrillic",
        "CP1252  Western",
        "CP1250  Central European",
        "CP1253  Greek",
        "CP1254  Turkish",
        "CP1256  Arabic",
    };
    static Mutex      instanceMutex;

    static readonly JavaScriptSerializer json            = new JavaScriptSerializer();
    static readonly object               winLock         = new object();
    static readonly object               injectLock      = new object();
    static readonly HashSet<IntPtr>      disabledWindows = new HashSet<IntPtr>();

    #endregion

    #region Entry point

    /// <summary>
    /// Application entry point. Handles CLI arguments or starts the tray app.
    /// </summary>
    static void Main(string[] args)
    {
        try { SetProcessDpiAwareness(2); } catch { }  // Per Monitor V2, Windows 8.1+
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        appDir     = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        configFile = Path.Combine(appDir, "config.json");

        bool createdNew;
        instanceMutex = new Mutex(true, "ClaudePusher_SingleInstance", out createdNew);
        if (!createdNew)
        {
            instanceMutex.Dispose();
            return;
        }

        if (args.Length > 0)
        {
            switch (args[0].ToLower())
            {
                case "--setup":            RunSetup();             return;
                case "--autostart":        RunSetAutoStart(true);  return;
                case "--remove-autostart": RunSetAutoStart(false); return;
                case "--help":
                    AllocConsole();
                    Console.WriteLine("Usage: ClaudePusher.exe [--setup | --autostart | --remove-autostart]");
                    Console.WriteLine("Config: " + configFile);
                    Console.ReadKey();
                    FreeConsole();
                    return;
            }
        }

        if (!LoadConfig())
        {
            MessageBox.Show(
                "Config not found.\nRun ClaudePusher.exe --setup to create it.\n\nConfig path:\n" + configFile,
                APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Hidden form acts as the message pump for the tray icon
        uiForm = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
        uiForm.Load += (s, e) =>
        {
            uiForm.Hide();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            tray = new NotifyIcon
            {
                Icon    = CreateAppIcon(),
                Visible = true,
                Text    = APP_NAME
            };

            var menu = new ContextMenu();
            menu.Popup += (ms, me) => RebuildTrayMenu(menu);
            tray.ContextMenu = menu;

            offset       = GetCurrentOffset();
            lastQuestion = DateTime.UtcNow;

            StartPipeServer();

            new Thread(PollLoop)        { IsBackground = true }.Start();
            new Thread(QuestionLoop)    { IsBackground = true }.Start();
            new Thread(IdleMonitorLoop) { IsBackground = true }.Start();
        };

        Application.Run(uiForm);
    }

    #endregion

    #region Named pipe server

    /// <summary>
    /// Starts a background thread listening on the named pipe for outgoing replies from Claude.
    /// Each line received is forwarded to Telegram via <see cref="TelegramSend"/>.
    /// </summary>
    static void StartPipeServer()
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PIPE_NAME, PipeDirection.In,
                        1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string text = reader.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(text))
                                TelegramSend(text);
                        }
                    }
                }
                catch { Thread.Sleep(1000); }
            }
        }) { IsBackground = true }.Start();
    }

    #endregion

    #region App icon

    /// <summary>
    /// Loads the app icon from <c>claude_icon.ico</c> next to the executable,
    /// or generates a fallback orange rounded square with a white "C".
    /// </summary>
    /// <returns>The application <see cref="Icon"/>.</returns>
    static Icon CreateAppIcon()
    {
        string icoPath = Path.Combine(appDir, "claude_icon.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath, 32, 32);

        // Fallback: orange rounded square with white "C"
        var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(255, 215, 118, 85)))
            using (var gp = new GraphicsPath())
            {
                int r = 6;
                gp.AddArc(0,      0,      r*2, r*2, 180, 90);
                gp.AddArc(32-r*2, 0,      r*2, r*2, 270, 90);
                gp.AddArc(32-r*2, 32-r*2, r*2, r*2,   0, 90);
                gp.AddArc(0,      32-r*2, r*2, r*2,  90, 90);
                gp.CloseFigure();
                g.FillPath(bg, gp);
            }
            using (var f  = new Font("Arial", 18f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var br = new SolidBrush(Color.White))
                g.DrawString("C", f, br, 7f, 6f);
        }
        IntPtr hIcon  = bmp.GetHicon();
        var    icon   = Icon.FromHandle(hIcon);
        var    clone  = (Icon)icon.Clone();
        icon.Dispose();
        bmp.Dispose();
        return clone;
    }

    #endregion

    #region Tray menu

    /// <summary>
    /// Rebuilds the native context menu on every popup to reflect the current window list,
    /// autostart state, and active encoding. Uses owner-draw for the gradient header.
    /// </summary>
    static void RebuildTrayMenu(ContextMenu menu)
    {
        menu.MenuItems.Clear();

        // Owner-draw gradient header ("ClaudePusher  v1.2")
        var header = new MenuItem { OwnerDraw = true };
        header.MeasureItem += (s, e) =>
        {
            using (var fBold = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (var fMenu = SystemFonts.MenuFont)
            {
                // Measure all regular menu item strings to find the widest
                string[] regularItems = { "Start with Windows", "Claude Code", "Encoding", "Exit" };
                float maxW = e.Graphics.MeasureString("ClaudePusher  v1.2", fBold).Width;
                foreach (string item in regularItems)
                    maxW = Math.Max(maxW, e.Graphics.MeasureString(item, fMenu).Width);
                var hSz = e.Graphics.MeasureString("ClaudePusher  v1.2", fBold);
                e.ItemWidth  = (int)maxW + 8;
                e.ItemHeight = Math.Max(26, (int)hSz.Height + 10);
            }
        };
        header.DrawItem += (s, e) =>
        {
            var r = e.Bounds;
            using (var brush = new LinearGradientBrush(
                r, Color.FromArgb(255, 220, 130, 90), Color.FromArgb(255, 170, 65, 30),
                LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(brush, r);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var f  = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (var b  = new SolidBrush(Color.White))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString("ClaudePusher  v1.2", f, b,
                    new RectangleF(8f, 0f, r.Width - 16f, r.Height), sf);
        };
        menu.MenuItems.Add(header);
        menu.MenuItems.Add("-");

        var autoItem = new MenuItem("Start with Windows") { Checked = GetAutoStart() };
        autoItem.Click += (s, e) => SetAutoStartSilent(!GetAutoStart());
        menu.MenuItems.Add(autoItem);
        menu.MenuItems.Add("-");

        var windows = FindClaudeWindows();
        if (windows.Count == 0)
        {
            menu.MenuItems.Add(new MenuItem("(no Claude Code windows)") { Enabled = false });
        }
        else
        {
            foreach (var pair in windows)
            {
                var  hwnd  = pair.Key;
                bool disabled;
                lock (winLock) disabled = disabledWindows.Contains(hwnd);
                string label = pair.Value.Length > 48 ? pair.Value.Substring(0, 45) + "..." : pair.Value;
                var item = new MenuItem(label) { Checked = !disabled };
                item.Click += (s, e) =>
                {
                    lock (winLock)
                    {
                        if (disabledWindows.Contains(hwnd)) disabledWindows.Remove(hwnd);
                        else disabledWindows.Add(hwnd);
                    }
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                };
                menu.MenuItems.Add(item);
            }
        }

        menu.MenuItems.Add("-");

        // Encoding submenu with radio checkmarks
        var encMenu = new MenuItem("Encoding");
        foreach (string enc in encodings)
        {
            string encCapture = enc;
            var encItem = new MenuItem(enc) { RadioCheck = true, Checked = (encoding == enc) };
            encItem.Click += (s, e) => { encoding = encCapture; SaveEncoding(); };
            encMenu.MenuItems.Add(encItem);
        }
        menu.MenuItems.Add(encMenu);

        menu.MenuItems.Add("-");
        menu.MenuItems.Add(new MenuItem("Exit", (s, e) =>
        {
            tray.Visible = false;
            Application.Exit();
        }));
    }

    #endregion

    #region Autostart

    /// <summary>Returns <c>true</c> if ClaudePusher is registered in the current user's Run key.</summary>
    static bool GetAutoStart()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY))
                return key != null && key.GetValue(APP_NAME) != null;
        }
        catch { return false; }
    }

    /// <summary>Adds or removes the autostart registry entry silently (no UI feedback).</summary>
    /// <param name="add"><c>true</c> to register, <c>false</c> to remove.</param>
    static void SetAutoStartSilent(bool add)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
            {
                if (add)
                    key.SetValue(APP_NAME, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else
                    key.DeleteValue(APP_NAME, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// CLI handler for <c>--autostart</c> / <c>--remove-autostart</c>: modifies the registry
    /// and prints confirmation to an allocated console window.
    /// </summary>
    /// <param name="add"><c>true</c> to register, <c>false</c> to remove.</param>
    static void RunSetAutoStart(bool add)
    {
        AllocConsole();
        using (var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
        {
            if (add)
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                key.SetValue(APP_NAME, "\"" + exe + "\"");
                Console.WriteLine("Added to autostart: " + exe);
            }
            else
            {
                key.DeleteValue(APP_NAME, false);
                Console.WriteLine("Removed from autostart.");
            }
        }
        Console.ReadKey();
        FreeConsole();
    }

    #endregion

    #region Encoding persistence

    /// <summary>
    /// Persists the currently selected encoding to <c>config.json</c>.
    /// </summary>
    static void SaveEncoding()
    {
        try
        {
            var cfg = json.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(configFile, Encoding.UTF8));
            cfg["encoding"] = encoding;
            File.WriteAllText(configFile, json.Serialize(cfg), Encoding.UTF8);
        }
        catch { }
    }

    #endregion

    #region Window discovery

    /// <summary>
    /// Enumerates all visible top-level windows and returns those whose title contains
    /// <see cref="windowTitle"/> (case-insensitive).
    /// </summary>
    /// <returns>List of (HWND, title) pairs for matching windows.</returns>
    static List<KeyValuePair<IntPtr, string>> FindClaudeWindows()
    {
        var result = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows((hWnd, lp) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();
            if (title.IndexOf(windowTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(new KeyValuePair<IntPtr, string>(hWnd, title));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    #endregion

    #region Config and setup

    /// <summary>
    /// Loads <c>config.json</c> from the executable directory into static fields.
    /// </summary>
    /// <returns><c>true</c> if the config was found and contains both token and chat_id.</returns>
    static bool LoadConfig()
    {
        if (!File.Exists(configFile)) return false;
        try
        {
            var cfg = json.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(configFile, Encoding.UTF8));

            tgToken       = cfg.ContainsKey("tg_token")               ? cfg["tg_token"].ToString()                      : "";
            chatId        = cfg.ContainsKey("chat_id")                 ? cfg["chat_id"].ToString()                       : "";
            pollInterval  = cfg.ContainsKey("poll_interval_ms")        ? Convert.ToInt32(cfg["poll_interval_ms"])        : 5000;
            questionHours = cfg.ContainsKey("question_interval_hours") ? Convert.ToInt32(cfg["question_interval_hours"]) : 4;
            windowTitle   = cfg.ContainsKey("window_title")            ? cfg["window_title"].ToString()                  : "Claude Code";
            idleThreshold = cfg.ContainsKey("idle_threshold_minutes")  ? Convert.ToInt32(cfg["idle_threshold_minutes"])  : 3;

            if (cfg.ContainsKey("encoding"))
            {
                string savedEnc = cfg["encoding"].ToString();
                // Exact match first, then match by CP number (handles renamed labels)
                if (Array.IndexOf(encodings, savedEnc) >= 0)
                    encoding = savedEnc;
                else
                {
                    var cpMatch = System.Text.RegularExpressions.Regex.Match(savedEnc, @"CP(\d+)");
                    if (cpMatch.Success)
                    {
                        string cpNum = cpMatch.Groups[1].Value;
                        foreach (string enc in encodings)
                            if (enc.StartsWith("CP" + cpNum)) { encoding = enc; break; }
                    }
                }
            }

            return !string.IsNullOrEmpty(tgToken) && !string.IsNullOrEmpty(chatId);
        }
        catch { return false; }
    }

    /// <summary>
    /// Interactive CLI setup wizard: prompts for bot token, chat_id, and window title,
    /// then writes <c>config.json</c>.
    /// </summary>
    static void RunSetup()
    {
        AllocConsole();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== ClaudePusher Setup ===");
        Console.WriteLine("Config will be saved to: " + configFile);
        Console.WriteLine();

        Console.Write("Telegram bot token: ");
        string token = Console.ReadLine().Trim();

        Console.Write("Your Telegram chat_id: ");
        string chatId = Console.ReadLine().Trim();

        Console.Write("Window title to find (default: Claude Code): ");
        string title = Console.ReadLine().Trim();
        if (string.IsNullOrEmpty(title)) title = "Claude Code";

        var cfg = new Dictionary<string, object>
        {
            { "tg_token",                token  },
            { "chat_id",                 chatId },
            { "poll_interval_ms",        5000   },
            { "question_interval_hours", 4      },
            { "window_title",            title  }
        };

        File.WriteAllText(configFile, json.Serialize(cfg), Encoding.UTF8);
        Console.WriteLine();
        Console.WriteLine("Config saved. Run ClaudePusher.exe to start.");
        Console.ReadKey();
        FreeConsole();
    }

    #endregion

    #region Poll and question loops

    /// <summary>
    /// Background loop: polls the Telegram Bot API every <see cref="pollInterval"/> ms
    /// and forwards new messages to <see cref="ProcessUpdates"/>.
    /// </summary>
    static void PollLoop()
    {
        while (true)
        {
            try
            {
                string url = "https://api.telegram.org/bot" + tgToken
                           + "/getUpdates?offset=" + offset + "&timeout=0";
                ProcessUpdates(HttpGet(url, 10000));
            }
            catch { }
            Thread.Sleep(pollInterval);
        }
    }

    /// <summary>
    /// Background loop: injects the "ask something" trigger into Claude Code
    /// once every <see cref="questionHours"/> hours to prompt a spontaneous question.
    /// </summary>
    static void QuestionLoop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(60000);
                if ((DateTime.UtcNow - lastQuestion).TotalHours >= questionHours)
                {
                    lastQuestion = DateTime.UtcNow;
                    InjectToClaude("ask something");
                }
            }
            catch { }
        }
    }

    #endregion

    #region Idle monitor

    static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref info);
        return (int)((Environment.TickCount - info.dwTime) / 1000);
    }

    static void IdleMonitorLoop()
    {
        while (true)
        {
            try
            {
                Thread.Sleep(30000);
                int idleSec = GetIdleSeconds();
                if (!userAway && idleSec >= idleThreshold * 60)
                {
                    userAway = true;
                    InjectToClaude("[T] user away");
                    Thread.Sleep(1000);
                }
                else if (userAway && idleSec < idleThreshold * 60)
                {
                    userAway = false;
                }
            }
            catch { }
        }
    }

    #endregion

    #region Telegram updates

    /// <summary>
    /// Fetches the latest update_id from the bot to skip messages received before startup.
    /// </summary>
    /// <returns>The next offset to use, or 0 on failure.</returns>
    static long GetCurrentOffset()
    {
        try
        {
            string resp = HttpGet("https://api.telegram.org/bot" + tgToken + "/getUpdates?limit=1&offset=-1", 10000);
            var data    = json.Deserialize<dynamic>(resp);
            var result  = data["result"] as object[];
            if (result != null && result.Length > 0)
            {
                var last = result[result.Length - 1] as Dictionary<string, object>;
                return Convert.ToInt64(last["update_id"]) + 1;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Parses a getUpdates response and injects each new message into Claude Code.
    /// Handles text messages, photos, and document attachments.
    /// </summary>
    /// <param name="resp">Raw JSON response from the Telegram getUpdates API.</param>
    static void ProcessUpdates(string resp)
    {
        var data   = json.Deserialize<dynamic>(resp);
        var result = data["result"] as object[];
        if (result == null || result.Length == 0) return;

        foreach (var obj in result)
        {
            var upd = obj as Dictionary<string, object>;
            if (upd == null) continue;

            offset = Convert.ToInt64(upd["update_id"]) + 1;

            if (!upd.ContainsKey("message")) continue;
            var msg = upd["message"] as Dictionary<string, object>;
            if (msg == null) continue;

            // Only process messages from the configured chat_id
            var chat = msg["chat"] as Dictionary<string, object>;
            if (chat == null || chat["id"].ToString() != chatId) continue;

            if (msg.ContainsKey("text"))
            {
                InjectToClaude("[T] " + msg["text"].ToString().Replace("\r", "").Replace("\n", " "));
            }
            else if (msg.ContainsKey("photo"))
            {
                string path    = DownloadPhoto(msg);
                string caption = msg.ContainsKey("caption") ? " " + msg["caption"].ToString().Replace("\r", "").Replace("\n", " ") : "";
                if (path != null) InjectToClaude("[T] [photo: " + path + "]" + caption);
            }
            else if (msg.ContainsKey("document"))
            {
                string path    = DownloadDocument(msg);
                string caption = msg.ContainsKey("caption") ? " " + msg["caption"].ToString().Replace("\r", "").Replace("\n", " ") : "";
                if (path != null) InjectToClaude("[T] [file: " + path + "]" + caption);
            }

            // Give Claude time to process before injecting the next message in the batch
            Thread.Sleep(1000);
        }
    }

    #endregion

    #region Telegram send

    /// <summary>
    /// Sends a text message to the configured Telegram chat via the Bot API.
    /// </summary>
    /// <param name="text">The message text to send.</param>
    static void TelegramSend(string text)
    {
        try
        {
            string body = "chat_id=" + Uri.EscapeDataString(chatId) + "&text=" + Uri.EscapeDataString(text);
            byte[] data = Encoding.UTF8.GetBytes(body);
            string url  = "https://api.telegram.org/bot" + tgToken + "/sendMessage";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method        = "POST";
            req.ContentType   = "application/x-www-form-urlencoded";
            req.ContentLength = data.Length;
            req.Timeout = req.ReadWriteTimeout = 10000;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (req.GetResponse()) { }
        }
        catch { }
    }

    #endregion

    #region File and photo download

    /// <summary>
    /// Downloads the largest available photo size from a Telegram message.
    /// </summary>
    /// <param name="msg">The Telegram message object containing a "photo" array.</param>
    /// <returns>Local file path of the downloaded image, or <c>null</c> on failure.</returns>
    static string DownloadPhoto(Dictionary<string, object> msg)
    {
        try
        {
            // Telegram sends photos as an array of sizes — take the largest (last)
            var photos = msg["photo"] as object[];
            if (photos == null || photos.Length == 0) return null;
            var largest = photos[photos.Length - 1] as Dictionary<string, object>;
            if (largest == null) return null;
            return DownloadTelegramFile(largest["file_id"].ToString(), ".jpg");
        }
        catch { return null; }
    }

    /// <summary>
    /// Downloads a document attachment from a Telegram message, preserving the file extension.
    /// </summary>
    /// <param name="msg">The Telegram message object containing a "document" field.</param>
    /// <returns>Local file path of the downloaded file, or <c>null</c> on failure.</returns>
    static string DownloadDocument(Dictionary<string, object> msg)
    {
        try
        {
            var doc = msg["document"] as Dictionary<string, object>;
            if (doc == null) return null;
            string fileName = doc.ContainsKey("file_name") ? doc["file_name"].ToString() : "file";
            return DownloadTelegramFile(doc["file_id"].ToString(), Path.GetExtension(fileName));
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves a Telegram file_id to a download URL via getFile, then saves the file
    /// to the <c>inbox_media/</c> folder with a timestamp-based name.
    /// </summary>
    /// <param name="fileId">Telegram file_id to resolve and download.</param>
    /// <param name="ext">File extension to use for the saved file (e.g. ".jpg").</param>
    /// <returns>Full local path of the saved file.</returns>
    static string DownloadTelegramFile(string fileId, string ext)
    {
        string meta     = HttpGet("https://api.telegram.org/bot" + tgToken + "/getFile?file_id=" + fileId, 10000);
        var    metaObj  = json.Deserialize<dynamic>(meta);
        string filePath = ((Dictionary<string, object>)metaObj["result"])["file_path"].ToString();
        string url      = "https://api.telegram.org/file/bot" + tgToken + "/" + filePath;
        string dir      = Path.Combine(appDir, "inbox_media");
        Directory.CreateDirectory(dir);
        string dest     = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext);
        var    req      = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = req.ReadWriteTimeout = 30000;
        using (var response = req.GetResponse())
        using (var fs = File.Create(dest))
            response.GetResponseStream().CopyTo(fs);
        return dest;
    }

    #endregion

    #region Keyboard injection

    /// <summary>
    /// Injects a text string followed by Enter into all enabled Claude Code windows.
    /// </summary>
    /// <param name="text">The text to inject.</param>
    static void InjectToClaude(string text)
    {
        lock (injectLock)
        {
            foreach (var pair in FindClaudeWindows())
            {
                bool disabled;
                lock (winLock) disabled = disabledWindows.Contains(pair.Key);
                if (!disabled) PostText(pair.Key, text);
            }
        }
    }

    /// <summary>
    /// Posts each character of <paramref name="text"/> as a WM_CHAR message to the target window,
    /// then sends a Return keystroke (WM_KEYDOWN/WM_KEYUP with VK_RETURN).
    /// Uses CP1251 encoding so Cyrillic characters are received correctly by mintty.
    /// </summary>
    /// <param name="hwnd">Target window handle.</param>
    /// <param name="text">Text to post.</param>
    static void PostText(IntPtr hwnd, string text)
    {
        try
        {
            // Pre-clear: send Enter to dismiss any stuck prompt line
            PostMessage(hwnd, WM_KEYDOWN, new IntPtr(0x0D), new IntPtr(0x001C0001));
            Thread.Sleep(20);
            PostMessage(hwnd, WM_KEYUP,   new IntPtr(0x0D), new IntPtr(0xC01C0001));
            Thread.Sleep(150);

            // WM_CHAR expects ANSI byte values; extract codepage number from e.g. "CP1251 (Cyrillic)"
            int cp = 1251;
            var m = System.Text.RegularExpressions.Regex.Match(encoding, @"CP(\d+)");
            if (m.Success) int.TryParse(m.Groups[1].Value, out cp);
            foreach (byte b in Encoding.GetEncoding(cp).GetBytes(text))
            {
                PostMessage(hwnd, WM_CHAR, new IntPtr(b), new IntPtr(1));
                Thread.Sleep(10);
            }
            Thread.Sleep(50);
            PostMessage(hwnd, WM_KEYDOWN, new IntPtr(0x0D), new IntPtr(0x001C0001));
            Thread.Sleep(20);
            PostMessage(hwnd, WM_KEYUP,   new IntPtr(0x0D), new IntPtr(0xC01C0001));
        }
        catch { }
    }

    #endregion

    #region HTTP

    /// <summary>
    /// Performs a synchronous HTTP GET request and returns the response body as a UTF-8 string.
    /// </summary>
    /// <param name="url">The URL to request.</param>
    /// <param name="timeoutMs">Request and read timeout in milliseconds.</param>
    /// <returns>Response body text.</returns>
    static string HttpGet(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = req.ReadWriteTimeout = timeoutMs;
        using (var resp   = (HttpWebResponse)req.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    #endregion
}

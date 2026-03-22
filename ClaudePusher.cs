// ClaudePusher 2.0 — unified Telegram + Voice interface for Claude Code.
//
// Modules:
//   Telegram: polls Telegram Bot API, injects messages into Claude Code, sends replies via pipe
//   Voice:    mic (winmm.dll) → energy VAD → Whisper STT → "[V] text" injected into Claude
//             Named pipe "voicepusher" → Edge TTS (WebSocket) → waveOut playback
//
// Config: config.json (sectioned). Auto-migrates from v1.x flat format.
//
// Compile: compile.ps1  (requires VS2022 Roslyn csc + VoicePusher/mp3sharp_src)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using MP3Sharp;

[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyProduct("ClaudePusher")]
[assembly: AssemblyDescription("Telegram + Voice interface for Claude Code")]
[assembly: AssemblyCopyright("(c) 2026 sensboston")]

// ─── Settings dialog ────────────────────────────────────────────────────────

class SettingsForm : Form
{
    // General
    ComboBox      cbEncoding;
    NumericUpDown nudIdle;
    CheckBox      chkAutoStart, chkTelegram, chkVoice;

    // Telegram
    TextBox       tbToken, tbChatId;

    // Voice
    TextBox       tbApiKey, tbSttModel, tbLanguage, tbTtsVoice, tbLogFile;
    NumericUpDown nudRms, nudSilSec, nudMinAudio;

    public SettingsForm()
    {
        SuspendLayout();
        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text            = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = MinimizeBox = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(490, 430);

        float dpiScale;
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            dpiScale = g.DpiX / 96f;

        var tabBg = SystemColors.Control;
        var tabs = new TabControl
        {
            Left      = 8,    Top    = 8,
            Width     = 474,  Height = 375,
            SizeMode  = TabSizeMode.Fixed,
            ItemSize  = new Size((int)(100 * dpiScale), (int)(26 * dpiScale)),
            DrawMode  = TabDrawMode.OwnerDrawFixed
        };
        tabs.DrawItem += (s, e) =>
        {
            var tc      = (TabControl)s;
            bool sel    = (e.Index == tc.SelectedIndex);
            var  bgCol  = sel ? tabBg : SystemColors.ControlDark;
            var  fgCol  = sel ? SystemColors.ControlText : SystemColors.ControlLightLight;
            using (var bg = new SolidBrush(bgCol))
                e.Graphics.FillRectangle(bg, e.Bounds);
            var r = e.Bounds;
            // For selected tab, extend fill downward by 2px to hide the bottom border line
            if (sel)
            {
                var ext = new Rectangle(r.Left + 1, r.Bottom - 2, r.Width - 2, 3);
                using (var bg = new SolidBrush(tabBg))
                    e.Graphics.FillRectangle(bg, ext);
            }
            TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text,
                sel ? new Font(tc.Font, FontStyle.Bold) : tc.Font,
                r, fgCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };

        // ── General tab ──────────────────────────────────────────────────────
        var tabG = new TabPage("General") { BackColor = tabBg };
        int y = 16;

        cbEncoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var enc in ClaudePusher.Encodings) cbEncoding.Items.Add(enc);
        cbEncoding.SelectedItem = ClaudePusher.encoding;
        if (cbEncoding.SelectedIndex < 0) cbEncoding.SelectedIndex = 0;
        y = AddRow(tabG, y, "Encoding:", cbEncoding, 260);

        nudIdle = new NumericUpDown { Minimum = 1, Maximum = 60, Value = ClaudePusher.idleThreshold };
        y = AddRow(tabG, y, "Inactivity alert (min):", nudIdle, 80);

        chkAutoStart = new CheckBox { Text = "Start with Windows", Checked = ClaudePusher.GetAutoStart(), Left = 12, Top = y, AutoSize = true };
        tabG.Controls.Add(chkAutoStart); y += 28;

        chkTelegram = new CheckBox { Text = "Enable Telegram module", Checked = ClaudePusher.enableTelegram, Left = 12, Top = y, AutoSize = true };
        tabG.Controls.Add(chkTelegram); y += 28;

        chkVoice = new CheckBox { Text = "Enable Voice module (STT + TTS)", Checked = ClaudePusher.enableVoice, Left = 12, Top = y, AutoSize = true };
        tabG.Controls.Add(chkVoice); y += 30;

        var note = new Label { Text = "Module changes take effect after restart.", Left = 12, Top = y, AutoSize = true, ForeColor = SystemColors.GrayText };
        tabG.Controls.Add(note);

        // ── Telegram tab ─────────────────────────────────────────────────────
        var tabT = new TabPage("Telegram") { BackColor = tabBg };
        y = 16;
        tbToken  = new TextBox { Text = ClaudePusher.tgToken };
        y = AddRow(tabT, y, "Bot token:", tbToken, 270);

        tbChatId = new TextBox { Text = ClaudePusher.chatId };
        y = AddRow(tabT, y, "Chat ID:", tbChatId, 270);

        // ── Voice tab ────────────────────────────────────────────────────────
        var tabV = new TabPage("Voice") { BackColor = tabBg };
        y = 16;
        tbApiKey  = new TextBox { Text = ClaudePusher.voiceApiKey, UseSystemPasswordChar = true };
        y = AddRow(tabV, y, "OpenAI API key:", tbApiKey, 270);

        tbSttModel = new TextBox { Text = ClaudePusher.sttModel };
        y = AddRow(tabV, y, "STT model:", tbSttModel, 120);

        tbLanguage = new TextBox { Text = ClaudePusher.language };
        y = AddRow(tabV, y, "Language (ru, en, ...):", tbLanguage, 80);

        tbTtsVoice = new TextBox { Text = ClaudePusher.ttsVoice };
        y = AddRow(tabV, y, "TTS voice:", tbTtsVoice, 270);

        nudRms = new NumericUpDown { Minimum = 0.001M, Maximum = 1M, Increment = 0.001M, DecimalPlaces = 3, Value = (decimal)ClaudePusher.silenceRms };
        y = AddRow(tabV, y, "Silence RMS threshold:", nudRms, 100);

        nudSilSec = new NumericUpDown { Minimum = 0.1M, Maximum = 5M, Increment = 0.1M, DecimalPlaces = 1, Value = (decimal)ClaudePusher.silenceSec };
        y = AddRow(tabV, y, "Silence duration (s):", nudSilSec, 80);

        nudMinAudio = new NumericUpDown { Minimum = 0.1M, Maximum = 5M, Increment = 0.1M, DecimalPlaces = 1, Value = (decimal)ClaudePusher.minAudioSec };
        y = AddRow(tabV, y, "Min audio (s):", nudMinAudio, 80);

        var lblLog = new Label { Text = "Log file:", Left = 12, Top = y + 3, Width = 160, AutoSize = false };
        tbLogFile  = new TextBox { Text = ClaudePusher.logFile, Left = 180, Top = y, Width = 220 };
        var btnBrowse = new Button { Text = "...", Left = 408, Top = y, Width = 32, Height = 23 };
        btnBrowse.Click += (s, e) =>
        {
            var dlg = new SaveFileDialog { Title = "Select log file", Filter = "Log files|*.log|All files|*.*", FileName = tbLogFile.Text };
            if (dlg.ShowDialog() == DialogResult.OK) tbLogFile.Text = dlg.FileName;
        };
        tabV.Controls.AddRange(new Control[] { lblLog, tbLogFile, btnBrowse });

        // ── Assemble ─────────────────────────────────────────────────────────
        tabs.TabPages.AddRange(new TabPage[] { tabG, tabT, tabV });
        Controls.Add(tabs);

        var btnOk     = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Left = 306, Top = 393, Width = 80 };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 398, Top = 393, Width = 80 };
        btnOk.Click += OnOk;
        Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        ResumeLayout(false);
        PerformLayout();
    }

    static int AddRow(TabPage parent, int y, string label, Control ctrl, int ctrlWidth)
    {
        var lbl = new Label { Text = label, Left = 12, Top = y + 3, Width = 160, AutoSize = false };
        ctrl.Left  = 180;
        ctrl.Top   = y;
        ctrl.Width = ctrlWidth;
        parent.Controls.AddRange(new Control[] { lbl, ctrl });
        return y + 30;
    }

    void OnOk(object sender, EventArgs e)
    {
        ClaudePusher.encoding      = cbEncoding.SelectedItem?.ToString() ?? ClaudePusher.encoding;
        ClaudePusher.idleThreshold = (int)nudIdle.Value;
        ClaudePusher.SetAutoStartSilent(chkAutoStart.Checked);

        bool restartNeeded = (chkTelegram.Checked != ClaudePusher.enableTelegram)
                          || (chkVoice.Checked    != ClaudePusher.enableVoice);

        ClaudePusher.enableTelegram = chkTelegram.Checked;
        ClaudePusher.enableVoice    = chkVoice.Checked;

        ClaudePusher.tgToken = tbToken.Text.Trim();
        ClaudePusher.chatId  = tbChatId.Text.Trim();

        ClaudePusher.voiceApiKey = tbApiKey.Text.Trim();
        ClaudePusher.sttModel    = tbSttModel.Text.Trim();
        ClaudePusher.language    = tbLanguage.Text.Trim();
        ClaudePusher.ttsVoice    = tbTtsVoice.Text.Trim();
        ClaudePusher.silenceRms  = (float)nudRms.Value;
        ClaudePusher.silenceSec  = (float)nudSilSec.Value;
        ClaudePusher.minAudioSec = (float)nudMinAudio.Value;
        ClaudePusher.logFile     = tbLogFile.Text.Trim();

        ClaudePusher.SaveConfig();

        if (restartNeeded)
            MessageBox.Show("Module changes will take effect after restart.", "ClaudePusher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

// ─── Main class ─────────────────────────────────────────────────────────────

static class ClaudePusher
{
    const string APP_NAME    = "ClaudePusher";
    const string APP_VERSION = "2.0";
    const string PIPE_TG     = "claudepusher";
    const string PIPE_VOICE  = "voicepusher";
    const string REG_RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string appDir;
    static string configFile;

    #region WinAPI — console + window

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int  GetWindowText(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] static extern int  GetClassName(IntPtr hWnd, StringBuilder b, int n);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll")] static extern bool   FreeConsole();
    [DllImport("kernel32.dll")] static extern bool   AttachConsole(uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
    static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] buf, uint n, out uint written);
    [DllImport("kernel32.dll")] static extern bool   CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] static extern bool   Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool   Process32Next(IntPtr snap, ref PROCESSENTRY32 e);

    const int    SW_RESTORE     = 9;
    const ushort KEY_EVENT      = 0x0001;
    const ushort VK_RETURN      = 0x000D;
    const uint   GENERIC_WRITE  = 0x40000000;
    const uint   FILE_SHARE_RW  = 0x00000003;
    const uint   OPEN_EXISTING  = 3;
    const uint   TH32CS_SNAP    = 0x00000002;

    delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PROCESSENTRY32
    {
        public uint   dwSize;
        public uint   cntUsage;
        public uint   th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint   th32ModuleID;
        public uint   cntThreads;
        public uint   th32ParentProcessID;
        public int    pcPriClassBase;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)]  public ushort EventType;
        [FieldOffset(4)]  public int    bKeyDown;
        [FieldOffset(8)]  public ushort wRepeatCount;
        [FieldOffset(10)] public ushort wVirtualKeyCode;
        [FieldOffset(12)] public ushort wVirtualScanCode;
        [FieldOffset(14)] public ushort UnicodeChar;
        [FieldOffset(16)] public uint   dwControlKeyState;
    }

    #endregion

    #region WinAPI — clipboard

    [DllImport("user32.dll")] static extern bool   OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool   CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern bool   IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool   GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);

    const uint CF_DIB        = 8;
    const uint CF_BITMAP     = 2;
    const uint CF_DIBSECTION = 0x0012;

    #endregion

    #region WinAPI — idle time

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    #endregion

    #region WinAPI — winmm (audio capture + playback)

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, WaveInProc cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, ref WAVEHDR hdr, int sz);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, ref WAVEHDR hdr, int sz);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, ref WAVEHDR hdr, int sz);

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr pHdr, int sz);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr pHdr, int sz);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr pHdr, int sz);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

    delegate void WaveInProc(IntPtr h, int msg, IntPtr inst, ref WAVEHDR hdr, IntPtr p2);
    const int MM_WIM_DATA       = 0x3C0;
    const int CALLBACK_FUNCTION = 0x30000;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public short wFormatTag, nChannels;
        public int   nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public int    dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public int    dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    #endregion

    #region WinAPI — WinHttp WebSocket (Edge TTS)

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpOpen(string ua, uint access, string proxy, string bypass, uint flags);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpConnect(IntPtr hSess, string host, int port, uint reserved);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WinHttpOpenRequest(IntPtr hConn, string verb, string path, string ver, string ref_, IntPtr acc, uint flags);
    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WinHttpAddRequestHeaders(IntPtr hReq, string hdrs, uint len, uint mods);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpSetOption(IntPtr h, uint opt, IntPtr buf, uint bufLen);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpSendRequest(IntPtr h, string hdrs, uint hdrLen, IntPtr body, uint bodyLen, uint total, IntPtr ctx);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpReceiveResponse(IntPtr h, IntPtr reserved);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern IntPtr WinHttpWebSocketCompleteUpgrade(IntPtr hReq, IntPtr ctx);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern uint WinHttpWebSocketSend(IntPtr hWs, uint bufType, byte[] buf, uint bufLen);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern uint WinHttpWebSocketReceive(IntPtr hWs, byte[] buf, uint bufLen, ref uint read, ref uint bufType);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern uint WinHttpWebSocketClose(IntPtr hWs, ushort status, IntPtr reason, uint reasonLen);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpCloseHandle(IntPtr h);
    [DllImport("winhttp.dll", SetLastError = true)]
    static extern bool WinHttpSetTimeouts(IntPtr h, int resolve, int connect, int send, int recv);

    const uint WINHTTP_ACCESS_TYPE_NO_PROXY         = 1;
    const uint WINHTTP_FLAG_SECURE                  = 0x00800000;
    const uint WINHTTP_ADDREQ_FLAG_ADD              = 0x20000000;
    const uint WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET = 114;
    const uint WS_BINARY_MESSAGE  = 0;
    const uint WS_BINARY_FRAGMENT = 1;
    const uint WS_UTF8_MESSAGE    = 2;
    const uint WS_UTF8_FRAGMENT   = 3;
    const uint WS_CLOSE           = 4;

    #endregion

    #region WinAPI — misc

    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern bool FreeConsoleK();

    #endregion

    #region Config

    public static readonly string[] Encodings = {
        "CP1251  Cyrillic",
        "CP1252  Western",
        "CP1250  Central European",
        "CP1253  Greek",
        "CP1254  Turkish",
        "CP1256  Arabic",
    };

    static readonly JavaScriptSerializer json = new JavaScriptSerializer();

    // General
    public static string encoding      = "CP1251  Cyrillic";
    public static int    idleThreshold = 3;
    public static bool   enableTelegram = true;
    public static bool   enableVoice    = false;

    // Telegram
    public static string tgToken = "";
    public static string chatId  = "";

    // Voice
    public static string voiceApiKey = "";
    public static string sttModel    = "whisper-1";
    public static string language    = "";
    public static float  silenceRms  = 0.005f;
    public static float  silenceSec  = 0.8f;
    public static float  minAudioSec = 0.5f;
    public static string ttsVoice    = "en-US-BrianMultilingualNeural";
    public static string logFile     = "";

    static bool LoadConfig()
    {
        if (!File.Exists(configFile)) return false;
        try
        {
            var cfg = json.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(configFile, Encoding.UTF8));

            // Auto-migrate from v1.x flat format
            if (cfg.ContainsKey("tg_token"))
            {
                tgToken       = cfg.ContainsKey("tg_token")              ? cfg["tg_token"].ToString()                     : tgToken;
                chatId        = cfg.ContainsKey("chat_id")               ? cfg["chat_id"].ToString()                      : chatId;
                idleThreshold = cfg.ContainsKey("idle_threshold_minutes") ? Convert.ToInt32(cfg["idle_threshold_minutes"]) : idleThreshold;
                if (cfg.ContainsKey("encoding")) ApplyEncoding(cfg["encoding"].ToString());
                enableTelegram = true;
                enableVoice    = false;
                SaveConfig();
                return !string.IsNullOrEmpty(tgToken) && !string.IsNullOrEmpty(chatId);
            }

            // v2.x sectioned format
            var general  = cfg.ContainsKey("general")  ? cfg["general"]  as Dictionary<string, object> : null;
            var telegram = cfg.ContainsKey("telegram")  ? cfg["telegram"] as Dictionary<string, object> : null;
            var voice    = cfg.ContainsKey("voice")     ? cfg["voice"]    as Dictionary<string, object> : null;

            if (general != null)
            {
                if (general.ContainsKey("encoding"))              ApplyEncoding(general["encoding"].ToString());
                if (general.ContainsKey("idle_threshold_minutes")) idleThreshold  = Convert.ToInt32(general["idle_threshold_minutes"]);
                if (general.ContainsKey("enable_telegram"))        enableTelegram = Convert.ToBoolean(general["enable_telegram"]);
                if (general.ContainsKey("enable_voice"))           enableVoice    = Convert.ToBoolean(general["enable_voice"]);
            }
            if (telegram != null)
            {
                if (telegram.ContainsKey("tg_token")) tgToken = telegram["tg_token"].ToString();
                if (telegram.ContainsKey("chat_id"))  chatId  = telegram["chat_id"].ToString();
            }
            if (voice != null)
            {
                if (voice.ContainsKey("api_key"))      voiceApiKey = voice["api_key"].ToString();
                if (voice.ContainsKey("stt_model"))    sttModel    = voice["stt_model"].ToString();
                if (voice.ContainsKey("language"))     language    = voice["language"].ToString();
                if (voice.ContainsKey("silence_rms"))  float.TryParse(voice["silence_rms"].ToString(), out silenceRms);
                if (voice.ContainsKey("silence_sec"))  float.TryParse(voice["silence_sec"].ToString(), out silenceSec);
                if (voice.ContainsKey("min_audio_sec")) float.TryParse(voice["min_audio_sec"].ToString(), out minAudioSec);
                if (voice.ContainsKey("tts_voice"))    ttsVoice    = voice["tts_voice"].ToString();
                if (voice.ContainsKey("log_file"))     logFile     = voice["log_file"].ToString();
            }

            if (enableTelegram) return !string.IsNullOrEmpty(tgToken) && !string.IsNullOrEmpty(chatId);
            if (enableVoice)    return !string.IsNullOrEmpty(voiceApiKey);
            return true;
        }
        catch { return false; }
    }

    static void ApplyEncoding(string saved)
    {
        if (Array.IndexOf(Encodings, saved) >= 0) { encoding = saved; return; }
        var m = Regex.Match(saved, @"CP(\d+)");
        if (!m.Success) return;
        string cp = m.Groups[1].Value;
        foreach (string enc in Encodings)
            if (enc.StartsWith("CP" + cp)) { encoding = enc; return; }
    }

    public static void SaveConfig()
    {
        try
        {
            var cfg = new Dictionary<string, object>
            {
                { "general", new Dictionary<string, object>
                    {
                        { "encoding",               encoding      },
                        { "idle_threshold_minutes", idleThreshold },
                        { "start_with_windows",     GetAutoStart() },
                        { "enable_telegram",        enableTelegram },
                        { "enable_voice",           enableVoice    }
                    }
                },
                { "telegram", new Dictionary<string, object>
                    {
                        { "tg_token", tgToken },
                        { "chat_id",  chatId  }
                    }
                },
                { "voice", new Dictionary<string, object>
                    {
                        { "api_key",      voiceApiKey },
                        { "stt_model",    sttModel    },
                        { "language",     language    },
                        { "silence_rms",  silenceRms  },
                        { "silence_sec",  silenceSec  },
                        { "min_audio_sec", minAudioSec },
                        { "tts_voice",    ttsVoice    },
                        { "log_file",     logFile     }
                    }
                }
            };
            File.WriteAllText(configFile, json.Serialize(cfg), Encoding.UTF8);
        }
        catch { }
    }

    #endregion

    #region Autostart

    public static bool GetAutoStart()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY))
                return key != null && key.GetValue(APP_NAME) != null;
        }
        catch { return false; }
    }

    public static void SetAutoStartSilent(bool add)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(REG_RUN_KEY, true))
            {
                if (add) key.SetValue(APP_NAME, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else     key.DeleteValue(APP_NAME, false);
            }
        }
        catch { }
    }

    #endregion

    #region Window discovery

    static Dictionary<uint, uint> GetParentPidMap()
    {
        var map  = new Dictionary<uint, uint>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAP, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            if (Process32First(snap, ref e))
                do { map[e.th32ProcessID] = e.th32ParentProcessID; }
                while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    static List<KeyValuePair<IntPtr, string>> FindClaudeWindows()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("claude"))
            pids.Add((uint)p.Id);

        var result = new List<KeyValuePair<IntPtr, string>>();
        if (pids.Count == 0) return result;

        var parentMap = GetParentPidMap();
        var hostMap   = new Dictionary<uint, uint>();
        foreach (uint cpid in pids)
        {
            hostMap[cpid] = cpid;
            uint cur = cpid;
            for (int depth = 0; depth < 5; depth++)
            {
                uint parent;
                if (!parentMap.TryGetValue(cur, out parent) || parent == 0) break;
                hostMap[parent] = cpid;
                cur = parent;
            }
        }

        EnumWindows((hWnd, lp) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var cls = new StringBuilder(64);
            GetClassName(hWnd, cls, 64);
            string cn = cls.ToString();
            if (cn != "ConsoleWindowClass" && cn != "CASCADIA_HOSTING_WINDOW_CLASS") return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            uint cpid;
            if (!hostMap.TryGetValue(pid, out cpid)) return true;
            result.Add(new KeyValuePair<IntPtr, string>(hWnd, "claude.exe [" + cpid + "]"));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    static volatile List<KeyValuePair<IntPtr, string>> cachedWindows = new List<KeyValuePair<IntPtr, string>>();
    static readonly HashSet<IntPtr> disabledWindows = new HashSet<IntPtr>();
    static readonly object          winLock         = new object();

    static void WindowCacheLoop()
    {
        while (true)
        {
            try { cachedWindows = FindClaudeWindows(); } catch { }
            Thread.Sleep(3000);
        }
    }

    #endregion

    #region Inject

    static readonly object injectLock = new object();

    static void InjectToClaude(string text)
    {
        lock (injectLock)
        {
            foreach (var pair in cachedWindows)
            {
                bool disabled;
                lock (winLock) disabled = disabledWindows.Contains(pair.Key);
                if (!disabled) PostText(pair.Key, text);
            }
        }
    }

    static void PostText(IntPtr hwnd, string text)
    {
        try
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            FreeConsole();
            if (!AttachConsole(pid)) return;

            IntPtr hConIn = CreateFile("CONIN$", GENERIC_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (hConIn == new IntPtr(-1)) { FreeConsole(); return; }

            try
            {
                WriteKey(hConIn, '\r', VK_RETURN);
                Thread.Sleep(150);
                foreach (char c in EmojiToText(text))
                {
                    WriteKey(hConIn, c, 0);
                    Thread.Sleep(30);
                }
                Thread.Sleep(50);
                WriteKey(hConIn, '\r', VK_RETURN);
            }
            finally
            {
                CloseHandle(hConIn);
                FreeConsole();
            }
        }
        catch { }
    }

    static void WriteKey(IntPtr hConIn, char c, ushort vk)
    {
        var recs = new INPUT_RECORD[2];
        for (int i = 0; i < 2; i++)
        {
            recs[i].EventType        = KEY_EVENT;
            recs[i].bKeyDown         = (i == 0) ? 1 : 0;
            recs[i].wRepeatCount     = 1;
            recs[i].wVirtualKeyCode  = vk;
            recs[i].wVirtualScanCode = 0;
            recs[i].UnicodeChar      = (ushort)c;
            recs[i].dwControlKeyState = 0;
        }
        uint written;
        WriteConsoleInput(hConIn, recs, 2, out written);
    }
    static string EmojiToText(string text)
    {
        var map = new Dictionary<string, string>
        {
            { "👍","(y)"   }, { "👎","(n)"   }, { "👏","(clap)"  }, { "🙌","(y)(y)" }, { "🤝","(ok)"  },
            { "👌","OK"    }, { "🆗","OK"    }, { "✌","(v)"    }, { "☝","(^)"    }, { "👉","(->)"  },
            { "👈","(<-)"  }, { "🤞","(x)"   }, { "💪","(str)"  },
            { "😀",":D"    }, { "😁",":D"    }, { "😂",":D)"   }, { "🤣","XD"    }, { "😊",":)"    },
            { "🙂",":)"    }, { "😉",";)"    }, { "😍",":*"    }, { "🥰",":*"    }, { "😎","B)"    },
            { "🤔","hmm"   }, { "😐",":|"    }, { "😑",":|"    }, { "😒",":/'"   }, { "😢",":("    },
            { "😭",":'("   }, { "😤",">:("   }, { "😠",">:("   }, { "😡",">:("   }, { "🤯","O_O"   },
            { "😱",":O"    }, { "😴","zzz"   }, { "🤢","blergh" }, { "😷","(mask)" },
            { "🙄","=_="   }, { "😏",";)"    }, { "🫠",":/'"   },
            { "❤","<3"    }, { "❤️","<3"    }, { "🧡","<3"    }, { "💛","<3"    }, { "💚","<3"    },
            { "💙","<3"    }, { "💜","<3"    }, { "💔","</3"   }, { "💯","100%"  }, { "🔥","fire"  },
            { "✨","*"     }, { "⭐","*"     }, { "🌟","*"     }, { "💡","(idea)" }, { "🎉","(yay)" },
            { "🎊","(yay)" }, { "🎁","(gift)" }, { "🏆","(win)" },
            { "✅","[+]"   }, { "❌","[-]"   }, { "⚠","[!]"   }, { "ℹ","[i]"    }, { "❓","?"     },
            { "❗","!"     }, { "⁉","!?"    }, { "‼","!!"    },
            { "👀","(eyes)" }, { "🙈","(nope)" }, { "🤦","(facepalm)" }, { "🤷","shrug" },
            { "💀","(dead)" }, { "☠","(dead)" }, { "🫡","(aye)" }, { "🫤","meh"   }, { "🫣","(peek)" },
        };
        foreach (var kv in map) text = text.Replace(kv.Key, kv.Value);
        return text;
    }

    #endregion

    #region Idle monitor

    static volatile bool userAway = false;

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

    #region Telegram module

    static long offset = 0;

    static void StartTelegramModule()
    {
        offset = GetCurrentOffset();

        new Thread(PollLoop)        { IsBackground = true }.Start();
        new Thread(IdleMonitorLoop) { IsBackground = true }.Start();
        StartTelegramPipeServer();
    }

    // Long polling: server holds connection up to 25 s and returns instantly on new update.
    // Near-real-time delivery with minimal API load.
    static void PollLoop()
    {
        while (true)
        {
            try
            {
                string url = "https://api.telegram.org/bot" + tgToken
                           + "/getUpdates?offset=" + offset + "&timeout=25";
                ProcessUpdates(HttpGet(url, 30000)); // 30 s HTTP timeout > 25 s server timeout
            }
            catch { Thread.Sleep(3000); } // back off on network error
        }
    }

    static long GetCurrentOffset()
    {
        try
        {
            string resp = HttpGet("https://api.telegram.org/bot" + tgToken + "/getUpdates?limit=1&offset=-1", 10000);
            var data   = json.Deserialize<dynamic>(resp);
            var result = data["result"] as object[];
            if (result != null && result.Length > 0)
            {
                var last = result[result.Length - 1] as Dictionary<string, object>;
                return Convert.ToInt64(last["update_id"]) + 1;
            }
        }
        catch { }
        return 0;
    }

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
            Thread.Sleep(1000);
        }
    }

    public static void TelegramSend(string text)
    {
        if (!enableTelegram) return;
        try
        {
            string body = "chat_id=" + Uri.EscapeDataString(chatId) + "&text=" + Uri.EscapeDataString(text);
            byte[] data = Encoding.UTF8.GetBytes(body);
            string url  = "https://api.telegram.org/bot" + tgToken + "/sendMessage";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method        = "POST";
            req.ContentType   = "application/x-www-form-urlencoded";
            req.ContentLength = data.Length;
            req.Timeout       = req.ReadWriteTimeout = 10000;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (req.GetResponse()) { }
        }
        catch { }
    }

    static string DownloadPhoto(Dictionary<string, object> msg)
    {
        try
        {
            var photos  = msg["photo"] as object[];
            if (photos == null || photos.Length == 0) return null;
            var largest = photos[photos.Length - 1] as Dictionary<string, object>;
            if (largest == null) return null;
            return DownloadTgFile(largest["file_id"].ToString(), ".jpg");
        }
        catch { return null; }
    }

    static string DownloadDocument(Dictionary<string, object> msg)
    {
        try
        {
            var doc = msg["document"] as Dictionary<string, object>;
            if (doc == null) return null;
            string name = doc.ContainsKey("file_name") ? doc["file_name"].ToString() : "file";
            return DownloadTgFile(doc["file_id"].ToString(), Path.GetExtension(name));
        }
        catch { return null; }
    }

    static string DownloadTgFile(string fileId, string ext)
    {
        string meta  = HttpGet("https://api.telegram.org/bot" + tgToken + "/getFile?file_id=" + fileId, 10000);
        var    mobj  = json.Deserialize<dynamic>(meta);
        string path  = ((Dictionary<string, object>)mobj["result"])["file_path"].ToString();
        string url   = "https://api.telegram.org/file/bot" + tgToken + "/" + path;
        string dir   = Path.Combine(appDir, "inbox_media");
        Directory.CreateDirectory(dir);
        string dest  = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext);
        var    req   = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout  = req.ReadWriteTimeout = 30000;
        using (var resp = req.GetResponse())
        using (var fs   = File.Create(dest))
            resp.GetResponseStream().CopyTo(fs);
        return dest;
    }

    // Pipe server: Claude replies → Telegram
    static void StartTelegramPipeServer()
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PIPE_TG,
                        PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                var t = line.Trim();
                                if (!string.IsNullOrEmpty(t))
                                    new Thread(() => TelegramSend(t)) { IsBackground = true }.Start();
                            }
                        }
                    }
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true }.Start();
    }

    #endregion

    #region Voice module — mic capture + VAD

    const int SAMPLE_RATE = 16000;
    const int BUF_SAMPLES = 4096;
    const int NUM_BUFS    = 2;

    static IntPtr     hWaveIn;
    static WAVEHDR[]  waveHeaders  = new WAVEHDR[NUM_BUFS];
    static GCHandle   headersPin;
    static GCHandle[] bufHandles   = new GCHandle[NUM_BUFS];
    static byte[][]   rawBufs      = new byte[NUM_BUFS][];
    static WaveInProc waveProc;

    static readonly List<short> audioBuf   = new List<short>();
    static float    silentAccum  = 0f;
    static bool     isRecording  = false;
    static readonly object audioLock = new object();
    static bool     micActive    = false;  // mic on/off state
    static bool     isSpeaking   = false;  // TTS playing — suppresses VAD
    static float    lastRms      = 0f;

    static void StartMic()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag      = 1,
            nChannels       = 1,
            nSamplesPerSec  = SAMPLE_RATE,
            nAvgBytesPerSec = SAMPLE_RATE * 2,
            nBlockAlign     = 2,
            wBitsPerSample  = 16,
            cbSize          = 0
        };
        waveProc = OnWaveData;
        waveInOpen(out hWaveIn, -1, ref fmt, waveProc, IntPtr.Zero, CALLBACK_FUNCTION);

        headersPin = GCHandle.Alloc(waveHeaders, GCHandleType.Pinned);
        for (int i = 0; i < NUM_BUFS; i++)
        {
            rawBufs[i]    = new byte[BUF_SAMPLES * 2];
            bufHandles[i] = GCHandle.Alloc(rawBufs[i], GCHandleType.Pinned);
            waveHeaders[i] = new WAVEHDR
            {
                lpData         = bufHandles[i].AddrOfPinnedObject(),
                dwBufferLength = BUF_SAMPLES * 2
            };
            waveInPrepareHeader(hWaveIn, ref waveHeaders[i], Marshal.SizeOf(waveHeaders[i]));
            waveInAddBuffer(hWaveIn, ref waveHeaders[i], Marshal.SizeOf(waveHeaders[i]));
        }
        waveInStart(hWaveIn);
    }

    static void StopMic()
    {
        waveInStop(hWaveIn);
        waveInClose(hWaveIn);
        if (headersPin.IsAllocated) headersPin.Free();
        foreach (var h in bufHandles) if (h.IsAllocated) h.Free();
    }

    static void OnWaveData(IntPtr hWi, int msg, IntPtr inst, ref WAVEHDR hdr, IntPtr p2)
    {
        try
        {
            if (msg != MM_WIM_DATA) return;

            int    samples = hdr.dwBytesRecorded / 2;
            var    chunk   = new short[samples];
            Marshal.Copy(hdr.lpData, chunk, 0, samples);
            waveInAddBuffer(hWi, ref hdr, Marshal.SizeOf(hdr));

            if (!micActive || isSpeaking || samples == 0) return;

            double sum = 0;
            foreach (var s in chunk) sum += (double)s * s;
            float rms = (float)Math.Sqrt(sum / samples) / 32768f;
            lastRms = rms;

            float chunkSec = samples / (float)SAMPLE_RATE;
            lock (audioLock)
            {
                if (rms > silenceRms)
                {
                    isRecording = true;
                    silentAccum = 0f;
                    audioBuf.AddRange(chunk);
                }
                else if (isRecording)
                {
                    audioBuf.AddRange(chunk);
                    silentAccum += chunkSec;
                    if (silentAccum >= silenceSec)
                    {
                        var audio   = audioBuf.ToArray();
                        audioBuf.Clear();
                        silentAccum = 0f;
                        isRecording = false;
                        float dur   = audio.Length / (float)SAMPLE_RATE;
                        if (dur >= minAudioSec)
                            new Thread(() => Transcribe(audio)) { IsBackground = true }.Start();
                    }
                }
            }
        }
        catch { }
    }

    static void ToggleMic()
    {
        micActive = !micActive;
        if (micActive)
        {
            StartMic();
            UpdateTrayText(GetTrayText());
            new Thread(() => InjectToClaude("[VP] mic on")) { IsBackground = true }.Start();
        }
        else
        {
            StopMic();
            UpdateTrayText(GetTrayText());
            new Thread(() => InjectToClaude("[VP] mic off")) { IsBackground = true }.Start();
        }
    }

    #endregion

    #region Voice module — STT (Whisper)

    static void Transcribe(short[] audio)
    {
        UpdateTrayText("CP | transcribing...");
        try
        {
            byte[] wav      = ToWavBytes(audio);
            string boundary = "----CPBoundary" + Guid.NewGuid().ToString("N");
            var    body     = new MemoryStream();
            Action<string> ws  = s => { var b = Encoding.ASCII.GetBytes(s); body.Write(b, 0, b.Length); };
            Action<string> wsu = s => { var b = Encoding.UTF8.GetBytes(s);  body.Write(b, 0, b.Length); };

            ws("--" + boundary + "\r\n");
            ws("Content-Disposition: form-data; name=\"model\"\r\n\r\n");
            ws(sttModel + "\r\n");
            if (!string.IsNullOrEmpty(language))
            {
                ws("--" + boundary + "\r\n");
                ws("Content-Disposition: form-data; name=\"language\"\r\n\r\n");
                ws(language + "\r\n");
            }
            ws("--" + boundary + "\r\n");
            ws("Content-Disposition: form-data; name=\"response_format\"\r\n\r\n");
            ws("verbose_json\r\n");
            ws("--" + boundary + "\r\n");
            ws("Content-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\n");
            ws("Content-Type: audio/wav\r\n\r\n");
            body.Write(wav, 0, wav.Length);
            ws("\r\n--" + boundary + "--\r\n");

            byte[] bodyBytes = body.ToArray();
            var    req = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/audio/transcriptions");
            req.Method        = "POST";
            req.ContentType   = "multipart/form-data; boundary=" + boundary;
            req.Headers.Add("Authorization", "Bearer " + voiceApiKey);
            req.ContentLength = bodyBytes.Length;
            req.Timeout       = 15000;
            using (var s = req.GetRequestStream()) s.Write(bodyBytes, 0, bodyBytes.Length);

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr   = new StreamReader(resp.GetResponseStream()))
            {
                var raw  = sr.ReadToEnd();
                var dict = json.Deserialize<Dictionary<string, object>>(raw);
                if (!dict.ContainsKey("text")) return;
                var text = dict["text"].ToString().Trim();
                if (string.IsNullOrEmpty(text)) return;

                // Filter hallucinations via no_speech_prob
                if (dict.ContainsKey("segments"))
                {
                    var segs = dict["segments"] as System.Collections.ArrayList;
                    if (segs != null && segs.Count > 0)
                    {
                        double total = 0;
                        foreach (Dictionary<string, object> seg in segs)
                            if (seg.ContainsKey("no_speech_prob"))
                                total += Convert.ToDouble(seg["no_speech_prob"]);
                        if (total / segs.Count > 0.4) return;
                    }
                }

                int letters = 0;
                foreach (char c in text) if (char.IsLetter(c)) letters++;
                if (letters < 3) return;

                string lower = text.ToLowerInvariant().Trim('.', '!', '?', ' ');
                foreach (string h in new[] { "thank you","thanks","bye","goodbye","see you","you","okay","ok",
                    "subtitles by the amara.org community","beep","silence","uh","um","hmm","mm","ah","oh" })
                    if (lower == h || lower.StartsWith("thank you from")) return;

                if (!string.IsNullOrEmpty(logFile))
                    try { File.AppendAllText(logFile, text + "\n", Encoding.UTF8); } catch { }

                InjectToClaude("[V] " + text);
            }
        }
        catch (Exception ex)
        {
            string err = "STT: " + ex.Message;
            if (!string.IsNullOrEmpty(logFile))
                try { File.AppendAllText(logFile, "[ERROR] " + err + "\n", Encoding.UTF8); } catch { }
            UpdateTrayText("CP | " + err.Substring(0, Math.Min(60, err.Length)));
            Thread.Sleep(3000);
        }
        finally { UpdateTrayText(GetTrayText()); }
    }

    static byte[] ToWavBytes(short[] samples)
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            int byteCount = samples.Length * 2;
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + byteCount);
            bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(SAMPLE_RATE); bw.Write(SAMPLE_RATE * 2);
            bw.Write((short)2); bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(byteCount);
            foreach (var s in samples) bw.Write(s);
            return ms.ToArray();
        }
    }

    #endregion

    #region Voice module — TTS (Edge)

    static readonly SemaphoreSlim ttsSem = new SemaphoreSlim(1, 1);

    static void Speak(string text)
    {
        if (!ttsSem.Wait(35000)) return;
        try   { isSpeaking = true; SpeakNative(text); }
        finally { isSpeaking = false; ttsSem.Release(); }
    }

    static string GenerateSecMsGec()
    {
        const string TOKEN    = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        const long   FIVE_MIN = 300L * 10000000L;
        long winTime = DateTime.UtcNow.ToFileTimeUtc();
        long rounded = winTime - (winTime % FIVE_MIN);
        string s = rounded.ToString() + TOKEN;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToUpper();
    }

    static void SpeakNative(string text)
    {
        const string UA    = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                             "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
        const string HOST  = "speech.platform.bing.com";
        const string TOKEN = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";

        IntPtr hSession = IntPtr.Zero, hConnect = IntPtr.Zero, hRequest = IntPtr.Zero, hWs = IntPtr.Zero;
        try
        {
            string connId = Guid.NewGuid().ToString("N");
            string muid   = Guid.NewGuid().ToString("N").ToUpper();
            string gec    = GenerateSecMsGec();
            string path   = "/consumer/speech/synthesize/readaloud/edge/v1" +
                            "?TrustedClientToken=" + TOKEN +
                            "&ConnectionId="       + connId +
                            "&Sec-MS-GEC="         + gec +
                            "&Sec-MS-GEC-Version=1-143.0.3650.75";

            hSession = WinHttpOpen(UA, WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, 0);
            if (hSession == IntPtr.Zero) throw new Exception("WinHttpOpen: " + Marshal.GetLastWin32Error());
            WinHttpSetTimeouts(hSession, 5000, 10000, 10000, 30000);

            hConnect = WinHttpConnect(hSession, HOST, 443, 0);
            if (hConnect == IntPtr.Zero) throw new Exception("WinHttpConnect: " + Marshal.GetLastWin32Error());

            hRequest = WinHttpOpenRequest(hConnect, "GET", path, null, null, IntPtr.Zero, WINHTTP_FLAG_SECURE);
            if (hRequest == IntPtr.Zero) throw new Exception("WinHttpOpenRequest: " + Marshal.GetLastWin32Error());

            WinHttpSetOption(hRequest, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, IntPtr.Zero, 0);

            string hdrs =
                "Origin: chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold\r\n" +
                "Pragma: no-cache\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Accept-Language: en-US,en;q=0.9\r\n" +
                "Cookie: muid=" + muid + ";\r\n";
            WinHttpAddRequestHeaders(hRequest, hdrs, (uint)hdrs.Length, WINHTTP_ADDREQ_FLAG_ADD);

            if (!WinHttpSendRequest(hRequest, null, 0, IntPtr.Zero, 0, 0, IntPtr.Zero))
                throw new Exception("WinHttpSendRequest: " + Marshal.GetLastWin32Error());
            if (!WinHttpReceiveResponse(hRequest, IntPtr.Zero))
                throw new Exception("WinHttpReceiveResponse: " + Marshal.GetLastWin32Error());

            hWs = WinHttpWebSocketCompleteUpgrade(hRequest, IntPtr.Zero);
            if (hWs == IntPtr.Zero) throw new Exception("CompleteUpgrade: " + Marshal.GetLastWin32Error());
            WinHttpCloseHandle(hRequest); hRequest = IntPtr.Zero;

            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            WsWinSend(hWs,
                "X-Timestamp:" + ts + "\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{" +
                "\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"true\"}," +
                "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}");

            string safe = text.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("\"","&quot;");
            WsWinSend(hWs,
                "X-RequestId:" + connId.ToUpper() + "\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                "X-Timestamp:" + ts + "\r\n" +
                "Path:ssml\r\n\r\n" +
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                "<voice name='" + ttsVoice + "'><prosody rate='+0%' pitch='+0Hz'>" +
                safe + "</prosody></voice></speak>");

            var mp3Queue   = new BlockingCollection<byte[]>(32);
            var hWsCapture = hWs;

            var recvThread = new Thread(() =>
            {
                try
                {
                    var  recvBuf = new byte[131072];
                    bool done    = false;
                    while (!done)
                    {
                        var  msgBuf  = new MemoryStream();
                        uint bufType = 0;
                        while (true)
                        {
                            uint read = 0;
                            uint err  = WinHttpWebSocketReceive(hWsCapture, recvBuf, (uint)recvBuf.Length, ref read, ref bufType);
                            if (err != 0) { done = true; break; }
                            if (read > 0) msgBuf.Write(recvBuf, 0, (int)read);
                            if (bufType == WS_UTF8_MESSAGE || bufType == WS_BINARY_MESSAGE || bufType == WS_CLOSE) break;
                        }
                        if (bufType == WS_CLOSE || done) break;
                        byte[] msg = msgBuf.ToArray();
                        if (bufType == WS_UTF8_MESSAGE)
                        {
                            if (Encoding.UTF8.GetString(msg).Contains("Path:turn.end")) done = true;
                        }
                        else if (bufType == WS_BINARY_MESSAGE && msg.Length > 2)
                        {
                            int hLen  = (msg[0] << 8) | msg[1];
                            int start = 2 + hLen;
                            if (start < msg.Length)
                            {
                                byte[] chunk = new byte[msg.Length - start];
                                Buffer.BlockCopy(msg, start, chunk, 0, chunk.Length);
                                mp3Queue.Add(chunk);
                            }
                        }
                    }
                }
                catch { }
                finally { mp3Queue.CompleteAdding(); }
            }) { IsBackground = true };
            recvThread.Start();

            IntPtr hWo = IntPtr.Zero;
            try
            {
                var src = new StreamingMp3Source(mp3Queue);
                MP3Stream mp3s;
                try { mp3s = new MP3Stream(src); }
                catch { recvThread.Join(5000); return; }

                int sampleRate = mp3s.Frequency;
                var fmt = new WAVEFORMATEX
                {
                    wFormatTag      = 1,
                    nChannels       = 2,
                    nSamplesPerSec  = sampleRate,
                    nAvgBytesPerSec = sampleRate * 2 * 2,
                    nBlockAlign     = 4,
                    wBitsPerSample  = 16,
                    cbSize          = 0
                };
                if (waveOutOpen(out hWo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                    throw new Exception("waveOutOpen failed");

                const int PCM_CHUNK = 32768;
                const int MAX_OUT   = 4;
                int hdrSz      = Marshal.SizeOf(typeof(WAVEHDR));
                int flagOffset = IntPtr.Size + 4 + 4 + IntPtr.Size;

                var    pending = new Queue<Tuple<IntPtr, GCHandle>>();
                byte[] readBuf = new byte[PCM_CHUNK];
                int    nRead;

                while ((nRead = mp3s.Read(readBuf, 0, readBuf.Length)) > 0)
                {
                    while (pending.Count >= MAX_OUT)
                    {
                        var front = pending.Peek();
                        while ((Marshal.ReadInt32(front.Item1, flagOffset) & 1) == 0)
                            Thread.Sleep(5);
                        waveOutUnprepareHeader(hWo, front.Item1, hdrSz);
                        Marshal.FreeHGlobal(front.Item1);
                        front.Item2.Free();
                        pending.Dequeue();
                    }
                    while (pending.Count > 0 && (Marshal.ReadInt32(pending.Peek().Item1, flagOffset) & 1) != 0)
                    {
                        var front = pending.Dequeue();
                        waveOutUnprepareHeader(hWo, front.Item1, hdrSz);
                        Marshal.FreeHGlobal(front.Item1);
                        front.Item2.Free();
                    }
                    byte[]   chunk = new byte[nRead];
                    Buffer.BlockCopy(readBuf, 0, chunk, 0, nRead);
                    GCHandle pin  = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                    IntPtr   pHdr = Marshal.AllocHGlobal(hdrSz);
                    for (int i = 0; i < hdrSz; i++) Marshal.WriteByte(pHdr, i, 0);
                    Marshal.WriteIntPtr(pHdr, 0,           pin.AddrOfPinnedObject());
                    Marshal.WriteInt32 (pHdr, IntPtr.Size, nRead);
                    waveOutPrepareHeader(hWo, pHdr, hdrSz);
                    waveOutWrite(hWo, pHdr, hdrSz);
                    pending.Enqueue(Tuple.Create(pHdr, pin));
                }
                mp3s.Close();

                foreach (var item in pending)
                {
                    while ((Marshal.ReadInt32(item.Item1, flagOffset) & 1) == 0)
                        Thread.Sleep(5);
                    waveOutUnprepareHeader(hWo, item.Item1, hdrSz);
                    Marshal.FreeHGlobal(item.Item1);
                    item.Item2.Free();
                }
            }
            finally { if (hWo != IntPtr.Zero) waveOutClose(hWo); }

            recvThread.Join(5000);
            try { WinHttpWebSocketClose(hWs, 1000, IntPtr.Zero, 0); } catch { }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(logFile))
                try { File.AppendAllText(logFile, "[ERROR] SpeakNative: " + ex.Message + "\n", Encoding.UTF8); } catch { }
        }
        finally
        {
            if (hRequest != IntPtr.Zero) WinHttpCloseHandle(hRequest);
            if (hWs      != IntPtr.Zero) WinHttpCloseHandle(hWs);
            if (hConnect != IntPtr.Zero) WinHttpCloseHandle(hConnect);
            if (hSession != IntPtr.Zero) WinHttpCloseHandle(hSession);
        }
    }

    static void WsWinSend(IntPtr hWs, string msg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg);
        uint   err   = WinHttpWebSocketSend(hWs, WS_UTF8_MESSAGE, bytes, (uint)bytes.Length);
        if (err != 0) throw new Exception("WsWinSend: " + err);
    }

    // Voice pipe server: Claude → TTS / pause / resume
    static void StartVoicePipeServer()
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PIPE_VOICE,
                        PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        pipe.WaitForConnection();
                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                var t = line.Trim();
                                if (string.IsNullOrEmpty(t)) continue;
                                if (t.Equals("pause", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (micActive) { micActive = false; StopMic(); UpdateTrayText(GetTrayText()); }
                                }
                                else if (t.Equals("resume", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!micActive) { micActive = true; StartMic(); UpdateTrayText(GetTrayText()); new Thread(() => Speak("Mic on.")) { IsBackground = true }.Start(); }
                                }
                                else
                                {
                                    string text = t;
                                    new Thread(() => Speak(text)) { IsBackground = true }.Start();
                                }
                            }
                        }
                    }
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true }.Start();
    }

    static void StartVoiceModule()
    {
        StartVoicePipeServer();
        // Mic starts paused; user enables via tray or "[V] включи микрофон"
    }

    #endregion

    #region Clipboard — screenshot

    static Bitmap BitmapFromDib(byte[] dib)
    {
        int   infoSize    = BitConverter.ToInt32(dib, 0);
        short bitCount    = BitConverter.ToInt16(dib, 14);
        int   compression = BitConverter.ToInt32(dib, 16);

        int paletteSize;
        if      (bitCount <= 8)    paletteSize = (1 << bitCount) * 4;
        else if (compression == 3) paletteSize = 12;
        else                       paletteSize = 0;

        int pixelOffset = 14 + infoSize + paletteSize;
        int fileSize    = 14 + dib.Length;

        byte[] bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        Array.Copy(BitConverter.GetBytes(fileSize),    0, bmp, 2,  4);
        Array.Copy(BitConverter.GetBytes(pixelOffset), 0, bmp, 10, 4);
        Array.Copy(dib, 0, bmp, 14, dib.Length);

        using (var ms  = new MemoryStream(bmp))
        using (var tmp = new Bitmap(ms))
            return new Bitmap(tmp);
    }

    static void PasteImageFromClipboard()
    {
        try
        {
            Image img = null;

            try { img = Clipboard.GetImage(); } catch { }

            if (img == null)
            {
                try
                {
                    var dataObj = Clipboard.GetDataObject();
                    if (dataObj != null && dataObj.GetDataPresent(DataFormats.Dib))
                    {
                        var ms = dataObj.GetData(DataFormats.Dib) as MemoryStream;
                        if (ms != null) img = BitmapFromDib(ms.ToArray());
                    }
                }
                catch { }
            }

            if (img == null)
            {
                bool opened = OpenClipboard(IntPtr.Zero);
                if (opened)
                {
                    try
                    {
                        IntPtr hDib = GetClipboardData(CF_DIB);
                        if (hDib != IntPtr.Zero)
                        {
                            IntPtr ptr = GlobalLock(hDib);
                            if (ptr != IntPtr.Zero)
                            {
                                try
                                {
                                    int    size = (int)GlobalSize(hDib).ToUInt64();
                                    byte[] dib  = new byte[size];
                                    Marshal.Copy(ptr, dib, 0, size);
                                    img = BitmapFromDib(dib);
                                }
                                finally { GlobalUnlock(hDib); }
                            }
                        }
                    }
                    finally { CloseClipboard(); }
                }
            }

            if (img == null) return;

            string dir  = Path.Combine(appDir, "inbox_media");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            InjectToClaude("[photo: " + path + "]");
        }
        catch (Exception ex)
        {
            InjectToClaude("[ClaudePusher] Screenshot error: " + ex.Message);
        }
    }

    #endregion

    #region HTTP

    static string HttpGet(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = req.ReadWriteTimeout = timeoutMs;
        using (var resp   = (HttpWebResponse)req.GetResponse())
        using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return reader.ReadToEnd();
    }

    #endregion

    #region Tray UI

    static NotifyIcon  tray;
    static Form        uiCtx;
    static SettingsForm settingsWindow;

    static string GetTrayText()
    {
        var sb = new StringBuilder(APP_NAME + " " + APP_VERSION);
        if (enableTelegram) sb.Append(" [TG]");
        if (enableVoice)    sb.Append(micActive ? " [MIC]" : " [mic off]");
        return sb.Length > 63 ? sb.ToString().Substring(0, 63) : sb.ToString();
    }

    static void UpdateTrayText(string text)
    {
        if (tray == null) return;
        if (text.Length > 63) text = text.Substring(0, 63);
        string t = text;
        if (uiCtx != null && uiCtx.IsHandleCreated)
            uiCtx.BeginInvoke(new Action(() => { if (tray != null) tray.Text = t; }));
        else
            tray.Text = t;
    }

    static void RebuildTrayMenu(ContextMenu menu)
    {
        menu.MenuItems.Clear();

        // Gradient header with app icon
        string icoPath = Path.Combine(appDir, "claude_icon.ico");
        var header = new MenuItem { OwnerDraw = true };
        header.MeasureItem += (s, e) =>
        {
            using (var fBold = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            {
                var sz = e.Graphics.MeasureString(APP_NAME + "  v" + APP_VERSION, fBold);
                e.ItemWidth  = Math.Max((int)sz.Width + 40, 200); // +40 for icon
                e.ItemHeight = Math.Max(32, (int)sz.Height + 12);
            }
        };
        header.DrawItem += (s, e) =>
        {
            var r = e.Bounds;
            using (var brush = new LinearGradientBrush(
                r, Color.FromArgb(255, 50, 120, 220), Color.FromArgb(255, 20, 70, 160),
                LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(brush, r);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw app icon on the left
            int iconSize = r.Height - 8;
            int iconX    = r.Left + 6;
            int iconY    = r.Top  + 4;
            try
            {
                if (File.Exists(icoPath))
                    using (var ico = new Icon(icoPath, iconSize, iconSize))
                        e.Graphics.DrawIcon(ico, new Rectangle(iconX, iconY, iconSize, iconSize));
            }
            catch { }

            // Draw text
            using (var f  = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            using (var b  = new SolidBrush(Color.White))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(APP_NAME + "  v" + APP_VERSION, f, b,
                    new RectangleF(iconX + iconSize + 6f, 0f, r.Width - iconX - iconSize - 12f, r.Height), sf);
        };
        menu.MenuItems.Add(header);
        menu.MenuItems.Add("-");

        // Claude windows list
        var windows = cachedWindows;
        if (windows.Count == 0)
        {
            menu.MenuItems.Add(new MenuItem("(no Claude Code windows)") { Enabled = false });
        }
        else
        {
            foreach (var pair in windows)
            {
                var  hwnd = pair.Key;
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

        // Send Screenshot (Telegram only)
        if (enableTelegram)
        {
            var ssItem = new MenuItem("Send Screenshot");
            ssItem.Click += (s, e) =>
            {
                if (IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_DIBSECTION))
                    PasteImageFromClipboard();
                else
                    MessageBox.Show("No image found in clipboard.\nUse PrtScr or snipping tool first.",
                        APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.MenuItems.Add(ssItem);
        }

        // Mic toggle (Voice only)
        if (enableVoice)
        {
            menu.MenuItems.Add("-");
            var micItem = new MenuItem(micActive ? "Pause Mic" : "Resume Mic");
            micItem.Click += (s, e) => ToggleMic();
            menu.MenuItems.Add(micItem);
        }

        menu.MenuItems.Add("-");

        var settingsItem = new MenuItem("Settings...");
        settingsItem.Click += (s, e) =>
        {
            if (uiCtx == null) return;
            uiCtx.Invoke(new Action(() =>
            {
                if (settingsWindow != null && !settingsWindow.IsDisposed)
                {
                    settingsWindow.BringToFront();
                    settingsWindow.Activate();
                    return;
                }
                settingsWindow = new SettingsForm();
                settingsWindow.ShowDialog();
                settingsWindow = null;
            }));
        };
        menu.MenuItems.Add(settingsItem);

        menu.MenuItems.Add("-");

        menu.MenuItems.Add(new MenuItem("Exit", (s, e) =>
        {
            tray.Visible = false;
            Application.Exit();
        }));
    }

    #endregion

    #region ClipboardListenerForm

    class ClipboardListenerForm : Form
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);

        public ClipboardListenerForm()
        {
            ShowInTaskbar  = false;
            WindowState    = FormWindowState.Minimized;
            Load += (s, e) =>
            {
                Hide();
                AddClipboardFormatListener(Handle);
            };
            FormClosing += (s, e) => RemoveClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) { /* clipboard change detected — no action needed here */ }
            base.WndProc(ref m);
        }
    }

    #endregion

    #region Main

    static Mutex instanceMutex;

    [STAThread]
    static void Main()
    {
        try { SetProcessDpiAwareness(1); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        appDir     = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        configFile = Path.Combine(appDir, "config.json");

        bool createdNew;
        instanceMutex = new Mutex(true, "ClaudePusher_SingleInstance", out createdNew);
        if (!createdNew) { instanceMutex.Dispose(); return; }

        if (!LoadConfig())
        {
            MessageBox.Show(
                "Config not found or incomplete.\n\nCreate config.json next to ClaudePusher.exe,\nor launch with --setup.",
                APP_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // Still run so Settings can be opened via first-time wizard
            // For now, just exit
            instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
            return;
        }

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        var uiForm = new ClipboardListenerForm();
        uiForm.Load += (s, e) =>
        {
            uiCtx = uiForm;

            tray = new NotifyIcon
            {
                Visible = true,
                Text    = GetTrayText()
            };

            // Load icon
            string icoPath = Path.Combine(appDir, "claude_icon.ico");
            if (File.Exists(icoPath))
                tray.Icon = new Icon(icoPath, 32, 32);
            else
                tray.Icon = SystemIcons.Application;

            var menu = new ContextMenu();
            menu.Popup += (ms, me) => RebuildTrayMenu(menu);
            tray.ContextMenu  = menu;
            tray.DoubleClick += (ts, te) =>
            {
                if (enableVoice) ToggleMic();
            };

            // RMS diagnostic tooltip when mic is on
            if (enableVoice)
            {
                var diagTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                diagTimer.Tick += (ts, te) =>
                {
                    if (micActive && !isSpeaking)
                        UpdateTrayText(string.Format("CP | RMS:{0:F4} thr:{1:F4}{2}",
                            lastRms, silenceRms, isRecording ? " [REC]" : ""));
                };
                diagTimer.Start();
            }

            // Start modules
            if (enableTelegram) StartTelegramModule();
            if (enableVoice)    StartVoiceModule();

            // Window cache refresh
            new Thread(() =>
            {
                while (true)
                {
                    try { cachedWindows = FindClaudeWindows(); } catch { }
                    Thread.Sleep(3000);
                }
            }) { IsBackground = true }.Start();
        };

        Application.Run(uiForm);

        if (enableVoice && micActive) StopMic();
        tray.Visible = false;
        instanceMutex.ReleaseMutex();
    }

    #endregion
}

// ─── MP3 streaming helper ────────────────────────────────────────────────────

class StreamingMp3Source : Stream
{
    readonly BlockingCollection<byte[]> _q;
    byte[] _current;
    int    _pos;

    public StreamingMp3Source(BlockingCollection<byte[]> q) { _q = q; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_current == null || _pos >= _current.Length)
        {
            try   { _current = _q.Take(); _pos = 0; }
            catch (InvalidOperationException) { return 0; }
        }
        int n = Math.Min(count, _current.Length - _pos);
        Buffer.BlockCopy(_current, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override bool CanRead  { get { return true;  } }
    public override bool CanSeek  { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length   { get { throw new NotSupportedException(); } }
    public override long Position
    {
        get { throw new NotSupportedException(); }
        set { throw new NotSupportedException(); }
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}

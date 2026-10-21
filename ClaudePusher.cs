// ClaudePusher 2.3 — unified Telegram + Voice interface for Claude Code.
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
using System.Linq;
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

[assembly: AssemblyVersion("2.3.0.0")]
[assembly: AssemblyFileVersion("2.3.0.0")]
[assembly: AssemblyProduct("ClaudePusher")]
[assembly: AssemblyDescription("Telegram + Voice interface for Claude Code")]
[assembly: AssemblyCopyright("(c) 2026 sensboston")]

// ─── Settings dialog ────────────────────────────────────────────────────────

class SettingsForm : Form
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    const int LBL_W  = 110;
    const int CTRL_X = 128;

    ComboBox cbEncoding;
    TrackBar trkIdle, trkRms, trkSilSec, trkBarge, trkMinSpeech;
    Label    lblIdleVal, lblRmsVal, lblSilSecVal, lblBargeVal, lblMinSpeechVal;
    CheckBox chkAutoStart, chkVoice, chkMicOnStart;
    TextBox  tbToken, tbChatId;
    TextBox  tbApiKey, tbSttModel;
    ComboBox cbTtsVoice;

    // Edge voices that handle both Russian and English (multilingual) plus native Russian ones
    static readonly string[][] TtsVoices = {
        new[] { "en-US-BrianMultilingualNeural",    "male" },
        new[] { "en-US-AndrewMultilingualNeural",   "male" },
        new[] { "en-AU-WilliamMultilingualNeural",  "male" },
        new[] { "fr-FR-RemyMultilingualNeural",     "male" },
        new[] { "de-DE-FlorianMultilingualNeural",  "male" },
        new[] { "it-IT-GiuseppeMultilingualNeural", "male" },
        new[] { "ko-KR-HyunsuMultilingualNeural",   "male" },
        new[] { "ru-RU-DmitryNeural",               "male, Russian only" },
        new[] { "en-US-AvaMultilingualNeural",      "female" },
        new[] { "en-US-EmmaMultilingualNeural",     "female" },
        new[] { "fr-FR-VivienneMultilingualNeural", "female" },
        new[] { "de-DE-SeraphinaMultilingualNeural","female" },
        new[] { "pt-BR-ThalitaMultilingualNeural",  "female" },
        new[] { "ru-RU-SvetlanaNeural",             "female, Russian only" },
    };
    Label    lblKeyStatus;

    public SettingsForm()
    {
        SuspendLayout();
        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text            = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = MinimizeBox = false;
        StartPosition   = FormStartPosition.CenterScreen;

        const int TAB_W = 564;
        var tabs = new TabControl
        {
            Left = 0, Top = 0, Width = TAB_W,
            SizeMode  = TabSizeMode.Fixed,
            ItemSize  = new Size(TAB_W / 6, 26),
            DrawMode  = TabDrawMode.OwnerDrawFixed,
        };
        tabs.DrawItem += (s, e) =>
        {
            var tc  = (TabControl)s;
            bool sel = e.Index == tc.SelectedIndex;
            Color bg = sel ? SystemColors.Control : Color.FromArgb(110, 110, 110);
            Color fg = sel ? Color.Black : Color.White;
            using (var br = new SolidBrush(bg)) e.Graphics.FillRectangle(br, e.Bounds);
            string text  = tc.TabPages[e.Index].Text;
            Font   font  = sel ? new Font(tc.Font, FontStyle.Bold) : tc.Font;
            SizeF  sz    = e.Graphics.MeasureString(text, font);
            using (var fr = new SolidBrush(fg))
                e.Graphics.DrawString(text, font, fr,
                    e.Bounds.X + (e.Bounds.Width  - sz.Width)  / 2f,
                    e.Bounds.Y + (e.Bounds.Height - sz.Height) / 2f);
        };

        // ── Tab: General ─────────────────────────────────────────────────────
        var tpG = new TabPage("General");
        int y = 28;

        cbEncoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var enc in ClaudePusher.Encodings) cbEncoding.Items.Add(enc);
        cbEncoding.SelectedItem = ClaudePusher.encoding;
        if (cbEncoding.SelectedIndex < 0) cbEncoding.SelectedIndex = 0;
        y = AddRow(tpG, y, "Encoding:", cbEncoding, 280);

        int idleVal = Math.Max(3, Math.Min(30, ClaudePusher.idleThreshold));
        trkIdle    = new TrackBar { Minimum = 3, Maximum = 30, Value = idleVal, TickFrequency = 3, SmallChange = 1, Left = CTRL_X, Top = y, Width = 340, AutoSize = false, Height = 45 };
        lblIdleVal = new Label   { Text = idleVal + " min", Left = CTRL_X + 344, Top = y + 3, Width = 60 };
        trkIdle.ValueChanged += (s, e) => lblIdleVal.Text = trkIdle.Value + " min";
        tpG.Controls.AddRange(new Control[] { new Label { Text = "Inactivity alert:", Left = 12, Top = y + 3, Width = LBL_W }, trkIdle, lblIdleVal });
        y += 50;

        chkAutoStart = new CheckBox { Text = "Start with Windows", Checked = ClaudePusher.GetAutoStart(), Left = 12, Top = y, AutoSize = true };
        tpG.Controls.Add(chkAutoStart); y += 50;

        bool hasKey = !string.IsNullOrEmpty(ClaudePusher.voiceApiKey);
        chkVoice = new CheckBox { Text = "Enable Voice", Checked = ClaudePusher.enableVoice && hasKey, Enabled = hasKey, Left = 12, Top = y, AutoSize = true };
        tpG.Controls.Add(chkVoice); y += 50;

        chkMicOnStart = new CheckBox { Text = "Mic on at startup", Checked = ClaudePusher.micOnStart, Left = 12, Top = y, AutoSize = true };
        tpG.Controls.Add(chkMicOnStart); y += 50;

        tpG.BackColor = SystemColors.Control;
        tabs.TabPages.Add(tpG);

        // ── Tab: Telegram ────────────────────────────────────────────────────
        var tpT = new TabPage("Telegram");
        y = 28;
        tbToken  = new TextBox { Text = ClaudePusher.tgToken };
        tbChatId = new TextBox { Text = ClaudePusher.chatId };
        y = AddRow(tpT, y, "Bot token:", tbToken,  400);
        y = AddRow(tpT, y, "Chat ID:",   tbChatId, 150);
        tpT.BackColor = SystemColors.Control;
        tabs.TabPages.Add(tpT);

        // ── Tab: Voice ───────────────────────────────────────────────────────
        var tpV = new TabPage("Voice");
        y = 28;

        // API key row: [textbox] [👁 outside, flush right] [gap] [Test]
        const int TRK_W = 390;
        const int BTN_TEST_W = 50;
        const int EYE_W = 22;
        tbApiKey = new TextBox { Text = ClaudePusher.voiceApiKey, UseSystemPasswordChar = true, Left = CTRL_X, Top = y, Width = TRK_W - EYE_W - 4 - BTN_TEST_W };
        var btnEye = new Button {
            Text = "👁", Left = CTRL_X + tbApiKey.Width, Top = y, Width = EYE_W, Height = 23,
            FlatStyle = FlatStyle.Flat, Font = new System.Drawing.Font("Segoe UI Emoji", 9f),
            Padding = System.Windows.Forms.Padding.Empty, TextAlign = ContentAlignment.MiddleCenter
        };
        btnEye.Click += (s, e) => tbApiKey.UseSystemPasswordChar = !tbApiKey.UseSystemPasswordChar;
        lblKeyStatus = new Label { Text = "", Left = 0, Top = 0, Width = 0, Height = 0 }; // hidden, feedback via btnTest
        var btnTest  = new Button { Text = "Test", Left = CTRL_X + TRK_W - BTN_TEST_W, Top = y, Width = BTN_TEST_W, Height = 23 };
        btnTest.Click += OnTestApiKey;
        tpV.Controls.AddRange(new Control[] { new Label { Text = "OpenAI API key:", Left = 12, Top = y + 3, Width = LBL_W }, tbApiKey, btnEye, btnTest });
        y += 50;

        tbSttModel = new TextBox { Text = ClaudePusher.sttModel };
        y = AddRow(tpV, y, "STT model:", tbSttModel, 100);

        // Voice list is editable: any other Edge voice name can be typed in
        cbTtsVoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Left = CTRL_X, Top = y, Width = TRK_W - BTN_TEST_W - 4 };
        foreach (var v in TtsVoices) cbTtsVoice.Items.Add(v[0] + "  (" + v[1] + ")");
        int voiceIdx = Array.FindIndex(TtsVoices, v => v[0] == ClaudePusher.ttsVoice);
        if (voiceIdx >= 0) cbTtsVoice.SelectedIndex = voiceIdx; else cbTtsVoice.Text = ClaudePusher.ttsVoice;
        var btnPreview = new Button { Text = "▶", Left = CTRL_X + TRK_W - BTN_TEST_W, Top = y, Width = BTN_TEST_W, Height = 23 };
        btnPreview.Click += (s, e) =>
        {
            string voice = SelectedTtsVoice();
            new Thread(() => ClaudePusher.Speak("Привет, это мой голос. Hello, this is my voice.", voice)) { IsBackground = true }.Start();
        };
        tpV.Controls.AddRange(new Control[] { new Label { Text = "TTS voice:", Left = 12, Top = y + 3, Width = LBL_W }, cbTtsVoice, btnPreview });
        y += 50;

        int rmsVal = Math.Max(1, Math.Min(50, (int)Math.Round(ClaudePusher.silenceRms * 1000)));
        trkRms    = new TrackBar { Minimum = 1, Maximum = 50, Value = rmsVal, TickFrequency = 5, SmallChange = 1, Left = CTRL_X, Top = y, Width = TRK_W - 64, AutoSize = false, Height = 45 };
        lblRmsVal = new Label   { Text = (rmsVal / 1000.0).ToString("F3"), Left = CTRL_X + TRK_W - 60, Top = y + 3, Width = 60 };
        trkRms.ValueChanged += (s, e) => lblRmsVal.Text = (trkRms.Value / 1000.0).ToString("F3");
        tpV.Controls.AddRange(new Control[] { new Label { Text = "Silence RMS:", Left = 12, Top = y + 3, Width = LBL_W }, trkRms, lblRmsVal });
        y += 50;

        // Voice louder than this interrupts TTS (0 = off); must stay above the speakers' echo level
        int bargeVal = Math.Max(0, Math.Min(100, (int)Math.Round(ClaudePusher.bargeInRms * 1000)));
        Func<int, string> bargeText = v => v == 0 ? "off" : (v / 1000.0).ToString("F3");
        trkBarge    = new TrackBar { Minimum = 0, Maximum = 100, Value = bargeVal, TickFrequency = 10, SmallChange = 1, Left = CTRL_X, Top = y, Width = TRK_W - 64, AutoSize = false, Height = 45 };
        lblBargeVal = new Label   { Text = bargeText(bargeVal), Left = CTRL_X + TRK_W - 60, Top = y + 3, Width = 60 };
        trkBarge.ValueChanged += (s, e) => lblBargeVal.Text = bargeText(trkBarge.Value);
        tpV.Controls.AddRange(new Control[] { new Label { Text = "Barge-in RMS:", Left = 12, Top = y + 3, Width = LBL_W }, trkBarge, lblBargeVal });
        y += 50;

        // Phrases whose loudest part stays below this are background speech (another room, TV); 0 = off
        int minSpeechVal = Math.Max(0, Math.Min(100, (int)Math.Round(ClaudePusher.minSpeechRms * 1000)));
        trkMinSpeech    = new TrackBar { Minimum = 0, Maximum = 100, Value = minSpeechVal, TickFrequency = 10, SmallChange = 1, Left = CTRL_X, Top = y, Width = TRK_W - 64, AutoSize = false, Height = 45 };
        lblMinSpeechVal = new Label   { Text = bargeText(minSpeechVal), Left = CTRL_X + TRK_W - 60, Top = y + 3, Width = 60 };
        trkMinSpeech.ValueChanged += (s, e) => lblMinSpeechVal.Text = bargeText(trkMinSpeech.Value);
        tpV.Controls.AddRange(new Control[] { new Label { Text = "Min speech RMS:", Left = 12, Top = y + 3, Width = LBL_W }, trkMinSpeech, lblMinSpeechVal });
        y += 50;

        int silVal = Math.Max(3, Math.Min(30, (int)Math.Round(ClaudePusher.silenceSec * 10)));
        trkSilSec    = new TrackBar { Minimum = 3, Maximum = 30, Value = silVal, TickFrequency = 3, SmallChange = 1, Left = CTRL_X, Top = y, Width = TRK_W - 64, AutoSize = false, Height = 45 };
        lblSilSecVal = new Label   { Text = (silVal / 10.0).ToString("F1") + " s", Left = CTRL_X + TRK_W - 60, Top = y + 3, Width = 60 };
        trkSilSec.ValueChanged += (s, e) => lblSilSecVal.Text = (trkSilSec.Value / 10.0).ToString("F1") + " s";
        tpV.Controls.AddRange(new Control[] { new Label { Text = "Silence duration:", Left = 12, Top = y + 3, Width = LBL_W }, trkSilSec, lblSilSecVal });
        y += 50;

        tpV.BackColor = SystemColors.Control;
        tabs.TabPages.Add(tpV);

        // Enable chkVoice when API key is entered
        tbApiKey.TextChanged += (s, e) => chkVoice.Enabled = !string.IsNullOrWhiteSpace(tbApiKey.Text);

        // Fix ItemSize after DPI scaling (ItemSize is not auto-scaled by WinForms)
        Load += (s, e) =>
        {
            float scale   = (float)tabs.Width / TAB_W;
            tabs.ItemSize = new Size((int)(94 * scale), (int)(27 * scale));
        };

        tabs.Height = y + 52;   // tallest tab content + tab header + padding

        // ── Buttons ──────────────────────────────────────────────────────────
        ClientSize = new Size(tabs.Width, tabs.Bottom + 46);
        int btnTop    = tabs.Bottom + 10;
        var btnOk     = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Left = ClientSize.Width - 176, Top = btnTop, Width = 80 };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = ClientSize.Width - 88,  Top = btnTop, Width = 80 };
        btnOk.Click += OnOk;
        Controls.AddRange(new Control[] { tabs, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        ResumeLayout(false);
        PerformLayout();
    }

    // Voice name from the combo: list items carry a "  (male)" style suffix
    string SelectedTtsVoice()
    {
        string text = cbTtsVoice.Text.Trim();
        int sp = text.IndexOf(' ');
        return sp > 0 ? text.Substring(0, sp) : text;
    }

    static int AddRow(TabPage tab, int y, string label, Control ctrl, int ctrlWidth)
    {
        var lbl = new Label { Text = label, Left = 12, Top = y + 3, Width = LBL_W, AutoSize = false };
        ctrl.Left  = CTRL_X;
        ctrl.Top   = y;
        ctrl.Width = ctrlWidth;
        tab.Controls.AddRange(new Control[] { lbl, ctrl });
        return y + 50;
    }

    void OnTestApiKey(object sender, EventArgs e)
    {
        string key = tbApiKey.Text.Trim();
        var btn = (Button)sender;
        if (string.IsNullOrEmpty(key)) { btn.Text = "?"; btn.ForeColor = SystemColors.GrayText; return; }
        btn.Text = "..."; btn.ForeColor = SystemColors.GrayText; btn.Enabled = false;
        new Thread(() =>
        {
            string msg; Color col;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.openai.com/v1/models");
                req.Headers["Authorization"] = "Bearer " + key;
                req.Timeout = 8000;
                using (req.GetResponse()) { }
                msg = "✓"; col = Color.Green;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && (int)r.StatusCode == 401)
            {
                msg = "✗"; col = Color.Red;
            }
            catch
            {
                msg = "✗"; col = Color.Red;
            }
            if (!IsDisposed) BeginInvoke((Action)(() =>
            {
                btn.Text = msg; btn.ForeColor = col; btn.Enabled = true;
                var t = new System.Threading.Timer(_ => { if (!IsDisposed) BeginInvoke((Action)(() => { btn.Text = "Test"; btn.ForeColor = SystemColors.ControlText; })); }, null, 3000, System.Threading.Timeout.Infinite);
            }));
        }) { IsBackground = true }.Start();
    }

    void OnOk(object sender, EventArgs e)
    {
        ClaudePusher.encoding      = cbEncoding.SelectedItem?.ToString() ?? ClaudePusher.encoding;
        ClaudePusher.idleThreshold = trkIdle.Value;
        ClaudePusher.SetAutoStartSilent(chkAutoStart.Checked);

        bool voiceChanged        = chkVoice.Checked != ClaudePusher.enableVoice;
        ClaudePusher.enableVoice = chkVoice.Enabled && chkVoice.Checked;
        ClaudePusher.micOnStart  = chkMicOnStart.Checked;
        ClaudePusher.tgToken     = tbToken.Text.Trim();
        ClaudePusher.chatId      = tbChatId.Text.Trim();
        ClaudePusher.voiceApiKey = tbApiKey.Text.Trim();
        ClaudePusher.sttModel    = tbSttModel.Text.Trim();
        ClaudePusher.ttsVoice    = SelectedTtsVoice();
        ClaudePusher.silenceRms  = trkRms.Value / 1000f;
        ClaudePusher.bargeInRms  = trkBarge.Value / 1000f;
        ClaudePusher.minSpeechRms = trkMinSpeech.Value / 1000f;
        ClaudePusher.silenceSec  = trkSilSec.Value / 10f;

        ClaudePusher.SaveConfig();

        if (voiceChanged)
            ClaudePusher.ApplyVoiceChange();
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
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out TrayRect rect);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct TrayRect { public int Left, Top, Right, Bottom; }

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
    public static bool   micOnStart     = true;

    // Telegram
    public static string tgToken = "";
    public static string chatId  = "";

    // Voice
    public static string voiceApiKey = "";
    public static string sttModel    = "whisper-1";
    public static string language    = "";
    public static float  silenceRms  = 0.005f;
    public static float  bargeInRms  = 0.025f;   // voice level that interrupts TTS; 0 = disabled
    public static float  minSpeechRms = 0.03f;   // phrases with a quieter peak are ignored; 0 = disabled
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
                if (general.ContainsKey("mic_on_start"))           micOnStart     = Convert.ToBoolean(general["mic_on_start"]);
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
                if (voice.ContainsKey("barge_in_rms")) float.TryParse(voice["barge_in_rms"].ToString(), out bargeInRms);
                if (voice.ContainsKey("min_speech_rms")) float.TryParse(voice["min_speech_rms"].ToString(), out minSpeechRms);
                if (voice.ContainsKey("silence_sec"))  float.TryParse(voice["silence_sec"].ToString(), out silenceSec);
                if (voice.ContainsKey("min_audio_sec")) float.TryParse(voice["min_audio_sec"].ToString(), out minAudioSec);
                if (voice.ContainsKey("tts_voice"))    ttsVoice    = voice["tts_voice"].ToString();
                if (voice.ContainsKey("log_file"))     logFile     = voice["log_file"].ToString();
                var levels = voice.ContainsKey("echo_levels") ? voice["echo_levels"] as Dictionary<string, object> : null;
                if (levels != null)
                    lock (echoLevels)
                        foreach (var kv in levels) echoLevels[kv.Key] = Convert.ToSingle(kv.Value);
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
                        { "enable_voice",           enableVoice    },
                        { "mic_on_start",           micOnStart     }
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
                        { "barge_in_rms", bargeInRms  },
                        { "min_speech_rms", minSpeechRms },
                        { "silence_sec",  silenceSec  },
                        { "min_audio_sec", minAudioSec },
                        { "tts_voice",    ttsVoice    },
                        { "log_file",     logFile     },
                        { "echo_levels",  EchoLevelsSnapshot() }
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
    static float    segmentPeak  = 0f;   // loudest chunk of the phrase being recorded
    static bool     isRecording  = false;
    static readonly object audioLock = new object();
    static bool     voicePipeStarted = false;
    static bool     micActive    = false;  // mic on/off state
    static bool     isSpeaking   = false;  // TTS playing — suppresses VAD
    static float    lastRms      = 0f;

    // Barge-in: user's voice (louder than the speakers' echo) stops TTS and starts recording
    static volatile bool ttsAbort = false;
    static int      bargeInHits  = 0;   // last 3 chunks as bits: 1 = louder than bargeInRms
    static readonly Queue<short[]> bargeInPre = new Queue<short[]>();   // ~0.5 s before the trigger

    // Text being spoken, to drop its echo if it still gets transcribed
    static string   ttsText      = "";
    static DateTime ttsEndedAt   = DateTime.MinValue;

    // Self-tuning barge-in: echo level of each voice is learned from phrases played to the end
    static string   ttsVoiceNow  = "";
    static readonly List<float> ttsEchoRms = new List<float>();               // mic level during the current phrase
    static readonly Dictionary<string, float> echoLevels = new Dictionary<string, float>();  // voice -> echo level (95th pct)
    const float BARGE_IN_UNLEARNED = 0.06f;   // conservative threshold until a voice's echo is known

    static float BargeInThreshold()
    {
        float echo;
        lock (echoLevels)
            if (!echoLevels.TryGetValue(ttsVoiceNow, out echo)) return Math.Max(bargeInRms, BARGE_IN_UNLEARNED);
        return Math.Max(bargeInRms, echo * 2f);
    }

    static void LearnEcho(string voice)
    {
        float[] v;
        lock (ttsEchoRms) v = ttsEchoRms.ToArray();
        if (v.Length < 8) return;   // phrase shorter than ~2 s: not enough data
        Array.Sort(v);
        float p95 = v[(int)(v.Length * 0.95)];
        float level;
        lock (echoLevels)
        {
            float prev;
            level = echoLevels.TryGetValue(voice, out prev) ? prev * 0.7f + p95 * 0.3f : p95;
            echoLevels[voice] = level;
        }
        LogVoice(string.Format("[TTS] echo {0:F4} (phrase {1:F4}) for {2}, barge-in threshold {3:F4}",
            level, p95, voice, Math.Max(bargeInRms, level * 2f)));
        SaveConfig();
    }

    static Dictionary<string, float> EchoLevelsSnapshot()
    {
        lock (echoLevels) return new Dictionary<string, float>(echoLevels);
    }

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

            if (!micActive || samples == 0) return;

            double sum = 0;
            foreach (var s in chunk) sum += (double)s * s;
            float rms = (float)Math.Sqrt(sum / samples) / 32768f;

            if (isSpeaking && !ttsAbort)
            {
                // 2 loud chunks out of the last 3, so a short pause between words doesn't reset detection
                lock (ttsEchoRms) ttsEchoRms.Add(rms);
                bargeInHits = ((bargeInHits << 1) | (bargeInRms > 0 && rms > BargeInThreshold() ? 1 : 0)) & 7;
                int loud = (bargeInHits & 1) + ((bargeInHits >> 1) & 1) + ((bargeInHits >> 2) & 1);
                if (loud < 2)
                {
                    bargeInPre.Enqueue(chunk);
                    while (bargeInPre.Count > 2) bargeInPre.Dequeue();
                    return;
                }

                // Voice detected: stop speaking and record from ~0.5 s back
                ttsAbort    = true;
                bargeInHits = 0;
                LogVoice("[TTS] " + DateTime.Now.ToString("HH:mm:ss.fff") + " barge-in");
                lock (audioLock)
                {
                    audioBuf.Clear();
                    foreach (var pre in bargeInPre) audioBuf.AddRange(pre);
                    isRecording = true;
                    silentAccum = 0f;
                    segmentPeak = 0f;
                }
                bargeInPre.Clear();
                // the current chunk goes through the regular VAD below
            }

            lastRms = rms;

            float chunkSec = samples / (float)SAMPLE_RATE;
            lock (audioLock)
            {
                if (rms > silenceRms)
                {
                    if (!isRecording) segmentPeak = 0f;
                    isRecording = true;
                    silentAccum = 0f;
                    segmentPeak = Math.Max(segmentPeak, rms);
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
                        // Quiet phrase (other room, TV, far away): not addressed to us, don't send to STT
                        if (minSpeechRms > 0 && segmentPeak < minSpeechRms)
                            LogVoice(string.Format("[QUIET] dropped {0:F1} s, peak {1:F4}", dur, segmentPeak));
                        else if (dur >= minAudioSec)
                        {
                            LogVoice(string.Format("[SPEECH] {0:F1} s, peak {1:F4}", dur, segmentPeak));
                            new Thread(() => Transcribe(audio)) { IsBackground = true }.Start();
                        }
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

                if (IsTtsEcho(text)) { LogVoice("[ECHO] dropped: " + text); return; }

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

    public static void Speak(string text, string voice = null)
    {
        if (!ttsSem.Wait(35000)) { LogVoice("[TTS] busy, phrase skipped"); return; }
        var sw = Stopwatch.StartNew();
        string speakVoice = voice ?? ttsVoice;
        lock (ttsEchoRms) ttsEchoRms.Clear();
        try   { ttsAbort = false; bargeInHits = 0; ttsText = text; ttsVoiceNow = speakVoice; isSpeaking = true; SpeakNative(text, speakVoice); }
        finally
        {
            bool aborted = ttsAbort;
            isSpeaking = false; ttsAbort = false; ttsEndedAt = DateTime.UtcNow; ttsSem.Release();
            LogVoice(string.Format("[TTS] {0} in {1} ms", aborted ? "interrupted" : "done", sw.ElapsedMilliseconds));
            if (!aborted) LearnEcho(speakVoice);   // interrupted phrases contain the user's voice, not just echo
        }
    }

    // True if the transcript is (mostly) the phrase just spoken, i.e. the speakers' echo
    static bool IsTtsEcho(string transcript)
    {
        if (string.IsNullOrEmpty(ttsText)) return false;
        if (!isSpeaking && (DateTime.UtcNow - ttsEndedAt).TotalSeconds > 15) return false;

        Func<string, string[]> words = s => Regex.Split(s.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+")
                                                 .Where(w => w.Length > 1).ToArray();
        string[] heard = words(transcript);
        if (heard.Length < 3) return false;
        var spoken = new HashSet<string>(words(ttsText));
        int matched = heard.Count(w => spoken.Contains(w));
        return matched >= heard.Length * 0.7;
    }

    static void LogVoice(string line)
    {
        if (!string.IsNullOrEmpty(logFile))
            try { File.AppendAllText(logFile, line + "\n", Encoding.UTF8); } catch { }
    }

    // Wait until waveOut marks the buffer done (WHDR_DONE); false on timeout
    static bool WaitWaveOutDone(IntPtr pHdr, int flagOffset, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while ((Marshal.ReadInt32(pHdr, flagOffset) & 1) == 0)
        {
            if (ttsAbort || sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    // Token format v2: "<CS64, 16 hex>-<SHA256, 64 hex>", both computed over "<5-min rounded FILETIME><TOKEN>".
    // SHA256 here hashes a 57-byte secret prefix + data; the prefix is baked into the precomputed state below.

    static readonly uint[] GecShaInitial = {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
    static readonly uint[] GecShaState = {
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2 };
    static readonly uint[] GecShaSchedule = {
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564 };
    static readonly uint[] GecShaK = {
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2 };
    const uint GecShaAdjustment = 0xC9DC06A7;
    const uint GecSigma0Adjustment = 0x059C0B80;
    const uint GecSigma1Adjustment = 0x000B92C0;

    static readonly uint[] GecCs64Coefficients = {
        0xE82E91B1, 0x44DE24C6, 0x3BEB65BC, 0xC89C5933, 0x8D1EC211, 0x314EF5D5, 0xE92491B1,
        0xF0A5B9D5, 0x06064CF3, 0xDA79FA60, 0xBED20560, 0x89F9C9F8, 0x35FA38C6, 0xCB49E3C6 };
    static readonly uint[] GecCs64Offsets = {
        0xB32700D0, 0x88DFF51D, 0x29514038, 0xB328CBDF, 0xD405B4D5, 0x26E55F3C, 0x607801C6,
        0x82B2C1FB, 0xFA51A3DA, 0x207CC464, 0x3C3C74F3, 0xD2D14B61, 0xBB04A2C1, 0xC5626167 };
    const uint GecCs64Factor = 0x82893933;
    const uint GecCs64Adjustment0 = 0xF2F90B1A;
    const uint GecCs64Adjustment1 = 0x0B1A0000;

    static uint GecRol16(uint v) { return (v << 16) | (v >> 16); }
    static uint GecRotr(uint v, int n) { return (v >> n) | (v << (32 - n)); }
    static uint GecSigma0(uint v) { return (v >> 7) ^ (v << 25) ^ (v >> 18) ^ (v << 14) ^ (v >> 3); }
    static uint GecSigma1(uint v) { return (v >> 17) ^ (v << 15) ^ (v >> 19) ^ (v << 13) ^ (v >> 10); }

    static void GecExpandSchedule(uint[] w, int firstWord)
    {
        for (int i = firstWord; i < 64; i++)
            w[i] = GecSigma1(w[i - 2]) + w[i - 7] + GecSigma0(w[i - 15]) + w[i - 16];
    }

    static uint[] GecShaRounds(uint[] state, uint[] w, int firstRound)
    {
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        uint e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = firstRound; i < 64; i++)
        {
            // The resumed first block uses an adjusted constant at round 14
            uint k = (firstRound == 14 && i == 14) ? GecShaAdjustment : GecShaK[i];
            uint t1 = h + (GecRotr(e, 6) ^ GecRotr(e, 11) ^ GecRotr(e, 25)) + (g ^ (e & (f ^ g))) + k + w[i];
            uint t2 = (GecRotr(a, 2) ^ GecRotr(a, 13) ^ GecRotr(a, 22)) + ((a & b) | (c & (a | b)));
            h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        return new[] { a, b, c, d, e, f, g, h };
    }

    static uint[] GecShaInit(byte[] data)
    {
        var w = new uint[64];
        w[14] = ((uint)data[0] << 16) | ((uint)data[1] << 8) | data[2];
        w[15] = ((uint)data[3] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6];
        for (int i = 16; i < 30; i++)
        {
            uint v = GecShaSchedule[i - 16];
            if (i - 2 >= 14) v += i == 16 ? GecSigma1(w[i - 2]) ^ GecSigma1Adjustment : GecSigma1(w[i - 2]);
            if (i - 7 >= 14) v += w[i - 7];
            if (i - 15 >= 14) v += i == 29 ? GecSigma0(w[i - 15]) ^ GecSigma0Adjustment : GecSigma0(w[i - 15]);
            w[i] = v;
        }
        w[30] = GecSigma1(w[28]) + w[23] + GecSigma0(w[15]) + w[14] + GecShaAdjustment - GecShaK[14];
        GecExpandSchedule(w, 31);
        uint[] working = GecShaRounds(GecShaState, w, 14);
        var result = new uint[8];
        for (int i = 0; i < 8; i++) result[i] = GecShaInitial[i] + working[i];
        return result;
    }

    static byte[] GecSha256(byte[] data)
    {
        int length = 57 + data.Length;
        int zeros = ((55 - length) % 64 + 64) % 64;
        var padded = new byte[data.Length - 7 + 1 + zeros + 8];
        Array.Copy(data, 7, padded, 0, data.Length - 7);
        padded[data.Length - 7] = 0x80;
        ulong bits = (ulong)length * 8;
        for (int i = 0; i < 8; i++) padded[padded.Length - 1 - i] = (byte)(bits >> (8 * i));

        uint[] state = GecShaInit(data);
        var w = new uint[64];
        for (int offset = 0; offset < padded.Length; offset += 64)
        {
            for (int i = 0; i < 16; i++)
                w[i] = ((uint)padded[offset + 4 * i] << 24) | ((uint)padded[offset + 4 * i + 1] << 16) |
                       ((uint)padded[offset + 4 * i + 2] << 8) | padded[offset + 4 * i + 3];
            GecExpandSchedule(w, 16);
            uint[] working = GecShaRounds(state, w, 0);
            for (int i = 0; i < 8; i++) state[i] += working[i];
        }
        var digest = new byte[32];
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 4; j++) digest[4 * i + j] = (byte)(state[i] >> (24 - 8 * j));
        return digest;
    }

    static uint[] GecCs64(byte[] data, byte[] digest)
    {
        uint keyA = BitConverter.ToUInt32(digest, 0) | 1;
        uint keyB = BitConverter.ToUInt32(digest, 4) | 1;
        uint[] factors = { keyA * GecCs64Factor, keyB * GecCs64Factor };

        // Little-endian words of 0x00 + data, zero-padded to an even word count
        int byteCount = data.Length + 1;
        int wordCount = (byteCount + 3) / 4;
        if (wordCount % 2 == 1) wordCount++;
        var bytes = new byte[wordCount * 4];
        Array.Copy(data, 0, bytes, 1, data.Length);

        int count = GecCs64Coefficients.Length + wordCount;
        var terms = new uint[count];
        var offsets = new uint[count];
        for (int i = 0; i < GecCs64Coefficients.Length; i++)
        {
            terms[i] = GecCs64Coefficients[i] * factors[i % 2];
            offsets[i] = GecCs64Offsets[i];
        }
        for (int i = 0; i < wordCount; i++)
        {
            uint word = BitConverter.ToUInt32(bytes, 4 * i);
            uint term = word * (i % 2 == 0 ? keyA : keyB);
            uint offset = GecRol16(word) * (i % 2 == 0 ? 0xEF0569FB : 0xC31713DB);
            if (i == 0)
            {
                term += GecCs64Adjustment0 * factors[0];
                offset += GecCs64Adjustment1;
            }
            terms[GecCs64Coefficients.Length + i] = term;
            offsets[GecCs64Coefficients.Length + i] = offset;
        }

        uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
        for (int i = 0; i < count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint q = GecRol16(offsets[i] + x) * 0x76F50F34;
            swapAcc += GecRol16(q) * 0xC3EFEA97 + q;
            q = GecRol16(offsets[i + 1] + y) * 0x1881CE3C;
            swapLast = GecRol16(q) * 0x35BD1EC9 + q;
            swapAcc += swapLast;

            state = state * keyA + x;
            state = GecRol16(state) * 0xCF98B111;
            state = GecRol16(state) * 0x87085B9F;
            state = GecRol16(state) * 0x12CEB96D;
            state = GecRol16(state) * 0x257E1D83;
            acc += state;
            uint t = state * keyB + y;
            t = GecRol16(t) * 0xA27416F5;
            t = GecRol16(t) * 0xD38396FF;
            state = GecRol16(GecRol16(t) * 0x7C932B89) * 0xBFA49F69;
            acc += state;
        }
        return new[] { swapLast ^ state, swapAcc ^ acc };
    }

    static string GenerateSecMsGec(long fileTimeUtc)
    {
        const string TOKEN    = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
        const long   FIVE_MIN = 300L * 10000000L;
        long rounded = fileTimeUtc - (fileTimeUtc % FIVE_MIN);
        byte[] data = Encoding.ASCII.GetBytes(rounded.ToString() + TOKEN);

        byte[] sha = GecSha256(data);
        uint[] cs64 = GecCs64(data, sha);
        var cs64Bytes = new byte[8];
        BitConverter.GetBytes(cs64[0]).CopyTo(cs64Bytes, 0);
        BitConverter.GetBytes(cs64[1]).CopyTo(cs64Bytes, 4);
        return BitConverter.ToString(cs64Bytes).Replace("-", "") + "-" + BitConverter.ToString(sha).Replace("-", "");
    }

    static string GenerateSecMsGec()
    {
        return GenerateSecMsGec(DateTime.UtcNow.ToFileTimeUtc());
    }

    static void SpeakNative(string text, string voice)
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
                            "&Sec-MS-GEC-Version=2-143.0.3650.75";

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
            LogVoice("[TTS] " + DateTime.Now.ToString("HH:mm:ss.fff") + " connected");
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
                "<voice name='" + voice + "'><prosody rate='+0%' pitch='+0Hz'>" +
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
                // A buffer plays ~0.35 s; if the device doesn't return it in time, reset playback
                // instead of waiting forever (mic input is ignored while speaking)
                const int WAVE_TIMEOUT_MS = 5000;
                int hdrSz      = Marshal.SizeOf(typeof(WAVEHDR));
                int flagOffset = IntPtr.Size + 4 + 4 + IntPtr.Size;
                bool stalled   = false;

                var    pending = new Queue<Tuple<IntPtr, GCHandle>>();
                byte[] readBuf = new byte[PCM_CHUNK];
                int    nRead;

                while (!stalled && !ttsAbort && (nRead = mp3s.Read(readBuf, 0, readBuf.Length)) > 0)
                {
                    while (pending.Count >= MAX_OUT)
                    {
                        var front = pending.Peek();
                        if (!WaitWaveOutDone(front.Item1, flagOffset, WAVE_TIMEOUT_MS))
                        {
                            waveOutReset(hWo);   // marks all queued buffers as done
                            stalled = !ttsAbort;
                        }
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
                if (ttsAbort) waveOutReset(hWo);   // barge-in: drop the rest of the phrase

                foreach (var item in pending)
                {
                    if (!WaitWaveOutDone(item.Item1, flagOffset, WAVE_TIMEOUT_MS))
                    {
                        waveOutReset(hWo);
                        stalled = !ttsAbort;
                    }
                    waveOutUnprepareHeader(hWo, item.Item1, hdrSz);
                    Marshal.FreeHGlobal(item.Item1);
                    item.Item2.Free();
                }
                if (stalled) LogVoice("[TTS] " + DateTime.Now.ToString("HH:mm:ss.fff") + " waveOut stalled, playback reset");
            }
            finally { if (hWo != IntPtr.Zero) waveOutClose(hWo); }

            if (!ttsAbort) recvThread.Join(5000);
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
        voicePipeStarted = true;
        // Otherwise the mic starts paused; user enables it via tray or "[V] включи микрофон"
        if (micOnStart)
        {
            micActive = true;
            StartMic();
            UpdateTrayText(GetTrayText());
        }
    }

    public static void ApplyVoiceChange()
    {
        if (enableVoice)
        {
            if (!voicePipeStarted) StartVoiceModule();
        }
        else
        {
            if (micActive) { micActive = false; StopMic(); }
            UpdateTrayText(GetTrayText());
        }
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
    const string EXIT_EVENT = "ClaudePusher_Exit";

    // Icons of killed processes stay in the tray until hovered: "hover" over the notification area to purge them
    static void PurgeDeadTrayIcons()
    {
        IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        h = FindWindowEx(h, IntPtr.Zero, "TrayNotifyWnd", null);
        h = FindWindowEx(h, IntPtr.Zero, "SysPager", null);
        HoverToolbar(FindWindowEx(h, IntPtr.Zero, "ToolbarWindow32", null));

        IntPtr overflow = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "NotifyIconOverflowWindow", null);
        HoverToolbar(FindWindowEx(overflow, IntPtr.Zero, "ToolbarWindow32", null));
    }

    static void HoverToolbar(IntPtr toolbar)
    {
        const uint WM_MOUSEMOVE = 0x0200;
        TrayRect r;
        if (toolbar == IntPtr.Zero || !GetClientRect(toolbar, out r)) return;
        for (int x = 0; x < r.Right; x += 5)
            for (int y = 0; y < r.Bottom; y += 5)
                SendMessageW(toolbar, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
    }

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

            try { PurgeDeadTrayIcons(); } catch { }

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

            // Graceful exit on request (e.g. before rebuild): unlike a killed process,
            // this removes the tray icon instead of leaving a dead one behind
            new Thread(() =>
            {
                using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EXIT_EVENT))
                    exitEvent.WaitOne();
                uiForm.BeginInvoke((Action)(() => { tray.Visible = false; Application.Exit(); }));
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
            // Bounded wait: a stalled connection must not keep TTS (and the muted mic) busy forever
            try   { if (!_q.TryTake(out _current, 10000)) return 0; _pos = 0; }
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

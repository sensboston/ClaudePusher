# ClaudePusher

Push external notifications into a running [Claude Code](https://claude.ai/code) CLI session on Windows — without any server, proxy, or tmux.

## What it does

ClaudePusher is a lightweight Windows tray app that monitors a Telegram bot for incoming messages and silently injects them into an active Claude Code terminal using the Windows `PostMessage` API (`WM_CHAR`). Claude Code receives the text as if you typed it, and sends replies back via a named pipe.

Supports text messages, photos, and file attachments. No server. No API middleware. No Linux/tmux dependency. Just .NET 4.8 and a Telegram bot token.

## How it works

```
Telegram message → ClaudePusher polls bot API (every 5s)
                 → injects "[T] <message>\n" into Claude Code window via WM_CHAR
                 → Claude processes the message and writes reply to named pipe
                 → ClaudePusher reads pipe, sends reply to Telegram
```

Unlike similar projects ([claude-code-telegram](https://github.com/RichardAtCT/claude-code-telegram), [claudegram](https://claudegram.com/)) that rely on `tmux send-keys` (Linux/Mac only), ClaudePusher uses the native Windows message API — works with any terminal emulator (Windows Terminal, mintty, ConEmu).

## Tray menu

Right-click the tray icon to access:

- **Start with Windows** — toggle autostart (reads/writes the registry)
- **Window list** — every Claude Code window found is listed with a checkbox;
  checked = injection enabled, unchecked = skipped. Clicking a window item also
  brings that window to the foreground so you can identify which session it is.
- **Encoding** — submenu to select the ANSI code page used for `WM_CHAR` injection
  (CP1251 Cyrillic by default). Selection is saved to `config.json` automatically.
- **Exit**

Multiple Claude Code sessions are supported simultaneously.

## Requirements

- Windows 10 or later
- .NET Framework 4.8 (pre-installed on Windows 10+)
- A running `claude.exe` session in a terminal window titled **"Claude Code"**
- A Telegram bot token (see [Setup](#setup))

## Setup

### 1. Create a Telegram bot

1. Open Telegram, find **@BotFather**
2. Send `/newbot`, follow the prompts
3. Copy the bot token (format: `123456789:AAxxxxxx...`)
4. Send any message to your new bot, then open:
   `https://api.telegram.org/bot<TOKEN>/getUpdates`
5. Find your `chat_id` in the response (`"chat":{"id": 123456789}`)

### 2. Configure ClaudePusher

Copy `config.example.json` to `config.json` **in the same folder as `ClaudePusher.exe`** and fill in your values:

```json
{
  "tg_token": "123456789:AAxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
  "chat_id": "123456789",
  "poll_interval_ms": 5000,
  "question_interval_hours": 4,
  "window_title": "Claude Code",
  "idle_threshold_minutes": 3,
  "encoding": "CP1251  Cyrillic"
}
```

The runtime config file (`config.json`) lives next to the executable — no AppData, no registry (except optional autostart).

### 3. Compile

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe ^
  /r:System.Web.Extensions.dll ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  /win32icon:claude_icon.ico ^
  /out:ClaudePusher.exe ^
  ClaudePusher.cs
```

Or run `compile.ps1`:
```powershell
powershell -ExecutionPolicy Bypass -File compile.ps1
```

### 4. Run

```
ClaudePusher.exe
```

Runs as a tray icon. Right-click → **Exit** to stop.

Optional: add to Windows startup via tray menu, or from command line:
```
ClaudePusher.exe --autostart
```

## Claude Code permissions

ClaudePusher communicates with Claude Code via a named pipe. For Claude to write to the pipe, it needs permission to run PowerShell from Bash.

### Minimum required permissions

Add to `~/.claude/settings.json`:

```json
{
  "permissions": {
    "allow": [
      "Bash(/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe:*)"
    ],
    "defaultMode": "ask"
  }
}
```

This is the minimum needed to send replies from Claude back to Telegram.


## Claude Code integration

Add this to your Claude Code instructions (`.claude/CLAUDE.md` or global `~/.claude/CLAUDE.md`):

```markdown
## Telegram — ClaudePusher

ClaudePusher injects incoming Telegram messages into this CLI session with the
prefix `[T] `. To send a reply back to Telegram, write to the named pipe
`\\.\pipe\claudepusher` using PowerShell:

    /c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -Command "
    \$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'claudepusher', [System.IO.Pipes.PipeDirection]::Out)
    \$p.Connect(2000)
    \$w = New-Object System.IO.StreamWriter(\$p, [System.Text.Encoding]::UTF8)
    \$w.WriteLine('your reply text here')
    \$w.Flush()
    \$p.Dispose()
    "

Rules:
- Message arrives with prefix `[T] ` → reply via pipe AND answer in the console
  (same text, no changes).
- Message arrives without prefix → answer in the console only, do not use pipe.
- When you receive "ask something" → think of a question genuinely interesting
  to you or the user, send it via pipe. If nothing comes to mind, skip silently.
```

## Project structure

```
ClaudePusher/
├── ClaudePusher.cs        # Main C# source
├── compile.ps1            # Build script
├── config.example.json    # Configuration template
└── README.md
```

## User activity detection

ClaudePusher monitors keyboard and mouse idle time using the Windows `GetLastInputInfo` API. When the user has been away for longer than `idle_threshold_minutes` (default: 3), it injects a notification into Claude Code:

```
[T] user away
```

This lets Claude Code adjust its behavior based on whether the user is present — for example, deferring long responses or sending a status update to Telegram.

The threshold is configurable via `idle_threshold_minutes` in `config.json`.

## Attachments

When a photo or file is sent to the bot, ClaudePusher downloads it automatically and injects a path reference into Claude Code:

```
[T] [photo: D:\...\inbox_media\20260302_084419.jpg]
[T] [file: D:\...\inbox_media\20260302_091200.pdf]
```

Downloaded files are saved to the `inbox_media/` folder next to `ClaudePusher.exe`. Claude Code can then read and process them using its built-in file tools.

## Extending

ClaudePusher is not limited to Telegram. The injection mechanism (`WM_CHAR` via `PostMessage`) works for any trigger source — GitHub webhooks, email, local events, scheduled tasks. Replace or extend the `PollLoop` in `ClaudePusher.cs`.

## License

MIT

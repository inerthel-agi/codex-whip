# Codex Whip

![Codex Whip banner](docs/assets/codex-whip-banner.png)

Codex Whip is a small Windows utility inspired by
[OpenWhip](https://github.com/GitFrog1111/OpenWhip). It displays an interactive
whip over Codex Desktop or Codex CLI and sends a short steering message when the
whip cracks.

The application runs in the Windows notification area. It does not modify Codex
configuration, environment variables, or session files.

Codex Whip is an independent community project. It is not affiliated with or
endorsed by OpenAI.

[Download the latest Windows release](https://github.com/stealthsrc/codex-whip/releases/latest)

## Start using Codex Whip

1. Open Codex Desktop, or start Codex CLI in Windows Terminal, classic
   PowerShell, or Command Prompt.
2. Run `CodexWhip.exe`. The whip starts hidden.
3. Press `F8` to show the whip.
4. Move the pointer quickly in one direction and back to crack it.
5. Press `F8` again to hide the whip.

For Codex Desktop, set **Settings > General > Follow-up behavior** to **Steer**.
With **Queue**, the message waits until the current task finishes instead of
redirecting it immediately.

## Understand how it works

- A left-click on the notification icon shows or hides the whip.
- `F8` is a global toggle. The **Whip shortcut** menu can switch it to `F9`,
  `F10`, or `Ctrl+Alt+W` for the current session.
- A slow or one-way pointer movement does not trigger a crack. A fast reversal
  does, and movement speed controls one of four sound levels.
- Manual steering supports Codex Desktop and one official Codex CLI session in
  Windows Terminal, classic PowerShell, or Command Prompt.
- One manual strike is sent at a time. Strikes made while a message is being
  sent are ignored and show a wait message.
- A strike that fails before any text is typed is retried up to three times.
  A strike is never retried after text reaches Codex.
- **Auto steering** attempts a steer every 60 seconds only while a compatible
  Codex window is already in the foreground. It never activates Codex. It is
  disabled at every startup. After three failures that occur after text was
  typed, it pauses for 10 minutes.
- Clicking inside the overlay does not hide it.

Codex Whip targets the `ChatGPT.exe` process installed by the official
`OpenAI.Codex` Windows package or an already running official Codex CLI process.

## Use the notification menu

- **Whip Codex** shows or hides the whip.
- **Auto steering** enables a steering attempt every 60 seconds.
- **Whip shortcut** selects `F8`, `F9`, `F10`, or `Ctrl+Alt+W` for the current
  session. `F8` is restored after a restart.
- **Quit** releases the global shortcut and closes the application.

If another application already owns a shortcut, Codex Whip keeps the previous
shortcut and displays a warning.

## Use Codex CLI

Steering supports Codex CLI in **Windows Terminal**, **classic PowerShell**,
and **Command Prompt**. Before sending input, Codex Whip verifies that:

- the terminal window is in the foreground;
- exactly one official Codex CLI session is present;
- in Windows Terminal, the Codex session runs in the selected tab, and that
  tab shows a single pane;
- a turn is active;
- the composer is empty, with no draft or attachment;
- no console selection or modifier key can interfere with input.

If any guard cannot be verified, no text is sent. After insertion, Codex Whip
presses `Esc` only when the exact pending steer preview is visible.

The VS Code integrated terminal, Git Bash, and WSL are not supported. Auto
steering never opens Codex or steals focus; unsafe or unsupported situations
are skipped silently.

## Supported languages

The interface follows the Windows display language. Eighteen languages are
included: English, French, Spanish, German, Italian, Portuguese, Dutch, Polish,
Russian, Ukrainian, Turkish, Arabic, Hindi, Indonesian, Japanese, Korean,
Simplified Chinese, and Traditional Chinese.

Unsupported languages fall back to English. Steering messages intentionally use
short, universal English instructions.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 Desktop Runtime.
- Codex Desktop, or Codex CLI in Windows Terminal, classic PowerShell, or
  Command Prompt.
- For Codex Desktop: **Settings > General > Follow-up behavior > Steer**.

## Build and run the project

```powershell
dotnet build -c Release
dotnet run -c Release
```

Start with the whip visible:

```powershell
dotnet run -c Release -- --show
```

Publish a framework-dependent, single-file Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The executable is written to
`bin\Release\net8.0-windows\win-x64\publish\CodexWhip.exe`. Runtime WAV files
are copied to the adjacent `sounds` directory.

## Command-line options

| Option                 | Behavior                                                                                                                           |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `--show`               | Starts the application with the whip visible.                                                                                      |
| `--diagnose`           | Checks Codex Desktop and writes JSON. Returns `0` when ready, or `2` on failure.                                                   |
| `--diagnose --verbose` | Adds a description of the Desktop composer area and the log file path to the JSON.                                                 |
| `--diagnose-cli`       | Waits up to 12 seconds for a terminal in the foreground, then reports how the CLI guards read it. Sends nothing. Returns `0` or `2`. |
| `--self-test`          | Tests gestures, sounds, languages, CLI and Desktop guards, the log, retries, and shortcuts. Returns `0` on success, or `3` on failure. |

Run Desktop diagnostics:

```powershell
dotnet run -c Release -- --diagnose --verbose
```

## Read the steering log

Each steering attempt writes one line to
`%LOCALAPPDATA%\CodexWhip\steer.log`: origin, target, result code, stage, and
duration. Failed attempts add the guard that blocked them. The log is capped
at 256 KB with one rotated copy. It contains no conversation text: interface
labels other than known button names are replaced by their length.

```powershell
Get-Content "$env:LOCALAPPDATA\CodexWhip\steer.log" -Tail 20
```

## Validate a change

```powershell
dotnet format CodexWhip.csproj --verify-no-changes --severity warn
dotnet build CodexWhip.csproj -c Release
dotnet run --project CodexWhip.csproj -c Release -- --self-test
```

The deterministic self-test covers gesture thresholds, all four sounds,
localization bundles, Codex CLI and Desktop guards, display scaling, the
steering log, retry rules, and global shortcut definitions.

## Safety guards

- Input is sent only to a verified official Codex process.
- An active task must expose its **Stop** button.
- Codex must own keyboard focus before input is sent.
- An existing draft is preserved and blocks steering.
- Codex CLI must expose one verifiable active turn with an empty composer.
- Only one manual strike is processed at a time.

## Troubleshoot common problems

| Problem                           | Check                                                                                              |
| --------------------------------- | -------------------------------------------------------------------------------------------------- |
| `F8` does nothing                 | Select another key in **Whip shortcut**. Another application may own `F8`.                         |
| The whip moves but does not crack | Make a faster, more deliberate back-and-forth movement. Slow or one-way movement is ignored.       |
| No sound plays                    | Confirm that `sounds\whip_1.wav` through `sounds\whip_4.wav` are next to the published executable. |
| Desktop receives no message       | Keep a task active with the **Stop** button visible, clear the composer, and select **Steer**.     |
| CLI receives no message           | Keep one active Codex turn in a single-pane tab, clear the composer, and run `--diagnose-cli`.     |
| Steering is blocked               | Clear console selection, release modifier keys, and read the reason in `steer.log`.               |
| Text stays in the composer        | Clear it by hand. Codex Whip treats any text as a draft and does not overwrite it.                 |

## Repository layout

```text
assets/
  icons/        Windows application icon
  sounds/       Source MP3 and runtime WAV files
docs/
  adr/          Architecture decisions
  assets/       README images
src/            C# application source
```

Project metadata and community files remain at the repository root.

## Known limitations

The Desktop integration relies on Windows UI Automation and guarded keyboard
input. A major Codex Desktop interface change may require an update to composer
detection. The Desktop `Steer` button label is only verified with the English
Codex interface. Codex CLI support is limited to the terminals listed above;
Windows Terminal split panes are refused.

## License and contributions

Codex Whip is available under the [MIT License](LICENSE). Read
[CONTRIBUTING.md](CONTRIBUTING.md) before submitting a change, and report
security issues according to [SECURITY.md](SECURITY.md).

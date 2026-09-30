# Steering reference

This page describes how Codex Whip decides whether to send a steering message,
how to read its log, and how to fix common problems.

## Sending rules

- One manual strike is sent at a time. Strikes made while a message is being
  sent are ignored and show a wait message.
- A strike that fails before any text is typed is retried up to three times.
  A strike is never retried after text reaches Codex.
- **Auto steering** attempts a steer every 60 seconds only while a compatible
  Codex window is already in the foreground. It never activates Codex and is
  disabled at every startup. After three failures that occur after text was
  typed, it pauses for 10 minutes.
- Steering messages are short, universal English instructions.

## Safety guards

- Input is sent only to a verified official Codex process: `ChatGPT.exe` from
  the `OpenAI.Codex` Windows package, or an official Codex CLI process.
- A Codex Desktop task must expose its **Stop** button.
- Codex must own keyboard focus before input is sent.
- An existing draft is preserved and blocks steering. Codex Whip treats any
  text in the composer as a draft and never overwrites it.

For Codex CLI, Codex Whip also verifies that:

- the terminal window is in the foreground;
- exactly one official Codex CLI session is present;
- in Windows Terminal, the Codex session runs in the selected tab, and that
  tab shows a single pane;
- a turn is active;
- the composer is empty, with no draft or attachment;
- no console selection or modifier key can interfere with input.

If any guard cannot be verified, no text is sent. After insertion, Codex Whip
presses `Esc` only when the exact pending steer preview is visible.

## Steering log

Each steering attempt writes one line to
`%LOCALAPPDATA%\CodexWhip\steer.log`: origin, target, result code, stage, and
duration. Failed attempts add the guard that blocked them. The log is capped at
256 KB with one rotated copy. It contains no conversation text: interface
labels other than known button names are replaced by their length.

```powershell
Get-Content "$env:LOCALAPPDATA\CodexWhip\steer.log" -Tail 20
```

## Troubleshooting

| Problem                           | Check                                                                                              |
| --------------------------------- | -------------------------------------------------------------------------------------------------- |
| `F8` does nothing                 | Select another key in **Whip shortcut**. Another application may own `F8`.                         |
| The whip moves but does not crack | Make a faster, more deliberate back-and-forth movement. Slow or one-way movement is ignored.       |
| No sound plays                    | Confirm that `sounds\whip_1.wav` through `sounds\whip_4.wav` are next to the published executable. |
| Desktop receives no message       | Keep a task active with the **Stop** button visible, clear the composer, and select **Steer**.     |
| CLI receives no message           | Keep one active Codex turn in a single-pane tab, clear the composer, and run `--diagnose-cli`.     |
| Steering is blocked               | Clear console selection, release modifier keys, and read the reason in `steer.log`.               |
| Text stays in the composer        | Clear it by hand. Codex Whip does not overwrite existing text.                                     |

## Languages

The interface follows the Windows display language. Eighteen languages are
included: English, French, Spanish, German, Italian, Portuguese, Dutch, Polish,
Russian, Ukrainian, Turkish, Arabic, Hindi, Indonesian, Japanese, Korean,
Simplified Chinese, and Traditional Chinese. Unsupported languages fall back to
English.

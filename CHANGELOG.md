# Changelog

All notable changes to Codex Whip are documented in this file.

## [Unreleased]

### Added

- Codex CLI steering in Windows Terminal, limited to the selected single-pane tab.
- Steering log at `%LOCALAPPDATA%\CodexWhip\steer.log` with result codes, stages, and blocking guards.
- `--diagnose --verbose` and `--diagnose-cli` dry-run diagnostics.
- Specific result codes that replace `FOCUS_GUARD`: `CANCELLED`, `FOCUS_NOT_GRANTED`, `COMPOSER_NOT_FOCUSED`, `MODIFIER_HELD`, and `TURN_CHANGED`.

### Changed

- Codex Desktop steering invokes the `Steer` action of the queued message it created, then restores the previous foreground window.
- Steering requests run through one cancellable scheduler shared by manual and automatic steering.
- Manual strikes are processed one at a time instead of queued, which removes delayed sends.
- Manual strikes retry up to three times when they fail before any text is typed.
- Automatic steering only acts when Codex is already in the foreground, and pauses for 10 minutes after repeated post-input failures.
- Desktop composer detection searches near the composer, waits for the Electron accessibility tree, and scales pixel limits with display DPI.
- Codex Desktop is restored only when minimized, so a maximized window stays maximized.
- A deliberate repeated crack registers after 0.28 seconds instead of 0.65 seconds.

### Fixed

- Windows Terminal tab titles animated by Codex CLI no longer fail the tab check.
- The CLI steer confirmation waits up to 1.5 seconds for Codex under load.

## [0.1.1] - 2026-07-18

### Security

- Revalidate the exact Codex Desktop composer and foreground window immediately before keyboard input.
- Fail closed when the Desktop composer cannot be read through UI Automation.
- Distinguish rendered placeholders from real drafts with the ProseMirror empty-document marker.
- Propagate automatic-steering cancellation through Codex CLI before text entry and submission.

## [0.1.0] - 2026-07-18

### Added

- Native Windows overlay with speed-sensitive whip physics and four sound levels.
- Manual steering for Codex Desktop and Codex CLI in classic Windows consoles.
- Optional automatic steering every 60 seconds when Codex is in the foreground.
- Global toggle shortcuts: `F8`, `F9`, `F10`, and `Ctrl+Alt+W`.
- Eighteen localized interface languages with universal English steering messages.
- Fail-closed focus, active-turn, draft, process, console, and cooldown guards.
- Black-and-white application icon, banner, diagnostics, and deterministic self-tests.

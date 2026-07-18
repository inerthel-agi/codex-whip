# Changelog

All notable changes to Codex Whip are documented in this file.

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

# Contributing to Codex Whip

Codex Whip is a Windows-only .NET 8 application. Keep changes focused, preserve
the fail-closed steering guards, and avoid adding dependencies unless necessary.

## Set up the project

1. Install the .NET 8 SDK on Windows 10 or Windows 11.
2. Clone the repository.
3. Run `dotnet build -c Release`.

## Validate a change

Run these commands before opening a pull request:

```powershell
dotnet format CodexWhip.csproj --verify-no-changes --severity warn
dotnet build CodexWhip.csproj -c Release
dotnet run --project CodexWhip.csproj -c Release -- --self-test
```

Describe the behavior change, the affected Codex target, and the manual test in
the pull request. Never include Codex session data, credentials, or private logs.

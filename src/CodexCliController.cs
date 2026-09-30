using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CodexWhip;

internal enum CliCaptureKind
{
    None,
    Ready,
    Unsupported
}

internal sealed record CodexCliTarget(
    int ProcessId,
    nint WindowHandle,
    long StartTimeTicks,
    string ExecutablePath);

internal sealed record CodexCliCapture(
    CliCaptureKind Kind,
    nint WindowHandle = default,
    CodexCliTarget? Target = null);

internal sealed class CodexCliController
{
    private enum CliSubmitOutcome
    {
        Steered,
        Cancelled,
        Failed
    }

    private enum CliInterruptDecision
    {
        SendEscape,
        Cancelled,
        Reject
    }

    private const string ClassicConsoleClass = "ConsoleWindowClass";
    private const string TerminalHostClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string PseudoConsoleClass = "PseudoConsoleWindow";
    private const uint GetAncestorRootOwner = 3;
    private const string ActiveTurnMarker = " to interrupt)";
    private const string VimModeMarker = "Vim:";
    private const string PendingSteerHeader = "Messages to be submitted after next tool call";
    private const string PendingSteerInterruptHint = "press esc to interrupt and send immediately";
    private const string RejectedSteerHeader = "Messages to be submitted at end of turn";
    private const string QueuedFollowUpHeader = "Queued follow-up inputs";
    private const int CliDraftConfirmationTimeoutMilliseconds = 1000;
    private const int CliDraftConfirmationPollMilliseconds = 15;
    private const int CliSubmitConfirmationTimeoutMilliseconds = 750;
    private const int CliSubmitConfirmationPollMilliseconds = 15;
    // Codex can take over a second to consume the pending steer under load.
    private const int CliInterruptConfirmationTimeoutMilliseconds = 1500;
    private static readonly string[] EmptyComposerPlaceholders =
    [
        "Ask Codex to do anything",
        "Explain this codebase",
        "Summarize recent commits",
        "Implement {feature}",
        "Find and fix a bug in @filename",
        "Write tests for @filename",
        "Improve documentation in @filename",
        "Run /review on my current changes",
        "Use /skills to list available skills"
    ];
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyEscape = 0x1B;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionIgnore = 0;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;

    private static readonly object ConsoleLock = new();
    private static readonly object SignatureCacheLock = new();
    private static readonly Regex ActiveStatusPattern = new(
        @"^(?:[•◦] )?\S.* \((?:\d+s|\d+m \d{2}s|\d+h \d{2}m \d{2}s) • .+ to interrupt\)(?: · .+)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RemoteImagePattern = new(
        @"^\s*\[Image #\d+\]\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, SignatureCacheEntry> SignatureCache =
        new(StringComparer.OrdinalIgnoreCase);

    public void PrewarmInstalledCli()
    {
        var packageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "node_modules",
            "@openai",
            "codex");
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             packageRoot,
                             "codex.exe",
                             SearchOption.AllDirectories)
                         .Where(IsExpectedOfficialPath)
                         .Take(4))
                {
                    _ = IsAuthenticOpenAiExecutable(path);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Codex CLI signature prewarm skipped: {exception.Message}");
            }
        });
    }

    public CodexCliCapture CaptureForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return new CodexCliCapture(CliCaptureKind.None);
        }

        var windowClass = GetWindowClass(foreground);
        var processName = GetWindowProcessName(foreground);
        var isSupportedHost = IsSupportedConsoleHost(windowClass, processName);
        var isUnsupportedTerminal = IsUnsupportedTerminal(windowClass, processName);
        if (!isSupportedHost && !isUnsupportedTerminal)
        {
            return new CodexCliCapture(CliCaptureKind.None);
        }

        if (!TryFindCliProcessCandidates(out var candidates))
        {
            return new CodexCliCapture(CliCaptureKind.Unsupported, foreground);
        }

        foreach (var candidate in candidates)
        {
            QueueSignatureVerification(candidate.ExecutablePath);
        }

        if (isSupportedHost)
        {
            var matches = new List<CliProcessIdentity>();
            foreach (var candidate in candidates)
            {
                if (TryGetAttachedConsoleWindow(candidate.ProcessId, out var consoleWindow)
                    && consoleWindow == foreground)
                {
                    matches.Add(candidate);
                }
            }

            if (candidates.Count == 1 && matches.Count == 1)
            {
                var match = matches[0];
                return new CodexCliCapture(
                    CliCaptureKind.Ready,
                    foreground,
                    new CodexCliTarget(
                        match.ProcessId,
                        foreground,
                        match.StartTimeTicks,
                        match.ExecutablePath));
            }

            return new CodexCliCapture(CliCaptureKind.Unsupported, foreground);
        }

        if (isUnsupportedTerminal)
        {
            return new CodexCliCapture(CliCaptureKind.Unsupported, foreground);
        }

        return new CodexCliCapture(CliCaptureKind.None);
    }

    // Dry run for --diagnose-cli: reports what the foreground window resolves to
    // and how the guards read its screen. Sends nothing.
    public string DescribeForeground()
    {
        var capture = CaptureForeground();
        if (capture.Kind != CliCaptureKind.Ready || capture.Target is not { } target)
        {
            return $"capture={capture.Kind}";
        }

        var host = TryGetAttachedConsoleWindow(target.ProcessId, out var hostWindow, out var hostProblem)
            ? $"host={(hostWindow == target.WindowHandle ? "foreground" : "other")}"
            : $"host-problem={hostProblem}";
        var valid = TryValidateTarget(target, verifySignature: true, verifyConsole: true);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < 40; index++)
        {
            if (!TryValidateTarget(target, verifySignature: false, verifyConsole: true))
            {
                var key = _lastValidationProblem ?? "unknown";
                failures[key] = failures.GetValueOrDefault(key) + 1;
            }
        }

        var repeat = $"repeat=40 avgMs={clock.ElapsedMilliseconds / 40.0:0.0} failures="
            + (failures.Count == 0 ? "0" : string.Join(",", failures.Select(pair => $"{pair.Key}:{pair.Value}")));
        var composerWidth = TryReadConsoleSnapshot(target.ProcessId, out var snapshot)
            ? $"screen={AnalyzeScreen(snapshot)} width={snapshot.WindowRight - snapshot.WindowLeft + 1}"
            : "screen=unreadable";
        return $"capture=Ready {composerWidth} {host} valid={valid} {repeat} pid={target.ProcessId}";
    }

    public SteerResult TrySteer(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken = default) =>
        TrySteerCore(target, message, cancellationToken, tryCommit: null);

    internal SteerResult TrySteer(
        CodexCliTarget target,
        string message,
        SteerAttempt attempt) =>
        TrySteerCore(target, message, attempt.Token, attempt.TryCommit);

    private SteerResult TrySteerCore(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken,
        Func<bool>? tryCommit)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure("CLI_CANCELLED", "Automatic CLI steering was cancelled.", target.WindowHandle);
        }

        if (!IsValidMessage(message))
        {
            return Failure("CLI_UNAVAILABLE", "The CLI steering message is invalid.", target.WindowHandle);
        }

        if (!TryValidateTarget(target, verifySignature: true, verifyConsole: true))
        {
            return Failure("CLI_CHANGED", "The Codex CLI target changed." + ValidationSuffix(), target.WindowHandle);
        }

        if (!TryReadConsoleSnapshot(target.ProcessId, out var before))
        {
            return Failure("CLI_UNAVAILABLE", "The Codex CLI screen could not be read.", target.WindowHandle);
        }

        switch (AnalyzeScreen(before))
        {
            case CliScreenState.Inactive:
                return Failure("CLI_NOT_ACTIVE", "Codex CLI has no active turn.", target.WindowHandle);
            case CliScreenState.DraftPresent:
                return Failure("CLI_DRAFT_PRESENT", "The Codex CLI composer is not empty.", target.WindowHandle);
        }

        var visibleWidth = before.WindowRight - before.WindowLeft + 1;
        if (message.Length + 4 > visibleWidth)
        {
            return Failure("CLI_UNAVAILABLE", "The Codex CLI composer is too narrow for safe steering.", target.WindowHandle);
        }

        _lastValidationProblem = null;
        if (AnyInputModifierDown())
        {
            return Failure("CLI_CHANGED", "The Codex CLI target or screen changed. [modifier]", target.WindowHandle);
        }

        if (!TryReadConsoleSnapshot(target.ProcessId, out var immediatelyBefore)
            || !IsReadyForInjection(immediatelyBefore, message))
        {
            return Failure("CLI_CHANGED", "The Codex CLI target or screen changed. [screen]", target.WindowHandle);
        }

        if (!TryValidateTarget(target, verifySignature: false, verifyConsole: true)
            || AnyInputModifierDown())
        {
            return Failure(
                "CLI_CHANGED",
                "The Codex CLI target or screen changed." + (ValidationSuffix() is { Length: > 0 } suffix ? suffix : " [modifier]"),
                target.WindowHandle);
        }

        var outcome = SendUnicodeTextAndEnter(
            target,
            message,
            cancellationToken,
            tryCommit);
        if (outcome == CliSubmitOutcome.Cancelled)
        {
            return Failure(
                "CLI_CANCELLED",
                "Automatic CLI steering was cancelled.",
                target.WindowHandle);
        }

        if (outcome == CliSubmitOutcome.Failed)
        {
            return Failure(
                "CLI_SEND_INPUT_FAILED",
                $"The CLI message could not be steered immediately. [{_lastSubmitStage}]{ValidationSuffix()}",
                target.WindowHandle);
        }

        return new SteerResult(true, "STEERED", "The active Codex CLI turn was steered.", target.WindowHandle);
    }

    internal static bool RunSelfTest(out string message)
    {
        var validPath = @"C:\npm\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";
        var invalidPath = @"C:\npm\node_modules\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";
        var activeLine = "• Working (2s • esc to interrupt)";
        const string legacyPlaceholder = "Ask Codex to do anything";
        const string dynamicPlaceholder = "Run /review on my current changes";
        var composerLine = $"› {legacyPlaceholder}";
        var readyScreens = EmptyComposerPlaceholders.Select(placeholder => CreateTestSnapshot(
            [activeLine, string.Empty, $"› {placeholder}", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: placeholder));
        var readyScreen = CreateTestSnapshot(
            [activeLine, string.Empty, composerLine, "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder);
        var draftScreen = CreateTestSnapshot(
            [activeLine, string.Empty, "› Go faster", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 11,
            dimStatus: true,
            dimComposerMarker: null);
        var dynamicPlaceholderAsDraftScreen = CreateTestSnapshot(
            [activeLine, string.Empty, $"› {dynamicPlaceholder}", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: null);
        var dynamicPlaceholderWithMovedCursorScreen = CreateTestSnapshot(
            [activeLine, string.Empty, $"› {dynamicPlaceholder}", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2 + dynamicPlaceholder.Length,
            dimStatus: true,
            dimComposerMarker: dynamicPlaceholder);
        const string sidePlaceholder = "How many files have been modified?";
        var sidePlaceholderScreen = CreateTestSnapshot(
            [activeLine, string.Empty, $"› {sidePlaceholder}", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: sidePlaceholder);
        var forgedStatusScreen = CreateTestSnapshot(
            [activeLine, string.Empty, composerLine, "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: false,
            dimComposerMarker: legacyPlaceholder);
        var forgedComposerScreen = CreateTestSnapshot(
            [activeLine, string.Empty, composerLine, "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: null);
        var alternateSpinnerScreen = CreateTestSnapshot(
            ["◦ Working (2s • esc to interrupt)", string.Empty, composerLine],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder);
        var reducedMotionScreen = CreateTestSnapshot(
            ["Working (2s • esc to interrupt)", string.Empty, composerLine],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder);
        var remoteImageScreen = CreateTestSnapshot(
            [activeLine, "  [Image #1]", string.Empty, composerLine],
            cursorRow: 3,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder);
        var inactiveScreen = CreateTestSnapshot(
            [composerLine, "? for shortcuts"],
            cursorRow: 0,
            cursorColumn: 2,
            dimStatus: false,
            dimComposerMarker: legacyPlaceholder);
        var vimModeScreens = new[] { "Vim: Normal", "Vim: Insert" }.Select(vimMode => CreateTestSnapshot(
            [activeLine, string.Empty, composerLine, vimMode],
            cursorRow: 2,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder));
        const string injectedMessage = "Go faster.";
        var pendingSteerScreen = CreateTestSnapshot(
            [activeLine, $"• {PendingSteerHeader} (press esc", "  to interrupt and send immediately)", $"  ↳ {injectedMessage}", composerLine],
            cursorRow: 4,
            cursorColumn: 2,
            dimStatus: true,
            dimComposerMarker: legacyPlaceholder);
        var injectedDraftScreen = CreateTestSnapshot(
            [activeLine, string.Empty, $"› {injectedMessage}", "? for shortcuts"],
            cursorRow: 2,
            cursorColumn: 2 + injectedMessage.Length,
            dimStatus: true,
            dimComposerMarker: null);
        var injectedDraftWithImageScreen = CreateTestSnapshot(
            [activeLine, "  [Image #1]", string.Empty, $"› {injectedMessage}", "? for shortcuts"],
            cursorRow: 3,
            cursorColumn: 2 + injectedMessage.Length,
            dimStatus: true,
            dimComposerMarker: null);
        var injectedDraftWithMovedCursorScreen = injectedDraftScreen with
        {
            CursorX = (short)(1 + injectedMessage.Length)
        };
        var expectedInputSize = nint.Size == 8 ? 40 : 28;

        if (!IsExpectedOfficialPath(validPath)
            || IsExpectedOfficialPath(invalidPath)
            || !IsClassicConsole(ClassicConsoleClass)
            || IsClassicConsole(TerminalHostClass)
            || !IsSupportedConsoleHost(TerminalHostClass, "WindowsTerminal")
            || IsSupportedConsoleHost(TerminalHostClass, "explorer")
            || IsUnsupportedTerminal(TerminalHostClass, "WindowsTerminal")
            || StripSpinnerPrefix("⠋ Compter | me") != StripSpinnerPrefix("⠸ Compter | me")
            || StripSpinnerPrefix("⠋ Compter | me") != "Compter | me"
            || StripSpinnerPrefix("Compter | me") != "Compter | me"
            || StripSpinnerPrefix("A Compter") != "A Compter"
            || StripSpinnerPrefix("⠋ Other | me") == StripSpinnerPrefix("⠸ Compter | me")
            || !IsUnsupportedTerminal("Chrome_WidgetWin_1", "Code")
            || IsUnsupportedTerminal("CabinetWClass", "explorer")
            || readyScreens.Any(screen => AnalyzeScreen(screen) != CliScreenState.Ready)
            || AnalyzeScreen(readyScreen) != CliScreenState.Ready
            || AnalyzeScreen(draftScreen) != CliScreenState.DraftPresent
            || AnalyzeScreen(dynamicPlaceholderAsDraftScreen) != CliScreenState.DraftPresent
            || AnalyzeScreen(dynamicPlaceholderWithMovedCursorScreen) != CliScreenState.DraftPresent
            || AnalyzeScreen(sidePlaceholderScreen) != CliScreenState.DraftPresent
            || AnalyzeScreen(forgedStatusScreen) == CliScreenState.Ready
            || AnalyzeScreen(forgedComposerScreen) == CliScreenState.Ready
            || AnalyzeScreen(alternateSpinnerScreen) != CliScreenState.Ready
            || AnalyzeScreen(reducedMotionScreen) != CliScreenState.Ready
            || AnalyzeScreen(remoteImageScreen) != CliScreenState.DraftPresent
            || AnalyzeScreen(inactiveScreen) != CliScreenState.Inactive
            || vimModeScreens.Any(screen => AnalyzeScreen(screen) != CliScreenState.DraftPresent)
            || AnalyzeScreen(pendingSteerScreen) != CliScreenState.DraftPresent
            || !IsPendingSteerReadyForEscape(pendingSteerScreen, injectedMessage)
            || IsPendingSteerReadyForEscape(pendingSteerScreen, "Move faster.")
            || IsPendingSteerConsumptionConfirmed(pendingSteerScreen, injectedMessage)
            || !IsPendingSteerConsumptionConfirmed(readyScreen, injectedMessage)
            || IsPendingSteerConsumptionConfirmed(draftScreen, injectedMessage)
            || !IsKnownEmptyComposerDisplay(readyScreen)
            || IsKnownEmptyComposerDisplay(draftScreen)
            || !IsExpectedComposerDraft(injectedDraftScreen, injectedMessage)
            || IsExpectedComposerDraft(injectedDraftScreen, "Move faster.")
            || IsExpectedComposerDraft(injectedDraftWithImageScreen, injectedMessage)
            || IsExpectedComposerDraft(injectedDraftWithMovedCursorScreen, injectedMessage)
            || !IsReadyForInjection(alternateSpinnerScreen, injectedMessage)
            || IsReadyForInjection(draftScreen, injectedMessage)
            || CliDraftConfirmationTimeoutMilliseconds <= 60
            || CliDraftConfirmationPollMilliseconds <= 0
            || CliDraftConfirmationPollMilliseconds >= CliDraftConfirmationTimeoutMilliseconds
            || ClassifyInterruptDecision(true, true, true, true, true)
                != CliInterruptDecision.Cancelled
            || ClassifyInterruptDecision(false, false, true, true, true)
                != CliInterruptDecision.Reject
            || ClassifyInterruptDecision(false, true, false, true, true)
                != CliInterruptDecision.Reject
            || ClassifyInterruptDecision(false, true, true, false, true)
                != CliInterruptDecision.Reject
            || ClassifyInterruptDecision(false, true, true, true, false)
                != CliInterruptDecision.Reject
            || ClassifyInterruptDecision(false, true, true, true, true)
                != CliInterruptDecision.SendEscape
            || !IsValidMessage("Go faster.")
            || IsValidMessage("first line\nsecond line")
            || Marshal.SizeOf<Input>() != expectedInputSize)
        {
            message = "FAIL: Codex CLI guards are inconsistent.";
            return false;
        }

        message = "PASS: Codex CLI path, terminal, screen, and message guards are consistent.";
        return true;
    }

    private static SteerResult Failure(string code, string message, nint windowHandle) =>
        new(false, code, message, windowHandle);

    private static bool IsValidMessage(string message) =>
        !string.IsNullOrWhiteSpace(message)
        && message.Length <= 256
        && !message.Any(char.IsControl);

    private static bool IsReadyForInjection(CliConsoleSnapshot snapshot, string message) =>
        AnalyzeScreen(snapshot) == CliScreenState.Ready
        && HasEscapeInterruptBinding(snapshot)
        && message.Length + 4 <= snapshot.WindowRight - snapshot.WindowLeft + 1;

    private static CliScreenState AnalyzeScreen(CliConsoleSnapshot snapshot)
    {
        if (HasVimFooter(snapshot))
        {
            return CliScreenState.DraftPresent;
        }

        if (!TryGetSnapshotRow(
                snapshot,
                snapshot.CursorY,
                out var composerLine,
                out var composerAttributes))
        {
            return CliScreenState.Inactive;
        }

        if (!TryGetActiveStatusRow(snapshot, out var statusRow))
        {
            return CliScreenState.Inactive;
        }

        for (var row = statusRow.Row + 1; row < snapshot.CursorY; row++)
        {
            if (TryGetSnapshotRow(snapshot, row, out var line, out _)
                && RemoteImagePattern.IsMatch(line))
            {
                return CliScreenState.DraftPresent;
            }
        }

        if (HasInputPreview(GetNormalizedComposerContext(snapshot, statusRow.Row)))
        {
            return CliScreenState.DraftPresent;
        }

        return IsVerifiedEmptyComposer(
            snapshot,
            composerLine,
            composerAttributes,
            statusRow.Line,
            statusRow.Attributes)
            ? CliScreenState.Ready
            : CliScreenState.DraftPresent;
    }

    private static bool TryGetActiveStatusRow(
        CliConsoleSnapshot snapshot,
        out (int Row, string Line, string Attributes) status)
    {
        status = default;
        var found = false;
        for (var row = Math.Max(snapshot.CapturedTop, snapshot.CursorY - 6); row < snapshot.CursorY; row++)
        {
            if (!TryGetSnapshotRow(snapshot, row, out var line, out var attributes)
                || !ActiveStatusPattern.IsMatch(line.TrimEnd()))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            status = (row, line, attributes);
            found = true;
        }

        return found;
    }

    private static string GetNormalizedComposerContext(CliConsoleSnapshot snapshot, int statusRow)
    {
        var lines = new List<string>();
        for (var row = statusRow + 1; row < snapshot.CursorY; row++)
        {
            if (TryGetSnapshotRow(snapshot, row, out var line, out _))
            {
                lines.Add(line.Trim());
            }
        }

        return Regex.Replace(string.Join(' ', lines), @"\s+", " ").Trim();
    }

    private static bool HasInputPreview(string context) =>
        context.Contains(PendingSteerHeader, StringComparison.Ordinal)
        || context.Contains(RejectedSteerHeader, StringComparison.Ordinal)
        || context.Contains(QueuedFollowUpHeader, StringComparison.Ordinal);

    private static bool HasEscapeInterruptBinding(CliConsoleSnapshot snapshot) =>
        TryGetActiveStatusRow(snapshot, out var status)
        && status.Line.Contains(" esc to interrupt)", StringComparison.OrdinalIgnoreCase);

    private static bool IsPendingSteerReadyForEscape(CliConsoleSnapshot snapshot, string message)
    {
        if (HasVimFooter(snapshot)
            || !IsKnownEmptyComposerDisplay(snapshot)
            || !TryGetActiveStatusRow(snapshot, out var status)
            || !status.Line.Contains(" esc to interrupt)", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var context = GetNormalizedComposerContext(snapshot, status.Row);
        if (!context.Contains(PendingSteerHeader, StringComparison.Ordinal)
            || !context.Contains(PendingSteerInterruptHint, StringComparison.OrdinalIgnoreCase)
            || context.Contains(RejectedSteerHeader, StringComparison.Ordinal)
            || context.Contains(QueuedFollowUpHeader, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedPreview = $"↳ {message}";
        var previews = context.Split('↳').Skip(1).Select(part => $"↳ {part.Trim()}").ToArray();
        return previews.Length == 1 && previews[0].Equals(expectedPreview, StringComparison.Ordinal);
    }

    private static bool IsPendingSteerConsumptionConfirmed(
        CliConsoleSnapshot snapshot,
        string message) =>
        IsReadyForInjection(snapshot, message);

    private static bool IsVerifiedEmptyComposer(
        CliConsoleSnapshot snapshot,
        string composerLine,
        string composerAttributes,
        string statusLine,
        string statusAttributes)
    {
        var cursorColumn = snapshot.CursorX - snapshot.WindowLeft;
        if (cursorColumn < 2
            || cursorColumn > composerLine.Length
            || composerLine[cursorColumn - 2] != '›'
            || composerLine[cursorColumn - 1] != ' '
            || composerLine.AsSpan(0, cursorColumn - 2).ContainsAnyExcept(' '))
        {
            return false;
        }

        var placeholderText = composerLine[cursorColumn..].TrimEnd(' ');
        if (!EmptyComposerPlaceholders.Contains(placeholderText, StringComparer.Ordinal))
        {
            return false;
        }

        if (!TryGetUniformAttribute(
                composerAttributes,
                cursorColumn,
                placeholderText.Length,
                out var placeholderAttribute))
        {
            return false;
        }

        var prefixAttribute = composerAttributes[cursorColumn - 2];
        var statusMarkerColumn = statusLine.IndexOf(ActiveTurnMarker, StringComparison.Ordinal);
        return statusMarkerColumn >= 0
            && TryGetUniformAttribute(
                statusAttributes,
                statusMarkerColumn,
                ActiveTurnMarker.Length,
                out var statusDimAttribute)
            && (placeholderAttribute & 0x0F) == (statusDimAttribute & 0x0F)
            && (placeholderAttribute & 0xF0) == (prefixAttribute & 0xF0)
            && (placeholderAttribute & 0x0F) != (prefixAttribute & 0x0F);
    }

    private static bool TryGetUniformAttribute(
        string attributes,
        int start,
        int length,
        out char attribute)
    {
        attribute = default;
        if (start < 0 || length <= 0 || start + length > attributes.Length)
        {
            return false;
        }

        attribute = attributes[start];
        return attributes.AsSpan(start, length).IndexOfAnyExcept(attribute) < 0;
    }

    private static bool TryGetSnapshotRow(
        CliConsoleSnapshot snapshot,
        int absoluteRow,
        out string line,
        out string attributes)
    {
        line = string.Empty;
        attributes = string.Empty;
        var width = snapshot.WindowRight - snapshot.WindowLeft + 1;
        var relativeRow = absoluteRow - snapshot.CapturedTop;
        var rowCount = snapshot.WindowBottom - snapshot.CapturedTop + 1;
        if (width <= 0 || relativeRow < 0 || relativeRow >= rowCount)
        {
            return false;
        }

        var textOffset = relativeRow * (width + 1);
        var attributeOffset = relativeRow * width;
        if (textOffset + width > snapshot.Text.Length
            || attributeOffset + width > snapshot.AttributeData.Length)
        {
            return false;
        }

        line = snapshot.Text.Substring(textOffset, width);
        attributes = snapshot.AttributeData.Substring(attributeOffset, width);
        return true;
    }

    private static bool HasVimFooter(CliConsoleSnapshot snapshot)
    {
        for (var row = snapshot.CursorY + 1; row <= snapshot.WindowBottom; row++)
        {
            if (TryGetSnapshotRow(snapshot, row, out var line, out _)
                && line.Contains(VimModeMarker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKnownEmptyComposerDisplay(CliConsoleSnapshot snapshot)
    {
        if (!TryGetSnapshotRow(snapshot, snapshot.CursorY, out var composerLine, out _))
        {
            return false;
        }

        var cursorColumn = snapshot.CursorX - snapshot.WindowLeft;
        if (cursorColumn != 2
            || composerLine.Length < 3
            || composerLine[0] != '›'
            || composerLine[1] != ' ')
        {
            return false;
        }

        var placeholder = composerLine[2..].TrimEnd(' ');
        return EmptyComposerPlaceholders.Contains(placeholder, StringComparer.Ordinal);
    }

    private static bool IsExpectedComposerDraft(CliConsoleSnapshot snapshot, string message)
    {
        if (!TryGetSnapshotRow(snapshot, snapshot.CursorY, out var composerLine, out _))
        {
            return false;
        }

        if (!TryGetActiveStatusRow(snapshot, out var statusRow))
        {
            return false;
        }

        for (var row = statusRow.Row + 1; row < snapshot.CursorY; row++)
        {
            if (TryGetSnapshotRow(snapshot, row, out var line, out _)
                && RemoteImagePattern.IsMatch(line))
            {
                return false;
            }
        }

        var cursorColumn = snapshot.CursorX - snapshot.WindowLeft;
        return cursorColumn == message.Length + 2
            && cursorColumn <= composerLine.Length
            && composerLine[0] == '›'
            && composerLine[1] == ' '
            && composerLine.AsSpan(2, message.Length).SequenceEqual(message)
            && composerLine.AsSpan(cursorColumn).ContainsAnyExcept(' ') is false;
    }

    private static CliConsoleSnapshot CreateTestSnapshot(
        string[] sourceLines,
        int cursorRow,
        int cursorColumn,
        bool dimStatus,
        string? dimComposerMarker)
    {
        var width = sourceLines.Max(line => line.Length) + 2;
        var lines = sourceLines.Select(line => line.PadRight(width)).ToArray();
        var attributes = Enumerable.Repeat((char)0x0F, width * lines.Length).ToArray();

        for (var row = 0; row < lines.Length; row++)
        {
            if (lines[row].Contains("› ", StringComparison.Ordinal))
            {
                Array.Fill(attributes, (char)0x1F, row * width, width);
            }

            if (dimStatus)
            {
                SetTestAttribute(lines[row], attributes, row, width, ActiveTurnMarker, (char)0x07);
            }

            if (dimComposerMarker is not null)
            {
                SetTestAttribute(lines[row], attributes, row, width, dimComposerMarker, (char)0x17);
            }
        }

        return new CliConsoleSnapshot(
            (short)width,
            (short)lines.Length,
            WindowLeft: 0,
            WindowTop: 0,
            WindowRight: (short)(width - 1),
            WindowBottom: (short)(lines.Length - 1),
            CapturedTop: 0,
            CursorX: (short)cursorColumn,
            CursorY: (short)cursorRow,
            Text: string.Join('\n', lines),
            AttributeData: new string(attributes));
    }

    private static void SetTestAttribute(
        string line,
        char[] attributes,
        int row,
        int width,
        string marker,
        char attribute)
    {
        var markerColumn = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerColumn < 0)
        {
            return;
        }

        Array.Fill(attributes, attribute, row * width + markerColumn, marker.Length);
    }

    private static bool AnyInputModifierDown() =>
        IsKeyDown(0x10)
        || IsKeyDown(0x11)
        || IsKeyDown(0x12)
        || IsKeyDown(0x5B)
        || IsKeyDown(0x5C);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool TryFindCliProcessCandidates(out List<CliProcessIdentity> candidates)
    {
        candidates = [];
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("codex");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }

        var enumerationComplete = true;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.MainModule?.FileName is not { } path)
                    {
                        continue;
                    }

                    path = Path.GetFullPath(path);
                    if (!IsExpectedOfficialPath(path))
                    {
                        continue;
                    }

                    candidates.Add(new CliProcessIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks,
                        path));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                    // A process can exit or become inaccessible while it is enumerated.
                    enumerationComplete = false;
                }
            }
        }

        return enumerationComplete;
    }

    // Reason for the last failed TryValidateTarget on this thread, for the log.
    [ThreadStatic]
    private static string? _lastValidationProblem;

    [ThreadStatic]
    private static string? _lastSubmitStage;

    private static bool TryValidateTarget(
        CodexCliTarget target,
        bool verifySignature,
        bool verifyConsole)
    {
        _lastValidationProblem = null;
        var valid = TryValidateTargetCore(target, verifySignature, verifyConsole);
        _lastValidationProblem ??= valid ? null : "process";
        return valid;
    }

    private static bool TryValidateTargetCore(
        CodexCliTarget target,
        bool verifySignature,
        bool verifyConsole)
    {
        if (target.WindowHandle == nint.Zero
            || GetForegroundWindow() != target.WindowHandle)
        {
            _lastValidationProblem = "not-foreground";
            return false;
        }

        if (!IsSupportedConsoleHost(
                GetWindowClass(target.WindowHandle),
                GetWindowProcessName(target.WindowHandle)))
        {
            _lastValidationProblem = "host-class";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited
                || process.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks
                || process.MainModule?.FileName is not { } path
                || !Path.GetFullPath(path).Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || !IsExpectedOfficialPath(path)
                || (verifySignature && !IsAuthenticOpenAiExecutable(path)))
            {
                return false;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }

        if (!verifyConsole)
        {
            return true;
        }

        if (!TryGetAttachedConsoleWindow(target.ProcessId, out var consoleWindow, out var consoleProblem))
        {
            _lastValidationProblem = "console:" + consoleProblem;
            return false;
        }

        if (consoleWindow != target.WindowHandle)
        {
            _lastValidationProblem = "console-window";
            return false;
        }

        return true;
    }

    private static string ValidationSuffix() =>
        _lastValidationProblem is { } problem ? $" [{problem}]" : string.Empty;

    // Returns the top-level window that displays the process console. Under
    // Windows Terminal the console is a hidden pseudo window owned by the terminal
    // window, and the process must live in the single visible, selected tab.
    private static bool TryGetAttachedConsoleWindow(int processId, out nint windowHandle) =>
        TryGetAttachedConsoleWindow(processId, out windowHandle, out _);

    // The terminal title can change for a moment (for example right after a
    // steer), so a title mismatch is re-read a few times before failing.
    private static bool TryGetAttachedConsoleWindow(
        int processId,
        out nint windowHandle,
        out string problem)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (TryGetAttachedConsoleWindowOnce(processId, out windowHandle, out problem)
                || problem != "title-mismatch"
                || attempt >= 2)
            {
                return windowHandle != nint.Zero && problem.Length == 0;
            }

            Thread.Sleep(40);
        }
    }

    private static bool TryGetAttachedConsoleWindowOnce(
        int processId,
        out nint windowHandle,
        out string problem)
    {
        var isPseudoConsole = false;
        var title = string.Empty;
        problem = string.Empty;
        lock (ConsoleLock)
        {
            windowHandle = nint.Zero;
            FreeConsole();
            if (!AttachConsole((uint)processId))
            {
                problem = "attach-failed";
                return false;
            }

            try
            {
                if (!AttachedConsoleContains(processId))
                {
                    problem = "not-in-console";
                    return false;
                }

                var console = GetConsoleWindow();
                if (console != nint.Zero
                    && GetWindowClass(console) == PseudoConsoleClass)
                {
                    isPseudoConsole = true;
                    windowHandle = GetAncestor(console, GetAncestorRootOwner);
                    var titleBuffer = new StringBuilder(512);
                    title = GetConsoleTitle(titleBuffer, (uint)titleBuffer.Capacity) > 0
                        ? titleBuffer.ToString()
                        : string.Empty;
                }
                else
                {
                    windowHandle = console;
                }
            }
            finally
            {
                FreeConsole();
            }
        }

        if (windowHandle == nint.Zero)
        {
            problem = "no-window";
            return false;
        }

        if (isPseudoConsole)
        {
            problem = TerminalTabProblem(windowHandle, title);
            if (problem.Length > 0)
            {
                windowHandle = nint.Zero;
                return false;
            }
        }

        return true;
    }

    // Codex CLI animates a spinner glyph (Braille dots) in front of the title while
    // a turn runs; the console title and the tab name are read a few milliseconds
    // apart, so the glyph can differ between the two reads.
    private static string StripSpinnerPrefix(string title) =>
        title.Length >= 2
        && title[1] == ' '
        && (title[0] is >= '⠀' and <= '⣿' || "✳✶✻✽·•◐◓◑◒".Contains(title[0]))
            ? title[2..]
            : title;

    // Typing goes to whichever tab and pane is active, so the console's own title
    // must match the one selected tab, and that tab must show a single pane.
    // Returns an empty string when the tab is valid, otherwise the reason.
    private static string TerminalTabProblem(nint terminalWindow, string consoleTitle)
    {
        if (consoleTitle.Length == 0)
        {
            return "no-title";
        }

        if (!GetWindowProcessName(terminalWindow).Equals(
                "WindowsTerminal",
                StringComparison.OrdinalIgnoreCase))
        {
            return "not-windows-terminal";
        }

        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(terminalWindow);
            var tabs = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.TabItem));
            var selectedNames = new List<string>();
            for (var index = 0; index < tabs.Count; index++)
            {
                if (tabs[index].TryGetCurrentPattern(
                        System.Windows.Automation.SelectionItemPattern.Pattern,
                        out var pattern)
                    && ((System.Windows.Automation.SelectionItemPattern)pattern).Current.IsSelected)
                {
                    selectedNames.Add(tabs[index].Current.Name);
                }
            }

            if (selectedNames.Count != 1)
            {
                return $"selected-tabs={selectedNames.Count}";
            }

            if (!string.Equals(
                    StripSpinnerPrefix(selectedNames[0]),
                    StripSpinnerPrefix(consoleTitle),
                    StringComparison.Ordinal))
            {
                return "title-mismatch";
            }

            var panes = root.FindAll(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.AndCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ClassNameProperty,
                        "TermControl"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.IsOffscreenProperty,
                        false)));
            return panes.Count == 1 ? string.Empty : $"visible-panes={panes.Count}";
        }
        catch (Exception exception) when (
            exception is System.Windows.Automation.ElementNotAvailableException
            or InvalidOperationException
            or COMException)
        {
            return "uia-error";
        }
    }

    private static bool TryReadConsoleSnapshot(int processId, out CliConsoleSnapshot snapshot)
    {
        lock (ConsoleLock)
        {
            snapshot = default!;
            FreeConsole();
            if (!AttachConsole((uint)processId))
            {
                return false;
            }

            try
            {
                if (!AttachedConsoleContains(processId))
                {
                    return false;
                }

                using var output = CreateFile(
                    "CONOUT$",
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    nint.Zero,
                    OpenExisting,
                    0,
                    nint.Zero);
                if (output.IsInvalid
                    || !GetConsoleScreenBufferInfo(output, out var info)
                    || !GetConsoleSelectionInfo(out var selection)
                    || selection.Flags != 0)
                {
                    return false;
                }

                var width = info.Window.Right - info.Window.Left + 1;
                var top = info.Window.Top;
                if (width <= 0 || top < 0 || info.Window.Bottom < top)
                {
                    return false;
                }

                var lines = new List<string>(info.Window.Bottom - top + 1);
                var attributeData = new StringBuilder(width * (info.Window.Bottom - top + 1));
                for (var row = top; row <= info.Window.Bottom; row++)
                {
                    var buffer = new char[width];
                    var attributes = new ushort[width];
                    if (!ReadConsoleOutputCharacter(
                            output,
                            buffer,
                            (uint)width,
                            new Coord((short)info.Window.Left, (short)row),
                            out var charactersRead)
                        || charactersRead != width
                        || !ReadConsoleOutputAttribute(
                            output,
                            attributes,
                            (uint)width,
                            new Coord((short)info.Window.Left, (short)row),
                            out var attributesRead)
                        || attributesRead != width)
                    {
                        return false;
                    }

                    lines.Add(new string(buffer, 0, (int)charactersRead));
                    foreach (var attribute in attributes)
                    {
                        attributeData.Append((char)attribute);
                    }
                }

                snapshot = new CliConsoleSnapshot(
                    info.Size.X,
                    info.Size.Y,
                    info.Window.Left,
                    info.Window.Top,
                    info.Window.Right,
                    info.Window.Bottom,
                    (short)top,
                    info.CursorPosition.X,
                    info.CursorPosition.Y,
                    string.Join('\n', lines),
                    attributeData.ToString());
                return true;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private static bool AttachedConsoleContains(int processId)
    {
        var processIds = new uint[16];
        var processCount = GetConsoleProcessList(processIds, (uint)processIds.Length);
        if (processCount == 0)
        {
            return false;
        }

        if (processCount > processIds.Length)
        {
            processIds = new uint[processCount];
            processCount = GetConsoleProcessList(processIds, (uint)processIds.Length);
        }

        return processIds.Take((int)Math.Min(processCount, (uint)processIds.Length))
            .Contains((uint)processId);
    }

    private static CliSubmitOutcome SendUnicodeTextAndEnter(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken,
        Func<bool>? tryCommit)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CliSubmitOutcome.Cancelled;
        }

        var inputs = new List<Input>(message.Length * 2);
        foreach (var character in message)
        {
            inputs.Add(Input.Unicode(character, keyUp: false));
            inputs.Add(Input.Unicode(character, keyUp: true));
        }

        if (tryCommit is not null && !tryCommit())
        {
            return CliSubmitOutcome.Cancelled;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CliSubmitOutcome.Cancelled;
        }

        _lastSubmitStage = "type";
        var inputArray = inputs.ToArray();
        if (SendInput((uint)inputArray.Length, inputArray, Marshal.SizeOf<Input>()) != inputArray.Length)
        {
            return CliSubmitOutcome.Failed;
        }

        _lastSubmitStage = "draft";
        if (WaitForExpectedDraft(target, message, cancellationToken))
        {
            _lastSubmitStage = "draft-recheck";
        }

        if (_lastSubmitStage != "draft-recheck"
            || !TryVerifyExpectedDraft(target, message))
        {
            return cancellationToken.IsCancellationRequested
                ? CliSubmitOutcome.Cancelled
                : CliSubmitOutcome.Failed;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CliSubmitOutcome.Cancelled;
        }

        Input[] enterInputs =
        [
            Input.VirtualKey(VirtualKeyReturn, keyUp: false),
            Input.VirtualKey(VirtualKeyReturn, keyUp: true)
        ];
        _lastSubmitStage = "enter";
        if (SendInput((uint)enterInputs.Length, enterInputs, Marshal.SizeOf<Input>()) != enterInputs.Length)
        {
            return CliSubmitOutcome.Failed;
        }

        _lastSubmitStage = "pending-preview";
        return WaitForPendingSteerAndInterrupt(
            target,
            message,
            cancellationToken);
    }

    private static bool WaitForExpectedDraft(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested
            && timer.ElapsedMilliseconds < CliDraftConfirmationTimeoutMilliseconds)
        {
            Thread.Sleep(CliDraftConfirmationPollMilliseconds);
            if (TryVerifyExpectedDraft(target, message))
            {
                return true;
            }
        }

        return false;
    }

    private static CliSubmitOutcome WaitForPendingSteerAndInterrupt(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < CliSubmitConfirmationTimeoutMilliseconds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CliSubmitOutcome.Cancelled;
            }

            Thread.Sleep(CliSubmitConfirmationPollMilliseconds);
            if (cancellationToken.IsCancellationRequested)
            {
                return CliSubmitOutcome.Cancelled;
            }

            if (!TryReadConsoleSnapshot(target.ProcessId, out var snapshot))
            {
                continue;
            }

            if (IsPendingSteerReadyForEscape(snapshot, message))
            {
                var modifiersClear = !AnyInputModifierDown();
                var targetValid = TryValidateTarget(
                    target,
                    verifySignature: false,
                    verifyConsole: true);
                var previewStillExact = TryReadConsoleSnapshot(
                        target.ProcessId,
                        out var immediatelyBeforeEscape)
                    && IsPendingSteerReadyForEscape(immediatelyBeforeEscape, message);
                var foregroundMatches = GetForegroundWindow() == target.WindowHandle;
                var decision = ClassifyInterruptDecision(
                    cancellationToken.IsCancellationRequested,
                    modifiersClear && !AnyInputModifierDown(),
                    targetValid,
                    previewStillExact,
                    foregroundMatches);
                if (decision == CliInterruptDecision.Cancelled)
                {
                    return CliSubmitOutcome.Cancelled;
                }

                if (decision == CliInterruptDecision.Reject)
                {
                    _lastSubmitStage = $"escape-rejected(mod={modifiersClear},target={targetValid},"
                        + $"preview={previewStillExact},fg={foregroundMatches})";
                    return CliSubmitOutcome.Failed;
                }

                Input[] escapeInputs =
                [
                    Input.VirtualKey(VirtualKeyEscape, keyUp: false),
                    Input.VirtualKey(VirtualKeyEscape, keyUp: true)
                ];
                if (cancellationToken.IsCancellationRequested)
                {
                    return CliSubmitOutcome.Cancelled;
                }

                if (AnyInputModifierDown())
                {
                    return CliSubmitOutcome.Failed;
                }

                if (SendInput(
                        (uint)escapeInputs.Length,
                        escapeInputs,
                        Marshal.SizeOf<Input>()) != escapeInputs.Length)
                {
                    return CliSubmitOutcome.Failed;
                }

                _lastSubmitStage = "escape-confirm";
                return WaitForPendingSteerConsumption(
                    target,
                    message,
                    cancellationToken);
            }
        }

        return cancellationToken.IsCancellationRequested
            ? CliSubmitOutcome.Cancelled
            : CliSubmitOutcome.Failed;
    }

    private static CliSubmitOutcome WaitForPendingSteerConsumption(
        CodexCliTarget target,
        string message,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var consecutiveAbsentObservations = 0;
        while (timer.ElapsedMilliseconds < CliInterruptConfirmationTimeoutMilliseconds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CliSubmitOutcome.Cancelled;
            }

            Thread.Sleep(CliSubmitConfirmationPollMilliseconds);
            if (!TryReadConsoleSnapshot(target.ProcessId, out var snapshot))
            {
                consecutiveAbsentObservations = 0;
                continue;
            }

            if (!TryValidateTarget(target, verifySignature: false, verifyConsole: true)
                || GetForegroundWindow() != target.WindowHandle
                || !IsPendingSteerConsumptionConfirmed(snapshot, message))
            {
                consecutiveAbsentObservations = 0;
                continue;
            }

            consecutiveAbsentObservations++;
            if (consecutiveAbsentObservations >= 3)
            {
                return CliSubmitOutcome.Steered;
            }
        }

        return cancellationToken.IsCancellationRequested
            ? CliSubmitOutcome.Cancelled
            : CliSubmitOutcome.Failed;
    }

    private static CliInterruptDecision ClassifyInterruptDecision(
        bool cancelled,
        bool modifiersClear,
        bool targetValid,
        bool previewStillExact,
        bool foregroundMatches)
    {
        if (cancelled)
        {
            return CliInterruptDecision.Cancelled;
        }

        return modifiersClear
            && targetValid
            && previewStillExact
            && foregroundMatches
                ? CliInterruptDecision.SendEscape
                : CliInterruptDecision.Reject;
    }

    private static bool TryVerifyExpectedDraft(CodexCliTarget target, string message) =>
        !AnyInputModifierDown()
        && TryValidateTarget(target, verifySignature: false, verifyConsole: true)
        && TryReadConsoleSnapshot(target.ProcessId, out var draft)
        && AnalyzeScreen(draft) == CliScreenState.DraftPresent
        && IsExpectedComposerDraft(draft, message)
        && GetForegroundWindow() == target.WindowHandle
        && !AnyInputModifierDown();

    private static bool IsExpectedOfficialPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        const string packageMarker = @"\node_modules\@openai\codex-win32-";
        const string vendorMarker = @"\vendor\";
        var packageIndex = normalized.IndexOf(packageMarker, StringComparison.OrdinalIgnoreCase);
        if (packageIndex < 0)
        {
            return false;
        }

        var platformStart = packageIndex + packageMarker.Length;
        var vendorIndex = normalized.IndexOf(vendorMarker, platformStart, StringComparison.OrdinalIgnoreCase);
        if (vendorIndex <= platformStart
            || normalized.AsSpan(platformStart, vendorIndex - platformStart).Contains('\\'))
        {
            return false;
        }

        var tail = normalized[(vendorIndex + vendorMarker.Length)..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 3
            && !string.IsNullOrWhiteSpace(tail[0])
            && tail[1].Equals("bin", StringComparison.OrdinalIgnoreCase)
            && tail[2].Equals("codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthenticOpenAiExecutable(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return false;
            }

            Lazy<bool> verification;
            lock (SignatureCacheLock)
            {
                if (SignatureCache.TryGetValue(path, out var cached)
                    && cached.Length == file.Length
                    && cached.LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks)
                {
                    verification = cached.Verification;
                }
                else
                {
                    verification = new Lazy<bool>(
                        () => VerifyAuthenticodeTrust(path) && HasOpenAiSigner(path),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    SignatureCache[path] = new SignatureCacheEntry(
                        file.Length,
                        file.LastWriteTimeUtc.Ticks,
                        verification);
                }
            }

            return verification.Value;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static void QueueSignatureVerification(string path)
    {
        _ = Task.Run(() =>
        {
            try
            {
                _ = IsAuthenticOpenAiExecutable(path);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Codex CLI signature pre-verification failed: {exception}");
            }
        });
    }

    private static bool VerifyAuthenticodeTrust(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = nint.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateActionIgnore,
                ProviderFlags = WinTrustCacheOnlyUrlRetrieval
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(nint.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            if (fileInfoPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    private static bool HasOpenAiSigner(string path)
    {
        using var certificate = X509Certificate.CreateFromSignedFile(path);
        using var signer = new X509Certificate2(certificate);
        return signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
            .Equals("OpenAI OpCo, LLC", StringComparison.Ordinal);
    }

    private static bool IsClassicConsole(string windowClass) =>
        windowClass.Equals(ClassicConsoleClass, StringComparison.Ordinal);

    private static bool IsWindowsTerminal(string windowClass, string processName) =>
        windowClass.Equals(TerminalHostClass, StringComparison.Ordinal)
        && processName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedConsoleHost(string windowClass, string processName) =>
        IsClassicConsole(windowClass) || IsWindowsTerminal(windowClass, processName);

    private static bool IsUnsupportedTerminal(string windowClass, string processName) =>
        processName.Equals("Code", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("Code - Insiders", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("mintty", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("wezterm-gui", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("alacritty", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("Hyper", StringComparison.OrdinalIgnoreCase)
        || processName.Equals("Tabby", StringComparison.OrdinalIgnoreCase);

    private static string GetWindowClass(nint windowHandle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(windowHandle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetWindowProcessName(nint windowHandle)
    {
        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private enum CliScreenState
    {
        Inactive,
        DraftPresent,
        Ready
    }

    private sealed record CliProcessIdentity(int ProcessId, long StartTimeTicks, string ExecutablePath);

    private sealed record CliConsoleSnapshot(
        short BufferWidth,
        short BufferHeight,
        short WindowLeft,
        short WindowTop,
        short WindowRight,
        short WindowBottom,
        short CapturedTop,
        short CursorX,
        short CursorY,
        string Text,
        string AttributeData);

    private sealed record SignatureCacheEntry(
        long Length,
        long LastWriteTimeUtcTicks,
        Lazy<bool> Verification);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        internal readonly short X;
        internal readonly short Y;

        internal Coord(short x, short y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        internal short Left;
        internal short Top;
        internal short Right;
        internal short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        internal Coord Size;
        internal Coord CursorPosition;
        internal ushort Attributes;
        internal SmallRect Window;
        internal Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleSelectionInfo
    {
        internal uint Flags;
        internal Coord SelectionAnchor;
        internal SmallRect Selection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;

        internal static Input Unicode(char character, bool keyUp) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = character,
                    Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
                }
            }
        };

        internal static Input VirtualKey(ushort virtualKey, bool keyUp) => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructureSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
    }

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetConsoleTitle(StringBuilder title, uint size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processIds, uint processCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(
        SafeFileHandle consoleOutput,
        out ConsoleScreenBufferInfo consoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleSelectionInfo(out ConsoleSelectionInfo consoleSelectionInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleOutputCharacter(
        SafeFileHandle consoleOutput,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] char[] characterBuffer,
        uint length,
        Coord readCoordinate,
        out uint charactersRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleOutputAttribute(
        SafeFileHandle consoleOutput,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] ushort[] attributeBuffer,
        uint length,
        Coord readCoordinate,
        out uint attributesRead);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);
}

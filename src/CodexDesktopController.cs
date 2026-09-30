using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CodexWhip;

internal sealed record SteerResult(
    bool Success,
    string Code,
    string Message,
    nint WindowHandle = default);

internal sealed class CodexDesktopController
{
    private static readonly string[] EmptyComposerLabels = AppLocalizer.EmptyComposerLabels;
    private static readonly string[] StopButtonLabels = AppLocalizer.StopButtonLabels;
    private static readonly string[] KnownButtonLabels = [.. StopButtonLabels, "Steer"];
    private const int SteerDiscoveryTimeoutMilliseconds = 2500;
    private const int AccessibilityWarmupMilliseconds = 2000;
    private const int ModifierReleaseTimeoutMilliseconds = 400;
    private const int StopSearchLevels = 3;

    // Electron builds its web accessibility tree only after a UI Automation
    // client first asks for it; remember which process already exposed it.
    private volatile int _accessibilityReadyProcessId;

    // Last step reached by TrySteer, for the steering log.
    public string LastStage { get; private set; } = string.Empty;

    public void PrewarmAccessibility() =>
        _ = Task.Run(() =>
        {
            try
            {
                if (TryFindCodexWindow(out var target))
                {
                    _ = FindComposerCandidates(target);
                }
            }
            catch (Exception exception) when (IsExpectedAutomationException(exception))
            {
                Debug.WriteLine($"Accessibility prewarm skipped: {exception.Message}");
            }
        });

    public bool TryGetCodexWindowHandle(out nint handle)
    {
        if (TryFindCodexWindow(out var target))
        {
            handle = target.Handle;
            return true;
        }

        handle = default;
        return false;
    }

    public bool IsCodexForeground()
    {
        return TryFindCodexWindow(out var target)
            && ForegroundWindowBelongsTo(target.ProcessId);
    }

    public SteerResult Diagnose()
    {
        return TryLocateActiveComposer(out var target, out _, out _)
            ? new SteerResult(
                true,
                "READY",
                "Codex Desktop is working and its composer is empty.",
                target.Handle)
            : _lastLocateFailure!;
    }

    public SteerResult TrySteer(string message)
    {
        return TrySteer(message, activateCodex: true, null, out _, afterSteerInvoked: null);
    }

    public SteerResult TrySteerForegroundOnly(string message, SteerAttempt attempt)
    {
        return TrySteer(message, activateCodex: false, attempt, out _, afterSteerInvoked: null);
    }

    public SteerResult TrySteerAndRestoreForeground(
        string message,
        SteerAttempt? attempt = null)
    {
        var previousForeground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(previousForeground, out var previousProcessId);
        nint activatedCodexWindow = nint.Zero;
        try
        {
            return TrySteer(
                message,
                activateCodex: true,
                attempt,
                out activatedCodexWindow,
                () => RestoreForegroundIfStillOwned(
                    previousForeground,
                    previousProcessId,
                    activatedCodexWindow));
        }
        finally
        {
            RestoreForegroundIfStillOwned(
                previousForeground,
                previousProcessId,
                activatedCodexWindow);
        }
    }

    private SteerResult TrySteer(
        string message,
        bool activateCodex,
        SteerAttempt? attempt,
        out nint activatedCodexWindow,
        Action? afterSteerInvoked)
    {
        activatedCodexWindow = nint.Zero;
        LastStage = "locate";
        CodexTarget target = default;
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return new SteerResult(false, "EMPTY_MESSAGE", "The steering message is empty.");
            }

            if (!TryLocateActiveComposer(out target, out var composer, out var stopButton))
            {
                return _lastLocateFailure!;
            }

            if (attempt?.IsCancellationRequested == true)
            {
                return new SteerResult(false, "CANCELLED", "Automatic steering was cancelled.", target.Handle);
            }

            if (!activateCodex && !ForegroundWindowBelongsTo(target.ProcessId))
            {
                return new SteerResult(false, "FOCUS_NOT_GRANTED", "Codex is no longer in the foreground.", target.Handle);
            }

            if (activateCodex)
            {
                LastStage = "activate";
                if (!TryActivateWindow(target.Handle))
                {
                    return new SteerResult(
                        false,
                        "FOCUS_NOT_GRANTED",
                        "Windows did not bring Codex to the foreground, so no text was sent.",
                        target.Handle);
                }

                activatedCodexWindow = target.Handle;

                if (!TryRelocateActiveComposer(
                        target.Handle,
                        attempt,
                        out var activatedTarget,
                        out var activatedComposer,
                        out var activatedStopButton))
                {
                    if (attempt?.IsCancellationRequested == true)
                    {
                        return new SteerResult(
                            false,
                            "CANCELLED",
                            "Automatic steering was cancelled while Codex was restored.",
                            target.Handle);
                    }

                    return _lastLocateFailure
                        ?? new SteerResult(
                            false,
                            "UI_CHANGED",
                            "The Codex interface changed while its window was restored.",
                            target.Handle);
                }

                target = activatedTarget;
                composer = activatedComposer;
                stopButton = activatedStopButton;
            }

            if (!TryGetRuntimeId(stopButton, out var expectedStopRuntimeId))
            {
                return new SteerResult(
                    false,
                    "UI_CHANGED",
                    "The active Codex turn could not be identified safely.",
                    target.Handle);
            }

            LastStage = "focus";
            try
            {
                composer.SetFocus();
            }
            catch (ElementNotAvailableException)
            {
                return new SteerResult(false, "COMPOSER_STALE", "The Codex composer changed. Try again.", target.Handle);
            }
            catch (InvalidOperationException)
            {
                return new SteerResult(false, "FOCUS_FAILED", "The Codex composer could not receive focus.", target.Handle);
            }

            Thread.Sleep(activateCodex ? 60 : 20);

            if (!ForegroundWindowBelongsTo(target.ProcessId))
            {
                return new SteerResult(
                    false,
                    "FOCUS_NOT_GRANTED",
                    "Codex did not receive focus, so no text was sent.",
                    target.Handle);
            }

            var focused = AutomationElement.FocusedElement;
            var composerHasFocus = focused is not null
                && focused.Current.ProcessId == target.ProcessId
                && IsDescendantOrSelf(focused, composer);
            if (!composerHasFocus
                && activateCodex
                && TryRelocateActiveComposer(
                    target.Handle,
                    attempt,
                    out var focusedTarget,
                    out var focusedComposer,
                    out var focusedStopButton)
                && IsExpectedStopElement(focusedTarget, focusedStopButton, expectedStopRuntimeId))
            {
                target = focusedTarget;
                composer = focusedComposer;
                stopButton = focusedStopButton;
                composer.SetFocus();
                Thread.Sleep(40);
                focused = AutomationElement.FocusedElement;
                composerHasFocus = focused is not null
                    && focused.Current.ProcessId == target.ProcessId
                    && IsDescendantOrSelf(focused, composer);
            }

            if (!composerHasFocus)
            {
                return new SteerResult(
                    false,
                    "COMPOSER_NOT_FOCUSED",
                    "The cursor is not in the Codex composer, so no text was sent.",
                    target.Handle);
            }

            LastStage = "guard";
            if (!TryIsComposerEmpty(composer, out var isComposerEmpty))
            {
                return new SteerResult(
                    false,
                    "COMPOSER_UNREADABLE",
                    "The Codex composer could not be read safely.",
                    target.Handle);
            }

            if (!isComposerEmpty)
            {
                return new SteerResult(
                    false,
                    "DRAFT_PRESENT",
                    "A draft is already present in Codex and was left untouched.",
                    target.Handle);
            }

            if (!WaitForModifiersReleased())
            {
                return ModifierHeldResult(target.Handle);
            }

            if (!TryCaptureSteerBaseline(
                    target,
                    composer,
                    stopButton,
                    expectedStopRuntimeId,
                    out var steerBaseline))
            {
                return new SteerResult(false, "TURN_CHANGED", "The Codex composer changed before sending.", target.Handle);
            }

            if (AreInputModifiersPressed())
            {
                return ModifierHeldResult(target.Handle);
            }

            if (!IsComposerReadyForInput(target, composer)
                || !IsExpectedStopElement(target, stopButton, expectedStopRuntimeId))
            {
                return new SteerResult(
                    false,
                    "TURN_CHANGED",
                    "The active Codex turn changed before sending.",
                    target.Handle);
            }

            if (attempt is not null && !attempt.TryCommit())
            {
                return new SteerResult(
                    false,
                    "CANCELLED",
                    "Automatic steering was cancelled before sending.",
                    target.Handle);
            }

            if (AreInputModifiersPressed())
            {
                return ModifierHeldResult(target.Handle);
            }

            if (GetForegroundWindow() != target.Handle)
            {
                return new SteerResult(
                    false,
                    "FOCUS_NOT_GRANTED",
                    "Codex lost the foreground immediately before sending.",
                    target.Handle);
            }

            if (!IsComposerReadyForInput(target, composer)
                || !IsExpectedStopElement(target, stopButton, expectedStopRuntimeId))
            {
                return new SteerResult(
                    false,
                    "TURN_CHANGED",
                    "The active Codex turn changed immediately before sending.",
                    target.Handle);
            }

            LastStage = "type";
            activatedCodexWindow = target.Handle;
            var inputSent = SendUnicodeTextAndEnter(message);
            if (!inputSent)
            {
                return new SteerResult(
                    false,
                    "SEND_INPUT_FAILED",
                    "Windows did not accept the complete keyboard sequence.",
                    target.Handle);
            }

            LastStage = "confirm";
            if (!TryInvokeSteerAction(
                    target,
                    steerBaseline,
                    message,
                    afterSteerInvoked))
            {
                return new SteerResult(
                    false,
                    "STEER_ACTION_FAILED",
                    "The queued message was created, but Codex did not expose its matching Steer action.",
                    target.Handle);
            }

            return new SteerResult(
                true,
                "STEERED",
                "Message sent to the active Codex task.",
                target.Handle);
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return new SteerResult(
                false,
                "UI_CHANGED",
                "The Codex interface changed during steering.",
                target.Handle);
        }
    }

    private bool TryRelocateActiveComposer(
        nint expectedWindowHandle,
        SteerAttempt? attempt,
        out CodexTarget target,
        out AutomationElement composer,
        out AutomationElement stopButton)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            if (attempt?.IsCancellationRequested == true)
            {
                break;
            }

            if (TryLocateActiveComposer(out target, out composer, out stopButton)
                && target.Handle == expectedWindowHandle)
            {
                return true;
            }

            Thread.Sleep(30);
        }
        while (timeout.ElapsedMilliseconds < 800);

        target = default;
        composer = null!;
        stopButton = null!;
        return false;
    }

    private SteerResult? _lastLocateFailure;

    private bool TryLocateActiveComposer(
        out CodexTarget target,
        out AutomationElement composer,
        out AutomationElement stopButton)
    {
        composer = null!;
        stopButton = null!;

        if (!TryFindCodexWindow(out target))
        {
            _lastLocateFailure = new SteerResult(
                false,
                "CODEX_NOT_FOUND",
                "The official Codex Desktop window could not be found.");
            return false;
        }

        try
        {
            var candidates = FindComposerCandidates(target);
            var stopSeen = false;
            foreach (var candidate in candidates)
            {
                var stop = FindStopButtonNear(target, candidate);
                if (stop is null)
                {
                    continue;
                }

                stopSeen = true;
                if (!IsComposerGeometry(
                        candidate.Current.BoundingRectangle,
                        stop.Current.BoundingRectangle,
                        GetDpiScale(target.Handle)))
                {
                    continue;
                }

                if (!TryIsComposerEmpty(candidate, out var isComposerEmpty))
                {
                    _lastLocateFailure = new SteerResult(
                        false,
                        "COMPOSER_UNREADABLE",
                        "The Codex composer could not be read safely.",
                        target.Handle);
                    return false;
                }

                if (!isComposerEmpty)
                {
                    _lastLocateFailure = new SteerResult(
                        false,
                        "DRAFT_PRESENT",
                        "A draft is already present in Codex and was left untouched.",
                        target.Handle);
                    return false;
                }

                composer = candidate;
                stopButton = stop;
                _lastLocateFailure = null;
                return true;
            }

            // An empty candidate list after the web tree was already exposed means
            // Codex shows a page without a composer, so there is no active turn.
            _lastLocateFailure = stopSeen || (candidates.Count == 0
                && _accessibilityReadyProcessId != target.ProcessId)
                ? new SteerResult(
                    false,
                    "COMPOSER_NOT_FOUND",
                    "The Codex composer was not recognized, so no text was sent.",
                    target.Handle)
                : new SteerResult(
                    false,
                    "NO_ACTIVE_TURN",
                    "Codex is open, but no active task is showing the Stop button.",
                    target.Handle);
            return false;
        }
        catch (ElementNotAvailableException)
        {
            _lastLocateFailure = new SteerResult(
                false,
                "UI_CHANGED",
                "The Codex interface changed during diagnostics. Try again.",
                target.Handle);
            return false;
        }
    }

    private static bool TryFindCodexWindow(out CodexTarget target)
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == nint.Zero)
                    {
                        continue;
                    }

                    var executablePath = process.MainModule?.FileName;
                    if (!IsOfficialCodexExecutable(executablePath))
                    {
                        continue;
                    }

                    var root = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (root is null)
                    {
                        continue;
                    }

                    target = new CodexTarget(process.Id, process.MainWindowHandle, root);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // The process can exit while it is being inspected.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Fail closed when the executable path cannot be verified.
                }
            }
        }

        target = default;
        return false;
    }

    private static bool IsOfficialCodexExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || path.Contains("OpenAI\\Codex", StringComparison.OrdinalIgnoreCase);
    }

    // Searches upward from the composer so the common case scans a few buttons
    // instead of the whole window; the full-window scan remains the fallback.
    private static AutomationElement? FindStopButtonNear(
        CodexTarget target,
        AutomationElement composer)
    {
        var scope = composer;
        for (var level = 0; level < StopSearchLevels; level++)
        {
            scope = TreeWalker.ControlViewWalker.GetParent(scope);
            if (scope is null)
            {
                break;
            }

            var stop = FindStopButton(target, scope);
            if (stop is not null)
            {
                return stop;
            }
        }

        return FindStopButton(target, target.Root);
    }

    private static AutomationElement? FindStopButton(CodexTarget target, AutomationElement scope)
    {
        var rootBounds = GetTargetBounds(target);
        if (rootBounds.IsEmpty)
        {
            return null;
        }

        var scale = GetDpiScale(target.Handle);
        var buttons = scope.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        AutomationElement? bestMatch = null;
        var bestScore = double.MaxValue;
        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            var name = button.Current.Name?.Trim();
            if (!StopButtonLabels.Any(label => string.Equals(name, label, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var bounds = button.Current.BoundingRectangle;
            if (button.Current.ProcessId != target.ProcessId
                || !button.Current.IsEnabled
                || button.Current.IsOffscreen
                || !button.TryGetCurrentPattern(InvokePattern.Pattern, out _)
                || !IsStopButtonGeometry(rootBounds, bounds, scale))
            {
                continue;
            }

            var score = (rootBounds.Bottom - bounds.Bottom) + (rootBounds.Right - bounds.Right);
            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = button;
            }
        }

        return bestMatch;
    }

    // Pixel limits are expressed at 100% display scaling and multiplied by scale.
    private static bool IsStopButtonGeometry(
        System.Windows.Rect rootBounds,
        System.Windows.Rect bounds,
        double scale = 1.0) =>
        !bounds.IsEmpty
        && bounds.Left >= rootBounds.Left + (rootBounds.Width * 0.5)
        && bounds.Top >= rootBounds.Top + (rootBounds.Height * 0.5)
        && bounds.Right <= rootBounds.Right
        && bounds.Bottom <= rootBounds.Bottom
        && bounds.Width <= 120 * scale
        && bounds.Height <= 80 * scale;

    private static bool IsComposerGeometry(
        System.Windows.Rect bounds,
        System.Windows.Rect stopBounds,
        double scale = 1.0) =>
        !bounds.IsEmpty
        && bounds.Width >= 180 * scale
        && bounds.Height >= 24 * scale
        && bounds.Height <= 140 * scale
        && bounds.Left < stopBounds.Left
        && bounds.Right >= stopBounds.Right - (24 * scale)
        && Math.Abs(stopBounds.Top - bounds.Bottom) <= 140 * scale;

    private static double GetDpiScale(nint windowHandle)
    {
        var dpi = windowHandle == nint.Zero ? 0 : GetDpiForWindow(windowHandle);
        return dpi >= 96 ? dpi / 96.0 : 1.0;
    }

    private static System.Windows.Rect GetTargetBounds(CodexTarget target)
    {
        var automationBounds = target.Root.Current.BoundingRectangle;
        if (!automationBounds.IsEmpty)
        {
            return automationBounds;
        }

        if (!IsIconic(target.Handle)
            && GetWindowRect(target.Handle, out var bounds)
            && bounds.Right > bounds.Left
            && bounds.Bottom > bounds.Top)
        {
            return new System.Windows.Rect(
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top);
        }

        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>()
        };
        return GetWindowPlacement(target.Handle, ref placement)
            && placement.NormalPosition.Right > placement.NormalPosition.Left
            && placement.NormalPosition.Bottom > placement.NormalPosition.Top
                ? new System.Windows.Rect(
                    placement.NormalPosition.Left,
                    placement.NormalPosition.Top,
                    placement.NormalPosition.Right - placement.NormalPosition.Left,
                    placement.NormalPosition.Bottom - placement.NormalPosition.Top)
                : System.Windows.Rect.Empty;
    }

    private static List<AutomationElement> FindNamedSteerButtons(AutomationElement root)
    {
        var buttons = root.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Steer")));
        var result = new List<AutomationElement>(buttons.Count);
        for (var index = 0; index < buttons.Count; index++)
        {
            result.Add(buttons[index]);
        }

        return result;
    }

    private static List<AutomationElement> FindValidSteerButtons(
        CodexTarget target,
        System.Windows.Rect rootBounds,
        System.Windows.Rect stopBounds)
    {
        var result = new List<AutomationElement>();
        var scale = GetDpiScale(target.Handle);
        foreach (var button in FindNamedSteerButtons(target.Root))
        {
            var bounds = button.Current.BoundingRectangle;
            if (button.Current.ProcessId != target.ProcessId
                || !button.Current.IsEnabled
                || button.Current.IsOffscreen
                || !IsSteerButtonGeometry(rootBounds, stopBounds, bounds, scale)
                || !button.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                continue;
            }

            result.Add(button);
        }

        return result;
    }

    private static bool TryCaptureSteerBaseline(
        CodexTarget target,
        AutomationElement composer,
        AutomationElement stopButton,
        string expectedStopRuntimeId,
        out SteerBaseline baseline)
    {
        baseline = default;
        if (!IsComposerReadyForInput(target, composer)
            || !IsExpectedStopElement(target, stopButton, expectedStopRuntimeId))
        {
            return false;
        }

        var rootBounds = GetTargetBounds(target);
        var stopBounds = stopButton.Current.BoundingRectangle;
        var existingSteerRuntimeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in FindNamedSteerButtons(target.Root))
        {
            if (button.Current.ProcessId != target.ProcessId)
            {
                continue;
            }

            if (!TryGetRuntimeId(button, out var runtimeId))
            {
                return false;
            }

            existingSteerRuntimeIds.Add(runtimeId);
        }

        if (!IsComposerReadyForInput(target, composer)
            || !IsExpectedStopElement(target, stopButton, expectedStopRuntimeId))
        {
            return false;
        }

        baseline = new SteerBaseline(
            rootBounds,
            stopBounds,
            existingSteerRuntimeIds);
        return true;
    }

    private static bool IsExpectedStopElement(
        CodexTarget target,
        AutomationElement stopButton,
        string expectedRuntimeId)
    {
        var name = stopButton.Current.Name?.Trim();
        var bounds = stopButton.Current.BoundingRectangle;
        return stopButton.Current.ProcessId == target.ProcessId
            && StopButtonLabels.Any(label =>
                string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
            && stopButton.Current.IsEnabled
            && !stopButton.Current.IsOffscreen
            && stopButton.TryGetCurrentPattern(InvokePattern.Pattern, out _)
            && IsStopButtonGeometry(GetTargetBounds(target), bounds, GetDpiScale(target.Handle))
            && TryGetRuntimeId(stopButton, out var runtimeId)
            && runtimeId == expectedRuntimeId;
    }

    private static bool TryFindNewSteerButton(
        CodexTarget target,
        SteerBaseline baseline,
        string message,
        out AutomationElement button)
    {
        var messageBounds = FindExactVisibleMessageBounds(target, message);
        foreach (var candidate in FindValidSteerButtons(
                     target,
                     baseline.RootBounds,
                     baseline.StopBounds))
        {
            if (!TryGetRuntimeId(candidate, out var candidateRuntimeId)
                || baseline.ExistingSteerRuntimeIds.Contains(candidateRuntimeId)
                || !IsSteerButtonAssociatedWithMessage(candidate, messageBounds))
            {
                continue;
            }

            button = candidate;
            return true;
        }

        button = null!;
        return false;
    }

    private static List<System.Windows.Rect> FindExactVisibleMessageBounds(
        CodexTarget target,
        string message)
    {
        var messageNodes = target.Root.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, message),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new OrCondition(
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Text),
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Document))));
        var result = new List<System.Windows.Rect>(messageNodes.Count);
        for (var index = 0; index < messageNodes.Count; index++)
        {
            var node = messageNodes[index];
            if (node.Current.ProcessId == target.ProcessId
                && !node.Current.BoundingRectangle.IsEmpty)
            {
                result.Add(node.Current.BoundingRectangle);
            }
        }

        return result;
    }

    private static bool IsSteerButtonAssociatedWithMessage(
        AutomationElement button,
        IReadOnlyCollection<System.Windows.Rect> messageBounds)
    {
        var buttonBounds = button.Current.BoundingRectangle;
        foreach (var bounds in messageBounds)
        {
            if (IsMessageNearSteerButton(buttonBounds, bounds))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMessageNearSteerButton(
        System.Windows.Rect buttonBounds,
        System.Windows.Rect messageBounds)
    {
        if (buttonBounds.IsEmpty
            || messageBounds.IsEmpty
            || messageBounds.Left >= buttonBounds.Left)
        {
            return false;
        }

        return messageBounds.Top < buttonBounds.Bottom
            && messageBounds.Bottom > buttonBounds.Top;
    }

    private static string NormalizeAutomationText(string? text) =>
        Regex.Replace(text ?? string.Empty, "\\s+", " ").Trim();

    private static bool TryGetRuntimeId(AutomationElement element, out string runtimeId)
    {
        var values = element.GetRuntimeId();
        runtimeId = values is { Length: > 0 }
            ? string.Join(".", values)
            : string.Empty;
        return runtimeId.Length > 0;
    }

    private static bool IsSteerButtonGeometry(
        System.Windows.Rect rootBounds,
        System.Windows.Rect stopBounds,
        System.Windows.Rect bounds,
        double scale = 1.0) =>
        !bounds.IsEmpty
        && bounds.Width >= 32 * scale
        && bounds.Width <= 140 * scale
        && bounds.Height >= 18 * scale
        && bounds.Height <= 60 * scale
        && bounds.Left >= rootBounds.Left + (rootBounds.Width * 0.5)
        && bounds.Bottom <= stopBounds.Top
        && stopBounds.Top - bounds.Bottom <= 220 * scale
        && bounds.Right >= stopBounds.Left - (200 * scale);

    private static bool TryInvokeSteerAction(
        CodexTarget target,
        SteerBaseline baseline,
        string message,
        Action? afterSteerInvoked)
    {
        Thread.Sleep(40);
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < SteerDiscoveryTimeoutMilliseconds)
        {
            try
            {
                if (TryFindNewSteerButton(
                        target,
                        baseline,
                        message,
                        out var steerButton)
                    && steerButton.Current.ProcessId == target.ProcessId
                    && steerButton.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    && pattern is InvokePattern invokePattern)
                {
                    invokePattern.Invoke();
                    afterSteerInvoked?.Invoke();
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                // Codex can briefly rebuild the queued-message row.
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    // Finds composers through the cheap server-side Edit filter first; the Group
    // scan is the fallback for Codex builds that expose ProseMirror as a group.
    // The first call after Codex starts waits for Electron to expose its tree.
    private List<AutomationElement> FindComposerCandidates(CodexTarget target)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var found = FindSupportedComposers(target.Root);
            if (found.Count > 0)
            {
                _accessibilityReadyProcessId = target.ProcessId;
                return found;
            }

            if (_accessibilityReadyProcessId == target.ProcessId
                || timeout.ElapsedMilliseconds >= AccessibilityWarmupMilliseconds)
            {
                return found;
            }

            Thread.Sleep(100);
        }
    }

    private static List<AutomationElement> FindSupportedComposers(AutomationElement root)
    {
        foreach (var controlType in new[] { ControlType.Edit, ControlType.Group })
        {
            var elements = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
            var result = new List<AutomationElement>();
            for (var index = 0; index < elements.Count; index++)
            {
                try
                {
                    if (IsSupportedComposer(elements[index]))
                    {
                        result.Add(elements[index]);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // The element disappeared while the tree was rebuilt.
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        return [];
    }

    private static bool IsSupportedComposer(AutomationElement candidate) =>
        candidate.Current.IsKeyboardFocusable
        && HasCssClass(candidate.Current.ClassName, "ProseMirror")
        && candidate.TryGetCurrentPattern(TextPattern.Pattern, out _);

    private static bool HasCssClass(string? classNames, string expectedClass) =>
        (classNames ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(expectedClass, StringComparer.Ordinal);

    private static bool TryIsComposerEmpty(AutomationElement composer, out bool isEmpty)
    {
        isEmpty = false;
        if (!composer.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
            || pattern is not TextPattern textPattern)
        {
            return false;
        }

        var text = textPattern.DocumentRange.GetText(2048);
        isEmpty = IsEmptyComposerText(text, HasEmptyDocumentMarker(composer));
        return true;
    }

    private static bool IsEmptyComposerText(string? text, bool hasEmptyDocumentMarker)
    {
        if (text is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = Regex.Replace(text, "\\s+", " ").Trim();
        var matchesPlaceholder = EmptyComposerLabels.Any(
            label => normalized.Equals(label, StringComparison.OrdinalIgnoreCase));
        return matchesPlaceholder && hasEmptyDocumentMarker;
    }

    private static bool HasEmptyDocumentMarker(AutomationElement composer) =>
        composer.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "ProseMirror-trailingBreak")) is not null;

    private static bool IsComposerReadyForInput(CodexTarget target, AutomationElement composer)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            return GetForegroundWindow() == target.Handle
                && focused is not null
                && focused.Current.ProcessId == target.ProcessId
                && IsDescendantOrSelf(focused, composer)
                && TryIsComposerEmpty(composer, out var isEmpty)
                && isEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool RunSelfTest(out string message)
    {
        var rootBounds = new System.Windows.Rect(188, 392, 1280, 743);
        var stopBounds = new System.Windows.Rect(1297, 1083, 29, 28);
        var steerBounds = new System.Windows.Rect(1186, 990, 66, 24);
        var success = !IsEmptyComposerText(null, false)
            && IsEmptyComposerText("   ", false)
            && IsEmptyComposerText("Do anything", true)
            && !IsEmptyComposerText("Do anything", false)
            && !IsEmptyComposerText("Real draft", true)
            && HasCssClass("ProseMirror ProseMirror-focused", "ProseMirror")
            && !HasCssClass("NotProseMirror", "ProseMirror")
            && NormalizeAutomationText("  Go   faster.\r\n") == "Go faster."
            && IsStopButtonGeometry(rootBounds, stopBounds)
            && !IsStopButtonGeometry(
                rootBounds,
                new System.Windows.Rect(1297, 1140, 29, 28))
            && IsSteerButtonGeometry(rootBounds, stopBounds, steerBounds)
            && !IsSteerButtonGeometry(
                rootBounds,
                stopBounds,
                new System.Windows.Rect(400, 990, 66, 24))
            && !IsSteerButtonGeometry(
                rootBounds,
                stopBounds,
                new System.Windows.Rect(1100, 900, 250, 48))
            && IsSteerButtonGeometry(
                rootBounds,
                stopBounds,
                new System.Windows.Rect(1100, 900, 250, 48),
                scale: 2.0)
            && IsComposerGeometry(
                new System.Windows.Rect(537, 827, 713, 44),
                new System.Windows.Rect(1225, 875, 29, 28))
            && !IsComposerGeometry(
                new System.Windows.Rect(537, 827, 100, 44),
                new System.Windows.Rect(1225, 875, 29, 28))
            && IsComposerGeometry(
                new System.Windows.Rect(950, 827, 300, 88),
                new System.Windows.Rect(1225, 875, 29, 28))
            && !IsComposerGeometry(
                new System.Windows.Rect(950, 827, 300, 88),
                new System.Windows.Rect(1225, 875, 29, 28),
                scale: 2.0);
        message = success
            ? "PASS: Desktop composer state and queued Steer action guards are consistent."
            : "FAIL: Desktop composer or queued Steer action guards are inconsistent.";
        return success;
    }

    private static bool IsExpectedAutomationException(Exception exception) =>
        exception is ElementNotAvailableException
            or InvalidOperationException
            or COMException;

    private static bool IsDescendantOrSelf(AutomationElement element, AutomationElement ancestor)
    {
        for (AutomationElement? current = element; current is not null; current = TreeWalker.ControlViewWalker.GetParent(current))
        {
            if (Automation.Compare(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ForegroundWindowBelongsTo(int processId)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        if (GetWindowThreadProcessId(foreground, out var foregroundProcessId) == 0)
        {
            return false;
        }

        return foregroundProcessId == processId;
    }

    private static void RestoreForegroundIfStillOwned(
        nint previousForeground,
        int previousProcessId,
        nint activatedCodexWindow)
    {
        if (previousForeground == nint.Zero
            || activatedCodexWindow == nint.Zero
            || previousForeground == activatedCodexWindow
            || GetForegroundWindow() != activatedCodexWindow
            || !IsWindow(previousForeground)
            || GetWindowThreadProcessId(previousForeground, out var currentPreviousProcessId) == 0
            || currentPreviousProcessId != previousProcessId)
        {
            return;
        }

        if (SetForegroundWindow(previousForeground)
            && GetForegroundWindow() == previousForeground)
        {
            return;
        }

        var foregroundAfterRestore = GetForegroundWindow();
        for (var attempt = 0;
             attempt < 5 && foregroundAfterRestore == nint.Zero;
             attempt++)
        {
            Thread.Sleep(15);
            foregroundAfterRestore = GetForegroundWindow();
        }

        if (foregroundAfterRestore == previousForeground)
        {
            return;
        }

        if (foregroundAfterRestore != activatedCodexWindow)
        {
            return;
        }

        var callerThreadId = GetCurrentThreadId();
        var foregroundThreadId = GetWindowThreadProcessId(activatedCodexWindow, out _);
        if (foregroundThreadId == 0)
        {
            return;
        }

        var attached = callerThreadId == foregroundThreadId
            || AttachThreadInput(callerThreadId, foregroundThreadId, attach: true);
        if (!attached)
        {
            return;
        }

        try
        {
            if (GetForegroundWindow() != activatedCodexWindow
                || !IsWindow(previousForeground)
                || GetWindowThreadProcessId(previousForeground, out var revalidatedPreviousProcessId) == 0
                || revalidatedPreviousProcessId != previousProcessId)
            {
                return;
            }

            _ = BringWindowToTop(previousForeground);
            _ = SetForegroundWindow(previousForeground);
        }
        finally
        {
            if (callerThreadId != foregroundThreadId)
            {
                _ = AttachThreadInput(callerThreadId, foregroundThreadId, attach: false);
            }
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var currentForeground = GetForegroundWindow();
            if (currentForeground == previousForeground)
            {
                return;
            }

            if (currentForeground != nint.Zero
                && currentForeground != activatedCodexWindow)
            {
                return;
            }

            _ = SetForegroundWindow(previousForeground);
            Thread.Sleep(15);
        }
    }

    private static bool AreInputModifiersPressed() =>
        IsKeyPressed(VirtualKeyShift)
        || IsKeyPressed(VirtualKeyControl)
        || IsKeyPressed(VirtualKeyMenu)
        || IsKeyPressed(VirtualKeyLeftWindows)
        || IsKeyPressed(VirtualKeyRightWindows);

    private static bool IsKeyPressed(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // A user still holding Shift/Ctrl/Alt/Win after the gesture would turn the
    // typed text into shortcuts, so wait briefly for release before giving up.
    private static bool WaitForModifiersReleased()
    {
        var timeout = Stopwatch.StartNew();
        while (AreInputModifiersPressed())
        {
            if (timeout.ElapsedMilliseconds >= ModifierReleaseTimeoutMilliseconds)
            {
                return false;
            }

            Thread.Sleep(20);
        }

        return true;
    }

    private static SteerResult ModifierHeldResult(nint windowHandle) =>
        new(
            false,
            "MODIFIER_HELD",
            "A Shift, Ctrl, Alt, or Windows key was held, so no text was sent.",
            windowHandle);

    private static bool TryActivateWindow(nint handle)
    {
        if (GetForegroundWindow() == handle)
        {
            return true;
        }

        // SW_RESTORE would also un-maximize a maximized window.
        if (IsIconic(handle))
        {
            ShowWindow(handle, ShowWindowRestore);
        }

        BringWindowToTop(handle);
        SetForegroundWindow(handle);
        if (WaitForForeground(handle, 150))
        {
            return true;
        }

        // Windows can refuse focus to a background process. Sharing input with the
        // current foreground thread is the usual workaround.
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, attach: true))
        {
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                _ = AttachThreadInput(currentThread, foregroundThread, attach: false);
            }
        }

        return WaitForForeground(handle, 300);
    }

    private static bool WaitForForeground(nint handle, int timeoutMilliseconds)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            if (GetForegroundWindow() == handle)
            {
                return true;
            }

            Thread.Sleep(15);
        }
        while (timeout.ElapsedMilliseconds < timeoutMilliseconds);

        return GetForegroundWindow() == handle;
    }

    // One-line view of the composer area for the steering log. UI text other than
    // known button labels is reduced to its length so titles never reach the log.
    public string DescribeSnapshot()
    {
        try
        {
            if (!TryFindCodexWindow(out var target))
            {
                return "window=none";
            }

            var composers = FindSupportedComposers(target.Root);
            var builder = new System.Text.StringBuilder(
                $"root={FormatRect(GetTargetBounds(target))} dpi={GetDpiScale(target.Handle):0.00} "
                + $"composers={composers.Count} steerButtons={FindNamedSteerButtons(target.Root).Count}");
            if (composers.Count > 0)
            {
                var composer = composers[0];
                builder.Append($" composer={FormatRect(composer.Current.BoundingRectangle)}");
                builder.Append(TryIsComposerEmpty(composer, out var isEmpty)
                    ? $" empty={isEmpty}"
                    : " empty=unreadable");
                var scope = TreeWalker.ControlViewWalker.GetParent(composer);
                if (scope is not null)
                {
                    var buttons = scope.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    builder.Append(" buttons=[");
                    for (var index = 0; index < Math.Min(buttons.Count, 20); index++)
                    {
                        var button = buttons[index];
                        builder.Append(index > 0 ? ", " : string.Empty);
                        builder.Append(SteerLog.Redact(button.Current.Name, KnownButtonLabels));
                        builder.Append(FormatRect(button.Current.BoundingRectangle));
                    }

                    builder.Append(']');
                }
            }

            return builder.ToString();
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return "snapshot=unavailable";
        }
    }

    private static string FormatRect(System.Windows.Rect bounds) =>
        bounds.IsEmpty
            ? "(empty)"
            : $"({bounds.X:0},{bounds.Y:0},{bounds.Width:0}x{bounds.Height:0})";

    private static bool SendUnicodeTextAndEnter(string text)
    {
        var inputs = new List<Input>((text.Length * 2) + 2);
        foreach (var character in text)
        {
            inputs.Add(Input.ForUnicode(character, keyUp: false));
            inputs.Add(Input.ForUnicode(character, keyUp: true));
        }

        inputs.Add(Input.ForVirtualKey(VirtualKeyReturn, keyUp: false));
        inputs.Add(Input.ForVirtualKey(VirtualKeyReturn, keyUp: true));
        var buffer = inputs.ToArray();
        return SendInput((uint)buffer.Length, buffer, Marshal.SizeOf<Input>()) == buffer.Length;
    }

    private readonly record struct CodexTarget(int ProcessId, nint Handle, AutomationElement Root);
    private readonly record struct SteerBaseline(
        System.Windows.Rect RootBounds,
        System.Windows.Rect StopBounds,
        HashSet<string> ExistingSteerRuntimeIds);

    private const int ShowWindowRestore = 9;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const ushort VirtualKeyReturn = 0x0D;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;

        public static Input ForUnicode(char character, bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        Scan = character,
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
                    }
                }
            };
        }

        public static Input ForVirtualKey(ushort virtualKey, bool keyUp)
        {
            return new Input
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
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(
        nint windowHandle,
        ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out int processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}

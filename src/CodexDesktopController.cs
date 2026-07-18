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
        return TrySteer(message, activateCodex: true, null);
    }

    public SteerResult TrySteerForegroundOnly(string message, CancellationTokenSource cancellation)
    {
        return TrySteer(message, activateCodex: false, cancellation);
    }

    private SteerResult TrySteer(string message, bool activateCodex, CancellationTokenSource? cancellation)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new SteerResult(false, "EMPTY_MESSAGE", "The steering message is empty.");
        }

        if (!TryLocateActiveComposer(out var target, out var composer, out _))
        {
            return _lastLocateFailure!;
        }

        if (!activateCodex
            && (cancellation!.IsCancellationRequested || !ForegroundWindowBelongsTo(target.ProcessId)))
        {
            return new SteerResult(false, "FOCUS_GUARD", "Codex is no longer in the foreground.", target.Handle);
        }

        if (activateCodex)
        {
            ShowWindow(target.Handle, ShowWindowRestore);
            BringWindowToTop(target.Handle);
            SetForegroundWindow(target.Handle);
            Thread.Sleep(80);

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

            Thread.Sleep(60);
        }

        if (!ForegroundWindowBelongsTo(target.ProcessId))
        {
            return new SteerResult(
                false,
                "FOCUS_GUARD",
                "Codex did not receive focus, so no text was sent.",
                target.Handle);
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null
                || focused.Current.ProcessId != target.ProcessId
                || !IsDescendantOrSelf(focused, composer))
            {
                return new SteerResult(
                    false,
                    "FOCUS_GUARD",
                    "The cursor is not in Codex, so no text was sent.",
                    target.Handle);
            }

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
        }
        catch (ElementNotAvailableException)
        {
            return new SteerResult(false, "COMPOSER_STALE", "The Codex composer changed. Try again.", target.Handle);
        }

        bool inputSent;
        if (activateCodex)
        {
            if (!IsComposerReadyForInput(target, composer))
            {
                return new SteerResult(false, "FOCUS_GUARD", "The Codex composer changed before sending.", target.Handle);
            }

            inputSent = SendUnicodeTextAndEnter(message);
        }
        else
        {
            lock (cancellation!)
            {
                if (cancellation.IsCancellationRequested || !IsComposerReadyForInput(target, composer))
                {
                    return new SteerResult(false, "FOCUS_GUARD", "Automatic steering was cancelled before sending.", target.Handle);
                }

                inputSent = SendUnicodeTextAndEnter(message);
            }
        }

        if (!inputSent)
        {
            return new SteerResult(
                false,
                "SEND_INPUT_FAILED",
                "Windows did not accept the complete keyboard sequence.",
                target.Handle);
        }

        return new SteerResult(
            true,
            "STEERED",
            "Message sent to the active Codex task.",
            target.Handle);
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
            stopButton = FindStopButton(target.Root)!;
            if (stopButton is null)
            {
                _lastLocateFailure = new SteerResult(
                    false,
                    "NO_ACTIVE_TURN",
                    "Codex is open, but no active task is showing the Stop button.",
                    target.Handle);
                return false;
            }

            composer = FindComposer(target.Root, stopButton)!;
            if (composer is null)
            {
                _lastLocateFailure = new SteerResult(
                    false,
                    "COMPOSER_NOT_FOUND",
                    "The Codex composer was not recognized, so no text was sent.",
                    target.Handle);
                return false;
            }

            if (!TryIsComposerEmpty(composer, out var isComposerEmpty))
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

            _lastLocateFailure = null;
            return true;
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

    private static AutomationElement? FindStopButton(AutomationElement root)
    {
        var rootBounds = root.Current.BoundingRectangle;
        var buttons = root.FindAll(
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
            if (bounds.IsEmpty
                || bounds.Left < rootBounds.Left + (rootBounds.Width * 0.5)
                || bounds.Top < rootBounds.Top + (rootBounds.Height * 0.5)
                || bounds.Width > 120
                || bounds.Height > 80)
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

    private static AutomationElement? FindComposer(AutomationElement root, AutomationElement stopButton)
    {
        var rootBounds = root.Current.BoundingRectangle;
        var stopBounds = stopButton.Current.BoundingRectangle;
        var groups = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));

        AutomationElement? bestMatch = null;
        var bestScore = double.MaxValue;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            if (!group.Current.IsKeyboardFocusable)
            {
                continue;
            }

            var bounds = group.Current.BoundingRectangle;
            if (bounds.IsEmpty
                || bounds.Width < Math.Max(320, rootBounds.Width * 0.35)
                || bounds.Height is < 24 or > 140
                || bounds.Left >= stopBounds.Left
                || bounds.Right < stopBounds.Right - 24)
            {
                continue;
            }

            var verticalGap = Math.Abs(stopBounds.Top - bounds.Bottom);
            if (verticalGap > 140)
            {
                continue;
            }

            var score = verticalGap + (Math.Abs(bounds.Right - stopBounds.Right) * 0.15);
            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = group;
            }
        }

        return bestMatch;
    }

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
        var success = !IsEmptyComposerText(null, false)
            && IsEmptyComposerText("   ", false)
            && IsEmptyComposerText("Do anything", true)
            && !IsEmptyComposerText("Do anything", false)
            && !IsEmptyComposerText("Real draft", true);
        message = success
            ? "PASS: Desktop composer state fails closed on placeholder collisions."
            : "FAIL: Desktop composer state guards are inconsistent.";
        return success;
    }

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

    private const int ShowWindowRestore = 9;
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out int processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}

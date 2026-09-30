using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace CodexWhip;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private const int HotkeyIdPrimary = 0x5748;
    private const int HotkeyIdSecondary = 0x5749;
    private const int WindowMessageHotkey = 0x0312;
    private const uint HotkeyNoRepeat = 0x4000;
    private const int ManualQueueCapacity = 1;
    private const int MaxAutoRetries = 3;
    private const int MaxAutoPostInputFailures = 3;
    private static readonly TimeSpan AutoInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoSteeringInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AutoPauseInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ManualRequestLifetime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan[] AutoRetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];
    private static readonly HotkeyChoice[] HotkeyChoices =
    [
        new("F8", 0, 0x77),
        new("F9", 0, 0x78),
        new("F10", 0, 0x79),
        new("Ctrl+Alt+W", 0x0001 | 0x0002, 0x57)
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        EnsureWpfEnvironment();

        if (args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunGestureSelfTest();
        }

        if (args.Any(arg => arg.Equals("--diagnose-cli", StringComparison.OrdinalIgnoreCase)))
        {
            return RunCliDiagnostics();
        }

        if (args.Any(arg => arg.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDiagnostics(args.Any(arg => arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase)));
        }

        var showWhipOnStart = args.Any(arg => arg.Equals("--show", StringComparison.OrdinalIgnoreCase));

        using var singleInstance = new Mutex(true, "Local\\CodexWhip.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        var application = new Wpf.Application
        {
            ShutdownMode = Wpf.ShutdownMode.OnExplicitShutdown
        };
        var controller = new CodexDesktopController();
        var cliController = new CodexCliController();
        cliController.PrewarmInstalledCli();
        controller.PrewarmAccessibility();
        var overlay = new WhipOverlayWindow();
        using var trayIcon = CreateTrayIcon();
        var autoSteeringItem = (Forms.ToolStripMenuItem)trayIcon.ContextMenuStrip!.Items[1];
        var shortcutMenu = (Forms.ToolStripMenuItem)trayIcon.ContextMenuStrip!.Items[2];
        var selectedHotkey = HotkeyChoices[0];
        var hotkeyHandle = new WindowInteropHelper(overlay).EnsureHandle();
        var hotkeySource = HwndSource.FromHwnd(hotkeyHandle)
            ?? throw new InvalidOperationException("The Codex Whip message window is unavailable.");
        int? activeHotkeyId = null;
        overlay.SetDismissShortcut(selectedHotkey.Label);
        var autoSteerTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = AutoSteeringInterval
        };
        var pendingSteers = new PendingSteerState(ManualQueueCapacity, ManualRequestLifetime);
        var pumpTask = Task.CompletedTask;
        var pumpRunning = false;
        var shuttingDown = false;
        PendingSteer? activeRequest = null;
        SteerAttempt? activeAttempt = null;
        var autoGeneration = 0;
        var autoRetryCount = 0;
        var autoPostInputFailures = 0;
        int? preemptedAutoGeneration = null;
        CodexCliCapture? pendingCliCapture = null;

        void UpdateHotkeyChecks()
        {
            foreach (var item in shortcutMenu.DropDownItems.OfType<Forms.ToolStripMenuItem>())
            {
                item.Checked = item.Tag is HotkeyChoice choice && choice == selectedHotkey;
            }
        }

        bool TrySetGlobalHotkey(HotkeyChoice choice)
        {
            if (choice == selectedHotkey && activeHotkeyId is not null)
            {
                return true;
            }

            var candidateId = activeHotkeyId == HotkeyIdPrimary ? HotkeyIdSecondary : HotkeyIdPrimary;
            if (!RegisterHotKey(
                hotkeyHandle,
                candidateId,
                choice.Modifiers | HotkeyNoRepeat,
                choice.VirtualKey))
            {
                trayIcon.ShowBalloonTip(
                    4000,
                    "Shortcut unavailable",
                    $"{choice.Label} is already used. Choose another shortcut from the tray menu.",
                    Forms.ToolTipIcon.Warning);
                return false;
            }

            if (activeHotkeyId is { } previousId)
            {
                _ = UnregisterHotKey(hotkeyHandle, previousId);
            }

            activeHotkeyId = candidateId;
            selectedHotkey = choice;
            overlay.SetDismissShortcut(choice.Label);
            UpdateHotkeyChecks();
            return true;
        }

        void ReleaseGlobalHotkey()
        {
            if (activeHotkeyId is { } registeredId)
            {
                _ = UnregisterHotKey(hotkeyHandle, registeredId);
                activeHotkeyId = null;
            }
        }

        nint HotkeyHook(nint _, int message, nint wordParameter, nint __, ref bool handled)
        {
            if (message == WindowMessageHotkey
                && activeHotkeyId == wordParameter.ToInt32())
            {
                handled = true;
                pendingCliCapture = null;
                application.Dispatcher.BeginInvoke(() => ShowWhip(restoreCliForeground: true));
            }

            return nint.Zero;
        }

        void ShowWhip(bool restoreCliForeground = false)
        {
            if (overlay.IsVisible)
            {
                overlay.DropWhip();
                return;
            }

            controller.PrewarmAccessibility();
            var cliCapture = pendingCliCapture ?? cliController.CaptureForeground();
            pendingCliCapture = null;
            if (restoreCliForeground
                && cliCapture.Kind is CliCaptureKind.Ready or CliCaptureKind.Unsupported)
            {
                var restored = SetForegroundWindow(cliCapture.WindowHandle)
                    && GetForegroundWindow() == cliCapture.WindowHandle;
                if (!restored && cliCapture.Kind == CliCaptureKind.Ready)
                {
                    cliCapture = new CodexCliCapture(
                        CliCaptureKind.Unsupported,
                        cliCapture.WindowHandle);
                }
            }

            var cliHandle = cliCapture.Kind is CliCaptureKind.Ready or CliCaptureKind.Unsupported
                ? cliCapture.WindowHandle
                : nint.Zero;
            var screen = cliHandle != nint.Zero
                ? Forms.Screen.FromHandle(cliHandle)
                : controller.TryGetCodexWindowHandle(out var desktopHandle)
                    ? Forms.Screen.FromHandle(desktopHandle)
                    : Forms.Screen.FromPoint(Forms.Cursor.Position);
            overlay.ShowOn(screen);
        }

        void ScheduleAuto(TimeSpan delay)
        {
            autoSteerTimer.Stop();
            if (!shuttingDown && autoSteeringItem.Checked)
            {
                autoSteerTimer.Interval = delay;
                autoSteerTimer.Start();
            }
        }

        void PreemptActiveAuto()
        {
            if (activeRequest is { Kind: PendingSteerKind.Automatic } request
                && activeAttempt?.Cancel() == true)
            {
                preemptedAutoGeneration = request.AutoGeneration;
            }
        }

        async Task<SteerResult> SendRequestAsync(
            PendingSteer request,
            SteerAttempt attempt,
            int tryNumber)
        {
            var capture = request.Kind == PendingSteerKind.Automatic
                ? cliController.CaptureForeground()
                : request.Capture ?? new CodexCliCapture(CliCaptureKind.None);
            var message = AppLocalizer.RandomSteeringMessage();

            SteerResult SendMessage()
            {
                if (capture.Kind == CliCaptureKind.Ready)
                {
                    return capture.Target is { } target
                        ? cliController.TrySteer(target, message, attempt)
                        : new SteerResult(
                            false,
                            "CLI_UNAVAILABLE",
                            "The captured Codex CLI target is no longer available.",
                            capture.WindowHandle);
                }

                if (request.Kind == PendingSteerKind.Automatic)
                {
                    // Automatic steering never takes focus: Codex must already be in front.
                    if (capture.Kind == CliCaptureKind.Unsupported)
                    {
                        return new SteerResult(
                            false,
                            "CLI_UNAVAILABLE",
                            "This terminal could not be verified, so automatic steering skipped it.",
                            capture.WindowHandle);
                    }

                    return controller.IsCodexForeground()
                        ? controller.TrySteerForegroundOnly(message, attempt)
                        : new SteerResult(false, "NOT_FOREGROUND", "Codex is not in the foreground.");
                }

                var desktopResult = controller.TrySteerAndRestoreForeground(message, attempt);
                if (capture.Kind == CliCaptureKind.Unsupported
                    && desktopResult.Code is "CODEX_NOT_FOUND" or "NO_ACTIVE_TURN")
                {
                    return new SteerResult(
                        false,
                        "CLI_UNAVAILABLE",
                        "This terminal could not be verified and no active Codex Desktop task was found.",
                        capture.WindowHandle);
                }

                return desktopResult;
            }

            SteerResult SendAndLog()
            {
                var clock = Stopwatch.StartNew();
                var result = SendMessage();
                if (result.Code != "NOT_FOREGROUND")
                {
                    var viaCli = capture.Kind == CliCaptureKind.Ready;
                    var line = $"{(request.Kind == PendingSteerKind.Automatic ? "auto" : "manual")} "
                        + $"try={tryNumber} target={(viaCli ? "cli" : "desktop")} code={result.Code} "
                        + $"stage={(viaCli ? "cli" : controller.LastStage)} ms={clock.ElapsedMilliseconds}"
                        + (result.Success ? string.Empty : $" msg=\"{result.Message}\"");
                    SteerLog.Write(result.Success || viaCli
                        ? line
                        : $"{line} snapshot: {controller.DescribeSnapshot()}");
                }

                return result;
            }

            return await Task.Run(SendAndLog);
        }

        void ReportManualResult(SteerResult result)
        {
            overlay.ShowFeedback(
                result.Success
                    ? AppLocalizer.Text(TextKey.StrikeReceived)
                    : AppLocalizer.ResultMessage(result),
                result.Success);

            if (!result.Success)
            {
                trayIcon.ShowBalloonTip(
                    4000,
                    AppLocalizer.Text(TextKey.StrikeHeldTitle),
                    $"{AppLocalizer.ResultMessage(result)} ({result.Code})",
                    Forms.ToolTipIcon.Warning);
            }
        }

        void CompleteAutoAttempt(PendingSteer request, SteerResult result)
        {
            var wasPreempted = preemptedAutoGeneration == request.AutoGeneration;
            if (wasPreempted)
            {
                preemptedAutoGeneration = null;
            }

            if (request.AutoGeneration != autoGeneration || !autoSteeringItem.Checked)
            {
                return;
            }

            if (wasPreempted)
            {
                ScheduleAuto(AutoSteeringInterval);
                return;
            }

            if (result.Success)
            {
                autoRetryCount = 0;
                autoPostInputFailures = 0;
                ScheduleAuto(AutoSteeringInterval);
                return;
            }

            // Text was typed but delivery failed: repeating every minute could pile
            // up unsent messages in Codex, so stop and tell the user.
            if (IsPostInputFailure(result.Code)
                && ++autoPostInputFailures >= MaxAutoPostInputFailures)
            {
                autoPostInputFailures = 0;
                autoRetryCount = 0;
                SteerLog.Write($"auto paused for {AutoPauseInterval.TotalMinutes:0} min after repeated {result.Code}");
                trayIcon.ShowBalloonTip(
                    4000,
                    AppLocalizer.Text(TextKey.StrikeHeldTitle),
                    $"{AppLocalizer.ResultMessage(result)} ({result.Code})",
                    Forms.ToolTipIcon.Warning);
                ScheduleAuto(AutoPauseInterval);
                return;
            }

            if (IsTransientFailure(result.Code) && autoRetryCount < MaxAutoRetries)
            {
                ScheduleAuto(AutoRetryDelays[autoRetryCount]);
                autoRetryCount++;
                return;
            }

            autoRetryCount = 0;
            ScheduleAuto(AutoSteeringInterval);
        }

        void EnsurePump()
        {
            if (shuttingDown
                || pumpRunning
                || !pendingSteers.HasWork(autoSteeringItem.Checked))
            {
                return;
            }

            pumpRunning = true;
            pumpTask = PumpAsync();
        }

        async Task PumpAsync()
        {
            try
            {
                while (!shuttingDown)
                {
                    var hasRequest = pendingSteers.TryTake(
                        autoSteeringItem.Checked,
                        Stopwatch.GetTimestamp(),
                        out var request,
                        out var expiredRequests);
                    if (expiredRequests > 0)
                    {
                        overlay.ShowFeedback(AppLocalizer.Text(TextKey.Cooldown), false);
                    }

                    if (!hasRequest)
                    {
                        break;
                    }

                    activeRequest = request;

                    SteerResult result;
                    try
                    {
                        if (request.Kind == PendingSteerKind.Manual)
                        {
                            overlay.ShowFeedback(AppLocalizer.Text(TextKey.StrikeSending), true);
                        }

                        // Only failures that happen before any text is typed are retried,
                        // so a retry can never send the message twice. Each try needs its
                        // own attempt because a committed attempt cannot commit again.
                        var tryNumber = 0;
                        while (true)
                        {
                            using var attempt = new SteerAttempt();
                            activeAttempt = attempt;
                            result = await SendRequestAsync(request, attempt, ++tryNumber);
                            activeAttempt = null;
                            if (request.Kind != PendingSteerKind.Manual
                                || result.Success
                                || !IsTransientFailure(result.Code)
                                || result.Code == "NO_ACTIVE_TURN"
                                || tryNumber > MaxAutoRetries
                                || shuttingDown)
                            {
                                break;
                            }

                            await Task.Delay(AutoRetryDelays[tryNumber - 1]);
                        }
                    }
                    catch (Exception exception)
                    {
                        SteerLog.Write($"error INTERNAL_ERROR {exception.GetType().Name}: {exception.Message}");
                        result = new SteerResult(
                            false,
                            "INTERNAL_ERROR",
                            "Codex Whip could not complete the steering request.");
                    }
                    finally
                    {
                        activeAttempt = null;
                        activeRequest = null;
                    }

                    if (shuttingDown)
                    {
                        break;
                    }

                    if (request.Kind == PendingSteerKind.Manual)
                    {
                        ReportManualResult(result);
                    }
                    else
                    {
                        CompleteAutoAttempt(request, result);
                    }
                }
            }
            catch (Exception exception)
            {
                SteerLog.Write($"error PUMP {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                pumpRunning = false;
                if (!shuttingDown && pendingSteers.HasWork(autoSteeringItem.Checked))
                {
                    EnsurePump();
                }
            }
        }

        async Task QuitAsync()
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            autoSteerTimer.Stop();
            pendingSteers.Stop();
            _ = activeAttempt?.Cancel();

            try
            {
                await pumpTask;
            }
            catch (Exception exception)
            {
                SteerLog.Write($"error SHUTDOWN {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                ReleaseGlobalHotkey();
                hotkeySource.RemoveHook(HotkeyHook);
                trayIcon.Visible = false;
                overlay.Close();
                application.Shutdown();
            }
        }

        void RequestQuit() =>
            application.Dispatcher.BeginInvoke(new Action(() => _ = QuitAsync()));

        hotkeySource.AddHook(HotkeyHook);
        UpdateHotkeyChecks();
        foreach (var item in shortcutMenu.DropDownItems.OfType<Forms.ToolStripMenuItem>())
        {
            item.Click += (_, _) => application.Dispatcher.Invoke(() =>
            {
                if (item.Tag is HotkeyChoice choice)
                {
                    _ = TrySetGlobalHotkey(choice);
                }
            });
        }

        trayIcon.MouseDown += (_, _) => pendingCliCapture = cliController.CaptureForeground();
        trayIcon.ContextMenuStrip.Closed += (_, _) => pendingCliCapture = null;
        trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                application.Dispatcher.Invoke(() => ShowWhip(restoreCliForeground: true));
            }
        };
        trayIcon.ContextMenuStrip!.Items[0].Click += (_, _) =>
            application.Dispatcher.Invoke(() => ShowWhip(restoreCliForeground: true));
        autoSteeringItem.CheckedChanged += (_, _) => application.Dispatcher.Invoke(() =>
        {
            autoGeneration++;
            autoRetryCount = 0;
            autoPostInputFailures = 0;
            autoSteerTimer.Stop();
            pendingSteers.ClearAutoDue();

            if (autoSteeringItem.Checked)
            {
                ScheduleAuto(AutoInitialDelay);
            }
            else
            {
                PreemptActiveAuto();
            }
        });
        trayIcon.ContextMenuStrip.Items[4].Click += (_, _) => RequestQuit();

        autoSteerTimer.Tick += (_, _) =>
        {
            autoSteerTimer.Stop();
            if (shuttingDown || !autoSteeringItem.Checked)
            {
                return;
            }

            pendingSteers.MarkAutoDue(autoGeneration);
            EnsurePump();
        };

        overlay.CrackRequested += (_, _) =>
        {
            if (shuttingDown)
            {
                return;
            }

            // Strikes during a send are dropped instead of queued: a backlog made
            // later strikes arrive seconds after the gesture.
            if (activeRequest is { Kind: PendingSteerKind.Manual })
            {
                overlay.ShowFeedback(AppLocalizer.Text(TextKey.Cooldown), false);
                return;
            }

            var cliCapture = cliController.CaptureForeground();
            if (!pendingSteers.TryEnqueueManual(
                    cliCapture,
                    Stopwatch.GetTimestamp(),
                    out var expiredRequests))
            {
                overlay.ShowFeedback(AppLocalizer.Text(TextKey.Cooldown), false);
                return;
            }

            if (expiredRequests > 0)
            {
                overlay.ShowFeedback(AppLocalizer.Text(TextKey.Cooldown), false);
            }

            PreemptActiveAuto();
            EnsurePump();
        };

        trayIcon.Visible = true;
        _ = TrySetGlobalHotkey(selectedHotkey);
        trayIcon.ShowBalloonTip(
            2500,
            AppLocalizer.Text(TextKey.ReadyTitle),
            AppLocalizer.Text(TextKey.ReadyBody),
            Forms.ToolTipIcon.Info);

        if (showWhipOnStart)
        {
            application.Dispatcher.BeginInvoke(() => ShowWhip());
        }

        var exitCode = application.Run();
        trayIcon.Visible = false;
        return exitCode;
    }

    // Failures after text reached the composer: never retried, and repeated ones
    // pause automatic steering.
    private static bool IsPostInputFailure(string code) =>
        code is "SEND_INPUT_FAILED"
            or "STEER_ACTION_FAILED"
            or "SEND_NOT_ACCEPTED"
            or "UNCONFIRMED";

    // Failures that happen before any text is typed and may clear on their own.
    private static bool IsTransientFailure(string code) =>
        code is "COMPOSER_NOT_FOUND"
            or "COMPOSER_STALE"
            or "NO_ACTIVE_TURN"
            or "FOCUS_GUARD"
            or "FOCUS_FAILED"
            or "FOCUS_NOT_GRANTED"
            or "COMPOSER_NOT_FOCUSED"
            or "MODIFIER_HELD"
            or "TURN_CHANGED"
            or "UI_CHANGED"
            or "CLI_CHANGED";

    private static Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(AppLocalizer.Text(TextKey.MenuWhip));
        menu.Items.Add(new Forms.ToolStripMenuItem(AppLocalizer.AutoSteeringLabel)
        {
            CheckOnClick = true
        });
        var shortcutMenu = new Forms.ToolStripMenuItem("Whip shortcut");
        foreach (var choice in HotkeyChoices)
        {
            shortcutMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(choice.Label) { Tag = choice });
        }

        menu.Items.Add(shortcutMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(AppLocalizer.Text(TextKey.MenuQuit));

        return new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = LoadApplicationIcon(),
            Text = "Codex Whip",
            Visible = false
        };
    }

    private static Icon LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "codex-whip.ico");
        return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
    }

    private static int RunCliDiagnostics()
    {
        AttachConsole(AttachParentProcess);
        Console.WriteLine("Focus the terminal running Codex CLI within 12 seconds...");
        Console.Out.Flush();
        var cli = new CodexCliController();
        var description = cli.DescribeForeground();
        for (var attempt = 0; attempt < 24 && description == "capture=None"; attempt++)
        {
            Thread.Sleep(500);
            description = cli.DescribeForeground();
        }

        Console.WriteLine(description);
        Console.Out.Flush();
        return description.StartsWith("capture=Ready screen=Ready", StringComparison.Ordinal) ? 0 : 2;
    }

    private static int RunDiagnostics(bool verbose)
    {
        AttachConsole(AttachParentProcess);
        var desktop = new CodexDesktopController();
        var result = desktop.Diagnose();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Success,
            result.Code,
            Message = AppLocalizer.ResultMessage(result),
            WindowHandle = $"0x{result.WindowHandle.ToInt64():X}",
            Snapshot = verbose ? desktop.DescribeSnapshot() : null,
            LogFile = verbose ? SteerLog.FilePath : null
        }));
        Console.Out.Flush();
        return result.Success ? 0 : 2;
    }

    private static int RunGestureSelfTest()
    {
        AttachConsole(AttachParentProcess);
        var gestureSuccess = WhipGestureDetector.RunSelfTest(out var gestureMessage);
        var soundSuccess = CrackSound.RunSelfTest(out var soundMessage);
        var localizationSuccess = AppLocalizer.RunSelfTest(out var localizationMessage);
        var cliSuccess = CodexCliController.RunSelfTest(out var cliMessage);
        var desktopSuccess = CodexDesktopController.RunSelfTest(out var desktopMessage);
        var attemptSuccess = SteerAttempt.RunSelfTest(out var attemptMessage);
        var schedulerSuccess = RunSteeringSchedulerSelfTest(out var schedulerMessage);
        var hotkeySuccess = HotkeyChoices.Select(choice => (choice.Modifiers, choice.VirtualKey)).Distinct().Count()
            == HotkeyChoices.Length;
        var hotkeyMessage = hotkeySuccess
            ? "PASS: configurable toggle hotkeys are valid and default to F8."
            : "FAIL: configurable toggle hotkeys are inconsistent.";
        var logSuccess = SteerLog.RunSelfTest(out var logMessage);
        var retrySuccess = IsTransientFailure("FOCUS_GUARD")
            && IsTransientFailure("CLI_CHANGED")
            && IsTransientFailure("FOCUS_NOT_GRANTED")
            && IsTransientFailure("COMPOSER_NOT_FOCUSED")
            && IsTransientFailure("MODIFIER_HELD")
            && IsTransientFailure("TURN_CHANGED")
            && !IsTransientFailure("CANCELLED")
            && !IsTransientFailure("NOT_FOREGROUND")
            && !IsTransientFailure("DRAFT_PRESENT")
            && !IsTransientFailure("COMPOSER_UNREADABLE")
            && !IsTransientFailure("SEND_INPUT_FAILED")
            && !IsTransientFailure("STEER_ACTION_FAILED")
            && IsPostInputFailure("STEER_ACTION_FAILED")
            && IsPostInputFailure("SEND_INPUT_FAILED")
            && !IsPostInputFailure("FOCUS_NOT_GRANTED")
            && !IsPostInputFailure("NO_ACTIVE_TURN");
        var retryMessage = retrySuccess
            ? "PASS: retries are limited to failures that occur before any text is typed."
            : "FAIL: retry guards are inconsistent.";
        Console.WriteLine(
            $"{gestureMessage} {soundMessage} {localizationMessage} {cliMessage} "
            + $"{desktopMessage} {attemptMessage} {schedulerMessage} {hotkeyMessage} "
            + $"{logMessage} {retryMessage}");
        Console.Out.Flush();
        return gestureSuccess
            && soundSuccess
            && localizationSuccess
            && cliSuccess
            && desktopSuccess
            && attemptSuccess
            && schedulerSuccess
            && hotkeySuccess
            && logSuccess
            && retrySuccess
            ? 0
            : 3;
    }

    private static bool RunSteeringSchedulerSelfTest(out string message)
    {
        var now = Stopwatch.Frequency * 10L;
        var first = new CodexCliCapture(CliCaptureKind.None, new nint(1));
        var second = new CodexCliCapture(CliCaptureKind.None, new nint(2));
        var rejected = new CodexCliCapture(CliCaptureKind.None, new nint(3));
        var state = new PendingSteerState(2, ManualRequestLifetime);

        var acceptedInOrder = state.TryEnqueueManual(first, now, out _)
            && state.TryEnqueueManual(second, now, out _)
            && !state.TryEnqueueManual(rejected, now, out _);
        state.MarkAutoDue(7);
        acceptedInOrder = acceptedInOrder
            && state.TryTake(true, now, out var firstRequest, out _)
            && firstRequest.Kind == PendingSteerKind.Manual
            && firstRequest.Capture?.WindowHandle == first.WindowHandle
            && state.TryTake(true, now, out var secondRequest, out _)
            && secondRequest.Kind == PendingSteerKind.Manual
            && secondRequest.Capture?.WindowHandle == second.WindowHandle
            && state.TryTake(true, now, out var autoRequest, out _)
            && autoRequest.Kind == PendingSteerKind.Automatic
            && autoRequest.AutoGeneration == 7
            && !state.TryTake(true, now, out _, out _);

        var expiryState = new PendingSteerState(2, ManualRequestLifetime);
        var expiredTimestamp = now
            - (long)((ManualRequestLifetime + TimeSpan.FromSeconds(1)).TotalSeconds
                * Stopwatch.Frequency);
        var expiryWorks = expiryState.TryEnqueueManual(first, expiredTimestamp, out _);
        expiryState.MarkAutoDue(9);
        expiryWorks = expiryWorks
            && expiryState.TryTake(
                true,
                now,
                out var requestAfterExpiry,
                out var expiredRequests)
            && expiredRequests == 1
            && requestAfterExpiry.Kind == PendingSteerKind.Automatic
            && requestAfterExpiry.AutoGeneration == 9;
        var freshAfterExpiryState = new PendingSteerState(1, ManualRequestLifetime);
        expiryWorks = expiryWorks
            && freshAfterExpiryState.TryEnqueueManual(first, expiredTimestamp, out _)
            && freshAfterExpiryState.TryEnqueueManual(second, now, out var purgedAtEnqueue)
            && purgedAtEnqueue == 1
            && freshAfterExpiryState.TryTake(true, now, out var freshRequest, out _)
            && freshRequest.Capture?.WindowHandle == second.WindowHandle;

        var burstState = new PendingSteerState(3, ManualRequestLifetime);
        var burstDeadline = now
            + (long)(TimeSpan.FromSeconds(30).TotalSeconds * Stopwatch.Frequency);
        var burstWorks = burstState.TryEnqueueManual(first, now, out _)
            && burstState.TryEnqueueManual(second, now, out _)
            && burstState.TryEnqueueManual(rejected, now, out _)
            && burstState.TryTake(false, burstDeadline, out var burstFirst, out var burstExpired)
            && burstExpired == 0
            && burstFirst.Capture?.WindowHandle == first.WindowHandle
            && burstState.TryTake(false, burstDeadline, out var burstSecond, out _)
            && burstSecond.Capture?.WindowHandle == second.WindowHandle
            && burstState.TryTake(false, burstDeadline, out var burstThird, out _)
            && burstThird.Capture?.WindowHandle == rejected.WindowHandle;

        var stoppedState = new PendingSteerState(2, ManualRequestLifetime);
        _ = stoppedState.TryEnqueueManual(first, now, out _);
        stoppedState.MarkAutoDue(1);
        stoppedState.Stop();
        var stopWorks = !stoppedState.HasWork(autoEnabled: true)
            && !stoppedState.TryEnqueueManual(second, now, out _)
            && !stoppedState.TryTake(true, now, out _, out _);

        var success = acceptedInOrder && expiryWorks && burstWorks && stopWorks;
        message = success
            ? "PASS: manual steering is bounded, FIFO, prioritized, burst-safe, expiring, and stoppable."
            : "FAIL: steering scheduler state is inconsistent.";
        return success;
    }

    private static void EnsureWpfEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot) && Path.IsPathFullyQualified(systemRoot))
        {
            Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint windowHandle, int id);

    private enum PendingSteerKind
    {
        Manual,
        Automatic
    }

    private readonly record struct PendingSteer(
        PendingSteerKind Kind,
        CodexCliCapture? Capture,
        int AutoGeneration,
        long EnqueuedTimestamp);

    private sealed class PendingSteerState
    {
        private readonly int _capacity;
        private readonly long _manualRequestLifetimeTicks;
        private readonly Queue<PendingSteer> _manual = new();
        private int? _autoDueGeneration;
        private bool _stopped;

        public PendingSteerState(int capacity, TimeSpan manualRequestLifetime)
        {
            _capacity = capacity;
            _manualRequestLifetimeTicks = Math.Max(
                1,
                (long)(manualRequestLifetime.TotalSeconds * Stopwatch.Frequency));
        }

        public bool HasWork(bool autoEnabled) =>
            !_stopped
            && (_manual.Count > 0 || (autoEnabled && _autoDueGeneration is not null));

        public bool TryEnqueueManual(
            CodexCliCapture capture,
            long timestamp,
            out int expiredRequests)
        {
            expiredRequests = RemoveExpired(timestamp);
            if (_stopped || _manual.Count >= _capacity)
            {
                return false;
            }

            _manual.Enqueue(new PendingSteer(
                PendingSteerKind.Manual,
                capture,
                AutoGeneration: 0,
                timestamp));
            return true;
        }

        public void MarkAutoDue(int generation)
        {
            if (!_stopped)
            {
                _autoDueGeneration = generation;
            }
        }

        public void ClearAutoDue()
        {
            _autoDueGeneration = null;
        }

        public bool TryTake(
            bool autoEnabled,
            long nowTimestamp,
            out PendingSteer request,
            out int expiredRequests)
        {
            expiredRequests = RemoveExpired(nowTimestamp);
            if (!_stopped && _manual.Count > 0)
            {
                request = _manual.Dequeue();
                return true;
            }

            if (!_stopped && autoEnabled && _autoDueGeneration is { } generation)
            {
                _autoDueGeneration = null;
                request = new PendingSteer(
                    PendingSteerKind.Automatic,
                    Capture: null,
                    generation,
                    EnqueuedTimestamp: 0);
                return true;
            }

            request = default;
            return false;
        }

        private int RemoveExpired(long nowTimestamp)
        {
            var removed = 0;
            while (_manual.Count > 0
                && nowTimestamp - _manual.Peek().EnqueuedTimestamp
                    > _manualRequestLifetimeTicks)
            {
                _manual.Dequeue();
                removed++;
            }

            return removed;
        }

        public void Stop()
        {
            _stopped = true;
            _manual.Clear();
            _autoDueGeneration = null;
        }
    }

    private sealed record HotkeyChoice(string Label, uint Modifiers, uint VirtualKey);
}

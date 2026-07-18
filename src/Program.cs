using System.Drawing;
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
    private static readonly TimeSpan AutoSteeringInterval = TimeSpan.FromMinutes(1);
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

        if (args.Any(arg => arg.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            return RunDiagnostics();
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
        var overlay = new WhipOverlayWindow();
        using var trayIcon = CreateTrayIcon();
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
        var steeringGate = new SemaphoreSlim(1, 1);
        var autoAttemptLock = new object();
        CancellationTokenSource? currentAutoAttempt = null;
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

        void CancelCurrentAutoAttempt()
        {
            lock (autoAttemptLock)
            {
                if (currentAutoAttempt is not null)
                {
                    lock (currentAutoAttempt)
                    {
                        currentAutoAttempt.Cancel();
                    }
                }
            }
        }

        async Task SendSteerAsync(
            bool showFeedback,
            CancellationTokenSource? autoAttempt = null,
            CodexCliCapture? cliCapture = null)
        {
            var acquired = await steeringGate.WaitAsync(showFeedback ? Timeout.Infinite : 0);
            if (!acquired)
            {
                return;
            }

            try
            {
                if (showFeedback)
                {
                    overlay.ShowFeedback(AppLocalizer.Text(TextKey.StrikeSending), true);
                }

                var message = AppLocalizer.RandomSteeringMessage();
                var result = await Task.Run(() =>
                {
                    return cliCapture switch
                    {
                        { Kind: CliCaptureKind.Ready, Target: { } target } =>
                            cliController.TrySteer(target, message, autoAttempt?.Token ?? default),
                        { Kind: CliCaptureKind.Ready } invalid =>
                            new SteerResult(
                                false,
                                "CLI_UNAVAILABLE",
                                "The captured Codex CLI target is no longer available.",
                                invalid.WindowHandle),
                        { Kind: CliCaptureKind.Unsupported } unsupported =>
                            new SteerResult(
                                false,
                                "CLI_UNAVAILABLE",
                                "This foreground terminal is not supported by Codex Whip yet.",
                                unsupported.WindowHandle),
                        _ when !showFeedback =>
                            controller.TrySteerForegroundOnly(message, autoAttempt!),
                        _ => controller.TrySteer(message)
                    };
                });
                if (!showFeedback)
                {
                    return;
                }

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
                        AppLocalizer.ResultMessage(result),
                        Forms.ToolTipIcon.Warning);
                }
            }
            finally
            {
                steeringGate.Release();
            }
        }

        void Quit()
        {
            autoSteerTimer.Stop();
            CancelCurrentAutoAttempt();
            ReleaseGlobalHotkey();
            hotkeySource.RemoveHook(HotkeyHook);
            trayIcon.Visible = false;
            overlay.Close();
            application.Shutdown();
        }

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
        var autoSteeringItem = (Forms.ToolStripMenuItem)trayIcon.ContextMenuStrip.Items[1];
        autoSteeringItem.CheckedChanged += (_, _) => application.Dispatcher.Invoke(() =>
        {
            if (autoSteeringItem.Checked)
            {
                autoSteerTimer.Start();
            }
            else
            {
                autoSteerTimer.Stop();
                CancelCurrentAutoAttempt();
            }
        });
        trayIcon.ContextMenuStrip.Items[4].Click += (_, _) => application.Dispatcher.Invoke(Quit);

        autoSteerTimer.Tick += async (_, _) =>
        {
            var autoAttempt = new CancellationTokenSource();
            lock (autoAttemptLock)
            {
                if (!autoSteeringItem.Checked || currentAutoAttempt is not null)
                {
                    autoAttempt.Dispose();
                    return;
                }

                currentAutoAttempt = autoAttempt;
            }

            try
            {
                var cliCapture = cliController.CaptureForeground();
                if (cliCapture.Kind == CliCaptureKind.Ready)
                {
                    await SendSteerAsync(showFeedback: false, autoAttempt, cliCapture);
                }
                else if (cliCapture.Kind == CliCaptureKind.None && controller.IsCodexForeground())
                {
                    await SendSteerAsync(showFeedback: false, autoAttempt);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Automatic steering skipped: {exception}");
            }
            finally
            {
                lock (autoAttemptLock)
                {
                    if (ReferenceEquals(currentAutoAttempt, autoAttempt))
                    {
                        currentAutoAttempt = null;
                    }
                }

                autoAttempt.Dispose();
            }
        };

        overlay.CrackRequested += async (_, _) =>
        {
            var cliCapture = cliController.CaptureForeground();
            CancelCurrentAutoAttempt();
            await SendSteerAsync(showFeedback: true, cliCapture: cliCapture);
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

    private static int RunDiagnostics()
    {
        AttachConsole(AttachParentProcess);
        var result = new CodexDesktopController().Diagnose();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Success,
            result.Code,
            Message = AppLocalizer.ResultMessage(result),
            WindowHandle = $"0x{result.WindowHandle.ToInt64():X}"
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
        var hotkeySuccess = HotkeyChoices.Select(choice => (choice.Modifiers, choice.VirtualKey)).Distinct().Count()
            == HotkeyChoices.Length;
        var hotkeyMessage = hotkeySuccess
            ? "PASS: configurable toggle hotkeys are valid and default to F8."
            : "FAIL: configurable toggle hotkeys are inconsistent.";
        Console.WriteLine($"{gestureMessage} {soundMessage} {localizationMessage} {cliMessage} {desktopMessage} {hotkeyMessage}");
        Console.Out.Flush();
        return gestureSuccess && soundSuccess && localizationSuccess && cliSuccess && desktopSuccess && hotkeySuccess ? 0 : 3;
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

    private sealed record HotkeyChoice(string Label, uint Modifiers, uint VirtualKey);
}

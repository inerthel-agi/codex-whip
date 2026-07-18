using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Forms = System.Windows.Forms;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace CodexWhip;

internal sealed class WhipOverlayWindow : Window
{
    private readonly WhipSurface _surface;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly DispatcherTimer _feedbackTimer;
    private string _dismissShortcut = "F8";

    public event EventHandler? CrackRequested;

    public WhipOverlayWindow()
    {
        Title = "Codex Whip";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _surface = new WhipSurface();
        _surface.Cracked += (_, _) => CrackRequested?.Invoke(this, EventArgs.Empty);
        _surface.Dismissed += (_, _) => Hide();

        _bannerText = new TextBlock
        {
            Text = AppLocalizer.Text(TextKey.Instruction),
            Foreground = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            FlowDirection = AppLocalizer.FlowDirection,
            VerticalAlignment = VerticalAlignment.Center
        };

        var glassOrb = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.28, 0.24),
                Center = new Point(0.38, 0.34),
                RadiusX = 0.78,
                RadiusY = 0.78,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(235, 255, 255, 255), 0),
                    new(Color.FromArgb(75, 255, 255, 255), 0.28),
                    new(Color.FromArgb(150, 4, 4, 5), 1)
                }
            },
            Margin = new Thickness(0, 0, 10, 0)
        };
        var bannerRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            FlowDirection = AppLocalizer.FlowDirection
        };
        bannerRow.Children.Add(glassOrb);
        bannerRow.Children.Add(_bannerText);
        var glassContent = new Grid();
        glassContent.Children.Add(new Border
        {
            Height = 14,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(18, 18, 8, 8),
            Background = new LinearGradientBrush(
                Color.FromArgb(82, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                90)
        });
        glassContent.Children.Add(bannerRow);
        _banner = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(218, 5, 5, 6), 0),
                    new(Color.FromArgb(155, 55, 55, 59), 0.48),
                    new(Color.FromArgb(205, 3, 3, 4), 1)
                }
            },
            BorderBrush = CreateGlassRimBrush(),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12, 9, 16, 9),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(28),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0, 0, 0),
                BlurRadius = 28,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0.52
            },
            Child = glassContent
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
        };
        root.Children.Add(_surface);
        root.Children.Add(_banner);
        Content = root;

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ResetBanner();
        };

        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    public void ShowOn(Forms.Screen screen)
    {
        ResetBanner();
        if (!IsVisible)
        {
            Show();
        }

        var handle = new WindowInteropHelper(this).Handle;
        var bounds = screen.Bounds;
        SetWindowPos(
            handle,
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SetWindowPositionNoActivate | SetWindowPositionShowWindow);
        _surface.Start();
    }

    public void DropWhip()
    {
        _surface.DropWhip();
    }

    public void SetDismissShortcut(string shortcut)
    {
        _dismissShortcut = shortcut;
        ResetBanner();
    }

    public void ShowFeedback(string message, bool success)
    {
        _bannerText.Text = message;
        _bannerText.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
            : new SolidColorBrush(Color.FromRgb(205, 205, 208));
        _banner.Opacity = success ? 1 : 0.88;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        _feedbackTimer.Stop();
        _surface.Stop();
        base.OnClosed(eventArgs);
    }

    private void ResetBanner()
    {
        var instruction = AppLocalizer.Text(TextKey.Instruction);
        var separator = instruction.IndexOf('·');
        _bannerText.Text = $"{(separator >= 0 ? instruction[..separator].TrimEnd() : instruction)} · [{_dismissShortcut}]";
        _bannerText.Foreground = new SolidColorBrush(Color.FromRgb(250, 250, 250));
        _banner.BorderBrush = CreateGlassRimBrush();
        _banner.Opacity = 1;
    }

    private static LinearGradientBrush CreateGlassRimBrush() => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new(Color.FromArgb(185, 255, 255, 255), 0),
            new(Color.FromArgb(35, 255, 255, 255), 0.46),
            new(Color.FromArgb(115, 255, 255, 255), 1)
        }
    };

    private void ApplyNoActivateStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, ExtendedWindowStyleIndex).ToInt64();
        SetWindowLongPtr(
            handle,
            ExtendedWindowStyleIndex,
            new nint(extendedStyle | ExtendedWindowStyleNoActivate | ExtendedWindowStyleToolWindow));
    }

    private const int ExtendedWindowStyleIndex = -20;
    private const long ExtendedWindowStyleNoActivate = 0x08000000L;
    private const long ExtendedWindowStyleToolWindow = 0x00000080L;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

internal sealed class WhipSurface : FrameworkElement
{
    private const int SegmentCount = 28;
    private const double Gravity = 0.52;
    private const double HandleVelocitySmoothingSeconds = 0.055;

    private readonly List<WhipPoint> _points = new(SegmentCount);
    private readonly Stopwatch _clock = new();
    private readonly WhipGestureDetector _gestureDetector = new();
    private Point _cursor;
    private Point _previousCursor;
    private Vector _smoothedHandleVelocity;
    private TimeSpan _lastFrame;
    private double _flashUntilSeconds;
    private double _flashIntensity;
    private bool _running;
    private bool _dropping;

    public event EventHandler? Cracked;
    public event EventHandler? Dismissed;

    public WhipSurface()
    {
        Focusable = false;
        MouseMove += OnMouseMove;
    }

    public void Start()
    {
        Stop();
        _points.Clear();
        _dropping = false;
        _flashUntilSeconds = 0;
        _flashIntensity = 0;

        var screenPoint = Forms.Cursor.Position;
        try
        {
            _cursor = PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
        }
        catch (InvalidOperationException)
        {
            _cursor = new Point(Math.Max(80, ActualWidth * 0.35), Math.Max(80, ActualHeight * 0.55));
        }

        _previousCursor = _cursor;
        _smoothedHandleVelocity = default;
        for (var index = 0; index < SegmentCount; index++)
        {
            var progress = index / (double)(SegmentCount - 1);
            var position = new Point(
                _cursor.X + (progress * 250),
                _cursor.Y - (Math.Sin(progress * Math.PI * 0.78) * 130));
            _points.Add(new WhipPoint(position));
        }

        _clock.Restart();
        _gestureDetector.Reset();
        _lastFrame = TimeSpan.Zero;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
        InvalidateVisual();
    }

    public void DropWhip()
    {
        if (_running)
        {
            _dropping = true;
        }
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _running = false;
        _clock.Stop();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        return new PointHitTestResult(this, hitTestParameters.HitPoint);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_points.Count < 2)
        {
            return;
        }

        for (var index = 0; index < _points.Count - 1; index++)
        {
            var progress = index / (double)(_points.Count - 2);
            var width = 7.5 - (progress * 6.2);
            var outer = new Pen(new SolidColorBrush(Color.FromArgb(205, 0, 0, 0)), width + 3.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var bodyValue = (byte)(34 + (progress * 92));
            var innerColor = Color.FromRgb(bodyValue, bodyValue, (byte)(bodyValue + 2));
            var inner = new Pen(new SolidColorBrush(innerColor), width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var highlight = new Pen(
                new SolidColorBrush(Color.FromArgb((byte)(165 - (progress * 70)), 255, 255, 255)),
                Math.Max(0.55, width * 0.18))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawingContext.DrawLine(outer, _points[index].Position, _points[index + 1].Position);
            drawingContext.DrawLine(inner, _points[index].Position, _points[index + 1].Position);
            drawingContext.DrawLine(highlight, _points[index].Position, _points[index + 1].Position);
        }

        var handle = _points[0].Position;
        var handleDirection = _points[1].Position - handle;
        if (handleDirection.Length > 0.01)
        {
            handleDirection.Normalize();
            var handleEnd = handle - (handleDirection * 48);
            var handleShadow = new Pen(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 18)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var handleRim = new Pen(new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)), 14.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(10, 10, 11)), 12);
            var gripPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), 2.2);
            drawingContext.DrawLine(handleShadow, handleEnd, handle);
            drawingContext.DrawLine(handleRim, handleEnd, handle);
            drawingContext.DrawLine(handlePen, handleEnd, handle);
            drawingContext.DrawLine(gripPen, handleEnd, handle);
            drawingContext.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(7, 7, 8)),
                new Pen(new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)), 1.2),
                handleEnd,
                6.5,
                6.5);
        }

        var tip = _points[^1].Position;
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(245, 245, 245)),
            new Pen(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 1),
            tip,
            2.5,
            2.5);

        if (_clock.Elapsed.TotalSeconds < _flashUntilSeconds)
        {
            var scale = 0.72 + (_flashIntensity * 0.78);
            var flash = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(245, 255, 255, 255), 0),
                    new(Color.FromArgb(125, 255, 255, 255), 0.34),
                    new(Color.FromArgb(0, 255, 255, 255), 1)
                }
            };
            drawingContext.DrawEllipse(flash, null, tip, 15 * scale, 15 * scale);
            var rayPen = new Pen(
                new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                1.2 + (_flashIntensity * 1.1));
            for (var ray = 0; ray < 8; ray++)
            {
                var angle = ray * Math.PI / 4;
                var start = tip + new Vector(Math.Cos(angle) * 12 * scale, Math.Sin(angle) * 12 * scale);
                var end = tip + new Vector(Math.Cos(angle) * 27 * scale, Math.Sin(angle) * 27 * scale);
                drawingContext.DrawLine(rayPen, start, end);
            }
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (!_dropping)
        {
            _cursor = eventArgs.GetPosition(this);
        }
    }

    private void OnRendering(object? sender, EventArgs eventArgs)
    {
        if (!_running || _points.Count == 0)
        {
            return;
        }

        var elapsed = _clock.Elapsed;
        var frameSeconds = Math.Clamp((elapsed - _lastFrame).TotalSeconds, 1.0 / 240.0, 1.0 / 20.0);
        _lastFrame = elapsed;
        var frameScale = frameSeconds * 60;

        for (var index = _dropping ? 0 : 1; index < _points.Count; index++)
        {
            var point = _points[index];
            var velocity = (point.Position - point.Previous) * 0.985;
            point.Previous = point.Position;
            point.Position += velocity + new Vector(0, Gravity * frameScale * frameScale * (_dropping ? 2.4 : 1));
        }

        if (!_dropping)
        {
            _points[0].Previous = _previousCursor;
            _points[0].Position = _cursor;
        }

        for (var iteration = 0; iteration < 9; iteration++)
        {
            for (var index = 0; index < _points.Count - 1; index++)
            {
                var first = _points[index];
                var second = _points[index + 1];
                var delta = second.Position - first.Position;
                var distance = Math.Max(0.001, delta.Length);
                var progress = index / (double)(_points.Count - 2);
                var targetLength = 17.5 - (progress * 5.8);
                var correction = delta * ((distance - targetLength) / distance);

                if (!_dropping && index == 0)
                {
                    second.Position -= correction;
                    first.Position = _cursor;
                }
                else
                {
                    first.Position += correction * 0.5;
                    second.Position -= correction * 0.5;
                }
            }

            if (!_dropping)
            {
                _points[0].Position = _cursor;
            }
        }

        var tip = _points[^1];
        var tipSpeed = (tip.Position - tip.Previous).Length / Math.Max(0.2, frameScale);
        var rawHandleVelocity = (_cursor - _previousCursor) / Math.Max(0.2, frameScale);
        var smoothingFactor = 1 - Math.Exp(-frameSeconds / HandleVelocitySmoothingSeconds);
        _smoothedHandleVelocity += (rawHandleVelocity - _smoothedHandleVelocity) * smoothingFactor;
        var seconds = elapsed.TotalSeconds;
        if (!_dropping && _gestureDetector.Update(seconds, _smoothedHandleVelocity, tipSpeed, out var intensity))
        {
            _flashUntilSeconds = seconds + 0.14;
            _flashIntensity = intensity;
            CrackSound.Play(intensity);
            Cracked?.Invoke(this, EventArgs.Empty);
        }

        _previousCursor = _cursor;
        InvalidateVisual();

        if (_dropping && _points.All(point => point.Position.Y > ActualHeight + 90))
        {
            Stop();
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class WhipPoint(Point position)
    {
        public Point Position { get; set; } = position;
        public Point Previous { get; set; } = position;
    }
}

internal sealed class WhipGestureDetector
{
    private const double SpawnGraceSeconds = 0.28;
    private const double CooldownSeconds = 0.65;
    private const double ReversalWindowSeconds = 0.30;
    private const double TipForceThreshold = 18;
    private const double FastHandleSpeed = 10;
    private const double MinimumReversalCombinedSpeed = 24;
    private const double ReversalDotThreshold = -0.35;

    private Vector _pendingImpulseVelocity;
    private double _pendingImpulseSeconds = double.NegativeInfinity;
    private double _lastCrackSeconds = double.NegativeInfinity;

    public void Reset()
    {
        _pendingImpulseVelocity = default;
        _pendingImpulseSeconds = double.NegativeInfinity;
        _lastCrackSeconds = double.NegativeInfinity;
    }

    public bool Update(double seconds, Vector handleVelocity, double tipSpeed, out double intensity)
    {
        intensity = 0;
        var handleSpeed = handleVelocity.Length;
        var pendingImpulseSpeed = _pendingImpulseVelocity.Length;
        var pendingImpulseIsRecent = seconds - _pendingImpulseSeconds <= ReversalWindowSeconds;

        var reversal = false;
        var directionSimilarity = 1.0;
        if (handleSpeed >= FastHandleSpeed
            && pendingImpulseSpeed >= FastHandleSpeed
            && pendingImpulseIsRecent)
        {
            directionSimilarity = Vector.Multiply(handleVelocity, _pendingImpulseVelocity)
                / (handleSpeed * pendingImpulseSpeed);
            reversal = directionSimilarity <= ReversalDotThreshold
                && handleSpeed + pendingImpulseSpeed >= MinimumReversalCombinedSpeed;
        }

        if (handleSpeed >= FastHandleSpeed)
        {
            if (!pendingImpulseIsRecent
                || reversal
                || directionSimilarity < 0
                || handleSpeed > pendingImpulseSpeed)
            {
                _pendingImpulseVelocity = handleVelocity;
            }

            _pendingImpulseSeconds = seconds;
        }

        var eligible = seconds >= SpawnGraceSeconds
            && seconds - _lastCrackSeconds >= CooldownSeconds;

        if (!eligible || !reversal)
        {
            return false;
        }

        var reversalForce = Math.Clamp(
            (handleSpeed + pendingImpulseSpeed - MinimumReversalCombinedSpeed) / 40,
            0,
            1);
        var tipForce = Math.Clamp((tipSpeed - TipForceThreshold) / 28, 0, 1);
        intensity = Math.Max(reversalForce, tipForce);
        _lastCrackSeconds = seconds;
        return true;
    }

    public static bool RunSelfTest(out string message)
    {
        var graceDetector = new WhipGestureDetector();
        graceDetector.Reset();
        if (graceDetector.Update(0.08, new Vector(20, 0), 4, out _)
            || graceDetector.Update(0.10, new Vector(-20, 0), 30, out _))
        {
            message = "FAIL: spawn grace did not block an early crack.";
            return false;
        }

        var slowDetector = new WhipGestureDetector();
        slowDetector.Reset();
        if (slowDetector.Update(0.30, new Vector(8, 0), 4, out _)
            || slowDetector.Update(0.50, new Vector(-8, 0), 40, out _))
        {
            message = "FAIL: slow pointer motion triggered a crack.";
            return false;
        }

        var reversalDetector = new WhipGestureDetector();
        reversalDetector.Reset();
        if (reversalDetector.Update(0.30, new Vector(16, 0), 4, out _)
            || reversalDetector.Update(0.38, default, 4, out _)
            || !reversalDetector.Update(0.54, new Vector(-16, 0), 4, out var naturalIntensity))
        {
            message = "FAIL: a natural fast reversal was not detected.";
            return false;
        }

        if (reversalDetector.Update(0.70, new Vector(20, 0), 20, out _)
            || reversalDetector.Update(0.80, new Vector(-20, 0), 20, out _))
        {
            message = "FAIL: cooldown allowed a duplicate crack.";
            return false;
        }

        if (reversalDetector.Update(1.12, new Vector(20, 0), 20, out _)
            || !reversalDetector.Update(1.22, new Vector(-20, 0), 20, out _))
        {
            message = "FAIL: cooldown blocked a deliberate repeated crack.";
            return false;
        }

        var strongDetector = new WhipGestureDetector();
        strongDetector.Reset();
        if (strongDetector.Update(0.30, new Vector(30, 0), 4, out _)
            || !strongDetector.Update(0.34, new Vector(-30, 0), 4, out var strongIntensity)
            || strongIntensity <= naturalIntensity)
        {
            message = "FAIL: gesture force did not increase with movement speed.";
            return false;
        }

        var tipOnlyDetector = new WhipGestureDetector();
        tipOnlyDetector.Reset();
        if (tipOnlyDetector.Update(0.30, new Vector(16, 0), 4, out _)
            || tipOnlyDetector.Update(0.54, default, 46, out _))
        {
            message = "FAIL: a tip impulse without reversal triggered a crack.";
            return false;
        }

        var idleDetector = new WhipGestureDetector();
        idleDetector.Reset();
        for (var index = 0; index < 20; index++)
        {
            if (idleDetector.Update(0.30 + (index * 0.02), new Vector(2.5, 0), 8, out _))
            {
                message = "FAIL: ordinary pointer motion triggered a crack.";
                return false;
            }
        }

        message = "PASS: fast reversal, force, slow motion, tip-only motion, grace, cooldown, and idle motion.";
        return true;
    }
}

internal static class CrackSound
{
    private static readonly SoundPlayer[] Players = CreatePlayers();

    public static void Play(double intensity)
    {
        var level = SelectLevel(intensity);
        try
        {
            Players[level - 1].Play();
        }
        catch (InvalidOperationException)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    internal static bool RunSelfTest(out string message)
    {
        for (var level = 1; level <= 4; level++)
        {
            var path = GetSoundPath(level);
            if (SelectLevel(level / 4.0) != level || !File.Exists(path))
            {
                message = $"FAIL: whip sound level {level} is missing or mapped incorrectly.";
                return false;
            }

            try
            {
                using var player = new SoundPlayer(path);
                player.Load();
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                message = $"FAIL: whip sound level {level} cannot be decoded: {exception.Message}";
                return false;
            }
        }

        message = "PASS: whip sound levels 1-4 are present, decodable, and force-mapped.";
        return true;
    }

    private static SoundPlayer[] CreatePlayers()
    {
        var players = Enumerable.Range(1, 4)
            .Select(level => new SoundPlayer(GetSoundPath(level)))
            .ToArray();
        foreach (var player in players)
        {
            try
            {
                player.Load();
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                // Self-test reports broken assets; steering remains available without audio.
            }
        }

        return players;
    }

    private static int SelectLevel(double intensity) =>
        Math.Clamp((int)Math.Ceiling(Math.Clamp(intensity, 0, 1) * 4), 1, 4);

    private static string GetSoundPath(int level) =>
        Path.Combine(AppContext.BaseDirectory, "sounds", $"whip_{level}.wav");
}

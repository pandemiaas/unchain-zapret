using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Unchain.Controls;

public enum PowerState { Off, Connecting, On, Error }

public partial class PowerButton : UserControl
{
    static readonly Color OffDisk = Color.FromRgb(0x26, 0x29, 0x30);
    static readonly Color OffIcon = Color.FromRgb(0x9A, 0xA1, 0xAE);
    static readonly Color OffGlow = Color.FromRgb(0x30, 0x34, 0x3C);
    static readonly Color Green = Color.FromRgb(0x1F, 0xA8, 0x5A);
    static readonly Color GreenGlow = Color.FromRgb(0x2E, 0xD1, 0x73);
    static readonly Color Red = Color.FromRgb(0xB8, 0x33, 0x3A);
    static readonly Color RedGlow = Color.FromRgb(0xE5, 0x48, 0x4D);

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(PowerState), typeof(PowerButton),
        new PropertyMetadata(PowerState.Off, (d, _) => ((PowerButton)d).Apply()));

    public PowerState State
    {
        get => (PowerState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public event EventHandler? Click;

    bool _pressed;

    public PowerButton()
    {
        InitializeComponent();
        Apply();
    }

    void Apply()
    {
        var (disk, glow, icon) = State switch
        {
            PowerState.On or PowerState.Connecting => (Green, GreenGlow, Colors.White),
            PowerState.Error => (Red, RedGlow, Colors.White),
            _ => (OffDisk, OffGlow, OffIcon),
        };

        var dur = TimeSpan.FromMilliseconds(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DiskBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(disk, dur) { EasingFunction = ease });
        HaloStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(glow, dur) { EasingFunction = ease });
        IconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(icon, dur) { EasingFunction = ease });
        Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(State == PowerState.Off ? 0.5 : 0.85, dur));

        var spin = State == PowerState.Connecting;
        Spinner.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        PowerIcon.Visibility = spin ? Visibility.Collapsed : Visibility.Visible;
        SpinRot.BeginAnimation(RotateTransform.AngleProperty, spin
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);

        // «Дыхание» свечения при подборе
        HaloStop.BeginAnimation(GradientStop.OffsetProperty, spin
            ? new DoubleAnimation(0.45, 0.7, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() }
            : new DoubleAnimation(0.55, dur));

        SetRipples(State == PowerState.On);

        // Орбита
        var orbitOn = State is PowerState.On or PowerState.Connecting;
        Orbit.BeginAnimation(OpacityProperty, new DoubleAnimation(orbitOn ? (spin ? 1 : 0.55) : 0, dur));
        OrbitRot.BeginAnimation(RotateTransform.AngleProperty, orbitOn
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(spin ? 1.4 : 14)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    void SetRipples(bool on)
    {
        Ripple(Ring1, Ring1Scale, on, TimeSpan.Zero);
        Ripple(Ring2, Ring2Scale, on, TimeSpan.FromSeconds(1.3));
    }

    static void Ripple(UIElement ring, ScaleTransform scale, bool on, TimeSpan delay)
    {
        if (!on)
        {
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        var period = TimeSpan.FromSeconds(2.6);
        var grow = new DoubleAnimation(1.0, 1.32, period)
        {
            BeginTime = delay,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(0.55, 0, period) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    void AnimateScale(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void OnEnter(object sender, MouseEventArgs e) => AnimateScale(1.04);
    void OnLeave(object sender, MouseEventArgs e) { _pressed = false; AnimateScale(1.0); }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        AnimateScale(0.94);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        AnimateScale(Root.IsMouseOver ? 1.04 : 1.0);
        if (!_pressed) return;
        _pressed = false;
        Click?.Invoke(this, EventArgs.Empty);
    }
}

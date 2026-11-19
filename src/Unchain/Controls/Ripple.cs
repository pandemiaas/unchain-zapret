using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Unchain.Controls;

/// <summary>
/// Волна от точки клика для всех кнопок, в шаблоне которых есть Canvas «RippleHost».
/// </summary>
public static class Ripple
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDown), true);

    static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Control { IsEnabled: true } c) return;
        if (c.Template?.FindName("RippleHost", c) is not Canvas host || host.ActualWidth <= 0) return;

        var p = e.GetPosition(host);
        var size = Math.Max(host.ActualWidth, host.ActualHeight) * 2.2;
        var scale = new ScaleTransform(0, 0);
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        Canvas.SetLeft(dot, p.X - size / 2);
        Canvas.SetTop(dot, p.Y - size / 2);
        host.Children.Add(dot);

        var d = TimeSpan.FromMilliseconds(550);
        var grow = new DoubleAnimation(0, 1, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(1, 0, d) { BeginTime = TimeSpan.FromMilliseconds(80) };
        fade.Completed += (_, _) => host.Children.Remove(dot);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        dot.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}

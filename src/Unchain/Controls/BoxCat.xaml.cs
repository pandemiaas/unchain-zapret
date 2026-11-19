using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShapePath = System.Windows.Shapes.Path;

namespace Unchain.Controls;

/// <summary>Котик в коробке: дышит, машет хвостом, месит лапками воздух. Клик — погладить.</summary>
public partial class BoxCat : UserControl
{
    static readonly Geometry Heart = Geometry.Parse("M 0,3 C 0,-1 5,-1 6,2 C 7,-1 12,-1 12,3 C 12,7 6,10 6,12 C 6,10 0,7 0,3 Z");
    readonly Random _rnd = new();

    /// <summary>Котика погладили.</summary>
    public event EventHandler? Petted;

    public BoxCat()
    {
        InitializeComponent();
        Loaded += (_, _) => Animate();
    }

    void Animate()
    {
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        Breath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.04, TimeSpan.FromSeconds(1.7))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
        Breath.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.03, TimeSpan.FromSeconds(1.7))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });

        TailRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-14, 12, TimeSpan.FromSeconds(1.3))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });

        PawL.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -2.2, TimeSpan.FromSeconds(0.45))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
        PawR.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2.2, 0, TimeSpan.FromSeconds(0.45))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine });
    }

    void OnPet(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Fx);
        for (var i = 0; i < 3; i++) SpawnHeart(p, i);

        // Котик жмурится от удовольствия
        Eyes.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(150)) { AutoReverse = true });
        Petted?.Invoke(this, EventArgs.Empty);
    }

    void SpawnHeart(Point p, int i)
    {
        var scale = 0.9 + _rnd.NextDouble() * 0.7;
        var heart = new ShapePath
        {
            Data = Heart,
            Fill = new SolidColorBrush(i == 1 ? Color.FromRgb(0xFF, 0x9C, 0xB0) : Color.FromRgb(0xFF, 0x6F, 0x8C)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
        };
        var tt = new TranslateTransform();
        var st = new ScaleTransform(scale, scale);
        heart.RenderTransform = new TransformGroup { Children = { st, tt } };
        Canvas.SetLeft(heart, p.X - 6);
        Canvas.SetTop(heart, p.Y - 6);
        Fx.Children.Add(heart);

        var d = TimeSpan.FromMilliseconds(900 + _rnd.Next(300));
        var delay = TimeSpan.FromMilliseconds(90 * i);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -40 - _rnd.Next(25), d)
        { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, _rnd.Next(-22, 23), d) { BeginTime = delay });
        var fade = new DoubleAnimation(1, 0, d) { BeginTime = delay };
        fade.Completed += (_, _) => Fx.Children.Remove(heart);
        heart.BeginAnimation(OpacityProperty, fade);
    }
}

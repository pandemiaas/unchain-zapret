using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Unchain.Controls;

/// <summary>Декоративный маскот: котик-клубок тихо дышит и посапывает «z».</summary>
public partial class CatBall : UserControl
{
    public CatBall()
    {
        InitializeComponent();
        Loaded += (_, _) => Animate();
    }

    void Animate()
    {
        var breath = new DoubleAnimation(1, 1.035, TimeSpan.FromSeconds(1.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Breath.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        Breath.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.015, TimeSpan.FromSeconds(1.8))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        var z = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(3.6) };
        z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        z.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
        z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));
        Zzz.BeginAnimation(OpacityProperty, z);
        ZzzT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = TimeSpan.FromSeconds(3.6),
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(-9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))),
                new LinearDoubleKeyFrame(-9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))),
            },
        });
        ZzzT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 5, TimeSpan.FromSeconds(1.2))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }
}

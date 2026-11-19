using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace Unchain.Controls;

/// <summary>
/// Линейная иконка из собственного набора (Icons в App.xaml, сетка 24×24).
/// Цвет наследуется от Foreground родителя — иконки в кнопках подсвечиваются вместе с текстом.
/// </summary>
public sealed class Glyph : Viewbox
{
    readonly ShapePath _path = new()
    {
        StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(Geometry), typeof(Glyph),
        new PropertyMetadata(null, (d, e) => ((Glyph)d)._path.Data = (Geometry?)e.NewValue));

    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(Glyph),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Glyph),
        new PropertyMetadata(1.8, (d, e) => ((Glyph)d)._path.StrokeThickness = (double)e.NewValue));

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Glyph()
    {
        Width = 18;
        Height = 18;
        IsHitTestVisible = false;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_path);
        Child = canvas;
        _path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
    }
}

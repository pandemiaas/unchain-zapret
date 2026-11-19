using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Unchain.Controls;

/// <summary>
/// Змейка в стиле приложения: зелёная змейка охотится за рыбками на тёмном поле.
/// Demo = true — ползает сама (для виджета), Demo = false — управляется стрелками/WASD.
/// </summary>
public sealed class SnakeGame : FrameworkElement
{
    readonly record struct Cell(int X, int Y);

    static readonly Brush DotBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    static readonly Brush FishBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xC0, 0x62)));
    static readonly Brush FishEye = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x1C, 0x10)));
    static readonly Brush EyeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F)));
    static readonly Color Head = Color.FromRgb(0x5B, 0xF0, 0x9A);
    static readonly Color Tail = Color.FromRgb(0x14, 0x5E, 0x38);

    readonly DispatcherTimer _timer = new();
    readonly List<Cell> _body = new();
    readonly Random _rnd = new();
    Cell _dir = new(1, 0), _next = new(1, 0), _food;
    double _phase;
    bool _waiting;

    public int Cols { get; set; } = 14;
    public int Rows { get; set; } = 10;
    public bool Demo { get; set; } = true;
    public int Score { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsWaiting => _waiting;

    public event Action<int>? ScoreChanged;
    public event Action<int>? GameOver;

    public SnakeGame()
    {
        _timer.Tick += (_, _) => Step();
        Loaded += (_, _) => { if (_body.Count == 0) Reset(); if (IsVisible) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) _timer.Start(); else _timer.Stop(); };
    }

    public void Reset()
    {
        _body.Clear();
        var cx = Cols / 2;
        var cy = Rows / 2;
        for (var i = 0; i < 4; i++) _body.Add(new Cell(cx - i, cy));
        _dir = _next = new Cell(1, 0);
        Score = 0;
        IsDead = false;
        _waiting = !Demo; // в игре ждём первую стрелку
        PlaceFood();
        _timer.Interval = TimeSpan.FromMilliseconds(Demo ? 140 : 135);
        ScoreChanged?.Invoke(0);
        InvalidateVisual();
    }

    /// <summary>Поворот (из обработчика клавиш окна).</summary>
    public void Turn(int dx, int dy)
    {
        if (Demo || IsDead) return;
        _waiting = false;
        if (dx == -_dir.X && dy == -_dir.Y) return; // разворот на 180° запрещён
        _next = new Cell(dx, dy);
    }

    void PlaceFood()
    {
        if (_body.Count >= Cols * Rows) return;
        do _food = new Cell(_rnd.Next(Cols), _rnd.Next(Rows));
        while (_body.Contains(_food));
    }

    void Step()
    {
        _phase += 0.4;
        if (IsDead || _waiting) { InvalidateVisual(); return; }
        if (Demo) _next = AutoPilot();
        _dir = _next;

        var h = _body[0];
        var n = new Cell(h.X + _dir.X, h.Y + _dir.Y);
        if (Demo) n = new Cell((n.X + Cols) % Cols, (n.Y + Rows) % Rows); // в виджете стены «сквозные»

        var hitWall = n.X < 0 || n.Y < 0 || n.X >= Cols || n.Y >= Rows;
        var hitSelf = _body.Take(_body.Count - 1).Contains(n);
        if (hitWall || hitSelf)
        {
            if (Demo) { Reset(); return; }
            IsDead = true;
            GameOver?.Invoke(Score);
            InvalidateVisual();
            return;
        }

        _body.Insert(0, n);
        if (n == _food)
        {
            Score++;
            ScoreChanged?.Invoke(Score);
            PlaceFood();
            if (Demo)
            {
                if (_body.Count > 12) _body.RemoveAt(_body.Count - 1);
            }
            else if (_timer.Interval.TotalMilliseconds > 65)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(_timer.Interval.TotalMilliseconds - 3);
            }
        }
        else _body.RemoveAt(_body.Count - 1);

        InvalidateVisual();
    }

    /// <summary>Автопилот для виджета: к рыбке, не врезаясь в себя.</summary>
    Cell AutoPilot()
    {
        var options = new[] { _dir, new Cell(_dir.Y, -_dir.X), new Cell(-_dir.Y, _dir.X) };
        var best = _dir;
        var bestScore = double.MaxValue;
        var h = _body[0];
        foreach (var d in options)
        {
            var n = new Cell((h.X + d.X + Cols) % Cols, (h.Y + d.Y + Rows) % Rows);
            if (_body.Take(_body.Count - 1).Contains(n)) continue;
            var dx = Math.Abs(n.X - _food.X);
            var dy = Math.Abs(n.Y - _food.Y);
            var dist = Math.Min(dx, Cols - dx) + Math.Min(dy, Rows - dy) + _rnd.NextDouble() * 0.6;
            if (dist < bestScore) { bestScore = dist; best = d; }
        }
        return best;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var hgt = ActualHeight;
        if (w <= 0 || hgt <= 0 || _body.Count == 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, hgt)); // чтобы ловить клики
        var cell = Math.Min(w / Cols, hgt / Rows);
        var ox = (w - cell * Cols) / 2;
        var oy = (hgt - cell * Rows) / 2;
        Point C(Cell c) => new(ox + (c.X + 0.5) * cell, oy + (c.Y + 0.5) * cell);

        // точки сетки
        for (var y = 0; y < Rows; y++)
            for (var x = 0; x < Cols; x++)
                dc.DrawEllipse(DotBrush, null, C(new Cell(x, y)), 1, 1);

        // рыбка
        var f = C(_food);
        var bob = Math.Sin(_phase) * cell * 0.06;
        var fr = cell * 0.32;
        dc.PushTransform(new TranslateTransform(0, bob));
        dc.DrawEllipse(FishBrush, null, f, fr, fr * 0.68);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            g.BeginFigure(new Point(f.X - fr * 0.8, f.Y), true, true);
            g.LineTo(new Point(f.X - fr * 1.5, f.Y - fr * 0.6), true, false);
            g.LineTo(new Point(f.X - fr * 1.5, f.Y + fr * 0.6), true, false);
        }
        tail.Freeze();
        dc.DrawGeometry(FishBrush, null, tail);
        dc.DrawEllipse(FishEye, null, new Point(f.X + fr * 0.45, f.Y - fr * 0.12), fr * 0.12, fr * 0.12);
        dc.Pop();

        // змейка: от хвоста к голове, цвет плавно светлеет
        var n = _body.Count;
        for (var i = n - 1; i >= 0; i--)
        {
            var t = n == 1 ? 0 : (double)i / (n - 1);
            var col = Color.FromRgb(
                (byte)(Head.R + (Tail.R - Head.R) * t),
                (byte)(Head.G + (Tail.G - Head.G) * t),
                (byte)(Head.B + (Tail.B - Head.B) * t));
            var inset = cell * (0.1 + 0.08 * t);
            var p = C(_body[i]);
            var r = new Rect(p.X - cell / 2 + inset, p.Y - cell / 2 + inset, cell - inset * 2, cell - inset * 2);
            var brush = new SolidColorBrush(IsDead ? Color.FromRgb(0x8A, 0x3A, 0x3E) : col);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, r, cell * 0.3, cell * 0.3);
        }

        // глазки
        var head = C(_body[0]);
        var ex = _dir.Y != 0 ? cell * 0.18 : 0;
        var ey = _dir.X != 0 ? cell * 0.18 : 0;
        var fwd = new Vector(_dir.X * cell * 0.15, _dir.Y * cell * 0.15);
        dc.DrawEllipse(EyeBrush, null, head + fwd + new Vector(ex, ey), cell * 0.07, cell * 0.07);
        dc.DrawEllipse(EyeBrush, null, head + fwd - new Vector(ex, ey), cell * 0.07, cell * 0.07);
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Unchain.Controls;
using Unchain.Core;
using Unchain.Core.Telegram;
using Ellipse = System.Windows.Shapes.Ellipse;
using WinForms = System.Windows.Forms;

namespace Unchain;

public partial class MainWindow : Window
{
    enum Svc { Checking, Ok, Blocked, Neutral }
    enum SheetKind { None, Catalog, Strategies, Diagnostics, Log, Snake }

    static readonly Color CGreen = Color.FromRgb(0x2E, 0xD1, 0x73);
    static readonly Color CRed = Color.FromRgb(0xE5, 0x67, 0x6D);
    static readonly Color CAmber = Color.FromRgb(0xE6, 0xB4, 0x50);
    static readonly Color CMuted = Color.FromRgb(0x7C, 0x84, 0x94);
    static readonly Color CFaint = Color.FromRgb(0x4F, 0x55, 0x61);

    readonly Settings _s = Settings.Load();
    readonly ZapretRunner _zapret = new();
    readonly bool _autostart;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _recheck = new() { Interval = TimeSpan.FromMinutes(5) };
    readonly DispatcherTimer _restartDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    readonly Dictionary<string, StrategyTest> _tests = new(StringComparer.OrdinalIgnoreCase);

    TgWsBridge? _tg;
    CancellationTokenSource? _op;
    DateTime? _since;
    bool _partial;
    int? _pingMs;
    readonly List<int> _pingHist = new();
    int _tab;
    SheetKind _sheet = SheetKind.None;
    ListKind _listKind = ListKind.Bypass;
    bool _loading = true, _exiting, _started, _trayHintShown;
    int _logLines;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripItem? _trayToggle;

    public MainWindow(bool autostart)
    {
        _autostart = autostart;
        InitializeComponent();

        _zapret.GameFilter = _s.GameFilter;
        _zapret.Ipset = _s.Ipset;

        Log.Line += l => Dispatcher.InvokeAsync(() => AppendLog(l));
        _zapret.Exited += c => Dispatcher.InvokeAsync(() => OnZapretDied(c));
        UserLists.Changed += () => Dispatcher.InvokeAsync(ScheduleRestart);

        _clock.Tick += (_, _) => UpdateClock();
        _recheck.Tick += async (_, _) => await RecheckAsync();
        _restartDebounce.Tick += (_, _) => { _restartDebounce.Stop(); RestartIfRunning(); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };
        _clock.Start();

        SourceInitialized += (_, _) => ApplyDwm();
        Loaded += async (_, _) => { MoveIndicator(false); await StartupAsync(); };
        TabsGrid.SizeChanged += (_, _) => MoveIndicator(false);

        InitControls();
        InitFun();
        SetupTray();
        AnimateBackground();
        _loading = false;
    }

    /// <summary>Запуск без показа окна (автозапуск с Windows).</summary>
    public void StartHidden() => _ = StartupAsync();

    async Task StartupAsync()
    {
        if (_started) return;
        _started = true;
        if (_s.EnableOnLaunch || _autostart) await EnableAsync(false);
        else await BaselineAsync();
    }

    // ═════════════════════ Главная кнопка ═════════════════════

    async void OnPowerClick(object? sender, EventArgs e)
    {
        switch (PowerBtn.State)
        {
            case PowerState.Connecting: _op?.Cancel(); break;
            case PowerState.On: Disable(); break;
            default: await EnableAsync(false); break;
        }
    }

    async Task EnableAsync(bool forceSelect)
    {
        if (_op != null) return;
        var cts = new CancellationTokenSource();
        _op = cts;
        var ct = cts.Token;

        HomeReselect.Visibility = Visibility.Collapsed;
        SetPower(PowerState.Connecting, "Подключаемся", "Запускаем обход…");
        SetSvc(YtDot, YtText, Svc.Checking, "Проверка…");
        SetSvc(DcDot, DcText, Svc.Checking, "Проверка…");
        SetBusy(true);

        try
        {
            StartTelegram();
            var sel = await StartZapretAsync(forceSelect, ct);

            _since = DateTime.Now;
            _partial = !sel.AllOk;
            _pingMs = sel.Result.AvgMs;
            RecordPing(_pingMs);
            ApplyResult(sel.Result);
            SetPower(PowerState.On, _partial ? "Работает частично" : "Всё работает", "");
            UpdateClock();
            HomeReselect.Visibility = _partial ? Visibility.Visible : Visibility.Collapsed;
            _recheck.Start();
            _ = UpdateTelegramStatusAsync();

            if (_s.ShowTelegramProxyDialog && _tg != null) OpenTgLink();
            Toast(_partial ? "Включено, но часть сервисов недоступна" : "Обход включён");
        }
        catch (OperationCanceledException)
        {
            StopAll();
            SetPower(PowerState.Off, "Выключено", "Нажми, чтобы включить обход");
            Toast("Подбор отменён");
            _ = BaselineAsync();
        }
        catch (Exception ex)
        {
            StopAll();
            Log.Write("Ошибка: " + ex.Message);
            SetPower(PowerState.Error, "Не получилось", ex.Message);
            HomeReselect.Visibility = Visibility.Visible;
            Toast("Не получилось включить", true);
        }
        finally
        {
            _op = null;
            cts.Dispose();
            SetBusy(false);
            HideProgress();
            RefreshStrategyInfo();
            if (_tab == 1) RefreshStrategyChips();
        }
    }

    async Task<Selection> StartZapretAsync(bool forceSelect, CancellationToken ct)
    {
        _zapret.GameFilter = _s.GameFilter;
        _zapret.Ipset = _s.Ipset;
        var progress = new Progress<SelectProgress>(OnSelectProgress);

        if (!forceSelect && _s is { ZapretVersion: { } ver, ZapretStrategy: { } strat })
        {
            var newer = false;
            if (_s.CheckZapretUpdates && !_s.ManualStrategy)
            {
                try
                {
                    using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    to.CancelAfter(TimeSpan.FromSeconds(5));
                    var rel = await ZapretStore.ListRemoteAsync(to.Token);
                    if (rel.Count > 0 && rel[0].Tag != ver)
                    {
                        newer = true;
                        Log.Write($"Вышла новая версия zapret {rel[0].Tag} — пробую её первой");
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }

            var build = ZapretStore.TryLoadLocal(ver);
            var file = build?.Strategies.FirstOrDefault(f => Path.GetFileName(f).Equals(strat, StringComparison.OrdinalIgnoreCase));
            if (!newer && build != null && file != null)
            {
                StatusSub.Text = $"Проверяем {ZapretStore.StrategyName(file)} · {ver}";
                _zapret.Start(build, file);
                await Task.Delay(1500, ct);
                var r = await Connectivity.RunAsync(Connectivity.BuildTargets(), ct);
                Log.Write($"Сохранённая стратегия: {r}");
                if (_zapret.IsRunning && (_s.ManualStrategy || r.AllOk || (r.CoreOk && r.Score >= _s.LastScore)))
                    return new Selection(build, file, r);
                Log.Write("Сохранённая стратегия больше не справляется — подбираю заново");
            }
        }

        var sel = await new AutoSelector(_zapret, _s.MaxVersionsToTry).RunAsync(Connectivity.BuildTargets(), progress, ct)
                  ?? throw new InvalidOperationException("Ни одна стратегия не помогла. Загляни в «Обход» → «Диагностика».");
        _s.ZapretVersion = sel.Build.Tag;
        _s.ZapretStrategy = Path.GetFileName(sel.Strategy);
        _s.LastScore = sel.Result.Score;
        _s.Save();
        return sel;
    }

    void OnSelectProgress(SelectProgress p)
    {
        if (PowerBtn.State != PowerState.Connecting) return;
        StatusTitle.Text = p.Title;
        StatusSub.Text = p.Total > 0 ? $"Проверено {p.Done} из {p.Total} · {p.Detail}" : p.Detail;
        ShowProgress(p.Total > 0 ? (double)p.Done / p.Total : -1);
        if (p.LastResult != null) ApplyResult(p.LastResult, interim: true);
    }

    void Disable()
    {
        _recheck.Stop();
        StopAll();
        _since = null;
        HomeReselect.Visibility = Visibility.Collapsed;
        SetPower(PowerState.Off, "Выключено", "Нажми, чтобы включить обход");
        Toast("Обход выключен");
        _ = BaselineAsync();
    }

    void StopAll()
    {
        _op?.Cancel();
        _zapret.Stop();
        _tg?.Dispose();
        _tg = null;
        SetSvc(TgDot, TgText, Svc.Neutral, "Без прокси");
    }

    async void OnReselect(object sender, RoutedEventArgs e)
    {
        if (_op != null) return;
        if (_s.ManualStrategy)
        {
            _s.ManualStrategy = false;
            _s.Save();
            _loading = true; ModeAuto.IsChecked = true; _loading = false;
        }
        _recheck.Stop();
        _zapret.Stop();
        CloseSheet();
        SelectTab(0);
        await EnableAsync(true);
    }

    void OnZapretDied(int code)
    {
        if (PowerBtn.State != PowerState.On || _op != null) return;
        _recheck.Stop();
        _since = null;
        SetPower(PowerState.Error, "Обход остановился", $"winws.exe закрылся (код {code}). Нажми, чтобы запустить снова");
        HomeReselect.Visibility = Visibility.Visible;
        Toast("winws.exe остановился", true);
    }

    async Task RecheckAsync()
    {
        if (PowerBtn.State != PowerState.On || _op != null) return;
        var r = await Connectivity.RunAsync(Connectivity.BuildTargets(), CancellationToken.None);
        if (PowerBtn.State != PowerState.On || _op != null) return;
        Log.Write("Плановая проверка: " + r);
        ApplyResult(r);
        _partial = !r.AllOk;
        _pingMs = r.AvgMs ?? _pingMs;
        RecordPing(r.AvgMs);
        StatusTitle.Text = _partial ? "Работает частично" : "Всё работает";
        HomeReselect.Visibility = _partial ? Visibility.Visible : Visibility.Collapsed;
        await UpdateTelegramStatusAsync();
    }

    /// <summary>Что блокируется без обхода — показываем ещё до включения.</summary>
    async Task BaselineAsync()
    {
        if (PowerBtn.State != PowerState.Off) return;
        SetSvc(YtDot, YtText, Svc.Checking, "Проверка…");
        SetSvc(DcDot, DcText, Svc.Checking, "Проверка…");
        SetSvc(TgDot, TgText, Svc.Neutral, "Без прокси");
        var r = await Connectivity.RunAsync(Connectivity.BuildTargets(), CancellationToken.None);
        if (PowerBtn.State != PowerState.Off) return;
        Log.Write("Без обхода: " + r);
        foreach (var (dot, text, svc) in new[] { (YtDot, YtText, Connectivity.YouTube), (DcDot, DcText, Connectivity.Discord) })
        {
            var ok = r.ServiceOk(svc) == true;
            SetSvc(dot, text, ok ? Svc.Ok : Svc.Blocked, ok ? "Работает без обхода" : "Блокируется");
        }
        RenderExtras(r, baseline: true);
    }

    void ApplyResult(TestResult r, bool interim = false)
    {
        foreach (var (dot, text, svc) in new[] { (YtDot, YtText, Connectivity.YouTube), (DcDot, DcText, Connectivity.Discord) })
        {
            var ok = r.ServiceOk(svc) == true;
            if (interim && !ok) SetSvc(dot, text, Svc.Checking, "Проверка…");
            else SetSvc(dot, text, ok ? Svc.Ok : Svc.Blocked, ok ? "Работает" : "Не открывается");
        }
        if (!interim) RenderExtras(r, baseline: false);
    }

    /// <summary>Плашки «Твои сервисы» на главной: то, что добавлено из каталога и вручную.</summary>
    void RenderExtras(TestResult r, bool baseline)
    {
        var extras = r.Items.Where(i => !i.Target.Core).GroupBy(i => i.Target.Service).ToList();
        ExtraServices.Children.Clear();
        ExtraBlock.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var i = 0;
        foreach (var g in extras)
        {
            var ok = g.All(x => x.Ok);
            var color = ok ? CGreen : CRed;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = g.Key, FontSize = 12, Foreground = (Brush)FindResource("Text2") });
            var ms = g.Where(x => x.Ok && x.Ms != null).Select(x => x.Ms!.Value).DefaultIfEmpty(-1).Max();
            if (!baseline && ms > 0)
                sp.Children.Add(new TextBlock { Text = $"  {ms} мс", FontSize = 11, Foreground = (Brush)FindResource("Faint"), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(0, 0, 6, 6),
                Child = sp,
                Opacity = 0,
                ToolTip = baseline ? (ok ? "Открывается без обхода" : "Блокируется") : (ok ? "Работает" : "Не открывается"),
            };
            ExtraServices.Children.Add(chip);
            chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(30 * i++) });
        }
    }

    void RecordPing(int? ms)
    {
        if (ms is not { } v) return;
        _pingHist.Add(v);
        if (_pingHist.Count > 24) _pingHist.RemoveAt(0);
        StatPing.Text = $"{v} мс";
        StatPing.Foreground = new SolidColorBrush(v < 120 ? CGreen : v < 250 ? CAmber : CRed);
        DrawSpark();
    }

    /// <summary>Мини-график пинга в плашке.</summary>
    void DrawSpark()
    {
        var w = StatPingHost.ActualWidth;
        if (w <= 0 || _pingHist.Count == 0) return;
        const double h = 18;
        var max = Math.Max(_pingHist.Max(), 1);
        var min = _pingHist.Min();
        var span = Math.Max(max - min, 20);
        var pts = new PointCollection();
        var n = _pingHist.Count;
        for (var k = 0; k < n; k++)
        {
            var x = n == 1 ? w : w * k / (n - 1);
            var y = 2 + (h - 4) * (_pingHist[k] - min) / span;
            pts.Add(new Point(x, y));
        }
        if (n == 1) pts.Insert(0, new Point(0, pts[0].Y));
        StatPingLine.Points = pts;
    }

    async Task UpdateTelegramStatusAsync()
    {
        if (_tg == null) { SetSvc(TgDot, TgText, Svc.Neutral, "Без прокси"); return; }
        SetSvc(TgDot, TgText, Svc.Checking, "Проверка…");
        bool ok;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ok = await TgWsBridge.ProbeAsync(cts.Token);
        }
        catch { ok = false; }
        if (_tg == null) return;
        SetSvc(TgDot, TgText, ok ? Svc.Ok : Svc.Blocked, ok ? "Через прокси" : "Нет связи с серверами");
    }

    // ═════════════════════ Telegram ═════════════════════

    void StartTelegram()
    {
        if (_tg != null) return;
        try
        {
            _tg = new TgWsBridge(_s.TelegramPort);
            _tg.Start();
            SetSvc(TgDot, TgText, Svc.Checking, "Запуск…");
        }
        catch (SocketException ex)
        {
            _tg?.Dispose();
            _tg = null;
            Log.Write($"Порт {_s.TelegramPort} занят ({ex.Message}). Поменяй TelegramPort в {Paths.SettingsFile}");
            SetSvc(TgDot, TgText, Svc.Blocked, $"Порт {_s.TelegramPort} занят");
        }
    }

    void OpenTgLink()
    {
        var link = $"tg://socks?server=127.0.0.1&port={_s.TelegramPort}";
        try { SystemTools.OpenUnelevated(link); }
        catch (Exception ex)
        {
            try { Clipboard.SetText(link); } catch { }
            Log.Write($"Telegram не открылся ({ex.Message}). Ссылка скопирована: {link}");
            Toast("Ссылка на прокси скопирована", true);
        }
        if (_tg == null) Toast("Прокси заработает после включения обхода");
    }

    void OnTgLink(object sender, RoutedEventArgs e) => OpenTgLink();

    // ═════════════════════ Состояние / анимации ═════════════════════

    void SetPower(PowerState st, string title, string sub)
    {
        PowerBtn.State = st;
        FadeText(StatusTitle, title);
        StatusSub.Text = sub;
        SetMood(st);

        var on = st == PowerState.On;
        SumState.Text = st switch
        {
            PowerState.On => "Включено",
            PowerState.Connecting => "Подбор…",
            PowerState.Error => "Ошибка",
            _ => "Выключено",
        };
        UpdateClock();
        TitleDot.Background = new SolidColorBrush(on ? CGreen : st == PowerState.Error ? CRed : CFaint);
        if (_tray != null)
        {
            _tray.Text = on ? "Unchain — обход включён" : "Unchain — выключено";
            if (_trayToggle != null) _trayToggle.Text = on || st == PowerState.Connecting ? "Выключить" : "Включить";
        }
    }

    static void FadeText(TextBlock tb, string text)
    {
        if (tb.Text == text) return;
        var outA = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        outA.Completed += (_, _) =>
        {
            tb.Text = text;
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        tb.BeginAnimation(OpacityProperty, outA);
    }

    void SetSvc(Ellipse dot, TextBlock text, Svc state, string label)
    {
        var to = state switch
        {
            Svc.Ok => CGreen,
            Svc.Blocked => CRed,
            Svc.Checking => CMuted,
            _ => CFaint,
        };
        var from = (dot.Fill as SolidColorBrush)?.Color ?? CFaint;
        var brush = new SolidColorBrush(from);
        dot.Fill = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(350)));

        dot.BeginAnimation(OpacityProperty, state == Svc.Checking
            ? new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);

        text.Text = label;
        text.Foreground = new SolidColorBrush(state switch
        {
            Svc.Ok => CGreen,
            Svc.Blocked => CRed,
            _ => CMuted,
        });
    }

    void ShowProgress(double fraction)
    {
        if (ProgressHost.Opacity < 1)
        {
            ProgressHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            var w = Math.Max(ProgressHost.ActualWidth, 200);
            ShineT.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-60, w, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        if (fraction >= 0)
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(ProgressHost.ActualWidth * fraction, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void HideProgress()
    {
        ProgressHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        ShineT.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressFill.BeginAnimation(WidthProperty, null);
        ProgressFill.Width = 0;
    }

    void AnimateBackground()
    {
        Drift(Blob1T, 70, 50, 17);
        Drift(Blob2T, -60, -70, 21);
    }

    static void Drift(TranslateTransform t, double dx, double dy, double sec)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(sec))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(sec * 1.3))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
    }

    /// <summary>Фон окна мягко перекрашивается под состояние.</summary>
    void SetMood(PowerState st)
    {
        var (a, b) = st switch
        {
            PowerState.On => (Color.FromRgb(0x0F, 0x3A, 0x25), Color.FromRgb(0x0B, 0x2A, 0x2C)),
            PowerState.Connecting => (Color.FromRgb(0x0E, 0x2E, 0x36), Color.FromRgb(0x10, 0x26, 0x38)),
            PowerState.Error => (Color.FromRgb(0x3A, 0x14, 0x18), Color.FromRgb(0x26, 0x13, 0x1B)),
            _ => (Color.FromRgb(0x1A, 0x21, 0x30), Color.FromRgb(0x15, 0x1A, 0x24)),
        };
        var d = TimeSpan.FromSeconds(1.2);
        Blob1Stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(a, d));
        Blob2Stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(b, d));
    }

    void UpdateClock()
    {
        if (PowerBtn.State != PowerState.On || _since is not { } s)
        {
            StatTime.Text = "—";
            StatTimeSub.Text = PowerBtn.State == PowerState.Connecting ? "подбор…" : "выключено";
            return;
        }
        var e = DateTime.Now - s;
        StatTime.Text = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
        StatTimeSub.Text = $"с {s:HH:mm}";
        StatusSub.Text = _partial
            ? "Часть сервисов не открывается — попробуй подобрать заново"
            : "Всё открывается — можно пользоваться";
    }

    void Toast(string text, bool error = false)
    {
        ToastText.Text = text;
        ToastDot.Fill = new SolidColorBrush(error ? CRed : CGreen);
        var d = TimeSpan.FromMilliseconds(260);
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void HideToast()
    {
        var d = TimeSpan.FromMilliseconds(220);
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, d));
    }

    void SetBusy(bool busy)
    {
        ReselectBtn.IsEnabled = !busy;
        TestAllBtn.IsEnabled = !busy;
        StrategyChips.IsEnabled = !busy;
    }

    // ═════════════════════ Котик и змейка ═════════════════════

    void InitFun()
    {
        BoxCatWidget.Petted += (_, _) =>
        {
            _s.CatPets++;
            UpdateCatText();
            if (_s.CatPets % 10 == 0) _s.Save();
            if (_s.CatPets is 1 or 50 or 100 or 500 or 1000) Toast(_s.CatPets == 1 ? "Мрр… котик доволен" : $"Котика погладили {_s.CatPets} раз — он счастлив");
        };
        UpdateCatText();

        SnakePlay.ScoreChanged += _ => UpdateSnakeSub();
        SnakePlay.GameOver += OnSnakeOver;
        UpdateSnakeBest();

        SnakeWidgetBtn.MouseEnter += (_, _) => SnakePlayHint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        SnakeWidgetBtn.MouseLeave += (_, _) => SnakePlayHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
    }

    void UpdateCatText() =>
        CatPetsText.Text = _s.CatPets == 0 ? "Погладь котика" : $"Погладили {_s.CatPets} {Plural(_s.CatPets, "раз", "раза", "раз")}";

    void UpdateSnakeBest() =>
        SnakeBestText.Text = _s.SnakeBest > 0 ? $"Змейка · рекорд {_s.SnakeBest}" : "Змейка · нажми, чтобы играть";

    void UpdateSnakeSub()
    {
        if (_sheet == SheetKind.Snake)
            SheetSub.Text = $"Счёт {SnakePlay.Score} · рекорд {_s.SnakeBest}";
    }

    void OnOpenSnake(object sender, RoutedEventArgs e) => OpenSheet(SheetKind.Snake);

    bool HandleSnakeKey(Key key)
    {
        (int dx, int dy)? dir = key switch
        {
            Key.Up or Key.W => (0, -1),
            Key.Down or Key.S => (0, 1),
            Key.Left or Key.A => (-1, 0),
            Key.Right or Key.D => (1, 0),
            _ => null,
        };
        if (dir is { } d)
        {
            if (SnakePlay.IsDead) return true;
            if (SnakePlay.IsWaiting) HideSnakeOverlay();
            SnakePlay.Turn(d.dx, d.dy);
            return true;
        }
        if (key is Key.Space or Key.Enter && SnakePlay.IsDead)
        {
            RestartSnake();
            return true;
        }
        return false;
    }

    void OnSnakeOver(int score)
    {
        var record = score > _s.SnakeBest;
        if (record)
        {
            _s.SnakeBest = score;
            _s.Save();
            UpdateSnakeBest();
        }
        UpdateSnakeSub();
        ShowSnakeOverlay(record && score > 0 ? "Новый рекорд!" : "Игра окончена",
            $"Счёт {score} · рекорд {_s.SnakeBest}", again: true);
        if (record && score > 0) Toast($"Новый рекорд в змейке: {score}");
    }

    void OnSnakeAgain(object sender, RoutedEventArgs e) => RestartSnake();

    void RestartSnake()
    {
        SnakePlay.Reset();
        ShowSnakeOverlay("Поехали", "Нажми любую стрелку", again: false);
        UpdateSnakeSub();
    }

    void ShowSnakeOverlay(string title, string sub, bool again)
    {
        SnakeOverlayTitle.Text = title;
        SnakeOverlaySub.Text = sub;
        SnakeAgain.Visibility = again ? Visibility.Visible : Visibility.Collapsed;
        SnakeOverlay.Visibility = Visibility.Visible;
        SnakeOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    void HideSnakeOverlay()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => { if (!SnakePlay.IsDead) SnakeOverlay.Visibility = Visibility.Collapsed; };
        SnakeOverlay.BeginAnimation(OpacityProperty, a);
    }

    // ═════════════════════ Панели поверх окна ═════════════════════

    void OnOpenStrategies(object sender, RoutedEventArgs e) => OpenSheet(SheetKind.Strategies);
    void OnOpenCatalog(object sender, RoutedEventArgs e) => OpenSheet(SheetKind.Catalog);
    void OnOpenDiag(object sender, RoutedEventArgs e) => OpenSheet(SheetKind.Diagnostics);
    void OnOpenLog(object sender, RoutedEventArgs e) => OpenSheet(SheetKind.Log);
    void OnSheetBack(object sender, RoutedEventArgs e) => CloseSheet();

    void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (_sheet == SheetKind.Snake && HandleSnakeKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _sheet != SheetKind.None)
        {
            CloseSheet();
            e.Handled = true;
        }
    }

    void OpenSheet(SheetKind kind)
    {
        _sheet = kind;
        SheetCatalog.Visibility = kind == SheetKind.Catalog ? Visibility.Visible : Visibility.Collapsed;
        SheetStrategies.Visibility = kind == SheetKind.Strategies ? Visibility.Visible : Visibility.Collapsed;
        SheetDiag.Visibility = kind == SheetKind.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        SheetLog.Visibility = kind == SheetKind.Log ? Visibility.Visible : Visibility.Collapsed;
        SheetSnake.Visibility = kind == SheetKind.Snake ? Visibility.Visible : Visibility.Collapsed;
        SheetAction.Visibility = Visibility.Collapsed;
        SheetAction.IsEnabled = true;

        switch (kind)
        {
            case SheetKind.Catalog:
                SheetTitle.Text = "Каталог сервисов";
                SheetSub.Text = "Нажми, чтобы добавить или убрать из обхода";
                RefreshCatalog();
                break;
            case SheetKind.Strategies:
                SheetTitle.Text = "Стратегии";
                SheetSub.Text = _s.ManualStrategy ? "Ручной режим: выбери стратегию" : "Сейчас подбираются автоматически";
                RefreshStrategyChips();
                break;
            case SheetKind.Diagnostics:
                SheetTitle.Text = "Диагностика";
                SheetSub.Text = "Ищем, что может мешать обходу";
                SheetAction.Visibility = Visibility.Visible;
                _ = RunDiagnosticsAsync();
                break;
            case SheetKind.Snake:
                SheetTitle.Text = "Змейка";
                SnakePlay.Reset();
                ShowSnakeOverlay("Змейка", "Нажми любую стрелку, чтобы начать", again: false);
                UpdateSnakeSub();
                break;
            case SheetKind.Log:
                SheetTitle.Text = "Журнал";
                SheetSub.Text = Paths.LogFile;
                SheetAction.Content = "Копировать";
                SheetAction.Visibility = Visibility.Visible;
                break;
        }

        Sheet.Visibility = Visibility.Visible;
        var d = TimeSpan.FromMilliseconds(320);
        Sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        SheetT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, d)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var content = kind switch
        {
            SheetKind.Catalog => SheetCatalog,
            SheetKind.Strategies => SheetStrategies,
            SheetKind.Diagnostics => SheetDiag,
            SheetKind.Snake => SheetSnake,
            _ => (FrameworkElement)SheetLog,
        };
        Stagger(content, 60);
        if (kind == SheetKind.Log) LogBox.ScrollToEnd();
    }

    void CloseSheet()
    {
        if (_sheet == SheetKind.None) return;
        _sheet = SheetKind.None;
        var d = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) =>
        {
            if (_sheet == SheetKind.None) Sheet.Visibility = Visibility.Collapsed;
        };
        Sheet.BeginAnimation(OpacityProperty, fade);
        SheetT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, d)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        if (_tab == 2) RefreshLists();
        if (_tab == 1) RefreshStrategyInfo();
    }

    void OnSheetAction(object sender, RoutedEventArgs e)
    {
        if (_sheet == SheetKind.Diagnostics) _ = RunDiagnosticsAsync();
        else if (_sheet == SheetKind.Log) CopyLog();
    }

    /// <summary>Строки появляются по очереди, с лёгким подъёмом.</summary>
    static void Stagger(FrameworkElement root, int startDelayMs = 0)
    {
        var panel = root switch
        {
            ScrollViewer { Content: Panel p } => p,
            Panel p => p,
            _ => null,
        };
        if (panel == null) return;

        var i = 0;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            var t = new TranslateTransform(0, 12);
            child.RenderTransform = t;
            var delay = TimeSpan.FromMilliseconds(startDelayMs + 40 * i++);
            child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = delay });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(360))
            { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    // ═════════════════════ Вкладки ═════════════════════

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var i)) SelectTab(i);
    }

    void SelectTab(int i)
    {
        CloseSheet();
        if (i == _tab) return;
        var pages = new FrameworkElement[] { PageHome, PageBypass, PageLists, PageSettings };
        var dir = i > _tab ? 1 : -1;
        pages[_tab].Visibility = Visibility.Collapsed;
        _tab = i;

        var page = pages[i];
        page.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(28 * dir, 0);
        page.RenderTransform = tt;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * dir, 0, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        MoveIndicator(true);
        if (i == 1) RefreshStrategyInfo();
        if (i == 2) RefreshLists();
        if (i != 0) Stagger(page, 40);
    }

    void MoveIndicator(bool animate)
    {
        var w = TabsGrid.ActualWidth / 4;
        if (w <= 0) return;
        TabIndicator.Width = w;
        var x = w * _tab;
        if (animate)
            TabIndicatorT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
        else
        {
            TabIndicatorT.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorT.X = x;
        }

        var tabs = new[] { Tab0, Tab1, Tab2, Tab3 };
        for (var k = 0; k < tabs.Length; k++)
        {
            if (k == _tab) tabs[k].Foreground = (Brush)FindResource("Text");
            else tabs[k].ClearValue(ForegroundProperty);
        }
    }

    // ═════════════════════ Вкладка «Обход» ═════════════════════

    void InitControls()
    {
        (_s.ManualStrategy ? ModeManual : ModeAuto).IsChecked = true;

        (_s.GameFilter switch
        {
            GameFilterMode.Tcp => GfTcp,
            GameFilterMode.Udp => GfUdp,
            GameFilterMode.All => GfAll,
            _ => GfOff,
        }).IsChecked = true;

        (_s.Ipset switch
        {
            IpsetMode.Any => IpAny,
            IpsetMode.None => IpNone,
            _ => IpLoaded,
        }).IsChecked = true;
        UpdateIpsetHint();

        LkBypass.IsChecked = true;

        SwAutostart.IsChecked = SystemTools.IsAutostartEnabled();
        SwEnableOnLaunch.IsChecked = _s.EnableOnLaunch;
        SwTray.IsChecked = _s.MinimizeToTray;
        SwUpdates.IsChecked = _s.CheckZapretUpdates;
        SwTgDialog.IsChecked = _s.ShowTelegramProxyDialog;
        TgAddrText.Text = $"SOCKS5 · 127.0.0.1:{_s.TelegramPort}";

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text = $"Unchain {ver?.ToString(3)} · open source · MIT";
        SumApp.Text = $"Unchain {ver?.ToString(3)}";
        SumAppSub.Text = "Открытый код · MIT · zapret от Flowseal";
        StatPingHost.SizeChanged += (_, _) => DrawSpark();

        RefreshStrategyInfo();
        RefreshLists();
    }

    ZapretBuild? UiBuild() =>
        _zapret.CurrentBuild
        ?? (_s.ZapretVersion is { } v ? ZapretStore.TryLoadLocal(v) : null)
        ?? ZapretStore.ListLocal().Select(r => ZapretStore.TryLoadLocal(r.Tag)).FirstOrDefault(b => b != null);

    void RefreshStrategyInfo()
    {
        var has = _s is { ZapretVersion: not null, ZapretStrategy: not null };
        var name = has ? ZapretStore.StrategyName(_s.ZapretStrategy!) : null;
        StrategyCurrent.Text = has ? $"Сейчас: {name} · zapret {_s.ZapretVersion}" : "Ещё не подобрана";
        ZapretVersionText.Text = $"{_s.ZapretVersion ?? "не скачана"} · скачано версий: {ZapretStore.ListLocal().Count}";

        StatStrategy.Text = name ?? "—";
        StatStrategySub.Text = _s.ManualStrategy ? "вручную" : "авто";
        SumStrategy.Text = has ? $"Стратегия {name}" : "Стратегия не выбрана";
        SumStrategySub.Text = has
            ? $"zapret {_s.ZapretVersion} · {(_s.ManualStrategy ? "выбрана вручную" : "подобрана по пингу")}"
            : "Подберётся при первом включении";
        SumPing.Text = _pingMs is { } pm ? $"{pm} мс" : "— мс";

        HomeHint.Text = PowerBtn.State == PowerState.On && has
            ? $"{(_s.ManualStrategy ? "Выбрано вручную" : "Подобрано автоматически")} · {name}"
            : has ? $"Последний рабочий способ: {name}" : "Способ обхода подберётся автоматически";
    }

    void RefreshStrategyChips()
    {
        StrategyChips.Children.Clear();
        var build = UiBuild();
        if (build == null)
        {
            ManualHint.Text = "Стратегии появятся после первого включения";
            return;
        }
        ManualHint.Text = $"zapret {build.Tag} · нажми на стратегию, чтобы включить её";

        var i = 0;
        foreach (var file in build.Strategies)
        {
            var fileName = Path.GetFileName(file);
            var selected = _s.ZapretVersion == build.Tag && string.Equals(_s.ZapretStrategy, fileName, StringComparison.OrdinalIgnoreCase);
            _tests.TryGetValue(file, out var test);

            var label = ZapretStore.StrategyName(file);
            Color? tint = null;
            if (test?.Result is { } r)
            {
                label += $" · {r.Passed}/{r.Total}";
                if (r.AvgMs is { } ms) label += $" · {ms} мс";
                tint = r.AllOk ? CGreen : r.Passed > 0 ? CAmber : CRed;
            }
            else if (test?.Error != null)
            {
                label += " · ошибка";
                tint = CRed;
            }

            var chip = new Button { Style = (Style)FindResource("Chip"), Content = label, Tag = file, Opacity = 0 };
            if (selected)
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x2E, 0xD1, 0x73));
                chip.BorderBrush = new SolidColorBrush(CGreen);
                chip.Foreground = new SolidColorBrush(CGreen);
            }
            else if (tint is { } t)
            {
                chip.Foreground = new SolidColorBrush(t);
            }
            if (test?.Result is { } tr) chip.ToolTip = tr.ToString();
            chip.Click += OnStrategyChip;
            StrategyChips.Children.Add(chip);
            chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(18 * i++) });
        }
    }

    void OnStrategyChip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string file } || _op != null) return;
        var build = UiBuild();
        if (build == null) return;

        _s.ZapretVersion = build.Tag;
        _s.ZapretStrategy = Path.GetFileName(file);
        _s.Save();

        if (PowerBtn.State == PowerState.On)
        {
            try
            {
                _zapret.Start(build, file);
                Toast($"Включена стратегия {ZapretStore.StrategyName(file)}");
                _ = RecheckSoonAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Не удалось запустить: " + ex.Message);
                Toast("Не удалось запустить стратегию", true);
            }
        }
        else Toast($"Выбрана {ZapretStore.StrategyName(file)} — включится вместе с обходом");

        RefreshStrategyChips();
        RefreshStrategyInfo();
    }

    async Task RecheckSoonAsync()
    {
        await Task.Delay(1500);
        await RecheckAsync();
    }

    void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.ManualStrategy = ModeManual.IsChecked == true;
        _s.Save();
        RefreshStrategyInfo();
        if (_s.ManualStrategy) OpenSheet(SheetKind.Strategies);
        else Toast("Стратегия снова подбирается автоматически");
    }

    async void OnTestAll(object sender, RoutedEventArgs e)
    {
        if (_op != null) return;
        var cts = new CancellationTokenSource();
        _op = cts;
        var wasOn = PowerBtn.State == PowerState.On;
        SetBusy(true);
        TestResultsBlock.Visibility = Visibility.Collapsed;
        TestAllBtn.Content = "Идёт проверка…";
        var progress = new Progress<SelectProgress>(p =>
            TestAllHint.Text = p.Total > 0 ? $"Проверено {p.Done} из {p.Total} · {p.Detail}" : p.Detail);

        try
        {
            var build = UiBuild();
            if (build == null)
            {
                TestAllHint.Text = "Скачиваю zapret…";
                var rel = (await AutoSelector.GetReleasesAsync(cts.Token)).FirstOrDefault()
                          ?? throw new InvalidOperationException("Нет доступных версий zapret");
                build = await ZapretStore.EnsureAsync(rel, new Progress<string>(s => TestAllHint.Text = s), cts.Token);
            }

            _zapret.GameFilter = _s.GameFilter;
            _zapret.Ipset = _s.Ipset;
            var results = await new AutoSelector(_zapret, 1).TestAllAsync(build, Connectivity.BuildTargets(), progress, cts.Token);
            foreach (var r in results) _tests[r.Strategy] = r;

            var working = results.Count(r => r.Result?.AllOk == true);
            StrategyTest? best = null;
            foreach (var t in results.Where(x => x.Result != null))
                if (t.Result!.BetterThan(best?.Result)) best = t;
            TestAllHint.Text = $"Работают все сервисы: {working} из {results.Count}"
                + (best != null ? $" · лучшая {ZapretStore.StrategyName(best.Strategy)}{(best.Result!.AvgMs is { } bm ? $" ({bm} мс)" : "")}" : "");
            ShowTestResults(results);
            Toast(working > 0 ? $"Рабочих стратегий: {working}" : "Полностью рабочих не нашлось", working == 0);
        }
        catch (OperationCanceledException) { TestAllHint.Text = "Проверка отменена"; }
        catch (Exception ex)
        {
            Log.Write("Тест стратегий: " + ex.Message);
            TestAllHint.Text = ex.Message;
        }
        finally
        {
            _op = null;
            cts.Dispose();
            SetBusy(false);
            TestAllBtn.Content = "Начать";
            RefreshStrategyChips();

            // Возвращаем рабочую стратегию, если обход был включён
            if (wasOn && _s is { ZapretVersion: { } v, ZapretStrategy: { } st }
                && ZapretStore.TryLoadLocal(v) is { } b
                && b.Strategies.FirstOrDefault(x => Path.GetFileName(x).Equals(st, StringComparison.OrdinalIgnoreCase)) is { } f)
            {
                try { _zapret.Start(b, f); } catch (Exception ex) { Log.Write("Не удалось вернуть стратегию: " + ex.Message); }
            }
        }
    }

    void ShowTestResults(List<StrategyTest> results)
    {
        TestResults.Children.Clear();
        var i = 0;
        foreach (var r in results.OrderByDescending(x => x.Result?.Score ?? -1).ThenBy(x => x.Result?.AvgMs ?? int.MaxValue))
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4), Opacity = 0 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ok = r.Result?.AllOk == true;
            var part = r.Result is { Passed: > 0 } && !ok;
            var color = ok ? CGreen : part ? CAmber : CRed;

            var name = new TextBlock { Text = ZapretStore.StrategyName(r.Strategy), FontSize = 12.5, Foreground = (Brush)FindResource("Text2") };
            var detail = r.Result is { } res
                ? $"YouTube {Mark(res.ServiceOk(Connectivity.YouTube))}   Discord {Mark(res.ServiceOk(Connectivity.Discord))}   {res.Passed}/{res.Total}{(res.AvgMs is { } p ? $" · {p} мс" : "")}"
                : r.Error ?? "ошибка";
            var right = new TextBlock { Text = detail, FontSize = 12, Foreground = new SolidColorBrush(color) };
            Grid.SetColumn(right, 1);
            g.Children.Add(name);
            g.Children.Add(right);
            TestResults.Children.Add(g);
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(25 * i++) });
        }
        TestResultsBlock.Visibility = Visibility.Visible;
        TestResultsBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));

        static string Mark(bool? b) => b == true ? "✓" : "✗";
    }

    void OnGameFilter(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.GameFilter = GfTcp.IsChecked == true ? GameFilterMode.Tcp
            : GfUdp.IsChecked == true ? GameFilterMode.Udp
            : GfAll.IsChecked == true ? GameFilterMode.All
            : GameFilterMode.Off;
        _s.Save();
        _zapret.GameFilter = _s.GameFilter;
        ScheduleRestart();
    }

    void OnIpset(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Ipset = IpAny.IsChecked == true ? IpsetMode.Any
            : IpNone.IsChecked == true ? IpsetMode.None
            : IpsetMode.Loaded;
        _s.Save();
        _zapret.Ipset = _s.Ipset;
        UpdateIpsetHint();
        ScheduleRestart();
    }

    void UpdateIpsetHint() => IpsetHint.Text = _s.Ipset switch
    {
        IpsetMode.Any => "Под фильтр попадает любой IP — максимум охвата",
        IpsetMode.None => "Обход по IP выключен, только по доменам",
        _ => "Обход по списку IP-адресов (ipset-all.txt)",
    };

    async void OnIpsetUpdate(object sender, RoutedEventArgs e)
    {
        IpsetUpdateBtn.IsEnabled = false;
        IpsetUpdateBtn.Content = "Загрузка…";
        try
        {
            var n = await ZapretStore.UpdateIpsetAsync(CancellationToken.None);
            Toast($"IPSet обновлён · {n} записей");
            ScheduleRestart();
        }
        catch (Exception ex)
        {
            Log.Write("Обновление IPSet: " + ex.Message);
            Toast("Не удалось обновить IPSet", true);
        }
        finally
        {
            IpsetUpdateBtn.IsEnabled = true;
            IpsetUpdateBtn.Content = "Обновить";
        }
    }

    async Task RunDiagnosticsAsync()
    {
        SheetAction.IsEnabled = false;
        SheetAction.Content = "Проверка…";
        DiagResults.Children.Clear();
        DiagResults.Children.Add(new TextBlock { Text = "Проверяем…", FontSize = 12.5, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 10) });
        try
        {
            var items = await SystemTools.DiagnoseAsync(_zapret.IsRunning, CancellationToken.None);
            DiagResults.Children.Clear();
            var i = 0;
            foreach (var d in items)
            {
                if (i > 0) DiagResults.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Divider") });
                var g = new Grid { Margin = new Thickness(0, 10, 0, 10), Opacity = 0 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top, Fill = new SolidColorBrush(d.Ok ? CGreen : CRed) };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = d.Name, Style = (Style)FindResource("RowTitle") });
                sp.Children.Add(new TextBlock { Text = d.Detail, Style = (Style)FindResource("RowHint") });
                Grid.SetColumn(sp, 1);
                g.Children.Add(dot);
                g.Children.Add(sp);
                DiagResults.Children.Add(g);
                g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(50 * i++) });
            }
            var bad = items.Count(x => !x.Ok);
            SheetSub.Text = bad == 0 ? "Проблем не найдено" : $"Найдено проблем: {bad}";
        }
        catch (Exception ex)
        {
            Log.Write("Диагностика: " + ex.Message);
            SheetSub.Text = "Не удалось завершить проверку";
        }
        finally
        {
            SheetAction.IsEnabled = true;
            SheetAction.Content = "Ещё раз";
        }
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Paths.Root } })?.Dispose(); } catch { }
    }

    void ScheduleRestart()
    {
        if (PowerBtn.State != PowerState.On || _op != null) return;
        _restartDebounce.Stop();
        _restartDebounce.Start();
    }

    void RestartIfRunning()
    {
        if (PowerBtn.State != PowerState.On || _op != null) return;
        try
        {
            if (_zapret.Restart())
            {
                Toast("Обход перезапущен с новыми настройками");
                _ = RecheckSoonAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Перезапуск: " + ex.Message);
            Toast("Не удалось перезапустить обход", true);
        }
    }

    // ═════════════════════ Вкладка «Списки» ═════════════════════

    void OnListKind(object sender, RoutedEventArgs e)
    {
        _listKind = LkExclude.IsChecked == true ? ListKind.Exclude
            : LkIp.IsChecked == true ? ListKind.ExcludeIp
            : ListKind.Bypass;
        AddError.Visibility = Visibility.Collapsed;
        if (!_loading) RefreshLists();
    }

    void RefreshLists()
    {
        ListKindHint.Text = _listKind switch
        {
            ListKind.Bypass => "Сайты, к которым применяется обход. Поддомены подхватываются сами.",
            ListKind.Exclude => "Сайты, которые обход не трогает — если что-то сломалось после включения.",
            _ => "IP-адреса и подсети, которые обход не трогает (например, 10.0.0.0/8).",
        };
        AddPlaceholder.Text = _listKind == ListKind.ExcludeIp ? "1.2.3.4 или 10.0.0.0/8" : "example.com";
        AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogRow.Visibility = _listKind == ListKind.Bypass ? Visibility.Visible : Visibility.Collapsed;
        var addedCount = Catalog.Entries.Count(Catalog.IsAdded);
        CatalogCount.Text = addedCount > 0
            ? $"Добавлено {addedCount} из {Catalog.Entries.Length} · соцсети, мессенджеры, игры"
            : "Соцсети, мессенджеры, игры — в один клик";
        if (Sheet.Visibility == Visibility.Visible && _sheet == SheetKind.Catalog) RefreshCatalog();

        var items = UserLists.Read(_listKind);
        MyListTitle.Text = (_listKind switch
        {
            ListKind.Bypass => "МОИ САЙТЫ",
            ListKind.Exclude => "ИСКЛЮЧЕНИЯ",
            _ => "IP-АДРЕСА",
        }) + $" · {items.Count}";

        MyList.Children.Clear();
        if (items.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 16, 0, 16) };
            empty.Children.Add(new CatBall { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            empty.Children.Add(new TextBlock
            {
                Text = "Пока пусто — котик ждёт твои сайты",
                FontSize = 12.5,
                Foreground = (Brush)FindResource("Faint"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            MyList.Children.Add(empty);
        }

        var i = 0;
        foreach (var item in items)
        {
            if (i > 0) MyList.Children.Add(new Border { Style = (Style)FindResource("RowDivider") });
            var g = new Grid { Margin = new Thickness(16, 6, 8, 6), Opacity = 0 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = item,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("Text2"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var del = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = new Glyph { Icon = (Geometry)FindResource("IcClose"), Width = 12, Height = 12, Thickness = 2.2 },
                Tag = item,
                ToolTip = "Убрать",
            };
            del.Click += OnRemoveItem;
            Grid.SetColumn(del, 1);
            g.Children.Add(del);
            MyList.Children.Add(g);
            g.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(Math.Min(20 * i, 400)) });
            i++;
        }

        var b = UiBuild();
        var mine = UserLists.Read(ListKind.Bypass).Count;
        var services = Catalog.Entries.Count(Catalog.IsAdded);
        var builtinCount = b != null ? ZapretStore.BuiltinDomainCount(b) : 0;
        SumLists.Text = $"В обходе {mine} {Plural(mine, "сайт", "сайта", "сайтов")}";
        SumListsSub.Text = $"{services} {Plural(services, "сервис", "сервиса", "сервисов")} из каталога"
            + (builtinCount > 0 ? $" · ещё {builtinCount} встроенных" : "");
        SumListsBadge.Text = $"+{mine}";

        BuiltinText.Text = b != null && _listKind == ListKind.Bypass
            ? $"Плюс встроенный список zapret: {ZapretStore.BuiltinDomainCount(b)} доменов (YouTube, Discord и другие)"
            : "";
    }

    /// <summary>Содержимое чипа: своя иконка + текст.</summary>
    object ChipContent(string icon, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Glyph { Icon = (Geometry)FindResource(icon), Width = 12, Height = 12, Thickness = 2.4, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text });
        return sp;
    }

    void RefreshCatalog()
    {
        CatalogChips.Children.Clear();
        var first = true;
        foreach (var cat in Catalog.Categories)
        {
            var builtin = Catalog.Builtin.Where(x => x.Category == cat).ToList();
            var entries = Catalog.Entries.Where(x => x.Category == cat).ToList();
            if (builtin.Count == 0 && entries.Count == 0) continue;

            var icon = cat switch
            {
                "Общение" => "IcChat",
                "Соцсети" => "IcUsers",
                "Игры" => "IcGamepad",
                "Видео и музыка" => "IcPlay",
                _ => "IcDots",
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, first ? 2 : 18, 0, 8) };
            header.Children.Add(new Glyph { Icon = (Geometry)FindResource(icon), Width = 13, Height = 13, Foreground = (Brush)FindResource("Faint"), Margin = new Thickness(0, 0, 7, 0) });
            header.Children.Add(new TextBlock { Text = cat.ToUpperInvariant(), Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0) });
            CatalogChips.Children.Add(header);
            first = false;

            var wrap = new WrapPanel();
            foreach (var (name, _) in builtin)
            {
                wrap.Children.Add(new Button
                {
                    Style = (Style)FindResource("Chip"),
                    Content = ChipContent("IcCheck", $"{name} · встроено"),
                    IsEnabled = false,
                    Foreground = new SolidColorBrush(CGreen),
                    Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x2E, 0xD1, 0x73)),
                });
            }
            foreach (var entry in entries)
            {
                var added = Catalog.IsAdded(entry);
                var chip = new Button
                {
                    Style = (Style)FindResource("Chip"),
                    Content = ChipContent(added ? "IcCheck" : "IcPlus", entry.Name),
                    Tag = entry,
                    ToolTip = string.Join(", ", entry.Domains),
                };
                if (added)
                {
                    chip.Foreground = new SolidColorBrush(CGreen);
                    chip.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x2E, 0xD1, 0x73));
                }
                chip.Click += OnCatalogChip;
                wrap.Children.Add(chip);
            }
            CatalogChips.Children.Add(new Border { Style = (Style)FindResource("CardBox"), Padding = new Thickness(14, 14, 8, 8), Child = wrap });
        }
    }

    void OnCatalogChip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogEntry entry }) return;
        try
        {
            if (Catalog.IsAdded(entry))
            {
                // Общие домены (например, cdninstagram.com у Instagram и Threads) не трогаем,
                // если они ещё нужны другому добавленному сервису
                var stillNeeded = Catalog.Entries
                    .Where(x => x != entry && Catalog.IsAdded(x))
                    .SelectMany(x => x.Domains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                UserLists.Remove(ListKind.Bypass, entry.Domains.Where(d => !stillNeeded.Contains(d)));
                Toast($"{entry.Name} убран из обхода");
            }
            else
            {
                UserLists.Add(ListKind.Bypass, entry.Domains);
                Toast($"{entry.Name} добавлен в обход");
            }
        }
        catch (Exception ex)
        {
            Log.Write("Списки: " + ex.Message);
            Toast("Не удалось сохранить список", true);
        }
        RefreshLists();
    }

    void OnAdd(object sender, RoutedEventArgs e) => AddFromBox();

    void OnAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddFromBox(); e.Handled = true; }
    }

    void OnAddTextChanged(object sender, TextChangedEventArgs e)
    {
        AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddError.Visibility = Visibility.Collapsed;
    }

    void AddFromBox()
    {
        var parts = AddBox.Text.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var good = new List<string>();
        var bad = new List<string>();
        foreach (var p in parts)
        {
            if (_listKind == ListKind.ExcludeIp)
            {
                if (UserLists.TryNormalizeIp(p, out var ip)) good.Add(ip); else bad.Add(p);
            }
            else
            {
                if (UserLists.TryNormalizeDomain(p, out var d)) good.Add(d); else bad.Add(p);
            }
        }

        if (good.Count > 0)
        {
            try
            {
                var n = UserLists.Add(_listKind, good);
                Toast(n > 0 ? $"Добавлено: {n}" : "Уже есть в списке");
            }
            catch (Exception ex)
            {
                Log.Write("Списки: " + ex.Message);
                Toast("Не удалось сохранить список", true);
            }
        }

        if (bad.Count > 0)
        {
            AddBox.Text = string.Join(" ", bad);
            AddError.Text = (_listKind == ListKind.ExcludeIp ? "Не похоже на IP: " : "Не похоже на адрес сайта: ") + string.Join(", ", bad);
            AddError.Visibility = Visibility.Visible;
        }
        else AddBox.Clear();

        RefreshLists();
    }

    void OnRemoveItem(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string item, Parent: Grid row }) return;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        anim.Completed += (_, _) =>
        {
            try { UserLists.Remove(_listKind, new[] { item }); }
            catch (Exception ex) { Log.Write("Списки: " + ex.Message); }
            RefreshLists();
        };
        row.BeginAnimation(OpacityProperty, anim);
    }

    // ═════════════════════ Настройки ═════════════════════

    void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleButton t) return;
        var on = t.IsChecked == true;

        if (t == SwAutostart)
        {
            if (!SystemTools.SetAutostart(on))
            {
                t.IsChecked = !on;
                Toast("Не удалось изменить автозапуск", true);
                return;
            }
            Toast(on ? "Unchain будет запускаться с Windows" : "Автозапуск выключен");
        }
        else if (t == SwEnableOnLaunch) _s.EnableOnLaunch = on;
        else if (t == SwTray) _s.MinimizeToTray = on;
        else if (t == SwUpdates) _s.CheckZapretUpdates = on;
        else if (t == SwTgDialog) _s.ShowTelegramProxyDialog = on;

        _s.Save();
    }

    void CopyLog()
    {
        try
        {
            Clipboard.SetText(File.Exists(Paths.LogFile) ? File.ReadAllText(Paths.LogFile) : LogBox.Text);
            Toast("Журнал скопирован");
        }
        catch { Toast("Не удалось скопировать", true); }
    }

    void AppendLog(string line)
    {
        if (++_logLines > 800)
        {
            LogBox.Clear();
            _logLines = 0;
        }
        LogBox.AppendText(line + Environment.NewLine);
        if (_sheet == SheetKind.Log && Sheet.Visibility == Visibility.Visible) LogBox.ScrollToEnd();
    }

    static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    // ═════════════════════ Окно и трей ═════════════════════

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon { Text = "Unchain — выключено", Visible = true };
        try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Paths.ExePath); }
        catch { _tray.Icon = System.Drawing.SystemIcons.Shield; }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowFromTray());
        _trayToggle = menu.Items.Add("Включить", null, (_, _) => OnPowerClick(null, EventArgs.Empty));
        menu.Items.Add("Добавить прокси в Telegram", null, (_, _) => OpenTgLink());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowFromTray(); };
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!_started) _ = StartupAsync();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _s.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2500, "Unchain работает в трее", "Обход продолжает работать. Выход — правой кнопкой по значку.", WinForms.ToolTipIcon.None);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _op?.Cancel();
        _zapret.Stop();
        _tg?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11: тёмная рамка и скруглённые углы окна.</summary>
    void ApplyDwm()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            var round = 2;
            DwmSetWindowAttribute(h, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
            var border = 0x002C2522;                               // #22252C в формате COLORREF (BGR)
            DwmSetWindowAttribute(h, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        }
        catch { }
    }
}

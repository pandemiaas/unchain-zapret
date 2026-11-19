using System.Windows;
using System.Windows.Threading;
using Unchain.Controls;
using Unchain.Core;

namespace Unchain;

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "Unchain.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Unchain уже запущен — значок в трее рядом с часами.", "Unchain",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.TrimFile();
        Log.Write("──── запуск Unchain ────");

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write("Критическая ошибка: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Write("Фоновая ошибка: " + a.Exception.GetBaseException().Message);
            a.SetObserved();
        };

        base.OnStartup(e);
        Ripple.Register();

        var autostart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(autostart);
        MainWindow = window;
        if (autostart) window.StartHidden();
        else window.Show();
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Ошибка интерфейса: " + e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ZapretRunner.KillStray();
        _single?.Dispose();
        base.OnExit(e);
    }
}

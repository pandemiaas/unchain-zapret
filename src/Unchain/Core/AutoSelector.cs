namespace Unchain.Core;

public sealed record Selection(ZapretBuild Build, string Strategy, TestResult Result)
{
    public bool AllOk => Result.AllOk;
}

/// <summary>Ход подбора для полоски прогресса. Done/Total — по стратегиям текущей версии.</summary>
public sealed record SelectProgress(string Title, string Detail, int Done, int Total, TestResult? LastResult = null);

public sealed record StrategyTest(string Strategy, TestResult? Result, string? Error);

/// <summary>
/// Подбор стратегии с учётом пинга и выбранных пользователем сервисов.
/// Для каждой версии zapret (новые → старые) проверяются ВСЕ стратегии;
/// среди тех, где работает всё, берётся самая быстрая. Если такой нет —
/// идём в версию постарше, а в конце берём лучшую по очкам, затем по пингу.
/// </summary>
public sealed class AutoSelector
{
    readonly ZapretRunner _runner;
    readonly int _maxVersions;

    public AutoSelector(ZapretRunner runner, int maxVersions)
    {
        _runner = runner;
        _maxVersions = Math.Max(1, maxVersions);
    }

    public static async Task<List<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        try
        {
            return await ZapretStore.ListRemoteAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write("GitHub недоступен (" + e.Message + "), беру скачанные версии");
            return ZapretStore.ListLocal();
        }
    }

    public async Task<Selection?> RunAsync(IReadOnlyList<Target> targets, IProgress<SelectProgress> progress, CancellationToken ct)
    {
        _runner.Stop();
        ZapretRunner.KillStray();

        Log.Write("Проверяем: " + string.Join(", ", targets.Select(t => t.Service).Distinct()));
        progress.Report(new("Проверяем, что блокируется", "Без обхода", 0, 0));
        var baseline = await Connectivity.RunAsync(targets, ct);
        Log.Write("Без обхода: " + baseline);

        progress.Report(new("Ищем лучший способ", "Получаю список версий…", 0, 0, baseline));
        var releases = await GetReleasesAsync(ct);
        if (releases.Count == 0)
            throw new InvalidOperationException("Нет ни одной версии zapret: GitHub недоступен и ничего не скачано");

        Selection? best = null;
        var dlProgress = new Progress<string>(s => progress.Report(new("Ищем лучший способ", s, 0, 0)));

        foreach (var rel in releases.Take(_maxVersions))
        {
            ZapretBuild build;
            try { build = await ZapretStore.EnsureAsync(rel, dlProgress, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Write($"Версия {rel.Tag} пропущена: {e.Message}");
                continue;
            }

            Selection? bestHere = null;
            for (var i = 0; i < build.Strategies.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = build.Strategies[i];
                var name = ZapretStore.StrategyName(s);
                var lead = bestHere is { } bh
                    ? $" · лидер {ZapretStore.StrategyName(bh.Strategy)}{(bh.Result.AvgMs is { } ms ? $" {ms} мс" : "")}"
                    : "";
                progress.Report(new("Ищем лучший способ", $"{rel.Tag} · {name}{lead}", i, build.Strategies.Count, bestHere?.Result));

                var r = await TryStrategyAsync(build, s, targets, ct);
                if (r.Result is not { } res) continue;
                Log.Write($"{rel.Tag} · {name}: {res}");

                if (res.BetterThan(bestHere?.Result)) bestHere = new Selection(build, s, res);
            }

            if (bestHere != null && bestHere.Result.BetterThan(best?.Result)) best = bestHere;

            if (bestHere is { AllOk: true })
            {
                Log.Write($"✓ Выбрано: {rel.Tag} · {ZapretStore.StrategyName(bestHere.Strategy)} · {bestHere.Result.AvgMs} мс");
                progress.Report(new("Готово", ZapretStore.StrategyName(bestHere.Strategy), build.Strategies.Count, build.Strategies.Count, bestHere.Result));
                _runner.Start(bestHere.Build, bestHere.Strategy);
                return bestHere;
            }

            Log.Write($"В версии {rel.Tag} не нашлось стратегии, где работает всё — пробую версию постарше");
        }

        if (best == null || best.Result.Score <= baseline.Score)
        {
            _runner.Stop();
            return null;
        }

        Log.Write($"Беру лучшую из найденных: {best.Build.Tag} · {ZapretStore.StrategyName(best.Strategy)} ({best.Result.Passed}/{best.Result.Total}, {best.Result.AvgMs} мс)");
        _runner.Start(best.Build, best.Strategy);
        return best;
    }

    /// <summary>Прогон всех стратегий одной версии — для таблицы «что работает у твоего провайдера».</summary>
    public async Task<List<StrategyTest>> TestAllAsync(ZapretBuild build, IReadOnlyList<Target> targets,
        IProgress<SelectProgress> progress, CancellationToken ct)
    {
        var list = new List<StrategyTest>();
        try
        {
            for (var i = 0; i < build.Strategies.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = build.Strategies[i];
                progress.Report(new("Проверяем все стратегии", ZapretStore.StrategyName(s), i, build.Strategies.Count));
                var r = await TryStrategyAsync(build, s, targets, ct);
                list.Add(r);
                if (r.Result != null) Log.Write($"Тест {ZapretStore.StrategyName(s)}: {r.Result}");
            }
        }
        finally
        {
            _runner.Stop();
        }
        return list;
    }

    async Task<StrategyTest> TryStrategyAsync(ZapretBuild build, string strategy, IReadOnlyList<Target> targets, CancellationToken ct)
    {
        try { _runner.Start(build, strategy); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"{ZapretStore.StrategyName(strategy)}: не запустилась — {e.Message}");
            return new(strategy, null, e.Message);
        }

        await Task.Delay(1500, ct);
        if (!_runner.IsRunning)
        {
            Log.Write($"{ZapretStore.StrategyName(strategy)}: winws сразу закрылся");
            return new(strategy, null, "winws закрылся");
        }

        return new(strategy, await Connectivity.RunAsync(targets, ct), null);
    }
}

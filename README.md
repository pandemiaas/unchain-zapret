# Unchain

Открытая нативная оболочка для Windows: в один клик включает обход блокировок YouTube/Discord (zapret) и ускоряет Telegram Desktop.

- **Zapret.** Сам скачивает сборку [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube), подбирает рабочую стратегию (`general*.bat`), а если в последней версии ни одна не помогает — откатывается на предыдущие.
- **Telegram.** Встроенный локальный SOCKS5-мост (`127.0.0.1:1080`) заворачивает трафик Telegram в WebSocket к `kwsN.web.telegram.org`. Идея та же, что у [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), реализация своя, без Python и без сторонних exe.
- **Вкладки:** «Главная» (одна кнопка и статусы YouTube/Discord/Telegram), «Обход» (всё из service.bat: игровой фильтр, IPSet, обновление IPSet, диагностика, тест всех стратегий, ручной выбор), «Списки» (каталог сервисов и ручное добавление доменов/IP в `*-user.txt`), «Настройки» (автозапуск, трей, журнал).
- C# / WPF, .NET 8. Без Electron: один exe, нужен только [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

## Как работает автоподбор

1. Проверка без обхода: YouTube, googlevideo, Discord (сайт, API, gateway, CDN).
2. Список релизов zapret берётся с GitHub (новые → старые, по умолчанию 3 версии). Если GitHub недоступен, используются уже скачанные.
3. Для каждой стратегии из `.bat` вытаскиваются аргументы `winws.exe`, переменные (`%BIN%`, `%LISTS%`, `%GameFilter%`…) подставляются, и winws запускается напрямую, без окон cmd.
4. Те же сайты проверяются снова. Для YouTube и Discord дочитывается ≥64 КБ — это ловит «обрыв на 16 КБ» от ТСПУ.
5. Первая связка, при которой всё ✓, сохраняется. Если такой нет, берётся лучшая по числу ✓.
6. При следующем включении сначала проверяется сохранённая связка, а если она перестала работать — подбор запускается заново.

Данные лежат в `C:\ProgramData\Unchain` (`settings.json`, `log.txt`, `lists\` — твои списки, `zapret\<версия>`).

## Скачать

Готовый `Unchain.exe` — на странице [Releases](https://github.com/pandemiaas/unchain-zapret/releases). .NET ставить не нужно, он встроен.

## Сборка

```
dotnet publish src/Unchain/Unchain.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

GitHub Actions собирает `Unchain.exe` на каждый push, а на тег `v*` создаёт релиз.

## Важно

- Нужны права администратора (WinDivert).
- Антивирусы часто ругаются на WinDivert/winws. Если после загрузки версия «не найдена», добавь `C:\ProgramData\Unchain` в исключения.
- Если у тебя установлен сервис zapret из `service.bat`, удали его — два winws будут мешать друг другу.

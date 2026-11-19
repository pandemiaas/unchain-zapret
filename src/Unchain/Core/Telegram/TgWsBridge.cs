using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Unchain.Core.Telegram;

/// <summary>
/// Локальный SOCKS5-прокси для Telegram Desktop.
/// Соединения к серверам Telegram заворачиваются в WebSocket (wss://kwsN.web.telegram.org/apiws) —
/// тот же путь, что у web-версии. Остальной трафик идёт напрямую.
/// Идея — как у Flowseal/tg-ws-proxy, реализация своя и нативная.
/// </summary>
public sealed class TgWsBridge : IDisposable
{
    readonly int _port;
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    int _active;

    public TgWsBridge(int port) => _port = port;

    public int Port => _port;
    public int ActiveConnections => Volatile.Read(ref _active);
    public string SocksLink => $"tg://socks?server=127.0.0.1&port={_port}";

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(); // SocketException, если порт занят
        Log.Write($"Telegram-мост слушает 127.0.0.1:{_port}");
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var s = client.GetStream();

            // --- SOCKS5 handshake ---
            var hello = await ReadExactAsync(s, 2, ct);
            if (hello[0] != 5) return;
            await ReadExactAsync(s, hello[1], ct);
            await s.WriteAsync(new byte[] { 5, 0 }, ct);

            var req = await ReadExactAsync(s, 4, ct);
            if (req[1] != 1) { await ReplyAsync(s, 7, ct); return; } // только CONNECT

            IPAddress? ip = null;
            string? host = null;
            switch (req[3])
            {
                case 1: ip = new IPAddress(await ReadExactAsync(s, 4, ct)); break;
                case 4: ip = new IPAddress(await ReadExactAsync(s, 16, ct)); break;
                case 3:
                    var len = (await ReadExactAsync(s, 1, ct))[0];
                    host = Encoding.ASCII.GetString(await ReadExactAsync(s, len, ct));
                    break;
                default: await ReplyAsync(s, 8, ct); return;
            }
            var pb = await ReadExactAsync(s, 2, ct);
            var port = (pb[0] << 8) | pb[1];

            if (ip == null || !TelegramIps.IsTelegram(ip))
            {
                await DirectAsync(s, ip, host, port, null, false, ct);
                return;
            }

            await ReplyAsync(s, 0, ct);

            // --- MTProto obfuscated2: первые 64 байта содержат номер DC ---
            var init = await ReadExactAsync(s, 64, ct);
            var dc = TryReadDc(init) ?? TelegramIps.DcFor(ip) ?? 2;
            var media = dc < 0;
            dc = Math.Abs(dc);

            ClientWebSocket? ws = dc is >= 1 and <= 5 ? await ConnectWsAsync(dc, media, ct) : null;
            if (ws == null)
            {
                Log.Write($"TG: DC{dc} через WS недоступен, иду напрямую на {ip}:{port}");
                await DirectAsync(s, ip, null, port, init, true, ct);
                return;
            }

            await ws.SendAsync(init.AsMemory(), WebSocketMessageType.Binary, true, ct);
            await PipeWsAsync(s, ws, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not IOException and not SocketException)
        {
            Log.Write("TG: " + e.Message);
        }
        catch { /* обрыв соединения — норма */ }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    static int? TryReadDc(byte[] init)
    {
        try
        {
            var key = init.AsSpan(8, 32).ToArray();
            var iv = init.AsSpan(40, 16).ToArray();
            var dec = AesCtr(key, iv, init);
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(dec.AsSpan(56, 4));
            if (tag is not (0xEFEFEFEF or 0xEEEEEEEE or 0xDDDDDDDD)) return null;
            int dc = BinaryPrimitives.ReadInt16LittleEndian(dec.AsSpan(60, 2));
            return dc == 0 ? null : dc;
        }
        catch { return null; }
    }

    static byte[] AesCtr(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var counter = (byte[])iv.Clone();
        var ks = new byte[16];
        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i += 16)
        {
            aes.EncryptEcb(counter, ks, PaddingMode.None);
            for (var j = 0; j < 16 && i + j < data.Length; j++)
                output[i + j] = (byte)(data[i + j] ^ ks[j]);
            for (var k = 15; k >= 0; k--)
                if (++counter[k] != 0) break;
        }
        return output;
    }

    static async Task<ClientWebSocket?> ConnectWsAsync(int dc, bool media, CancellationToken ct)
    {
        var hosts = media
            ? new[] { $"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org" }
            : new[] { $"kws{dc}.web.telegram.org" };

        foreach (var h in hosts)
        {
            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("binary");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            ws.Options.Proxy = null;
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await ws.ConnectAsync(new Uri($"wss://{h}/apiws"), to.Token);
                return ws;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log.Write($"TG: {h} — {e.Message}");
                ws.Dispose();
            }
        }
        return null;
    }

    static async Task PipeWsAsync(NetworkStream s, ClientWebSocket ws, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var up = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                int n;
                while ((n = await s.ReadAsync(buf, token)) > 0)
                    await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, token);
            }
            finally { linked.Cancel(); }
        }, token);

        var down = Task.Run(async () =>
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buf.AsMemory(), token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    await s.WriteAsync(buf.AsMemory(0, r.Count), token);
                }
            }
            finally { linked.Cancel(); }
        }, token);

        try { await Task.WhenAll(up, down); } catch { }
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch { }
        ws.Dispose();
    }

    static async Task DirectAsync(NetworkStream s, IPAddress? ip, string? host, int port, byte[]? prefix, bool replied, CancellationToken ct)
    {
        using var remote = new TcpClient { NoDelay = true };
        try
        {
            if (ip != null) await remote.ConnectAsync(ip, port, ct);
            else await remote.ConnectAsync(host!, port, ct);
        }
        catch
        {
            if (!replied) await ReplyAsync(s, 5, ct);
            return;
        }
        if (!replied) await ReplyAsync(s, 0, ct);

        var rs = remote.GetStream();
        if (prefix != null) await rs.WriteAsync(prefix, ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var a = CopyAsync(s, rs, linked);
        var b = CopyAsync(rs, s, linked);
        try { await Task.WhenAll(a, b); } catch { }
    }

    static async Task CopyAsync(Stream from, Stream to, CancellationTokenSource linked)
    {
        try { await from.CopyToAsync(to, 65536, linked.Token); }
        finally { linked.Cancel(); }
    }

    static Task ReplyAsync(NetworkStream s, byte code, CancellationToken ct) =>
        s.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    static async Task<byte[]> ReadExactAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    /// <summary>Доходит ли WebSocket до серверов Telegram (для статуса на главной).</summary>
    public static async Task<bool> ProbeAsync(CancellationToken ct)
    {
        using var ws = await ConnectWsAsync(2, false, ct);
        return ws != null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Log.Write("Telegram-мост остановлен");
    }
}

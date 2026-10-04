using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace iPhoneCam.Net;

/// <summary>手機裝置資訊（UDP 信標收集）。</summary>
public sealed record PhoneDevice(string Name, string Ip, int Port, string Codecs, string Max, DateTime LastSeen)
{
    public string Display => $"{Name}（{Ip}）[{Max}]";
}

/// <summary>
/// UDP 信標監聽（SPEC-v4 §11.1）：手機每 2 秒廣播到 19301，PC 收集去重、10 秒逾時移除。
/// </summary>
public sealed class BeaconListener : IDisposable
{
    public const int BeaconPort = 19301;
    public event Action<IReadOnlyList<PhoneDevice>>? DevicesChanged;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, PhoneDevice> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Start()
    {
        if (_udp != null) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(BeaconPort);
        _ = ReceiveLoop(_cts.Token);
        _ = ExpireLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _udp?.Close(); } catch { }
        _udp = null;
    }

    public IReadOnlyList<PhoneDevice> Snapshot()
    {
        lock (_lock) return _devices.Values.OrderBy(d => d.Name).ToList();
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(ct);
                string json = Encoding.UTF8.GetString(r.Buffer);
                PhoneDevice? dev = Parse(json, r.RemoteEndPoint);
                if (dev == null) continue;
                bool changed;
                lock (_lock)
                {
                    // 新 IP 或檔位/編碼變了才刷新（手機切畫質時 PC 清單跟著更新）
                    changed = !_devices.TryGetValue(dev.Ip, out var old)
                        || old.Max != dev.Max || old.Codecs != dev.Codecs || old.Name != dev.Name;
                    _devices[dev.Ip] = dev;
                }
                if (changed) DevicesChanged?.Invoke(Snapshot());
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task ExpireLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                bool changed = false;
                lock (_lock)
                {
                    // 家用 AP 廣播常掉包，15 秒才算失聯（手機每 2 秒一發＋已知 PC 補單播）
                    var old = _devices.Values.Where(d => (DateTime.UtcNow - d.LastSeen).TotalSeconds > 15).ToList();
                    foreach (var d in old) { _devices.Remove(d.Ip); changed = true; }
                }
                if (changed) DevicesChanged?.Invoke(Snapshot());
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private static PhoneDevice? Parse(string json, EndPoint remote)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("kind", out var k) && k.GetString() != "beacon") return null;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            string ip = root.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(ip) && remote is IPEndPoint ep) ip = ep.Address.ToString();
            int port = root.TryGetProperty("port", out var p) ? p.GetInt32() : Cam1.Port;
            string codecs = "h264";
            if (root.TryGetProperty("codec", out var c))
                codecs = c.ValueKind == JsonValueKind.Array
                    ? string.Join(",", c.EnumerateArray().Select(e => e.GetString()))
                    : c.GetString() ?? "h264";
            string max = root.TryGetProperty("max", out var m) ? m.GetString() ?? "" : "";
            return new PhoneDevice(name, ip, port, codecs, max, DateTime.UtcNow);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// CAM1 主動連線端（SPEC-v4 §11.1 正式路徑：手機監聽 19300，PC 連過去）。
/// 事件形狀與 Cam1Server 對稱，GUI 可共用同一組 handler。
/// </summary>
public sealed class Cam1Client : IDisposable
{
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<Cam1.Frame>? VideoReceived;
    public event Action<string, string>? ControlReceived;
    public event Action<Cam1Server.StatsSnapshot>? StatsUpdated;

    private TcpClient? _tcp;
    private CancellationTokenSource? _cts;
    private long _videoFrames, _videoBytes, _controlMsgs;
    private long _windowFrames, _windowBytes;
    private DateTime _windowStart = DateTime.UtcNow;
    private string _remote = "";
    private bool _disposed;

    public bool IsConnected => _tcp?.Connected == true;

    public async Task<(bool ok, string msg)> ConnectAsync(string ip, int port)
    {
        Disconnect();
        _cts = new CancellationTokenSource();
        try
        {
            _tcp = new TcpClient();
            _tcp.NoDelay = true; // 小 NAL 別被 Nagle 拖（延遲敏感）
            await _tcp.ConnectAsync(ip, port, _cts.Token).ConfigureAwait(false);
            _remote = $"{ip}:{port}";
            // 收訊迴圈放背景執行緒：它會一路呼叫 VideoReceived→Feed（pipe 寫入可能擋），
            // 絕不能跑在 UI 同步環境（之前它繼承 UI context，Feed 一擋就整個 App 卡死＋手機被背壓拖死）。
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            return (true, $"已連線 {_remote}");
        }
        catch (Exception e)
        {
            Disconnect();
            return (false, "連不上：" + e.Message);
        }
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _tcp?.Close(); } catch { }
        _tcp = null;
    }

    /// <summary>發控制訊息（UI 執行緒可呼叫，如 idr_request）。</summary>
    public async Task SendControlAsync(string kind, object message)
    {
        var tcp = _tcp;
        if (tcp == null || !tcp.Connected) return;
        try { await Cam1.SendControlAsync(tcp.GetStream(), kind, message, CancellationToken.None); }
        catch { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        NetworkStream stream;
        try { stream = _tcp!.GetStream(); }
        catch { return; }
        Connected?.Invoke(_remote);
        try
        {
            // 交握：回 hello＋link_info（手機是伺服器，先講話的是它）
            await Cam1.SendControlAsync(stream, "hello",
                new { app = "iPhoneCam-PC", version = "v4", device = Environment.MachineName }, ct);
            while (!ct.IsCancellationRequested && _tcp?.Connected == true)
            {
                var frame = await Cam1.ReceiveAsync(stream, ct);
                switch (frame.Type)
                {
                    case Cam1.TypeVideo:
                        _videoFrames++; _videoBytes += frame.Payload.Length;
                        _windowFrames++; _windowBytes += frame.Payload.Length;
                        VideoReceived?.Invoke(frame);
                        break;
                    case Cam1.TypeControl:
                        _controlMsgs++;
                        string json = Encoding.UTF8.GetString(frame.Payload);
                        string kind = "";
                        try { kind = Cam1.ControlKind(json); } catch { }
                        ControlReceived?.Invoke(kind, json);
                        break;
                }
                TickStats();
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        Disconnected?.Invoke(_remote);
    }

    private void TickStats()
    {
        var now = DateTime.UtcNow;
        double sec = (now - _windowStart).TotalSeconds;
        if (sec >= 1.0)
        {
            var snap = new Cam1Server.StatsSnapshot(_remote, _videoFrames, _videoBytes, _controlMsgs,
                _windowFrames / sec, _windowBytes * 8.0 / sec / 1_000_000.0);
            _windowFrames = 0; _windowBytes = 0; _windowStart = now;
            StatsUpdated?.Invoke(snap);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
    }
}

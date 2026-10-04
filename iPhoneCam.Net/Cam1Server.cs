using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace iPhoneCam.Net;

/// <summary>CAM1 收流端（SPEC-v3 §3/§4）：監聽 19300，交握＋碼率擋板＋統計。P2a 先收先計，不解碼。</summary>
public sealed class Cam1Server : IDisposable
{
    public sealed record StatsSnapshot(string Remote, long VideoFrames, long VideoBytes,
        long ControlMsgs, double Fps, double Mbps);

    public event Action<string>? ClientConnected;
    public event Action<string>? ClientDisconnected;
    public event Action<Cam1.Frame>? VideoReceived;
    public event Action<string, string>? ControlReceived; // (kind, json)
    public event Action<StatsSnapshot>? StatsUpdated;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Cam1Session? _session;

    public bool Running => _listener != null;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Cam1.Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _session?.Close(); } catch { }
        _session = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _session?.Close();
                _session = new Cam1Session(client, this);
                _ = _session.RunAsync();
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
        }
    }

    public StatsSnapshot? CurrentStats => _session?.Snapshot();

    public void Dispose() => Stop();

    private sealed class Cam1Session(TcpClient client, Cam1Server owner)
    {
        private readonly TcpClient _client = client;
        private long _videoFrames, _videoBytes, _controlMsgs;
        private long _windowFrames, _windowBytes;
        private DateTime _windowStart = DateTime.UtcNow;
        private double _fps, _mbps;

        public void Close()
        {
            try { _client.Close(); } catch { }
        }

        public StatsSnapshot Snapshot()
        {
            string remote = "unknown";
            try { remote = _client.Client.RemoteEndPoint?.ToString() ?? remote; } catch { }
            return new StatsSnapshot(remote, _videoFrames, _videoBytes, _controlMsgs, _fps, _mbps);
        }

        public async Task RunAsync()
        {
            string remote;
            try { remote = _client.Client.RemoteEndPoint?.ToString() ?? "unknown"; }
            catch { remote = "unknown"; }
            owner.ClientConnected?.Invoke(remote);
            try
            {
                using var stream = _client.GetStream();
                // 交握：先回 hello（PC＝大腦，見 SPEC-v3 §1）
                await Cam1.SendControlAsync(stream, "hello",
                    new { app = "iPhoneCam-PC", version = "v3", device = Environment.MachineName },
                    CancellationToken.None);
                while (_client.Connected)
                {
                    var frame = await Cam1.ReceiveAsync(stream, CancellationToken.None);
                    switch (frame.Type)
                    {
                        case Cam1.TypeVideo:
                            _videoFrames++; _videoBytes += frame.Payload.Length;
                            _windowFrames++; _windowBytes += frame.Payload.Length;
                            owner.VideoReceived?.Invoke(frame);
                            break;
                        case Cam1.TypeControl:
                            _controlMsgs++;
                            string json = Encoding.UTF8.GetString(frame.Payload);
                            string kind = "";
                            try { kind = Cam1.ControlKind(json); } catch { }
                            if (kind == "link_info") await ReplyLinkInfoAsync(stream, json);
                            else owner.ControlReceived?.Invoke(kind, json);
                            break;
                    }
                    TickStats(remote);
                }
            }
            catch { }
            owner.ClientDisconnected?.Invoke(remote);
        }

        private void TickStats(string remote)
        {
            var now = DateTime.UtcNow;
            double sec = (now - _windowStart).TotalSeconds;
            if (sec >= 1.0)
            {
                _fps = _windowFrames / sec;
                _mbps = _windowBytes * 8.0 / sec / 1_000_000.0;
                _windowFrames = 0; _windowBytes = 0; _windowStart = now;
                owner.StatsUpdated?.Invoke(Snapshot());
            }
        }

        // 碼率擋板（SPEC-v3 §3）：wifi 100／usb2 100／usb3 400，取雙方最低
        private static async Task ReplyLinkInfoAsync(NetworkStream stream, string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string transport = root.TryGetProperty("transport", out var t) ? t.GetString() ?? "wifi" : "wifi";
                double measured = root.TryGetProperty("measured_mbps", out var m) ? m.GetDouble() : 0;
                double cap = transport switch
                {
                    "usb3" => 400,
                    "usb2" => 100,
                    _ => 100, // wifi（含 AX3000，見 SPEC-v3 §3）
                };
                double allowed = Math.Min(cap, measured > 0 ? measured : cap);
                await Cam1.SendControlAsync(stream, "link_cap",
                    new { transport, cap_mbps = cap, allowed_mbps = allowed }, CancellationToken.None);
            }
            catch { }
        }
    }
}

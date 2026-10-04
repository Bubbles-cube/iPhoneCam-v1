using System.Text;
using System.Windows;
using DirectN;
using Windows.Devices.Enumeration;
using Microsoft.Win32;
using iPhoneCam.Net;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Diagnostics;

namespace iPhoneCam.App;

public partial class MainWindow : Window
{
    private const string FriendlyName = "iPhoneCam";
    private const string SourceId = "{8136b935-e87f-4c31-9456-31eedfd84776}"; // P2d後新 CLSID（舊的疑似被快取）
    private IComObject<IMFVirtualCamera>? _camera;
    private bool _mfStarted;
    // 虛擬攝像頭畫布（跟著串流顯示尺寸走；換檔重建裝置，預設 720p 4:3 原生）
    private int _camW = 960, _camH = 720;

    private static bool IsCamTier(int w, int h) =>
        (w == 960 && h == 720) || (w == 1440 && h == 1080) ||
        (w == 1920 && h == 1440) || (w == 2880 && h == 2160) ||
        (w == 720 && h == 960) || (w == 1080 && h == 1440) ||
        (w == 1440 && h == 1920) || (w == 2160 && h == 2880);
    private readonly object _lock = new();
    private BeaconListener? _beacon;
    private Cam1Client? _client;
    // UDP 影像通道（無線最優解；TCP 只留控制）。_useUdp=true 後 TCP 影像忽略。
    private UdpVideoReceiver? _udp;
    private bool _useUdp;
    private bool _connected;
    private long _lastUdpMs;
    private long _connMs;
    private FileStream? _dump;
    private bool _saving;
    // 即時預覽：網路執行緒只 enqueue，pump 執行緒顯示；解碼走 ffmpeg 子程序（MF 自幹版有 AV 坑，先冰著）
    private readonly ConcurrentQueue<byte[]> _previewQ = new();
    private readonly H264Preview _decoder = new();
    private readonly FfmpegPreview _ff = new();
    private readonly SharedFrameWriter _frameWriter = new(); // P2d：餵給虛擬攝像頭
    private int _vidW, _vidH, _vidFps = 30, _vidRot;
    private bool _vidFlip;
    private string _vidCodec = "h264", _ffKey = "", _previewLabel = "";
    /// <summary>解碼器運作中的規格（不含方向/影像參數）。規格單獨不變＝方向或參數變，立即跟隨不受防暴衝擋。</summary>
    private string _ffBase = "";
    private long _lastFfStartMs;
    private long _lastNetLogMs;
    private long _lastShownLogMs;
    private long _shown;
    private long _drops;
    private string _dropLoggedFor = "";
    private long _netIn;
    /// <summary>硬解掛過一次就整場改軟解（硬體不會中途長出來；C:\Temp\ipc-softdec 存在也強制軟解，測試基線用）。</summary>
    private bool _hwBroken;
    private string _firstLoggedFor = "";
    private static readonly string PreviewLog = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ipc-preview.log");

    private static void PLog(string s)
    {
        try { System.IO.File.AppendAllText(PreviewLog, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); }
        catch { }
    }
    private byte[]? _sps, _pps, _seqHead;
    private CancellationTokenSource? _pumpCts;
    private WriteableBitmap? _bitmapA, _bitmapB;
    private bool _useA;
    private DateTime _lastShown = DateTime.MinValue;
    // 儀表板狀態：影像 eq（亮度/對比/飽和/銳利）、美顏（大眼/美白/瘦臉/瞳孔）、UI 就緒旗標
    private double _eqB, _eqC = 1, _eqS = 1, _eqU;
    private int _beautyEye = 20, _beautyWhite = 49, _beautyFace = 30, _beautyPupil = 60;
    private bool _uiReady;
    private readonly System.Windows.Threading.DispatcherTimer _eqTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _beautyTimer = new();
    private string _phoneName = "", _phoneIp = "";
    private WriteableBitmap? _lastShownBitmap;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    // 開即用：啟動自動開虛擬相機＋永遠搜尋（按鈕已拿掉）
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "虛擬相機：啟動中…";
        // 儀表板初始化：下拉預設＋防抖 timer＋軟解勾選狀態（先設好再開 _uiReady，避免載入時誤發）
        try
        {
            CmbRes.SelectedIndex = 0;
            CmbFps.SelectedIndex = 0;
            ChkSoftDec.IsChecked = File.Exists(@"C:\Temp\ipc-softdec");
            _eqTimer.Interval = TimeSpan.FromMilliseconds(800);
            _eqTimer.Tick += (s, a) => { _eqTimer.Stop(); TryConfigureDecoder(); };
            _beautyTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _beautyTimer.Tick += (s, a) => { _beautyTimer.Stop(); SendBeauty(); };
            ShowTab("Video");
            HighlightNav("Home");
            _uiReady = true;
        }
        catch { }
        var (ok, msg) = await Task.Run(StartCamera);
        TxtStatus.Text = msg;
        StartBeaconAlways();
    }

    private void StartBeaconAlways()
    {
        try
        {
            _beacon ??= new BeaconListener();
            _beacon.DevicesChanged += devs => Dispatcher.Invoke(() => RefreshPhones(devs));
            _beacon.Start();
            TxtNet.Text = "搜尋中…（手機開 App 按開始廣播）";
        }
        catch (Exception ex)
        {
            TxtNet.Text = "搜尋啟動失敗：" + ex.Message;
        }
    }

    private void RefreshPhones(IReadOnlyList<PhoneDevice> devs)
    {
        object? sel = LstPhones.SelectedItem;
        LstPhones.Items.Clear();
        foreach (var d in devs) LstPhones.Items.Add(d);
        LstPhones.DisplayMemberPath = "Display";
        if (sel is PhoneDevice s)
        {
            foreach (var item in LstPhones.Items)
                if (item is PhoneDevice d && d.Ip == s.Ip) { LstPhones.SelectedItem = item; break; }
        }
        if (LstPhones.SelectedItem == null && LstPhones.Items.Count > 0) LstPhones.SelectedIndex = 0;
        // 信標一直在跑：已連線時只刷新清單，不洗掉「接收：…」狀態
        if (_connected) return;
        TxtNet.Text = $"搜尋中…找到 {devs.Count} 台";
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (LstPhones.SelectedItem is not PhoneDevice d)
        {
            TxtNet.Text = "先選一台手機（或下面手動輸 IP）";
            return;
        }
        await ConnectPhone(d.Ip, d.Port);
    }

    private async void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        string ip = TxtIp.Text.Trim();
        if (string.IsNullOrEmpty(ip)) { TxtNet.Text = "先輸手機 IP"; return; }
        await ConnectPhone(ip, Cam1.Port);
    }

    private async Task ConnectPhone(string ip, int port)
    {
        try { _client?.Dispose(); } catch { }
        try
        {
            if (LstPhones.SelectedItem is PhoneDevice sd) { _phoneName = sd.Name; _phoneIp = sd.Ip; }
            else { _phoneName = "手機"; _phoneIp = ip; }
        }
        catch { _phoneName = "手機"; _phoneIp = ip; }
        _client = new Cam1Client();
        _client.Connected += ep => Dispatcher.Invoke(() =>
        {
            TxtNet.Text = $"接收：已連線（{ep}）";
            TxtHeadConn.Text = $"   ● 已連接手機  {_phoneName}（{_phoneIp}）";
            TxtHeadConn.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3DFF88"));
            TxtPhone.Text = $"{_phoneName}（{_phoneIp}）";
        });
        _client.Disconnected += ep => Dispatcher.Invoke(() => TxtNet.Text = "接收：已斷線");
        _client.StatsUpdated += s => Dispatcher.Invoke(() =>
        {
            TxtNet.Text = $"接收：{s.Remote}｜{s.Fps:F1}fps｜{s.Mbps:F2}Mbps";
            // 每 5 秒記一次 net 計數：跟手機「已送 N 幀」對帳用
            if (Environment.TickCount64 - _lastNetLogMs > 5000)
            {
                _lastNetLogMs = Environment.TickCount64;
                PLog($"net {s.Fps:F1}fps {s.Mbps:F2}Mbps 累計幀={s.VideoFrames} 丟棄={_drops}");
            }
            // UDP 啟用中：每秒回報 loss/jitter，手機走 ABR 階梯
            try
            {
                if (_useUdp && _udp != null && _client != null)
                {
                    var (loss, jm) = _udp.TakeSecondStats();
                    _ = _client.SendControlAsync("net_state", new { loss, jitterMs = jm });
                }
            }
            catch { }
        });
        _client.ControlReceived += OnNetControl;
        _client.VideoReceived += OnNetVideo;
        TxtNet.Text = $"連線中…{ip}:{port}";
        BtnConnect.IsEnabled = false;
        var (ok, msg) = await _client.ConnectAsync(ip, port);
        TxtNet.Text = (ok ? "接收：" : "") + msg;
        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = ok;
        if (ok)
        {
            _connected = true;
            _connMs = Environment.TickCount64;
            _lastUdpMs = 0;
            // UDP 影像通道：先開再告知（手機收到 udp_ready 才會切過來）
            try
            {
                _udp?.Dispose();
                _useUdp = false;
                _udp = new UdpVideoReceiver();
                _udp.FrameReady += OnUdpVideo;
                _udp.NackNeeded += seqs =>
                {
                    try { _ = _client?.SendControlAsync("nack", new { seqs }); } catch { }
                };
                _udp.Start();
                _ = _client.SendControlAsync("udp_ready", new { port = UdpVideoReceiver.DefaultPort, host = GetLanIp() });
            }
            catch (Exception ex) { PLog($"UDP 通道啟動失敗（續用 TCP）：{ex.Message}"); }
            _pumpCts = new CancellationTokenSource();
            _ = Task.Run(() => PreviewPump(_pumpCts.Token));
            // 連上就推送 PC 下拉的畫質（斷線期間改的下拉，否則下次連線不會生效）
            try { SendSetVideo(); } catch { }
        }
    }

    /// <summary>區網 IPv4（給手機回打 UDP 用；adb 轉發時 TCP 對端是假位址，不能用）。</summary>
    private static string GetLanIp()
    {
        try
        {
            string? pick = null, first = null;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.")) continue;
                    first ??= ip;
                    if (ip.StartsWith("192.168.")) return ip;
                    if (pick == null && (ip.StartsWith("10.") || ip.StartsWith("172."))) pick = ip;
                }
            }
            return pick ?? first ?? "127.0.0.1";
        }
        catch { return "127.0.0.1"; }
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _ff.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
        _connected = false;
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _useUdp = false;
        _ffKey = "";
        _previewLabel = "";
        _firstLoggedFor = "";
        _shown = 0; _netIn = 0; _drops = 0; _dropLoggedFor = "";
        _saving = false;
        Dispatcher.Invoke(() => TxtLive.Visibility = Visibility.Collapsed);
        CloseDump();
        TxtNet.Text = "接收：未連線";
        TxtHeadConn.Text = "   ○ 未連接手機";
        TxtHeadConn.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9AA3C0"));
        TxtPhone.Text = "未連線";
        TxtDecMode.Text = "解碼：-";
        BtnDisconnect.IsEnabled = false;
        // 斷線殘留清掉：上一幀畫面／預覽文字／統計，否則看起來像還連著
        try
        {
            ImgPreview.Source = null;
            _lastShownBitmap = null;
            TxtPreview.Text = "等串流…";
            TxtStats.Text = "幀：-";
            BtnSave.Content = "存檔：關";
        }
        catch { }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _saving = !_saving;
        if (_saving)
        {
            try
            {
                string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_captures");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, $"recv-{DateTime.Now:yyyyMMdd-HHmmss}.h264");
                _dump = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                BtnSave.Content = "存檔：開";
                TxtNet.Text += $"（存到 {System.IO.Path.GetFileName(path)}）";
            }
            catch (Exception ex)
            {
                _saving = false;
                TxtNet.Text = "存檔打不開：" + ex.Message;
            }
        }
        else
        {
            CloseDump();
            BtnSave.Content = "存檔：關";
        }
    }

    private void CloseDump()
    {
        try { lock (_lock) { _dump?.Dispose(); _dump = null; } } catch { }
    }

    /// <summary>遠端重啟手機編碼器（手機出幀異常：凍結／0.1fps 慢流時按，手機會重啟編碼＋重送參數）。</summary>
    private async void BtnRestartEnc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var c = _client;
            if (c == null) { TxtNet.Text = "沒連線，重啟無效"; return; }
            int w, h, fps; string codec;
            lock (_lock) { w = _vidW; h = _vidH; fps = _vidFps; codec = _vidCodec; }
            if (w <= 0 || h <= 0) { w = 960; h = 720; fps = 30; codec = "h264"; }
            await c.SendControlAsync("set_video", new { width = w, height = h, fps, codec });
            PLog($"已送 set_video（重啟編碼）：{w}x{h}@{fps} {codec}");
            TxtNet.Text = "已要求手機重啟編碼…";
        }
        catch (Exception ex) { TxtNet.Text = "重啟要求失敗：" + ex.Message; }
    }

    private void OnNetControl(string kind, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (kind == "video_params")
            {
                lock (_lock)
                {
                    if (root.TryGetProperty("width", out var w)) _vidW = w.GetInt32();
                    if (root.TryGetProperty("height", out var h)) _vidH = h.GetInt32();
                    if (root.TryGetProperty("fps", out var f)) _vidFps = f.GetInt32();
                    if (root.TryGetProperty("rot", out var r)) _vidRot = RotNorm(r.GetInt32());
                    if (root.TryGetProperty("flip", out var fl)) _vidFlip = fl.GetInt32() != 0;
                    if (root.TryGetProperty("codec", out var c)) _vidCodec = c.GetString() == "hevc" ? "hevc" : "h264";
                    PLog($"video_params {_vidW}x{_vidH}@{_vidFps} {_vidCodec} rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                }
                TryConfigureDecoder();
            }
            else if (kind == "orientation")
            {
                // 手機方向為主：手機端目前畫面該顯示的正確朝向（變化時＋每 2 秒心跳）。
                // 收到就跟隨，漏掉也靠心跳自癒，不再卡死。
                bool changed = false;
                lock (_lock)
                {
                    if (root.TryGetProperty("rot", out var r))
                    {
                        int nr = RotNorm(r.GetInt32());
                        if (nr != _vidRot) { _vidRot = nr; changed = true; }
                    }
                    if (root.TryGetProperty("flip", out var fl))
                    {
                        bool nf = fl.GetInt32() != 0;
                        if (nf != _vidFlip) { _vidFlip = nf; changed = true; }
                    }
                    if (changed) PLog($"📱方向 rot={_vidRot} flip={(_vidFlip ? 1 : 0)}");
                }
                if (changed) TryConfigureDecoder();
            }
            else if (kind == "codec_config")
            {
                byte[] sps = Convert.FromBase64String(root.GetProperty("sps").GetString()!);
                byte[] pps = Convert.FromBase64String(root.GetProperty("pps").GetString()!);
                lock (_lock) { _sps = sps; _pps = pps; }
                // 手機 v0.2 起 sps 欄位已是整段序列頭（Annex-B），直接寫；舊版才包裝
                byte[] annexb = IsAnnexB(sps) ? sps : AnnexB.SpsPpsToAnnexB(sps, pps);
                lock (_lock) { _seqHead = annexb; if (_saving) _dump?.Write(annexb); }
                TryConfigureDecoder();
            }
        }
        catch { }
    }

    private static bool IsAnnexB(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 1) ||
                          (b[0] == 0 && b[1] == 0 && b[2] == 1));

    private static int RotNorm(int r)
    {
        int n = ((r % 360) + 360) % 360;
        return (n == 90 || n == 180 || n == 270) ? n : 0;
    }

    private void TryConfigureDecoder(bool force = false)
    {
        string? uiStartText = null, uiFailText = null, uiCamMsg = null;
        lock (_lock)
        {
            if (_vidW <= 0 || _vidH <= 0) return;
            int effRot = _vidRot;
            bool wantHw = !_hwBroken && !File.Exists(@"C:\Temp\ipc-softdec");
            string basePart = $"{_vidCodec}:{_vidW}x{_vidH}:{_vidFps}:{(wantHw ? "hw" : "sw")}";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string eqPart = string.Format(ci, "eq{0:F2},{1:F2},{2:F2},{3:F2}", _eqB, _eqC, _eqS, _eqU);
            string key = $"{basePart}:r{effRot}:f{(_vidFlip ? 1 : 0)}:{eqPart}";
            if (key == _ffKey && _ff.Running) return; // 同一組參數，免重啟
            // 手機方向為主＋參數即時跟隨：手機已做 1 秒防抖、滑桿有 800ms 防抖，
            // 方向/影像參數單獨變就立即重啟（不擋）；只有規格變化（解析度/編碼/fps/hw）才防暴衝。
            bool paramOnly = basePart == _ffBase && key != _ffKey;
            if (!force && !paramOnly && _ff.Running && Environment.TickCount64 - _lastFfStartMs < 2000)
            {
                PLog($"重啟略過（防暴衝）：{key} 沿用 {_ffKey}");
                return;
            }
            if (paramOnly) PLog($"參數跟隨：{key}");
            _ffKey = key;
            _lastFfStartMs = Environment.TickCount64;
            try { _ff.Stop(); } catch { }
            string? ff = FfmpegPreview.FindFfmpeg();
            // Dispatcher.Invoke 絕不能在 _lock 裡面（網路執行緒拿鎖等 UI、UI 拿鎖等網路＝死結，實測卡死過）。
            // 先在鎖內算好，出去再 Invoke。
            bool started = ff != null && _ff.Start(_vidW, _vidH, _vidFps, ff, effRot, _vidFlip, _vidCodec, wantHw,
                _eqB, _eqC, _eqS, _eqU);
            if (started)
            {
                _ffBase = basePart;
                PLog($"解碼器啟動：{key}");
                // 新實例第一口先餵 SPS（管線裡的 P 幀無 SPS，直接等 IDR 也行，雙保險）
                try { if (_seqHead != null) _ff.Feed(_seqHead); } catch { }
                string facing = _vidFlip ? "前鏡" : "後鏡";
                string portrait = (effRot % 180 != 0) ? "（直向）" : "";
                _previewLabel = $"{_ff.OutW}x{_ff.OutH} {facing} rot={effRot}{portrait} 預覽中・{_ff.HwMode}";
                uiStartText = $"{_ff.OutW}x{_ff.OutH} 預覽解碼器啟動…{facing} rot={effRot}{portrait}・{_ff.HwMode}";
                // 新解碼器等 keyframe 最久 2 秒，直接要一顆 IDR 馬上有畫面
                _ = _client?.SendControlAsync("idr_request", new { });
                PLog("idr_request 已發");
                // 虛擬攝像頭跟著串流換畫布（只認檔位尺寸；OBS 那邊要重加來源）
                int ow = _ff.OutW, oh = _ff.OutH;
                if ((ow != _camW || oh != _camH) && IsCamTier(ow, oh))
                {
                    var (rok, rmsg) = RestartCamera(ow, oh);
                    uiCamMsg = (rok ? "✅ " : "❌ ") + rmsg + "（OBS 請重加來源）";
                }
            }
            else if (ff == null)
                uiFailText = "找不到 ffmpeg（C:\\Temp\\ffmpeg）";
        }
        if (uiStartText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiStartText);
        else if (uiFailText != null) Dispatcher.Invoke(() => TxtPreview.Text = uiFailText);
        if (uiCamMsg != null) Dispatcher.Invoke(() => TxtStatus.Text = uiCamMsg);
    }

    private void OnNetVideo(Cam1.Frame frame)
    {
        // UDP 啟用後 TCP 影像就當備援（手機切 UDP 後不再發 TCP 影像）
        if (_useUdp) return;
        HandleVideoBytes(frame.Payload);
    }

    /// <summary>UDP 影像到達（已重組＋排序）。第一次到就切換來源＋回 udp_ok。</summary>
    private void OnUdpVideo(UdpVideoReceiver.VideoFrame f)
    {
        try
        {
            _lastUdpMs = Environment.TickCount64;
            if (!_useUdp)
            {
                _useUdp = true;
                PLog("影像來源切換：UDP（TCP 只留控制）");
                try { _ = _client?.SendControlAsync("udp_ok", new { }); } catch { }
            }
            HandleVideoBytes(f.Avcc);
        }
        catch { }
    }

    private void HandleVideoBytes(byte[] payload)
    {
        byte[]? annexb = null;
        try { annexb = AnnexB.ConvertAvcc(payload); } catch { }
        if (annexb == null)
        {
            _drops++;
            // 留下第一個被丟幀的開頭指紋（每組參數一次），看看到底是什麼格式
            if (_dropLoggedFor != _ffKey)
            {
                _dropLoggedFor = _ffKey;
                int n = Math.Min(16, payload.Length);
                PLog($"首丟幀 len={payload.Length} hex={BitConverter.ToString(payload, 0, n)}");
            }
            return;
        }
        try
        {
            if ((_netIn++ % 150) == 0) PLog($"vin {_netIn} annexb={annexb.Length}B saving={_saving}");
            if (_saving) lock (_lock) { _dump?.Write(annexb); };
            // 每顆 IDR 前面補 SPS/PPS：解碼器每組 keyframe 都有參數集，
            // parser 不再每幀噴 non-existing PPS（CPU 有感）＋中途加入／丟包後下一顆 IDR 直接恢復
            if (_vidCodec != "hevc" && _seqHead != null && ContainsIdr(annexb))
                _ff.Feed(_seqHead);
            _ff.Feed(annexb);
        }
        catch { }
    }

    /// <summary>AnnexB 裡有沒有 H.264 IDR（NAL type 5）。3 位元組／4 位元組起始碼都吃。</summary>
    private static bool ContainsIdr(byte[] b)
    {
        for (int i = 0; i + 4 < b.Length; i++)
        {
            int sc;
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) sc = 3;
            else if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1) sc = 4;
            else continue;
            if ((b[i + sc] & 0x1F) == 5) return true;
            i += sc;
        }
        return false;
    }

    private async Task PreviewPump(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 解碼器死了（ffmpeg 崩潰／"no frame!" 自殺）：顯示死因，不空轉；
                // 超過 3 秒沒人重啟就自己救（不等下一次 video_params）
                if (!_ff.Running && _ffKey != "")
                {
                    string reason = _ff.DeathReason;
                    if (_firstLoggedFor != "dead:" + _ffKey)
                    {
                        _firstLoggedFor = "dead:" + _ffKey;
                        PLog($"解碼器死亡：{reason}");
                    }
                    Dispatcher.Invoke(() =>
                    {
                        string msg = "解碼器停了" + (reason != "" ? "：" + reason : "（重連會自動重啟）");
                        if (TxtPreview.Text != msg) TxtPreview.Text = msg;
                    });
                    if (Environment.TickCount64 - _lastFfStartMs > 3000)
                    {
                        // 硬解早死（一顆沒解出來＋stderr 在抱怨硬體）＝這台 D3D11VA 不可用，整場退回軟解
                        if (!_hwBroken && _ff.HwMode == "硬解" && _ff.FramesRead == 0 && _ff.UpMs < 8000)
                        {
                            string err = _ff.LastErrLines(30).ToLowerInvariant();
                            string[] kws = { "d3d11", "d3d12", "cuda", "hwaccel", "hwdownload", "hardware", "dxva", "qsv", "amf", "cuvid", "nvidia", "gpu" };
                            if (kws.Any(k => err.Contains(k)))
                            {
                                _hwBroken = true;
                                PLog($"硬解不可用，退回軟解：{_ff.DeathReason}");
                            }
                        }
                        PLog("死亡自救：重啟解碼器");
                        TryConfigureDecoder();
                    }
                    else await Task.Delay(500, ct);
                    continue;
                }
                byte[]? nv12 = _ff.TakeLatest();
                if (nv12 == null)
                {
                    // 解碼器活著卻沒東西：5 秒報一次（跟 net/顯示行對帳用）
                    if (Environment.TickCount64 - _lastShownLogMs > 5000)
                    {
                        _lastShownLogMs = Environment.TickCount64;
                        PLog($"pump 空轉（顯示 {_shown} 幀 解碼讀幀={_ff.FramesRead} ffCpu={_ff.CpuTime.TotalSeconds:F1}s 餵丟={_ff.FeedDrops} 寫入={_ff.LastFeedLog} 管線={_ff.PipeState()} ferr5=[{_ff.LastErrLines(5)}]）");
                    }
                    await Task.Delay(15, ct);
                    continue;
                }
                if (_firstLoggedFor != _ffKey)
                {
                    _firstLoggedFor = _ffKey;
                    PLog($"首幀已解：{_ff.OutW}x{_ff.OutH} NV12（{nv12.Length}B）");
                    Dispatcher.Invoke(() =>
                    {
                        TxtLive.Visibility = Visibility.Visible;
                        TxtDecMode.Text = $"解碼：{_ff.HwMode}";
                    });
                }
                if ((DateTime.UtcNow - _lastShown).TotalMilliseconds < 33) continue;
                _lastShown = DateTime.UtcNow;
                int w = _ff.OutW, h = _ff.OutH;
                if (w <= 0 || h <= 0 || (w & 1) != 0 || (h & 1) != 0) continue;
                int nvSize = w * h * 3 / 2;
                if (nv12.Length < nvSize) continue;
                // 虛擬攝像頭吃滿幀 NV12 直通（OBS/Discord 要流暢＋零轉換），
                // 預覽只顯示一半幀的半解析小圖（省上傳＋渲染，監看夠用）
                try { _frameWriter.Publish(w, h, nv12, SharedFrame.FmtNv12); } catch { } // 背景執行緒，不擋 UI
                if (_shown % 2 == 1) { _shown++; continue; }
                _shown++;
                int pw = w / 2, ph = h / 2;
                byte[] small;
                try { small = Nv12Preview.ConvertHalf(nv12, w, h); }
                catch { continue; }
                Dispatcher.Invoke(() =>
                {
                    // 雙 WriteableBitmap 輪替：寫的不跟 UI 渲染搶同一張
                    var target = _useA ? _bitmapA : _bitmapB;
                    _useA = !_useA;
                    if (target == null || target.PixelWidth != pw || target.PixelHeight != ph)
                    {
                        target = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null);
                        if (_useA) _bitmapB = target; else _bitmapA = target;
                    }
                    target.WritePixels(new Int32Rect(0, 0, pw, ph), small, pw * 4, 0);
                    ImgPreview.Source = target;
                    _lastShownBitmap = target;
                    // 同一文字不重設（省掉每幀的版面配置）
                    string label = _previewLabel != "" ? _previewLabel : $"{w}x{h} 預覽中";
                    if (TxtPreview.Text != label) TxtPreview.Text = label;
                });
                if (Environment.TickCount64 - _lastShownLogMs > 5000)
                {
                    _lastShownLogMs = Environment.TickCount64;
                    // UDP 看門狗：連著卻 3 秒沒 UDP 幀 → 當斷流，切回 TCP（手機收到 udp_lost 會切回來）
                    if (_connected && !_useUdp && _udp != null
                        && Environment.TickCount64 - _lastFfStartMs > 8000
                        && Environment.TickCount64 - _connMs > 3000)
                    {
                        // 還沒切過去又沒幀：可能 UDP 被擋，通知手機續用 TCP（只送一次，用 _ffKey 記）
                        if (_firstLoggedFor != "udplost:" + _ffKey)
                        {
                            _firstLoggedFor = "udplost:" + _ffKey;
                            PLog("UDP 無幀（被擋？），通知手機續用 TCP");
                            try { _ = _client?.SendControlAsync("udp_lost", new { }); } catch { }
                        }
                    }
                    string udpInfo = "";
                    try
                    {
                        if (_udp != null)
                            udpInfo = $" udp收{_udp.Received}/成{_udp.Complete}/丟{_udp.Lost} 抖{_udp.JitterMs:F0}ms";
                    }
                    catch { }
                    PLog($"顯示 {_shown} 幀 解碼讀幀={_ff.FramesRead} ffCpu={_ff.CpuTime.TotalSeconds:F1}s 餵丟={_ff.FeedDrops}{udpInfo}");
                    string st = $"幀 顯示{_shown}/解碼{_ff.FramesRead} 餵丟{_ff.FeedDrops} CPU{_ff.CpuTime.TotalSeconds:F1}s";
                    Dispatcher.Invoke(() => { if (TxtStats.Text != st) TxtStats.Text = st; });
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async void BtnDiag_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"相機狀態：{TxtStatus.Text}");
        sb.AppendLine($"系統版本：{Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "?")} "
            + $"(DisplayVersion {Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "?")})");
        sb.AppendLine("裝置清單：");
        try
        {
            var devs = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            foreach (var d in devs) sb.AppendLine($"  [{(d.IsEnabled ? "啟用" : "停用")}] {d.Name}");
        }
        catch (Exception ex) { sb.AppendLine("  列舉失敗：" + ex.Message); }
        Clipboard.SetText(sb.ToString());
        TxtStatus.Text = "診斷已複製到剪貼簿";
    }

    private void Border_Drag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 儀表板：導航＋分頁 ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not string tag) return;
        HighlightNav(tag);
        if (tag == "Home") { PageHome.Visibility = Visibility.Visible; PageSub.Visibility = Visibility.Collapsed; return; }
        PageHome.Visibility = Visibility.Visible; // 預覽永遠看得到，只切右面板
        PageSub.Visibility = Visibility.Collapsed;
        if (tag == "Image") ShowTab("Video");
        else if (tag == "Filter") ShowTab("Fx");
        else if (tag == "Devices") ShowSub("Devices");
        else if (tag == "Settings") ShowSub("Settings");
        else if (tag == "About") ShowSub("About");
    }

    private void HighlightNav(string tag)
    {
        foreach (var b in new[] { NavHome, NavImage, NavFilter, NavDevices, NavSettings, NavAbout })
        {
            bool on = (b.Tag as string) == tag;
            b.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(on ? "#FF5FD2" : "#9AA3C0"));
            b.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private void ShowSub(string which)
    {
        PageHome.Visibility = Visibility.Collapsed;
        PageSub.Visibility = Visibility.Visible;
        PageDevices.Visibility = which == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = which == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = which == "About" ? Visibility.Visible : Visibility.Collapsed;
        TxtPageTitle.Text = which == "Devices" ? "裝置管理" : which == "Settings" ? "設定" : "關於";
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is string tag) ShowTab(tag);
    }

    private void ShowTab(string tag)
    {
        PanelVideo.Visibility = tag == "Video" ? Visibility.Visible : Visibility.Collapsed;
        PanelFx.Visibility = tag == "Fx" ? Visibility.Visible : Visibility.Collapsed;
        PanelAdv.Visibility = tag == "Adv" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, on) in new[] { (TabVideo, tag == "Video"), (TabFx, tag == "Fx"), (TabAdv, tag == "Adv") })
        {
            btn.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(on ? "#FF5FD2" : "#9AA3C0"));
            btn.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    // ---------- 影像設定：解析度/FPS（發 set_video 給手機）＋eq 滑桿 ----------

    private void CmbResFps_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _client == null) return;
        SendSetVideo();
    }

    private void SendSetVideo()
    {
        try
        {
            var c = _client;
            if (c == null) return;
            int w = 960, h = 720;
            if (CmbRes.SelectedIndex == 1) { w = 1440; h = 1080; }
            else if (CmbRes.SelectedIndex == 2) { w = 1920; h = 1440; }
            else if (CmbRes.SelectedIndex == 3) { w = 2880; h = 2160; }
            int fps = CmbFps.SelectedIndex == 1 ? 60 : 30;
            string codec;
            lock (_lock) { codec = _vidCodec; }
            _ = c.SendControlAsync("set_video", new { width = w, height = h, fps, codec });
            PLog($"已送 set_video：{w}x{h}@{fps} {codec}（等手機回 video_params 自動切換）");
        }
        catch { }
    }

    private void EqSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _eqB = (SldBright.Value - 50) / 50;
        _eqC = SldContrast.Value / 50;
        _eqS = SldSat.Value / 50;
        _eqU = (SldSharp.Value - 50) / 50 * 1.5;
        TxtBright.Text = $"{SldBright.Value:F0}";
        TxtContrast.Text = $"{SldContrast.Value:F0}";
        TxtSat.Text = $"{SldSat.Value:F0}";
        TxtSharp.Text = $"{SldSharp.Value:F0}";
        _eqTimer.Stop();
        _eqTimer.Start(); // 800ms 防抖：拖完才重啟解碼器
    }

    private static readonly (string name, double b, double c, double s, double u)[] FxPresets =
    {
        ("原圖", 50, 50, 50, 50), ("柔光", 62, 45, 55, 25), ("清新", 57, 56, 66, 62),
        ("粉調", 60, 47, 72, 50), ("黑白", 50, 50, 0, 50),
    };

    private void FxPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || !int.TryParse(b.Tag as string, out int i)) return;
        if (i < 0 || i >= FxPresets.Length) return;
        var p = FxPresets[i];
        TxtFxName.Text = $"目前：{p.name}";
        SldBright.Value = p.b; SldContrast.Value = p.c; SldSat.Value = p.s; SldSharp.Value = p.u;
        // ValueChanged 會自己觸發防抖重啟，這裡不用多做
    }

    // ---------- 美顏滑桿（發 set_image 給手機） ----------

    private void BeautySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        _beautyEye = (int)SldEye.Value; _beautyWhite = (int)SldWhite.Value;
        _beautyFace = (int)SldFace.Value; _beautyPupil = (int)SldPupil.Value;
        TxtEye.Text = $"{_beautyEye}"; TxtWhite.Text = $"{_beautyWhite}";
        TxtFace.Text = $"{_beautyFace}"; TxtPupil.Text = $"{_beautyPupil}";
        _beautyTimer.Stop();
        _beautyTimer.Start();
    }

    private void SendBeauty()
    {
        try
        {
            var c = _client;
            if (c == null) return;
            _ = c.SendControlAsync("set_image", new
            {
                beauty = new { eye = _beautyEye, white = _beautyWhite, face = _beautyFace, pupil = _beautyPupil }
            });
            PLog($"已送 set_image 美顏：眼{_beautyEye} 白{_beautyWhite} 臉{_beautyFace} 瞳{_beautyPupil}");
        }
        catch { }
    }

    // ---------- 進階：IDR／截圖／最大化／平台／開播 ----------

    private void BtnIdr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_client == null) { TxtNet.Text = "沒連線，要 IDR 無效"; return; }
            _ = _client.SendControlAsync("idr_request", new { });
            PLog("手動 IDR 已發");
        }
        catch { }
    }

    private void BtnSnap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wb = _lastShownBitmap;
            if (wb == null) { TxtPreview.Text = "還沒畫面，截不到"; return; }
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "_captures");
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"snap-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            enc.Save(fs);
            TxtPreview.Text = $"已截圖：{System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) { TxtPreview.Text = "截圖失敗：" + ex.Message; }
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void BtnObs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var obs = Process.GetProcessesByName("obs64").FirstOrDefault();
            if (obs != null)
            {
                ShowWindow(obs.MainWindowHandle, 9);
                SetForegroundWindow(obs.MainWindowHandle);
                TxtPreview.Text = "已切到 OBS（把 iPhoneCam 加為視訊來源即可播出）";
            }
            else
            {
                string path = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path)
                    {
                        WorkingDirectory = @"C:\Program Files\obs-studio\bin\64bit",
                        UseShellExecute = true,
                    });
                    TxtPreview.Text = "OBS 啟動中…";
                }
                else TxtPreview.Text = "找不到 OBS（沒裝？）";
            }
        }
        catch (Exception ex) { TxtPreview.Text = "OBS 開啟失敗：" + ex.Message; }
    }

    private void Platform_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is string url)
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        // 開始直播＝連上手機（有選就用選的，沒選用第一台）
        try
        {
            if (LstPhones.SelectedItem == null && LstPhones.Items.Count > 0) LstPhones.SelectedIndex = 0;
            BtnConnect_Click(sender, e);
        }
        catch { }
    }

    private void BtnStopLive_Click(object sender, RoutedEventArgs e)
    {
        BtnDisconnect_Click(sender, e);
    }

    private void ChkSoftDec_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        try
        {
            string flag = @"C:\Temp\ipc-softdec";
            if (ChkSoftDec.IsChecked == true) File.WriteAllText(flag, "soft");
            else File.Delete(flag);
            TxtDecMode.Text = $"解碼：{(ChkSoftDec.IsChecked == true ? "軟解（重連生效）" : "硬解優先")}";
        }
        catch { }
    }

    private (bool ok, string msg) StartCamera()
    {
        try
        {
            lock (_lock)
            {
                if (!_mfStarted) { MFFunctions.MFStartup(); _mfStarted = true; }
                if (_camera == null)
                {
                    var hr = Functions.MFCreateVirtualCamera(
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
                        __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
                        FriendlyName, SourceId, null, 0, out var camera);
                    if (hr.IsError) return (false, $"建立失敗：{hr.Value}（Source DLL 有註冊嗎？）");
                    _camera = new ComObject<IMFVirtualCamera>(camera);
                }
                var shr = _camera.Object.Start(null);
                if (shr.IsError) return (false, $"啟動失敗：{shr.Value}（管理員註冊過了嗎？）");
                return (true, $"虛擬相機：{_camW}x{_camH} 已啟動（{FriendlyName}）");
            }
        }
        catch (Exception ex)
        {
            return (false, $"啟動發生問題：{ex.Message}");
        }
    }

    /// <summary>
    /// 虛擬攝像頭跟著串流換畫布：寫登錄＋重建裝置（OBS 那邊要重加來源或開關眼珠一次）。
    /// 只有尺寸真的變了才動；MF 不關（一直開著）。
    /// </summary>
    private (bool ok, string msg) RestartCamera(int w, int h)
    {
        try
        {
            lock (_lock)
            {
                if (w == _camW && h == _camH) return (true, "畫布沒變，不用重建");
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\iPhoneCam");
                    k?.SetValue("CamW", w, RegistryValueKind.DWord);
                    k?.SetValue("CamH", h, RegistryValueKind.DWord);
                }
                catch (Exception ex) { return (false, $"寫登錄失敗：{ex.Message}"); }
                try { _camera?.Object.Remove(); } catch { }
                try { _camera?.Dispose(); } catch { }
                _camera = null;
                _camW = w; _camH = h;
            }
            var (ok, msg) = StartCamera();
            if (ok) PLog($"虛擬攝像頭換畫布：{w}x{h}（OBS 請重加來源或開關眼珠）");
            return (ok, msg);
        }
        catch (Exception ex)
        {
            return (false, $"重建發生問題：{ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _pumpCts?.Cancel(); } catch { }
        try { _frameWriter.Dispose(); } catch { }
        try { _ff.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _beacon?.Dispose(); } catch { }
        try { _decoder.Dispose(); } catch { }
        CloseDump();
        try
        {
            lock (_lock)
            {
                _camera?.Object.Remove();
                _camera?.Dispose();
                _camera = null;
                if (_mfStarted) MFFunctions.MFShutdown();
            }
        }
        catch { }
        base.OnClosed(e);
    }
}

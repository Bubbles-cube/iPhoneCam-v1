package com.iphonecam.sender;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.Matrix;
import android.graphics.RectF;
import android.graphics.SurfaceTexture;
import android.hardware.SensorManager;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.ImageReader;
import android.graphics.ImageFormat;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;
import android.util.Base64;
import android.view.OrientationEventListener;
import android.view.Surface;
import android.view.TextureView;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.TextView;

import org.json.JSONObject;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.List;

/**
 * iPhoneCam Android Sender v0.1（對照 SPEC-v4 §11.2）。
 * 手機＝TCP 伺服器（19300）＋UDP 信標（19301），PC 主動連入。
 * v0.1 範圍：720p30 H.264 固定起播，set_video 可重啟換檔；美顏/音訊なし。
 */
public class MainActivity extends Activity {

    static final String TAG = "IPC";
    static final int CAM_PORT = 19300;
    static final int BEACON_PORT = 19301;
    static final byte T_VIDEO = 0x01;
    static final byte T_CONTROL = 0x03;

    TextureView preview;
    android.widget.FrameLayout frameBox;
    android.widget.ImageView standbyView;
    TextView status;
    Button btnToggle;

    // 儀表板 UI（展板佈局）
    TextView liveBadge, liveFps, connDot, connTitle, connIp, connSig;
    TextView resVal, fpsVal, camVal, beautyVal;
    TextView eyeVal, whiteVal, faceVal, pupilVal, bitrateVal;
    android.view.View beautyPanel, pageHome, pageSettings, pageHelp, funcCard;
    Button navHome, navSettings, navHelp;
    android.widget.SeekBar seekEye, seekWhite, seekFace, seekPupil, bitrateSeek;
    int resIdx = 0, fpsIdx = 0, beautyEye = 20, beautyWhite = 49, beautyFace = 30, beautyPupil = 60;
    // session 失敗 fallback 用：hasVideo＝真有畫面（LIVE 徽章看這個，不只看開關）；
    // sessFails＝連敗計數（防無限重試）；overOff＝此檔超採樣開不起來，先關掉重試
    volatile boolean hasVideo = false;
    volatile int sessFails = 0;
    volatile boolean overOff = false;
    int lastFw = 0, lastFh = 0;
    int bufW = 0, bufH = 0;
    String fitInfo = "-";
    // 超採樣：逼 HAL 用原生 CMOS 全片幅讀出（幀直接丟掉，只取讀出模式）
    ImageReader overReader = null;
    android.view.Surface overSurface = null;
    int overW = 0, overH = 0;
    java.util.ArrayList<int[]> overSizes = null;
    android.util.Range<Integer>[] fpsRanges = null;

    HandlerThread bgThread;
    Handler bg;
    CameraManager cameras;
    String backId, frontId;
    boolean useFront = false;
    CameraDevice device;
    CameraCaptureSession session;
    volatile MediaCodec encoder; // volatile：drain 執行緒要即時看到新舊交替，否則舊執行緒拿著新編碼器雙重 drain 直接閃退
    android.view.Surface encoderSurface;
    Thread drainThread, serverThread, beaconThread;
    volatile boolean broadcasting, streaming;
    OutputStream clientOut;
    Socket clientSocket;
    java.net.ServerSocket serverSocket;
    String clientIp = "-";
    String lastPcIp = "";
    WifiManager.WifiLock wifiLock;

    // 目前編碼參數（set_video 可改；原生 4:3，CMOS 滿幅不裁切）
    int vidW = 960, vidH = 720, vidFps = 30;
    double vidMbps = 4.0;
    String vidCodec = "video/avc";
    byte[] sps, pps, vps;

    // 陀螺儀方向：串流維持 sensor 原生，rot 告訴 PC 顯示時轉幾度（順時針）
    OrientationEventListener orient;
    int lastAngle = -1, curRot = -1, curFlip = -1, sensorOrient = 90;
    int rotCandidate = -1;

    long rotCandidateSince = 0;

    /** 方向心跳上次發送時間（drainLoop 內每 2 秒重申，PC 漏跟隨會自癒）。 */
    long lastOrientMs = 0;
    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        installCrashLog();
        setContentView(R.layout.activity_main);
        preview = findViewById(R.id.preview);
        standbyView = findViewById(R.id.standbyView);
        status = findViewById(R.id.status);
        btnToggle = findViewById(R.id.btnToggle);
        btnToggle.setOnClickListener(v -> toggle());
        bindDashboard();

        bgThread = new HandlerThread("cam");
        bgThread.start();
        bg = new Handler(bgThread.getLooper());
        cameras = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
        try {
            for (String id : cameras.getCameraIdList()) {
                Integer facing = cameras.getCameraCharacteristics(id)
                        .get(CameraCharacteristics.LENS_FACING);
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) backId = id;
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_FRONT) frontId = id;
            }
        } catch (CameraAccessException e) {
            log("相機列舉失敗：" + e.getMessage());
        }
        preview.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            public void onSurfaceTextureAvailable(SurfaceTexture s, int w, int h) {
                openCamera();
                if (preview != null) fitPreview();
            }
            public void onSurfaceTextureSizeChanged(SurfaceTexture s, int w, int h) {
                if (preview != null) fitPreview();
            }
            public boolean onSurfaceTextureDestroyed(SurfaceTexture s) { return true; }
            public void onSurfaceTextureUpdated(SurfaceTexture s) {}
        });
        orient = new OrientationEventListener(this, SensorManager.SENSOR_DELAY_NORMAL) {
            public void onOrientationChanged(int o) {
                if (o == ORIENTATION_UNKNOWN) return;
                final int a = ((o + 45) / 90 * 90) % 360;
                if (bg != null) bg.post(() -> debounceAngle(a));
            }
        };
        if (orient.canDetectOrientation()) orient.enable();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, 1);
        } else if (preview.isAvailable()) {
            openCamera();
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 1 && grantResults.length > 0
                && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
            if (preview.isAvailable()) openCamera(); // surface 還沒好則等 listener
            else log("相機權限已給，預覽準備中…");
        } else if (requestCode == 1) {
            log("需要相機權限才能預覽");
        }
    }

    void toggle() {
        if (!broadcasting) {
            broadcasting = true;
            // 開播按鈕文字由 updateLiveBadge 統一（（（•）） 開始直播／■ 停止直播）
            // 直播軟體規格：執行中螢幕恆亮（不暗不鎖），否則 Doze/屏息會拖死相機管線
            try { getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); }
            catch (Exception ignored) {}
            try {
                WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "iPhoneCam:stream");
                wifiLock.acquire();
            } catch (Exception ignored) {}
            startThermalWatch(); // 過熱自動降碼率（長時間直播保命）
            refreshConnIp(); // Wi-Fi 可能換過，重抓一次本機 IP
            startEncoder();
            startServer();
            startBeacon();
            log("\uD83D\uDD34 LIVE 廣播中…");
            updateLiveBadge();
        } else {
            broadcasting = false;
            streaming = false;
            // 關播按鈕文字由 updateLiveBadge 統一
            setConn(false, "");
            updateLiveBadge();
            try { getWindow().clearFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); }
            catch (Exception ignored) {}
            stopThermalWatch();
            stopAll();
            log("已停止");
        }
    }

    // ---------- 相機 ----------

    void openCamera() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) return;
        if (device != null) return; // 已經開了（權限回呼＋surface 回呼可能各調一次）
        String id = useFront && frontId != null ? frontId : backId;
        if (id == null) { log("找不到鏡頭"); return; }
        try {
            CameraCharacteristics cc = cameras.getCameraCharacteristics(id);
            Integer so = cc.get(CameraCharacteristics.SENSOR_ORIENTATION);
            sensorOrient = so != null ? so : 90;
            probeSensorCaps(cc); // 超採樣尺寸＋FPS 範圍（開鏡頭時算一次）
            updateRotation(true);
            cameras.openCamera(id, new CameraDevice.StateCallback() {
                public void onOpened(CameraDevice d) { device = d; createSession(); }
                public void onDisconnected(CameraDevice d) { d.close(); device = null; setStandby(true); }
                public void onError(CameraDevice d, int e) { d.close(); device = null; setStandby(true); log("相機錯誤 " + e); }
            }, bg);
        } catch (CameraAccessException | SecurityException e) {
            log("開相機失敗：" + e.getMessage());
        }
    }

    void restartCamera() {
        closeSession();
        if (device != null) { try { device.close(); } catch (Exception ignored) {} device = null; }
        openCamera();
    }

    void switchCamera() {
        useFront = !useFront;
        restartCamera();
        updateCamLabels();
    }

    void updateCamLabels() {
        final String t = useFront ? "前鏡頭 ＞" : "後鏡頭 ＞";
        runOnUiThread(() -> {
            if (camVal != null) camVal.setText(t);
        });
    }

    // ---------- 儀表板接線 ----------

    void bindDashboard() {
        try {
            liveBadge = findViewById(R.id.liveBadge);
            liveFps = findViewById(R.id.liveFps);
            connDot = findViewById(R.id.connDot);
            connTitle = findViewById(R.id.connTitle);
            connIp = findViewById(R.id.connIp);
            connSig = findViewById(R.id.connSig);
            resVal = findViewById(R.id.resVal);
            fpsVal = findViewById(R.id.fpsVal);
            camVal = findViewById(R.id.camVal);
            beautyVal = findViewById(R.id.beautyVal);
            eyeVal = findViewById(R.id.eyeVal);
            whiteVal = findViewById(R.id.whiteVal);
            faceVal = findViewById(R.id.faceVal);
            pupilVal = findViewById(R.id.pupilVal);
            bitrateVal = findViewById(R.id.bitrateVal);
            beautyPanel = findViewById(R.id.beautyPanel);
            funcCard = findViewById(R.id.funcCard);
            pageHome = findViewById(R.id.pageHome);
            pageSettings = findViewById(R.id.pageSettings);
            pageHelp = findViewById(R.id.pageHelp);
            navHome = findViewById(R.id.navHome);
            navSettings = findViewById(R.id.navSettings);
            navHelp = findViewById(R.id.navHelp);
            frameBox = findViewById(R.id.previewFrame);
            seekEye = findViewById(R.id.seekEye);
            seekWhite = findViewById(R.id.seekWhite);
            seekFace = findViewById(R.id.seekFace);
            seekPupil = findViewById(R.id.seekPupil);
            bitrateSeek = findViewById(R.id.bitrateSeek);
            if (frameBox != null) frameBox.addOnLayoutChangeListener(
                    (v, l, t, r, b, ol, ot, or, ob) -> {
                        // 框尺寸真變了才重算（layout 本身很吵，不擋會空轉）
                        int fw = frameBox.getWidth(), fh = frameBox.getHeight();
                        if (Math.abs(fw - lastFw) <= 2 && Math.abs(fh - lastFh) <= 2) return;
                        lastFw = fw;
                        lastFh = fh;
                        fitPreview(); // 視圖已是 match_parent，這裡只重算矩陣
                    });
            if (navHome != null) navHome.setOnClickListener(v -> showPage("home"));
            if (navSettings != null) navSettings.setOnClickListener(v -> showPage("settings"));
            if (navHelp != null) navHelp.setOnClickListener(v -> showPage("help"));
            android.view.View rr = findViewById(R.id.resRow);
            android.view.View fr = findViewById(R.id.fpsRow);
            android.view.View cr = findViewById(R.id.camRow);
            android.view.View br = findViewById(R.id.beautyRow);
            if (rr != null) rr.setOnClickListener(v -> cycleRes());
            if (fr != null) fr.setOnClickListener(v -> cycleFps());
            if (cr != null) cr.setOnClickListener(v -> switchCamera());
            if (br != null) br.setOnClickListener(v -> toggleBeautyPanel());
            bindSeek(seekEye, eyeVal, v -> beautyEye = v);
            bindSeek(seekWhite, whiteVal, v -> beautyWhite = v);
            bindSeek(seekFace, faceVal, v -> beautyFace = v);
            bindSeek(seekPupil, pupilVal, v -> beautyPupil = v);
            if (bitrateSeek != null) {
                bitrateSeek.setOnSeekBarChangeListener(new android.widget.SeekBar.OnSeekBarChangeListener() {
                    public void onProgressChanged(android.widget.SeekBar s, int p, boolean fromUser) {
                        if (bitrateVal != null) bitrateVal.setText((p + 1) + "M");
                    }
                    public void onStartTrackingTouch(android.widget.SeekBar s) {}
                    public void onStopTrackingTouch(android.widget.SeekBar s) {
                        vidMbps = s.getProgress() + 1;
                        abrBase = vidMbps; // 手動＝新天花板
                        abrStep = 0;
                        if (broadcasting) applyVideo(vidW, vidH, vidFps, vidMbps,
                                vidCodec.endsWith("avc") ? "h264" : "hevc");
                        else log("碼率設為 " + (int) vidMbps + "M（開播生效）");
                    }
                });
            }
            updateCamLabels();
            refreshResFpsLabels();
            refreshConnIp(); // 本機 IP 顯示（不用只寫「廣播中」）
            if (bitrateSeek != null) bitrateSeek.setProgress((int) vidMbps - 1);
        } catch (Exception e) {
            Log.i(TAG, "儀表板接線失敗：" + e);
        }
    }

    interface IntSet { void set(int v); }

    void bindSeek(android.widget.SeekBar sk, final TextView tv, final IntSet st) {
        if (sk == null) return;
        sk.setOnSeekBarChangeListener(new android.widget.SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(android.widget.SeekBar s, int p, boolean fromUser) {
                st.set(p);
                if (tv != null) tv.setText(String.valueOf(p));
                if (beautyVal != null) beautyVal.setText("自訂"); // 動過就不是「自然」了
            }
            public void onStartTrackingTouch(android.widget.SeekBar s) {}
            public void onStopTrackingTouch(android.widget.SeekBar s) {}
        });
    }

    void showPage(String which) {
        try {
            boolean home = "home".equals(which);
            boolean set = "settings".equals(which);
            if (pageHome != null) pageHome.setVisibility(home ? android.view.View.VISIBLE : android.view.View.GONE);
            if (pageSettings != null) pageSettings.setVisibility(set ? android.view.View.VISIBLE : android.view.View.GONE);
            if (pageHelp != null) pageHelp.setVisibility((!home && !set) ? android.view.View.VISIBLE : android.view.View.GONE);
            int pink = 0xFFFF5FD2, gray = 0xFF9AA3C0;
            if (navHome != null) navHome.setTextColor(home ? pink : gray);
            if (navSettings != null) navSettings.setTextColor(set ? pink : gray);
            if (navHelp != null) navHelp.setTextColor((!home && !set) ? pink : gray);
        } catch (Exception ignored) {}
    }

    // 輸出解析度檔位（w,h,名稱,建議碼率Mbps）：原生 4:3（CMOS 滿幅，上下不裁）／FPS 檔位（分開調整）
    static final int[][] RESTIERS = {{960, 720}, {1440, 1080}, {1920, 1440}, {2880, 2160}};
    static final String[] RES_NAMES = {"720P", "1080P", "2K", "4K"};
    static final double[] RES_BR = {4.0, 8.0, 16.0, 32.0};
    static final int[] FPSTIERS = {30, 60};

    void refreshResFpsLabels() {
        final String r = RES_NAMES[resIdx] + " ＞";
        final String f = FPSTIERS[fpsIdx] + "FPS ＞";
        final String lf = " " + RES_NAMES[resIdx] + " " + FPSTIERS[fpsIdx] + "FPS";
        runOnUiThread(() -> {
            if (resVal != null) resVal.setText(r);
            if (fpsVal != null) fpsVal.setText(f);
            if (liveFps != null) liveFps.setText(lf);
            if (bitrateSeek != null) bitrateSeek.setProgress(Math.max(0, Math.min(39, (int) vidMbps - 1)));
            if (bitrateVal != null) bitrateVal.setText(((int) vidMbps) + "M");
            fitPreview();
        });
    }

    void cycleRes() {
        resIdx = (resIdx + 1) % RESTIERS.length;
        vidMbps = RES_BR[resIdx]; // 換檔帶建議碼率（可在設定頁再調）
        refreshResFpsLabels();
        applyQuality();
    }

    void cycleFps() {
        fpsIdx = (fpsIdx + 1) % FPSTIERS.length;
        refreshResFpsLabels();
        applyQuality();
    }

    void applyQuality() {
        int w = RESTIERS[resIdx][0], h = RESTIERS[resIdx][1], fps = FPSTIERS[fpsIdx];
        // 使用者手動換檔＝新的嘗試：fallback 狀態歸零（連敗計數＋超採樣開關）
        sessFails = 0;
        overOff = false;
        // 2K 以上預設 HEVC（同畫質一半碼率，Wi-Fi 友善；不支援會走 fallback）
        String codec = resIdx >= 2 ? "hevc" : "h264";
        if (broadcasting) {
            applyVideo(w, h, fps, vidMbps, codec);
        } else {
            vidW = w; vidH = h; vidFps = fps;
            abrBase = vidMbps; // 還沒開播也同步天花板，否則 ABR 第一動就按舊檔位算
            abrStep = 0;
            log("輸出設為 " + RES_NAMES[resIdx] + " " + fps + "FPS " + codec + "（開播生效）");
        }
    }

    void toggleBeautyPanel() {
        runOnUiThread(() -> {
            if (beautyPanel == null) return;
            boolean opening = beautyPanel.getVisibility() != android.view.View.VISIBLE;
            beautyPanel.setVisibility(opening ? android.view.View.VISIBLE : android.view.View.GONE);
            // 美顏面板跟功能列高度互換：展開時收起功能列，開播鈕才不會被擠出螢幕
            if (funcCard != null)
                funcCard.setVisibility(opening ? android.view.View.GONE : android.view.View.VISIBLE);
        });
    }

    void updateLiveBadge() {
        final boolean live = broadcasting && hasVideo; // 徽章＝真有畫面，不只是開關開著（session 失敗就滅）
        final boolean cast = broadcasting;
        final String fps = " " + RES_NAMES[resIdx] + " " + FPSTIERS[fpsIdx] + "FPS";
        runOnUiThread(() -> {
            if (liveBadge != null)
                liveBadge.setVisibility(live ? android.view.View.VISIBLE : android.view.View.INVISIBLE);
            if (liveFps != null) liveFps.setText(fps);
            if (btnToggle != null) btnToggle.setText(cast ? "■ 停止直播" : "（（•）） 開始直播");
        });
    }

    void setConn(final boolean on, final String ip) {
        runOnUiThread(() -> {
            if (connTitle != null) connTitle.setText(on ? "已連接 PC" : "等待 PC 連接…");
            if (connIp != null) connIp.setText(on ? ("IP: " + ip) : ("本機 IP：" + localIp()));
            if (connDot != null) connDot.setTextColor(on ? 0xFF3DFF88 : 0xFF555577);
            // 訊號燈跟著連線走，不再是靜態擺設
            if (connSig != null) {
                connSig.setText(on ? "訊號良好" : "待機");
                connSig.setTextColor(on ? 0xFF3DFF88 : 0xFF555577);
            }
        });
    }

    /** 本機 IP 顯示（連線卡還沒連線時也看得到真實位址，方便手動連 IP）。 */
    void refreshConnIp() {
        runOnUiThread(() -> {
            if (connIp != null) connIp.setText("本機 IP：" + localIp());
        });
    }

    /** 待機圖：相機沒畫面時顯示（去背霓虹兔系圖），有串流自動藏起來。 */
    void setStandby(final boolean on) {
        runOnUiThread(() -> {
            if (standbyView != null)
                standbyView.setVisibility(on ? android.view.View.VISIBLE : android.view.View.GONE);
        });
    }

    /** 開鏡頭時探測：4:3 原生感測器尺寸表（超採樣候選，寬度上限 4096）＋AE FPS 範圍表。 */
    void probeSensorCaps(CameraCharacteristics cc) {
        overSizes = null;
        fpsRanges = null;
        try {
            android.hardware.camera2.params.StreamConfigurationMap map = cc.get(
                    CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            if (map != null) {
                android.util.Size[] sizes = map.getOutputSizes(ImageFormat.YUV_420_888);
                if (sizes == null || sizes.length == 0) sizes = map.getOutputSizes(ImageFormat.JPEG);
                if (sizes != null) {
                    java.util.ArrayList<int[]> cands = new java.util.ArrayList<>();
                    for (android.util.Size s : sizes) {
                        double ar = (double) s.getWidth() / s.getHeight();
                        if (Math.abs(ar - 4.0 / 3.0) > 0.05) continue; // 只要 4:3 原生（CMOS 滿幅；16:9 檔是裁切出來的）
                        if (s.getWidth() > 4096) continue; // 上限 4096（48MP 那類巨幅讀出太燒電）
                        cands.add(new int[]{s.getWidth(), s.getHeight()});
                    }
                    overSizes = cands;
                }
            }
            fpsRanges = cc.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
            log("感測器：4:3 原生檔 " + (overSizes != null ? overSizes.size() : 0) + " 個");
        } catch (Exception e) {
            log("感測器探測失敗：" + e.getMessage());
        }
    }

    void closeSession() {
        if (session != null) { try { session.close(); } catch (Exception ignored) {} session = null; }
        hasVideo = false;
        updateLiveBadge();
        closeOverReader();
        setStandby(true);
    }

    /** 超採樣 surface：取「輸出 2 倍像素以上、最小的那個」4:3 原生檔（夠用就好，再大就是燒電），
     * 逼 HAL 全片幅讀出，幀直接丟（acquireLatest 丟 backlog，只關不看）。
     * 沒有 2 倍檔時退而求其次：取比輸出大的最大檔（部分超採，仍比同尺寸讀出好）。 */
    void setupOverReader() {
        closeOverReader();
        try {
            overW = overH = 0;
            if (overSizes != null) {
                long need = (long) vidW * vidH * 2;
                int bestPx = Integer.MAX_VALUE;
                int bw = 0, bh = 0;
                long bpx = 0;
                for (int[] s : overSizes) {
                    long px = (long) s[0] * s[1];
                    if (s[0] <= vidW && s[1] <= vidH) continue; // 沒更大就沒意義
                    if (px > bpx) { bpx = px; bw = s[0]; bh = s[1]; } // 備胎：最大的更大檔
                    if (px < need) continue;
                    if (px < bestPx) { bestPx = (int) px; overW = s[0]; overH = s[1]; }
                }
                if (overW <= 0 && bpx > 0) {
                    overW = bw; overH = bh;
                    log("超採樣部分檔（不到 2 倍）：用最大 " + bw + "x" + bh);
                }
            }
            if (overW <= 0 || overH <= 0) return; // 無更大檔：直接用輸出尺寸，不超採
            overReader = ImageReader.newInstance(overW, overH, ImageFormat.YUV_420_888, 2);
            overReader.setOnImageAvailableListener(reader -> {
                android.media.Image img = null;
                try { img = reader.acquireLatestImage(); } catch (Exception ignored) {}
                if (img != null) { try { img.close(); } catch (Exception ignored) {} }
            }, bg);
            overSurface = overReader.getSurface();
            log("超採樣讀出 " + overW + "x" + overH + " → 輸出 " + vidW + "x" + vidH);
        } catch (Exception e) {
            log("超採樣啟動失敗：" + e.getMessage());
            closeOverReader();
        }
    }

    void closeOverReader() {
        try { if (overReader != null) overReader.close(); } catch (Exception ignored) {}
        overReader = null;
        overSurface = null;
    }

    /** 選 AE FPS 範圍：優先鎖 [fps,fps]（30fps 快門最慢 1/30s 吃滿曝光、亮度不跳；60 同理）。 */
    android.util.Range<Integer> pickFpsRange(int fps) {
        try {
            if (fpsRanges != null) {
                for (android.util.Range<Integer> r : fpsRanges) {
                    if (r.getLower() == fps && r.getUpper() == fps) return r;
                }
                android.util.Range<Integer> best = null;
                for (android.util.Range<Integer> r : fpsRanges) {
                    if (r.getLower() <= fps && fps <= r.getUpper()) {
                        if (best == null || (r.getUpper() - r.getLower()) < (best.getUpper() - best.getLower()))
                            best = r;
                    }
                }
                if (best != null) return best;
            }
        } catch (Exception ignored) {}
        return null;
    }

    void createSession() {
        if (device == null || !preview.isAvailable()) return;
        try {
            SurfaceTexture st = preview.getSurfaceTexture();
            st.setDefaultBufferSize(vidW, vidH);
            bufW = vidW; bufH = vidH; // 記住實際緩衝尺寸（矩陣只認這個，不認猜的）
            android.view.Surface previewSurface = new android.view.Surface(st);
            // 超採樣只在編碼時開（預覽不需要全片幅，省電）：encoder 存在＝要串流；
            // overOff＝此檔超採樣開不起來（session 失敗 fallback），直接關掉重試
            if (encoderSurface != null && !overOff) setupOverReader();
            else closeOverReader();
            List<android.view.Surface> targets = new ArrayList<>();
            targets.add(previewSurface);
            if (encoderSurface != null) targets.add(encoderSurface);
            if (overSurface != null) targets.add(overSurface);
            CaptureRequest.Builder req = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            req.addTarget(previewSurface);
            if (encoderSurface != null) req.addTarget(encoderSurface);
            if (overSurface != null) req.addTarget(overSurface);
            android.util.Range<Integer> fpsRange = pickFpsRange(vidFps);
            if (fpsRange != null) {
                req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fpsRange);
                log("快門鎖定 " + fpsRange.getLower() + "~" + fpsRange.getUpper() + "fps"
                        + (vidFps == 30 ? "（30fps 曝光最慢 1/30s）" : ""));
            }
            device.createCaptureSession(targets, new CameraCaptureSession.StateCallback() {
                public void onConfigured(CameraCaptureSession s) {
                    session = s;
                    sessFails = 0; // 開起來了，連敗計數歸零
                    hasVideo = true;
                    setStandby(false); // 有畫面了，待機圖退場
                    try {
                        s.setRepeatingRequest(req.build(), null, bg);
                        runOnUiThread(() -> {
                            fitPreview();
                            // 成功留痕：規格＋超採樣＋矩陣實測（視/buf/縮放），切檔後對答案用
                            log("預覽就緒 " + vidW + "x" + vidH
                                    + (overW > 0 ? "＋超採樣" + overW + "x" + overH : "（無超採樣）")
                                    + " " + fitInfo);
                            // 緩衝剛換尺寸，幀流穩定後再對一次（防過渡期量到舊尺寸）
                            try {
                                if (preview != null) preview.postDelayed(() -> fitPreview(), 500);
                            } catch (Exception ignored) {}
                        });
                    }
                    catch (CameraAccessException e) {
                        onSessionFailed("repeating 失敗：" + e.getMessage());
                    }
                    updateLiveBadge();
                }
                public void onConfigureFailed(CameraCaptureSession s) {
                    try { s.close(); } catch (Exception ignored) {}
                    session = null;
                    onSessionFailed("HAL 拒收此組合");
                }
            }, bg);
        } catch (CameraAccessException e) {
            onSessionFailed("createSession 例外：" + e.getMessage());
        }
    }

    /** session 開不起來（HAL 拒收／例外）：先關超採樣重試，再逐檔降
     * （4K→2K→1080P→720P→30fps），全滅才報錯。每一檔都走同一條鏈，
     * 所以每個解析度檔位都有救，不只 4K。 */
    void onSessionFailed(String why) {
        hasVideo = false;
        updateLiveBadge(); // 徽章滅（沒畫面不該亮 LIVE）
        setStandby(true); // 待機圖出來，別留黑底殘幀
        if (!broadcasting) { log("session 失敗（已關播，不重試）： " + why); return; }
        if (sessFails >= 4) { log("session 連敗多次，先停住（請手動降檔或重開）： " + why); return; }
        sessFails++;
        if (!overOff && overW > 0) {
            overOff = true;
            log("session 失敗，先關超採樣重試…（" + why + "）");
            try { bg.post(() -> createSession()); }
            catch (Exception e) { log("重試排程失敗：" + e.getMessage()); }
            return;
        }
        if (resIdx > 0) {
            resIdx--;
            overOff = false;
            vidMbps = RES_BR[resIdx];
            abrBase = vidMbps;
            abrStep = 0;
            refreshResFpsLabels();
            log("此解析度開不起來，自動降檔→" + RES_NAMES[resIdx] + "…（" + why + "）");
            applyVideo(RESTIERS[resIdx][0], RESTIERS[resIdx][1], FPSTIERS[fpsIdx], vidMbps,
                    vidCodec.endsWith("avc") ? "h264" : "hevc");
            return;
        }
        if (FPSTIERS[fpsIdx] > 30) {
            fpsIdx = 0;
            overOff = false;
            refreshResFpsLabels();
            log("降 FPS→30 重試…（" + why + "）");
            applyVideo(vidW, vidH, 30, vidMbps, vidCodec.endsWith("avc") ? "h264" : "hevc");
            return;
        }
        log("最低檔也開不起來，請重開 App 或換手機：（" + why + "）");
    }

    /** 閃退記錄：未捕捉例外先寫檔再交回系統；下次啟動狀態列顯示死因。 */
    void installCrashLog() {
        try {
            File dir = getExternalFilesDir(null);
            if (dir == null) return;
            final File crash = new File(dir, "crash.log");
            if (crash.exists()) {
                String msg = "上次閃退：";
                try {
                    java.io.BufferedReader br = new java.io.BufferedReader(new java.io.FileReader(crash));
                    String l1 = br.readLine(), l2 = br.readLine();
                    br.close();
                    if (l1 != null) msg += l1;
                    if (l2 != null) msg += "｜" + l2;
                    if (msg.length() > 200) msg = msg.substring(0, 200);
                } catch (Exception ignored) {}
                final String show = msg;
                runOnUiThread(() -> status.setText(show));
                crash.renameTo(new File(dir, "crash-last.log"));
            }
            final Thread.UncaughtExceptionHandler prev = Thread.getDefaultUncaughtExceptionHandler();
            Thread.setDefaultUncaughtExceptionHandler((t, e) -> {
                try {
                    FileWriter fw = new FileWriter(crash);
                    fw.write(e.toString() + "\n");
                    for (StackTraceElement s : e.getStackTrace()) fw.write("  at " + s + "\n");
                    fw.close();
                } catch (Exception ignored) {}
                if (prev != null) prev.uncaughtException(t, e);
            });
        } catch (Exception ignored) {}
    }

    // ---------- 陀螺儀方向 ----------

    /** 防抖：同一角度穩定 1 秒才真的切換（手持抖動不該狂發 video_params）。背景執行緒跑，不卡主執行緒。 */
    void debounceAngle(int a) {
        long now = SystemClock.uptimeMillis();
        if (a != rotCandidate) { rotCandidate = a; rotCandidateSince = now; return; }
        if (a != lastAngle && now - rotCandidateSince >= 1000) {
            lastAngle = a;
            updateRotation(false);
        }
    }

    /** 依裝置傾角算串流旋轉量。官方定義 OEL 90＝左側在上、270＝右側在上；
     * 對 CameraX sensor-90 表換算：後鏡 (sensor+角)、前鏡 (sensor-角)＋鏡像。前鏡待驗。 */
    void updateRotation(boolean force) {
        try {
            if (lastAngle < 0) lastAngle = 0;
            int r, f;
            if (useFront) { r = (sensorOrient - lastAngle + 360) % 360; f = 1; }
            else { r = (sensorOrient + lastAngle) % 360; f = 0; }
            if (!force && r == curRot && f == curFlip) return;
            curRot = r; curFlip = f;
            sendVideoParams(); // 沒連線時 clientOut==null，內部直接略過
            sendOrientation(); // 手機方向為主：方向權威訊息，PC 收到立即跟隨
            final int showR = r, showF = f;
            runOnUiThread(() -> {
                fitPreview();
                status.setText("方向 rot=" + showR + (showF == 1 ? " 鏡像" : ""));
            });
        } catch (Exception ignored) {}
    }

    /** 預覽自適應（2026-10-04 重寫）：視圖一律 match_parent 填滿畫框（版面系統保證），
     * 矩陣只用「實測視圖尺寸＋實際緩衝尺寸」現算 cover（取大縮放填滿＋置中，單一等比無拉伸）。
     * 不再手算 LayoutParams（時序一錯就壓扁／縮角，且無法自癒），全檔位通用。
     * 使用者裁示：本機預覽方向歸零、完全不轉（感測器原生方向）。
     * 送 PC 的 rot 照舊由陀螺儀決定（phone-master 協議不動）。 */
    void fitPreview() {
        if (preview == null) return;
        try {
            int vw = preview.getWidth(), vh = preview.getHeight();
            if (vw <= 0 || vh <= 0) return;
            int bw = bufW > 0 ? bufW : vidW, bh = bufH > 0 ? bufH : vidH;
            if (bw <= 0 || bh <= 0) return;
            float s = Math.max((float) vw / bw, (float) vh / bh);
            if (!(s > 0)) return;
            Matrix m = new Matrix();
            m.setScale(s, s);
            m.postTranslate((vw - bw * s) / 2f, (vh - bh * s) / 2f);
            preview.setTransform(m);
            fitInfo = "視" + vw + "x" + vh + "/buf" + bw + "x" + bh + "/x" + String.format("%.2f", s);
        } catch (Exception ignored) {}
    }

    void configureTransform(int viewW, int viewH) {
        fitPreview(); // 舊入口保留轉發（呼叫端陸續改過來）
    }

    // ---------- 編碼 ----------

    synchronized void startEncoder() {
        stopEncoder();
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat(vidCodec, vidW, vidH);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                    MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE, (int) (vidMbps * 1_000_000));
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, vidFps);
            // GOP 分檔：60fps 或 2K 以上用 1 秒（丟包最多 1 秒恢復）；其餘 2 秒（省碼率）
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, (vidFps >= 60 || vidW >= 2560) ? 1 : 2);
            try { fmt.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR); }
            catch (Exception ignored) {}
            if (Build.VERSION.SDK_INT >= 30) {
                try { fmt.setInteger("low-latency", 1); } catch (Exception ignored) {}
            }
            encoder = MediaCodec.createEncoderByType(vidCodec);
            encoder.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            encoderSurface = encoder.createInputSurface();
            encoder.start();
            suckCsd(); // 有些機器 csd 只在 output format，不發 codec-config buffer
            createSession(); // 把編碼 surface 接進 session
            streaming = true;
            encGen++; // 新一代編碼器（舊 drain 的重啟排程看到代號不同會作廢）
            drainThread = new Thread(this::drainLoop);
            drainThread.start();
            log("編碼 " + vidW + "x" + vidH + "@" + vidFps + " " + vidMbps + "Mbps");
        } catch (Exception e) {
            log("編碼啟動失敗：" + e.getMessage());
            sendControl("video_rejected", "{\"reason\":\"encoder\",\"fallback\":\"720p30 h264\"}");
        }
    }

    synchronized void stopEncoder() {
        streaming = false;
        hasVideo = false; // 編碼停＝沒畫面，徽章跟著滅
        updateLiveBadge();
        if (drainThread != null) { try { drainThread.join(500); } catch (Exception ignored) {} drainThread = null; }
        if (encoder != null) { try { encoder.stop(); } catch (Exception ignored) {} try { encoder.release(); } catch (Exception ignored) {} encoder = null; }
        if (encoderSurface != null) { try { encoderSurface.release(); } catch (Exception ignored) {} encoderSurface = null; }
        sps = pps = vps = null;
        spsLogged = false;
    }

    /** 從 output format 吸 csd-0/1/2（Exynos 等只放這裡，不發 buffer）。 */
    void suckCsd() {
        try {
            if (encoder == null) return;
            MediaFormat of = encoder.getOutputFormat();
            for (String k : new String[]{"csd-0", "csd-1", "csd-2"}) {
                if (!of.containsKey(k)) continue;
                ByteBuffer bb = of.getByteBuffer(k);
                if (bb == null || bb.remaining() < 2) continue;
                byte[] b = new byte[bb.remaining()];
                bb.get(b);
                parseCsd(b);
            }
        } catch (Exception ignored) {}
    }

    void drainLoop() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        final int myGen = encGen; // 這條 drain 屬於第幾代編碼器（set_video 重啟會換代，舊 drain 的重啟排程要作廢）
        long sent = 0, keys = 0;
        // 多加 encGen == myGen：換禎數/解析度時舊 drain 還卡在 dequeue 裡（stopEncoder 只等 500ms），
        // 睡醒若直接繼續，會跟新 drain 同時挖同一顆編碼器＝閃退。舊代直接退場。
        while (streaming && encoder != null && encGen == myGen) {
            try {
                int idx = encoder.dequeueOutputBuffer(info, 2000); // 10s→2s：停播/換檔時舊執行緒更快退場
                if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) { suckCsd(); continue; }
                if (idx < 0) continue;
                ByteBuffer buf = encoder.getOutputBuffer(idx);
                byte[] data = new byte[info.size];
                buf.get(data);
                boolean key = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                boolean cfg = (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                if (cfg) parseCsd(data); // 存 sps/pps/vps，不送出
                if (!cfg || key) { // 純設定不送；key（含設定）照送，避免少 key 餓死解碼器
                    // 沒人連就不送（省電省熱），但 buffer 照樣 release，編碼器才不會噎住
                    if (clientOut != null) {
                        byte[] avcc = data;
                        if (key && sps != null && pps != null) avcc = concat(seqHead(), data);
                        if (useUdp) {
                            udpSendFrame(avcc, key, !vidCodec.endsWith("avc"));
                        } else {
                            sendVideo(avcc);
                        }
                        sent++;
                        if (key) keys++;
                        if (sent % 300 == 0) log("已送 " + sent + " 幀 key=" + keys);
                    }
                }
                // 方向心跳：每 2 秒重申目前朝向（PC 即使漏掉變化也會自癒跟上）
                long nowMs = SystemClock.uptimeMillis();
                if (nowMs - lastOrientMs >= 2000) { lastOrientMs = nowMs; sendOrientation(); }
                encoder.releaseOutputBuffer(idx, false);
            } catch (Exception e) {
                log("drain 死：" + e);
                break;
            }
        }
        log("drain 結束（streaming=" + streaming + "）");
        // 只有同代才排重啟：舊代（換檔被取代）直接退場，不污染重試計數
        if (encGen == myGen) scheduleEncoderRestart(myGen);
    }

    int encRestartTries = 0;
    volatile int encGen = 0; // volatile：drain 迴圈每圈都要看到最新代號

    /** drain 莫名死亡（長時間直播最怕這個）：只要還在廣播，就退避重啟編碼器，不靜默停播。
     * 注意不能在 drain 執行緒裡直接調 startEncoder（裡面會 join 自己＝死結），丟給 bg 做。 */
    void scheduleEncoderRestart(final int gen) {
        if (!broadcasting) return;
        encRestartTries++;
        final int waitMs = Math.min(30000, 1000 * encRestartTries);
        final int n = encRestartTries;
        log("編碼重啟排程（第 " + n + " 次，" + waitMs + "ms 後）");
        try {
            bg.postDelayed(() -> {
                try {
                    // 已經有人重啟過（set_video／新一代）或關播了，就作廢
                    if (!broadcasting || encGen != gen) return;
                    startEncoder();
                    if (streaming) { encRestartTries = 0; log("編碼重啟成功"); }
                } catch (Exception e) {
                    log("編碼重啟失敗：" + e.getMessage());
                    scheduleEncoderRestart(gen);
                }
            }, waitMs);
        } catch (Exception ignored) {}
    }

    /** 解析 codec-config：三種格式都吃（實機行為不一）。
     * ① Annex-B（00 00 00 01 開頭，Exynos 常見）② avcC/hevcC 盒裝 ③ 單顆裸 NAL。
     * sps/pps/vps 分次到達會累積，湊齊才送 codec_config。 */
    void parseCsd(byte[] csd) {
        try {
            if (csd == null || csd.length < 2) return;
            if (startsWith(csd, 0, 0, 0, 1) || startsWith(csd, 0, 0, 1)) {
                for (byte[] u : splitAnnexB(csd)) classifyNal(u);
            } else if ((csd[0] & 0xFF) == 1 && csd.length > 8) {
                parseBoxed(csd);
            } else {
                classifyNal(csd); // 單顆裸 NAL（如 67 64 00 1F… 開頭的 SPS）
            }
            if (sps != null && pps != null) sendCodecConfig();
        } catch (Exception e) {
            log("csd 解析失敗：" + e.getMessage());
        }
    }

    static boolean startsWith(byte[] b, int... pre) {
        if (b.length < pre.length) return false;
        for (int i = 0; i < pre.length; i++) if ((b[i] & 0xFF) != pre[i]) return false;
        return true;
    }

    /** Annex-B 按起始碼切分（不含起始碼）。 */
    static List<byte[]> splitAnnexB(byte[] b) {
        List<byte[]> out = new ArrayList<>();
        int start = -1;
        for (int i = 0; i + 3 < b.length; i++) {
            int sc = (b[i] == 0 && b[i + 1] == 0)
                    ? (b[i + 2] == 1 ? 3 : (b[i + 2] == 0 && b[i + 3] == 1 ? 4 : 0)) : 0;
            if (sc > 0) {
                if (start >= 0 && i > start) out.add(java.util.Arrays.copyOfRange(b, start, i));
                start = i + sc;
                i += sc - 1;
            }
        }
        if (start >= 0 && start < b.length) out.add(java.util.Arrays.copyOfRange(b, start, b.length));
        return out;
    }

    /** NAL 類型分流：H264(7/8)／HEVC(32/33/34)。 */
    void classifyNal(byte[] u) {
        if (u == null || u.length < 2) return;
        if (vidCodec.endsWith("avc")) {
            int t = u[0] & 0x1F;
            if (t == 7 && sps == null) sps = u;
            else if (t == 8 && pps == null) pps = u;
        } else {
            int t = ((u[0] & 0xFF) >> 1) & 0x3F;
            if (t == 32 && vps == null) vps = u;
            else if (t == 33 && sps == null) sps = u;
            else if (t == 34 && pps == null) pps = u;
        }
    }

    /** avcC/hevcC 盒裝解析（帶邊界檢查）。 */
    void parseBoxed(byte[] csd) {
        if (vidCodec.endsWith("avc")) {
            int nSps = csd[5] & 0x1F, p = 6;
            for (int k = 0; k < nSps && p + 2 <= csd.length; k++) {
                int len = ((csd[p] & 0xFF) << 8) | (csd[p + 1] & 0xFF);
                if (len <= 0 || p + 2 + len > csd.length) break;
                if (sps == null) { sps = new byte[len]; System.arraycopy(csd, p + 2, sps, 0, len); }
                p += 2 + len;
            }
            if (p >= csd.length) return;
            int nPps = csd[p++] & 0xFF;
            for (int k = 0; k < nPps && p + 2 <= csd.length; k++) {
                int len = ((csd[p] & 0xFF) << 8) | (csd[p + 1] & 0xFF);
                if (len <= 0 || p + 2 + len > csd.length) break;
                if (pps == null) { pps = new byte[len]; System.arraycopy(csd, p + 2, pps, 0, len); }
                p += 2 + len;
            }
        } else {
            int p = 22;
            if (csd.length < 23) return;
            int arrays = csd[21] & 0xFF;
            for (int a = 0; a < arrays && p + 3 < csd.length; a++) {
                int naluType = csd[p] & 0x3F; p += 3;
                if (p + 1 >= csd.length) break;
                int cnt = ((csd[p - 1] & 0xFF) << 8) | (csd[p] & 0xFF); p++;
                for (int k = 0; k < cnt && p + 2 < csd.length; k++) {
                    int len = ((csd[p] & 0xFF) << 8) | (csd[p + 1] & 0xFF);
                    if (len <= 0 || p + 2 + len > csd.length) break;
                    byte[] u = new byte[len];
                    System.arraycopy(csd, p + 2, u, 0, len);
                    if (naluType == 32 && vps == null) vps = u;
                    else if (naluType == 33 && sps == null) sps = u;
                    else if (naluType == 34 && pps == null) pps = u;
                    p += 2 + len;
                }
            }
        }
    }

    static byte[] annex(byte[] nal) {
        byte[] out = new byte[nal.length + 4];
        out[0] = 0; out[1] = 0; out[2] = 0; out[3] = 1;
        System.arraycopy(nal, 0, out, 4, nal.length);
        return out;
    }

    static byte[] concat(byte[]... parts) {
        int n = 0;
        for (byte[] p : parts) n += p.length;
        byte[] out = new byte[n];
        int o = 0;
        for (byte[] p : parts) { System.arraycopy(p, 0, out, o, p.length); o += p.length; }
        return out;
    }

    // ---------- 過熱保護 ----------

    Object thermalListener = null;

    /** 長時間直播：系統過熱（SEVERE 以上）就自降碼率到 2Mbps 續命，單向（不自動升回，避免震盪）。 */
    void startThermalWatch() {
        try {
            if (Build.VERSION.SDK_INT < 29) return;
            android.os.PowerManager pm = (android.os.PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm == null) return;
            stopThermalWatch();
            android.os.PowerManager.OnThermalStatusChangedListener l =
                    new android.os.PowerManager.OnThermalStatusChangedListener() {
                        public void onThermalStatusChanged(int status) {
                            // 4=SEVERE 5=CRITICAL 6=EMERGENCY
                            if (status >= 4 && vidMbps > 2.0) {
                                vidMbps = 2.0;
                                log("過熱降碼率到 2Mbps（要全速請重開廣播）");
                                runOnUiThread(() -> {
                                    if (bitrateSeek != null) bitrateSeek.setProgress(1);
                                    if (bitrateVal != null) bitrateVal.setText("2M");
                                });
                                try { bg.post(() -> applyVideo(vidW, vidH, vidFps, vidMbps, vidCodec)); }
                                catch (Exception ignored) {}
                            }
                        }
                    };
            thermalListener = l;
            pm.addThermalStatusListener(getMainExecutor(), l);
        } catch (Exception ignored) {}
    }

    void stopThermalWatch() {
        try {
            if (Build.VERSION.SDK_INT < 29 || thermalListener == null) return;
            android.os.PowerManager pm = (android.os.PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null) pm.removeThermalStatusListener(
                    (android.os.PowerManager.OnThermalStatusChangedListener) thermalListener);
        } catch (Exception ignored) {}
        thermalListener = null;
    }

    // ---------- CAM1（手機＝伺服器） ----------

    void startServer() {
        serverThread = new Thread(() -> {
            Log.i(TAG, "server 執行緒啟動，bind " + CAM_PORT);
            try (ServerSocket srv = new ServerSocket(CAM_PORT)) {
                serverSocket = srv;
                Log.i(TAG, "server 已監聽 " + srv.getLocalSocketAddress());
                while (broadcasting) {
                    Socket c = srv.accept();
                    try { c.setTcpNoDelay(true); } catch (Exception ignored) {} // 小 NAL 別被 Nagle 拖
                    closeClient();
                    clientSocket = c;
                    clientIp = c.getInetAddress().getHostAddress();
                    lastPcIp = clientIp;
                    clientOut = c.getOutputStream();
                    InputStream in = c.getInputStream();
                    runOnUiThread(() -> log("PC 連入：" + clientIp));
                    setConn(true, clientIp);
                    sendControl("hello", "{\"app\":\"iPhoneCam-Android\",\"version\":\"v4\",\"device\":\"" + Build.MODEL + "\"}");
                    sendVideoParams();
                    if (sps != null) sendCodecConfig();
                    try {
                        while (broadcasting && !c.isClosed()) {
                            byte[] hdr = readN(in, 17);
                            int len = ((hdr[13] & 0xFF) << 24) | ((hdr[14] & 0xFF) << 16)
                                    | ((hdr[15] & 0xFF) << 8) | (hdr[16] & 0xFF);
                            byte[] pl = len > 0 ? readN(in, len) : new byte[0];
                            if (hdr[4] == T_CONTROL) onControl(new String(pl, "UTF-8"));
                        }
                    } catch (Exception ignored) {}
                    final String goneIp = clientIp;
                    closeClient();
                    runOnUiThread(() -> log("PC 斷線：" + goneIp));
                    setConn(false, "");
                }
            } catch (IOException e) {
                Log.i(TAG, "server 監聽失敗：" + e);
                runOnUiThread(() -> log("監聽失敗：" + e.getMessage()));
            } catch (Throwable t) {
                Log.i(TAG, "server 非 IO 死亡：" + t);
            }
            Log.i(TAG, "server 執行緒結束");
        });
        serverThread.start();
    }

    void onControl(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String kind = o.optString("kind");
            if ("set_video".equals(kind)) {
                int w = o.optInt("width", vidW), h = o.optInt("height", vidH);
                int fps = o.optInt("fps", vidFps);
                double br = o.optDouble("bitrate_mbps", vidMbps);
                String codec = o.optString("codec", "h264");
                sessFails = 0; // PC/手動指定＝新的嘗試，fallback 狀態歸零
                overOff = false;
                applyVideo(w, h, fps, br, codec);
            } else if ("set_image".equals(kind)) {
                // PC 特效濾鏡頁的美顏滑桿：參數收下顯示（美顏引擎 v0.2，收到先記住）
                try {
                    org.json.JSONObject bb = o.optJSONObject("beauty");
                    if (bb != null) {
                        beautyEye = bb.optInt("eye", beautyEye);
                        beautyWhite = bb.optInt("white", beautyWhite);
                        beautyFace = bb.optInt("face", beautyFace);
                        beautyPupil = bb.optInt("pupil", beautyPupil);
                        final int e = beautyEye, w2 = beautyWhite, f2 = beautyFace, p2 = beautyPupil;
                        runOnUiThread(() -> {
                            if (seekEye != null) seekEye.setProgress(e);
                            if (seekWhite != null) seekWhite.setProgress(w2);
                            if (seekFace != null) seekFace.setProgress(f2);
                            if (seekPupil != null) seekPupil.setProgress(p2);
                            if (beautyVal != null) beautyVal.setText("自訂");
                            if (eyeVal != null) eyeVal.setText(String.valueOf(e));
                            if (whiteVal != null) whiteVal.setText(String.valueOf(w2));
                            if (faceVal != null) faceVal.setText(String.valueOf(f2));
                            if (pupilVal != null) pupilVal.setText(String.valueOf(p2));
                        });
                        log("PC 美顏參數：眼" + e + " 白" + w2 + " 臉" + f2 + " 瞳" + p2 + "（引擎開發中，先記住）");
                    }
                } catch (Exception ignored) {}
            } else if ("udp_ready".equals(kind)) {
                // PC 準備好收 UDP：host 由 PC 明確告知（adb 轉發時 TCP 對端是假位址不可信）
                String rip = o.optString("host", "");
                if (rip == null || rip.length() < 7) rip = clientIp;
                if (rip == null || rip.length() < 7) rip = lastPcIp;
                int rport = o.optInt("port", UDP_PORT);
                if (rip != null && rip.length() >= 7) udpSetup(rip, rport);
            } else if ("udp_lost".equals(kind)) {
                udpDown(); // PC 側收不到 UDP，切回 TCP（不斷線）
            } else if ("nack".equals(kind)) {
                try {
                    org.json.JSONArray arr = o.optJSONArray("seqs");
                    if (arr != null) {
                        int[] seqs = new int[arr.length()];
                        for (int i = 0; i < arr.length(); i++) seqs[i] = arr.optInt(i);
                        udpResend(seqs);
                    }
                } catch (Exception ignored) {}
            } else if ("net_state".equals(kind)) {
                onNetState(o.optDouble("loss", 0), o.optInt("jitterMs", 0));
            } else if ("idr_request".equals(kind)) {
                log("收到 IDR 要求");
                requestIdr();
            }
        } catch (Exception ignored) {}
    }

    // ABR 碼率階梯（PC 回報 loss/jitter 後走）：以檔位建議值為頂，只降不爆
    double abrBase = 4.0;
    int abrStep = 0; // 0=頂 1=-25% 2=-50% 3=-65%
    int abrGoodStreak = 0;

    /** PC 回授網路狀態→走碼率階梯（每秒一次，由 PC 推）。爛線降＋要 IDR，順線慢慢爬回。 */
    void onNetState(double loss, int jitterMs) {
        try {
            if (!broadcasting || !streaming) return;
            boolean bad = loss > 0.10 || jitterMs > 150;
            boolean okk = loss < 0.02 && jitterMs < 40;
            if (bad) {
                abrGoodStreak = 0;
                if (abrStep < 3) {
                    abrStep++;
                    applyBitrate();
                }
                if (loss > 0.20) requestIdr();
            } else if (okk) {
                if (++abrGoodStreak >= 5 && abrStep > 0) {
                    abrGoodStreak = 0;
                    abrStep--;
                    applyBitrate();
                }
            } else {
                abrGoodStreak = 0;
            }
        } catch (Exception ignored) {}
    }

    static final double[] ABR_MUL = {1.0, 0.75, 0.5, 0.35};

    /** 線上改碼率（不斷編碼器；多數 SoC 吃 setParameters 即時生效）。 */
    void applyBitrate() {
        try {
            double br = abrBase * ABR_MUL[Math.max(0, Math.min(3, abrStep))];
            if (encoder == null) return;
            Bundle b = new Bundle();
            b.putInt(MediaFormat.KEY_BIT_RATE, (int) (br * 1_000_000));
            encoder.setParameters(b);
            vidMbps = br;
            log("ABR 碼率→" + br + "Mbps（階" + abrStep + "）");
        } catch (Exception e) {
            log("ABR 改碼率失敗：" + e.getMessage());
        }
    }

    void requestIdr() {
        try {
            if (encoder == null) return;
            Bundle b = new Bundle();
            b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
            encoder.setParameters(b);
        } catch (Exception ignored) {}
    }

    synchronized void applyVideo(int w, int h, int fps, double br, String codec) {
        String mime = "hevc".equalsIgnoreCase(codec) ? "video/hevc" : "video/avc";
        boolean ok = true;
        String saveCodec = vidCodec;
        int sw = vidW, sh = vidH, sf = vidFps;
        double sb = vidMbps;
        vidCodec = mime; vidW = w; vidH = h; vidFps = fps; vidMbps = br;
        try {
            startEncoder(); // 重啟編碼器＋session
        } catch (Exception e) { ok = false; }
        if (!ok || !streaming) {
            vidCodec = saveCodec; vidW = sw; vidH = sh; vidFps = sf; vidMbps = sb;
            try { startEncoder(); } catch (Exception ignored) {}
            sendControl("video_rejected", "{\"reason\":\"unsupported\",\"fallback\":\"720p30 h264\"}");
        } else {
            sendVideoParams();
            refreshQualityCustom();
            abrBase = br; // 新檔位＝新天花板，階梯歸零
            abrStep = 0;
            abrGoodStreak = 0;
        }
    }

    void refreshQualityCustom() {
        // PC 下的 set_video：檔位對得上就吸附，否則顯示自訂 WxH
        for (int i = 0; i < RESTIERS.length; i++) {
            if (RESTIERS[i][0] == vidW && RESTIERS[i][1] == vidH) resIdx = i;
        }
        for (int i = 0; i < FPSTIERS.length; i++) {
            if (FPSTIERS[i] == vidFps) fpsIdx = i;
        }
        refreshResFpsLabels();
    }

    void sendVideoParams() {
        sendControl("video_params", "{\"width\":" + vidW + ",\"height\":" + vidH
                + ",\"fps\":" + vidFps + ",\"codec\":\"" + (vidCodec.endsWith("avc") ? "h264" : "hevc")
                + "\",\"rot\":" + Math.max(curRot, 0) + ",\"flip\":" + Math.max(curFlip, 0) + "}");
    }

    /** 方向權威訊息：手機端目前畫面該顯示的正確朝向（順時針度數＋是否鏡像）。
     * 旋轉變化時發一次＋串流中心跳每 2 秒一次，PC 以此為主跟隨顯示。 */
    void sendOrientation() {
        sendControl("orientation", "{\"rot\":" + Math.max(curRot, 0)
                + ",\"flip\":" + Math.max(curFlip, 0) + "}");
    }

    /** 序列頭（HEVC 含 VPS），每顆 keyframe 前綴＋codec_config 內容。 */
    byte[] seqHead() {
        if (vps != null && !vidCodec.endsWith("avc")) return concat(annex(vps), annex(sps), annex(pps));
        return concat(annex(sps), annex(pps));
    }

    boolean spsLogged = false;

    void sendCodecConfig() {
        if (sps == null || pps == null) return;
        byte[] head = seqHead();
        // sps 欄位塞整個序列頭（接收端直接寫檔／餵解碼器），pps 照送保持相容
        sendControl("codec_config", "{\"sps\":\"" + Base64.encodeToString(head, Base64.NO_WRAP)
                + "\",\"pps\":\"" + Base64.encodeToString(pps, Base64.NO_WRAP) + "\"}");
        if (!spsLogged) { spsLogged = true; log("SPS 就緒 sps=" + sps.length + " pps=" + pps.length); }
    }

    void sendVideo(byte[] avcc) {
        OutputStream o = clientOut;
        if (o == null || avcc.length == 0) return;
        try {
            synchronized (this) {
                if (clientOut == null) return;
                writeFrame(clientOut, T_VIDEO, avcc);
            }
        } catch (IOException e) {
            closeClient();
        }
    }

    synchronized void sendControl(String kind, String jsonBody) {
        if (clientOut == null) return;
        try {
            String json = jsonBody.trim();
            if (json.startsWith("{")) json = json.substring(0, json.length() - 1) + ",\"kind\":\"" + kind + "\"}";
            else json = "{\"kind\":\"" + kind + "\"}";
            writeFrame(clientOut, T_CONTROL, json.getBytes("UTF-8"));
        } catch (IOException e) {
            closeClient();
        }
    }

    static void writeFrame(OutputStream o, byte type, byte[] payload) throws IOException {
        long ts = System.nanoTime() / 1000L;
        o.write(new byte[]{(byte) 0x43, (byte) 0x41, (byte) 0x4D, (byte) 0x31, type,
                (byte) (ts >>> 56), (byte) (ts >>> 48), (byte) (ts >>> 40), (byte) (ts >>> 32),
                (byte) (ts >>> 24), (byte) (ts >>> 16), (byte) (ts >>> 8), (byte) ts,
                (byte) (payload.length >>> 24), (byte) (payload.length >>> 16),
                (byte) (payload.length >>> 8), (byte) payload.length});
        o.write(payload);
        o.flush();
    }

    static byte[] readN(InputStream in, int n) throws IOException {
        byte[] b = new byte[n];
        int got = 0;
        while (got < n) {
            int r = in.read(b, got, n - got);
            if (r < 0) throw new IOException("closed");
            got += r;
        }
        return b;
    }

    void closeClient() {
        try { if (clientSocket != null) clientSocket.close(); } catch (Exception ignored) {}
        clientSocket = null;
        clientOut = null;
        useUdp = false; // 斷線回 TCP 預設，下個客戶端重新 udp_ready
    }

    // ---------- UDP 即時影像（CUM1：Cam Udp Media，無線最優解） ----------
    // 格式（全大端）：[MAGIC 4B 0x43554D31][seq u32][tsUs u64][flags 1B][fragIdx u16][fragTotal u16][payload≤1400B]
    // flags: bit0=keyframe bit1=hevc。payload＝跟 TCP 一樣的 AVCC 訊框（PC 端下游不用改）。
    // 控制走既有 TCP：PC→手機 `udp_ready{port}` 開 UDP、`udp_lost` 切回 TCP、`nack{seqs}` 要重傳；
    // 3 秒收不到 UDP，PC 會送 udp_lost（手機自動回 TCP，不斷線）。
    static final int UDP_MAGIC = 0x43554D31;
    static final int UDP_PORT = 19302;
    static final int UDP_MTU = 1400;
    DatagramSocket udpSock = null;
    InetAddress udpAddr = null;
    int udpPort = UDP_PORT;
    volatile boolean useUdp = false;
    int udpSeq = 0;
    // 重傳快取（最近 32 幀 AVCC，NACK 來了重發；約幾百 KB）
    final java.util.LinkedHashMap<Integer, byte[]> udpCache =
            new java.util.LinkedHashMap<Integer, byte[]>(40, 0.75f, true) {
                protected boolean removeEldestEntry(java.util.Map.Entry<Integer, byte[]> e) {
                    return size() > 32;
                }
            };

    synchronized void udpSetup(String ip, int port) {
        try {
            if (udpSock == null || udpSock.isClosed()) udpSock = new DatagramSocket();
            udpAddr = InetAddress.getByName(ip);
            udpPort = port > 0 ? port : UDP_PORT;
            synchronized (udpCache) { udpCache.clear(); }
            useUdp = true;
            log("UDP 影像啟動：" + ip + ":" + udpPort);
        } catch (Exception e) {
            log("UDP 啟動失敗（續用 TCP）：" + e.getMessage());
            useUdp = false;
        }
    }

    void udpSendFrame(byte[] avcc, boolean key, boolean hevc) {
        DatagramSocket ds = udpSock;
        InetAddress addr = udpAddr;
        if (ds == null || ds.isClosed() || addr == null) return;
        try {
            int seq;
            synchronized (this) { seq = udpSeq++; }
            synchronized (udpCache) { udpCache.put(seq, avcc); }
            int total = (avcc.length + UDP_MTU - 1) / UDP_MTU;
            if (total < 1) total = 1;
            if (total > 600) return; // 單幀超過 ~840KB 不合理，丟（防爆）
            long ts = System.nanoTime() / 1000L;
            int flags = (key ? 1 : 0) | (hevc ? 2 : 0);
            for (int i = 0; i < total; i++) {
                int off = i * UDP_MTU;
                int len = Math.min(UDP_MTU, avcc.length - off);
                java.nio.ByteBuffer b = java.nio.ByteBuffer.allocate(21 + len);
                b.order(java.nio.ByteOrder.BIG_ENDIAN);
                b.putInt(UDP_MAGIC);
                b.putInt(seq);
                b.putLong(ts);
                b.put((byte) flags);
                b.putShort((short) i);
                b.putShort((short) total);
                b.put(avcc, off, len);
                byte[] pkt = b.array();
                ds.send(new DatagramPacket(pkt, pkt.length, addr, udpPort));
            }
        } catch (Exception ignored) {}
    }

    void udpResend(int[] seqs) {
        if (udpSock == null || udpAddr == null) return;
        try {
            for (int seq : seqs) {
                byte[] avcc;
                synchronized (udpCache) { avcc = udpCache.get(seq); }
                if (avcc == null) continue;
                // 重傳不進快取（避免污染排序）、不佔 seq
                int total = (avcc.length + UDP_MTU - 1) / UDP_MTU;
                if (total < 1) total = 1;
                if (total > 600) continue;
                long ts = System.nanoTime() / 1000L;
                for (int i = 0; i < total; i++) {
                    int off = i * UDP_MTU;
                    int len = Math.min(UDP_MTU, avcc.length - off);
                    java.nio.ByteBuffer b = java.nio.ByteBuffer.allocate(21 + len);
                    b.order(java.nio.ByteOrder.BIG_ENDIAN);
                    b.putInt(UDP_MAGIC);
                    b.putInt(seq);
                    b.putLong(ts);
                    b.put((byte) 0);
                    b.putShort((short) i);
                    b.putShort((short) total);
                    b.put(avcc, off, len);
                    byte[] pkt = b.array();
                    udpSock.send(new DatagramPacket(pkt, pkt.length, udpAddr, udpPort));
                }
            }
        } catch (Exception ignored) {}
    }

    void udpDown() {
        useUdp = false;
        log("UDP 斷流，切回 TCP（不斷線）");
    }

    // ---------- UDP 信標 ----------

    /** hevc 支不支援（一輩子不會變，探一次就好；之前每 2 秒建兩個編碼器探測，白燒電）。 */
    Boolean hevcCached = null;

    void startBeacon() {
        beaconThread = new Thread(() -> {
            try {
                DatagramSocket ds = new DatagramSocket();
                ds.setBroadcast(true);
                String model = Build.MODEL;
                while (broadcasting) {
                    try {
                        String ip = localIp();
                        if (hevcCached == null) {
                            boolean hevc = false;
                            try {
                                for (String t : new String[]{"video/hevc", "video/avc"}) {
                                    MediaCodec.createEncoderByType(t).release();
                                    if (t.endsWith("hevc")) hevc = true;
                                }
                            } catch (Exception ignored) {}
                            hevcCached = hevc;
                        }
                        boolean hevc = hevcCached;
                        JSONObject o = new JSONObject();
                        o.put("kind", "beacon");
                        o.put("name", model);
                        o.put("ip", ip);
                        o.put("port", CAM_PORT);
                        org.json.JSONArray arr = new org.json.JSONArray();
                        arr.put("h264");
                        if (hevc) arr.put("hevc");
                        o.put("codec", arr);
                        // 能力跟著目前檔位走（PC 清單顯示用；之前寫死 720p30 是錯的）
                        o.put("max", RES_NAMES[resIdx] + FPSTIERS[fpsIdx]);
                        byte[] b = o.toString().getBytes("UTF-8");
                        ds.send(new DatagramPacket(b, b.length,
                                InetAddress.getByName("255.255.255.255"), BEACON_PORT));
                        // 家用 AP 的 WiFi 廣播常掉包：已知 PC 就補一發單播（同埠，PC 照收）
                        try {
                            String pc = lastPcIp;
                            if (pc != null && pc.length() > 7 && pc.indexOf(':') < 0)
                                ds.send(new DatagramPacket(b, b.length,
                                        InetAddress.getByName(pc), BEACON_PORT));
                        } catch (Exception ignored) {}
                    } catch (Exception ignored) {}
                    Thread.sleep(2000);
                }
                ds.close();
            } catch (Exception ignored) {}
        });
        beaconThread.start();
    }

    static String localIp() {
        try {
            Enumeration<NetworkInterface> nis = NetworkInterface.getNetworkInterfaces();
            for (NetworkInterface ni : Collections.list(nis)) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (java.net.InetAddress a : Collections.list(ni.getInetAddresses())) {
                    if (a.isSiteLocalAddress() && a.getHostAddress() != null
                            && a.getHostAddress().indexOf(':') < 0) return a.getHostAddress();
                }
            }
        } catch (Exception ignored) {}
        return "0.0.0.0";
    }

    void stopAll() {
        closeClient();
        useUdp = false;
        try { if (udpSock != null) udpSock.close(); } catch (Exception ignored) {}
        udpSock = null;
        stopEncoder();
        closeSession();
        stopThermalWatch();
        // 關監聽 socket 才能叫醒卡在 accept 的執行緒，否則重開會 EADDRINUSE
        try { if (serverSocket != null) serverSocket.close(); } catch (Exception ignored) {}
        serverSocket = null;
        if (serverThread != null) { try { serverThread.join(500); } catch (Exception ignored) {} serverThread = null; }
        try { if (wifiLock != null && wifiLock.isHeld()) wifiLock.release(); } catch (Exception ignored) {}
        wifiLock = null;
    }

    void log(String s) {
        Log.i(TAG, s);
        runOnUiThread(() -> status.setText(s));
    }

    @Override
    protected void onDestroy() {
        broadcasting = false;
        try { if (orient != null) orient.disable(); } catch (Exception ignored) {}
        stopAll();
        if (bgThread != null) bgThread.quitSafely();
        super.onDestroy();
    }
}

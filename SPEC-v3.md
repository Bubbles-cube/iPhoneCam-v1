# iPhoneCam 規格定稿 v3（A 計畫：原生雙端）

> 定稿日：2026-10-01｜PC：Windows 11｜手機：iPhone 16 Pro Max（USB3 Gen2）｜Mac：暫無，iOS 端借 Mac 時再實作
> 目標：iPhone 鏡頭當 PC 攝像頭，裝 PC 軟體後多一顆虛擬攝像頭，OBS / Discord 直接選來用。

## 1. 系統架構

```
iPhone App（Swift，原生）
  AVCapture → 輕美顏 → VideoToolbox 硬編（H.264/HEVC）→ TCP 送出
      ↓ Wi-Fi（同區網）或 USB（usbmuxd 轉發，個人熱點 USB 亦可）
PC App（C#，Win11，控制大腦＋重活全收）
  發現裝置 → 收串流 → 硬解 → D3D 重濾鏡 → MF 虛擬攝像頭
      ↓
  OBS / Discord 選「iPhoneCam」當攝影機
```

分工一句話：**手機是攝影機＋編碼器，PC 是後製機＋大腦**。編碼只能在手機做（原始 4K60 約 6Gbps，不編碼傳不出來），其餘全放 PC。

## 2. 影像檔位（預設 1080p30，4K60 是最高檔不是預設）

| 檔位 | 解析度/幀率 | 編碼 | 碼率 | 用途 |
|---|---|---|---|---|
| 流暢 | 720p30 | H.264 | 4Mbps | Discord、會議預設 |
| 標準 | 1080p30 | H.264 | 8Mbps | OBS 預設 |
| 高畫質 | 1080p60 | H.264/HEVC | 12Mbps | 直播常用 |
| 超高畫質 | 4K30 | HEVC 優先 | 20Mbps | 錄影、特寫 |
| 旗艦 | 4K60 | HEVC 優先 | 30Mbps | 選配，過熱/網路差自動提示降檔 |

過熱或連續掉幀時 PC 提示降檔（4K60→4K30→1080p60），**只提示不自動降**，由使用者決定。

## 3. 碼率政策（自訂 0.5~400Mbps＋上限擋板）

* PC 端滑桿＋數字輸入＋「自動」檔。
* 按線路設上限（交握 `link_info` 後取雙方最低值，超過擋下並說明原因）：
  * Wi-Fi：上限 100Mbps（使用者家為 Wi-Fi 6 AX3000，5GHz 實測穩定吞吐數百 Mbps，30Mbps 的 4K60 只佔一成餘裕充足；擁塞時靠 `stats` 動態提示降檔）
  * USB2（含 Lightning、普通版 USB-C）：上限 100Mbps
  * USB3（15 Pro 以上）：開放到 400Mbps
* 視覺無損（HEVC 100~200Mbps）：USB 全開；Wi-Fi 僅當 `link_info` 實測≥150Mbps 且 `stats` 無丟包時開放，否則擋下並說明原因。
* 數學無損（`codec: "raw_nv12"`，編碼器 bypass、PC 免解碼直餵，USB Pro 限定實驗功能）：
  * 4K30 直傳約 3Gbps → USB3 Gen2 可跑，列為無損預設
  * 4K60 直傳約 6Gbps → 實驗開關，跑不動就掉幀，UI 明確標實驗性
  * 1080p60 直傳約 1.5Gbps → USB3 穩跑，無損入門檔

## 4. 傳輸協定

* iOS 端 TCP 監聽 **19300**，Bonjour 廣播 `_iphonecam._tcp`；USB 模式經 usbmuxd（`127.0.0.1:27015`，plist 協定，裝 iTunes 即有）轉發同一埠，App 層無感。
* 訊框格式（大端序，格式凍結為 CAM1）：`[MAGIC 4B "CAM1"][型別 1B][時間戳 8B][長度 4B][酬載]`
  型別：`0x01`=影像、`0x02`=保留（純視訊產品，不做音訊）、`0x03`=控制 JSON。
* 影像：AVCC 格式，SPS/PPS 帶內，連上先送一次 IDR；低延遲設定（無 B 幀、關鍵幀間隔 2 秒、CBR）。
* 控制訊息：`hello`（版本交握）、`link_info`（線路型別＋實測頻寬）、`set_video {width,height,fps,codec,bitrate_mbps}`、`set_image {location,beauty,camera,style}`（手機端 100ms 節流套用）、`idr_request`、`ping/pong`、`stats {temp_level,dropped_frames,rtt_ms}`（1 秒一次，供降檔提示）。
* 拒絕格式：`video_rejected {reason,fallback}`，PC 自動套用 fallback 並提示。

## 5. 美顏與調參（重濾鏡放 PC）

| 濾鏡 | 跑在哪 | 理由 |
|---|---|---|
| 磨皮/美白基礎 0~100 | 手機 | 便宜，編碼前做畫質最好 |
| 瘦臉/大眼（v2.5）、背景虛化（v3）、LUT、降噪 | PC（D3D pixel shader，VCamSample 管線延伸） | 吃獨顯，手機跑會發燙降頻 |
| 曝光補償 ±2EV、白平衡鎖定＋色溫 2800~7500K、對焦自動/手動＋鎖定、變焦、ISO 上限、對比/飽和/亮度/銳利 ±100、鏡像、方向、一鍵關美顏 | 手機收參數執行／PC 解碼後做 | `set_image.location` 註明 phone/pc |

## 6. HEVC 無硬解政策（使用者已定案）

* 切 4K/HEVC 前 PC 先自檢（Media Foundation HEVC 硬解：D3D11 Video Device / DXVA）。
* 沒過**只檢測＋提示，不自動降檔**，三選一：降 H.264 1080p（一鍵）／堅持 HEVC 軟解試試（卡了亮紅燈，隨時切回）／記住選擇。
* 診斷頁常駐 `HEVC 硬解：有/無`。

## 7. 虛擬攝像頭（Win11 專用）

* `MFCreateVirtualCamera`＋自訂 `IMFMediaSource` COM DLL（C#，VCamNetSample 模式），系統管理員 `regsvr32` 註冊。
* 輸出 NV12，同時宣告 720p/1080p/4K 三檔（OBS 拿 4K、Discord 拿 720p，各取所需，手機只出一份流）。
* DirectShow 舊路線不做（Win10 才需要，本機 Win11 跳過）。

## 8. 藍牙定位

藍牙 LE 只做發現與配對信號，影像一律走 Wi-Fi TCP 或 USB。頻寬跑不動 1080p 以上，此點凍結不再議。

## 9. 里程碑（無 Mac 可先做 P1~P3）

* **P1**：PC 空殼＋測試圖（彩條）→ OBS 抓得到虛擬攝像頭，MF 註冊鏈打通；加測 4K60 NV12 在本機跑不跑得動。
* **P2**：PC 網路接收器＋假串流模擬器（送假 H.264）→ 解碼→虛擬相機整條鏈驗收。
* **P3**：usbmuxd 轉發打通（iPhone＋傳輸線即可驗，不需 Mac）＋HEVC 自檢＋碼率上限擋板。
* **P4**（需借到 Mac）：iOS 端照本規格實作，真機對接；另需 Apple Developer 帳號上架/TestFlight。

## 10. 已知風險

* usbmuxd 轉發實際吞吐需實測（理論 USB2 約 200~300Mbps）。
* VCamSample 已知問題：Teams 預覽有畫面但遠端可能收不到，P1 驗收要測 Discord/OBS 兩家。
* 4K60 連續編碼發熱降頻：靠 `stats`＋降檔提示處理，不硬扛。

## 11. Android 完整版＋多裝置選擇（SPEC-v4，2026-10-02）

> 背景：無 Mac，iOS 端等借 Mac；先以 Android（S21 主力、XZP 備用）實作**完整規格**，不是測試樁。
> S21（S888）：4K60 HEVC／H.264 硬編、USB-C 3.x，走完整檔位；XZP（S835、API 26~28）只跑 720p/1080p H.264。

### 11.1 連線方向修正（重要，P2 做反了）

* P2 為了先跑起來做成 **PC 監聽、手機連入**；多裝置選擇必須走原設計：**手機端 TCP 監聽 19300、PC 主動連過去**。
* 發現機制 v1 用 **UDP 廣播信標**（mDNS 從缺，NSD/PC 兩端都要加依賴，v2 再說）：
  * 手機每 2 秒向 `255.255.255.255:19301` 廣播 JSON：`{kind:"beacon",name,ip,port:19300,codec:["h264","hevc"],max:"4K60"|"1080p30"}`。
  * PC 監聽 19301，收集成裝置清單（同 IP 去重、10 秒沒信標移除）。
  * USB（Android 網路共享/RNDIS）同樣走 IP，只是 transport 標 `usb`，上限照 §3。
* PC 一次只連一台（連新＝斷舊）；已連線裝置的信標照收，但清單標示「使用中」。
* `Cam1Server`（PC 監聽）保留給迴路測試；正式路徑是 `Cam1Client`（PC 主動連線＋交握＋統計，介面與 Server 對稱）。

### 11.2 Android Sender 完整功能（對照 iOS 計畫）

* 擷取：Camera2，檔位 720p30→1080p30/60→4K30→4K60（按機型能力宣告，XZP 只開到 1080p30）。
* 編碼：MediaCodec 硬編，H.264（Baseline、無 B 幀、CBR、低延遲）＋HEVC（S21 的 4K 檔）；關鍵幀間隔 2 秒；**IDR 必帶 SPS/PPS**（接收端免猜）。
* 控制（手機是執行者，PC 是大腦）：收 `set_video`/`set_image`；回 `video_params`＋`codec_config`＋`stats`（1 秒）。
* 手機端 UI（最小可用）：本機預覽（TextureView）、廣播開關、目前檔位／連線 PC 顯示、前後鏡頭切換。
* 美顏 v1 範圍：手機端**先不做**（連基礎磨皮都不做），串流乾淨送出，重濾鏡全在 PC（符合 §5 分工，Android 寫 GL 管線成本太高，留到有 iOS 版再一起做）。

### 11.3 PC 多裝置 UI

* 網路面板改為：裝置清單（名稱＋IP＋最高檔位＋狀態）＋〔連線〕〔中斷〕＋手動 IP 輸入＋連線。
* 統計／存檔／預覽沿用（訊框來源改接 Client）。
* 配對碼 v1 不做（同區網＋手動確認裝置名即夠；v2 加 PIN）。

### 11.4 方向與 csd 相容（v0.2，2026-10-02 真機回報修）

* 背景：真機兩顆雷：① codec-config 不是 avcC 而是**裸 SPS**（`67 64 00 1F…`，level 誤讀成長度爆掉）② App 鎖 landscape＋固定 1280x720，手機直拿時畫面轉 90°＋比例壓扁。
* 方向設計：**串流維持 sensor 原生方向、編碼中途不重啟**；陀螺儀（OrientationEventListener）算 `rot`（順時針 0/90/180/270）＋`flip`（前鏡=1），`video_params` 加帶，PC 顯示時用 ffmpeg `-vf` 轉正（`hflip`＋`transpose`）；手機本機預覽用 TextureView transform 轉正（Camera2 官方公式）。
* `rot` 公式：後鏡 `(sensor+角)%360`、前鏡 `(sensor-角)%360`（依 OEL 官方定義＋CameraX sensor-90 表推導；舊版正負號反了，橫拿必錯。前鏡待真機驗）。
* csd 解析三格式通吃：Annex-B／avcC-hevcC 盒裝／單顆裸 NAL；sps/pps/vps 分次到達可累積；HEVC 的 `codec_config.sps` 欄位塞整段序列頭（VPS+SPS+PPS，Annex-B）。
* 訊框格式：MediaCodec 實機吐的是 **Annex-B**（自帶起始碼），PC `ConvertAvcc` 改直通；純 AVCC（假客戶端）仍轉換。
* App 改 `fullSensor`＋`configChanges`（UI 跟著轉、不重建相機）。

## 12. 圖示與 UI 風格（2026-10-03 使用者定稿：直播主播女孩風）

* 主視覺：深紫黑底＋霓虹青粉漸層框＋圓角＋兔子吉祥物＋星星點綴，致敬《主播女孩重度依賴》（NSO：深色底＋粉青霓虹＋圓角視窗＋狀態藥丸＋JINE 式上線燈）。
* 色票：底 `#0D0D24`、面板 `#12122A`、次要字 `#9AA3C0`、青 `#35D6FF`、粉 `#FF5FD2`、白字。
* 元件語言：漸層邊框（青→粉）、圓角卡片 10~16、按鈕 6~8、LIVE 藥丸（`● LIVE` 粉粗體，串流中才顯示：PC `TxtLive`／手機狀態列 `🔴 LIVE`）。
* 圖示：同一張主視覺出三端——`assets/icon-src.png`（原圖）→ `icon-rounded.png`（圓角透明，r=20%）→ PC `iPhoneCam.App/assets/icon.ico`（16~256）→ Android `mipmap-*/ic_launcher.png`（48~192）＋`icon-512.png`（Play）；iOS 等有 Mac 再包 xcassets。產生器 `assets/gen_icon.py`（Pillow 4x 超採樣遮罩去鋸齒，方形中央裁切）。

## 13. P2d 手機畫面進虛擬攝像頭（2026-10-03 完成）

* 架構：fork 上游 `VCamNetSampleSource` → `iPhoneCam.Source`（同 CLSID `b624d1c1…`，重註冊即透明替換；namespace 沿用省 diff）。
* 跨處理程序通道 `iPhoneCam.Net/SharedFrame.cs`：MMF `iPhoneCam.Frame.v1`（標頭 64B：magic/version/w/h/seq/tick，payload 上限 3840x2160xBGRA）＋具名 mutex；寫端 15ms 拿不到鎖就跳幀，讀端永遠 WaitOne(0)（絕不擋 MF 管線），500ms 沒新 seq 回測試圖。
* DLL 端：`ExternalFrame.cs`（讀＋letterbox 進固定 1280x960：720p 上下黑邊、直向左右黑邊）→ `Generate` 有新幀就跳過測試圖繪製、走原 CPU 路徑（WIC＋CColorConvertDMO）；`SetD3DManager` 直接回 S_OK（v1 固定 CPU，OBS 給不給 D3D 結果一樣）。
* App 端：`PreviewPump` 每顯示一幀就 `SharedFrameWriter.Publish`（BGRA 顯示尺寸）；關程式 Dispose；無串流＝不發＝測試圖。
* 註冊：系統管理員 `regsvr32 iPhoneCam.Source.comhost.dll`（跳 UAC；驗過登錄 CLSID 指到新 DLL）。舊上游 DLL 檔留著但已無人引用。
* 驗收（假手機會動 testsrc＋dshow 抓）：50 幀中 f5→f45 差 44%、後段連續幀 27%——手機動畫確實流進虛擬攝像頭；無串流時回測試圖。


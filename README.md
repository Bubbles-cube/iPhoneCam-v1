# 泡泡直播助手 V1（iPhoneCam v1）

> **這是舊版（v1）存檔。** 功能驗證通過，但補丁疊補丁、後期已成難維護的「屎山」，已由 v2（泡泡直播助手 v2，清理重寫）取代。本倉庫純為留檔與對照，不再開發。

手機鏡頭 → Wi-Fi／USB → PC 虛擬攝像頭 → OBS／Discord。裝好 PC 軟體後，系統會多出一顆叫 `iPhoneCam` 的虛擬攝影機，直播軟體直接選來用。

## 系統架構

```
手機 App（Android：Camera2 → MediaCodec 硬編 H.264/HEVC → TCP/UDP 送出）
        ↓ Wi-Fi（同區網）或 USB（RNDIS 網路共享）
PC（C#，Win11：收串流 → ffmpeg 硬解 → MF 虛擬攝像頭）
        ↓
OBS / Discord 選「iPhoneCam」當攝影機
```

一句話分工：**手機是攝影機＋編碼器，PC 是收流端＋大腦**。編碼只能在手機做（原始畫面頻寬太大），重活（硬解、多檔輸出、控制）全放 PC。

## 通訊協定（CAM1）

* 訊框格式（大端序，凍結為 CAM1）：`[MAGIC 4B "CAM1"][型別 1B][時間戳 8B][長度 4B][酬載]`
  * `0x01`＝影像、`0x03`＝控制 JSON（`0x02` 保留）
* 影像：H.264（AVCC／Annex-B），SPS/PPS 帶內，連上先送一次 IDR，關鍵幀間隔 2 秒、無 B 幀、CBR。
* **控制走 TCP 19300**（手機監聽、PC 主動連）：`hello`／`link_info`／`set_video`／`set_image`／`idr_request`／`ping`／`pong`／`stats`。
* **影像可走 UDP**（分片 1400B＋NACK 重送＋抖動緩衝＋看門狗回退 TCP），比純 TCP 更抗無線抖動。
* **裝置發現走 UDP 信標 19301**：手機每 2 秒廣播 JSON `{kind:"beacon",name,ip,port,codec,max}`，PC 收集成清單（同 IP 去重、逾時移除）。

## 專案結構

```
iPhoneCam/
├── iPhoneCam.slnx          # PC 端方案（.NET 10）
├── iPhoneCam.App/          # PC 控制台（WPF）：裝置清單、預覽、參數、統計、霓虹 UI
├── iPhoneCam.Net/          # PC 共用庫（net10.0，無 UI 相依）
│   ├── Cam1Protocol.cs     #   CAM1 訊框＋控制訊息
│   ├── Cam1Server.cs       #   PC 監聽版（保留給迴路測試）
│   ├── Cam1Client.cs       #   PC 主動連線版（正式路徑）
│   ├── UdpVideo.cs         #   UDP 影像重組＋NACK
│   ├── AnnexB.cs           #   AVCC ↔ Annex-B
│   ├── FfmpegPreview.cs    #   ffmpeg 子程序硬解（D3D11VA）
│   ├── H264Preview.cs      #   自幹 MF 解碼（備用）
│   ├── SharedFrame.cs      #   跨處理程序共享記憶體（MMF＋mutex）
│   └── Nv12Preview.cs
├── iPhoneCam.Source/       # MF 虛擬攝像頭 COM DLL（fork 自 smourier/VCamNetSample）
├── AndroidSender/          # Android 發送端（Camera2＋MediaCodec）
├── assets/                 # 圖示與產生器（gen_icon.py）
└── SPEC-v3.md              # 規格定稿（v3 凍結），含檔位、碼率、濾鏡分工
```

## 功能

* **PC 控制台**：即時預覽（ffmpeg 硬解優先、失敗退軟解）、裝置搜尋＋手動 IP、解析度／FPS 下拉、IDR、截圖、存檔、統計（fps／Mbps／累計）。
* **虛擬攝像頭**：`MFCreateVirtualCamera`＋自訂 `IMFMediaSource`（NV12），跨處理程序以共享記憶體餵幀；換畫布跟著串流解析度走，斷訊凍結上一幀（不閃彩條）。
* **方向跟隨**：手機陀螺儀算 `rot`／`flip` 經控制訊息送 PC，PC 用 ffmpeg `transpose`／`hflip` 轉正。
* **保命機制**：session 失敗自動降檔、drain 防重入、亮屏＋WiFi 鎖、過熱降碼率、閃退記錄。
* **霓虹 UI**：深紫黑底＋青粉漸層框＋LIVE 藥丸，致敬《主播女孩重度依賴》風格。

## 建置

**PC 端**（需 .NET 10 SDK）：
```powershell
dotnet build iPhoneCam.slnx -c Release
```

**Android 端**（需 JDK 21；Gradle 8.7）：
```powershell
gradle assembleDebug   # 在 AndroidSender/ 下，產出 app-debug.apk
```

**註冊虛擬攝像頭**（系統管理員一次，同 CLSID 透明替換）：
```powershell
regsvr32 iPhoneCam.Source.comhost.dll
```

## 開發歷程與已知問題

* 這是「patch 時代」的完整快照：功能一路疊上去可用，但架構越補越亂，後期變成難維護的屎山 → 因而推倒重寫為 v2。
* 部分設計（PC 同時供 NV12 與 RGB32、自幹 MF 解碼在 App 內多執行緒易閃退）是當時踩到的雷，v2 已改掉。
* 規格與檔位設計見 `SPEC-v3.md`（v3 凍結版，含 4K60、碼率上限、HEVC 政策）。

## 授權與來源

* 虛擬攝像頭實作 fork 自 [`smourier/VCamNetSample`](https://github.com/smourier/VCamNetSample)（MIT）。
* 本專案其餘部分為個人專案程式碼。

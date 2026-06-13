# Tài liệu Yêu cầu Cải tiến — WindowsIPTV

> **Dự án:** [nongtiensonpro/WindowsIPTV](https://github.com/nongtiensonpro/WindowsIPTV)
> **Ngày lập:** 2026-06-13
> **Trạng thái:** v1.0.0-pre → mục tiêu v1.1.0
> **Tập trung:** Chất lượng video & Hiệu năng / Ổn định

---

## Mục lục

1. [Tổng quan hiện trạng](#1-tổng-quan-hiện-trạng)
2. [Ưu tiên 1 — Fix bug LAN / Multi-network interface](#2-ưu-tiên-1--fix-bug-lan--multi-network-interface)
3. [Ưu tiên 2 — Deinterlacing cho luồng 1080i](#3-ưu-tiên-2--deinterlacing-cho-luồng-1080i)
4. [Ưu tiên 3 — Deband (khử vỡ khối màu)](#4-ưu-tiên-3--deband-khử-vỡ-khối-màu)
5. [Ưu tiên 4 — HDR Tone Mapping tự động](#5-ưu-tiên-4--hdr-tone-mapping-tự-động)
6. [Ưu tiên 5 — Auto-reconnect nâng cao](#6-ưu-tiên-5--auto-reconnect-nâng-cao)
7. [Ưu tiên 6 — Shader Profile thông minh](#7-ưu-tiên-6--shader-profile-thông-minh)
8. [Ưu tiên 7 — Fast Channel Switching](#8-ưu-tiên-7--fast-channel-switching)
9. [Ưu tiên 8 — Windows SMTC Integration](#9-ưu-tiên-8--windows-smtc-integration)
10. [Bổ sung thông số kỹ thuật (Stats Panel)](#10-bổ-sung-thông-số-kỹ-thuật-stats-panel)
11. [Lộ trình triển khai](#11-lộ-trình-triển-khai)
12. [Checklist tổng hợp](#12-checklist-tổng-hợp)

---

## 1. Tổng quan hiện trạng

### Điểm mạnh hiện có

| Thành phần | Trạng thái | Ghi chú |
|---|---|---|
| WinUI 3 + libmpv hybrid | ✅ Tốt | Kiến trúc native, ít overhead |
| D3D11VA zero-copy | ✅ Tốt | Xác nhận qua `hw-pixelformat` |
| UDP low-latency profile | ✅ Tốt | `cache=no`, `demuxer-thread=no`, `vd-lavc-threads=1` |
| FSRCNNX + CAS shader | ✅ Tốt | 3 preset shader hoạt động |
| Auto-reconnect cơ bản | ✅ Có | Cần nâng cấp thêm |
| Stats panel đầy đủ | ✅ Tốt | Sau patch gần nhất |
| PCR discontinuity fix | ✅ Tốt | `ignore_pcr_discontinuity=1` |
| HWND child window | ✅ Tốt | `WS_EX_NOPARENTNOTIFY` chống flicker |

### Khoảng trống cần lấp

| Thành phần | Trạng thái | Mức độ ưu tiên |
|---|---|---|
| Multi-network interface (LAN bug) | ❌ Bug nghiêm trọng | 🔴 Cực cao |
| Deinterlacing 1080i | ❌ Hoàn toàn thiếu | 🔴 Rất cao |
| Deband | ❌ Hoàn toàn thiếu | 🔴 Cao |
| HDR Tone Mapping | ❌ Chưa có | 🟡 Trung bình-cao |
| Auto-reconnect nâng cao | ⚠️ Cơ bản | 🟡 Cao |
| Shader profile tự động | ⚠️ Thủ công | 🟡 Trung bình |
| Fast channel switching | ⚠️ Đủ dùng | 🟡 Trung bình |
| Windows SMTC | ❌ Chưa có | 🟢 UX |

---

## 2. Ưu tiên 1 — Fix bug LAN / Multi-network interface

### Mô tả vấn đề

Khi máy có cả **WiFi và LAN cắm đồng thời**, xem UDP multicast chỉ hoạt động qua WiFi mà không hoạt động qua LAN. Nguyên nhân: Windows chọn network interface sai khi join IGMP multicast group vì routing metric ưu tiên LAN nhưng multicast stream chỉ có trên interface WiFi (hoặc ngược lại).

**Root cause cụ thể:**

```
UDP Multicast 239.x.x.x
    → Windows route lookup
    → Chọn interface theo default gateway metric
    → Nếu metric LAN < WiFi → gửi IGMP Join qua LAN
    → Nhưng IPTV multicast chỉ có trên WiFi subnet
    → Không nhận được gói tin → màn đen
```

### Yêu cầu triển khai

**File cần sửa:** `IptvApp/Controls/MpvPlayer.cs`

**Field mới cần thêm vào class:**

```csharp
private string? _cachedMulticastLocalIp = null;
private DateTime _lastIpCacheTime = DateTime.MinValue;
private readonly TimeSpan _ipCacheDuration = TimeSpan.FromSeconds(30);
```

**Hàm `GetLocalIpForMulticast()` — lấy IP của interface đang được OS dùng để đến multicast:**

```csharp
private string? GetLocalIpForMulticast(string multicastTarget = "239.255.255.250")
{
    // Cache 30 giây để tránh query liên tục
    if (_cachedMulticastLocalIp != null &&
        DateTime.Now - _lastIpCacheTime < _ipCacheDuration)
        return _cachedMulticastLocalIp;

    try
    {
        // Tạo UDP socket giả — hỏi OS: interface nào sẽ được dùng để đến 239.x.x.x?
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, 0);

        socket.Connect(multicastTarget, 65530);
        var ep = socket.LocalEndPoint as System.Net.IPEndPoint;
        _cachedMulticastLocalIp = ep?.Address.ToString();
        _lastIpCacheTime = DateTime.Now;

        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: Multicast local IP detected: {_cachedMulticastLocalIp}");
        return _cachedMulticastLocalIp;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: GetLocalIpForMulticast failed: {ex.Message}");
        return null;
    }
}
```

**Sửa phần UDP trong `Play()`** — thêm `localaddr` vào lavf options:

```csharp
// THAY THẾ dòng lavfOptions hiện tại bằng:
string? localIp = GetLocalIpForMulticast();
string localAddrPart = !string.IsNullOrEmpty(localIp)
    ? $",localaddr={localIp}"
    : "";

string lavfOptions =
    $"fifo_size=5000000,overrun_nonfatal=1," +
    $"buffer_size=4194304,pkt_size=1316" +
    localAddrPart;

string streamOpts =
    $"buffer_size=4194304,pkt_size=1316" +
    (!string.IsNullOrEmpty(localIp) ? $",localaddr={localIp}" : "");

Mpv.SetOptionString(_mpvHandle, "demuxer-lavf-o", lavfOptions);
Mpv.SetOptionString(_mpvHandle, "stream-lavf-o", streamOpts);
```

**Thêm listener network change** vào constructor `MpvPlayer()`:

```csharp
public MpvPlayer()
{
    this.Loaded    += MpvPlayer_Loaded;
    this.Unloaded  += MpvPlayer_Unloaded;
    this.SizeChanged   += MpvPlayer_SizeChanged;
    this.LayoutUpdated += MpvPlayer_LayoutUpdated;

    // Xóa cache IP khi mạng thay đổi (cắm/rút LAN, đổi WiFi)
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
        (_, _) =>
        {
            _cachedMulticastLocalIp = null;
            _lastIpCacheTime = DateTime.MinValue;
            System.Diagnostics.Debug.WriteLine(
                "MpvPlayer: Network address changed — IP cache invalidated.");
        };
}
```

**Hủy đăng ký event trong `Cleanup()`:**

```csharp
private void Cleanup()
{
    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -=
        (_, _) => { _cachedMulticastLocalIp = null; };
    // ... phần còn lại giữ nguyên
}
```

### Kiểm tra sau triển khai

- [ ] Cắm LAN + WiFi cùng lúc → xem UDP → phải hoạt động
- [ ] Rút LAN → xem tiếp qua WiFi → tự nhận IP mới
- [ ] Cắm LAN lại → cache invalidate → tiếp tục xem

---

## 3. Ưu tiên 2 — Deinterlacing cho luồng 1080i

### Mô tả vấn đề

Phần lớn kênh IPTV nhà mạng VN (VNPT, Viettel, FPT) phát ở chuẩn **1080i** (interlaced — quét xen kẽ). Nếu không deinterlace, khi có chuyển động nhanh (bóng đá, tin tức chạy chữ, phim hành động) sẽ xuất hiện **sọc ngang răng cưa (combing artifact)** rõ rệt.

Toàn bộ code hiện tại trong `InitMpv()` và `Play()` **không có một dòng nào** xử lý deinterlacing.

### Thuật toán khuyến nghị

| Thuật toán | Ưu điểm | Nhược điểm | Dùng cho |
|---|---|---|---|
| `bwdif` (Bob Weaver) | Chất lượng cao, ít halo | Nặng hơn yadif ~20% | VOD, file local |
| `bwdif=mode=field` | Giữ nguyên field order, ít delay | Không nội suy | UDP live (ưu tiên) |
| `yadif` | Nhẹ, nhanh | Chất lượng thấp hơn bwdif | Máy yếu |
| `yadif=mode=field` | Nhanh nhất | Thấp nhất | Emergency fallback |

### Yêu cầu triển khai

**File cần sửa:** `IptvApp/Controls/MpvPlayer.cs`

**Thêm hàm `ApplyDeinterlaceIfNeeded()` gọi sau khi stream bắt đầu:**

```csharp
/// <summary>
/// Kiểm tra video-params để quyết định có cần deinterlace không.
/// Gọi từ UpdatePlaybackStats() khi đã có thông tin video.
/// </summary>
public void ApplyDeinterlaceIfNeeded()
{
    if (_mpvHandle == IntPtr.Zero) return;

    // Lấy thông tin scan type từ mpv
    string? scanType = Mpv.GetPropertyString(_mpvHandle, "video-params/scan-type");
    // scanType = "interlaced" hoặc "progressive" hoặc null

    // Fallback: kiểm tra thông qua container format
    string? fieldOrder = Mpv.GetPropertyString(_mpvHandle, "video-params/field-order");
    // fieldOrder: "top-first", "bottom-first", "progressive", "unknown"

    bool isInterlaced = scanType == "interlaced"
        || fieldOrder == "top-first"
        || fieldOrder == "bottom-first";

    if (isInterlaced && !_deinterlaceApplied)
    {
        // Dùng bwdif mode=field cho live stream (ít delay hơn mode=frame)
        // mode=field: xử lý từng field riêng lẻ → latency = 1 field (~20ms ở 50i)
        // mode=frame: ghép 2 field → chất lượng cao hơn nhưng delay thêm 1 field
        bool isLiveUdp = _currentUrl?.StartsWith("udp://",
            StringComparison.OrdinalIgnoreCase) == true
            || _currentUrl?.StartsWith("rtp://",
            StringComparison.OrdinalIgnoreCase) == true;

        string bwdifMode = isLiveUdp ? "field" : "frame";
        string bwdifParity = fieldOrder == "bottom-first" ? "1" : "0";

        Mpv.Command(_mpvHandle, new[]
        {
            "vf", "add",
            $"bwdif=mode={bwdifMode}:parity={bwdifParity}:deint=interlaced"
        });

        _deinterlaceApplied = true;
        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: Deinterlace applied — bwdif mode={bwdifMode}, " +
            $"parity={bwdifParity}, fieldOrder={fieldOrder}");
    }
    else if (!isInterlaced && _deinterlaceApplied)
    {
        // Stream đổi sang progressive → tắt bwdif
        Mpv.Command(_mpvHandle, new[] { "vf", "del", "bwdif" });
        _deinterlaceApplied = false;
        System.Diagnostics.Debug.WriteLine(
            "MpvPlayer: Deinterlace removed — stream is progressive.");
    }
}
```

**Field mới cần thêm vào class:**

```csharp
private bool _deinterlaceApplied = false;
private string? _currentUrl = null;
```

**Sửa `Play()` để lưu URL và reset deinterlace state:**

```csharp
public void Play(string url)
{
    _currentUrl = url;           // LƯU URL để ApplyDeinterlaceIfNeeded dùng
    _deinterlaceApplied = false; // RESET state khi chuyển kênh

    // ... phần còn lại giữ nguyên
}
```

**Gọi `ApplyDeinterlaceIfNeeded()` từ `UpdatePlaybackStats()`** sau khi đã có `width` và `vCodec`:

```csharp
public void UpdatePlaybackStats(HomeViewModel vm)
{
    // ... code hiện tại ...

    // Thêm vào sau phần Guard (kiểm tra width/vCodec):
    ApplyDeinterlaceIfNeeded();

    // ... phần còn lại
}
```

**Bổ sung thông số trong `UpdatePlaybackStats()` để hiển thị trạng thái deinterlace:**

```csharp
// Thêm vào cuối UpdatePlaybackStats():
string? scanType = Mpv.GetPropertyString(_mpvHandle, "video-params/scan-type");
vm.StatsDeinterlace = _deinterlaceApplied
    ? "bwdif (Đang khử quét xen kẽ)"
    : (scanType == "progressive" ? "Không cần (Progressive)" : "Không áp dụng");
```

**Property mới trong `HomeViewModel`:**

```csharp
private string _statsDeinterlace = "-";
public string StatsDeinterlace
{
    get => _statsDeinterlace;
    set => SetProperty(ref _statsDeinterlace, value);
}
```

### Kiểm tra sau triển khai

- [ ] Xem kênh 1080i → `StatsDeinterlace` hiển thị "bwdif (Đang khử quét xen kẽ)"
- [ ] Không còn sọc ngang khi có chuyển động nhanh
- [ ] Xem file 1080p local → `StatsDeinterlace` = "Không cần (Progressive)"
- [ ] Chuyển kênh → deinterlace tự tắt/bật đúng theo nguồn mới

---

## 4. Ưu tiên 3 — Deband (khử vỡ khối màu)

### Mô tả vấn đề

IPTV nén ở 7-8 Mbps cho 1080p là tương đối thấp. Kết quả: xuất hiện **color banding** (dải màu phân tầng bậc thang) rõ rệt ở các vùng chuyển sắc mượt như bầu trời, da người, phông nền studio tin tức.

**Quan trọng:** Deband chỉ nên bật với **VOD / file local**. Với **UDP live** thì **không bật** vì bộ lọc này tốn thêm ~2-5ms GPU time và không phù hợp với low-latency mode.

### Yêu cầu triển khai

**File cần sửa:** `IptvApp/Controls/MpvPlayer.cs`

**Thêm vào phần VOD config trong `Play()`:**

```csharp
// Trong else branch (VOD/HTTP/File Local), thêm sau các dòng SetOptionString hiện có:

// --- Deband: khử color banding do nén IPTV ---
// Chỉ bật cho VOD vì deband tốn thêm GPU time (~2-5ms)
// Không phù hợp với low-latency UDP mode
Mpv.SetOptionString(_mpvHandle, "deband", "yes");
Mpv.SetOptionString(_mpvHandle, "deband-iterations", "2");
// iterations=2: đủ để khử banding mà không tạo ra blur
// iterations=4: tốt hơn nhưng có thể mất chi tiết nhỏ

Mpv.SetOptionString(_mpvHandle, "deband-threshold", "48");
// threshold=48: mức trung bình — thấp hơn ít hiệu quả, cao hơn mất chi tiết

Mpv.SetOptionString(_mpvHandle, "deband-range", "12");
// range=12: bán kính tìm điểm tương đồng — tốt cho 1080p

Mpv.SetOptionString(_mpvHandle, "deband-grain", "24");
// grain=24: thêm nhiễu nhẹ sau deband để che artifact và tăng cảm giác tự nhiên
// Nếu không muốn grain thì set = "0"
```

**Đảm bảo tắt deband trong UDP branch:**

```csharp
// Trong if branch (UDP/RTP), thêm tường minh:
Mpv.SetOptionString(_mpvHandle, "deband", "no");
// Tắt hoàn toàn — không để inherit từ session trước
```

**Thêm thông số hiển thị:**

```csharp
// Trong UpdatePlaybackStats():
bool isDebandOn = Mpv.GetPropertyString(_mpvHandle, "deband") == "yes";
vm.StatsDeband = isDebandOn ? "Bật (iterations=2, threshold=48)" : "Tắt (Low-latency mode)";
```

**Property mới trong `HomeViewModel`:**

```csharp
private string _statsDeband = "-";
public string StatsDeband
{
    get => _statsDeband;
    set => SetProperty(ref _statsDeband, value);
}
```

### Kiểm tra sau triển khai

- [ ] Xem kênh tin tức (bầu trời, phông nền đơn màu) → không còn dải màu phân tầng
- [ ] Stats panel hiển thị trạng thái deband đúng
- [ ] UDP live → deband = "Tắt", không ảnh hưởng latency
- [ ] VOD file local → deband = "Bật"

---

## 5. Ưu tiên 4 — HDR Tone Mapping tự động

### Mô tả vấn đề

Kênh 4K HDR10 từ nhà mạng khi phát trên màn hình SDR (không HDR) sẽ bị **cháy sáng** (highlight clipping) hoặc **quá tối** nếu không có tone mapping. mpv có sẵn hệ thống tone mapping nhưng hiện tại code không kích hoạt.

### Yêu cầu triển khai

**File cần sửa:** `IptvApp/Controls/MpvPlayer.cs`

**Thêm hàm `ApplyHdrToneMappingIfNeeded()`** gọi sau khi có thông tin video:

```csharp
private bool _hdrToneMappingApplied = false;

public void ApplyHdrToneMappingIfNeeded()
{
    if (_mpvHandle == IntPtr.Zero) return;

    string? primaries = Mpv.GetPropertyString(_mpvHandle, "video-params/primaries");
    string? transferFunc = Mpv.GetPropertyString(_mpvHandle, "video-params/gamma");
    // primaries = "bt.2020" với HDR10/HLG
    // gamma/transfer = "pq" (HDR10), "hlg" (HLG), "bt.709" (SDR)

    bool isHdr = primaries == "bt.2020"
        || transferFunc == "pq"
        || transferFunc == "hlg"
        || transferFunc == "smpte-st-2084";

    if (isHdr && !_hdrToneMappingApplied)
    {
        // bt.2390: thuật toán chuẩn ICTCP tốt nhất cho TV content
        // (SMPTE ST 2390 — cân bằng highlight và shadow tốt nhất)
        Mpv.SetOptionString(_mpvHandle, "tone-mapping", "bt.2390");

        // Tự động đo peak brightness của nội dung (quan trọng với HDR variable)
        Mpv.SetOptionString(_mpvHandle, "hdr-compute-peak", "yes");

        // Bảo toàn màu sắc khi map từ bt.2020 xuống bt.709
        Mpv.SetOptionString(_mpvHandle, "gamut-mapping-mode", "desaturate");

        // Giữ nguyên không gian màu để shader xử lý
        Mpv.SetOptionString(_mpvHandle, "target-colorspace-hint", "yes");

        _hdrToneMappingApplied = true;
        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: HDR tone mapping enabled — primaries={primaries}, " +
            $"transfer={transferFunc}, algorithm=bt.2390");
    }
    else if (!isHdr && _hdrToneMappingApplied)
    {
        // Trở về SDR → tắt tone mapping
        Mpv.SetOptionString(_mpvHandle, "tone-mapping", "auto");
        Mpv.SetOptionString(_mpvHandle, "hdr-compute-peak", "no");
        _hdrToneMappingApplied = false;
        System.Diagnostics.Debug.WriteLine(
            "MpvPlayer: HDR tone mapping disabled — stream is SDR.");
    }
}
```

**Reset trong `Play()`:**

```csharp
public void Play(string url)
{
    _currentUrl = url;
    _deinterlaceApplied = false;
    _hdrToneMappingApplied = false; // THÊM DÒNG NÀY
    // ...
}
```

**Gọi trong `UpdatePlaybackStats()`:**

```csharp
ApplyDeinterlaceIfNeeded();
ApplyHdrToneMappingIfNeeded(); // THÊM DÒNG NÀY
```

**Thêm thông số HDR vào stats:**

```csharp
// Trong UpdatePlaybackStats():
string? primaries = Mpv.GetPropertyString(_mpvHandle, "video-params/primaries");
string? transferFunc = Mpv.GetPropertyString(_mpvHandle, "video-params/gamma");
bool isHdr = primaries == "bt.2020" || transferFunc == "pq" || transferFunc == "hlg";

vm.StatsHdrStatus = isHdr
    ? $"HDR ({transferFunc?.ToUpper() ?? "?"}) → Tone mapping bt.2390"
    : "SDR";
```

**Property mới trong `HomeViewModel`:**

```csharp
private string _statsHdrStatus = "-";
public string StatsHdrStatus
{
    get => _statsHdrStatus;
    set => SetProperty(ref _statsHdrStatus, value);
}
```

### Kiểm tra sau triển khai

- [ ] Kênh SDR (bt.709) → `StatsHdrStatus` = "SDR", không bật tone mapping
- [ ] Kênh HDR10 (bt.2020/pq) → `StatsHdrStatus` = "HDR (PQ) → Tone mapping bt.2390"
- [ ] Không còn cháy sáng hoặc quá tối khi xem kênh 4K HDR trên màn SDR
- [ ] Chuyển từ kênh HDR sang kênh SDR → tone mapping tự tắt

---

## 6. Ưu tiên 5 — Auto-reconnect nâng cao

### Mô tả vấn đề

Theo README, auto-reconnect cơ bản đã có (3 giây cố định). Cần nâng cấp ba điểm:

1. **Exponential backoff** — 3s → 6s → 12s → 24s → max 60s, tránh spam request khi server quá tải
2. **DNS re-resolve** — URL dạng hostname (không phải IP) có thể đổi IP sau outage; cần resolve lại mỗi lần reconnect
3. **UI feedback** — người dùng cần thấy trạng thái "Đang kết nối lại (lần 3, thử lại sau 12 giây...)" thay vì màn đen im lặng

### Yêu cầu triển khai

**File cần sửa:** `IptvApp/Controls/MpvPlayer.cs`

**Fields mới trong class:**

```csharp
private System.Timers.Timer? _reconnectTimer = null;
private string? _reconnectUrl = null;
private int _reconnectAttempt = 0;
private const int MaxReconnectDelay = 60; // giây
private const int BaseReconnectDelay = 3;  // giây
```

**Hàm `EnableAutoReconnect()`:**

```csharp
public void EnableAutoReconnect(string url)
{
    _reconnectUrl = url;
    _reconnectAttempt = 0;

    _reconnectTimer?.Stop();
    _reconnectTimer?.Dispose();
    _reconnectTimer = new System.Timers.Timer(BaseReconnectDelay * 1000);
    _reconnectTimer.Elapsed += OnReconnectTick;
    _reconnectTimer.AutoReset = false; // Quan trọng: dùng one-shot + reschedule thủ công
    _reconnectTimer.Start();
}

public void DisableAutoReconnect()
{
    _reconnectTimer?.Stop();
    _reconnectTimer?.Dispose();
    _reconnectTimer = null;
    _reconnectAttempt = 0;
}
```

**Hàm `OnReconnectTick()` với exponential backoff:**

```csharp
private void OnReconnectTick(object? sender, System.Timers.ElapsedEventArgs e)
{
    if (_mpvHandle == IntPtr.Zero || _reconnectUrl == null) return;

    // Kiểm tra xem stream đã hồi phục chưa
    string? coreIdle   = Mpv.GetPropertyString(_mpvHandle, "core-idle");
    string? eofReached = Mpv.GetPropertyString(_mpvHandle, "eof-reached");

    bool streamDead = coreIdle == "yes" || eofReached == "yes";

    if (streamDead)
    {
        _reconnectAttempt++;

        // Exponential backoff: 3 → 6 → 12 → 24 → 48 → 60 (max)
        double nextDelay = Math.Min(
            BaseReconnectDelay * Math.Pow(2, _reconnectAttempt - 1),
            MaxReconnectDelay);

        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: Reconnect attempt #{_reconnectAttempt}, " +
            $"next retry in {nextDelay:F0}s");

        // Thông báo lên UI
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            // Cần có property này trong HomeViewModel
            // vm.StatsStreamStatus = $"Mất tín hiệu — Đang kết nối lại " +
            //     $"(lần {_reconnectAttempt}, thử lại sau {nextDelay:F0}s...)";
        });

        // Re-resolve DNS trước khi reconnect
        // (phòng trường hợp server đổi IP sau outage)
        ResolveAndReconnect(_reconnectUrl);

        // Lên lịch lần reconnect tiếp theo
        _reconnectTimer!.Interval = nextDelay * 1000;
        _reconnectTimer.Start();
    }
    else
    {
        // Stream đã hồi phục → reset counter
        _reconnectAttempt = 0;
        System.Diagnostics.Debug.WriteLine(
            "MpvPlayer: Stream recovered, reconnect counter reset.");

        // Tiếp tục polling định kỳ để phát hiện mất stream mới
        _reconnectTimer!.Interval = BaseReconnectDelay * 1000;
        _reconnectTimer.Start();
    }
}
```

**Hàm `ResolveAndReconnect()` với DNS re-resolve:**

```csharp
private async void ResolveAndReconnect(string url)
{
    string resolvedUrl = url;

    try
    {
        // Chỉ resolve nếu URL dùng hostname (không phải IP)
        var uri = new Uri(url);
        bool isIpAddress = System.Net.IPAddress.TryParse(uri.Host, out _);

        if (!isIpAddress)
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            if (addresses.Length > 0)
            {
                // Dùng IP đã resolve để tránh phụ thuộc DNS trong lúc reconnect
                var builder = new UriBuilder(uri) { Host = addresses[0].ToString() };
                resolvedUrl = builder.ToString();
                System.Diagnostics.Debug.WriteLine(
                    $"MpvPlayer: DNS resolved {uri.Host} → {addresses[0]}");
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: DNS resolve failed: {ex.Message}, using original URL");
    }

    // Thực hiện reconnect trên UI thread
    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
    {
        if (_mpvHandle == IntPtr.Zero) return;
        Mpv.Command(_mpvHandle, new[] { "loadfile", resolvedUrl, "replace" });
    });
}
```

**Sửa `Play()` để tự động kích hoạt reconnect:**

```csharp
public void Play(string url)
{
    // ... code hiện tại ...

    // Kích hoạt auto-reconnect ngay khi bắt đầu play
    EnableAutoReconnect(url);
}
```

**Tắt reconnect trong `Cleanup()`:**

```csharp
private void Cleanup()
{
    DisableAutoReconnect(); // THÊM DÒNG NÀY ở đầu Cleanup()
    // ... phần còn lại giữ nguyên
}
```

### Kiểm tra sau triển khai

- [ ] Tắt WiFi trong khi đang xem → app hiển thị "Đang kết nối lại (lần 1...)"
- [ ] Bật WiFi lại → stream tự phục hồi, counter reset về 0
- [ ] Delay tăng dần: 3s → 6s → 12s → 24s → không vượt 60s
- [ ] Log Debug có in đúng delay và lần attempt

---

## 7. Ưu tiên 6 — Shader Profile thông minh

### Mô tả vấn đề

Hiện tại người dùng phải tự chọn shader thủ công (None / CAS / FSRCNNX). Không có khái niệm "loại nội dung" — shader tốt nhất cho phim khác với shader tốt nhất cho tin tức hoặc thể thao.

Ngoài ra, **Anime4K** đang được đề cập trong `plan.md` nhưng chưa có trong `Assets/Shaders/`.

### Yêu cầu triển khai

**Thêm file `Assets/Shaders/` cần bổ sung:**

```
Assets/
└── Shaders/
    ├── KrigBilateral.glsl       ✅ Có sẵn
    ├── CAS.glsl                 ✅ Có sẵn
    ├── FSRCNNX_x2_16-0-4-1.glsl ✅ Có sẵn
    ├── Anime4K_Upscale_CNN_x2_M.glsl   ❌ CẦN TẢI THÊM
    └── Anime4K_Restore_CNN_M.glsl       ❌ CẦN TẢI THÊM
```

> **Nguồn tải Anime4K:** https://github.com/bloc97/Anime4K/releases — dùng bản `Anime4K_v4.x_Shader_GLSL`

**Thêm enum `ContentProfile` vào project:**

```csharp
// File mới: IptvApp/Models/ContentProfile.cs
namespace IptvApp.Models;

public enum ContentProfile
{
    Auto,        // Tự động detect từ metadata
    News,        // Tin tức / Talk show — ưu tiên sharpness nhẹ
    Sports,      // Thể thao — ưu tiên fps, không shader nặng
    Movie,       // Phim — chất lượng tối đa
    Anime,       // Hoạt hình — Anime4K
    Documentary  // Phim tài liệu — cân bằng
}
```

**Thêm hàm `SetShaderModeForProfile()` trong `MpvPlayer.cs`:**

```csharp
public void SetShaderModeForProfile(ContentProfile profile)
{
    if (_mpvHandle == IntPtr.Zero) return;

    string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
    string shaderDir = System.IO.Path.Combine(baseDir, "Assets", "Shaders");

    // Xóa shader cũ
    Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "clr", "" });

    switch (profile)
    {
        case ContentProfile.News:
        case ContentProfile.Documentary:
            // CAS nhẹ — làm nét chữ chạy và chi tiết nhỏ
            ApplyShaderIfExists(shaderDir, "KrigBilateral.glsl");
            ApplyShaderIfExists(shaderDir, "CAS.glsl");
            Mpv.SetOptionString(_mpvHandle, "scale", "spline36");
            break;

        case ContentProfile.Sports:
            // Không shader — ưu tiên tốc độ xử lý, giảm GPU load
            // Chỉ giữ KrigBilateral để màu sắc đẹp hơn
            ApplyShaderIfExists(shaderDir, "KrigBilateral.glsl");
            Mpv.SetOptionString(_mpvHandle, "scale", "bilinear");
            // bilinear: nhanh nhất, phù hợp khi ưu tiên fps
            break;

        case ContentProfile.Movie:
            // FSRCNNX full — chất lượng tối đa
            ApplyShaderIfExists(shaderDir, "KrigBilateral.glsl");
            ApplyShaderIfExists(shaderDir, "FSRCNNX_x2_16-0-4-1.glsl");
            Mpv.SetOptionString(_mpvHandle, "scale", "ewa_lanczos");
            break;

        case ContentProfile.Anime:
            // Anime4K — tối ưu cho nét vẽ và màu phẳng
            ApplyShaderIfExists(shaderDir, "Anime4K_Restore_CNN_M.glsl");
            ApplyShaderIfExists(shaderDir, "Anime4K_Upscale_CNN_x2_M.glsl");
            Mpv.SetOptionString(_mpvHandle, "scale", "mitchell");
            break;

        default: // Auto và None
            Mpv.SetOptionString(_mpvHandle, "scale", "spline36");
            break;
    }
}

private void ApplyShaderIfExists(string shaderDir, string fileName)
{
    string path = System.IO.Path.Combine(shaderDir, fileName);
    if (System.IO.File.Exists(path))
        Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "append", path });
    else
        System.Diagnostics.Debug.WriteLine(
            $"MpvPlayer: Shader not found: {path}");
}
```

**Cập nhật `HomeViewModel` với property `ContentProfile`:**

```csharp
private ContentProfile _selectedProfile = ContentProfile.Auto;
public ContentProfile SelectedProfile
{
    get => _selectedProfile;
    set => SetProperty(ref _selectedProfile, value);
}
```

### Kiểm tra sau triển khai

- [ ] Chọn profile Sports → không có shader nặng, GPU render time thấp
- [ ] Chọn profile Movie → FSRCNNX active, thấy trong StatsActiveShaders
- [ ] Chọn profile Anime → Anime4K active
- [ ] Anime4K shader file tồn tại trong Assets/Shaders/

---

## 8. Ưu tiên 7 — Fast Channel Switching

### Mô tả vấn đề

Hiện tại khi chuyển kênh, có khoảng đen ~300-500ms do trình tự `stop` → `loadfile`. Nguyên nhân: không có cơ chế "warmup" trước, và buffer settings chưa được reset về minimum kịp thời.

### Yêu cầu triển khai

**Thêm hàm `PrefetchChannel()` để gọi khi hover kênh:**

```csharp
/// <summary>
/// Gọi khi người dùng hover chuột vào kênh — bắt đầu resolve DNS trước.
/// Giảm thời gian chờ khi click chính thức.
/// </summary>
public async System.Threading.Tasks.Task PrefetchChannel(string url)
{
    try
    {
        if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
            return; // UDP multicast không cần resolve

        var uri = new Uri(url);
        if (!System.Net.IPAddress.TryParse(uri.Host, out _))
        {
            // Pre-resolve DNS — kết quả sẽ được OS cache ~30 giây
            await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            System.Diagnostics.Debug.WriteLine(
                $"MpvPlayer: Prefetch DNS resolved: {uri.Host}");
        }
    }
    catch { /* Ignore prefetch errors */ }
}
```

**Tối ưu trình tự trong `Play()` — giảm khoảng đen:**

```csharp
public void Play(string url)
{
    if (_mpvHandle == IntPtr.Zero) return;

    // ... cấu hình UDP/VOD options như hiện tại ...

    // THAY THẾ 2 dòng stop + loadfile hiện tại bằng:

    // 1. Set demuxer-readahead-secs = 0 trước loadfile để không phải chờ buffer fill
    // (Chỉ trong thời điểm chuyển kênh, sẽ khôi phục sau)
    if (!url.StartsWith("udp://") && !url.StartsWith("rtp://"))
    {
        Mpv.SetOptionString(_mpvHandle, "demuxer-readahead-secs", "0.5");
        // Sau khi video bắt đầu, UpdatePlaybackStats sẽ khôi phục về giá trị bình thường
    }

    // 2. Dùng loadfile replace trực tiếp — không cần stop trước
    // mpv tự flush buffer khi gặp replace command
    Mpv.Command(_mpvHandle, new[] { "loadfile", url, "replace" });

    // KHÔNG dùng: Mpv.Command(_mpvHandle, new[] { "stop" });
    // Lý do: stop gây khoảng đen thêm ~100-200ms rồi mới loadfile
}
```

### Kiểm tra sau triển khai

- [ ] Đo thời gian từ click kênh đến first frame hiển thị — phải < 300ms với kênh UDP
- [ ] Không còn màn đen dài > 500ms khi chuyển kênh
- [ ] Log Debug không có dòng "stop" trước "loadfile"

---

## 9. Ưu tiên 8 — Windows SMTC Integration

### Mô tả vấn đề

`plan.md` đã đề cập tính năng này nhưng chưa implement. SMTC (System Media Transport Controls) cho phép:

- Hiện tên kênh + logo trên thanh volume Windows khi Alt+Tab
- Hỗ trợ phím media (Play/Pause/Next channel) trên bàn phím và tai nghe
- Hiện thông tin kênh trên lock screen

### Yêu cầu triển khai

**Thêm NuGet package** (nếu chưa có):

```xml
<!-- IptvApp.csproj -->
<PackageReference Include="Microsoft.Windows.SDK.Contracts" Version="10.0.26100.x" />
```

**Tạo file mới `IptvApp/Services/SmtcService.cs`:**

```csharp
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace IptvApp.Services;

public class SmtcService
{
    private SystemMediaTransportControls? _smtc;
    private SystemMediaTransportControlsDisplayUpdater? _updater;

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;

    public void Initialize(nint windowHandle)
    {
        // Lấy SMTC từ window handle
        _smtc = SystemMediaTransportControlsInterop
            .GetForWindow(windowHandle);

        _smtc.IsPlayEnabled   = true;
        _smtc.IsPauseEnabled  = true;
        _smtc.IsNextEnabled   = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsEnabled = true;

        _smtc.ButtonPressed += OnButtonPressed;
        _updater = _smtc.DisplayUpdater;
        _updater.Type = MediaPlaybackType.Video;
    }

    public void UpdateChannel(string channelName, string? logoUrl = null)
    {
        if (_smtc == null || _updater == null) return;

        _updater.VideoProperties.Title  = channelName;
        _updater.VideoProperties.Subtitle = "IPTV Live";

        if (!string.IsNullOrEmpty(logoUrl))
        {
            try
            {
                _updater.Thumbnail =
                    RandomAccessStreamReference.CreateFromUri(new Uri(logoUrl));
            }
            catch { /* Logo load fail — ignore */ }
        }

        _updater.Update();
    }

    public void SetPlaybackStatus(MediaPlaybackStatus status)
        => _smtc?.PlaybackStatus is not null
            ? _smtc!.PlaybackStatus = status
            : throw new InvalidOperationException("SMTC not initialized");

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                PlayPressed?.Invoke(); break;
            case SystemMediaTransportControlsButton.Pause:
                PausePressed?.Invoke(); break;
            case SystemMediaTransportControlsButton.Next:
                NextPressed?.Invoke(); break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousPressed?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_smtc != null)
            _smtc.ButtonPressed -= OnButtonPressed;
    }
}
```

**Đăng ký trong `App.xaml.cs` hoặc `MainWindow.xaml.cs`:**

```csharp
private SmtcService _smtcService = new();

// Trong OnLaunched hoặc sau khi MainWindow tạo xong:
nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
_smtcService.Initialize(hwnd);

// Kết nối với player events:
_smtcService.PlayPressed  += () => Player.Pause(); // Pause cycle
_smtcService.PausePressed += () => Player.Pause();
_smtcService.NextPressed  += () => ViewModel.NextChannel();
_smtcService.PreviousPressed += () => ViewModel.PreviousChannel();
```

**Gọi `UpdateChannel()` mỗi khi chuyển kênh:**

```csharp
// Trong HomeViewModel hoặc code xử lý chuyển kênh:
_smtcService.UpdateChannel(
    channelName: currentChannel.Name,
    logoUrl: currentChannel.LogoUrl);

_smtcService.SetPlaybackStatus(MediaPlaybackStatus.Playing);
```

### Kiểm tra sau triển khai

- [ ] Nhấn phím Play/Pause trên bàn phím → video toggle pause
- [ ] Nhấn phím Next Track → chuyển sang kênh tiếp theo
- [ ] Alt+Tab → thấy tên kênh và logo trong task switcher
- [ ] Lock screen → hiện tên kênh

---

## 10. Bổ sung thông số kỹ thuật (Stats Panel)

Dựa trên phân tích stats hiện tại, cần bổ sung các thông số sau vào `UpdatePlaybackStats()` và `HomeViewModel`:

### Thông số mới cần thêm

| Property VM | mpv property | Mô tả |
|---|---|---|
| `StatsStreamStatus` | `core-idle`, `eof-reached`, `pause` | Trạng thái real-time |
| `StatsDisplayFps` | `estimated-display-fps` | Tần số quét màn hình |
| `StatsPixelFormat` | `video-params/pixelformat`, `hw-pixelformat` | Xác nhận zero-copy |
| `StatsColorMatrix` | `video-params/colormatrix` | bt.709 / bt.2020 |
| `StatsColorRange` | `video-params/colorlevels` | Limited / Full |
| `StatsAudioFormat` | `audio-params/format` | s16, floatp, s32 |
| `StatsLiveLatency` | `demuxer-cache-time - playback-time` | Độ trễ live edge |
| `StatsPacketLoss` | `vo-delayed-frame-count` (ước tính) | Không có property trực tiếp |
| `StatsActiveGpu` | `d3d11-adapter` | GPU nào đang thực sự render |
| `StatsDeinterlace` | Logic nội bộ | Trạng thái bwdif |
| `StatsDeband` | `deband` property | Trạng thái deband |
| `StatsHdrStatus` | `video-params/primaries`, `gamma` | Trạng thái HDR |

### Thay đổi logic hiện có

| Property | Thay đổi |
|---|---|
| `StatsFps` | Thêm nhãn chuẩn `[50Hz PAL]`, `[25fps PAL]`, `[60Hz NTSC]` |
| `StatsVideoCodec` | Thêm profile + level: `H.264 (High L4.1)` |
| `StatsAudioCodec` | Cảnh báo nếu là MP3 (bất thường cho IPTV) |
| `StatsCacheState` | `"0 giây"` → `"0 giây (Low-latency mode)"` |
| `StatsRealTimeBitrate` | Tách riêng: `7.48 Mbps (V: 7.35 / A: 0.13)` |
| `StatsNetworkSpeed` | Đổi KB/s → Mbps để thống nhất đơn vị |
| `StatsAvSync` | Thêm nhãn `[Đồng bộ tốt / Lệch nhẹ / Lệch đáng kể]` |
| `StatsGpuRenderTime` | Thêm nhãn `[Xuất sắc / Tốt / Chấp nhận / Quá tải]` |

---

## 11. Lộ trình triển khai

```
Tuần 1 (Ngay bây giờ)
├── [P1] Fix LAN bug — GetLocalIpForMulticast + NetworkAddressChanged listener
└── [P2] Deinterlacing — bwdif auto-detect từ video-params/scan-type

Tuần 2
├── [P3] Deband — chỉ VOD, với cảnh báo trong Stats
└── [P4] HDR Tone Mapping — auto-detect bt.2020/pq, algorithm bt.2390

Tuần 3
├── [P5] Auto-reconnect nâng cao — exponential backoff + DNS re-resolve + UI feedback
└── [P10] Stats Panel bổ sung — tất cả property mới + fix đơn vị

Tuần 4
├── [P6] Shader Profiles — enum ContentProfile + Anime4K shaders
└── [P7] Fast Channel Switching — bỏ stop, thêm prefetch DNS

Sau (polish)
└── [P8] SMTC Integration — media keys + lock screen info
```

---

## 12. Checklist tổng hợp

### Chất lượng video

- [ ] **[P2]** `bwdif` deinterlacing tự động detect 1080i từ `video-params/scan-type`
- [ ] **[P2]** Deinterlace mode=field cho UDP, mode=frame cho VOD
- [ ] **[P2]** Reset `_deinterlaceApplied = false` khi `Play()` mới
- [ ] **[P3]** `deband=yes` chỉ trong VOD branch
- [ ] **[P3]** `deband=no` tường minh trong UDP branch
- [ ] **[P3]** Thông số deband hiển thị trong Stats panel
- [ ] **[P4]** HDR detect từ `video-params/primaries == bt.2020`
- [ ] **[P4]** Tone mapping algorithm `bt.2390`
- [ ] **[P4]** `hdr-compute-peak=yes` khi HDR active
- [ ] **[P4]** Tắt tone mapping khi chuyển sang kênh SDR
- [ ] **[P6]** Download Anime4K GLSL shaders vào `Assets/Shaders/`
- [ ] **[P6]** Enum `ContentProfile` với 6 preset
- [ ] **[P6]** `SetShaderModeForProfile()` với logic per-profile

### Hiệu năng & ổn định

- [ ] **[P1]** `GetLocalIpForMulticast()` với socket UDP trick
- [ ] **[P1]** `localaddr=` trong `demuxer-lavf-o` và `stream-lavf-o`
- [ ] **[P1]** Cache IP 30 giây, invalidate khi network change
- [ ] **[P1]** `NetworkChange.NetworkAddressChanged` listener trong constructor
- [ ] **[P1]** Hủy đăng ký listener trong `Cleanup()`
- [ ] **[P5]** `_reconnectAttempt` counter
- [ ] **[P5]** Exponential backoff: `3 * 2^(attempt-1)`, max 60s
- [ ] **[P5]** `ResolveAndReconnect()` với DNS re-resolve
- [ ] **[P5]** UI feedback: status string với lần attempt và delay tiếp theo
- [ ] **[P5]** `DisableAutoReconnect()` trong `Cleanup()`
- [ ] **[P7]** Bỏ `stop` trước `loadfile replace`
- [ ] **[P7]** `PrefetchChannel()` async cho hover event
- [ ] **[P8]** `SmtcService` class với button pressed events
- [ ] **[P8]** `UpdateChannel()` gọi mỗi khi chuyển kênh
- [ ] **[P8]** Media keys hoạt động (Play/Pause/Next/Prev)

### Stats Panel

- [ ] **[P10]** `StatsStreamStatus` — trạng thái real-time
- [ ] **[P10]** `StatsDisplayFps` — tần số quét màn hình
- [ ] **[P10]** `StatsPixelFormat` — với nhãn "Zero-copy xác nhận"
- [ ] **[P10]** `StatsColorMatrix` và `StatsColorRange`
- [ ] **[P10]** `StatsAudioFormat` — s16/floatp/s32
- [ ] **[P10]** `StatsLiveLatency` — tính từ `demuxer-cache-time - playback-time`
- [ ] **[P10]** `StatsActiveGpu` — từ `d3d11-adapter`
- [ ] **[P10]** `StatsDeinterlace` — trạng thái bwdif
- [ ] **[P10]** `StatsDeband` — trạng thái deband
- [ ] **[P10]** `StatsHdrStatus` — HDR/SDR + thuật toán
- [ ] **[P10]** Fix `StatsFps` — thêm nhãn PAL/NTSC
- [ ] **[P10]** Fix `StatsRealTimeBitrate` — tách V/A
- [ ] **[P10]** Fix `StatsNetworkSpeed` — thống nhất Mbps
- [ ] **[P10]** Fix `StatsAvSync` — thêm nhãn mức độ lệch

---

*Tài liệu này được tạo tự động từ phân tích codebase tại commit mới nhất của nhánh `main`. Cập nhật lần cuối: 2026-06-13.*

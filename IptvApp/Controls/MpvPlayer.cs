using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using IptvApp.Native;
using IptvApp.ViewModels;
using System.Linq;

namespace IptvApp.Controls;

public class MpvPlayer : Grid
{
    private IntPtr _mpvHandle = IntPtr.Zero;
    private IntPtr _childHwnd = IntPtr.Zero;
    private bool _initialized;

    private DispatcherTimer? _posDebounce;
    private DispatcherTimer? _reconnectTimer;
    private string _reconnectUrl = string.Empty;
    private bool _isReconnecting = false;

    // Keep track of last position and size to avoid redundant Win32 calls
    private int _lastX = -1;
    private int _lastY = -1;
    private int _lastWidth = -1;
    private int _lastHeight = -1;

    // Track frame drops for rate calculation
    private long _lastDecDrop = 0;
    private long _lastVoDrop = 0;
    private DateTime _lastDropCheckTime = DateTime.MinValue;

    // Win32 API imports
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    // Style constants
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_EX_NOPARENTNOTIFY = 0x00000004;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public MpvPlayer()
    {
        this.Loaded += MpvPlayer_Loaded;
        this.Unloaded += MpvPlayer_Unloaded;
        this.SizeChanged += MpvPlayer_SizeChanged;
    }

    private void MpvPlayer_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            System.Diagnostics.Debug.WriteLine("MpvPlayer: Loaded event fired.");

            if (App.MainWindow == null)
            {
                System.Diagnostics.Debug.WriteLine("MpvPlayer: App.MainWindow is null!");
                return;
            }

            // Get the HWND of the main window
            IntPtr parentHwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            if (parentHwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("MpvPlayer: Failed to get parent HWND!");
                return;
            }

            int width = (int)this.ActualWidth;
            int height = (int)this.ActualHeight;
            if (width <= 0) width = 100;
            if (height <= 0) height = 100;

            // Create a child window of the main window using the standard "STATIC" window class
            _childHwnd = CreateWindowEx(
                WS_EX_NOPARENTNOTIFY,
                "STATIC",
                "",
                (uint)(WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS),
                0, 0, width, height,
                parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_childHwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("MpvPlayer: Failed to create child HWND!");
                return;
            }

            // Disable input on the child window so mouse clicks pass through to WinUI 3
            EnableWindow(_childHwnd, false);
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Created child HWND: {_childHwnd}");

            // Update its position relative to the main window client area
            UpdateChildWindowPosition();

            // Initialize libmpv
            InitMpv();
            _initialized = true;
        }
    }

    private void MpvPlayer_Unloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

    private void MpvPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _posDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _posDebounce.Tick += (_, _) => { _posDebounce.Stop(); UpdateChildWindowPosition(); };
        _posDebounce.Stop();
        _posDebounce.Start();
    }

    private void UpdateChildWindowPosition()
    {
        if (_childHwnd == IntPtr.Zero) return;

        try
        {
            // Get position of the control relative to the Window client area
            var transform = this.TransformToVisual(null);
            var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            int x = (int)position.X;
            int y = (int)position.Y;
            int width = (int)this.ActualWidth;
            int height = (int)this.ActualHeight;

            // Only update position/size if it actually changed
            if (x != _lastX || y != _lastY || width != _lastWidth || height != _lastHeight)
            {
                SetWindowPos(_childHwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
                _lastX = x;
                _lastY = y;
                _lastWidth = width;
                _lastHeight = height;
            }
        }
        catch (Exception)
        {
            // TransformToVisual can throw if the element is not in the visual tree yet
        }
    }

    private void InitMpv()
    {
        System.Diagnostics.Debug.WriteLine("MpvPlayer: InitMpv starting...");

        _mpvHandle = Mpv.Create();
        if (_mpvHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("MpvPlayer: Failed to create mpv instance (Mpv.Create returned null)");
            throw new Exception("Failed to create mpv instance");
        }
        System.Diagnostics.Debug.WriteLine($"MpvPlayer: Mpv instance created: {_mpvHandle}");

        // Set standard video options
        Mpv.SetOptionString(_mpvHandle, "keep-open", "yes");
        // Đặt lại vo=gpu và gpu-api=d3d11 vì Vulkan/gpu-next thường bị lỗi màn đen khi nhúng qua HWND (wid) trên Windows
        Mpv.SetOptionString(_mpvHandle, "vo", "gpu");
        Mpv.SetOptionString(_mpvHandle, "gpu-api", "d3d11");
        
        // Tối ưu hóa chất lượng hình ảnh (High Quality Scaling) đặc biệt cho D3D11
        Mpv.SetOptionString(_mpvHandle, "d3d11-flip", "yes"); // Bật Flip model để tăng cường mượt mà và giảm xé hình (Tearing)
        
        // Cấu hình GPU Đơn (Single GPU - Zero Copy)
        // Ép sử dụng d3d11va thuần túy (không có -copy). Điều này sẽ giam toàn bộ quá trình giải mã và kết xuất
        // trong VRAM của duy nhất 1 GPU (Intel hoặc NVIDIA tùy theo hệ thống phân bổ), tiết kiệm băng thông RAM.
        Mpv.SetOptionString(_mpvHandle, "hwdec", "d3d11va");
        Mpv.SetOptionString(_mpvHandle, "scale", "spline36"); // Thuật toán upscale chất lượng cao
        Mpv.SetOptionString(_mpvHandle, "cscale", "spline36"); // Thuật toán upscale màu sắc
        Mpv.SetOptionString(_mpvHandle, "dscale", "mitchell"); // Thuật toán downscale chống răng cưa
        Mpv.SetOptionString(_mpvHandle, "dither-depth", "auto");
        Mpv.SetOptionString(_mpvHandle, "correct-downscaling", "yes");
        Mpv.SetOptionString(_mpvHandle, "linear-downscaling", "yes");
        Mpv.SetOptionString(_mpvHandle, "sigmoid-upscaling", "yes");

        // Cấu hình AI Frame Interpolation (Làm mượt chuyển động lên 60FPS+)
        Mpv.SetOptionString(_mpvHandle, "video-sync", "display-resample");
        Mpv.SetOptionString(_mpvHandle, "interpolation", "yes");
        Mpv.SetOptionString(_mpvHandle, "tscale", "oversample");

        Mpv.SetOptionString(_mpvHandle, "cache", "yes");
        Mpv.SetOptionString(_mpvHandle, "demuxer-max-bytes", "150MiB");
        Mpv.SetOptionString(_mpvHandle, "stream-buffer-size", "2MiB");

        // Associate with the child window handle
        if (_childHwnd != IntPtr.Zero)
        {
            string widStr = _childHwnd.ToInt64().ToString();
            Mpv.SetOptionString(_mpvHandle, "wid", widStr);
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Configured libmpv wid option: {widStr}");
        }

        int initResult = Mpv.Initialize(_mpvHandle);
        System.Diagnostics.Debug.WriteLine($"MpvPlayer: Mpv.Initialize result: {initResult}");
        if (initResult < 0)
        {
            throw new Exception($"Failed to initialize libmpv: {initResult}");
        }
    }

    public void Play(string url)
    {
        if (_mpvHandle == IntPtr.Zero) return;
        System.Diagnostics.Debug.WriteLine($"MpvPlayer: Play called for URL: {url}");
        _isReconnecting = false;

        // Tối ưu hóa ĐẶC BIỆT cho luồng UDP/RTP Multicast (Live IPTV nhà mạng)
        if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
        {
            // 1. Áp dụng profile có sẵn của mpv để tự động tắt cache, untimed, v.v.
            Mpv.SetOptionString(_mpvHandle, "profile", "low-latency");

            // 2. Tối ưu buffer UDP ở tầng Socket (FFmpeg) để chống rớt gói tin (Packet Loss) do Jitter mạng
            // buffer_size=4194304 (4MB): Rất quan trọng trên Windows vì buffer UDP mặc định quá nhỏ (64KB).
            // pkt_size=1316: Kích thước chuẩn cho luồng MPEG-TS (7 gói * 188 bytes), tránh phân mảnh IP.
            // fifo_size & overrun_nonfatal: Chống tràn bộ nhớ đệm nội bộ của demuxer.
            string lavfOptions = "fifo_size=5000000,overrun_nonfatal=1,buffer_size=4194304,pkt_size=1316,ignore_pcr_discontinuity=1,skip_clear=1";
            Mpv.SetOptionString(_mpvHandle, "demuxer-lavf-o", lavfOptions);
            Mpv.SetOptionString(_mpvHandle, "stream-lavf-o", "buffer_size=4194304,pkt_size=1316,recv_buffer_size=8388608");

            // 3. Tắt Cache để đảm bảo độ trễ (Latency) thấp nhất, xem trực tiếp real-time
            Mpv.SetOptionString(_mpvHandle, "cache", "no");
            
            // 4. Tắt demuxer thread để giảm thiểu hàng đợi (queue) giữa luồng đọc mạng và luồng giải mã
            Mpv.SetOptionString(_mpvHandle, "demuxer-thread", "no");
            
            // 5. Giới hạn 1 luồng giải mã. Giải mã đa luồng (multi-threading) yêu cầu buffer 
            // các frame để ghép lại, làm tăng độ trễ. 1 luồng là nhanh nhất cho live-stream.
            Mpv.SetOptionString(_mpvHandle, "vd-lavc-threads", "1");

            // --- FIX LỖI ĐỘ TRỄ NGHIÊM TRỌNG DO CẤU HÌNH TOÀN CỤC (InitMpv) ---
            // Các thuật toán làm mượt (Interpolation) và display-resample đang bật ở InitMpv
            // sẽ buffer thêm vài trăm ms đến vài giây để nội suy frame. Bắt buộc phải TẮT cho Live UDP!
            Mpv.SetOptionString(_mpvHandle, "interpolation", "no");
            Mpv.SetOptionString(_mpvHandle, "video-sync", "audio"); // Đồng bộ theo Audio an toàn hơn cho Live TV, tránh bị biến đổi pitch
            
            // 6. Chiến lược rớt khung hình (Frame Drop): Nếu giải mã không kịp, thà vứt bỏ frame cũ 
            // để bám sát "Live Edge" (thời gian thực) thay vì phát lại đoạn video bị trễ.
            Mpv.SetOptionString(_mpvHandle, "framedrop", "decoder+vo");
            
            // 7. Giảm buffer của Audio xuống mức tối thiểu (0.1 giây) để âm thanh hình ảnh đồng bộ real-time
            Mpv.SetOptionString(_mpvHandle, "audio-buffer", "0.1");
            
            // 8. Bật hack giảm độ trễ video (hữu ích cho một số luồng TS có timestamp bất thường)
            Mpv.SetOptionString(_mpvHandle, "video-latency-hacks", "yes");
        }
        else
        {
            // Cấu hình cho VOD / HTTP / File Local (Xem phim, video thông thường)
            Mpv.SetOptionString(_mpvHandle, "profile", "default");
            Mpv.SetOptionString(_mpvHandle, "cache", "yes");
            Mpv.SetOptionString(_mpvHandle, "demuxer-thread", "yes");
            Mpv.SetOptionString(_mpvHandle, "vd-lavc-threads", "0");
            Mpv.SetOptionString(_mpvHandle, "demuxer-lavf-o", "");
            Mpv.SetOptionString(_mpvHandle, "stream-lavf-o", "");
            
            // Khôi phục lại chế độ làm mượt và đồng bộ display-resample cho VOD/Video thông thường
            Mpv.SetOptionString(_mpvHandle, "interpolation", "yes");
            Mpv.SetOptionString(_mpvHandle, "tscale", "oversample");
            Mpv.SetOptionString(_mpvHandle, "video-sync", "display-resample");
            
            // VOD ưu tiên chất lượng hình ảnh hơn là rớt frame, nên chỉ cho drop ở tầng decoder nếu thật sự cần
            Mpv.SetOptionString(_mpvHandle, "framedrop", "decoder"); 
            Mpv.SetOptionString(_mpvHandle, "audio-buffer", "0.2");
            Mpv.SetOptionString(_mpvHandle, "video-latency-hacks", "no");
        }

        // Ensure proper clean up of previous state
        Mpv.Command(_mpvHandle, new[] { "stop" });

        Mpv.Command(_mpvHandle, new[] { "loadfile", url, "replace" });

        // Auto-reconnect logic cho luồng Live
        if (url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
        {
            Mpv.SetOptionString(_mpvHandle, "idle", "yes");
            _reconnectUrl = url;
            if (_reconnectTimer == null)
            {
                _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _reconnectTimer.Tick += (_, _) => CheckAndReconnect();
            }
            _reconnectTimer.Start();
        }
        else
        {
            _reconnectTimer?.Stop();
        }
    }

    private void CheckAndReconnect()
    {
        if (_mpvHandle == IntPtr.Zero) return;
        string? idleStr = Mpv.GetPropertyString(_mpvHandle, "core-idle");
        string? eofStr  = Mpv.GetPropertyString(_mpvHandle, "eof-reached");
        
        if (idleStr == "yes" || eofStr == "yes")
        {
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Stream died, auto-reconnecting to {_reconnectUrl}");
            _isReconnecting = true;
            Play(_reconnectUrl);
            _isReconnecting = true;
        }
    }

    public void SetShaderMode(string mode)
    {
        if (_mpvHandle == IntPtr.Zero) return;

        if (mode == "None")
        {
            // Xóa tất cả các shader
            Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "clr", "" });
            // Trả scale lại Spline36
            Mpv.SetOptionString(_mpvHandle, "scale", "spline36");
        }
        else if (mode == "CAS")
        {
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string krigPath = System.IO.Path.Combine(baseDir, "Assets", "Shaders", "KrigBilateral.glsl");
            string casPath = System.IO.Path.Combine(baseDir, "Assets", "Shaders", "CAS.glsl");
            
            // Luôn clear trước khi thêm mới
            Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "clr", "" });

            bool shaderLoaded = false;

            if (System.IO.File.Exists(krigPath))
            {
                // Thêm KrigBilateral để xử lý màu (Chroma)
                Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "append", krigPath });
                shaderLoaded = true;
            }

            if (System.IO.File.Exists(casPath))
            {
                // Thêm AMD FidelityFX CAS để tăng cường chi tiết, làm nét ảnh gốc
                Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "append", casPath });
                shaderLoaded = true;
            }

            if (shaderLoaded)
            {
                // Khi bật AI shader, dùng scale mặc định để shader tự upscale tối ưu nhất
                Mpv.SetOptionString(_mpvHandle, "scale", "ewa_lanczos");
            }
        }
        else if (mode == "FSRCNNX")
        {
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            string krigPath = System.IO.Path.Combine(baseDir, "Assets", "Shaders", "KrigBilateral.glsl");
            string fsrcnnxPath = System.IO.Path.Combine(baseDir, "Assets", "Shaders", "FSRCNNX_x2_16-0-4-1.glsl");
            
            // Luôn clear trước khi thêm mới
            Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "clr", "" });

            bool shaderLoaded = false;

            if (System.IO.File.Exists(krigPath))
            {
                Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "append", krigPath });
                shaderLoaded = true;
            }

            if (System.IO.File.Exists(fsrcnnxPath))
            {
                Mpv.Command(_mpvHandle, new[] { "change-list", "glsl-shaders", "append", fsrcnnxPath });
                shaderLoaded = true;
            }

            if (shaderLoaded)
            {
                Mpv.SetOptionString(_mpvHandle, "scale", "ewa_lanczos");
            }
        }
    }

    public void Stop()
    {
        if (_mpvHandle == IntPtr.Zero) return;
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Stop called");
        Mpv.Command(_mpvHandle, new string[] { "stop" });
    }

    public void Pause()
    {
        if (_mpvHandle == IntPtr.Zero) return;
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Pause called");
        Mpv.Command(_mpvHandle, new string[] { "cycle", "pause" });
    }

    public void UpdatePlaybackStats(HomeViewModel vm)
    {
        if (_mpvHandle == IntPtr.Zero || vm == null) return;

        string? width = Mpv.GetPropertyString(_mpvHandle, "width");
        string? height = Mpv.GetPropertyString(_mpvHandle, "height");
        string? vCodec = Mpv.GetPropertyString(_mpvHandle, "video-codec");
        string? aCodec = Mpv.GetPropertyString(_mpvHandle, "audio-codec");
        string? aSampleRate = Mpv.GetPropertyString(_mpvHandle, "audio-params/samplerate");
        string? aChannels = Mpv.GetPropertyString(_mpvHandle, "audio-params/channel-count");
        string? hwdec = Mpv.GetPropertyString(_mpvHandle, "hwdec-current");
        string? dropped = Mpv.GetPropertyString(_mpvHandle, "frame-drop-count");
        string? cacheSec = Mpv.GetPropertyString(_mpvHandle, "demuxer-cache-duration");

        if (string.IsNullOrEmpty(width) && string.IsNullOrEmpty(vCodec))
        {
            return;
        }

        vm.IsPlayerLoading = false;
        
        // 1. Resolution
        if (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height))
        {
            if (vm.CurrentShaderMode == "FSRCNNX" && int.TryParse(width, out int w) && int.TryParse(height, out int h))
            {
                vm.StatsResolution = $"{w}x{h} ➔ {w * 2}x{h * 2} (AI Upscaled / 60FPS+)";
            }
            else if (vm.CurrentShaderMode == "CAS" && int.TryParse(width, out int w2) && int.TryParse(height, out int h2))
            {
                vm.StatsResolution = $"{w2}x{h2} (Enhanced / 60FPS+)";
            }
            else
            {
                vm.StatsResolution = $"{width}x{height}";
            }
        }
        else
        {
            vm.StatsResolution = "-";
        }
        
        // 2. Real-time / Container FPS
        string? containerFps = Mpv.GetPropertyString(_mpvHandle, "fps");
        string? estimatedFps = Mpv.GetPropertyString(_mpvHandle, "estimated-vf-fps");
        double.TryParse(containerFps, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cFps);
        double.TryParse(estimatedFps, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double eFps);

        if (cFps > 0 && eFps > 0)
        {
            vm.StatsFps = $"{cFps:F0} fps (Luồng) | {eFps:F1} fps (Thực tế)";
        }
        else if (eFps > 0)
        {
            vm.StatsFps = $"{eFps:F1} fps (Thực tế)";
        }
        else if (cFps > 0)
        {
            vm.StatsFps = $"{cFps:F0} fps (Luồng)";
        }
        else
        {
            vm.StatsFps = "-";
        }

        // 3. Display FPS (estimated-display-fps)
        string? displayFpsStr = Mpv.GetPropertyString(_mpvHandle, "estimated-display-fps");
        if (double.TryParse(displayFpsStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double displayFpsVal) && displayFpsVal > 0)
        {
            vm.StatsDisplayFps = $"{displayFpsVal:F1} Hz";
        }
        else
        {
            vm.StatsDisplayFps = "-";
        }
        
        // 4. Codecs
        vm.StatsVideoCodec = !string.IsNullOrEmpty(vCodec) ? vCodec : "-";
        vm.StatsHwdec = (hwdec != "no" && !string.IsNullOrEmpty(hwdec)) ? $"{hwdec} (Giải mã cứng)" : "no (Giải mã mềm)";

        string? aCodecName = Mpv.GetPropertyString(_mpvHandle, "audio-codec-name");
        vm.StatsAudioCodec = !string.IsNullOrEmpty(aCodecName) ? aCodecName : (!string.IsNullOrEmpty(aCodec) ? aCodec : "-");
        vm.StatsAudioChannels = !string.IsNullOrEmpty(aChannels) ? $"{aChannels} kênh" : "-";

        double.TryParse(aSampleRate, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double srVal);
        vm.StatsAudioSampleRate = srVal > 0 ? $"{srVal / 1000:F1} kHz" : "-";
        
        // 5. Pixel Format
        string? pixelFormat = Mpv.GetPropertyString(_mpvHandle, "video-params/pixelformat");
        string? hwPixelFormat = Mpv.GetPropertyString(_mpvHandle, "video-params/hw-pixelformat");
        if (!string.IsNullOrEmpty(pixelFormat))
        {
            vm.StatsPixelFormat = !string.IsNullOrEmpty(hwPixelFormat) ? $"{pixelFormat} (HW: {hwPixelFormat})" : pixelFormat;
        }
        else
        {
            vm.StatsPixelFormat = "-";
        }

        // 6. Connection Status
        string? idle = Mpv.GetPropertyString(_mpvHandle, "core-idle");
        string? pausedForCache = Mpv.GetPropertyString(_mpvHandle, "paused-for-cache");
        
        if (_isReconnecting)
        {
            vm.StatsConnectionStatus = "Mất tín hiệu - Đang kết nối lại...";
        }
        else if (pausedForCache == "yes" || idle == "yes")
        {
            vm.StatsConnectionStatus = "Đang tải bộ đệm (Buffering...)";
        }
        else
        {
            vm.StatsConnectionStatus = "Đang phát ổn định (Live)";
        }

        if (!string.IsNullOrEmpty(width) && idle != "yes")
        {
            _isReconnecting = false;
        }

        // 7. Extended Stats: Frame Drops & VO Delayed
        string? voDropStr = Mpv.GetPropertyString(_mpvHandle, "vo-drop-frame-count");
        long.TryParse(dropped, out long currentDecDrop);
        long.TryParse(voDropStr, out long currentVoDrop);

        var now = DateTime.Now;
        if (_lastDropCheckTime != DateTime.MinValue)
        {
            var elapsedSec = (now - _lastDropCheckTime).TotalSeconds;
            if (elapsedSec >= 1.0)
            {
                if (currentDecDrop < _lastDecDrop || currentVoDrop < _lastVoDrop)
                {
                    _lastDecDrop = 0;
                    _lastVoDrop = 0;
                }
                
                var decRate = Math.Max(0, (currentDecDrop - _lastDecDrop) / elapsedSec);
                var voRate = Math.Max(0, (currentVoDrop - _lastVoDrop) / elapsedSec);
                var totalRate = Math.Round(decRate + voRate);
                
                if (totalRate > 0)
                {
                    vm.StatsDroppedFrames = $"{currentDecDrop} (Decoder) / {currentVoDrop} (VO) [Đang rớt {totalRate} fps]";
                }
                else
                {
                    vm.StatsDroppedFrames = $"{currentDecDrop} (Decoder) / {currentVoDrop} (VO) [Ổn định]";
                }

                // Packet Loss
                string? pktLoss = Mpv.GetPropertyString(_mpvHandle, "demuxer-cache-state/raw-input-rate");
                vm.StatsPacketLoss = !string.IsNullOrEmpty(pktLoss) ? $"{long.Parse(pktLoss) / 1024} KB/s (Input Rate)" : "0 (Khỏe)";

                // Live Edge Latency
                string? pts = Mpv.GetPropertyString(_mpvHandle, "playback-time");
                string? cacheEnd = Mpv.GetPropertyString(_mpvHandle, "demuxer-cache-time");

                if (double.TryParse(pts, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ptsVal) &&
                    double.TryParse(cacheEnd, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cacheEndVal))
                {
                    double liveLatencyMs = (cacheEndVal - ptsVal) * 1000;
                    if (liveLatencyMs > 0 && liveLatencyMs < 60000)
                    {
                        vm.StatsLiveLatency = $"~{liveLatencyMs:F0} ms (so với luồng gốc)";
                    }
                    else
                    {
                        vm.StatsLiveLatency = "Real-time (0 ms)";
                    }
                }
                else
                {
                    vm.StatsLiveLatency = "-";
                }
                
                _lastDecDrop = currentDecDrop;
                _lastVoDrop = currentVoDrop;
                _lastDropCheckTime = now;
            }
        }
        else
        {
            _lastDecDrop = currentDecDrop;
            _lastVoDrop = currentVoDrop;
            _lastDropCheckTime = now;
            vm.StatsDroppedFrames = $"{currentDecDrop} (Decoder) / {currentVoDrop} (VO)";
        }

        string? delayed = Mpv.GetPropertyString(_mpvHandle, "vo-delayed-frame-count");
        vm.StatsVoDelayed = !string.IsNullOrEmpty(delayed) ? $"{delayed} frames" : "0";

        // 8. Demuxer Cache State
        double.TryParse(cacheSec, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cacheSecVal);
        vm.StatsCacheState = cacheSecVal > 0 ? $"{cacheSecVal:F1} giây" : "0 giây (Low-latency mode — cache=no)";

        // 9. Bit Depth & Color Primaries
        string? bitDepth = Mpv.GetPropertyString(_mpvHandle, "video-params/color-depth");
        if (string.IsNullOrEmpty(bitDepth))
        {
            string? vFormat = Mpv.GetPropertyString(_mpvHandle, "video-format");
            if (vFormat != null && vFormat.Contains("10")) bitDepth = "10";
            else if (vFormat != null && vFormat.Contains("12")) bitDepth = "12";
            else if (vFormat != null) bitDepth = "8";
        }
        vm.StatsBitDepth = !string.IsNullOrEmpty(bitDepth) ? $"{bitDepth}-bit" : "8-bit (Mặc định)";

        string? colorPrimaries = Mpv.GetPropertyString(_mpvHandle, "video-params/primaries");
        vm.StatsColorPrimaries = !string.IsNullOrEmpty(colorPrimaries) ? colorPrimaries : "-";

        // 10. Network Speed & Real-time Bitrate
        string? aBitrateStr = Mpv.GetPropertyString(_mpvHandle, "audio-bitrate");
        string? vBitrateStr = Mpv.GetPropertyString(_mpvHandle, "video-bitrate");
        double.TryParse(aBitrateStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double aBitrate);
        double.TryParse(vBitrateStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double vBitrate);
        double totalBitrate = aBitrate + vBitrate;

        string vBitStr = vBitrate > 0 ? $"V: {(vBitrate / 1000000.0):F2} Mbps" : "V: -";
        string aBitStr = aBitrate > 0 ? $"A: {(aBitrate / 1000.0):F0} Kbps" : "A: -";
        vm.StatsBitrateDetails = $"{vBitStr} | {aBitStr}";
        vm.StatsRealTimeBitrate = totalBitrate > 0 ? $"{(totalBitrate / 1000000.0):F2} Mbps" : "-";

        string? cacheSpeedStr = Mpv.GetPropertyString(_mpvHandle, "cache-speed");
        if (double.TryParse(cacheSpeedStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cacheSpeed) && cacheSpeed > 0)
        {
            vm.StatsNetworkSpeed = $"{cacheSpeed / 1024:F0} KB/s (~{(cacheSpeed * 8 / 1000000.0):F2} Mbps)";
        }
        else
        {
            vm.StatsNetworkSpeed = "-";
        }

        // 11. A/V Sync
        string? avsyncStr = Mpv.GetPropertyString(_mpvHandle, "avsync");
        if (double.TryParse(avsyncStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double avsync))
            vm.StatsAvSync = $"{avsync * 1000:F1} ms";
        else
            vm.StatsAvSync = "-";

        // 12. Active Shaders & GPU Render Time
        string? glslShaders = Mpv.GetPropertyString(_mpvHandle, "glsl-shaders");
        if (string.IsNullOrEmpty(glslShaders))
        {
            vm.StatsActiveShaders = "None";
        }
        else
        {
            var files = glslShaders.Split(new[] { ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
                                   .Select(p => System.IO.Path.GetFileName(p));
            vm.StatsActiveShaders = string.Join(" + ", files);
        }
        
        long totalRenderTimeNs = 0;
        string? countStr = Mpv.GetPropertyString(_mpvHandle, "vo-passes/fresh/count");
        if (int.TryParse(countStr, out int count))
        {
            for (int i = 0; i < count; i++)
            {
                string? avgStr = Mpv.GetPropertyString(_mpvHandle, $"vo-passes/fresh/{i}/avg");
                if (long.TryParse(avgStr, out long avg))
                {
                    totalRenderTimeNs += avg;
                }
            }
        }

        string? vsyncJitter = Mpv.GetPropertyString(_mpvHandle, "vsync-jitter");
        double.TryParse(vsyncJitter, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double jitterVal);

        if (totalRenderTimeNs > 0)
        {
            double renderTimeMs = totalRenderTimeNs / 1000000.0;
            vm.StatsGpuRenderTime = $"{renderTimeMs:F2} ms / Jitter: {jitterVal * 1000:F2} ms";
        }
        else if (jitterVal > 0)
        {
            vm.StatsGpuRenderTime = $"Jitter: {jitterVal * 1000:F2} ms";
        }
        else
        {
            vm.StatsGpuRenderTime = "Chế độ Zero-copy (Độ trễ siêu thấp)";
        }

        // 13. Active GPU name & System GPUs
        string? activeGpu = Mpv.GetPropertyString(_mpvHandle, "gl-device-name");
        if (!string.IsNullOrEmpty(activeGpu))
        {
            vm.StatsActiveGpu = activeGpu;
        }

        if (vm.StatsGpuName == "-" || string.IsNullOrEmpty(vm.StatsGpuName))
        {
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                    var gpus = new System.Collections.Generic.List<string>();
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(name))
                        {
                            gpus.Add(name);
                        }
                    }
                    if (gpus.Count > 0)
                    {
                        var sortedGpus = gpus.OrderByDescending(g => g.Contains("NVIDIA") || g.Contains("AMD")).ToList();
                        string gpuList = string.Join(" / ", sortedGpus);
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => {
                            vm.StatsGpuName = gpuList;
                            if (string.IsNullOrEmpty(activeGpu) || activeGpu == "-")
                            {
                                // fallback active GPU to the primary one if gl-device-name isn't populated yet
                                vm.StatsActiveGpu = sortedGpus[0];
                            }
                        });
                    }
                    else
                    {
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => vm.StatsGpuName = "Không xác định");
                    }
                }
                catch { App.MainWindow?.DispatcherQueue.TryEnqueue(() => vm.StatsGpuName = "Không thể lấy thông tin"); }
            });
        }
    }

    private void Cleanup()
    {
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Cleanup starting...");
        
        IntPtr handleToDestroy = _mpvHandle;
        _mpvHandle = IntPtr.Zero;

        if (handleToDestroy != IntPtr.Zero)
        {
            // Tách cửa sổ hiển thị ra khỏi mpv trước khi hủy để tránh treo render thread
            Mpv.SetOptionString(handleToDestroy, "wid", "");
            
            // Chạy lệnh stop và destroy trên thread nền để không làm treo UI thread (gây đơ app khi chuyển trang)
            System.Threading.Tasks.Task.Run(() =>
            {
                Mpv.Command(handleToDestroy, new string[] { "stop" });
                Mpv.Destroy(handleToDestroy);
                System.Diagnostics.Debug.WriteLine("MpvPlayer: Destroyed in background.");
            });
        }

        if (_childHwnd != IntPtr.Zero)
        {
            DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        _initialized = false;
        _lastX = -1;
        _lastY = -1;
        _lastWidth = -1;
        _lastHeight = -1;
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Cleanup finished.");
    }
}

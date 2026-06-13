using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using IptvApp.Native;
using IptvApp.ViewModels;
using System.Linq;

using IptvApp.Models;

namespace IptvApp.Controls;

public class MpvPlayer : Grid
{
    private IntPtr _mpvHandle = IntPtr.Zero;
    private IntPtr _childHwnd = IntPtr.Zero;
    private bool _initialized;

    private DispatcherTimer? _posDebounce;
    private System.Timers.Timer? _advReconnectTimer = null;
    private string _reconnectUrl = string.Empty;
    private bool _isReconnecting = false;
    private int _reconnectAttempt = 0;
    private const int MaxReconnectDelay = 60;
    private const int BaseReconnectDelay = 3;

    private string? _cachedMulticastLocalIp = null;
    private DateTime _lastIpCacheTime = DateTime.MinValue;
    private readonly TimeSpan _ipCacheDuration = TimeSpan.FromSeconds(30);

    private bool _deinterlaceApplied = false;
    private string? _currentUrl = null;
    private bool _hdrToneMappingApplied = false;
    private bool _lastAiQualityMode = false;

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

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
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

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _cachedMulticastLocalIp = null;
        _lastIpCacheTime = DateTime.MinValue;
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Network address changed — IP cache invalidated.");
    }

    private void UpdateChildWindowPosition()
    {
        if (_childHwnd == IntPtr.Zero) return;

        try
        {
            // Get position of the control relative to the Window client area
            var transform = this.TransformToVisual(null);
            var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;

            int x = (int)Math.Round(position.X * scale);
            int y = (int)Math.Round(position.Y * scale);
            int width = (int)Math.Round(this.ActualWidth * scale);
            int height = (int)Math.Round(this.ActualHeight * scale);

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
        Play(url, _lastAiQualityMode);
    }

    public void Play(string url, bool aiQualityMode)
    {
        if (_mpvHandle == IntPtr.Zero) return;
        System.Diagnostics.Debug.WriteLine($"MpvPlayer: Play called for URL: {url}, aiQualityMode: {aiQualityMode}");
        _isReconnecting = false;
        _currentUrl = url;
        _lastAiQualityMode = aiQualityMode;
        _deinterlaceApplied = false;
        _hdrToneMappingApplied = false;

        bool isUdp = url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase);

        if (isUdp)
        {
            string? localIp = GetLocalIpForMulticast();
            string localAddrPart = !string.IsNullOrEmpty(localIp) ? $",localaddr={localIp}" : "";

            if (aiQualityMode)
            {
                // Tối ưu hóa chất lượng AI (chấp nhận trễ lớn để chạy shader tốt nhất)
                Mpv.SetOptionString(_mpvHandle, "profile", "default"); // dùng profile mặc định, không ép low-latency

                // Tăng buffer socket của FFmpeg để tránh mất gói UDP khi GPU xử lý AI làm nghẽn CPU
                string lavfOptions = "ignore_pcr_discontinuity=1,skip_clear=1" + localAddrPart;
                Mpv.SetOptionString(_mpvHandle, "demuxer-lavf-o", lavfOptions);
                Mpv.SetOptionString(_mpvHandle, "stream-lavf-o", "buffer_size=8388608,fifo_size=500000" + localAddrPart); // 8MB buffer

                Mpv.SetOptionString(_mpvHandle, "demuxer-thread", "yes");
                
                // Cấu hình đệm Demuxer lớn để có dữ liệu bù đắp
                Mpv.SetOptionString(_mpvHandle, "cache", "yes");
                Mpv.SetOptionString(_mpvHandle, "demuxer-max-bytes", "150MiB");
                Mpv.SetOptionString(_mpvHandle, "demuxer-max-back-bytes", "50MiB");
                Mpv.SetOptionString(_mpvHandle, "stream-buffer-size", "4MiB");

                // Cho phép mượt chuyển động và đồng bộ display-resample
                Mpv.SetOptionString(_mpvHandle, "interpolation", "yes");
                Mpv.SetOptionString(_mpvHandle, "tscale", "oversample");
                Mpv.SetOptionString(_mpvHandle, "video-sync", "display-resample");
                Mpv.SetOptionString(_mpvHandle, "framedrop", "decoder"); // Không rớt khung hình ở VO để giữ trọn khung hình qua AI
                
                Mpv.SetOptionString(_mpvHandle, "audio-buffer", "2.0"); // đệm âm thanh lớn
                Mpv.SetOptionString(_mpvHandle, "mc", "1.5"); // ép đồng bộ A/V nhanh
                Mpv.SetOptionString(_mpvHandle, "autosync", "30"); // tự động đồng bộ A/V
                Mpv.SetOptionString(_mpvHandle, "video-latency-hacks", "no");
                Mpv.SetOptionString(_mpvHandle, "vd-lavc-threads", "0"); // tự động luồng giải mã tối đa
                
                // Bật deband
                Mpv.SetOptionString(_mpvHandle, "deband", "yes");
                Mpv.SetOptionString(_mpvHandle, "deband-iterations", "2");
                Mpv.SetOptionString(_mpvHandle, "deband-threshold", "48");
                Mpv.SetOptionString(_mpvHandle, "deband-range", "12");
                Mpv.SetOptionString(_mpvHandle, "deband-grain", "24");
            }
            else
            {
                // Chế độ trễ thấp nguyên bản cho UDP
                Mpv.SetOptionString(_mpvHandle, "profile", "low-latency");

                string lavfOptions = "fflags=nobuffer,flags=low_delay,ignore_pcr_discontinuity=1,skip_clear=1" + localAddrPart;
                Mpv.SetOptionString(_mpvHandle, "demuxer-lavf-o", lavfOptions);
                Mpv.SetOptionString(_mpvHandle, "stream-lavf-o", "buffer_size=2097152,fifo_size=50000" + localAddrPart);

                Mpv.SetOptionString(_mpvHandle, "demuxer-thread", "yes");
                
                Mpv.SetOptionString(_mpvHandle, "cache", "yes");
                Mpv.SetOptionString(_mpvHandle, "demuxer-max-bytes", "10MiB");
                Mpv.SetOptionString(_mpvHandle, "demuxer-max-back-bytes", "0");
                Mpv.SetOptionString(_mpvHandle, "stream-buffer-size", "2MiB");

                Mpv.SetOptionString(_mpvHandle, "interpolation", "no");
                Mpv.SetOptionString(_mpvHandle, "video-sync", "audio");
                Mpv.SetOptionString(_mpvHandle, "framedrop", "vo");
                
                Mpv.SetOptionString(_mpvHandle, "audio-buffer", "0.2");
                Mpv.SetOptionString(_mpvHandle, "video-latency-hacks", "yes");
                Mpv.SetOptionString(_mpvHandle, "vd-lavc-threads", "1");
                Mpv.SetOptionString(_mpvHandle, "deband", "no");
            }
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
            
            Mpv.SetOptionString(_mpvHandle, "interpolation", "yes");
            Mpv.SetOptionString(_mpvHandle, "tscale", "oversample");
            Mpv.SetOptionString(_mpvHandle, "video-sync", "display-resample");
            
            Mpv.SetOptionString(_mpvHandle, "framedrop", "decoder"); 
            Mpv.SetOptionString(_mpvHandle, "audio-buffer", aiQualityMode ? "2.0" : "0.2");
            if (aiQualityMode)
            {
                Mpv.SetOptionString(_mpvHandle, "mc", "1.5");
                Mpv.SetOptionString(_mpvHandle, "autosync", "30");
            }
            else
            {
                Mpv.SetOptionString(_mpvHandle, "mc", "0.1");
                Mpv.SetOptionString(_mpvHandle, "autosync", "0");
            }
            Mpv.SetOptionString(_mpvHandle, "video-latency-hacks", "no");

            Mpv.SetOptionString(_mpvHandle, "deband", "yes");
            Mpv.SetOptionString(_mpvHandle, "deband-iterations", "2");
            Mpv.SetOptionString(_mpvHandle, "deband-threshold", "48");
            Mpv.SetOptionString(_mpvHandle, "deband-range", "12");
            Mpv.SetOptionString(_mpvHandle, "deband-grain", "24");
        }

        // 1. Set demuxer-readahead-secs trước loadfile
        if (!isUdp && !aiQualityMode)
        {
            Mpv.SetOptionString(_mpvHandle, "demuxer-readahead-secs", "0.5");
        }
        else if (aiQualityMode)
        {
            Mpv.SetOptionString(_mpvHandle, "demuxer-readahead-secs", "3.0"); // buffer trước 3 giây
        }

        // 2. Dùng loadfile replace trực tiếp — không cần stop trước
        Mpv.Command(_mpvHandle, new[] { "loadfile", url, "replace" });

        // Auto-reconnect logic cho luồng Live
        if (isUdp)
        {
            Mpv.SetOptionString(_mpvHandle, "idle", "yes");
            EnableAutoReconnect(url);
        }
        else
        {
            DisableAutoReconnect();
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

    public bool IsPaused
    {
        get
        {
            if (_mpvHandle == IntPtr.Zero) return false;
            return Mpv.GetPropertyString(_mpvHandle, "pause") == "yes";
        }
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

        // Tự động chạy deinterlacing và HDR mapping khi có thông tin luồng phát
        ApplyDeinterlaceIfNeeded();
        ApplyHdrToneMappingIfNeeded();
        
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

        string fpsLabel = "";
        if (Math.Abs(cFps - 50.0) < 0.1 || Math.Abs(cFps - 25.0) < 0.1)
            fpsLabel = " [50Hz PAL]";
        else if (Math.Abs(cFps - 60.0) < 0.1 || Math.Abs(cFps - 30.0) < 0.1 || Math.Abs(cFps - 29.97) < 0.1 || Math.Abs(cFps - 59.94) < 0.1)
            fpsLabel = " [60Hz NTSC]";

        if (cFps > 0 && eFps > 0)
        {
            vm.StatsFps = $"{cFps:F0} fps (Luồng){fpsLabel} | {eFps:F1} fps (Thực tế)";
        }
        else if (eFps > 0)
        {
            vm.StatsFps = $"{eFps:F1} fps (Thực tế)";
        }
        else if (cFps > 0)
        {
            vm.StatsFps = $"{cFps:F0} fps (Luồng){fpsLabel}";
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
        string? level = Mpv.GetPropertyString(_mpvHandle, "video-params/level");
        string levelPart = !string.IsNullOrEmpty(level) ? $" (L{level})" : "";
        vm.StatsVideoCodec = !string.IsNullOrEmpty(vCodec) ? $"{vCodec}{levelPart}" : "-";
        vm.StatsHwdec = (hwdec != "no" && !string.IsNullOrEmpty(hwdec)) ? $"{hwdec} (Giải mã cứng)" : "no (Giải mã mềm)";

        string? aCodecName = Mpv.GetPropertyString(_mpvHandle, "audio-codec-name");
        string audioCodecLabel = !string.IsNullOrEmpty(aCodecName) ? aCodecName : (!string.IsNullOrEmpty(aCodec) ? aCodec : "-");
        if (audioCodecLabel.Contains("mp3", StringComparison.OrdinalIgnoreCase))
        {
            audioCodecLabel += " (Cảnh báo: MP3 bất thường)";
        }
        vm.StatsAudioCodec = audioCodecLabel;
        vm.StatsAudioChannels = !string.IsNullOrEmpty(aChannels) ? $"{aChannels} kênh" : "-";

        double.TryParse(aSampleRate, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double srVal);
        vm.StatsAudioSampleRate = srVal > 0 ? $"{srVal / 1000:F1} kHz" : "-";
        
        // 5. Pixel Format
        string? pixelFormat = Mpv.GetPropertyString(_mpvHandle, "video-params/pixelformat");
        string? hwPixelFormat = Mpv.GetPropertyString(_mpvHandle, "video-params/hw-pixelformat");
        if (!string.IsNullOrEmpty(pixelFormat))
        {
            string zeroCopyLabel = "";
            if (!string.IsNullOrEmpty(hwPixelFormat) && hwPixelFormat.Contains("d3d11"))
            {
                zeroCopyLabel = " (Zero-copy)";
            }
            vm.StatsPixelFormat = !string.IsNullOrEmpty(hwPixelFormat) ? $"{pixelFormat} (HW: {hwPixelFormat}){zeroCopyLabel}" : pixelFormat;
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

        // Cập nhật các thông số mới cho Stats Panel
        string? scanType = Mpv.GetPropertyString(_mpvHandle, "video-params/scan-type");
        vm.StatsDeinterlace = _deinterlaceApplied
            ? "bwdif (Đang khử quét xen kẽ)"
            : (scanType == "progressive" ? "Không cần (Progressive)" : "Không áp dụng");

        string? debandVal = Mpv.GetPropertyString(_mpvHandle, "deband");
        vm.StatsDeband = debandVal == "yes" ? "Bật (iterations=2, threshold=48)" : "Tắt";

        string? primaries = Mpv.GetPropertyString(_mpvHandle, "video-params/primaries");
        string? transferFunc = Mpv.GetPropertyString(_mpvHandle, "video-params/gamma");
        bool isHdr = primaries == "bt.2020" || transferFunc == "pq" || transferFunc == "hlg" || transferFunc == "smpte-st-2084";
        vm.StatsHdrStatus = isHdr
            ? $"HDR ({transferFunc?.ToUpper() ?? "?"}) → Tone mapping bt.2390"
            : "SDR";

        string? paused = Mpv.GetPropertyString(_mpvHandle, "pause");
        string? eofReached = Mpv.GetPropertyString(_mpvHandle, "eof-reached");
        if (_isReconnecting)
            vm.StatsStreamStatus = "Mất kết nối — Đang kết nối lại";
        else if (idle == "yes")
            vm.StatsStreamStatus = "Chờ (Idle)";
        else if (eofReached == "yes")
            vm.StatsStreamStatus = "Kết thúc (EOF)";
        else if (paused == "yes")
            vm.StatsStreamStatus = "Tạm dừng (Paused)";
        else
            vm.StatsStreamStatus = "Đang phát (Active)";

        string? colormatrix = Mpv.GetPropertyString(_mpvHandle, "video-params/colormatrix");
        string? colorlevels = Mpv.GetPropertyString(_mpvHandle, "video-params/colorlevels");
        vm.StatsColorMatrix = !string.IsNullOrEmpty(colormatrix) ? colormatrix : "-";
        vm.StatsColorRange = !string.IsNullOrEmpty(colorlevels) ? colorlevels : "-";

        if (!string.IsNullOrEmpty(width) && !_isReconnecting && idle != "yes")
        {
            if (_currentUrl != null && !_currentUrl.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) && !_currentUrl.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase))
            {
                Mpv.SetOptionString(_mpvHandle, "demuxer-readahead-secs", "1.0");
            }
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
        vm.StatsRealTimeBitrate = totalBitrate > 0 ? $"{(totalBitrate / 1000000.0):F2} Mbps (V: {(vBitrate / 1000000.0):F2} / A: {(aBitrate / 1000000.0):F2})" : "-";

        string? cacheSpeedStr = Mpv.GetPropertyString(_mpvHandle, "cache-speed");
        if (double.TryParse(cacheSpeedStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cacheSpeed) && cacheSpeed > 0)
        {
            double speedMbps = (cacheSpeed * 8) / 1000000.0;
            vm.StatsNetworkSpeed = $"{speedMbps:F2} Mbps";
        }
        else
        {
            vm.StatsNetworkSpeed = "-";
        }

        // 11. A/V Sync
        string? avsyncStr = Mpv.GetPropertyString(_mpvHandle, "avsync");
        if (double.TryParse(avsyncStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double avsync))
        {
            double avsyncMs = avsync * 1000;
            string syncLabel = "";
            if (Math.Abs(avsyncMs) < 15.0)
                syncLabel = " [Đồng bộ tốt]";
            else if (Math.Abs(avsyncMs) < 50.0)
                syncLabel = " [Lệch nhẹ]";
            else
                syncLabel = " [Lệch đáng kể]";

            vm.StatsAvSync = $"{avsyncMs:F1} ms{syncLabel}";
        }
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
            string perfLabel = "";
            if (renderTimeMs < 5.0)
                perfLabel = " [Xuất sắc]";
            else if (renderTimeMs < 12.0)
                perfLabel = " [Tốt]";
            else if (renderTimeMs < 20.0)
                perfLabel = " [Chấp nhận]";
            else
                perfLabel = " [Quá tải]";

            vm.StatsGpuRenderTime = $"{renderTimeMs:F2} ms{perfLabel} / Jitter: {jitterVal * 1000:F2} ms";
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

    private string? GetLocalIpForMulticast(string multicastTarget = "239.255.255.250")
    {
        if (_cachedMulticastLocalIp != null && DateTime.Now - _lastIpCacheTime < _ipCacheDuration)
            return _cachedMulticastLocalIp;

        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);

            socket.Connect(multicastTarget, 65530);
            var ep = socket.LocalEndPoint as System.Net.IPEndPoint;
            _cachedMulticastLocalIp = ep?.Address.ToString();
            _lastIpCacheTime = DateTime.Now;

            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Multicast local IP detected: {_cachedMulticastLocalIp}");
            return _cachedMulticastLocalIp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: GetLocalIpForMulticast failed: {ex.Message}");
            return null;
        }
    }

    public void ApplyDeinterlaceIfNeeded()
    {
        if (_mpvHandle == IntPtr.Zero) return;

        string? scanType = Mpv.GetPropertyString(_mpvHandle, "video-params/scan-type");
        string? fieldOrder = Mpv.GetPropertyString(_mpvHandle, "video-params/field-order");

        bool isInterlaced = scanType == "interlaced"
            || fieldOrder == "top-first"
            || fieldOrder == "bottom-first";

        if (isInterlaced && !_deinterlaceApplied)
        {
            bool isLiveUdp = _currentUrl?.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) == true
                || _currentUrl?.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase) == true;

            string bwdifMode = isLiveUdp ? "field" : "frame";
            string bwdifParity = fieldOrder == "bottom-first" ? "1" : "0";

            Mpv.Command(_mpvHandle, new[]
            {
                "vf", "add",
                $"bwdif=mode={bwdifMode}:parity={bwdifParity}:deint=interlaced"
            });

            _deinterlaceApplied = true;
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Deinterlace applied — bwdif mode={bwdifMode}, parity={bwdifParity}, fieldOrder={fieldOrder}");
        }
        else if (!isInterlaced && _deinterlaceApplied)
        {
            Mpv.Command(_mpvHandle, new[] { "vf", "del", "bwdif" });
            _deinterlaceApplied = false;
            System.Diagnostics.Debug.WriteLine("MpvPlayer: Deinterlace removed — stream is progressive.");
        }
    }

    public void ApplyHdrToneMappingIfNeeded()
    {
        if (_mpvHandle == IntPtr.Zero) return;

        string? primaries = Mpv.GetPropertyString(_mpvHandle, "video-params/primaries");
        string? transferFunc = Mpv.GetPropertyString(_mpvHandle, "video-params/gamma");

        bool isHdr = primaries == "bt.2020"
            || transferFunc == "pq"
            || transferFunc == "hlg"
            || transferFunc == "smpte-st-2084";

        if (isHdr && !_hdrToneMappingApplied)
        {
            Mpv.SetOptionString(_mpvHandle, "tone-mapping", "bt.2390");
            Mpv.SetOptionString(_mpvHandle, "hdr-compute-peak", "yes");
            Mpv.SetOptionString(_mpvHandle, "gamut-mapping-mode", "desaturate");
            Mpv.SetOptionString(_mpvHandle, "target-colorspace-hint", "yes");

            _hdrToneMappingApplied = true;
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: HDR tone mapping enabled — primaries={primaries}, transfer={transferFunc}, algorithm=bt.2390");
        }
        else if (!isHdr && _hdrToneMappingApplied)
        {
            Mpv.SetOptionString(_mpvHandle, "tone-mapping", "auto");
            Mpv.SetOptionString(_mpvHandle, "hdr-compute-peak", "no");
            _hdrToneMappingApplied = false;
            System.Diagnostics.Debug.WriteLine("MpvPlayer: HDR tone mapping disabled — stream is SDR.");
        }
    }

    public void EnableAutoReconnect(string url)
    {
        _reconnectUrl = url;
        _reconnectAttempt = 0;

        _advReconnectTimer?.Stop();
        _advReconnectTimer?.Dispose();
        _advReconnectTimer = new System.Timers.Timer(BaseReconnectDelay * 1000);
        _advReconnectTimer.Elapsed += OnReconnectTick;
        _advReconnectTimer.AutoReset = false; // one-shot, reschedule manually
        _advReconnectTimer.Start();
    }

    public void DisableAutoReconnect()
    {
        _advReconnectTimer?.Stop();
        _advReconnectTimer?.Dispose();
        _advReconnectTimer = null;
        _reconnectAttempt = 0;
    }

    private void OnReconnectTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_mpvHandle == IntPtr.Zero || _reconnectUrl == null) return;

        string? coreIdle   = Mpv.GetPropertyString(_mpvHandle, "core-idle");
        string? eofReached = Mpv.GetPropertyString(_mpvHandle, "eof-reached");

        bool streamDead = coreIdle == "yes" || eofReached == "yes";

        if (streamDead)
        {
            _reconnectAttempt++;

            double nextDelay = Math.Min(
                BaseReconnectDelay * Math.Pow(2, _reconnectAttempt - 1),
                MaxReconnectDelay);

            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Reconnect attempt #{_reconnectAttempt}, next retry in {nextDelay:F0}s");

            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                var vm = App.Services.GetService(typeof(HomeViewModel)) as HomeViewModel;
                if (vm != null)
                {
                    vm.StatsConnectionStatus = $"Mất tín hiệu — Đang kết nối lại (lần {_reconnectAttempt}, thử lại sau {nextDelay:F0}s...)";
                }
            });

            ResolveAndReconnect(_reconnectUrl);

            if (_advReconnectTimer != null)
            {
                _advReconnectTimer.Interval = nextDelay * 1000;
                _advReconnectTimer.Start();
            }
        }
        else
        {
            _reconnectAttempt = 0;
            System.Diagnostics.Debug.WriteLine("MpvPlayer: Stream recovered, reconnect counter reset.");

            if (_advReconnectTimer != null)
            {
                _advReconnectTimer.Interval = BaseReconnectDelay * 1000;
                _advReconnectTimer.Start();
            }
        }
    }

    private async void ResolveAndReconnect(string url)
    {
        string resolvedUrl = url;

        try
        {
            var uri = new Uri(url);
            bool isIpAddress = System.Net.IPAddress.TryParse(uri.Host, out _);

            if (!isIpAddress)
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
                if (addresses.Length > 0)
                {
                    var builder = new UriBuilder(uri) { Host = addresses[0].ToString() };
                    resolvedUrl = builder.ToString();
                    System.Diagnostics.Debug.WriteLine($"MpvPlayer: DNS resolved {uri.Host} → {addresses[0]}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: DNS resolve failed: {ex.Message}, using original URL");
        }

        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_mpvHandle == IntPtr.Zero) return;
            Mpv.Command(_mpvHandle, new[] { "loadfile", resolvedUrl, "replace" });
        });
    }

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
                ApplyShaderIfExists(shaderDir, "KrigBilateral.glsl");
                Mpv.SetOptionString(_mpvHandle, "scale", "bilinear");
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
            System.Diagnostics.Debug.WriteLine($"MpvPlayer: Shader not found: {path}");
    }

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
                await System.Net.Dns.GetHostAddressesAsync(uri.Host);
                System.Diagnostics.Debug.WriteLine($"MpvPlayer: Prefetch DNS resolved: {uri.Host}");
            }
        }
        catch { /* Ignore prefetch errors */ }
    }

    private void Cleanup()
    {
        System.Diagnostics.Debug.WriteLine("MpvPlayer: Cleanup starting...");

        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        DisableAutoReconnect();
        
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

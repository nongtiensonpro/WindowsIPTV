using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using IptvApp.Core.Models;
using IptvApp.Core.Services;

namespace IptvApp.Controls;

public sealed partial class ScanDialog : ContentDialog
{
    private readonly List<Channel> _channelsToScan;
    private readonly ObservableCollection<ChannelHealthResult> _scanResults = new();
    private CancellationTokenSource? _cts;
    private bool _isScanning = false;
    private bool _isCompleted = false;

    public ScanDialog(List<Channel> channels)
    {
        this.InitializeComponent();
        _channelsToScan = channels ?? new List<Channel>();
        TotalChannelsText.Text = $"{_channelsToScan.Count} kênh";
        
        ResultsListView.ItemsSource = _scanResults;
    }

    private void TimeoutSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (TimeoutValueText != null)
        {
            TimeoutValueText.Text = $"{(int)e.NewValue} giây";
        }
    }

    private void ParallelSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ParallelValueText != null)
        {
            ParallelValueText.Text = $"{(int)e.NewValue} luồng";
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Chặn đóng dialog mặc định nếu đang ở chế độ Config hoặc Scanning
        if (!_isCompleted)
        {
            args.Cancel = true;
        }

        if (!_isScanning && !_isCompleted)
        {
            // Bắt đầu quét
            _isScanning = true;
            ConfigPanel.Visibility = Visibility.Collapsed;
            ScanProgressGrid.Visibility = Visibility.Visible;
            
            // Cập nhật trạng thái nút
            this.PrimaryButtonText = "Đang quét...";
            this.IsPrimaryButtonEnabled = false;
            this.CloseButtonText = "Hủy quét";

            int timeout = (int)TimeoutSlider.Value;
            int maxParallel = (int)ParallelSlider.Value;

            _cts = new CancellationTokenSource();

            var progress = new Progress<ChannelScanProgress>(OnScanProgress);
            var checker = new ChannelHealthChecker();

            try
            {
                var results = await checker.CheckAllAsync(_channelsToScan, maxParallel, timeout, progress, _cts.Token);
                OnScanFinished(results);
            }
            catch (OperationCanceledException)
            {
                ScanStatusTitleText.Text = "Đã hủy quét kênh!";
                CurrentChannelCheckingText.Text = "Người dùng đã hủy quá trình quét.";
                OnScanFinished(null);
            }
            catch (Exception ex)
            {
                ScanStatusTitleText.Text = "Có lỗi xảy ra khi quét!";
                CurrentChannelCheckingText.Text = ex.Message;
                OnScanFinished(null);
            }
        }
        else if (_isCompleted)
        {
            // Xuất file M3U (đã hoàn thành quét)
            args.Cancel = true; // Chặn đóng dialog để người dùng xuất file xong mới đóng thủ công
            await ExportAliveChannelsAsync();
        }
    }

    private void ContentDialog_CloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_isScanning)
        {
            // Nếu đang quét, CloseButton hoạt động như nút "Hủy quét"
            args.Cancel = true; // Chặn đóng dialog
            CancelScan();
        }
    }

    private void CancelScan()
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            CurrentChannelCheckingText.Text = "Đang hủy quét...";
        }
    }

    private void OnScanProgress(ChannelScanProgress progressInfo)
    {
        // Cập nhật giao diện tiến trình
        ScanProgressBar.Value = progressInfo.ProgressPercentage;
        ProgressPercentageText.Text = $"{(int)progressInfo.ProgressPercentage}%";
        CurrentChannelCheckingText.Text = $"Đang quét: {progressInfo.CurrentChannelName}";

        CompletedCountText.Text = progressInfo.Completed.ToString();
        TotalCountText.Text = progressInfo.Total.ToString();
        AliveCountText.Text = progressInfo.Alive.ToString();
        DeadCountText.Text = progressInfo.Dead.ToString();
        
        // Tính số lượng Unknown
        int unknown = progressInfo.Completed - progressInfo.Alive - progressInfo.Dead;
        UnknownCountText.Text = unknown >= 0 ? unknown.ToString() : "0";

        if (progressInfo.LastResult != null)
        {
            _scanResults.Add(progressInfo.LastResult);
            
            // Tự động cuộn ListView xuống cuối để người dùng xem real-time
            if (_scanResults.Count > 0)
            {
                ResultsListView.ScrollIntoView(_scanResults[^1]);
            }
        }
    }

    private void OnScanFinished(List<ChannelHealthResult>? results)
    {
        _isScanning = false;
        _isCompleted = true;

        this.IsPrimaryButtonEnabled = true;
        this.PrimaryButtonText = "Xuất file M3U";
        this.CloseButtonText = "Đóng";

        ScanStatusTitleText.Text = "Quét kênh hoàn tất!";
        CurrentChannelCheckingText.Text = "Đã quét xong tất cả các kênh.";
        
        // Thống kê chất lượng
        int good = _scanResults.Count(r => r.Quality == SignalQuality.Good);
        int fair = _scanResults.Count(r => r.Quality == SignalQuality.Fair);
        int poor = _scanResults.Count(r => r.Quality == SignalQuality.Poor);

        GoodQualityText.Text = $"{good} kênh";
        FairQualityText.Text = $"{fair} kênh";
        PoorQualityText.Text = $"{poor} kênh";

        QualitySummaryBorder.Visibility = Visibility.Visible;
        CompletedMessageText.Visibility = Visibility.Visible;
    }

    private async Task ExportAliveChannelsAsync()
    {
        // Lấy danh sách kênh sống (Alive) hoặc chưa xác định (Unknown) để tránh bỏ sót
        var aliveChannels = _scanResults
            .Where(r => r.Status == ChannelStatus.Alive || r.Status == ChannelStatus.Unknown)
            .Select(r => r.Channel)
            .ToList();

        if (aliveChannels.Count == 0)
        {
            var noChannelDialog = new ContentDialog
            {
                Title = "Thông báo",
                Content = "Không tìm thấy kênh nào còn sống hoặc không xác định để xuất file.",
                CloseButtonText = "Đồng ý",
                XamlRoot = this.XamlRoot
            };
            await noChannelDialog.ShowAsync();
            return;
        }

        try
        {
            var savePicker = new FileSavePicker();
            
            // Lấy HWND của MainWindow trong WinUI 3
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("M3U Playlist File", new List<string>() { ".m3u" });
            savePicker.SuggestedFileName = $"IPTV_Alive_Channels_{DateTime.Now:yyyyMMdd_HHmmss}";

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                await M3uExporter.ExportToFileAsync(file.Path, aliveChannels);
                
                var successDialog = new ContentDialog
                {
                    Title = "Xuất file thành công",
                    Content = $"Đã xuất {aliveChannels.Count} kênh hoạt động ra file:\n{file.Name}",
                    CloseButtonText = "Tuyệt vời",
                    XamlRoot = this.XamlRoot
                };
                await successDialog.ShowAsync();
                
                // Đóng dialog quét sau khi xuất thành công
                this.Hide();
            }
        }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Lỗi xuất file",
                Content = $"Không thể xuất file: {ex.Message}",
                CloseButtonText = "Đóng",
                XamlRoot = this.XamlRoot
            };
            await errorDialog.ShowAsync();
        }
    }

    // Các hàm Helper phục vụ x:Bind hiển thị trong DataTemplate của ListView

    public static string GetStatusGlyph(ChannelStatus status)
    {
        return status switch
        {
            ChannelStatus.Alive => "\uE73E",  // CheckMark
            ChannelStatus.Dead => "\uF13C",   // StatusErrorFull
            ChannelStatus.Timeout => "\uE10A",// Warning
            _ => "\uE9CE"                     // Help
        };
    }

    public static Brush GetStatusColor(ChannelStatus status)
    {
        var color = status switch
        {
            ChannelStatus.Alive => ColorHelper.FromArgb(255, 34, 197, 94),   // #22C55E (xanh lá)
            ChannelStatus.Dead => ColorHelper.FromArgb(255, 239, 68, 68),    // #EF4444 (đỏ)
            ChannelStatus.Timeout => ColorHelper.FromArgb(255, 245, 158, 11), // #F59E0B (cam)
            _ => ColorHelper.FromArgb(255, 156, 163, 175)                     // #9CA3AF (xám)
        };
        return new SolidColorBrush(color);
    }

    public static string GetQualityText(SignalQuality quality)
    {
        return quality switch
        {
            SignalQuality.Good => "Tốt",
            SignalQuality.Fair => "Trung bình",
            SignalQuality.Poor => "Yếu",
            _ => "Không có"
        };
    }

    public static Brush GetQualityColor(SignalQuality quality)
    {
        var color = quality switch
        {
            SignalQuality.Good => ColorHelper.FromArgb(255, 34, 197, 94),
            SignalQuality.Fair => ColorHelper.FromArgb(255, 245, 158, 11),
            SignalQuality.Poor => ColorHelper.FromArgb(255, 239, 68, 68),
            _ => ColorHelper.FromArgb(255, 156, 163, 175)
        };
        return new SolidColorBrush(color);
    }

    public static string GetBitrateText(double bitrateKbps, ChannelStatus status)
    {
        if (status != ChannelStatus.Alive || bitrateKbps <= 0) return string.Empty;
        
        if (bitrateKbps >= 1000)
        {
            return $"{(bitrateKbps / 1000.0):F1} Mbps";
        }
        return $"{(int)bitrateKbps} Kbps";
    }

    public static string GetResponseTimeText(int responseTimeMs, ChannelStatus status)
    {
        if (status == ChannelStatus.Dead) return "Lỗi kết nối";
        if (status == ChannelStatus.Timeout) return "Timeout";
        if (status == ChannelStatus.Unknown) return "Không rõ";
        
        return $"{responseTimeMs} ms";
    }
}

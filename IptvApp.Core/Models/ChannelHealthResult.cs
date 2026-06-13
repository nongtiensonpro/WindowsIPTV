using System;

namespace IptvApp.Core.Models;

public enum ChannelStatus
{
    Alive,      // Kênh hoạt động, nhận được dữ liệu video/audio
    Dead,       // Kênh không phản hồi hoặc lỗi kết nối
    Timeout,    // Kênh không phản hồi trong thời gian chờ
    Unknown     // Không thể xác định (VD: UDP ngoài mạng nội bộ)
}

public enum SignalQuality
{
    Good,       // Bitrate >= 2 Mbps, Response < 2s
    Fair,       // Bitrate >= 500 Kbps, Response < 5s  
    Poor,       // Bitrate < 500 Kbps hoặc Response > 5s
    None        // Không có tín hiệu
}

public class ChannelHealthResult
{
    public Channel Channel { get; set; } = null!;
    public ChannelStatus Status { get; set; }
    public int ResponseTimeMs { get; set; }
    public long ReceivedBytes { get; set; }
    public double EstimatedBitrateKbps { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContentType { get; set; }
    public SignalQuality Quality { get; set; }
}

public class ChannelScanProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Alive { get; set; }
    public int Dead { get; set; }
    public string CurrentChannelName { get; set; } = string.Empty;
    public double ProgressPercentage => Total > 0 ? (double)Completed / Total * 100 : 0;
    public ChannelHealthResult? LastResult { get; set; }
}

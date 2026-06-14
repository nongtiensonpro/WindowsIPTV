using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using IptvApp.Core.Models;
using IptvApp.Core.Data;
using Microsoft.EntityFrameworkCore;
using System.IO;
using IptvApp.Core.Services;
using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using IptvApp.Models;

namespace IptvApp.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly AppDbContext _dbContext;
    private System.Collections.Generic.List<Channel> _allChannels = new();

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string PlaylistUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlayingChannelName { get; set; } = "Chưa chọn kênh";

    [ObservableProperty]
    public partial string StatsResolution { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsFps { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsVideoCodec { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsHwdec { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsAudioCodec { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsAudioChannels { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsAudioSampleRate { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsDroppedFrames { get; set; } = "0";

    [ObservableProperty]
    public partial string StatsCacheState { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsActiveShaders { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsGpuRenderTime { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsGpuName { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsBitDepth { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsColorPrimaries { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsRealTimeBitrate { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsNetworkSpeed { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsAvSync { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsLiveLatency { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsPacketLoss { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsDisplayFps { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsVoDelayed { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsBitrateDetails { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsPixelFormat { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsConnectionStatus { get; set; } = "Đang dừng";

    [ObservableProperty]
    public partial string StatsActiveGpu { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsDeinterlace { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsDeband { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsHdrStatus { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsStreamStatus { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsColorMatrix { get; set; } = "-";

    [ObservableProperty]
    public partial string StatsColorRange { get; set; } = "-";

    [ObservableProperty]
    public partial ContentProfile SelectedProfile { get; set; } = ContentProfile.Auto;

    [ObservableProperty]
    public partial bool IsAiQualityEnabled { get; set; } = false;

    [ObservableProperty]
    public partial PlayerFeatureMode DeinterlaceMode { get; set; } = PlayerFeatureMode.Auto;

    [ObservableProperty]
    public partial PlayerFeatureMode DebandMode { get; set; } = PlayerFeatureMode.Auto;

    [ObservableProperty]
    public partial PlayerFeatureMode HdrMode { get; set; } = PlayerFeatureMode.Auto;

    [ObservableProperty]
    public partial PlayerFeatureMode InterpolationMode { get; set; } = PlayerFeatureMode.Auto;

    [ObservableProperty]
    public partial PlayerFeatureMode BufferingMode { get; set; } = PlayerFeatureMode.Auto;

    [ObservableProperty]
    public partial string SelectedBufferPreset { get; set; } = "Balanced";

    [ObservableProperty]
    public partial string BufferPresetDescription { get; set; } = "Cân bằng chất lượng/trễ | Cache: 3.0s | RAM: ~100MB";

    [ObservableProperty]
    public partial string SelectedTscaleAlgorithm { get; set; } = "oversample";

    public System.Collections.Generic.List<string> BufferPresetItems { get; } = new()
    {
        "Ultra Low Latency",
        "Low Latency",
        "Balanced",
        "High Quality",
        "Ultra Smooth (30s)",
        "Ultra Smooth (60s)",
        "Ultra Smooth (180s)"
    };

    public System.Collections.Generic.List<string> TscaleAlgorithms { get; } = new()
    {
        "oversample",
        "mitchell",
        "catmull_rom",
        "gaussian",
        "linear"
    };

    partial void OnSelectedBufferPresetChanged(string value)
    {
        BufferPresetDescription = value switch
        {
            "Ultra Low Latency" => "Trễ cực thấp cho UDP | Cache: 0.5s | RAM: ~10MB",
            "Low Latency" => "Trễ thấp, hơi mượt | Cache: 1.0s | RAM: ~30MB",
            "Balanced" => "Cân bằng chất lượng/trễ | Cache: 3.0s | RAM: ~100MB",
            "High Quality" => "Ưu tiên chất lượng | Cache: 10.0s | RAM: ~200MB",
            "Ultra Smooth (30s)" => "Mượt tối đa, trễ ~30s | Cache: 30.0s | RAM: ~300MB",
            "Ultra Smooth (60s)" => "Mượt tối đa, trễ ~1 phút | Cache: 60.0s | RAM: ~400MB",
            "Ultra Smooth (180s)" => "Mượt tối đa, trễ ~3 phút | Cache: 180.0s | RAM: ~512MB",
            _ => "Không xác định"
        };
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedCategory { get; set; } = "Tất cả";

    [ObservableProperty]
    public partial bool ShowStats { get; set; } = false;

    [ObservableProperty]
    public partial bool IsPlayerLoading { get; set; } = false;

    [ObservableProperty]
    public partial string NotificationMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNotificationOpen { get; set; } = false;

    [ObservableProperty]
    public partial InfoBarSeverity NotificationSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string CurrentShaderMode { get; set; } = "None";

    public bool IsShaderNone => CurrentShaderMode == "None";
    public bool IsShaderCas => CurrentShaderMode == "CAS";
    public bool IsShaderFsrcnnx => CurrentShaderMode == "FSRCNNX";

    partial void OnCurrentShaderModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsShaderNone));
        OnPropertyChanged(nameof(IsShaderCas));
        OnPropertyChanged(nameof(IsShaderFsrcnnx));
    }

    [ObservableProperty]
    public partial string ShaderButtonLabel { get; set; } = "AI: OFF";

    public void ShowError(string message)
    {
        NotificationMessage = message;
        NotificationSeverity = InfoBarSeverity.Error;
        IsNotificationOpen = true;
    }

    public void ShowSuccess(string message)
    {
        NotificationMessage = message;
        NotificationSeverity = InfoBarSeverity.Success;
        IsNotificationOpen = true;
    }

    public void ShowWarning(string message)
    {
        NotificationMessage = message;
        NotificationSeverity = InfoBarSeverity.Warning;
        IsNotificationOpen = true;
    }

    public System.Collections.Generic.List<Channel> AllChannelsList => _allChannels;
    public ObservableCollection<Channel> Channels { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public System.Collections.Generic.List<ContentProfile> ContentProfiles { get; } = System.Enum.GetValues<ContentProfile>().ToList();
    public System.Collections.Generic.List<FeatureModeItem> FeatureModeItems { get; } = new()
    {
        new() { Name = "Tự động", Mode = PlayerFeatureMode.Auto },
        new() { Name = "Ép buộc bật", Mode = PlayerFeatureMode.ForceOn },
        new() { Name = "Ép buộc tắt", Mode = PlayerFeatureMode.ForceOff }
    };

    public HomeViewModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        Categories.Add("Tất cả");
        Categories.Add("Yêu thích");
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilters();

    private void PopulateCategories()
    {
        Categories.Clear();
        Categories.Add("Tất cả");
        Categories.Add("Yêu thích");

        var groups = _allChannels
            .Select(c => c.GroupName)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        foreach (var g in groups)
        {
            Categories.Add(g);
        }
    }

    public void ApplyFilters()
    {
        System.Collections.Generic.IEnumerable<Channel> filtered = _allChannels;

        // 1. Filter by category
        if (SelectedCategory == "Yêu thích")
        {
            filtered = filtered.Where(c => c.IsFavorite);
        }
        else if (SelectedCategory != "Tất cả" && !string.IsNullOrEmpty(SelectedCategory))
        {
            filtered = filtered.Where(c => c.GroupName == SelectedCategory);
        }

        // 2. Filter by search text
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            filtered = filtered.Where(c => c.Name.Contains(query, System.StringComparison.OrdinalIgnoreCase) || 
                                           c.GroupName.Contains(query, System.StringComparison.OrdinalIgnoreCase));
        }

        // 3. Update ObservableCollection
        Channels.Clear();
        var list = filtered.OrderByDescending(c => c.IsFavorite).ThenBy(c => c.Name).ToList();
        foreach (var ch in list)
        {
            Channels.Add(ch);
        }
    }

    [RelayCommand]
    public async Task ToggleFavoriteAsync(Channel channel)
    {
        if (channel == null) return;
        channel.IsFavorite = !channel.IsFavorite;

        _dbContext.Channels.Update(channel);
        await _dbContext.SaveChangesAsync();

        ApplyFilters();
    }

    [RelayCommand]
    public void ToggleStats()
    {
        ShowStats = !ShowStats;
    }

    [RelayCommand]
    public void SetShader(string mode)
    {
        CurrentShaderMode = mode;
        if (mode == "None") ShaderButtonLabel = "AI: OFF";
        else if (mode == "CAS") ShaderButtonLabel = "AI: CAS (Sharp)";
        else if (mode == "FSRCNNX") ShaderButtonLabel = "AI: FSRCNNX (4K)";
        
        // Force refresh just in case ToggleButton unchecked itself
        OnPropertyChanged(nameof(IsShaderNone));
        OnPropertyChanged(nameof(IsShaderCas));
        OnPropertyChanged(nameof(IsShaderFsrcnnx));
    }

    [RelayCommand]
    public async Task LoadChannelsFromDbAsync()
    {
        IsLoading = true;
        
        await _dbContext.Database.EnsureCreatedAsync();

        // Create indexes on SQLite tables to speed up EPG query
        try
        {
            await _dbContext.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_EpgPrograms_StartTime_EndTime ON EpgPrograms (StartTime, EndTime);");
            await _dbContext.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_EpgPrograms_ChannelId ON EpgPrograms (ChannelId);");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error creating indexes: {ex.Message}");
        }

        _allChannels = await _dbContext.Channels.ToListAsync();
        
        PopulateCategories();
        ApplyFilters();
        
        IsLoading = false;
    }

    [RelayCommand]
    public async Task ImportPlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
        {
            ShowWarning("Vui lòng nhập đường dẫn danh sách kênh (M3U) hợp lệ.");
            return;
        }

        IsLoading = true;
        try
        {
            using TextReader reader = PlaylistUrl.StartsWith("http", System.StringComparison.OrdinalIgnoreCase) 
                ? new StringReader(await new System.Net.Http.HttpClient().GetStringAsync(PlaylistUrl))
                : new StreamReader(PlaylistUrl);

            var parsed = await M3uParser.ParseAsync(reader);

            if (parsed.Count > 0)
            {
                await _dbContext.Database.EnsureCreatedAsync();
                
                // Clear existing
                _dbContext.Channels.RemoveRange(_dbContext.Channels);
                await _dbContext.SaveChangesAsync();

                // Save to db
                await _dbContext.Channels.AddRangeAsync(parsed);
                await _dbContext.SaveChangesAsync();

                _allChannels = parsed;
                PopulateCategories();
                ApplyFilters();
                
                ShowSuccess($"Tải thành công {parsed.Count} kênh phát sóng!");
            }
            else
            {
                ShowError("Không tìm thấy kênh nào trong nguồn M3U đã nhập.");
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error importing playlist: {ex.Message}");
            ShowError($"Lỗi khi tải danh sách kênh: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Removed EPG loading code.
}

public class FeatureModeItem
{
    public string Name { get; set; } = string.Empty;
    public PlayerFeatureMode Mode { get; set; }
}

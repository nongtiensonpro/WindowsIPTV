using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using IptvApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace IptvApp.Pages;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private bool _isFullScreen;
    private int _loadingSeconds;

    public HomePage()
    {
        this.InitializeComponent();
        this.ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        this.DataContext = this.ViewModel;

        // Stats timer (updates quality properties)
        _statsTimer = new DispatcherTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick += StatsTimer_Tick;

        this.ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Auto-hide fullscreen popup timer
        _fullscreenTimer = new DispatcherTimer();
        _fullscreenTimer.Interval = TimeSpan.FromSeconds(5);
        _fullscreenTimer.Tick += FullscreenTimer_Tick;

        this.Loaded += HomePage_Loaded;
        this.Unloaded += HomePage_Unloaded;
        
        // Listen to keyboard shortcuts
        this.KeyDown += HomePage_KeyDown;

        // Đăng ký sự kiện SMTC
        App.Smtc.PlayPressed += Smtc_PlayPressed;
        App.Smtc.PausePressed += Smtc_PausePressed;
        App.Smtc.NextPressed += Smtc_NextPressed;
        App.Smtc.PreviousPressed += Smtc_PreviousPressed;
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _statsTimer.Stop();
        _fullscreenTimer.Stop();
        this.ViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        // Hủy đăng ký sự kiện SMTC
        App.Smtc.PlayPressed -= Smtc_PlayPressed;
        App.Smtc.PausePressed -= Smtc_PausePressed;
        App.Smtc.NextPressed -= Smtc_NextPressed;
        App.Smtc.PreviousPressed -= Smtc_PreviousPressed;
    }

    private void FullscreenTimer_Tick(object? sender, object e)
    {
        if (_isFullScreen)
        {
            FullscreenExitButton.Visibility = Visibility.Collapsed;
        }
        _fullscreenTimer.Stop();
    }

    private void MainGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            FullscreenExitButton.Visibility = Visibility.Visible;
            _fullscreenTimer.Stop();
            _fullscreenTimer.Start();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CurrentShaderMode))
        {
            Player?.SetShaderMode(ViewModel.CurrentShaderMode);
        }
        else if (e.PropertyName == nameof(ViewModel.SelectedProfile))
        {
            Player?.SetShaderModeForProfile(ViewModel.SelectedProfile);
        }
    }

    private void StatsTimer_Tick(object? sender, object e)
    {
        if (Player != null && ViewModel != null)
        {
            Player.UpdatePlaybackStats(ViewModel);

            if (ViewModel.IsPlayerLoading)
            {
                _loadingSeconds++;
                if (_loadingSeconds > 15) // 15 seconds timeout
                {
                    _loadingSeconds = 0;
                    ViewModel.IsPlayerLoading = false;
                    Player.Stop();
                    ViewModel.ShowError("Không thể kết nối hoặc tải luồng phát của kênh này. Vui lòng kiểm tra lại nguồn phát hoặc đường truyền mạng.");
                }
            }
            else
            {
                _loadingSeconds = 0;
            }
        }
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        // Focus the page so it receives key down events
        this.Focus(FocusState.Programmatic);
        await ViewModel.LoadChannelsFromDbCommand.ExecuteAsync(null);
    }

    private async void ChannelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is Core.Models.Channel selectedChannel)
        {
            _loadingSeconds = 0;
            ViewModel.IsPlayerLoading = true;
            ViewModel.IsNotificationOpen = false; // Hide previous errors
            Player.Play(selectedChannel.StreamUrl);
            _statsTimer.Start();
            ViewModel.PlayingChannelName = selectedChannel.Name;

            // Đồng bộ thông tin kênh lên SMTC
            App.Smtc.UpdateChannel(selectedChannel.Name, selectedChannel.LogoUrl);
            App.Smtc.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Playing);
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Pause();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        _statsTimer.Stop();
        ResetStats();

        // Đồng bộ trạng thái SMTC
        App.Smtc.SetPlaybackStatus(Windows.Media.MediaPlaybackStatus.Stopped);
    }

    private void ResetStats()
    {
        if (ViewModel != null)
        {
            ViewModel.IsPlayerLoading = false;
            ViewModel.StatsResolution = "-";
            ViewModel.StatsFps = "-";
            ViewModel.StatsDisplayFps = "-";
            ViewModel.StatsVideoCodec = "-";
            ViewModel.StatsHwdec = "-";
            ViewModel.StatsPixelFormat = "-";
            ViewModel.StatsAudioCodec = "-";
            ViewModel.StatsAudioChannels = "-";
            ViewModel.StatsAudioSampleRate = "-";
            ViewModel.StatsDroppedFrames = "0";
            ViewModel.StatsVoDelayed = "0";
            ViewModel.StatsLiveLatency = "-";
            ViewModel.StatsPacketLoss = "-";
            ViewModel.StatsCacheState = "-";
            ViewModel.StatsRealTimeBitrate = "-";
            ViewModel.StatsBitrateDetails = "-";
            ViewModel.StatsNetworkSpeed = "-";
            ViewModel.StatsConnectionStatus = "Đang dừng";
            ViewModel.StatsActiveGpu = "-";
        }
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleStats();
    }

    private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Core.Models.Channel channel)
        {
            await ViewModel.ToggleFavoriteCommand.ExecuteAsync(channel);
        }
    }

    private void PlayerContainer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.SetFullScreenMode(_isFullScreen);
        }

        if (_isFullScreen)
        {
            MainGrid.Padding = new Thickness(0);
            MainGrid.RowSpacing = 0;
            
            ContentGrid.ColumnSpacing = 0;
            ChannelsColumn.Width = new GridLength(0);
            ChannelsGrid.Visibility = Visibility.Collapsed;
            
            StatsColumn.Width = new GridLength(0);
            StatsSidebar.Visibility = Visibility.Collapsed;
            
            MetadataRow.Height = new GridLength(0);
            MetadataPanel.Visibility = Visibility.Collapsed;
            
            PlayerBorder.CornerRadius = new CornerRadius(0);
            PlayerBorder.BorderThickness = new Thickness(0);

            // Show exit fullscreen button and start auto-hide timer
            FullscreenExitButton.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(FullscreenExitButton, "Thoát toàn màn hình (F11 / Esc)");
            _fullscreenTimer.Start();
        }
        else
        {
            MainGrid.Padding = new Thickness(24, 16, 24, 16);
            MainGrid.RowSpacing = 16;
            
            ContentGrid.ColumnSpacing = 16;
            ChannelsColumn.Width = new GridLength(320);
            ChannelsGrid.Visibility = Visibility.Visible;
            
            StatsColumn.Width = GridLength.Auto;
            StatsSidebar.Visibility = ViewModel.ShowStats ? Visibility.Visible : Visibility.Collapsed;
            
            MetadataRow.Height = GridLength.Auto;
            MetadataPanel.Visibility = Visibility.Visible;
            
            PlayerBorder.CornerRadius = new CornerRadius(12);
            PlayerBorder.BorderThickness = new Thickness(1);
 
            // Hide button and stop timer
            FullscreenExitButton.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(FullscreenExitButton, null);
            _fullscreenTimer.Stop();
        }
    }

    private void HomePage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool isCtrlDown = ctrlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (isCtrlDown && e.Key == Windows.System.VirtualKey.I)
        {
            ViewModel.ToggleStats();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void FullscreenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleFullScreen();
        args.Handled = true;
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isFullScreen)
        {
            ToggleFullScreen();
            args.Handled = true;
        }
    }

    // Helper functions for XAML bindings
    public Visibility GetVisibility(bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility GetStatsVisibility(string? stats)
    {
        return string.IsNullOrWhiteSpace(stats) ? Visibility.Collapsed : Visibility.Visible;
    }

    public Visibility GetStatsShowVisibility(bool showStats, string? stats)
    {
        return (showStats && !string.IsNullOrWhiteSpace(stats)) ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string GetFavoriteGlyph(bool isFavorite)
    {
        return isFavorite ? "\uE735" : "\uE734"; // Solid star vs outline star
    }

    public static Microsoft.UI.Xaml.Media.ImageSource? GetImageSource(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
        }
        catch (Exception)
        {
            // Fail silently
        }

        return null;
    }
    private async void ChannelItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is Core.Models.Channel channel)
        {
            await Player.PrefetchChannel(channel.StreamUrl);
        }
    }

    private void Smtc_PlayPressed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Player.Pause();
            UpdateSmtcPlaybackStatus();
        });
    }

    private void Smtc_PausePressed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Player.Pause();
            UpdateSmtcPlaybackStatus();
        });
    }

    private void Smtc_NextPressed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            int index = ChannelsList.SelectedIndex;
            if (index < ChannelsList.Items.Count - 1)
            {
                ChannelsList.SelectedIndex = index + 1;
            }
            else if (ChannelsList.Items.Count > 0)
            {
                ChannelsList.SelectedIndex = 0;
            }
        });
    }

    private void Smtc_PreviousPressed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            int index = ChannelsList.SelectedIndex;
            if (index > 0)
            {
                ChannelsList.SelectedIndex = index - 1;
            }
            else if (ChannelsList.Items.Count > 0)
            {
                ChannelsList.SelectedIndex = ChannelsList.Items.Count - 1;
            }
        });
    }

    private void UpdateSmtcPlaybackStatus()
    {
        if (Player != null)
        {
            var status = Player.IsPaused 
                ? Windows.Media.MediaPlaybackStatus.Paused 
                : Windows.Media.MediaPlaybackStatus.Playing;
            App.Smtc.SetPlaybackStatus(status);
        }
    }
}

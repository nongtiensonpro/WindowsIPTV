using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IptvApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace IptvApp.Pages;

public sealed partial class SettingsPage : Page
{
    public HomeViewModel ViewModel { get; }

    public SettingsPage()
    {
        InitializeComponent();
        this.ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        this.DataContext = this.ViewModel;
    }

    public Visibility GetVisibility(bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BrowsePlaylist_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.PlaylistUrl = file.Path;
        }
    }

}

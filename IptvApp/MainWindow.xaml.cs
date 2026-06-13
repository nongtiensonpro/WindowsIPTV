using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using IptvApp.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace IptvApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Load HomePage by default
        NavFrame.Navigate(typeof(HomePage));
    }



    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "home":
                    NavFrame.Navigate(typeof(HomePage));
                    break;
                default:
                    break;
            }
        }
    }

    public void SetFullScreenMode(bool isFullScreen)
    {
        if (isFullScreen)
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            TitleBarRow.Height = new GridLength(0);
            NavView.IsPaneVisible = false;
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            NavView.Margin = new Thickness(0, -48, 0, 0); // Hide title bar gap just in case
            
            // Remove any internal padding from NavigationView that causes left/right gaps
            NavView.Padding = new Thickness(0);
        }
        else
        {
            AppTitleBar.Visibility = Visibility.Visible;
            TitleBarRow.Height = new GridLength(48);
            NavView.IsPaneVisible = true;
            NavView.PaneDisplayMode = NavigationViewPaneDisplayMode.Top;
            NavView.Margin = new Thickness(0);
        }
    }
}

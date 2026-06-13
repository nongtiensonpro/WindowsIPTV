using System;
using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinRT;

namespace IptvApp.Services;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ISystemMediaTransportControlsInteropVftbl
{
    public IntPtr QueryInterface;
    public IntPtr AddRef;
    public IntPtr Release;
    public IntPtr GetIids;
    public IntPtr GetRuntimeClassName;
    public IntPtr GetTrustLevel;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int> GetForWindow;
}

public static class SystemMediaTransportControlsInterop
{
    public static SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Media.SystemMediaTransportControls", new Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a"));
        
        Guid riid = new Guid("99FA3FF4-1742-42A6-902E-087D41F965EC"); // ISystemMediaTransportControls IID
        IntPtr smtcPtr = IntPtr.Zero;
        
        unsafe
        {
            IntPtr* vtablePtr = (IntPtr*)factory.ThisPtr;
            var vftbl = (ISystemMediaTransportControlsInteropVftbl*)(*vtablePtr);
            
            int hr = vftbl->GetForWindow(factory.ThisPtr, hwnd, &riid, &smtcPtr);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        
        return WinRT.MarshalInterface<SystemMediaTransportControls>.FromAbi(smtcPtr);
    }
}

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
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);

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
    {
        if (_smtc != null)
        {
            _smtc.PlaybackStatus = status;
        }
    }

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

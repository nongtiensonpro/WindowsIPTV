using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IptvApp.Native;

public delegate void MpvRenderUpdateCallback(IntPtr cbCtx);

[StructLayout(LayoutKind.Sequential)]
public struct MpvRenderParam
{
    public int Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvD3D11RenderTarget
{
    public IntPtr Resource; // ID3D11Texture2D*
    public IntPtr Rtv;      // ID3D11RenderTargetView*
}

public static partial class Mpv
{
    private const string MpvDll = "libmpv-2.dll";

    public const int MPV_RENDER_PARAM_API_TYPE = 1;
    public const int MPV_RENDER_PARAM_D3D11_DEVICE = 5;
    public const int MPV_RENDER_PARAM_D3D11_RENDER_TARGET = 6;

    [LibraryImport(MpvDll, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(MpvDll, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr handle);

    [LibraryImport(MpvDll, EntryPoint = "mpv_destroy")]
    public static partial void Destroy(IntPtr handle);

    [LibraryImport(MpvDll, EntryPoint = "mpv_set_option_string")]
    private static partial int mpv_set_option_string(IntPtr handle, byte[] name, byte[] value);

    [LibraryImport(MpvDll, EntryPoint = "mpv_command")]
    private static partial int mpv_command(IntPtr handle, IntPtr[] args);

    [LibraryImport(MpvDll, EntryPoint = "mpv_get_property_string")]
    private static partial IntPtr mpv_get_property_string(IntPtr handle, byte[] name);

    [LibraryImport(MpvDll, EntryPoint = "mpv_free")]
    public static partial void Free(IntPtr data);

    public static string? GetPropertyString(IntPtr handle, string name)
    {
        if (handle == IntPtr.Zero) return null;
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        IntPtr ptr = mpv_get_property_string(handle, nameBytes);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringAnsi(ptr);
        }
        finally
        {
            Free(ptr);
        }
    }

    [LibraryImport(MpvDll, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(out IntPtr ctx, IntPtr handle, MpvRenderParam[] @params);

    [LibraryImport(MpvDll, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderUpdateCallback callback, IntPtr cbCtx);

    [LibraryImport(MpvDll, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(IntPtr ctx, MpvRenderParam[] @params);

    [LibraryImport(MpvDll, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(IntPtr ctx);

    public static int SetOptionString(IntPtr handle, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var valueBytes = Encoding.UTF8.GetBytes(value + "\0");
        return mpv_set_option_string(handle, nameBytes, valueBytes);
    }

    public static int Command(IntPtr handle, string[] args)
    {
        IntPtr[] nativeArgs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                nativeArgs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }
            nativeArgs[args.Length] = IntPtr.Zero;

            return mpv_command(handle, nativeArgs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (nativeArgs[i] != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(nativeArgs[i]);
                }
            }
        }
    }
}

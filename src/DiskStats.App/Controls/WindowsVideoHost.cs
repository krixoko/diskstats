using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace DiskStats.App.Controls;

/// <summary>Embedded playback through Windows Media Foundation and the installed system codecs.</summary>
public sealed class WindowsVideoHost(string path) : NativeControlHost
{
    private IMFPMediaPlayer? _player;
    public event Action<string>? Failed;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows()) return base.CreateNativeControlCore(parent);
        nint hwnd = CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (hwnd == 0) throw new InvalidOperationException("Cannot create video surface.");
        int result = MFPCreateMediaPlayer(new Uri(path).AbsoluteUri, true, 0, 0, hwnd, out _player);
        if (result < 0) Failed?.Invoke(Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}");
        else Dispatcher.UIThread.Post(UpdateVideo, DispatcherPriority.Loaded);
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Dispatcher.UIThread.Post(UpdateVideo, DispatcherPriority.Loaded);
    }

    private void Check(int? result)
    {
        if (result is < 0) Failed?.Invoke(Marshal.GetExceptionForHR(result.Value)?.Message ?? $"0x{result:X8}");
    }
    // Media creation is asynchronous. A resize during startup can return
    // MF_E_INVALIDREQUEST before the renderer exists; playback will paint when ready.
    private void UpdateVideo() => _player?.UpdateVideo();
    public void Play() => Check(_player?.Play());
    public void Pause() => Check(_player?.Pause());
    public void Restart() { Check(_player?.Stop()); Check(_player?.Play()); }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_player is { } player)
        {
            player.Shutdown();
            if (OperatingSystem.IsWindows()) Marshal.FinalReleaseComObject(player);
            _player = null;
        }
        if (OperatingSystem.IsWindows()) DestroyWindow(control.Handle);
        else base.DestroyNativeControlCore(control);
    }

    [DllImport("mfplay.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFPCreateMediaPlayer(string url, [MarshalAs(UnmanagedType.Bool)] bool start,
        uint options, nint callback, nint hwnd, [MarshalAs(UnmanagedType.Interface)] out IMFPMediaPlayer? player);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);

    // Keep the complete native vtable order. Unused native structs are represented by pointers.
    [ComImport, Guid("A714590A-58AF-430A-85BF-44F5EC838D85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition(in Guid type, nint value);
        [PreserveSig] int GetPosition(in Guid type, nint value);
        [PreserveSig] int GetDuration(in Guid type, nint value);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates([MarshalAs(UnmanagedType.Bool)] bool forward, out float slow, out float fast);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] bool sync, nuint data, out nint item);
        [PreserveSig] int CreateMediaItemFromObject(nint source, [MarshalAs(UnmanagedType.Bool)] bool sync, nuint data, out nint item);
        [PreserveSig] int SetMediaItem(nint item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out nint item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute);
        [PreserveSig] int GetNativeVideoSize(nint video, nint aspect);
        [PreserveSig] int GetIdealVideoSize(nint min, nint max);
        [PreserveSig] int SetVideoSourceRect(nint rect);
        [PreserveSig] int GetVideoSourceRect(nint rect);
        [PreserveSig] int SetAspectRatioMode(uint mode);
        [PreserveSig] int GetAspectRatioMode(out uint mode);
        [PreserveSig] int GetVideoWindow(out nint hwnd);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(uint color);
        [PreserveSig] int GetBorderColor(out uint color);
        [PreserveSig] int InsertEffect(nint effect, [MarshalAs(UnmanagedType.Bool)] bool optional);
        [PreserveSig] int RemoveEffect(nint effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }
}

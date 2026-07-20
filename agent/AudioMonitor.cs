// Sistemden ses çıkıp çıkmadığını okur (video/müzik oynuyor mu).
// Amaç: kullanıcı video izlerken klavye/fareye dokunmaz → girdi-bazlı "boşta" algısı onu kaçırır.
// Ses çıkıyorsa kullanıcı içerik tüketiyor demektir → AKTİF say (film/dizi/müzik süresi doğru düşsün).
// WASAPI IAudioMeterInformation ile varsayılan çıkış cihazının anlık ses seviyesini (peak) okur.

using System.Runtime.InteropServices;

namespace Argus.Agent;

internal static class AudioMonitor
{
    private static IAudioMeterInformation? _meter;

    // Varsayılan hoparlör/kulaklıktan ses çıkıyor mu? (sessizlik/çok küçük gürültü elenir)
    public static bool IsPlaying()
    {
        try
        {
            _meter ??= CreateMeter();
            if (_meter is null) return false;
            _meter.GetPeakValue(out var peak);
            return peak > 0.005f;   // gerçek ses ~0.1-1.0; sessizlik ~0
        }
        catch { _meter = null; return false; }   // cihaz değişmiş olabilir → sonraki çağrıda yeniden kur
    }

    private static IAudioMeterInformation? CreateMeter()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var device) != 0 || device is null)
            return null;
        var iid = typeof(IAudioMeterInformation).GUID;
        if (device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out var o) != 0 || o is null)
            return null;
        return (IAudioMeterInformation)o;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);          // vtable sırası için
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }
}

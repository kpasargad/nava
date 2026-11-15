using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Nava.Core;

namespace Nava.Services;

public interface IAudioCapture : IDisposable
{
    event Action<byte[]>? DataAvailable;
    event Action<double>? LevelChanged;
    event Action? Failed;
    double DurationMs { get; }
    void Start(string path,int deviceId);
    void Stop();
}

public sealed class AudioRecorder : IAudioCapture
{
    private const int BufferSize = 3200; // 100 ms, 16 kHz mono signed PCM16.
    private readonly object _gate = new();
    private readonly WaveCallback _callback;
    private readonly List<(IntPtr Header, IntPtr Data)> _buffers = new();
    private IntPtr _handle;
    private FileStream? _file;
    private volatile bool _active;
    private int _callbacks, _failureReported;
    private readonly ManualResetEventSlim _drained = new(true);
    private long _bytes;
    public event Action<byte[]>? DataAvailable;
    public event Action<double>? LevelChanged;
    public event Action? Failed;
    public double DurationMs => _bytes / 32.0;
    public AudioRecorder() => _callback = OnWave;
    public static IReadOnlyList<MicrophoneDevice> Devices()
    {
        var result = new List<MicrophoneDevice> { new(-1,"میکروفن پیش‌فرض ویندوز") };
        for (uint i = 0; i < waveInGetNumDevs(); i++) if (waveInGetDevCaps((UIntPtr)i,out var caps,(uint)Marshal.SizeOf<WaveCaps>()) == 0) result.Add(new((int)i,caps.Name));
        return result;
    }
    public void Start(string path,int deviceId)
    {
        var format = new WaveFormat { FormatTag=1,Channels=1,SamplesPerSec=16000,AvgBytesPerSec=32000,BlockAlign=2,BitsPerSample=16 };
        var error = waveInOpen(out _handle,unchecked((uint)deviceId),ref format,_callback,IntPtr.Zero,0x30000);
        if (error != 0) throw new InvalidOperationException(error == 4 ? "میکروفن در اختیار برنامهٔ دیگری است. دستگاه دیگری انتخاب کنید." : "میکروفن باز نشد. اتصال و اجازهٔ دسترسی میکروفن را در تنظیمات ویندوز بررسی کنید.");
        try
        {
            _file = new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read); _file.Write(new byte[44]); _bytes=0; _active=true;
            for (int i=0;i<5;i++)
            {
                var data=Marshal.AllocHGlobal(BufferSize); var header=Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data=data,BufferLength=BufferSize },header,false); _buffers.Add((header,data));
                if (waveInPrepareHeader(_handle,header,(uint)Marshal.SizeOf<WaveHeader>()) != 0 || waveInAddBuffer(_handle,header,(uint)Marshal.SizeOf<WaveHeader>()) != 0) throw new InvalidOperationException("آماده‌سازی میکروفن انجام نشد.");
            }
            if (waveInStart(_handle) != 0) throw new InvalidOperationException("شروع ضبط ممکن نشد.");
        }
        catch { Stop(); throw; }
    }
    private void OnWave(IntPtr handle,uint message,IntPtr instance,IntPtr parameter,IntPtr unused)
    {
        if (message != 0x3C0) return;
        if (Interlocked.Increment(ref _callbacks)==1) _drained.Reset();
        try
        {
            var header=Marshal.PtrToStructure<WaveHeader>(parameter);
            if (header.BytesRecorded > 0)
            {
                var data=new byte[header.BytesRecorded]; Marshal.Copy(header.Data,data,0,data.Length);
                lock (_gate) { _file?.Write(data); _bytes+=data.Length; }
                double sum=0; for(int i=0;i+1<data.Length;i+=2) { double value=(short)(data[i]|(data[i+1]<<8))/32768.0; sum+=value*value; }
                LevelChanged?.Invoke(Math.Min(1,Math.Sqrt(sum/Math.Max(1,data.Length/2))*5));
                DataAvailable?.Invoke(data);
            }
            lock (_gate) { if (_active) waveInAddBuffer(handle,parameter,(uint)Marshal.SizeOf<WaveHeader>()); }
        }
        catch { if (Interlocked.Exchange(ref _failureReported,1)==0) { try { Failed?.Invoke(); } catch { } } }
        finally { if (Interlocked.Decrement(ref _callbacks)==0) _drained.Set(); }
    }
    public void Stop()
    {
        lock(_gate) _active=false;
        if (_handle!=IntPtr.Zero)
        {
            waveInStop(_handle); waveInReset(_handle);
            _drained.Wait(TimeSpan.FromSeconds(2));
            foreach(var (header,data) in _buffers)
            {
                uint result=33;
                for(int attempt=0;attempt<200 && result==33;attempt++) { result=waveInUnprepareHeader(_handle,header,(uint)Marshal.SizeOf<WaveHeader>()); if(result==33) Thread.Sleep(5); }
                // In a broken driver retain native memory rather than freeing an in-flight buffer.
                if(result==0 && _callbacks==0) { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); }
            }
            _buffers.Clear(); waveInClose(_handle); _handle=IntPtr.Zero;
        }
        lock(_gate)
        {
            if(_file != null)
            {
                _file.Position=0; using(var writer=new BinaryWriter(_file,System.Text.Encoding.ASCII,true)) { writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write((int)_bytes+36); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write((int)_bytes); }
                _file.Flush(true); _file.Dispose(); _file=null;
            }
        }
    }
    public void Dispose()=>Stop();
    public static void RepairHeader(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
        if(stream.Length<44) return;
        using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true);
        stream.Position=0; writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write((int)stream.Length-8); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write((int)stream.Length-44);
    }
    [StructLayout(LayoutKind.Sequential,Pack=2)] private struct WaveFormat { public ushort FormatTag,Channels; public uint SamplesPerSec,AvgBytesPerSec; public ushort BlockAlign,BitsPerSample,ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader { public IntPtr Data; public uint BufferLength,BytesRecorded; public UIntPtr User; public uint Flags,Loops; public IntPtr Next,Reserved; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WaveCaps { public ushort ManufacturerId,ProductId; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; public uint Formats; public ushort Channels,Reserved; }
    private delegate void WaveCallback(IntPtr handle,uint message,IntPtr instance,IntPtr parameter,IntPtr unused);
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll",CharSet=CharSet.Unicode,EntryPoint="waveInGetDevCapsW")] private static extern uint waveInGetDevCaps(UIntPtr device,out WaveCaps caps,uint size);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle,uint device,ref WaveFormat format,WaveCallback callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
}

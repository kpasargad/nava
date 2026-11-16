using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly HashSet<IntPtr> _pendingBuffers = new();
    private IntPtr _handle;
    private FileStream? _file;
    private volatile bool _active;
    private volatile bool _stopRequested;
    private int _callbacks, _failureReported, _callbackFailed;
    private readonly ConcurrentQueue<IntPtr> _readyBuffers = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly ManualResetEventSlim _stopped = new(true);
    private Thread? _worker;
    private long _bytes;
    public event Action<byte[]>? DataAvailable;
    public event Action<double>? LevelChanged;
    public event Action? Failed;
    public double DurationMs => Interlocked.Read(ref _bytes) / 32.0;
    public AudioRecorder() => _callback = OnWave;
    public static IReadOnlyList<MicrophoneDevice> Devices()
    {
        var result = new List<MicrophoneDevice> { new(-1,"میکروفن پیش‌فرض ویندوز") };
        for (uint i = 0; i < waveInGetNumDevs(); i++) if (waveInGetDevCaps((UIntPtr)i,out var caps,(uint)Marshal.SizeOf<WaveCaps>()) == 0) result.Add(new((int)i,caps.Name));
        return result;
    }
    public void Start(string path,int deviceId)
    {
        if (!_stopped.IsSet || _handle != IntPtr.Zero) throw new InvalidOperationException("ضبط قبلی هنوز بسته نشده است.");
        _failureReported=0; _callbackFailed=0; _stopRequested=false; _readyBuffers.Clear(); _pendingBuffers.Clear();
        var format = new WaveFormat { FormatTag=1,Channels=1,SamplesPerSec=16000,AvgBytesPerSec=32000,BlockAlign=2,BitsPerSample=16 };
        var error = waveInOpen(out _handle,unchecked((uint)deviceId),ref format,_callback,IntPtr.Zero,0x30000);
        if (error != 0) throw new InvalidOperationException(error == 4 ? "میکروفن در اختیار برنامهٔ دیگری است. دستگاه دیگری انتخاب کنید." : "میکروفن باز نشد. اتصال و اجازهٔ دسترسی میکروفن را در تنظیمات ویندوز بررسی کنید.");
        try
        {
            _file = new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read); WriteHeader(_file,0); _bytes=0; _active=true;
            for (int i=0;i<5;i++)
            {
                var data=Marshal.AllocHGlobal(BufferSize); var header=Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(new WaveHeader { Data=data,BufferLength=BufferSize },header,false); _buffers.Add((header,data));
                if (waveInPrepareHeader(_handle,header,(uint)Marshal.SizeOf<WaveHeader>()) != 0 || waveInAddBuffer(_handle,header,(uint)Marshal.SizeOf<WaveHeader>()) != 0) throw new InvalidOperationException("آماده‌سازی میکروفن انجام نشد.");
                _pendingBuffers.Add(header);
            }
            _stopped.Reset();
            _worker=new Thread(Pump) { IsBackground=true,Name="Nava audio capture" }; _worker.Start();
            if (waveInStart(_handle) != 0) throw new InvalidOperationException("شروع ضبط ممکن نشد.");
        }
        catch { Stop(); throw; }
    }
    private void OnWave(IntPtr handle,uint message,IntPtr instance,IntPtr parameter,IntPtr unused)
    {
        if (message != 0x3C0) return;
        Interlocked.Increment(ref _callbacks);
        try
        {
            // waveInProc must not call wave functions or subscriber/file code.
            // The pump owns processing and requeueing after this callback returns.
            _readyBuffers.Enqueue(parameter); _ready.Set();
        }
        catch { Interlocked.Exchange(ref _callbackFailed,1); _stopRequested=true; }
        finally { Interlocked.Decrement(ref _callbacks); }
    }
    private void ProcessBuffer(IntPtr parameter)
    {
        if (!_pendingBuffers.Remove(parameter)) return;
        var header=Marshal.PtrToStructure<WaveHeader>(parameter);
        if (header.BytesRecorded > BufferSize || (header.BytesRecorded&1)!=0) throw new IOException("Invalid microphone buffer.");
        if (header.BytesRecorded > 0)
        {
            var data=new byte[header.BytesRecorded]; Marshal.Copy(header.Data,data,0,data.Length);
            lock (_gate)
            {
                if (_file==null) return;
                _file.Write(data); Interlocked.Add(ref _bytes,data.Length);
            }
            double sum=0; for(int i=0;i+1<data.Length;i+=2) { double value=(short)(data[i]|(data[i+1]<<8))/32768.0; sum+=value*value; }
            try { LevelChanged?.Invoke(Math.Min(1,Math.Sqrt(sum/Math.Max(1,data.Length/2))*5)); } catch { }
            try { DataAvailable?.Invoke(data); } catch { }
        }
        if (_active)
        {
            if (waveInAddBuffer(_handle,parameter,(uint)Marshal.SizeOf<WaveHeader>())!=0) throw new IOException("Microphone buffer could not be queued.");
            _pendingBuffers.Add(parameter);
        }
    }
    private void Pump()
    {
        try
        {
            while (!_stopRequested)
            {
                _ready.WaitOne(100);
                while (_readyBuffers.TryDequeue(out var header)) ProcessBuffer(header);
            }
            if (Volatile.Read(ref _callbackFailed)!=0) ReportFailure();
        }
        catch { ReportFailure(); }
        finally
        {
            _active=false;
            try
            {
                // This worker, its delegate, and its buffers remain rooted if a
                // broken driver delays reset/close. Never free in-flight memory.
                if (_handle!=IntPtr.Zero) { waveInStop(_handle); waveInReset(_handle); }
                var drain=Stopwatch.StartNew();
                while (true)
                {
                    while (_readyBuffers.TryDequeue(out var header))
                        try { ProcessBuffer(header); } catch { ReportFailure(); }
                    // A DONE flag can precede delivery of WIM_DATA. Require an
                    // observed callback for every queued header before freeing.
                    if (_pendingBuffers.Count==0 && Volatile.Read(ref _callbacks)==0 && _readyBuffers.IsEmpty) break;
                    if (drain.Elapsed>TimeSpan.FromSeconds(1)) ReportFailure();
                    _ready.WaitOne(20);
                }
                foreach(var (header,_) in _buffers)
                {
                    while ((Marshal.PtrToStructure<WaveHeader>(header).Flags&2)!=0)
                    {
                        if (waveInUnprepareHeader(_handle,header,(uint)Marshal.SizeOf<WaveHeader>())==0) break;
                        ReportFailure(); _ready.WaitOne(20);
                    }
                }
                if (_handle!=IntPtr.Zero)
                {
                    while (true)
                    {
                        uint close=waveInClose(_handle);
                        if (close is 0 or 5) break;
                        ReportFailure(); _ready.WaitOne(20);
                    }
                    _handle=IntPtr.Zero;
                }
                while (Volatile.Read(ref _callbacks)!=0) _ready.WaitOne(1);
                foreach(var (header,data) in _buffers) { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); }
                _buffers.Clear();
            }
            catch { ReportFailure(); }
            finally
            {
                lock(_gate)
                {
                    if (_file!=null)
                    {
                        try { WriteHeader(_file,Interlocked.Read(ref _bytes)); _file.Flush(true); }
                        catch { ReportFailure(); }
                        finally { _file.Dispose(); _file=null; }
                    }
                }
                _stopped.Set();
            }
            GC.KeepAlive(_callback);
        }
    }
    private void ReportFailure()
    {
        if (Interlocked.Exchange(ref _failureReported,1)==0) { try { Failed?.Invoke(); } catch { } }
    }
    public void Stop()
    {
        _active=false; _stopRequested=true; _ready.Set();
        if (_worker==null && _handle!=IntPtr.Zero)
        {
            _stopped.Reset(); _worker=new Thread(Pump) { IsBackground=true,Name="Nava audio cleanup" }; _worker.Start();
        }
        if (_worker!=Thread.CurrentThread && !_stopped.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("میکروفن هنوز بسته نشده است. صدا محفوظ است؛ کمی بعد دوباره تلاش کنید.");
    }
    public void Dispose() { try { Stop(); } catch { /* Cleanup worker keeps the driver callback alive. */ } }
    private static void WriteHeader(Stream stream,long bytes)
    {
        stream.Position=0;
        using var writer=new BinaryWriter(stream,System.Text.Encoding.ASCII,true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(checked((int)bytes+36)); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(checked((int)bytes));
    }
    public static void RepairHeader(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
        if(stream.Length<44) return;
        var header=new byte[44]; stream.ReadExactly(header);
        bool placeholder=true; foreach(byte value in header) if(value!=0) { placeholder=false; break; }
        bool canonical=System.Text.Encoding.ASCII.GetString(header,0,4)=="RIFF" && System.Text.Encoding.ASCII.GetString(header,8,8)=="WAVEfmt "
            && BitConverter.ToInt32(header,16)==16 && BitConverter.ToInt16(header,20)==1 && BitConverter.ToInt16(header,22)==1
            && BitConverter.ToInt32(header,24)==16000 && BitConverter.ToInt32(header,28)==32000 && BitConverter.ToInt16(header,32)==2
            && BitConverter.ToInt16(header,34)==16 && System.Text.Encoding.ASCII.GetString(header,36,4)=="data";
        if (canonical && BitConverter.ToInt32(header,4)==stream.Length-8) return;
        // Only our empty recording header is recoverable by this format-specific
        // repair. Preserve valid WAVs with extra chunks or different formats.
        if (!placeholder && (!canonical || BitConverter.ToInt32(header,40)!=0)) return;
        long bytes=(stream.Length-44)&~1L;
        if (stream.Length!=bytes+44) stream.SetLength(bytes+44);
        WriteHeader(stream,bytes);
        stream.Flush(true);
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

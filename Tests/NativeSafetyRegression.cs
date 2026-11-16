using System;
using System.IO;
using System.Linq;
using System.Text;
using Nava.Services;

namespace Nava.Tests;

public static class NativeSafetyRegression
{
    public static void PasteRequiresOriginalFocusAndReleasedModifiers()
    {
        IntPtr target=new(123),other=new(456);
        if(!WindowsIntegration.IsSafePasteState(target,target,true,false)) throw new InvalidOperationException("Valid unchanged paste target rejected");
        if(WindowsIntegration.IsSafePasteState(target,other,true,false)) throw new InvalidOperationException("Paste would steal focus from a different window");
        if(WindowsIntegration.IsSafePasteState(target,target,true,true)) throw new InvalidOperationException("Paste would inject a shortcut while a modifier is held");
        if(WindowsIntegration.IsSafePasteState(target,target,false,false)) throw new InvalidOperationException("Paste accepted a destroyed window");
        if(WindowsIntegration.IsSafePasteState(IntPtr.Zero,IntPtr.Zero,true,false)) throw new InvalidOperationException("Paste accepted a zero window handle");
    }
    public static void WaveRecoveryPreservesUnrelatedAudio(string root)
    {
        Directory.CreateDirectory(root);
        string extended=Path.Combine(root,"fmt18-stereo.wav");
        WriteWave(extended,18,2,22050,16);
        RequireUnchanged(extended,"Extended/stereo WAV was overwritten by 16 kHz mono recovery");
        string differentRate=Path.Combine(root,"canonical48k.wav");
        WriteWave(differentRate,16,1,48000,16);
        RequireUnchanged(differentRate,"A valid differently sampled WAV was rewritten");
        string random=Path.Combine(root,"unrelated.wav");
        File.WriteAllBytes(random,Enumerable.Range(0,140).Select(i=>(byte)(i+1)).ToArray());
        RequireUnchanged(random,"An unrelated file was overwritten with a fake WAV header");
    }

    private static void RequireUnchanged(string path,string error)
    {
        byte[] before=File.ReadAllBytes(path); AudioRecorder.RepairHeader(path);
        if(!before.SequenceEqual(File.ReadAllBytes(path))) throw new InvalidOperationException(error);
    }
    private static void WriteWave(string path,int formatLength,ushort channels,int sampleRate,ushort bits)
    {
        using var stream=File.Create(path); using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
        byte[] samples=Enumerable.Range(0,3200).Select(i=>(byte)(i%253)).ToArray();
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(0); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(formatLength); writer.Write((ushort)1); writer.Write(channels); writer.Write(sampleRate); writer.Write(sampleRate*channels*bits/8); writer.Write((ushort)(channels*bits/8)); writer.Write(bits);
        if(formatLength>16) writer.Write(new byte[formatLength-16]);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length); writer.Write(samples);
        stream.Position=4; writer.Write((int)stream.Length-8);
    }
}

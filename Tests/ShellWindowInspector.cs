using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nava.Tests;

/// <summary>Read-only HWND flag audit, restricted to one validated Nava process. No window text or input.</summary>
public static class ShellWindowInspector
{
    public static int Run(int processId)
    {
        if(processId<=0) { Console.WriteLine("FAIL a valid Nava PID is required"); return 2; }
        try
        {
            using var process=Process.GetProcessById(processId);
            if(!string.Equals(process.ProcessName,"Nava",StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL target process is not Nava"); return 2; }
            int total=0,violations=0;
            var visible=new List<object>();
            EnumWindowsCallback callback=(window,_)=> {
                GetWindowThreadProcessId(window,out uint ownerProcessId);
                if(ownerProcessId!=(uint)processId) return true;
                total++;
                if(!IsWindowVisible(window)) return true;
                uint style=ExtendedStyle(window);
                bool tool=(style&0x80)!=0,app=(style&0x40000)!=0,layered=(style&0x80000)!=0,owned=GetWindow(window,4)!=IntPtr.Zero;
                // Ownership alone is insufficient proof: a hidden WPF owner
                // can leave its last active popup eligible for shell switching.
                if(app || !tool) violations++;
                visible.Add(new { toolWindow=tool,appWindow=app,layered,owned });
                return true;
            };
            if(!EnumWindows(callback,IntPtr.Zero)) { Console.WriteLine("FAIL native window enumeration returned error "+Marshal.GetLastWin32Error()); return 1; }
            GC.KeepAlive(callback);
            Console.WriteLine(JsonSerializer.Serialize(new { processId,scope="Validated Nava PID only; HWND flags; no input, titles, text, or screenshots",totalWindows=total,visibleWindowCount=visible.Count,violations,visibleWindows=visible },new JsonSerializerOptions { WriteIndented=true }));
            if(visible.Count==0) { Console.WriteLine("FAIL no visible Nava windows were available to inspect"); return 1; }
            Console.WriteLine(violations==0?"PASS installed Nava visible windows enforce native Alt+Tab exclusion":"FAIL a visible Nava window lacks explicit native Alt+Tab exclusion");
            return violations==0?0:1;
        }
        catch(ArgumentException) { Console.WriteLine("FAIL target Nava process no longer exists"); return 2; }
        catch(InvalidOperationException) { Console.WriteLine("FAIL target Nava process could not be inspected"); return 2; }
    }
    private static uint ExtendedStyle(IntPtr window)=>IntPtr.Size==8?unchecked((uint)GetWindowLongPtr(window,-20).ToInt64()):unchecked((uint)GetWindowLong(window,-20));
    private delegate bool EnumWindowsCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr data);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
}

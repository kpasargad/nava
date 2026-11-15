using System;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Pure state transitions; no native registration, input injection, or keyboard access.</summary>
public static class HotkeyHoldContract
{
    public static void Run()
    {
        var hold=new HotkeyHoldState(); hold.Configure(0x4000,0x90);
        var down=hold.Process(0x90,true,0);
        Require(down.Pressed && down.Suppress && !down.Released && hold.IsHeld,"NumLock down did not start hold");
        for(int i=0;i<10;i++) { var repeat=hold.Process(0x90,true,0); Require(repeat.Suppress && !repeat.Pressed && !repeat.Released,"Auto-repeat retriggered capture"); }
        var unrelated=hold.Process(0x41,true,0); Require(!unrelated.Suppress && !unrelated.Pressed && hold.IsHeld,"Unrelated typing was captured");
        var up=hold.Process(0x90,false,0); Require(up.Released && up.Suppress && !up.Pressed && !hold.IsHeld,"NumLock up did not finish hold");
        Require(!hold.Process(0x90,false,0).Released,"Duplicate up retriggered finish");

        hold.Configure(0x4003,0x20);
        Require(!hold.Process(0x20,true,2).Pressed,"Incomplete modifier chord started capture");
        Require(hold.Process(0x20,true,3).Pressed,"Complete chord did not start capture");
        var modifierUp=hold.Process(0xA4,false,2); Require(modifierUp.Released && !modifierUp.Suppress && !hold.IsHeld,"Required modifier release did not finish capture");
        Require(!hold.Process(0x20,true,3).Pressed,"Held primary restarted after modifier re-press");
        Require(hold.Process(0x20,false,3).Suppress,"Owned primary keyup leaked after modifier release");
        Require(hold.Process(0x20,true,3).Pressed,"Fresh press did not rearm after complete release");
        Require(hold.Poll(false,3).Released && !hold.IsHeld,"Watchdog missed lost primary-up");
        Require(hold.Process(0x20,true,3).Pressed,"Watchdog did not rearm next press");
        Require(hold.Poll(true,1).Released && !hold.IsHeld,"Watchdog missed lost required modifier-up");
        hold.Process(0x20,false,1);
        Require(!hold.Process(0x20,true,3,true).Pressed && !hold.IsHeld,"Injected down started capture");
        Require(!hold.Process(0x20,false,3,true).Released,"Injected up finished a physical hold");
        hold.Process(0x20,true,3);
        Require(!hold.Process(0x20,false,3,true).Released && hold.IsHeld,"Injected keyup ended physical hold");
        Require(hold.Configure(0x4000,0x90).Released && !hold.IsHeld,"Changing chord left capture held");
        Require(!hold.Process(0x90,true,4).Pressed,"Unexpected extra modifier activated shortcut");
    }
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nava.Core;
using Nava.Services;

namespace Nava.Tests;

/// <summary>Release-facing contracts: stable character choices and private-data-free application payloads.</summary>
public static class ReleaseChecks
{
    public static void CharacterCatalogContract()
    {
        var expected=new[] { "ghost","cat","rabbit","fox","bear","panda","frog","penguin","owl","octopus" };
        Require(CharacterCatalog.All.Count==10,"Character picker must contain the original ghost and nine alternatives");
        Require(CharacterCatalog.All.Select(x=>x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()==10,"Character IDs are not unique");
        Require(CharacterCatalog.All.Select(x=>x.Id).OrderBy(x=>x).SequenceEqual(expected.OrderBy(x=>x)),"Stable character IDs changed");
        Require(CharacterCatalog.DefaultId=="ghost","Existing users lost the original default character");
        foreach(var definition in CharacterCatalog.All)
        {
            Require(!string.IsNullOrWhiteSpace(definition.Name) && !string.IsNullOrWhiteSpace(definition.Description),"Character is missing its accessible name or description");
            Require(CharacterCatalog.Normalize(definition.Id)==definition.Id,"Known character normalized incorrectly");
            Require(CharacterCatalog.FindOrDefault(definition.Id).Id==definition.Id,"Known character resolves to another character");
            Require(CharacterCatalog.Normalize("  "+definition.Id.ToUpperInvariant()+"  ")==definition.Id,"Stored character casing/whitespace was not normalized");
        }
        foreach(string? invalid in new string?[] { null,""," ","unknown","../cat","ghost.json" })
        {
            Require(CharacterCatalog.Normalize(invalid)=="ghost","Invalid stored character does not recover to ghost");
            Require(CharacterCatalog.FindOrDefault(invalid).Id=="ghost","Invalid character renderer lookup does not recover to ghost");
        }
    }

    public static void CharacterSettingsRoundTrip(string root)
    {
        var store=new LocalStore(root);
        var settings=new AppSettings { CharacterId="octopus",Hotkey="Ctrl+Alt+Space",KeepAudio=true };
        store.SaveSettings(settings);
        var loaded=store.LoadSettings();
        Require(loaded.CharacterId=="octopus","Chosen non-default character was not persisted");
        Require(loaded.Hotkey==settings.Hotkey && loaded.KeepAudio,"Saving a character changed unrelated settings");
        File.WriteAllText(Path.Combine(root,"settings.json"),"{\"Hotkey\":\"NumLock\"}");
        Require(store.LoadSettings().CharacterId=="ghost","Pre-character settings do not migrate to ghost");
        foreach(string? invalid in new string?[] { "retired-character",null })
        {
            File.WriteAllText(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(new { CharacterId=invalid }));
            Require(store.LoadSettings().CharacterId=="ghost","Unknown/null persisted character is not repaired on load");
        }
        File.WriteAllText(Path.Combine(root,"settings.json"),"{invalid");
        Require(store.LoadSettings().CharacterId=="ghost","Malformed settings lost the default character recovery");
    }

    public static void FreshInstallDefaults()
    {
        var settings=new AppSettings();
        Require(string.IsNullOrWhiteSpace(settings.BaseUrl),"Public build contains a private default service address");
        Require(settings.ApiKey.Length==0 && settings.ProxyUrl.Length==0,"Public build contains preconfigured credentials");
        Require(!settings.StartWithWindows && !settings.AutoPaste,"Fresh installation enabled a behavior requiring user choice");
        Require(settings.CharacterId=="ghost","Fresh installation changed the original character default");
    }

    public static void InstalledVersusPortableDataLocation()
    {
        // Only query the resolver; never instantiate a default store or create
        // real LocalAppData user data. The marker is inside this test's bin dir.
        string marker=Path.Combine(AppContext.BaseDirectory,"installed.flag");
        Require(!File.Exists(marker),"Test output already contains an installation marker");
        Require(Path.GetFullPath(LocalStore.DefaultRoot)==Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"Data")),"Portable copy no longer keeps its original Data location");
        try
        {
            File.WriteAllText(marker,"nava-test-installed-marker");
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Nava");
            Require(Path.GetFullPath(LocalStore.DefaultRoot)==Path.GetFullPath(expected),"Installed copy does not resolve to the persistent user-data folder");
        }
        finally { File.Delete(marker); }
        Require(Path.GetFullPath(LocalStore.DefaultRoot)==Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"Data")),"Removing installer marker did not restore portable data routing");
    }

    public static void VerifyCleanPayload(string payloadDirectory)
    {
        var root=Path.GetFullPath(payloadDirectory);
        Require(Directory.Exists(root),"Release payload directory does not exist");
        var privateDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".local",".git","Data","History","Audio","controller-ptt-smoke","wpf-smoke-data" };
        var privateFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "credentials.bin","settings.json","live-test-pending-id.txt","live-test-result.json","live-rt-result.json" };
        var rawAudioExtensions=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav",".m4a",".mp3",".mp4",".ogg",".flac",".opus",".webm" };
        foreach(var path in Directory.EnumerateFileSystemEntries(root,"*",SearchOption.AllDirectories))
        {
            if(Directory.Exists(path)) Require(!privateDirectories.Contains(Path.GetFileName(path)),"Private data directory present in release payload");
            else
            {
                Require(!privateFiles.Contains(Path.GetFileName(path)),"Private configuration/verification file present in release payload");
                Require(!rawAudioExtensions.Contains(Path.GetExtension(path)),"Raw audio/video present in release payload");
            }
        }
        Require(File.Exists(Path.Combine(root,"Nava.exe")),"Application executable missing from release payload");
    }

    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}

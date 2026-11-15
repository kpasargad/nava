using System;
using System.Collections.Generic;

namespace Nava.Core;

public sealed record CharacterDefinition(string Id, string Name, string Description, string Accent);

public static class CharacterCatalog
{
    public const string DefaultId="ghost";
    public static IReadOnlyList<CharacterDefinition> All { get; }=Array.AsReadOnly(new[] {
        new CharacterDefinition("ghost","روح کوچولو","همان دوست آرام و نعنایی نوا","#BAEDCD"),
        new CharacterDefinition("cat","گربهٔ هلو","گوش‌های تیز و سبیل‌های کنجکاو","#F4BD99"),
        new CharacterDefinition("rabbit","خرگوش ابری","دو گوش بلند و یک لبخند کوچک","#D6C7ED"),
        new CharacterDefinition("fox","روباه کنجکاو","صورت نارنجی با گونه‌های روشن","#EBA46C"),
        new CharacterDefinition("bear","خرس عسلی","گوش‌های گرد و رنگ گرم عسل","#E8BC74"),
        new CharacterDefinition("panda","پاندای آرام","چشم‌های تیره و صورت شیری","#EEEAE0"),
        new CharacterDefinition("frog","قورباغهٔ برکه","چشم‌های درشت و لبخند سبز","#ABD39B"),
        new CharacterDefinition("penguin","پنگوئن کوچولو","باله‌های کوچک و شکم سفید","#A9CFDC"),
        new CharacterDefinition("owl","جغد بیدار","چشم‌های گرد و بال‌های یاسی","#BAAED8"),
        new CharacterDefinition("octopus","هشت‌پای آبی","هشت بازوی کوچک و صورتی خندان","#9DCAD9")
    });
    public static CharacterDefinition FindOrDefault(string? id)
    {
        var candidate=id?.Trim();
        foreach(var character in All) if(string.Equals(character.Id,candidate,StringComparison.OrdinalIgnoreCase)) return character;
        return All[0];
    }
    public static string Normalize(string? id)=>FindOrDefault(id).Id;
}

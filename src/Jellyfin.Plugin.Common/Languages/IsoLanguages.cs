using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Common.Languages;

/// <summary>
/// ISO 639 language codes without depending on the server's culture data: with invariant globalization or minimal ICU
/// data (Alpine, some containers), .NET knows no languages, and matching subtitle tags through it silently finds
/// nothing (FAM-01). The table (two-letter code, three-letter terminology code, bibliographic code where it differs,
/// English name) was generated from .NET's own culture data.
/// </summary>
internal static class IsoLanguages
{
    private const string Table = @"
af|afr||Afrikaans
ak|aka||Akan
am|amh||Amharic
ar|ara||Arabic
as|asm||Assamese
az|aze||Azerbaijani
be|bel||Belarusian
bg|bul||Bulgarian
bm|bam||Bambara
bn|ben||Bangla
bo|bod|tib|Tibetan
br|bre||Breton
bs|bos||Bosnian
ca|cat||Catalan
ce|che||Chechen
cs|ces|cze|Czech
cv|chv||Chuvash
cy|cym|wel|Welsh
da|dan||Danish
de|deu|ger|German
dz|dzo||Dzongkha
ee|ewe||Ewe
el|ell|gre|Greek
en|eng||English
eo|epo||Esperanto
es|spa||Spanish
et|est||Estonian
eu|eus|baq|Basque
fa|fas|per|Persian
ff|ful||Fula
fi|fin||Finnish
fo|fao||Faroese
fr|fra|fre|French
fy|fry||Western Frisian
ga|gle||Irish
gd|gla||Scottish Gaelic
gl|glg||Galician
gu|guj||Gujarati
gv|glv||Manx
ha|hau||Hausa
he|heb||Hebrew
hi|hin||Hindi
hr|hrv||Croatian
hu|hun||Hungarian
hy|hye|arm|Armenian
ia|ina||Interlingua
id|ind||Indonesian
ie|ile||Interlingue
ig|ibo||Igbo
ii|iii||Sichuan Yi
is|isl|ice|Icelandic
it|ita||Italian
ja|jpn||Japanese
jv|jav||Javanese
ka|kat|geo|Georgian
ki|kik||Kikuyu
kk|kaz||Kazakh
kl|kal||Kalaallisut
km|khm||Khmer
kn|kan||Kannada
ko|kor||Korean
ks|kas||Kashmiri
ku|kur||Kurdish
kw|cor||Cornish
ky|kir||Kyrgyz
lb|ltz||Luxembourgish
lg|lug||Ganda
ln|lin||Lingala
lo|lao||Lao
lt|lit||Lithuanian
lu|lub||Luba-Katanga
lv|lav||Latvian
mg|mlg||Malagasy
mi|mri|mao|Māori
mk|mkd|mac|Macedonian
ml|mal||Malayalam
mn|mon||Mongolian
mr|mar||Marathi
ms|msa|may|Malay
mt|mlt||Maltese
my|mya|bur|Burmese
nb|nob||Norwegian Bokmål
nd|nde||North Ndebele
ne|nep||Nepali
nl|nld|dut|Dutch
nn|nno||Norwegian Nynorsk
no|nor||Norwegian
oc|oci||Occitan
om|orm||Oromo
or|ori||Odia
os|oss||Ossetic
pa|pan||Punjabi
pl|pol||Polish
ps|pus||Pashto
pt|por||Portuguese
qu|que||Quechua
rm|roh||Romansh
rn|run||Rundi
ro|ron|rum|Romanian
ru|rus||Russian
rw|kin||Kinyarwanda
sa|san||Sanskrit
sc|srd||Sardinian
sd|snd||Sindhi
se|sme||Northern Sami
sg|sag||Sango
si|sin||Sinhala
sk|slk|slo|Slovak
sl|slv||Slovenian
sn|sna||Shona
so|som||Somali
sq|sqi|alb|Albanian
sr|srp||Serbian
su|sun||Sundanese
sv|swe||Swedish
sw|swa||Swahili
ta|tam||Tamil
te|tel||Telugu
tg|tgk||Tajik
th|tha||Thai
ti|tir||Tigrinya
tk|tuk||Turkmen
to|ton||Tongan
tr|tur||Turkish
tt|tat||Tatar
ug|uig||Uyghur
uk|ukr||Ukrainian
ur|urd||Urdu
uz|uzb||Uzbek
vi|vie||Vietnamese
wo|wol||Wolof
xh|xho||Xhosa
yi|yid||Yiddish
yo|yor||Yoruba
za|zha||Zhuang
zh|zho|chi|Chinese
zu|zul||Zulu";

    private static readonly Dictionary<string, string> ToTwo = Build();

    private static readonly Dictionary<string, string> ToThree = ToTwo
        .Where(p => p.Key.Length == 2)
        .ToDictionary(p => p.Key, p => ThreeOf(p.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The ISO 639-1 (two-letter) code for a language code or English name: <c>en</c>, <c>eng</c>, <c>English</c>,
    /// <c>en-GB</c>, <c>ger</c> and <c>deu</c> are all recognised.
    /// </summary>
    /// <param name="value">The code or name.</param>
    /// <returns>The two-letter code, or <c>null</c> if it isn't a known language.</returns>
    public static string? TwoLetter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        var dash = v.IndexOfAny(['-', '_']);
        if (dash is 2 or 3)
        {
            v = v[..dash];
        }

        return ToTwo.TryGetValue(v, out var two) ? two : null;
    }

    /// <summary>
    /// The ISO 639-2 terminology (three-letter) code for a language code or English name.
    /// </summary>
    /// <param name="value">The code or name.</param>
    /// <returns>The three-letter code, or <c>null</c> if it isn't a known language.</returns>
    public static string? ThreeLetter(string? value)
        => TwoLetter(value) is { } two && ToThree.TryGetValue(two, out var three) ? three : null;

    /// <summary>
    /// The English name of a language.
    /// </summary>
    /// <param name="value">A code or name.</param>
    /// <returns>The name, or <c>null</c> if it isn't a known language.</returns>
    public static string? EnglishName(string? value)
        => TwoLetter(value) is { } two ? Rows().FirstOrDefault(r => r[0] == two)?[3] : null;

    private static IEnumerable<string[]> Rows()
        => Table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|'));

    private static string ThreeOf(string two) => Rows().First(r => r[0] == two)[1];

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Rows())
        {
            foreach (var key in new[] { r[0], r[1], r[2], r[3] }.Where(k => k.Length > 0))
            {
                map.TryAdd(key, r[0]);
            }
        }

        return map;
    }
}

using System.Text;

namespace Replica;

/// <summary>
/// Canonical compare key for player-name collision detection. Pipeline:
/// NFKC → trim → lowercase → Cyrillic→Latin transliteration → leetspeak
/// digit strip in letter-bearing runs. Display names preserve original
/// case; only the lookup key is canonical. 'Нагибатор' / 'Nagibator' /
/// 'NaGiBaToR' collapse to the same key; 'Tr1ck5t3r' / 'trickster'
/// collapse; 'killer_2010' keeps its year suffix (digits in pure-numeric
/// runs are NOT folded).
/// </summary>
public static class NameKey
{
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var folded = s.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        folded = TransliterateCyrillic(folded);
        folded = StripLeetspeak(folded);
        return folded;
    }

    private static string TransliterateCyrillic(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'а' => "a",  'б' => "b",  'в' => "v",  'г' => "g",  'д' => "d",
                'е' => "e",  'ё' => "e",  'ж' => "zh", 'з' => "z",  'и' => "i",
                'й' => "y",  'к' => "k",  'л' => "l",  'м' => "m",  'н' => "n",
                'о' => "o",  'п' => "p",  'р' => "r",  'с' => "s",  'т' => "t",
                'у' => "u",  'ф' => "f",  'х' => "h",  'ц' => "ts", 'ч' => "ch",
                'ш' => "sh", 'щ' => "shch", 'ъ' => "", 'ы' => "y",  'ь' => "",
                'э' => "e",  'ю' => "yu", 'я' => "ya",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    // Substitute leet digits with their letter equivalents only inside
    // alphanumeric runs that contain at least one ASCII letter. A run is
    // a maximal contiguous span of [a-z0-9]. This keeps numeric-suffix
    // patterns ('killer_2010', 'lol_228') intact while collapsing
    // letter-mixed leet ('Tr1ck5t3r' → 'trickster', 'n00b' → 'noob').
    private static string StripLeetspeak(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!alnum) { sb.Append(c); i++; continue; }
            int j = i;
            bool hasLetter = false;
            while (j < s.Length)
            {
                char cj = s[j];
                bool ja = (cj >= 'a' && cj <= 'z') || (cj >= '0' && cj <= '9');
                if (!ja) break;
                if (cj >= 'a' && cj <= 'z') hasLetter = true;
                j++;
            }
            for (int k = i; k < j; k++)
            {
                char ch = s[k];
                if (hasLetter && ch >= '0' && ch <= '9')
                {
                    ch = ch switch
                    {
                        '0' => 'o', '1' => 'i', '3' => 'e',
                        '4' => 'a', '5' => 's', '7' => 't',
                        _ => ch,
                    };
                }
                sb.Append(ch);
            }
            i = j;
        }
        return sb.ToString();
    }
}

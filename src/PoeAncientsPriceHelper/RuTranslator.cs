using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

/// <summary>
/// Translates Russian OCR text to normalized English names using the JSON lookup database.
/// Sits between OcrScanner and BuildPriceRows — converts RU item names to the same
/// normalized EN strings that poe.ninja keys use, so the rest of the pipeline is unchanged.
/// </summary>
internal sealed class RuTranslator
{
    // Normalized RU name (quantity suffix stripped) → normalized EN name (quantity suffix stripped)
    private readonly Dictionary<string, string> _dict;
    private readonly Action<string>? _log;

    public bool IsAvailable => _dict.Count > 0;

    public RuTranslator(string jsonPath, Action<string>? log = null)
    {
        _log = log;
        _dict = new Dictionary<string, string>(StringComparer.Ordinal);
        Load(jsonPath);
    }

    private void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _log?.Invoke($"[RuTranslator] JSON не найден: {path}");
                return;
            }
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var entries = JsonConvert.DeserializeObject<List<RuEntry>>(json);
            if (entries is null)
            {
                _log?.Invoke("[RuTranslator] JSON десериализован в null");
                return;
            }

            foreach (var e in entries)
            {
                // Strip quantity suffix from BOTH sides before storing as lookup key/value.
                // RU JSON uses "xN" suffix ("Сфера хаоса x1"), screen shows "(N)" suffix ("Сфера хаоса (1)").
                // Normalizing both eliminates the format mismatch without editing the JSON file.
                var ruKey = Normalize(StripQuantitySuffix(e.Ru));
                var enVal = Normalize(StripQuantitySuffix(e.En));
                if (!string.IsNullOrEmpty(ruKey) && !string.IsNullOrEmpty(enVal))
                    _dict[ruKey] = enVal;
            }
            _log?.Invoke($"[RuTranslator] загружено {_dict.Count} записей из {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[RuTranslator] ошибка загрузки: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to translate a raw OCR line (Russian) to a normalized English name.
    /// Also extracts the item multiplier from the Russian "(N)" suffix.
    /// Returns false when no match found — caller should show "проверь json".
    /// </summary>
    public bool TryTranslate(string rawOcrText, out string enNormalizedName, out int multiplier)
    {
        enNormalizedName = "";
        multiplier = 1;

        // 1. Extract multiplier from trailing "(N)" — RU screen format
        multiplier = ExtractRuMultiplier(rawOcrText);

        // 2. Strip the "(N)" or "xN" quantity suffix before lookup
        var stripped = StripQuantitySuffix(rawOcrText);

        // 3. Normalize for lookup
        var ruKey = Normalize(stripped);
        if (string.IsNullOrEmpty(ruKey)) return false;

        // 4. Exact match
        if (_dict.TryGetValue(ruKey, out var exactVal))
        {
            enNormalizedName = exactVal;
            _log?.Invoke($"[RuTranslator] ТОЧНО '{rawOcrText.Trim()}' → '{enNormalizedName}' ×{multiplier}");
            return true;
        }

        // 5. Fuzzy match — rescues single-character OCR errors in Cyrillic
        //    (e.g. "Вожественная" instead of "Божественная").
        //    Threshold 0.82: allows ~2 wrong chars on a 12-char key.
        string? bestKey = null;
        double bestScore = 0.82;
        foreach (var key in _dict.Keys)
        {
            if (Math.Abs(key.Length - ruKey.Length) > 5) continue;
            int dist = Levenshtein(ruKey, key);
            double score = 1.0 - (double)dist / Math.Max(ruKey.Length, key.Length);
            if (score > bestScore) { bestScore = score; bestKey = key; }
        }

        if (bestKey is not null)
        {
            enNormalizedName = _dict[bestKey];
            _log?.Invoke($"[RuTranslator] НЕЧЁТКО '{rawOcrText.Trim()}' → ключ='{bestKey}' → '{enNormalizedName}' ×{multiplier} совпадение={bestScore:0.00}");
            return true;
        }

        _log?.Invoke($"[RuTranslator] НЕТ СОВПАДЕНИЯ для '{rawOcrText.Trim()}' (ключ='{ruKey}')");
        return false;
    }

    // RU screen format: quantity is a trailing "(N)" in parentheses AFTER the item name.
    // Examples:
    //   "Неогранённый камень духа (уровень 19) (1)" → 1
    //   "Божественная сфера (10)"                   → 10
    //   "5 шт. случайной валюты"                    → 1 (no suffix — the 5 is part of the name)
    internal static int ExtractRuMultiplier(string rawText)
    {
        // Match a standalone "(N)" at the very end of the string.
        // The lookbehind (?<!\d) ensures we don't match level numbers like "(уровень 19)":
        // that "(19)" is NOT at the end (item name follows), but for safety we also anchor to end.
        var m = Regex.Match(rawText.TrimEnd(), @"(?<!\w)\((\d{1,3})\)\s*$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1)
            return Math.Min(n, 999);
        return 1;
    }

    // Strip trailing quantity suffix from a raw item string:
    //   "(N)" at end  — RU screen format
    //   " xN" at end  — JSON "ru" field format (defensive, also handles EN JSON "en" values)
    internal static string StripQuantitySuffix(string text)
    {
        var s = text.TrimEnd();
        // Strip trailing " (N)" — RU screen: "Сфера хаоса (1)" → "Сфера хаоса"
        s = Regex.Replace(s, @"\s*\(\d{1,3}\)\s*$", "").TrimEnd();
        // Strip trailing " xN" — JSON format: "Сфера хаоса x1" → "Сфера хаоса"
        s = Regex.Replace(s, @"\s+x\d{1,3}\s*$", "").TrimEnd();
        return s;
    }

    // Same normalization as OcrScanner.NormalizeName / PriceRepository.NormalizeName:
    // lowercase, remove punctuation (keep letters/digits/underscore/spaces), collapse spaces.
    // \w in .NET matches Unicode word chars → Cyrillic letters are preserved correctly.
    internal static string Normalize(string text)
    {
        var s = text.ToLowerInvariant();
        s = Regex.Replace(s, @"[^\w\s]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private sealed class RuEntry
    {
        [JsonProperty("en")] public string En { get; set; } = "";
        [JsonProperty("ru")] public string Ru { get; set; } = "";
    }
}

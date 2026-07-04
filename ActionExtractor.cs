using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIFileButler;

/// <summary>
/// Finds obligations with deadlines inside a document's text: "pay by 14 July",
/// "respond within 28 days", "appointment on 3 Aug at 09:20". Uses the local LLM
/// when available (best recall, also names the sender), with a regex fallback so
/// rules-only installs still catch the common phrasings. Relative dates resolve
/// against today. Purely on-device.
/// </summary>
public static class ActionExtractor
{
    public sealed record Found(string Quote, string Kind, string DateIso, string Sender);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static List<Found> Extract(string fileName, string text, string ollamaUrl, string model, bool tryLlm)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        if (tryLlm)
        {
            var llm = ExtractLlm(fileName, text, ollamaUrl, model);
            if (llm is not null) return llm; // trust the model when it answered
        }
        return ExtractRegex(text);
    }

    // ---- LLM path ---------------------------------------------------------

    private const string Prompt =
        "You read a letter/document and list every ACTION THE RECIPIENT MUST TAKE that has " +
        "a deadline or scheduled date: payments due, replies required, appointments, forms or " +
        "items to return, renewals. Ignore marketing offers, discounts and newsletters. " +
        "For each: the exact sentence it came from (quote), a kind (pay|respond|appointment|return|renew|other) " +
        "and the date as yyyy-MM-dd. Resolve relative deadlines like 'within 28 days' against TODAY = {0}. " +
        "Also give the sender/organisation the letter is from ('' if unclear).\n" +
        "Respond ONLY with JSON: {{\"sender\":\"\",\"actions\":[{{\"quote\":\"\",\"kind\":\"\",\"date\":\"yyyy-MM-dd\"}}]}}. " +
        "If there are none: {{\"sender\":\"\",\"actions\":[]}}.\n\nFILE: {1}\nTEXT:\n{2}\n";

    private static List<Found>? ExtractLlm(string fileName, string text, string url, string model)
    {
        try
        {
            var prompt = string.Format(Prompt, DateTime.Today.ToString("yyyy-MM-dd"), fileName,
                text.Length > 4000 ? text[..4000] : text);
            var body = JsonSerializer.Serialize(new { model, prompt, stream = false, format = "json", options = new { temperature = 0.1 } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = Http.PostAsync($"{url.TrimEnd('/')}/api/generate", content).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var outer = JsonDocument.Parse(raw);
            using var doc = JsonDocument.Parse(outer.RootElement.GetProperty("response").GetString() ?? "{}");
            var root = doc.RootElement;
            var sender = root.TryGetProperty("sender", out var sEl) ? sEl.GetString() ?? "" : "";
            var list = new List<Found>();
            if (root.TryGetProperty("actions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    var quote = a.TryGetProperty("quote", out var q) ? q.GetString() ?? "" : "";
                    var kind = a.TryGetProperty("kind", out var k) ? k.GetString() ?? "other" : "other";
                    var date = a.TryGetProperty("date", out var dEl) ? dEl.GetString() ?? "" : "";
                    if (quote.Length == 0 || !Reminders.TryDate(date, out var d)) continue;
                    if (!Plausible(d)) continue;
                    if (!KnownKinds.Contains(kind)) kind = "other";
                    list.Add(new Found(quote, kind, d.ToString("yyyy-MM-dd"), sender));
                }
            return list;
        }
        catch { return null; }
    }

    private static readonly string[] KnownKinds = { "pay", "respond", "appointment", "return", "renew", "other" };

    // ---- regex fallback ---------------------------------------------------

    // date sub-patterns (EN + RO)
    private const string Mon =
        "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|" +
        "sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|" +
        "ian(?:uarie)?|feb(?:ruarie)?|mart(?:ie)?|apr(?:ilie)?|mai|iun(?:ie)?|iul(?:ie)?|" +
        "aug(?:ust)?|sept(?:embrie)?|oct(?:ombrie)?|noi(?:embrie)?|dec(?:embrie)?";
    private const string D =
        @"(?:\d{4}-\d{2}-\d{2}" +                                   // ISO
        @"|\d{1,2}[./]\d{1,2}[./]\d{2,4}" +                          // 14/07/2026
        @"|\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?(?:" + Mon + @")(?:\s+\d{4})?" + // 14 July [2026]
        @"|(?:" + Mon + @")\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?)";          // July 14[, 2026]

    private static readonly (string Kind, Regex Rx)[] Patterns =
    {
        ("pay",         new Regex(@"(?:pay(?:ment)?|amount|balance|bill|invoice|fee|fine|plat[ăa]|factur[ăa]|amend[ăa]).{0,60}?(?:due(?:\s+(?:by|on))?|by|before|until|no later than|p[âa]n[ăa] (?:la|pe))\s+(" + D + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("respond",     new Regex(@"(?:respond|reply|contact us|get in touch|appeal|r[ăa]spunde|contesta[țt]i).{0,50}?(?:by|before|within|no later than|p[âa]n[ăa] la)\s+(" + D + @"|\d{1,3}\s+(?:days|zile))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("appointment", new Regex(@"(?:appointment|interview|hearing|meeting|visit|biometrics|programare|audien[țt][ăa]|interviu).{0,50}?(?:is\s+)?(?:on|at|scheduled for|pe(?:\s+data\s+de)?)\s+(" + D + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("return",      new Regex(@"(?:return|send back|submit|hand in|returna[țt]i|depune[țt]i|trimite[țt]i).{0,60}?(?:by|before|until|no later than|p[âa]n[ăa] (?:la|pe))\s+(" + D + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("renew",       new Regex(@"(?:renew(?:al)?|re[îi]nnoi(?:re|[țt]i)?).{0,50}?(?:by|before|until|due|p[âa]n[ăa] la)\s+(" + D + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("other",       new Regex(@"(?:deadline|due date|must|required|trebuie|termen(?:\s+limit[ăa])?).{0,60}?(?:is|by|before|until|no later than|p[âa]n[ăa] (?:la|pe))\s+(" + D + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("respond",     new Regex(@"(?:within|in|[îi]n(?:\s+termen\s+de)?)\s+(\d{1,3})\s+(?:days|zile)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static List<Found> ExtractRegex(string text)
    {
        var list = new List<Found>();
        var seen = new HashSet<string>();
        foreach (var (kind, rx) in Patterns)
        {
            foreach (Match m in rx.Matches(text))
            {
                DateTime? d;
                var cap = m.Groups[1].Value;
                if (Regex.IsMatch(cap, @"^\d{1,3}$")) d = DateTime.Today.AddDays(int.Parse(cap)); // "within N days"
                else d = ParseDate(cap);
                if (d is null || !Plausible(d.Value)) continue;
                var quote = SentenceAround(text, m.Index, m.Length);
                if (!seen.Add(quote)) continue;
                list.Add(new Found(quote, kind, d.Value.ToString("yyyy-MM-dd"), GuessSender(text)));
                if (list.Count >= 8) return list; // cap noise
            }
        }
        return list;
    }

    private static readonly string[] MonKeys =
    { "ian","jan","feb","mar","apr","mai","may","iun","jun","iul","jul","aug","sep","oct","noi","nov","dec" };
    private static readonly int[] MonVals =
    { 1, 1, 2, 3, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 11, 12 };

    private static DateTime? ParseDate(string s)
    {
        s = s.Trim().ToLowerInvariant();
        var iso = Regex.Match(s, @"^(\d{4})-(\d{2})-(\d{2})$");
        if (iso.Success) return Build(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value));
        var num = Regex.Match(s, @"^(\d{1,2})[./](\d{1,2})[./](\d{2,4})$");
        if (num.Success)
        {
            int a = int.Parse(num.Groups[1].Value), b = int.Parse(num.Groups[2].Value), y = int.Parse(num.Groups[3].Value);
            if (y < 100) y += 2000;
            var (day, mon) = a > 12 && b <= 12 ? (a, b) : (b > 12 && a <= 12 ? (b, a) : (a, b)); // day-first bias
            return Build(y, mon, day);
        }
        // name-month forms: "14(th) (of) july(, 2026)" or "july 14(, 2026)"
        var dm = Regex.Match(s, @"^(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?([a-zăâî]+)(?:\s+(\d{4}))?$");
        var md = Regex.Match(s, @"^([a-zăâî]+)\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?$");
        int dd, mm; string? yr;
        if (dm.Success) { dd = int.Parse(dm.Groups[1].Value); mm = MonthOf(dm.Groups[2].Value); yr = dm.Groups[3].Value; }
        else if (md.Success) { mm = MonthOf(md.Groups[1].Value); dd = int.Parse(md.Groups[2].Value); yr = md.Groups[3].Value; }
        else return null;
        if (mm == 0) return null;
        int year = string.IsNullOrEmpty(yr) ? DateTime.Today.Year : int.Parse(yr);
        var built = Build(year, mm, dd);
        // no year given and the date already passed → they meant next year
        if (built is DateTime b2 && string.IsNullOrEmpty(yr) && b2.Date < DateTime.Today) built = Build(year + 1, mm, dd);
        return built;
    }

    private static int MonthOf(string w)
    {
        for (int i = 0; i < MonKeys.Length; i++)
            if (w.StartsWith(MonKeys[i], StringComparison.Ordinal)) return MonVals[i];
        return 0;
    }

    private static DateTime? Build(int y, int m, int d)
    {
        if (m is < 1 or > 12 || d is < 1 or > 31) return null;
        try { return new DateTime(y, m, d); } catch { return null; }
    }

    private static bool Plausible(DateTime d) =>
        d.Date >= DateTime.Today && d.Date <= DateTime.Today.AddYears(3);

    private static string SentenceAround(string text, int idx, int len)
    {
        int start = idx, end = idx + len;
        while (start > 0 && !".!?\n\r".Contains(text[start - 1]) && idx - start < 120) start--;
        while (end < text.Length && !".!?\n\r".Contains(text[end]) && end - idx - len < 80) end++;
        return text[start..end].Trim();
    }

    // best-effort issuer for dismissal memory: first known-org keyword near the top
    private static readonly Regex OrgRx = new(
        @"\b(HMRC|HM Revenue|Home Office|UKVI|NHS|DVLA|DWP|TV Licensing|Council|Aviva|Admiral|Direct Line|EDF|E\.ON|British Gas|Octopus|Barclays|HSBC|Lloyds|NatWest|Santander|Monzo|Revolut|School|Academy|University|ANAF|Prim[ăa]ria)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string GuessSender(string text)
    {
        var head = text.Length > 1200 ? text[..1200] : text;
        var m = OrgRx.Match(head);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}

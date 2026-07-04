using System.Text.Json;

namespace AIFileButler;

/// <summary>
/// The Action Inbox: obligations found inside filed documents ("pay by 14 July",
/// "respond within 28 days", "appointment on 3 Aug"). Each is a suggestion the user
/// explicitly accepts (becomes a reminder) or dismisses — nothing is added silently.
/// Dismissals can remember the sender so marketing mail stops suggesting. Stored
/// locally in actions.json.
/// </summary>
public static class ActionInbox
{
    public sealed class Item
    {
        public string Id { get; set; } = "";        // stable key: file+quote hash
        public string File { get; set; } = "";      // the filed document
        public string Quote { get; set; } = "";     // the sentence the deadline came from
        public string Kind { get; set; } = "";      // pay | respond | appointment | return | renew | other
        public string Date { get; set; } = "";      // yyyy-MM-dd
        public string Sender { get; set; } = "";    // best-effort issuer (for dismissal memory)
        public string Status { get; set; } = "pending"; // pending | accepted | dismissed
    }

    private sealed class Store
    {
        public List<Item> Items { get; set; } = new();
        public List<string> MutedSenders { get; set; } = new();
    }

    private static readonly string Path_ = Paths.File("actions.json");
    private static readonly object _gate = new();
    private static Store? _cache;

    private static Store Load()
    {
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            try { _cache = File.Exists(Path_) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(Path_)) ?? new() : new(); }
            catch { _cache = new(); }
            return _cache;
        }
    }

    private static void Save()
    {
        lock (_gate)
        {
            try
            {
                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
                if (File.Exists(Path_)) File.Replace(tmp, Path_, Path_ + ".bak");
                else File.Move(tmp, Path_);
            }
            catch { }
        }
    }

    // string.GetHashCode is randomized per process in .NET, so use a stable FNV-1a
    // hash — the dedupe key must survive restarts.
    private static string KeyOf(string file, string quote)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in file + "|" + quote) { h ^= c; h *= 16777619; }
            return h.ToString("X8");
        }
    }

    /// <summary>Suggest a found obligation. Deduped; muted senders are ignored;
    /// past dates are ignored. Returns true if something new was added.</summary>
    public static bool Suggest(string file, string quote, string kind, string dateIso, string sender)
    {
        if (!Reminders.TryDate(dateIso, out var d) || d.Date < DateTime.Today) return false;
        lock (_gate)
        {
            var s = Load();
            if (sender.Length > 0 && s.MutedSenders.Contains(sender, StringComparer.OrdinalIgnoreCase)) return false;
            var id = KeyOf(file, quote);
            if (s.Items.Any(x => x.Id == id)) return false;
            s.Items.Add(new Item
            {
                Id = id, File = file, Quote = Clean(quote), Kind = kind,
                Date = d.ToString("yyyy-MM-dd"), Sender = sender.Trim(),
            });
            Save();
            return true;
        }
    }

    public static List<Item> Pending() =>
        Load().Items.Where(i => i.Status == "pending" &&
                                Reminders.TryDate(i.Date, out var d) && d.Date >= DateTime.Today)
              .OrderBy(i => i.Date).ToList();

    /// <summary>Accept: create the reminder (task bucket — it's an obligation, not a
    /// document expiry) and mark the suggestion accepted.</summary>
    public static void Accept(Item i)
    {
        Reminders.AddTask(TitleFor(i), i.Date, repeat: "");
        lock (_gate)
        {
            var it = Load().Items.FirstOrDefault(x => x.Id == i.Id);
            if (it is not null) { it.Status = "accepted"; Save(); }
        }
    }

    public static void Dismiss(Item i, bool muteSender)
    {
        lock (_gate)
        {
            var s = Load();
            var it = s.Items.FirstOrDefault(x => x.Id == i.Id);
            if (it is not null) it.Status = "dismissed";
            if (muteSender && i.Sender.Length > 0 &&
                !s.MutedSenders.Contains(i.Sender, StringComparer.OrdinalIgnoreCase))
                s.MutedSenders.Add(i.Sender);
            Save();
        }
    }

    private static string TitleFor(Item i)
    {
        var verb = i.Kind switch
        {
            "pay" => L.S("act_v_pay"),
            "respond" => L.S("act_v_respond"),
            "appointment" => L.S("act_v_appointment"),
            "return" => L.S("act_v_return"),
            "renew" => L.S("act_v_renew"),
            _ => L.S("act_v_other"),
        };
        var src = i.Sender.Length > 0 ? i.Sender : System.IO.Path.GetFileNameWithoutExtension(i.File);
        return $"{verb} — {src}";
    }

    private static string Clean(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}

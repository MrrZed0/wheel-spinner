// ============================================================================
//  STREAMER.BOT — WHEEL SYSTEM
//  Paste this whole file into ONE Execute C# Code sub-action.
//  Action name must match the "Sync action name" in the customizer (default: Wheel)
//
//  TRIGGERS to attach to this one action (Twitch > Chat Message > Command):
//    !addwheel      !wheel         !spinwheel
//    !removewheel   !wheellist     !clearwheel
//  (every command routes here; the code decides what to do)
//
//  Entries live in:  <Streamer.bot folder>\data\wheel_entries.tsv
//  One entry per user is enforced by user ID, so a renamed user keeps their slot.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// NOTE ON THE CLASS LINE BELOW
// Streamer.bot adds ": CPHInlineBase" for you, so the line must stay as it is.
// Only if an OLD build says "CPH does not exist in the current context"
// should you change it to:  public class CPHInline : CPHInlineBase
public class CPHInline
{
    // ===================== SETTINGS — EDIT THESE =====================

    // Written by the Wheel Customizer. Edit here or there, whichever suits.

    // Where chat replies go: "twitch", "youtube", or "both"
    const string PLATFORM = "twitch";

    // Send as the bot account (false = send as the broadcaster account)
    const bool SEND_AS_BOT = true;

    // Post wheel messages as a Twitch /announce instead of normal chat.
    // The sending account must be a moderator, or it falls back to chat.
    const bool USE_ANNOUNCE = true;
    const string ANNOUNCE_COLOR = "orange";

    // Reply in chat at all? false = the overlay does all the talking.
    const bool CHAT_REPLIES = true;

    // How many things one viewer may have on the wheel at once. 0 = unlimited.
    const int ENTRIES_PER_USER = 1;

    const int MAX_ITEM_LENGTH = 60;   // characters allowed in an entry
    const int MIN_ITEM_LENGTH = 2;
    const int MAX_ENTRIES = 0;   // hard cap on wheel size (0 = unlimited)
    const bool BLOCK_DUPLICATE_ITEMS = true;
    const bool MODS_ONLY_SPIN = true;
    const bool MODS_ONLY_CLEAR = true;
    const int LIST_MAX_SHOWN = 10;

    // How long (ms) after !spinwheel before the winner is named in chat.
    // Set this to the overlay spin length so chat and the wheel agree.
    const int ANNOUNCE_DELAY_MS = 6000;

    // Take the winner off the wheel when it is picked.
    const bool REMOVE_WINNER = true;

    // Database file name (kept in Streamer.bot\data)
    const string DB_FILE = "wheel_entries.tsv";

    // ===================== END SETTINGS =====================

    static readonly object DbLock = new object();
    static readonly Random Rng = new Random(Guid.NewGuid().GetHashCode());

    class Entry
    {
        public string Id;
        public string UserId;
        public string User;     // display name
        public string Login;    // login / lowercase name
        public string Item;
        public string Added;
    }

    // ------------------------------------------------------------------ MAIN
    public bool Execute()
    {
        string mode = Arg("wheelMode");
        string rawInput = Arg("rawInput");
        string command = Arg("command");

        // If no explicit mode was passed, work it out from the command used.
        if (string.IsNullOrEmpty(mode))
        {
            string c = (command ?? "").Trim().ToLowerInvariant();
            string first = FirstWord(rawInput).ToLowerInvariant();

            if (c == "!spinwheel" || c == "!spin") mode = "spin";
            else if (c == "!addwheel" || c == "!add") mode = "add";
            else if (c == "!removewheel") mode = "remove";
            else if (c == "!wheellist" || c == "!wheelentries") mode = "list";
            else if (c == "!clearwheel") mode = "clear";
            else if (c == "!wheel")
            {
                // !wheel <sub> <value>
                if (first == "add") { mode = "add"; rawInput = DropFirstWord(rawInput); }
                else if (first == "remove" || first == "del") { mode = "remove"; rawInput = DropFirstWord(rawInput); }
                else if (first == "list") { mode = "list"; }
                else if (first == "clear") { mode = "clear"; }
                else if (first == "spin") { mode = "spin"; }
                else if (first == "show") { mode = "show"; }
                else if (first == "hide") { mode = "hide"; }
                else if (string.IsNullOrWhiteSpace(first)) { mode = "help"; }
                else { mode = "add"; } // "!wheel Elden Ring" just adds it
            }
            else mode = "help";
        }

        switch (mode)
        {
            case "add": return DoAdd(rawInput);
            case "remove": return DoRemove(rawInput);
            case "list": return DoList();
            case "clear": return DoClear();
            case "spin": return DoSpin();
            case "sync": return DoSync();
            case "show": Broadcast("{\"wheelEvent\":\"show\"}"); return true;
            case "hide": Broadcast("{\"wheelEvent\":\"hide\"}"); return true;
            default:
                Reply("Wheel commands: !addwheel <thing> · !removewheel · !wheellist · !spinwheel (mods)");
                return true;
        }
    }

    // ------------------------------------------------------------------ ADD
    bool DoAdd(string rawInput)
    {
        string userId = UserId();
        string display = Display();

        if (string.IsNullOrEmpty(userId))
        {
            CPH.LogWarn("[Wheel] add called with no user context — ignoring.");
            return false;
        }

        string item = Clean(rawInput);

        if (item.Length < MIN_ITEM_LENGTH)
        {
            Reply($"@{display} give me something to put on the wheel — e.g. !addwheel Grand Theft Auto V");
            return true;
        }
        if (item.Length > MAX_ITEM_LENGTH)
            item = item.Substring(0, MAX_ITEM_LENGTH).TrimEnd();

        lock (DbLock)
        {
            var list = Load();

            // ---- PER-USER ENTRY LIMIT ----
            if (ENTRIES_PER_USER > 0)
            {
                var theirs = FindAllByUser(list, userId);
                if (theirs.Count >= ENTRIES_PER_USER)
                {
                    if (ENTRIES_PER_USER == 1)
                    {
                        Reply($"@{display} you already have \"{theirs[0].Item}\" on the wheel. " +
                              $"Wait for it to be picked, or use !removewheel first.");
                    }
                    else
                    {
                        var used = new StringBuilder();
                        for (int i = 0; i < theirs.Count; i++)
                        {
                            if (i > 0) used.Append(" · ");
                            used.Append(theirs[i].Item);
                        }
                        Reply($"@{display} you're using all {ENTRIES_PER_USER} of your slots: {used}. " +
                              $"Wait for one to be picked, or !removewheel <name> to free one up.");
                    }
                    return true;
                }
            }

            if (MAX_ENTRIES > 0 && list.Count >= MAX_ENTRIES)
            {
                Reply($"@{display} the wheel is full ({MAX_ENTRIES} entries). Try again after the next spin.");
                return true;
            }

            if (BLOCK_DUPLICATE_ITEMS)
            {
                Entry dupe = FindByItem(list, item);
                if (dupe != null)
                {
                    Reply($"@{display} \"{dupe.Item}\" is already on the wheel (added by {dupe.User}). Pick something else.");
                    return true;
                }
            }

            var entry = new Entry
            {
                Id = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture),
                UserId = userId,
                User = display,
                Login = Login(),
                Item = item,
                Added = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            list.Add(entry);
            Save(list);

            Broadcast(Json(list, "add", -1, entry));

            string slots = "";
            if (ENTRIES_PER_USER > 1)
                slots = $" (your slot {FindAllByUser(list, userId).Count} of {ENTRIES_PER_USER})";
            Reply($"@{display} added \"{item}\" to the wheel{slots} — {list.Count} entr{(list.Count == 1 ? "y" : "ies")} now.");
        }
        return true;
    }

    // --------------------------------------------------------------- REMOVE
    bool DoRemove(string rawInput)
    {
        string userId = UserId();
        string display = Display();
        if (string.IsNullOrEmpty(userId)) return false;

        string want = Clean(rawInput);

        lock (DbLock)
        {
            var list = Load();
            var theirs = FindAllByUser(list, userId);
            if (theirs.Count == 0)
            {
                Reply($"@{display} you don't have anything on the wheel right now.");
                return true;
            }

            Entry mine;
            if (want.Length > 0)
            {
                // they said which one to take off
                mine = null;
                for (int i = 0; i < theirs.Count; i++)
                    if (string.Equals(theirs[i].Item, want, StringComparison.OrdinalIgnoreCase)) mine = theirs[i];
                if (mine == null)
                {
                    var have = new StringBuilder();
                    for (int i = 0; i < theirs.Count; i++)
                    {
                        if (i > 0) have.Append(" · ");
                        have.Append(theirs[i].Item);
                    }
                    Reply($"@{display} you don't have \"{want}\" on the wheel. You have: {have}");
                    return true;
                }
            }
            else mine = theirs[theirs.Count - 1];   // no name given: take the newest off

            list.Remove(mine);
            Save(list);
            Broadcast(Json(list, "remove", -1, mine));
            Reply($"@{display} removed \"{mine.Item}\" from the wheel.");
        }
        return true;
    }

    // ----------------------------------------------------------------- LIST
    bool DoList()
    {
        lock (DbLock)
        {
            var list = Load();
            if (list.Count == 0) { Reply("The wheel is empty — add something with !addwheel <thing>"); return true; }

            int shown = Math.Min(LIST_MAX_SHOWN, list.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(list[i].Item);
            }
            string more = list.Count > shown ? $" (+{list.Count - shown} more)" : "";
            Reply($"On the wheel ({list.Count}): {sb}{more}");
        }
        return true;
    }

    // ---------------------------------------------------------------- CLEAR
    bool DoClear()
    {
        if (MODS_ONLY_CLEAR && !IsElevated())
        {
            Reply($"@{Display()} only mods can clear the wheel.");
            return true;
        }
        lock (DbLock)
        {
            Save(new List<Entry>());
            Broadcast("{\"wheelEvent\":\"clear\",\"entries\":[]}");
            Reply("The wheel has been cleared. !addwheel <thing> to start it off.");
        }
        return true;
    }

    // ----------------------------------------------------------------- SYNC
    bool DoSync()
    {
        lock (DbLock)
        {
            var list = Load();
            Broadcast(Json(list, "sync", -1, null));
        }
        return true;
    }

    // ----------------------------------------------------------------- SPIN
    bool DoSpin()
    {
        if (MODS_ONLY_SPIN && !IsElevated())
        {
            Reply($"@{Display()} only mods can spin the wheel.");
            return true;
        }

        List<Entry> snapshot;
        Entry winner;
        int winIdx;

        lock (DbLock)
        {
            var list = Load();
            if (list.Count == 0)
            {
                Reply("Nothing on the wheel yet — !addwheel <thing> to get it going.");
                return true;
            }

            winIdx = Rng.Next(list.Count);
            winner = list[winIdx];
            snapshot = new List<Entry>(list);

            // Tell the overlay BEFORE we touch the database, so it can draw the
            // full wheel and land on the winner.
            Broadcast(Json(snapshot, "spin", winIdx, winner));

            if (REMOVE_WINNER)
            {
                list.RemoveAt(winIdx);
                Save(list);
            }
        }

        // Expose the result for other actions (TTS, OBS, queue systems, etc.)
        CPH.SetGlobalVar("wheelLastWinnerItem", winner.Item, true);
        CPH.SetGlobalVar("wheelLastWinnerUser", winner.User, true);
        CPH.SetGlobalVar("wheelLastWinnerUserId", winner.UserId, true);
        CPH.SetArgument("wheelWinnerItem", winner.Item);
        CPH.SetArgument("wheelWinnerUser", winner.User);

        if (ANNOUNCE_DELAY_MS > 0) CPH.Wait(ANNOUNCE_DELAY_MS);

        Reply($"🎡 The wheel landed on \"{winner.Item}\" — entered by {winner.User}!" +
              (REMOVE_WINNER ? $" ({winner.User} can now add something new.)" : ""));

        // Push the post-spin list so the overlay and database stay in step.
        lock (DbLock) { Broadcast(Json(Load(), "sync", -1, null)); }
        return true;
    }

    // ---------------------------------------------------------------- FIND
    // Plain loops instead of LINQ — Streamer.bot does not reference System.Core.
    static Entry FindByUser(List<Entry> list, string userId)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].UserId == userId) return list[i];
        return null;
    }

    static List<Entry> FindAllByUser(List<Entry> list, string userId)
    {
        var found = new List<Entry>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].UserId == userId) found.Add(list[i]);
        return found;
    }

    static Entry FindByItem(List<Entry> list, string item)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Item, item, StringComparison.OrdinalIgnoreCase)) return list[i];
        return null;
    }

    // ============================== DATABASE ==============================
    string DbPath()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, DB_FILE);
    }

    List<Entry> Load()
    {
        var list = new List<Entry>();
        string path = DbPath();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] f = line.Split('\t');
                if (f.Length < 5) continue;
                list.Add(new Entry
                {
                    Id = f[0],
                    UserId = f[1],
                    User = Unescape(f[2]),
                    Login = f.Length > 5 ? Unescape(f[5]) : Unescape(f[2]).ToLowerInvariant(),
                    Item = Unescape(f[3]),
                    Added = f[4]
                });
            }
        }
        catch (Exception ex) { CPH.LogError("[Wheel] load failed: " + ex.Message); }
        return list;
    }

    void Save(List<Entry> list)
    {
        string path = DbPath();
        try
        {
            var sb = new StringBuilder();
            foreach (var e in list)
            {
                sb.Append(e.Id).Append('\t')
                  .Append(e.UserId).Append('\t')
                  .Append(Escape(e.User)).Append('\t')
                  .Append(Escape(e.Item)).Append('\t')
                  .Append(e.Added).Append('\t')
                  .Append(Escape(e.Login ?? "")).Append('\n');
            }
            // write to a temp file first so a crash can't leave a half-written database
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex) { CPH.LogError("[Wheel] save failed: " + ex.Message); }
    }

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
    }
    static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
    }

    // ============================ OVERLAY FEED ============================
    string Json(List<Entry> list, string ev, int winnerIndex, Entry winner)
    {
        var sb = new StringBuilder();
        sb.Append("{\"wheelEvent\":\"").Append(ev).Append("\"");
        if (winnerIndex >= 0) sb.Append(",\"winnerIndex\":").Append(winnerIndex);
        if (winner != null)
        {
            sb.Append(",\"winner\":");
            AppendEntry(sb, winner);
        }
        sb.Append(",\"count\":").Append(list.Count);
        sb.Append(",\"entries\":[");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendEntry(sb, list[i]);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    void AppendEntry(StringBuilder sb, Entry e)
    {
        sb.Append("{\"id\":").Append(Q(e.Id))
          .Append(",\"user\":").Append(Q(e.User))
          .Append(",\"userId\":").Append(Q(e.UserId))
          .Append(",\"item\":").Append(Q(e.Item))
          .Append('}');
    }

    static string Q(string s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ' || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    void Broadcast(string json)
    {
        try { CPH.WebsocketBroadcastJson(json); }
        catch (Exception ex) { CPH.LogError("[Wheel] broadcast failed: " + ex.Message); }
    }

    // ============================== HELPERS ===============================
    string Arg(string key)
    {
        object v;
        if (args != null && args.TryGetValue(key, out v) && v != null) return v.ToString();
        return "";
    }

    string UserId()
    {
        string id = Arg("userId");
        if (string.IsNullOrEmpty(id)) id = Arg("userid");
        if (string.IsNullOrEmpty(id)) id = Arg("user");   // last resort: name as identity
        return id;
    }

    string Display()
    {
        string d = Arg("user");
        if (string.IsNullOrEmpty(d)) d = Arg("userName");
        if (string.IsNullOrEmpty(d)) d = "someone";
        return d;
    }

    string Login()
    {
        string l = Arg("userName");
        if (string.IsNullOrEmpty(l)) l = Display().ToLowerInvariant();
        return l;
    }

    bool IsElevated()
    {
        // No user context at all (hotkey, stream deck, timer) = trusted.
        if (string.IsNullOrEmpty(Arg("user")) && string.IsNullOrEmpty(Arg("userId"))) return true;
        return Flag("isModerator") || Flag("isBroadcaster") || Flag("isSuperMod");
    }

    bool Flag(string key)
    {
        object v;
        if (args != null && args.TryGetValue(key, out v) && v != null)
        {
            if (v is bool) return (bool)v;
            bool b;
            if (bool.TryParse(v.ToString(), out b)) return b;
        }
        return false;
    }

    static string FirstWord(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        int i = s.IndexOf(' ');
        return i < 0 ? s : s.Substring(0, i);
    }

    static string DropFirstWord(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        int i = s.IndexOf(' ');
        return i < 0 ? "" : s.Substring(i + 1).Trim();
    }

    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s.Trim())
            if (!char.IsControl(c)) sb.Append(c);
        // collapse runs of whitespace
        string outp = sb.ToString();
        while (outp.Contains("  ")) outp = outp.Replace("  ", " ");
        return outp.Trim();
    }

    void Reply(string message)
    {
        if (!CHAT_REPLIES || string.IsNullOrEmpty(message)) return;
        try
        {
            if (PLATFORM == "twitch" || PLATFORM == "both")
            {
                if (USE_ANNOUNCE) CPH.TwitchAnnounce(message, SEND_AS_BOT, ANNOUNCE_COLOR, true);
                else CPH.SendMessage(message, SEND_AS_BOT);
            }
            if (PLATFORM == "youtube" || PLATFORM == "both") CPH.SendYouTubeMessage(message, SEND_AS_BOT);
        }
        catch (Exception ex) { CPH.LogWarn("[Wheel] reply failed: " + ex.Message); }
    }
}

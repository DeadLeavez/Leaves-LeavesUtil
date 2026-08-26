using System.Text;
using System.Text.RegularExpressions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace Leaves
{
    // Turns serialized JSON into JSONC by appending "// what this id is" to any line ending in one, so a dump of raw MongoIds becomes readable.
    // String in, string out: it matches ids in text and resolves them from the game database, so it works on any generated file. locale only picks the language of those game names.
    // It post-processes because System.Text.Json won't write comments; SPT's JsonUtil reads them back fine (ReadCommentHandling.Skip).
    [Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
    public class JsonIdAnnotator(
        DatabaseService databaseService,
        Utils leavesUtils)
    {
        // A quoted value at end of line (a property value or array element); anchoring to line-end means a comment can only land after a complete value, never inside a string.
        private static readonly Regex TrailingValue = new(@"""([^""\\]+)""(,?)\s*$", RegexOptions.Compiled);

        // A quoted key at start of line, for dictionaries keyed BY id (an id->count map), which TrailingValue can't catch since a key doesn't end its line.
        private static readonly Regex LeadingKey = new(@"^\s*""([^""\\]+)""\s*:", RegexOptions.Compiled);

        // Relies on WriteIndented putting every value on its own line, so a line-oriented pass is correct.
        public string Annotate(string json, string locale = "en")
        {
            var builder = new StringBuilder(json.Length + json.Length / 4);
            var itemDB = databaseService.GetItems();
            var quests = databaseService.GetQuests();
            // memoise lookups; the same id recurs across hundreds of lines
            var resolved = new Dictionary<string, string?>();

            foreach (string line in json.ReplaceLineEndings("\n").Split('\n'))
            {
                builder.Append(line);

                // value first: it's the more specific thing on the line, and only one comment is appended anyway
                string? name = Lookup(TrailingValue, line, locale, itemDB, quests, resolved)
                            ?? Lookup(LeadingKey, line, locale, itemDB, quests, resolved);
                if (name != null)
                {
                    builder.Append(" // ").Append(name);
                }
                builder.Append('\n');
            }

            return builder.ToString();
        }

        // Runs one pattern against the line and resolves whatever it captured, memoised across the file.
        private string? Lookup(
            Regex pattern,
            string line,
            string locale,
            Dictionary<MongoId, TemplateItem> itemDB,
            Dictionary<MongoId, Quest> quests,
            Dictionary<string, string?> resolved)
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                return null;
            }
            string value = match.Groups[1].Value;
            if (!resolved.TryGetValue(value, out var name))
            {
                name = Describe(value, locale, itemDB, quests);
                resolved[value] = name;
            }
            return name;
        }

        // Null for anything unnameable (condition ids, enums, plain strings); annotating those just adds noise.
        private string? Describe(
            string value,
            string locale,
            Dictionary<MongoId, TemplateItem> itemDB,
            Dictionary<MongoId, Quest> quests)
        {
            // map codenames have the same problem ("bigmap" tells you nothing); the game's location data names them
            if (!IsMongoId(value))
            {
                var location = leavesUtils.findLocationByCodeName(value);
                string? mapName = location?.Base?.Name;
                if (mapName != null && mapName.Length > 0)
                {
                    return mapName;
                }
                return null;
            }

            MongoId id = value;

            if (quests.ContainsKey(id))
            {
                string? questName = leavesUtils.getLocale(locale, value, " name");
                if (!string.IsNullOrWhiteSpace(questName))
                {
                    return Leaves.Utils.Instance.RTT_Strip(questName);
                }
            }

            if (itemDB.TryGetValue(id, out var template))
            {
                // item CLASSES have no locale entry, so fall back to the template's internal name ("Drugs", "Medkit")
                string? itemName = leavesUtils.getLocale(locale, value, " Name");
                return !string.IsNullOrWhiteSpace(itemName) ? Leaves.Utils.Instance.RTT_Strip(itemName) : template.Name;
            }

            return null;
        }

        private static bool IsMongoId(string value)
        {
            if (value.Length != 24)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }
    }
}

using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Leaves
{
    // Optional startup dump of the item tree and quest list to debug/ files, gated behind the dataDump config flag.
    // Runs after LeavesUtils' own OnLoad (PostSptModLoader + 1) so item names read through getLocale are already fully transformed.
    [Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 1)]
    public class DataDump(
        DatabaseService databaseService,
        JsonUtil jsonUtil,
        ModHelper modHelper,
        Utils leavesUtils)
        : IOnLoad
    {
        private readonly string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());

        public Task OnLoad()
        {
            var config = modHelper.GetJsonDataFromFile<LeavesUtilsConfig>(modPath, "config/config.jsonc");
            if (config.DataDump)
            {
                dataDump();
            }
            return Task.CompletedTask;
        }

        public void dataDump()
        {
            // hand-formatted text, not valid JSON, so it's written directly rather than through a serializer helper
            string debugFolder = System.IO.Path.Combine(modPath, "debug");
            System.IO.Directory.CreateDirectory(debugFolder);

            var questDB = databaseService.GetQuests();
            string questList = "";
            foreach (var quest in questDB)
            {
                questList += $"\"{quest.Key}\", //{quest.Value.QuestName}\n";
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(debugFolder, "quests.jsonc"), questList);

            var target = new Dictionary<string, object>();
            var itemDB = databaseService.GetItems();
            int totalItems = itemDB.Count;
            int itemsDone = 0;
            leavesUtils.printColor($"Starting dump of items (0/{totalItems})");
            foreach (var item in itemDB)
            {
                if (item.Value.Type == "Item")
                {
                    try
                    {
                        add(item.Key, target);
                    }
                    catch (Exception)
                    {
                        leavesUtils.debugJsonOutput(target);
                        return;
                    }
                }

                itemsDone++;
                if (itemsDone % 100 == 0)
                {
                    printProgress($"Dumping items: {itemsDone}/{totalItems}");
                }
            }
            Console.WriteLine();
            leavesUtils.printColor($"Finished building item tree ({totalItems}/{totalItems})");

            string serialized = jsonUtil.Serialize(target, true) ?? "";
            var lines = serialized.Split('\n');
            int totalLines = lines.Length;
            int linesDone = 0;
            string processed = "";
            foreach (var line in lines)
            {
                processed += line;
                if (line.Contains('"'))
                {
                    string id = getStringBetweenChars(line, "\"", "\"");
                    string? locale = leavesUtils.getLocale("en", id, " Name");
                    if (locale == null && itemDB.ContainsKey(id))
                    {
                        locale = itemDB[id].Name;
                    }
                    processed += " //" + locale;
                }
                processed += "\n";

                linesDone++;
                if (linesDone % 100 == 0)
                {
                    printProgress($"Annotating output: {linesDone}/{totalLines}");
                }
            }
            Console.WriteLine();
            leavesUtils.printColor($"Finished annotating output ({totalLines}/{totalLines})");
            System.IO.File.WriteAllText(System.IO.Path.Combine(debugFolder, "allItems.jsonc"), processed);
            leavesUtils.printColor("Data dump complete.");
        }

        // straight to the console, not the async SPT logger, so the \r actually overwrites the previous line
        private void printProgress(string message)
        {
            Console.Write("\r" + message.PadRight(60));
        }

        private string getStringBetweenChars(
            string original,
            string char1,
            string char2
        )
        {
            int start = original.IndexOf(char1) + 1;
            int end = original.LastIndexOf(char2);
            return original.Substring(start, end - start);
        }

        private void add(MongoId item, Dictionary<string, object> target)
        {
            var itemDB = databaseService.GetItems();
            var order = new List<MongoId>();
            MongoId current = item;
            MongoId finalParent = itemDB[item].Parent;

            // build the parent chain, root first
            do
            {
                current = itemDB[current].Parent;
                if (current == "")
                {
                    break;
                }
                order.Insert(0, current);
            } while (current != "");

            // walk that chain, creating each nested dictionary and hanging the item off its final parent
            var tempTarget = target;
            foreach (var toCheck in order)
            {
                string key = toCheck.ToString();
                if (toCheck == finalParent)
                {
                    if (!tempTarget.ContainsKey(key))
                    {
                        tempTarget[key] = new Dictionary<string, object>();
                    }
                    ((Dictionary<string, object>)tempTarget[key])[item.ToString()] = true;
                }
                if (!tempTarget.ContainsKey(key))
                {
                    tempTarget[key] = new Dictionary<string, object>();
                }

                tempTarget = (Dictionary<string, object>)tempTarget[key];
            }
        }
    }
}

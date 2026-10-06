using System.Numerics;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Leaves
{
    // Singleton so this class's own OnLoad runs on the same instance every mod injects; without it DI builds a second instance whose OnLoad clears a cache nobody reads.
    // Runs last (PostSptModLoader) so every locale-mutating transformer is registered before OnLoad clears localeCache.
    [Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]
    public partial class Utils : IOnLoad
    {
        // Static handle to the one shared Utils, for code that can't inject it. Set in the constructor (a field initializer can't reference this, and OnLoad runs too late for other mods that want it early).
        public static Utils Instance { get; private set; } = null!;

        private readonly ISptLogger<Utils> logger; // logger must carry the class in its <> brackets
        private readonly DatabaseService databaseService;
        private readonly JsonUtil jsonUtil;
        private readonly WeightedRandomHelper weightedRandomHelper;
        private readonly LocaleService localeService;
        private readonly FileUtil fileUtil;
        private readonly TimeUtil timeUtil;
        private readonly ProfileHelper profileHelper;
        private readonly ModHelper modHelper;

        public Utils(
            ISptLogger<Utils> logger,
            DatabaseService databaseService,
            JsonUtil jsonUtil,
            WeightedRandomHelper weightedRandomHelper,
            LocaleService localeService,
            FileUtil fileUtil,
            TimeUtil timeUtil,
            ProfileHelper profileHelper,
            ModHelper modHelper)
        {
            this.logger = logger;
            this.databaseService = databaseService;
            this.jsonUtil = jsonUtil;
            this.weightedRandomHelper = weightedRandomHelper;
            this.localeService = localeService;
            this.fileUtil = fileUtil;
            this.timeUtil = timeUtil;
            this.profileHelper = profileHelper;
            this.modHelper = modHelper;
            Instance = this;
        }

        // ============================================================
        // Shared web UI profile filter
        // ============================================================
        // The suite-wide "hide utility/alt profiles from web UI pickers" rule, read from this mod's config so every mod filters the same way. Loaded once (config is boot-time static).
        private LeavesUtilsConfig? webUiConfig;
        private readonly object webUiConfigLock = new();

        private LeavesUtilsConfig getWebUiConfig()
        {
            lock (webUiConfigLock)
            {
                if (webUiConfig == null)
                {
                    string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
                    webUiConfig = modHelper.GetJsonDataFromFile<LeavesUtilsConfig>(modPath, "config/config.jsonc");
                }
                return webUiConfig;
            }
        }

        // True if this nickname matches one of the hidden prefixes (a headless/dev account). Case-insensitive; always false when the filter is off.
        public bool ShouldHideProfileFromWebUi(string nickname)
        {
            var config = getWebUiConfig();
            if (!config.WebUiFilterProfiles)
            {
                return false;
            }
            foreach (var prefix in config.WebUiHiddenProfilePrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && nickname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private readonly Random random = new();

        // Per-language locale cache, cleared once by OnLoad after every mod has loaded.
        // The clear matters: a locale read before a later mod registers its locale transformer would otherwise cache the stale pre-transform copy forever and silently undo that mod's fix.
        private readonly Dictionary<string, Dictionary<string, string>> localeCache = new();

        public Task OnLoad()
        {
            localeCache.Clear();
            return Task.CompletedTask;
        }

        // ============================================================
        // File I/O
        // ============================================================

        // Loads a file by full path through SPT's JsonUtil, which handles MongoId and .jsonc comments natively.
        public T loadFile<T>(string file)
        {
            return jsonUtil.Deserialize<T>(fileUtil.ReadFile(file))!;
        }

        // Parses a JSON string through SPT's JsonUtil (MongoId-aware). Returns default for empty
        // input; throws on malformed JSON, same as jsonUtil.Deserialize.
        // Pairs with the web config editor: the POST body is a string, not a file.
        public T? stringToJson<T>(string json)
        {
            return jsonUtil.Deserialize<T>(json);
        }

        // MongoId-safe save via SPT's JsonUtil. modPath is explicit because this class is shared, so there's no "current mod folder" to assume.
        public void saveJsonFile<T>(
            T data,
            string modPath,
            string file
        )
        {
            string serialized = jsonUtil.Serialize(data, true) ?? "{}";
            fileUtil.WriteFile(System.IO.Path.Combine(modPath, file), serialized);
        }

        // Empty list rather than throwing when the folder doesn't exist: a runtime-only folder won't exist on a fresh install.
        public List<string> getFilesInFolder(string modPath, string folder)
        {
            // TODO: possibly strip full path.
            string fullPath = System.IO.Path.Combine(modPath, folder);
            if (!fileUtil.DirectoryExists(fullPath))
            {
                return new List<string>();
            }
            return fileUtil.GetFiles(fullPath);
        }

        public string getFileWithoutExtension(string file)
        {
            // Not SPT's StripExtension: it cuts at the FIRST dot, so an install path like ...\SPT-4.0.13\... truncates to ...\SPT-4. This drops only the final extension.
            return System.IO.Path.GetFileNameWithoutExtension(file);
        }

        // Appends to a full path (SPT's FileUtil has no append), creating the parent dir first. For runtime logs, so the path isn't resolved against any mod folder.
        public void appendFile(string data, string file)
        {
            string? dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            System.IO.File.AppendAllText(file, data);
        }

        // Writes text to <outputFolder>/<prefix>_<date>_<time>.json and returns that path, so a caller logging it doesn't recompute the timestamp (and risk a different name).
        public string writeTimestampedResult(
            string outputFolder,
            string prefix,
            string text
        )
        {
            string fileName = $"{prefix}_{timeUtil.GetDate()}_{timeUtil.GetTime()}.json";
            string fullPath = System.IO.Path.Combine(outputFolder, fileName);
            fileUtil.WriteFile(fullPath, text);
            return fullPath;
        }

        // ============================================================
        // Item & id helpers
        // ============================================================

        // Returns the stored id for name, minting a fresh one if absent. Caller owns the dictionary (this stays stateless).
        public MongoId getOrCreateId(Dictionary<string, MongoId> ids, string name)
        {
            if (!ids.TryGetValue(name, out var id))
            {
                id = new MongoId();
                ids[name] = id;
            }
            return id;
        }

        // Reassigns every item a fresh MongoId, rewriting ParentId links so the parent-child structure survives (e.g. cloning a weapon plus its attachments into a new assort entry).
        public void generateNewItemIds(List<Item> items)
        {
            var ids = new Dictionary<MongoId, MongoId>();

            foreach (var item in items)
            {
                if (!ids.ContainsKey(item.Id))
                {
                    ids[item.Id] = new MongoId();
                }
            }

            foreach (var (oldId, newId) in ids)
            {
                foreach (var item in items)
                {
                    if (item.Id == oldId)
                    {
                        item.Id = newId;
                    }

                    if (item.ParentId != null && item.ParentId == oldId.ToString())
                    {
                        item.ParentId = newId.ToString();
                    }
                }
            }
        }

        // Positional access into Slots/Grids (which are IEnumerable, not indexable), returning the live filter HashSet so callers can .Add() to it in place.
        public HashSet<MongoId> getSlotFilter(TemplateItem item, int slotIndex)
        {
            return item.Properties!.Slots!.ElementAt(slotIndex).Properties!.Filters!.First().Filter!;
        }

        public HashSet<MongoId> getGridFilter(TemplateItem item, int gridIndex)
        {
            return item.Properties!.Grids!.ElementAt(gridIndex).Properties!.Filters!.First().Filter!;
        }

        // depth 0 = walk to the root; depth N = at most N hops (1 = immediate _parent only).
        public bool hasParent(MongoId tpl, MongoId parentNode, int depth = 0)
        {
            Dictionary<MongoId, TemplateItem> itemDB = databaseService.GetItems();
            if (!itemDB.TryGetValue(tpl, out TemplateItem? item))
            {
                return false;
            }

            MongoId current = item.Parent;
            int hops = 0;
            while (true)
            {
                if (current == parentNode)
                {
                    return true;
                }
                hops++;
                if (depth > 0 && hops >= depth)
                {
                    return false;
                }
                if (hops > 64)
                {
                    // cycle guard
                    return false;
                }
                if (!itemDB.TryGetValue(current, out TemplateItem? node))
                {
                    // root has no DB entry for its _parent - end of the chain
                    return false;
                }
                current = node.Parent;
            }
        }

        // Adds `tpl` to the ExcludedFilter of every grid of every item under `familyNode`.
        // Call from a late IOnLoad (PostSptModLoader + high) or content-mod items are missed.
        // Returns the count of templates touched.
        public int addToGridBlacklist(MongoId tpl, MongoId familyNode, int depth = 0)
        {
            Dictionary<MongoId, TemplateItem> itemDB = databaseService.GetItems();
            MongoId itemRoot = new("54009119af1c881c07000029");
            int touched = 0;

            foreach (KeyValuePair<MongoId, TemplateItem> entry in itemDB)
            {
                TemplateItem template = entry.Value;
                if (template.Properties == null || template.Properties.Grids == null)
                {
                    continue;
                }
                if (!hasParent(entry.Key, familyNode, depth))
                {
                    continue;
                }

                bool changed = false;
                foreach (Grid grid in template.Properties.Grids)
                {
                    if (grid.Properties == null)
                    {
                        continue;
                    }

                    bool hasFilter = false;
                    if (grid.Properties.Filters != null)
                    {
                        foreach (GridFilter existing in grid.Properties.Filters)
                        {
                            hasFilter = true;
                            break;
                        }
                    }
                    if (!hasFilter)
                    {
                        // no filter = accepts everything; synthesise one that still does
                        grid.Properties.Filters = new List<GridFilter>
                        {
                            new GridFilter
                            {
                                Filter = new HashSet<MongoId> { itemRoot },
                                ExcludedFilter = new HashSet<MongoId>(),
                            },
                        };
                    }

                    foreach (GridFilter filter in grid.Properties.Filters!)
                    {
                        if (filter.ExcludedFilter == null)
                        {
                            filter.ExcludedFilter = new HashSet<MongoId>();
                        }
                        if (filter.ExcludedFilter.Add(tpl))
                        {
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    touched++;
                }
            }

            return touched;
        }

        // Is this trade a straight cash sale rather than a barter?
        // Guarded because other trader mods ship trades with a missing/empty barter scheme; we log and treat those as non-money rather than crash.
        public bool isMoneyTrade(TraderAssort assort, Item trade)
        {
            if (!assort.BarterScheme.TryGetValue(trade.Id, out var scheme)
                || scheme.Count == 0
                || scheme[0].Count == 0
            )
            {
                printColor("[LeavesUtils] isMoneyTrade: trade has a missing or empty barter scheme - treating as non-money.", LogTextColor.Red);
                printColor($"[LeavesUtils] Offending trade - Template: {trade.Template} TradeID: {trade.Id}", LogTextColor.Red);
                return false;
            }

            if (scheme[0].Count > 1)
            {
                return false;
            }
            return Money.GetMoneyTpls().Contains(scheme[0][0].Template);
        }

        // Adds a purchasable trade to a trader: the stock item, its cost, and its loyalty gate.
        // `cost` is a list of required items (all needed together - an AND barter); a single money/currency entry makes it a plain cash sale.
        // Returns the new assort item id (distinct from the tpl - one tpl can be sold multiple times).
        // Mirrors the vanilla assort shape (Items + BarterScheme + LoyalLevelItems) so it behaves like any normal trader offer.
        // `default` (empty id) is returned if the trader doesn't exist.
        public MongoId addTrade(MongoId traderId, MongoId itemTpl, List<TradeEntry> cost, int loyaltyLevel, int stackCount = 999999, bool unlimited = true)
        {
            var traders = databaseService.GetTraders();
            if (!traders.TryGetValue(traderId, out var trader) || trader.Assort == null)
            {
                printColor($"[LeavesUtils] addTrade: trader {traderId} not found or has no assort.", LogTextColor.Red);
                return default;
            }
            return addTrade(trader.Assort, itemTpl, cost, loyaltyLevel, stackCount, unlimited);
        }

        // Assort overload for callers that already hold the TraderAssort.
        public MongoId addTrade(TraderAssort assort, MongoId itemTpl, List<TradeEntry> cost, int loyaltyLevel, int stackCount = 999999, bool unlimited = true)
        {
            var itemId = new MongoId();

            var item = new Item
            {
                Id = itemId,
                Template = itemTpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd
                {
                    StackObjectsCount = stackCount,
                    UnlimitedCount = unlimited,
                },
            };
            assort.Items.Add(item);

            var scheme = new List<BarterScheme>();
            foreach (var entry in cost)
            {
                scheme.Add(new BarterScheme
                {
                    Count = entry.Count,
                    Template = entry.Tpl,
                });
            }
            assort.BarterScheme[itemId] = new List<List<BarterScheme>> { scheme };
            assort.LoyalLevelItems[itemId] = loyaltyLevel;

            return itemId;
        }

        // Convenience overloads for the common single-currency cash sale.
        // `price` is in units of `currency` (Money.ROUBLES, USD/EUR, a GP-coin tpl, …).
        public MongoId addTrade(MongoId traderId, MongoId itemTpl, MongoId currency, double price, int loyaltyLevel, int stackCount = 999999, bool unlimited = true)
        {
            return addTrade(traderId, itemTpl, new List<TradeEntry> { new TradeEntry(currency, price) }, loyaltyLevel, stackCount, unlimited);
        }

        public MongoId addTrade(TraderAssort assort, MongoId itemTpl, MongoId currency, double price, int loyaltyLevel, int stackCount = 999999, bool unlimited = true)
        {
            return addTrade(assort, itemTpl, new List<TradeEntry> { new TradeEntry(currency, price) }, loyaltyLevel, stackCount, unlimited);
        }

        // ============================================================
        // Database / world lookups
        // ============================================================

        // Map codenames (e.g. "factory4_day") for every location that has loose loot, i.e. actual playable maps, not "hideout"/"develop"/etc.
        public List<string> getLocationNames()
        {
            var names = new List<string>();
            foreach (var location in databaseService.GetLocations().GetDictionary().Values)
            {
                if (location.LooseLoot != null)
                {
                    names.Add(location.Base.Id.ToLower());
                }
            }
            return names;
        }

        // GetDictionary() is keyed by C# property name ("Factory4Day"), not the config codename ("factory4_day"), so match on Base.Id instead.
        public Location? findLocationByCodeName(string mapCodeName)
        {
            foreach (var location in databaseService.GetLocations().GetDictionary().Values)
            {
                if (string.Equals(location.Base.Id, mapCodeName, StringComparison.OrdinalIgnoreCase))
                {
                    return location;
                }
            }
            return null;
        }

        public string getTraderNickname(MongoId traderID)
        {
            string? name = databaseService.GetTraders()[traderID].Base.Nickname;

            if (name == null)
            {
                printColor($"[LeavesUtils] Found *VERY* broken trader:{traderID}. Consider contact the author of the trader and yell at them. XD", LogTextColor.Red);
                return "ERROR";
            }

            return name;
        }

        // The trader's portrait path, served same-origin so a web page can drop it into an <img src>. Null if the trader is missing or has no avatar.
        public string? getTraderAvatar(MongoId traderID)
        {
            if (databaseService.GetTraders().TryGetValue(traderID, out var trader))
            {
                return trader.Base?.Avatar;
            }
            return null;
        }

        // ============================================================
        // Profile quest status
        // ============================================================

        // Statuses that mean the player has accepted the quest (progressed, failed, or finished). One list so both helpers below agree on where "started" begins.
        private static readonly QuestStatusEnum[] StartedQuestStatuses =
        {
            QuestStatusEnum.Started,
            QuestStatusEnum.AvailableForFinish,
            QuestStatusEnum.Success,
            QuestStatusEnum.Fail,
            QuestStatusEnum.FailRestartable,
            QuestStatusEnum.MarkedAsFailed,
            QuestStatusEnum.Expired,
        };

        // The profile's status for one quest, or null when the profile has no entry (never offered/accepted). profileId is the PMC session id.
        public QuestStatusEnum? getProfileQuestStatus(MongoId profileId, MongoId questId)
        {
            var pmc = profileHelper.GetPmcProfile(profileId);
            if (pmc == null || pmc.Quests == null)
            {
                return null;
            }
            foreach (var questStatus in pmc.Quests)
            {
                if (questStatus.QId == questId)
                {
                    return questStatus.Status;
                }
            }
            return null;
        }

        // True if the player has ever accepted this quest. Use it to avoid disturbing a quest they're (or were) running.
        public bool hasProfileStartedQuest(MongoId profileId, MongoId questId)
        {
            QuestStatusEnum? status = getProfileQuestStatus(profileId, questId);
            if (status == null)
            {
                return false;
            }

            return StartedQuestStatuses.Contains(status.Value);
        }

        // ============================================================
        // Locale
        // ============================================================

        // GetLocaleDb re-parses the whole multi-megabyte locale file on every call, so cache each language once; calling it per item in a loop used to reparse the file every time.
        private Dictionary<string, string> getCachedLocaleDb(string language)
        {
            if (!localeCache.TryGetValue(language, out var db))
            {
                db = localeService.GetLocaleDb(language);
                localeCache[language] = db;
            }
            return db;
        }

        public string? getLocale(
            string locale,
            string id,
            string type = ""
        )
        {
            var localeDB = getCachedLocaleDb(locale);

            if (!localeDB.ContainsKey(id + type))
            {
                localeDB = getCachedLocaleDb("en");
            }

            return localeDB.TryGetValue(id + type, out var value) ? value : null;
        }

        // ============================================================
        // Logging / debug
        // ============================================================

        public bool debug { private get; set; }

        public void printColor(
            string message,
            LogTextColor color = LogTextColor.Green,
            LogLevel level = LogLevel.Info,
            bool debug = false
        )
        {
            if (debug == true && this.debug == false)
            {
                return;
            }
            logger.Log(level, message, color);
        }

        public void debugJsonOutput(object jsonObject, string label = "")
        {
            if (label.Length > 0)
            {
                logger.LogWithColor($"[{label}]", LogTextColor.Green);
            }
            logger.LogWithColor(jsonUtil.Serialize(jsonObject, true)!, LogTextColor.Magenta);
        }

        public bool searchObject(string stringToFind, object o)
        {
            // hackiest shit ever
            return jsonUtil.Serialize(o)?.Contains(stringToFind) == true;
        }

        // ============================================================
        // Random / math
        // ============================================================

        // set guarantees unique values
        public T[] getUniqueValues<T>(T[] array, int count) where T : notnull
        {
            if (count > array.Length)
            {
                count = array.Length;
            }

            var generatedValues = new HashSet<T>();

            while (generatedValues.Count < count)
            {
                generatedValues.Add(array[random.Next(0, array.Length)]);
            }

            return generatedValues.ToArray();
        }

        public T[] getUniqueWeightedValues<T>(Dictionary<T, double> weightedArray, int count) where T : notnull
        {
            if (count > weightedArray.Count)
            {
                count = weightedArray.Count;
            }

            var generatedValues = new HashSet<T>();

            while (generatedValues.Count < count)
            {
                generatedValues.Add(weightedRandomHelper.GetWeightedValue(weightedArray));
            }

            return generatedValues.ToArray();
        }

        public double generateValueAdjustmentInt(int previousValue, Vector2 factors)
        {
            double multiplier = 1 + (random.NextDouble() * factors.X - random.NextDouble() * factors.Y);
            double newValue = Math.Round(previousValue * multiplier);
            if (newValue < 1)
            {
                return 1;
            }
            return newValue;
        }

        // ============================================================
        // Rich text formatting (RTT_*)
        // ============================================================

        public string RTT_Underline(string original)
        {
            return $"<underline>{original}</underline>";
        }
        public string RTT_Rotate(string original, double angle)
        {
            return $"<rotate=\"{angle}\">{original}</rotate>";
        }
        public string RTT_Align(string original, string alignment)
        {
            return $"<align=\"{alignment}\">{original}<align>";
        }
        public string RTT_Bold(string original)
        {
            return $"<b>{original}</b>";
        }
        public string RTT_Italic(string original)
        {
            return $"<i>{original}</i>";
        }
        public string RTT_Color(string original, string color)
        {
            if (color.Length == 0)
            {
                return original;
            }
            if (color[0] == '#')
            {
                return $"<color={color}>{original}</color>";
            }
            else
            {
                return $"<color=\"{color}\">{original}</color>";
            }
        }
        public string RTT_Rainbowify(string original)
        {
            string newString = "";
            double step = 1.0 / original.Length;
            double start = 0;
            foreach (char c in original)
            {
                var color = HSVtoRGB(start, 1, 1);
                string hexstring = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
                newString += RTT_Color(c.ToString(), hexstring);
                start += step;
            }
            return newString;
        }
        public string RTT_TFify(string original)
        {
            string newString = "";
            int step = 0;
            foreach (char c in original)
            {
                var color = TFifyColor(step, original.Length);
                string hexstring = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
                newString += RTT_Color(c.ToString(), hexstring);
                step++;
            }
            return newString;
        }
        private (int R, int G, int B) TFifyColor(int step, int length)
        {
            (int R, int G, int B) colorb = (91, 206, 250);
            (int R, int G, int B) colorp = (245, 169, 184);
            (int R, int G, int B) colorw = (255, 255, 255);

            double ratio = (double)step / length;
            if (ratio <= 0.2)
                return colorb;
            if (ratio > 0.2 && ratio <= 0.4)
                return colorp;
            if (ratio > 0.4 && ratio <= 0.6)
                return colorw;
            if (ratio > 0.6 && ratio <= 0.8)
                return colorp;
            if (ratio > 0.8)
                return colorb;

            return colorp;
        }
        public string RTT_Size(string original, string size)
        {
            return $"<size={size}>{original}</size>";
        }

        // Strip RTT tags. Matcher is source-generated at compile time, so no startup JIT cost.
        [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
        private static partial System.Text.RegularExpressions.Regex RTT_TagPattern();
        public string RTT_Strip(string text)
        {
            return RTT_TagPattern().Replace(text, "");
        }

        // HSV to RGB, from https://stackoverflow.com/questions/17242144
        public (int R, int G, int B) HSVtoRGB(
            double h,
            double s,
            double v
        )
        {
            double red, green, blue;
            int i = (int)Math.Floor(h * 6);
            double f = h * 6 - i;
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);
            switch (i % 6)
            {
                case 0: red = v; green = t; blue = p; break;
                case 1: red = q; green = v; blue = p; break;
                case 2: red = p; green = v; blue = t; break;
                case 3: red = p; green = q; blue = v; break;
                case 4: red = t; green = p; blue = v; break;
                default: red = v; green = p; blue = q; break;
            }
            return ((int)Math.Round(red * 255), (int)Math.Round(green * 255), (int)Math.Round(blue * 255));
        }

        // Builds a hideout-craft recipe and adds it to the live production list, the one call a mod needs to add a craft.
        // Returns the recipe, but it's already added so the return can be ignored.
        public HideoutProduction addCraft(
            MongoId id,
            HideoutAreas areaType,
            List<Requirement> requirements,
            double productionTime,
            MongoId endProduct
        )
        {
            var craft = makeCraft(id, areaType, requirements, productionTime, endProduct);

            var production = databaseService.GetHideout().Production;
            production.Recipes ??= new List<HideoutProduction>();
            production.Recipes.Add(craft);

            return craft;
        }

        // Pure builder for one HideoutProduction with the fixed housekeeping flags (no fuel, unlocked, not continuous, single output). Callers use addCraft.
        private HideoutProduction makeCraft(
            MongoId id,
            HideoutAreas areaType,
            List<Requirement> requirements,
            double productionTime,
            MongoId endProduct
        )
        {
            return new HideoutProduction
            {
                Id = id,
                AreaType = areaType,
                Requirements = requirements,
                ProductionTime = productionTime,
                NeedFuelForAllProductionTime = false,
                Locked = false,
                EndProduct = endProduct,
                Continuous = false,
                Count = 1,
                ProductionLimitCount = 0,
                IsEncoded = false
            };
        }

        // A consumed-item requirement (Type "Item"): count of templateId used up by the craft.
        public Requirement craftItemRequirement(MongoId templateId, int count)
        {
            return new Requirement
            {
                TemplateId = templateId,
                Count = count,
                IsEncoded = false,
                IsFunctional = false,
                IsSpawnedInSession = false,
                Type = "Item"
            };
        }

        // A tool requirement (Type "Tool"): the item must be present but is not consumed by the craft.
        public Requirement craftToolRequirement(MongoId templateId)
        {
            return new Requirement
            {
                TemplateId = templateId,
                Type = "Tool"
            };
        }

        // An area requirement (Type "Area"): which hideout area and level the craft needs.
        // areaType stays a raw int because some areas (e.g. Lavatory = 2) have no HideoutAreas enum member.
        public Requirement craftAreaRequirement(int areaType, int requiredLevel)
        {
            return new Requirement
            {
                AreaType = areaType,
                RequiredLevel = requiredLevel,
                Type = "Area"
            };
        }
    }

    public static class RTT_Align
    {
        public const string LEFT = "left";
        public const string CENTER = "center";
        public const string RIGHT = "right";
        public const string JUSTIFIED = "justified";
        public const string FLUSH = "flush";
    }

    public static class RTT_Colors
    {
        public const string BLACK = "black";
        public const string BLUE = "blue";
        public const string GREEN = "green";
        public const string ORANGE = "orange";
        public const string PURPLE = "purple";
        public const string RED = "red";
        public const string WHITE = "white";
    }
}

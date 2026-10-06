using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Leaves;

// The web UI's HTTP surface, hosted from LeavesUtils under the "/webui/" prefix: the shared nav rail and stylesheet, the item browser page and its data feeds, and shared profile/stash feeds. See Handle for the routing.
[Injectable]
public partial class WebUiHost(
    ModHelper modHelper,
    DatabaseService databaseService,
    LocaleService localeService,
    ProfileHelper profileHelper,
    SaveServer saveServer,
    JsonUtil jsonUtil)
    : IHttpListener, IOnLoad
{
    // Register our own routes into the shared directory once every mod has loaded (Utils.Instance is set well before any OnLoad).
    public Task OnLoad()
    {
        var routes = new List<RouteInfo>
        {
            // The static page/asset files below (rail.js, base.css, the item-browser / dev / load-order
            // / config-editor pages) are now served by the Leaves-WebUI mod, which registers them under
            // "Web UI (static files)". Left here commented so the move is visible.
            //   new RouteInfo("GET", "/webui/rail.js", "Suite-wide nav-rail client script (served from rail.txt).", "internal"),
            //   new RouteInfo("GET", "/webui/base.css", "Suite-wide shared stylesheet linked by every mod page.", "internal"),
            //   new RouteInfo("GET", "/webui/db", "Item Browser page.", "ui"),
            //   new RouteInfo("GET", "/webui/dev/routes", "Dev page: this route directory.", "ui"),
            //   new RouteInfo("GET", "/webui/dev/loadorder", "Dev page: IOnLoad run order across all mods + core.", "ui"),
            //   new RouteInfo("GET", "/webui/config/ui", "Universal config editor page (?mod=<registered mod name>).", "ui"),
            new RouteInfo("GET", "/webui/dev/routesJson", "Every route each installed mod registered, as JSON.", "api"),
            new RouteInfo("GET", "/webui/dev/loadorderJson", "Every IOnLoad component and its Injectable priority, in run order, as JSON.", "api"),
            new RouteInfo("GET", "/webui/rail/neighbors", "Installed non-Leaves mod UIs for the rail to link out to.", "api"),
            new RouteInfo("GET", "/webui/lock", "Web UI accident-lock state: { enabled, hash } (SHA-256 of the configured password) for the rail's padlock.", "api"),
            new RouteInfo("GET", "/webui/config", "Config editor feed: mods that registered a Kind=\"config\" GET (+ optional POST).", "api"),
            new RouteInfo("GET", "/webui/db/index", "Full item search index (one compact row per item).", "api"),
            new RouteInfo("GET", "/webui/db/query?parent=&name=&desc=&type=&limit=", "Item query; name/desc are case-insensitive regex, all filters AND.", "api"),
            new RouteInfo("GET", "/webui/db/nodes", "Category (Node) tree only, nested from the root; no leaf items.", "api"),
            new RouteInfo("GET", "/webui/db/traders", "Trader picker feed [{ id, nickname }] for [AllowedIds(Trader)] fields.", "api"),
            new RouteInfo("GET", "/webui/db/profiles", "Profile-picker feed [{ id, nickname, level }].", "api"),
            new RouteInfo("GET", "/webui/db/stash?profileId=", "A profile's stash contents summed by template id.", "api"),
            new RouteInfo("GET", "/webui/db/item/{id}", "Full detail blob for one item (template + locale + handbook).", "api"),
            new RouteInfo("GET", "/webui/db/localeNames", "Flat { id: displayName } of every \" Name\" locale key plus trader nicknames; the live (modded) name table for offline id-label lookups.", "api"),
        };
        Utils.Instance.RegisterRoutes("LeavesUtil (Web UI)", routes);
        return Task.CompletedTask;
    }

    private const string Prefix = "/webui/";
    private const string DbPagePath = "/webui/db";
    private const string DbIndexPath = "/webui/db/index";
    private const string DbQueryPath = "/webui/db/query";
    private const string DbNodesPath = "/webui/db/nodes";
    private const string DbTradersPath = "/webui/db/traders";
    private const string DbItemPrefix = "/webui/db/item/";
    private const string DbProfilesPath = "/webui/db/profiles";
    private const string DbStashPath = "/webui/db/stash";
    private const string DbLocaleNamesPath = "/webui/db/localeNames";
    private const string RailNeighborsPath = "/webui/rail/neighbors";
    private const string LockPath = "/webui/lock";
    private const string ConfigListPath = "/webui/config";
    private const string ConfigUiPath = "/webui/config/ui";
    private const string DevPagePath = "/webui/dev/routes";
    private const string DevRoutesJsonPath = "/webui/dev/routesJson";
    private const string LoadOrderPagePath = "/webui/dev/loadorder";
    private const string LoadOrderJsonPath = "/webui/dev/loadorderJson";

    // Non-Leaves mods that also ship a web UI, for the rail to link out to. Detected by folder presence (see ServeNeighbors), since RaidReview's separate http port can't be probed from an https page anyway.
    private static readonly NeighborDef[] Neighbors =
    {
        new NeighborDef("acidphantasm-botplacementsystem", "Bot Placement", "🤖", "/botplacementsystem/", false, 0),
        new NeighborDef("acidphantasm-progressivebotsystem", "Progressive Bots", "📈", "/progressivebotsystem/", false, 0),
        new NeighborDef("RaidReview", "Raid Review", "🎬", "/", true, 7829),
    };

    // The static UI files this host used to serve (rail.txt, base.css, itembrowser.html,
    // config-editor.html, dev.html, loadorder.html) moved to the Leaves-WebUI mod - so there is no
    // assets/ui/ folder here any more. The compute endpoints below build their responses in code.
    //   private readonly string uiDir = System.IO.Path.Combine(
    //       modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly()),
    //       "assets", "ui");

    // Built once on first request; the database is static after load. Locked because SPT dispatches requests concurrently.
    private readonly object cacheLock = new();
    private string? cachedIndexJson;
    private Dictionary<string, string>? cachedLocale;
    private HashSet<string>? cachedVanillaIds;
    private string? cachedLoadOrderJson;
    private string? cachedNodeTreeJson;
    private string? cachedLocaleNamesJson;
    private string? cachedTradersJson;

    // Static page/asset paths now served by the Leaves-WebUI mod. Excluded here so exactly one listener
    // claims each (dispatch order between listeners is not guaranteed). Everything else under /webui/ -
    // the /webui/db/* data feeds, /webui/dev/*Json, /webui/config, /webui/rail/neighbors, /webui/lock -
    // is still ours. See WEBUI_EXTRACTION_PLAN.md.
    private static readonly HashSet<string> MovedToWebUiMod = new(StringComparer.OrdinalIgnoreCase)
    {
        "/webui/rail.js",
        "/webui/base.css",
        "/webui/db",
        "/webui/config/ui",
        "/webui/dev/routes",
        "/webui/dev/loadorder",
    };

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        if (!HttpUtils.HasPrefix(context, Prefix))
        {
            return false;
        }
        string path = (context.Request.Path.Value ?? "").TrimEnd('/');
        return !MovedToWebUiMod.Contains(path);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        // --- Static file serves moved to the Leaves-WebUI mod (see WEBUI_EXTRACTION_PLAN.md). Kept
        //     commented rather than deleted; CanHandle already routes these paths elsewhere. ---
        //   if (method == "GET" && path.EndsWith("/rail.js", ...))                 -> shared/rail.txt
        //   else if (method == "GET" && path.EndsWith("/base.css", ...))           -> shared/base.css
        //   else if (method == "GET" && path.Equals(DbPagePath, ...))              -> shared/itembrowser.html
        //   else if (method == "GET" && path.Equals(ConfigUiPath, ...))            -> shared/config-editor.html
        //   else if (method == "GET" && path.Equals(DevPagePath, ...))             -> shared/dev.html
        //   else if (method == "GET" && path.Equals(LoadOrderPagePath, ...))       -> shared/loadorder.html
        if (method == "GET" && path.Equals(RailNeighborsPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeNeighbors(context);
        }
        else if (method == "GET" && path.Equals(LockPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeLock(context);
        }
        else if (method == "GET" && path.Equals(ConfigListPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeConfigList(context);
        }
        // config-editor.html / dev.html static serves moved to the Leaves-WebUI mod.
        //   else if (method == "GET" && path.Equals(ConfigUiPath, ...)) -> shared/config-editor.html
        //   else if (method == "GET" && path.Equals(DevPagePath, ...))  -> shared/dev.html
        else if (method == "GET" && path.Equals(DevRoutesJsonPath, StringComparison.OrdinalIgnoreCase))
        {
            // The suite-wide route directory: every route each installed mod registered into the shared Utils singleton.
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(Utils.Instance.GetRegisteredRoutes(), false) ?? "[]");
        }
        // loadorder.html static serve moved to the Leaves-WebUI mod.
        //   else if (method == "GET" && path.Equals(LoadOrderPagePath, ...)) -> shared/loadorder.html
        else if (method == "GET" && path.Equals(LoadOrderJsonPath, StringComparison.OrdinalIgnoreCase))
        {
            // Every IOnLoad component the server will run at boot, in the order it runs them.
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, GetLoadOrderJson());
        }
        else if (method == "GET" && path.Equals(DbIndexPath, StringComparison.OrdinalIgnoreCase))
        {
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, GetIndexJson());
        }
        else if (method == "GET" && path.Equals(DbQueryPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeQuery(context);
        }
        else if (method == "GET" && path.Equals(DbNodesPath, StringComparison.OrdinalIgnoreCase))
        {
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, GetNodeTreeJson());
        }
        else if (method == "GET" && path.Equals(DbTradersPath, StringComparison.OrdinalIgnoreCase))
        {
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, GetTradersJson());
        }
        else if (method == "GET" && path.Equals(DbProfilesPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeProfiles(context);
        }
        else if (method == "GET" && path.Equals(DbLocaleNamesPath, StringComparison.OrdinalIgnoreCase))
        {
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, GetLocaleNamesJson());
        }
        else if (method == "GET" && path.Equals(DbStashPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeStash(context);
        }
        else if (method == "GET" && path.StartsWith(DbItemPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // The rest of the path is the item id; used only as a dict key below, never a file path, so no shape check needed.
            string rawId = path.Substring(DbItemPrefix.Length);
            string id = Uri.UnescapeDataString(rawId);
            await ServeItemDetail(context, id);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"Unknown WebUI resource: {path}");
        }
    }

    // The "en" locale table, cached; GetLocaleDb re-parses the whole file on every call otherwise.
    private Dictionary<string, string> GetLocale()
    {
        lock (cacheLock)
        {
            cachedLocale ??= localeService.GetLocaleDb("en");
            return cachedLocale;
        }
    }

    // GET /webui/db/localeNames -> a flat { "<id>": "<displayName>" } of every locale "<id> Name"
    // key (suffix stripped) plus every trader's nickname. This is the live, post-mod name table -
    // WTT items, cloned meds, modded traders all included - so an offline tool (ConfigSync) can
    // resolve the same id labels CommentedJson's defaultIdLabel would, without a running server.
    // Locale wins over a trader nickname on the rare id that has both, matching defaultIdLabel.
    // Built once and cached; the database is static after load.
    private string GetLocaleNamesJson()
    {
        lock (cacheLock)
        {
            if (cachedLocaleNamesJson != null)
            {
                return cachedLocaleNamesJson;
            }

            const string suffix = " Name";
            var names = new Dictionary<string, string>();

            foreach (var pair in GetLocale())
            {
                if (!pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(pair.Value))
                {
                    continue;
                }
                string id = pair.Key.Substring(0, pair.Key.Length - suffix.Length);
                if (id.Length == 0)
                {
                    continue;
                }
                names[id] = pair.Value;
            }

            foreach (var pair in databaseService.GetTraders())
            {
                string id = pair.Key.ToString();
                // Prefer the locale "<id> Nickname" (the shown name: "Ref", "Lightkeeper", "BTR
                // Driver") over Base.Nickname, which carries the wrong internal value for several
                // traders ("Arena", "caretaker", Cyrillic "БТР").
                string nickname = TraderDisplayName(id, pair.Value.Base?.Nickname, GetLocale());
                if (string.IsNullOrEmpty(nickname))
                {
                    continue;
                }
                if (!names.ContainsKey(id))
                {
                    names[id] = nickname;
                }
            }

            cachedLocaleNamesJson = jsonUtil.Serialize(names, false) ?? "{}";
            return cachedLocaleNamesJson;
        }
    }

    // Item ids from the on-disk vanilla DB (SPT_Data), read once: a live id absent here is a modded item (modded ids share the vanilla shape, so this is the only reliable tell).
    // Null when the file's missing, and callers then treat everything as vanilla rather than mislabel it all modded.
    private HashSet<string>? GetVanillaIds()
    {
        lock (cacheLock)
        {
            if (cachedVanillaIds != null)
            {
                return cachedVanillaIds.Count > 0 ? cachedVanillaIds : null;
            }

            cachedVanillaIds = new HashSet<string>();
            // three parents up from the mod folder is the SPT root holding SPT_Data
            string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
            string? root = System.IO.Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
            string itemsFile = System.IO.Path.Combine(root ?? "", "SPT_Data", "database", "templates", "items.json");
            if (System.IO.File.Exists(itemsFile))
            {
                using var items = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(itemsFile));
                foreach (var item in items.RootElement.EnumerateObject())
                {
                    cachedVanillaIds.Add(item.Name);
                }
            }
            return cachedVanillaIds.Count > 0 ? cachedVanillaIds : null;
        }
    }

    // Every IOnLoad component the server runs at boot, in run order, as JSON.
    // Built once (the set of loaded types is fixed after startup) and cached.
    //
    // We discover them by REFLECTION rather than by injecting IEnumerable<IOnLoad>: this class is itself an IOnLoad, so taking that collection in our constructor would be a circular dependency (resolving us needs us).
    // Reflection also constructs nothing, so a component's constructor can't run a second time at request time.
    // The DI container decides run order purely by Injectable.TypePriority ascending (DependencyInjectionHandler sorts by it), so replicating that sort reproduces the real order.
    // Ties (two components at the same priority) have no guaranteed order at runtime either - they simply sort adjacent here, which is exactly the ambiguity worth seeing.
    //
    // Caveat: this lists every DI-registered IOnLoad type present; it can't see a type dropped by another mod's [Injectable(TypeOverride=...)], and it reflects IOnLoad ORDER, which equals a mod's real effect order only when that mod does its work inside OnLoad (code run outside the IOnLoad pass is invisible).
    private string GetLoadOrderJson()
    {
        lock (cacheLock)
        {
            if (cachedLoadOrderJson != null)
            {
                return cachedLoadOrderJson;
            }

            var entries = new List<LoadOrderEntry>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // A partially loadable assembly still yields the types it could load; the rest are null.
                    types = ex.Types;
                }
                catch (Exception)
                {
                    // Anything else (dynamic assembly, access issue): skip it entirely.
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || type.IsInterface)
                    {
                        continue;
                    }
                    if (!typeof(IOnLoad).IsAssignableFrom(type))
                    {
                        continue;
                    }
                    // No [Injectable] means the DI container never registers or runs it, so leave it out.
                    var injectable = type.GetCustomAttribute<Injectable>();
                    if (injectable == null)
                    {
                        continue;
                    }

                    int priority = injectable.TypePriority;
                    entries.Add(new LoadOrderEntry
                    {
                        Priority = priority,
                        PriorityLabel = priority == int.MaxValue ? "int.MaxValue" : priority.ToString(),
                        Stage = StageLabel(priority),
                        Class = type.FullName ?? type.Name,
                        Assembly = assembly.GetName().Name ?? "?",
                        Injection = injectable.InjectionType.ToString(),
                    });
                }
            }

            entries.Sort(CompareLoadOrder);
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Index = i + 1;
            }

            cachedLoadOrderJson = jsonUtil.Serialize(entries, false) ?? "[]";
            return cachedLoadOrderJson;
        }
    }

    // Sort by run order: ascending TypePriority (what the DI container uses), then class name so the display is stable within a shared priority.
    private static int CompareLoadOrder(LoadOrderEntry a, LoadOrderEntry b)
    {
        if (a.Priority != b.Priority)
        {
            return a.Priority.CompareTo(b.Priority);
        }
        return string.CompareOrdinal(a.Class, b.Class);
    }

    // Name the OnLoadOrder band a priority falls in, plus its offset from that band's base (e.g. "PostDBModLoader +90000").
    private static string StageLabel(int priority)
    {
        if (priority == int.MaxValue)
        {
            return "Unspecified (runs last)";
        }
        if (priority >= OnLoadOrder.PostSptModLoader) { return Band("PostSptModLoader", priority, OnLoadOrder.PostSptModLoader); }
        if (priority >= OnLoadOrder.RagfairCallbacks) { return Band("RagfairCallbacks", priority, OnLoadOrder.RagfairCallbacks); }
        if (priority >= OnLoadOrder.PresetCallbacks) { return Band("PresetCallbacks", priority, OnLoadOrder.PresetCallbacks); }
        if (priority >= OnLoadOrder.TraderCallbacks) { return Band("TraderCallbacks", priority, OnLoadOrder.TraderCallbacks); }
        if (priority >= OnLoadOrder.SaveCallbacks) { return Band("SaveCallbacks", priority, OnLoadOrder.SaveCallbacks); }
        if (priority >= OnLoadOrder.HandbookCallbacks) { return Band("HandbookCallbacks", priority, OnLoadOrder.HandbookCallbacks); }
        if (priority >= OnLoadOrder.TraderRegistration) { return Band("TraderRegistration", priority, OnLoadOrder.TraderRegistration); }
        if (priority >= OnLoadOrder.PostDBModLoader) { return Band("PostDBModLoader", priority, OnLoadOrder.PostDBModLoader); }
        if (priority >= OnLoadOrder.GameCallbacks) { return Band("GameCallbacks", priority, OnLoadOrder.GameCallbacks); }
        if (priority >= OnLoadOrder.Database) { return Band("Database", priority, OnLoadOrder.Database); }
        if (priority >= OnLoadOrder.PreSptModLoader) { return Band("PreSptModLoader", priority, OnLoadOrder.PreSptModLoader); }
        if (priority >= OnLoadOrder.Watermark) { return Band("Watermark", priority, OnLoadOrder.Watermark); }
        return "Pre-Watermark (" + priority + ")";
    }

    private static string Band(string name, int priority, int band)
    {
        int offset = priority - band;
        if (offset == 0)
        {
            return name;
        }
        return name + " +" + offset;
    }

    // The search index the page loads up front, one row per item (id, name, short, type, parent).
    // Nodes are included so the client can walk the parent chain; only Item rows are offered as search results.
    private string GetIndexJson()
    {
        lock (cacheLock)
        {
            if (cachedIndexJson != null)
            {
                return cachedIndexJson;
            }

            var locale = GetLocale();
            var itemDb = databaseService.GetItems();
            var entries = new List<ItemIndexEntry>(itemDb.Count);

            foreach (var pair in itemDb)
            {
                var template = pair.Value;
                string id = pair.Key.ToString();

                string name;
                string shortName;
                if (locale.TryGetValue(id + " Name", out var localeName) && localeName.Length > 0)
                {
                    name = localeName;
                }
                else
                {
                    name = template.Name ?? id;
                }
                locale.TryGetValue(id + " ShortName", out var shortNameValue);
                shortName = shortNameValue ?? "";

                var entry = new ItemIndexEntry
                {
                    Id = id,
                    Name = name,
                    Short = shortName,
                    Type = template.Type ?? "",
                    Parent = template.Parent.ToString(),
                };
                entries.Add(entry);
            }

            cachedIndexJson = jsonUtil.Serialize(entries, false) ?? "[]";
            return cachedIndexJson;
        }
    }

    // The full detail blob for one item: raw template (game _id/_name field names) plus its locale and handbook entries. 404s for an unknown id.
    private async Task ServeItemDetail(HttpContext context, string id)
    {
        var itemDb = databaseService.GetItems();
        // converting a non-MongoId string throws, so screen the shape first and 404
        if (!IsMongoIdShape(id) || !itemDb.TryGetValue(id, out var template))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"No item with id {id}");
            return;
        }

        var locale = GetLocale();
        var localeBlock = new LocaleBlock();
        if (locale.TryGetValue(id + " Name", out var lName))
        {
            localeBlock.Name = lName;
        }
        if (locale.TryGetValue(id + " ShortName", out var lShort))
        {
            localeBlock.ShortName = lShort;
        }
        if (locale.TryGetValue(id + " Description", out var lDesc))
        {
            localeBlock.Description = lDesc;
        }

        HandbookBlock? handbookBlock = null;
        var handbook = databaseService.GetHandbook().Items;
        foreach (var hb in handbook)
        {
            if (hb.Id == id)
            {
                handbookBlock = new HandbookBlock
                {
                    ParentId = hb.ParentId.ToString(),
                    Price = hb.Price,
                };
                break;
            }
        }

        // modded = not in the vanilla snapshot; drives the tarkov.dev link (vanilla only) and the "modded" marker
        var vanillaIds = GetVanillaIds();
        bool modded = vanillaIds != null && !vanillaIds.Contains(id);

        var detail = new ItemDetail
        {
            Item = template,
            Locale = localeBlock,
            Handbook = handbookBlock,
            Modded = modded,
        };

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(detail, true) ?? "{}");
    }

    // General item query for any mod page: GET /webui/db/query?parent=&name=&desc=&type=&limit= (name/desc are case-insensitive regex, all filters AND, default limit 500).
    // Returns the same compact rows as the index. Regex has a 1s timeout (a bad pattern can't hang the request); an unparseable one is a 400.
    private async Task ServeQuery(HttpContext context)
    {
        var query = context.Request.Query;
        string parent = query["parent"].ToString();
        string namePattern = query["name"].ToString();
        string descPattern = query["desc"].ToString();
        string type = query["type"].ToString();

        int limit = 500;
        if (int.TryParse(query["limit"].ToString(), out int parsedLimit) && parsedLimit > 0)
        {
            limit = parsedLimit;
        }

        Regex? nameRegex = null;
        Regex? descRegex = null;
        try
        {
            if (namePattern.Length > 0)
            {
                nameRegex = new Regex(namePattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            if (descPattern.Length > 0)
            {
                descRegex = new Regex(descPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
        }
        catch (ArgumentException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"Bad regex: {ex.Message}");
            return;
        }

        var locale = GetLocale();
        var itemDb = databaseService.GetItems();
        var results = new List<ItemIndexEntry>();

        try
        {
            foreach (var pair in itemDb)
            {
                var template = pair.Value;
                string id = pair.Key.ToString();

                if (type.Length > 0 && (template.Type ?? "") != type)
                {
                    continue;
                }
                if (parent.Length > 0 && template.Parent.ToString() != parent)
                {
                    continue;
                }

                string name;
                if (locale.TryGetValue(id + " Name", out var localeName) && localeName.Length > 0)
                {
                    name = localeName;
                }
                else
                {
                    name = template.Name ?? id;
                }

                if (nameRegex != null && !nameRegex.IsMatch(name))
                {
                    continue;
                }
                if (descRegex != null)
                {
                    locale.TryGetValue(id + " Description", out var description);
                    if (description == null || !descRegex.IsMatch(description))
                    {
                        continue;
                    }
                }

                locale.TryGetValue(id + " ShortName", out var shortNameValue);
                results.Add(new ItemIndexEntry
                {
                    Id = id,
                    Name = name,
                    Short = shortNameValue ?? "",
                    Type = template.Type ?? "",
                    Parent = template.Parent.ToString(),
                });

                if (results.Count >= limit)
                {
                    break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Regex match timed out - simplify the pattern.");
            return;
        }

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(results, false) ?? "[]");
    }

    // One node of /webui/db/nodes: a category in the item tree (Type == "Node"), never a real item.
    // directItems = real items whose parent is exactly this node; totalItems = real items anywhere in this subtree.
    private class NodeTreeEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("short")] public string Short { get; set; } = "";
        [JsonPropertyName("parent")] public string Parent { get; set; } = "";
        [JsonPropertyName("depth")] public int Depth { get; set; }
        [JsonPropertyName("directItems")] public int DirectItems { get; set; }
        [JsonPropertyName("totalItems")] public int TotalItems { get; set; }
        [JsonPropertyName("children")] public List<NodeTreeEntry> Children { get; set; } = new();
    }

    // GET /webui/db/nodes -> the category skeleton only: every Type == "Node" template, nested from the single root ("Item"). No leaf items.
    // Built once and cached; the item DB is static after load.
    private string GetNodeTreeJson()
    {
        lock (cacheLock)
        {
            if (cachedNodeTreeJson != null)
            {
                return cachedNodeTreeJson;
            }

            var locale = GetLocale();
            var itemDb = databaseService.GetItems();

            var nodesById = new Dictionary<string, NodeTreeEntry>();
            var directItemCount = new Dictionary<string, int>();

            foreach (var pair in itemDb)
            {
                var template = pair.Value;
                string id = pair.Key.ToString();
                string parent = template.Parent.ToString();

                if ((template.Type ?? "") == "Node")
                {
                    string name;
                    if (locale.TryGetValue(id + " Name", out var localeName) && localeName.Length > 0)
                    {
                        name = localeName;
                    }
                    else
                    {
                        name = template.Name ?? id;
                    }
                    locale.TryGetValue(id + " ShortName", out var shortValue);

                    nodesById[id] = new NodeTreeEntry
                    {
                        Id = id,
                        Name = name,
                        Short = shortValue ?? "",
                        Parent = parent,
                    };
                }
                else if (directItemCount.TryGetValue(parent, out int existing))
                {
                    directItemCount[parent] = existing + 1;
                }
                else
                {
                    directItemCount[parent] = 1;
                }
            }

            var childNodeIds = new Dictionary<string, List<string>>();
            string? rootId = null;
            foreach (NodeTreeEntry entry in nodesById.Values)
            {
                directItemCount.TryGetValue(entry.Id, out int direct);
                entry.DirectItems = direct;

                if (nodesById.ContainsKey(entry.Parent))
                {
                    if (!childNodeIds.TryGetValue(entry.Parent, out var kids))
                    {
                        kids = new List<string>();
                        childNodeIds[entry.Parent] = kids;
                    }
                    kids.Add(entry.Id);
                }
                else
                {
                    // parent isn't itself a node, so this is the tree root
                    rootId = entry.Id;
                }
            }

            NodeTreeEntry? root = null;
            if (rootId != null)
            {
                root = BuildNodeSubtree(rootId, 0, nodesById, childNodeIds);
            }

            cachedNodeTreeJson = jsonUtil.Serialize(root, true) ?? "{}";
            return cachedNodeTreeJson;
        }
    }

    // Depth-first assembly of one node and everything under it; fills Depth, Children (sorted by name), and TotalItems (own DirectItems plus every descendant's).
    private static NodeTreeEntry BuildNodeSubtree(
        string nodeId,
        int depth,
        Dictionary<string, NodeTreeEntry> nodesById,
        Dictionary<string, List<string>> childNodeIds
    )
    {
        NodeTreeEntry node = nodesById[nodeId];
        node.Depth = depth;

        int total = node.DirectItems;
        if (childNodeIds.TryGetValue(nodeId, out var kids))
        {
            kids.Sort((string a, string b) =>
            {
                return string.CompareOrdinal(nodesById[a].Name, nodesById[b].Name);
            });
            foreach (string kidId in kids)
            {
                NodeTreeEntry child = BuildNodeSubtree(kidId, depth + 1, nodesById, childNodeIds);
                node.Children.Add(child);
                total += child.TotalItems;
            }
        }
        node.TotalItems = total;
        return node;
    }

    // A trader's shown name: the locale "<id> Nickname" when present and non-empty, else Base.Nickname.
    // Base.Nickname is an internal value for several traders (Ref = "Arena", Lightkeeper = "caretaker",
    // BTR = Cyrillic "БТР"); the locale nickname is what the game actually shows. A modded/newer trader
    // with no locale key (e.g. Storyteller) still resolves via its Base.Nickname.
    private static string TraderDisplayName(string id, string? baseNickname, Dictionary<string, string> locale)
    {
        if (locale.TryGetValue(id + " Nickname", out var localeNick) && !string.IsNullOrEmpty(localeNick))
        {
            return localeNick;
        }
        return baseNickname ?? "";
    }

    // GET /webui/db/traders -> [{ id, nickname }], the trader-picker feed for [AllowedIds(Trader)]
    // fields in the config editor. Every trader with a nickname; sorted by nickname for a stable list.
    // Built once and cached; the trader table is static after load.
    private string GetTradersJson()
    {
        lock (cacheLock)
        {
            if (cachedTradersJson != null)
            {
                return cachedTradersJson;
            }

            var locale = GetLocale();
            var traders = new List<TraderEntry>();
            foreach (var pair in databaseService.GetTraders())
            {
                string id = pair.Key.ToString();
                string nickname = TraderDisplayName(id, pair.Value.Base?.Nickname, locale);
                if (string.IsNullOrEmpty(nickname))
                {
                    continue;
                }
                traders.Add(new TraderEntry
                {
                    Id = id,
                    Nickname = nickname,
                });
            }

            traders.Sort((TraderEntry a, TraderEntry b) =>
            {
                return string.CompareOrdinal(a.Nickname, b.Nickname);
            });

            cachedTradersJson = jsonUtil.Serialize(traders, false) ?? "[]";
            return cachedTradersJson;
        }
    }

    // GET /webui/db/profiles -> [{ id, nickname, level }], the profile-picker feed for any web UI page.
    // Skips profiles with no nickname (not yet created) and utility/alt accounts hit by the shared prefix filter.
    private async Task ServeProfiles(HttpContext context)
    {
        var profiles = new List<ProfileEntry>();
        foreach (var profileId in saveServer.GetProfiles().Keys)
        {
            var pmc = profileHelper.GetPmcProfile(profileId);
            string? nickname = pmc?.Info?.Nickname;
            if (nickname == null || nickname.Length == 0)
            {
                continue; // incomplete / not-yet-created profile
            }
            if (Utils.Instance.ShouldHideProfileFromWebUi(nickname))
            {
                continue; // utility/alt account, hidden by the shared filter
            }
            profiles.Add(new ProfileEntry
            {
                Id = profileId.ToString(),
                Nickname = nickname,
                Level = pmc?.Info?.Level ?? 0,
            });
        }
        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(profiles, false) ?? "[]");
    }

    // GET /webui/db/stash?profileId=<id> -> { profileId, nickname, items: { <tpl>: <totalCount> } }, everything in the stash summed by template id.
    // "Stash" means every item reachable by parent links from the stash root, so nested containers (item cases, a rig kept there) are included; equipment, pockets, and the secure container are not.
    private async Task ServeStash(HttpContext context)
    {
        string profileId = context.Request.Query["profileId"].ToString();
        if (profileId.Length == 0 || !IsMongoIdShape(profileId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("A valid profileId query parameter is required.");
            return;
        }

        var pmc = profileHelper.GetPmcProfile(new MongoId(profileId));
        string? nickname = pmc?.Info?.Nickname;
        if (nickname == null || nickname.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync($"No profile with id {profileId}");
            return;
        }

        var response = new StashResponse
        {
            ProfileId = profileId,
            Nickname = nickname,
            Items = new Dictionary<string, int>(),
        };

        var items = pmc?.Inventory?.Items;
        MongoId? stashId = pmc?.Inventory?.Stash;
        if (items != null && stashId.HasValue)
        {
            // index children by parent once, then walk breadth-first from the stash root so nested containers are picked up for free
            var childrenByParent = new Dictionary<string, List<Item>>();
            foreach (var item in items)
            {
                string parent = item.ParentId ?? "";
                if (parent.Length == 0)
                {
                    continue;
                }
                if (!childrenByParent.TryGetValue(parent, out var siblings))
                {
                    siblings = new List<Item>();
                    childrenByParent[parent] = siblings;
                }
                siblings.Add(item);
            }

            var queue = new Queue<string>();
            queue.Enqueue(stashId.Value.ToString());
            while (queue.Count > 0)
            {
                string parentId = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parentId, out var children))
                {
                    continue;
                }
                foreach (var child in children)
                {
                    string tpl = child.Template.ToString();
                    int count = (int)(child.Upd?.StackObjectsCount ?? 1);
                    if (response.Items.TryGetValue(tpl, out int existing))
                    {
                        response.Items[tpl] = existing + count;
                    }
                    else
                    {
                        response.Items[tpl] = count;
                    }
                    // descend in case this item is itself a container holding more items
                    queue.Enqueue(child.Id.ToString());
                }
            }
        }

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(response, false) ?? "{}");
    }

    // GET /webui/config -> one row per mod that registered a Kind="config" GET into the shared
    // route directory, carrying that GET's path and (when present) its Kind="config" POST path.
    // The rail turns each row into a cog linking to /webui/config/ui?mod=<name>.
    private async Task ServeConfigList(HttpContext context)
    {
        var byMod = new Dictionary<string, ConfigModEntry>();
        foreach (RouteInfo route in Utils.Instance.GetRegisteredRoutes())
        {
            if (route.Kind != "config")
            {
                continue;
            }

            if (!byMod.TryGetValue(route.Mod, out ConfigModEntry? entry))
            {
                entry = new ConfigModEntry { Mod = route.Mod };
                byMod[route.Mod] = entry;
            }

            if (route.Method == "GET")
            {
                entry.Get = route.Path;
            }
            else if (route.Method == "POST")
            {
                entry.Save = route.Path;
            }
        }

        var list = new List<ConfigModEntry>();
        foreach (ConfigModEntry entry in byMod.Values)
        {
            // A POST with no GET isn't a usable surface - nothing to load into the form.
            if (entry.Get != null)
            {
                list.Add(entry);
            }
        }

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(list, false) ?? "[]");
    }

    // GET /webui/rail/neighbors -> the installed non-Leaves mod UIs, for the rail to link out to.
    // Same-origin mods get a relative url; an external mod (RaidReview) gets an absolute http://host:port/ built from the request host and its port. A mod we can't inspect is just left out.
    private async Task ServeNeighbors(HttpContext context)
    {
        // parent of the mod folder is user\mods, holding every mod
        string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
        string? modsDir = System.IO.Directory.GetParent(modPath)?.FullName;

        var found = new List<NeighborEntry>();
        if (modsDir != null)
        {
            string requestHost = context.Request.Host.Host;
            foreach (var def in Neighbors)
            {
                string folder = System.IO.Path.Combine(modsDir, def.Folder);
                if (!System.IO.Directory.Exists(folder))
                {
                    continue;
                }

                string url;
                if (def.External)
                {
                    int port = ReadExternalPort(folder, def.DefaultPort);
                    url = "http://" + requestHost + ":" + port + def.Path;
                }
                else
                {
                    url = def.Path;
                }

                found.Add(new NeighborEntry
                {
                    Name = def.Name,
                    Icon = def.Icon,
                    Url = url,
                    External = def.External,
                });
            }
        }

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(found, false) ?? "[]");
    }

    // GET /webui/lock -> LockInfo. The rail's padlock verifies the typed password against this hash entirely client-side.
    // Read fresh (not cached) so editing WebUI_LockPassword takes effect without a server restart.
    private async Task ServeLock(HttpContext context)
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<LeavesUtilsConfig>(modPath, "config/config.jsonc");
        string password = config.WebUiLockPassword ?? "";

        var info = new LockInfo();
        if (password.Length > 0)
        {
            byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            var hex = new System.Text.StringBuilder(digest.Length * 2);
            foreach (byte b in digest)
            {
                hex.Append(b.ToString("x2"));
            }
            info.Enabled = true;
            info.Hash = hex.ToString();
        }

        await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(info, false) ?? "{}");
    }

    // Reads RaidReview's live http port from its config/config.json ("web_client_port"), falling back to the default if unreadable.
    private static int ReadExternalPort(string modFolder, int fallback)
    {
        try
        {
            string configFile = System.IO.Path.Combine(modFolder, "config", "config.json");
            if (System.IO.File.Exists(configFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(configFile));
                if (doc.RootElement.TryGetProperty("web_client_port", out var portElement)
                    && portElement.TryGetInt32(out int port)
                    && port > 0
                )
                {
                    return port;
                }
            }
        }
        catch (Exception)
        {
            // malformed or locked config: fall through to the default port below
        }
        return fallback;
    }

    // 24 hex chars; item ids are always this shape, so reject anything else before touching the MongoId-keyed dictionary.
    private static bool IsMongoIdShape(string id)
    {
        if (id.Length != 24)
        {
            return false;
        }
        foreach (char c in id)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }
}

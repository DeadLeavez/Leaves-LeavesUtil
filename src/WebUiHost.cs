using System.Reflection;
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
            new RouteInfo("GET", "/webui/rail.js", "Suite-wide nav-rail client script (served from rail.txt).", "internal"),
            new RouteInfo("GET", "/webui/base.css", "Suite-wide shared stylesheet linked by every mod page.", "internal"),
            new RouteInfo("GET", "/webui/db", "Item Browser page.", "ui"),
            new RouteInfo("GET", "/webui/dev/routes", "Dev page: this route directory.", "ui"),
            new RouteInfo("GET", "/webui/dev/routesJson", "Every route each installed mod registered, as JSON.", "api"),
            new RouteInfo("GET", "/webui/dev/loadorder", "Dev page: IOnLoad run order across all mods + core.", "ui"),
            new RouteInfo("GET", "/webui/dev/loadorderJson", "Every IOnLoad component and its Injectable priority, in run order, as JSON.", "api"),
            new RouteInfo("GET", "/webui/rail/neighbors", "Installed non-Leaves mod UIs for the rail to link out to.", "api"),
            new RouteInfo("GET", "/webui/db/index", "Full item search index (one compact row per item).", "api"),
            new RouteInfo("GET", "/webui/db/query?parent=&name=&desc=&type=&limit=", "Item query; name/desc are case-insensitive regex, all filters AND.", "api"),
            new RouteInfo("GET", "/webui/db/profiles", "Profile-picker feed [{ id, nickname, level }].", "api"),
            new RouteInfo("GET", "/webui/db/stash?profileId=", "A profile's stash contents summed by template id.", "api"),
            new RouteInfo("GET", "/webui/db/item/{id}", "Full detail blob for one item (template + locale + handbook).", "api"),
        };
        Utils.Instance.RegisterRoutes("LeavesUtil (Web UI)", routes);
        return Task.CompletedTask;
    }

    private const string Prefix = "/webui/";
    private const string DbPagePath = "/webui/db";
    private const string DbIndexPath = "/webui/db/index";
    private const string DbQueryPath = "/webui/db/query";
    private const string DbItemPrefix = "/webui/db/item/";
    private const string DbProfilesPath = "/webui/db/profiles";
    private const string DbStashPath = "/webui/db/stash";
    private const string RailNeighborsPath = "/webui/rail/neighbors";
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

    // UI assets under assets/ui/, read fresh per request (rare) so tweaks to a deployed file show without a rebuild.
    private readonly string uiDir = System.IO.Path.Combine(
        modHelper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly()),
        "assets", "ui");

    // Built once on first request; the database is static after load. Locked because SPT dispatches requests concurrently.
    private readonly object cacheLock = new();
    private string? cachedIndexJson;
    private Dictionary<string, string>? cachedLocale;
    private HashSet<string>? cachedVanillaIds;
    private string? cachedLoadOrderJson;

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        return HttpUtils.HasPrefix(context, Prefix);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        if (method == "GET" && path.EndsWith("/rail.js", StringComparison.OrdinalIgnoreCase))
        {
            // Stored on disk as rail.txt, not rail.js: SPT's ModValidator rejects any mod folder containing a .js/.ts file as a legacy mod. The browser only cares about the Content-Type.
            await HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "rail.txt"), "application/javascript; charset=utf-8");
        }
        else if (method == "GET" && path.EndsWith("/base.css", StringComparison.OrdinalIgnoreCase))
        {
            // The suite-wide shared stylesheet, linked by every mod page.
            await HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "base.css"), "text/css; charset=utf-8");
        }
        else if (method == "GET" && (
            path.Equals(DbPagePath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(DbPagePath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "itembrowser.html"), "text/html; charset=utf-8");
        }
        else if (method == "GET" && path.Equals(RailNeighborsPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeNeighbors(context);
        }
        else if (method == "GET" && (
            path.Equals(DevPagePath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(DevPagePath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "dev.html"), "text/html; charset=utf-8");
        }
        else if (method == "GET" && path.Equals(DevRoutesJsonPath, StringComparison.OrdinalIgnoreCase))
        {
            // The suite-wide route directory: every route each installed mod registered into the shared Utils singleton.
            await HttpUtils.WriteJsonRaw(context, StatusCodes.Status200OK, jsonUtil.Serialize(Utils.Instance.GetRegisteredRoutes(), false) ?? "[]");
        }
        else if (method == "GET" && (
            path.Equals(LoadOrderPagePath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(LoadOrderPagePath + "/", StringComparison.OrdinalIgnoreCase)))
        {
            await HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "loadorder.html"), "text/html; charset=utf-8");
        }
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
        else if (method == "GET" && path.Equals(DbProfilesPath, StringComparison.OrdinalIgnoreCase))
        {
            await ServeProfiles(context);
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

    // Every IOnLoad component the server runs at boot, in run order, as JSON. Built once (the set of loaded
    // types is fixed after startup) and cached.
    //
    // We discover them by REFLECTION rather than by injecting IEnumerable<IOnLoad>: this class is itself an
    // IOnLoad, so taking that collection in our constructor would be a circular dependency (resolving us needs
    // us). Reflection also constructs nothing, so a component's constructor can't run a second time at request
    // time. The DI container decides run order purely by Injectable.TypePriority ascending
    // (DependencyInjectionHandler sorts by it), so replicating that sort reproduces the real order. Ties (two
    // components at the same priority) have no guaranteed order at runtime either - they simply sort adjacent
    // here, which is exactly the ambiguity worth seeing.
    //
    // Caveat: this lists every DI-registered IOnLoad type present; it can't see a type dropped by another
    // mod's [Injectable(TypeOverride=...)], and it reflects IOnLoad ORDER, which equals a mod's real effect
    // order only when that mod does its work inside OnLoad (code run outside the IOnLoad pass is invisible).
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

    // Sort by run order: ascending TypePriority (what the DI container uses), then class name so the display is
    // stable within a shared priority.
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
                Level = pmc.Info.Level ?? 0,
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

        var items = pmc.Inventory?.Items;
        MongoId? stashId = pmc.Inventory?.Stash;
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

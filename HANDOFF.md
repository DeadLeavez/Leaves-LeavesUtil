# Leaves-LeavesUtil — Handoff

Last updated: 2026-08-10. Read `4.0 Mods\HANDOFF.md` first for project-wide rules.

---

## 1. What this is

The shared utility library every other Leaves mod depends on (via `ProjectReference`). It is
not a gameplay mod, but it *is* a real SPT mod: it registers in DI, has an `OnLoad`, ships a
config, and deploys to `user/mods/Leaves-LeavesUtil` like the rest. Method names are
lowerCamelCase (`loadFile`, `getLocale`) — kept from the TS original on purpose so ported
call sites read the same; don't "fix" the casing.

During the 4.0 port it was slimmed down hard: Newtonsoft.Json and the old CommonUtils JSON
layer were removed (everything goes through SPT's own `JsonUtil` now — handles MongoId and
`.jsonc` comments natively), dead API was pruned, and version checking was reduced to SPT's
own official mod-dependency check. The **RTT_* helpers were deliberately all kept**, including
currently-unused ones — they're a public surface other people's mods may call.

## 2. Files

| File | Role |
|---|---|
| `src/Utils.cs` | The utility grab-bag every mod injects — class `Leaves.Utils` (§3). |
| `src/TierList.cs` | Item tier-list loading + tier math, stateless (§4). |
| `src/DataDump.cs` | Optional startup dump of the full item tree + quest list (§5). |
| `src/Config.cs` | One field: `dataDump` toggle. |
| `src/ModMetadata.cs` | SPT mod metadata. |
| `src/WebUiHost.cs` | The shared web UI's HTTP surface (§6) — nav rail script, shared stylesheet, item browser, and the general `/webui/db/query` item-query endpoint. Folded in from the former standalone `Leaves-WebUI` mod on 2026-08-10. |
| `assets/ui/rail.txt` | The cross-mod nav-rail client script, served at `/webui/rail.js` (stored as `.txt` so ModValidator doesn't reject the folder as a legacy JS mod). Also installs the suite-wide Ctrl+F / Esc search shortcuts. |
| `assets/ui/base.css` | Suite-wide shared stylesheet (palette tokens + universal primitives), served at `/webui/base.css`; every mod page links it first. |
| `assets/ui/itembrowser.html` | The Item Browser page, served at `/webui/db`. |
| `config/config.jsonc` | `"dataDump": false` — leave off unless needed; the dump walks every item in the game. |

## 3. `Utils` — the main class (namespace `Leaves`)

> **2026-08-10 rename:** namespace `LeavesUtils` → **`Leaves`**, class `LeavesUtils` → **`Utils`**,
> file `LeavesUtils.cs` → `Utils.cs`. So the type is now `Leaves.Utils` (was the doubled
> `LeavesUtils.LeavesUtils`), sibling types are `Leaves.TierList` / `Leaves.RTT_Colors` /
> `Leaves.JsonIdAnnotator`, and consumers do `using Leaves;`. The mod folder/DLL/`ModGuid` are
> unchanged. All 10 mods were updated + rebuilt in the same pass.

### 3.0 `Leaves.Utils.Instance` — the self-registered static handle
The constructor sets `Instance = this`, exposing a static `public static Utils Instance`. Because this
is a DI **Singleton** every mod injects, DI constructs it exactly once, *before* any mod's `OnLoad`
runs — so `Leaves.Utils.Instance` is safe to read from anywhere with **no injection and no threading**.
Set in the constructor on purpose: a field initializer can't reference `this`, and `OnLoad` runs too
late (PostSptModLoader). This replaced Questrandomizer's old per-mod `QR` facade; mods may still inject
`Leaves.Utils` the normal DI way (most do) — `Instance` is just an always-available alternative. The
primary constructor became a **classic constructor** to allow the `Instance = this` line.

### 3.1 Lifecycle (the subtle part)
`[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostSptModLoader)]` and it
implements `IOnLoad`. **Singleton is load-bearing**: the class is resolved both through the
IOnLoad collection *and* by plain constructor injection in every other mod — without
Singleton, DI creates two disconnected instances and the OnLoad clears a cache nobody reads
(and `Instance` could point at the wrong one).
(Project handoff §4.1 documents this pitfall generally; this class is the canonical example.)

Its `OnLoad` does exactly one thing: **clear `localeCache`**. Why: the cache populates lazily
via `LocaleService.GetLocaleDb`, which reads through whatever locale transformers are
registered *at that moment*. If mod A reads a locale during its OnLoad (LootFuckery does, for
item names) before mod B registers a transformer (TreasureBox's FakeVodkaFix), the stale
pre-transform dictionary would be locked in forever. The clear runs at the final stage, after
every mod's OnLoad, so the next read repopulates from a fully-transformed snapshot. Safe
because nothing in this codebase registers locale transformers outside OnLoad.

### 3.2 Why the locale cache exists at all
`LocaleService.GetLocaleDb` re-deserializes the **entire multi-megabyte locale file from disk
on every access** — no caching of its own. A single BarterEconomy trader update was doing that
thousands of times (one per item-name lookup). `getCachedLocaleDb` makes it a one-time cost
per language per process. `getLocale(locale, id, type)` is the public lookup on top.

### 3.3 API surface (grouped as in the file)
- **File I/O:** `loadFile<T>` / `saveJsonFile<T>` (SPT JsonUtil; MongoId-safe; `.jsonc` OK),
  `getFilesInFolder` (empty list, not throw, for not-yet-existing runtime folders),
  `appendFile` (creates parent dirs; SPT's FileUtil has no append), `writeTimestampedResult`
  (the `<folder>/<prefix>_<date>_<time>.json` dump convention; returns the path written).
  Design rule throughout: **`modPath` is always an explicit parameter, never instance state** —
  this class is shared by every mod, so any implicit "current mod folder" would resolve to
  whichever mod touched it last.
- **Item/id helpers:** `getOrCreateId` (caller owns the dictionary — same statelessness rule),
  `generateNewItemIds` (fresh MongoIds for an item list, rewiring ParentId references so
  attachment trees survive duplication), `getSlotFilter` / `getGridFilter` (positional access
  to Slots/Grids filter HashSets, returned live so callers can `.Add()` in place; formerly
  duplicated privately in TreasureBox and ShinyAirdropGuns).
- **World lookups:** `getLocationNames` (playable maps = locations with loose loot),
  `findLocationByCodeName` (matches on `Base.Id`, because `Locations.GetDictionary()` keys are
  C# property names like `Factory4Day`, not codenames like `factory4_day`),
  `getTraderNickname`.
- **Trader offers:** `addTrade(traderId|assort, itemTpl, cost, loyaltyLevel, [stack, unlimited])` — adds one
  purchasable offer (Items + BarterScheme + LoyalLevelItems), returns the new assort item id. `cost` is a
  `List<TradeEntry>` (`new TradeEntry(tpl, count)`), all required together = an AND barter. Convenience
  overloads take a single `(currency, price)` for a plain cash sale (Money.ROUBLES, USD/EUR, a GP-coin tpl,
  …). Used by GuildStash to stock closed boxes at Ref.
- **Profile quest status:** `getProfileQuestStatus` (a profile's `QuestStatusEnum?` for one quest,
  `null` when absent), `hasProfileStartedQuest` (true iff the player has *accepted* it — Started/
  AvailableForFinish/Success/Fail/FailRestartable/MarkedAsFailed/Expired; false for locked/available/
  absent). Backed by injected `ProfileHelper`. Used by Questrandomizer's outline purge.
- **Logging/debug:** `printColor` (respects the `debug` flag), `debugJsonOutput`,
  `searchObject` (self-described "hackiest shit ever" — recursive value search for debugging).
- **Random/math:** `getUniqueValues`, `getUniqueWeightedValues`, `generateValueAdjustmentInt`
  (the `[down, up]` factor-range pattern configs use).
- **RTT_* (rich text):** Unity rich-text wrappers used in quest/trade text — `RTT_Color`,
  `RTT_Bold`, `RTT_Size`, `RTT_Rainbowify` (per-character HSV walk), `RTT_TFify`, etc., plus
  the `RTT_Align`/`RTT_Colors` constant classes and `HSVtoRGB`. All kept on purpose.

## 4. `TierList`

Buckets items into numeric tiers (used by Questrandomizer handovers/rewards, BarterEconomy).
**Stateless by design** — it used to cache the loaded tier list as instance fields set by a
`setTierList()` call, which meant whichever mod called it last silently overwrote the tier
data every other mod was reading (including at runtime). Now every mod loads and owns its own
dictionary via `loadTierList(file)` and passes it into `getClosestTier` /
`getRandomItemFromTier` / `getTierFromID` explicitly. Don't reintroduce instance state here.

## 5. `DataDump`

Config-gated (`dataDump: false` by default). Runs at `PostSptModLoader + 1` — deliberately
*after* LeavesUtils' own OnLoad so the locale cache is already fully-transformed and item
names in the dump are accurate. Writes hand-formatted annotated text (not valid JSON) of the
full item class tree and quest list to its `debug/` folder; progress is printed with a
carriage-return trick straight to the console (the async SPT logger would race the
overwrite). Slow by nature; it exists for eyeballing item class hierarchies when building
data files (weapon categories, blacklists).

## 6. `WebUiHost` — the shared web UI (`/webui/`)

An `[Injectable] IHttpListener` that owns every `/webui/` URL. Folded in from the former standalone
`Leaves-WebUI` mod on 2026-08-10 — since every mod already `ProjectReference`s LeavesUtils, hosting it
here adds no *new* dependency, and the suite is always installed together. The `/webui/` URL prefix was
kept unchanged, so no mod page needed relinking.

Serves four things, all read fresh from `assets/ui/` per request (rarely hit):
- `/webui/rail.js` ← `rail.txt` — the nav rail + the Ctrl+F / Esc search-box shortcuts (focus the page's
  search box on Ctrl+F; if a page has none it doesn't `preventDefault`, so native Find still works).
- `/webui/base.css` ← `base.css` — the shared stylesheet. Palette **design tokens** + only the rules that
  were byte-identical across pages (body baseline, `.muted`, `mark`, base link colour). Deliberately NOT
  any spacing/layout — that genuinely differs per page and stays in each page's own CSS. Linked first, so
  page CSS still wins. Pages still carry original hex; migrating them to `var(--token)` is a safe follow-up.
- `/webui/db` ← `itembrowser.html` — the Item Browser page.
- `/webui/db/index` and `/webui/db/item/{id}` — its data endpoints (compact item index; per-item detail).
  `/webui/db/item/<MONGOID>` doubles as a handy dev lookup: while the server is running, hit it to read
  any item's DB info (e.g. `/webui/db/item/5939a00786f7742fe8132936`) instead of loading a JSON blob.
- `/webui/db/query` — a **general item query** and reusable building block for any mod page (`ServeQuery`).
  Filters every item by `parent=<id>` (exact), `name=<regex>`, `desc=<regex>`, `type=<Item|Node>`, `limit`
  (default 500); all ANDed; returns the same `{id,name,short,type,parent}` rows as the index. Regex is
  case-insensitive with a 1 s match timeout (a bad pattern is a 400). First consumer: the BarterEconomy
  Tier-list page. Handy as a dev query too, e.g. `/webui/db/query?parent=<catId>&name=salewa`.

**csproj gotcha:** `WebUiHost` uses ASP.NET `HttpContext`/`StatusCodes`. The old WebUI mod used SDK
`Microsoft.NET.Sdk.Web` (which references the ASP.NET shared framework); LeavesUtils uses plain
`Microsoft.NET.Sdk`, which does not. So the csproj carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
— compile-only, ships nothing extra (resolves against the SPT host's already-loaded ASP.NET runtime).

## 7. History / removed things (don't be surprised by git-less absence)

- `setModFolder` pattern: removed — every path is explicit now.
- Custom version checking: removed in favor of SPT's own mod dependency check.
- `<map>_probability.json` files that once appeared in the live mod folder were stale DataDump
  side-outputs from an older iteration; deleted, nothing regenerates them.
- Log lines from this assembly print callers' prefixes (e.g. `[Questrandomizer]`) because
  `printColor` takes the fully-formed message — the misattributed-prefix cleanup already
  happened; keep prefixes in the *caller's* string.

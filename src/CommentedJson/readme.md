# CommentedJson

Write an object to a `.jsonc` file **with `/* */` comments**, so a mod can save config changes
(from a web UI, say) without throwing away the inline documentation.

Comment text lives on the C# type as `[Comment]`; the file is a projection of the type, rewritten
in full every save. Reading is unchanged - SPT's `JsonUtil` / `loadFile<T>` /
`GetJsonDataFromFile<T>` already skip comments.

---

## Quick start

```csharp
using System.Text.Json.Serialization;
using Leaves;

public record MyConfig
{
    [Comment("how many")]
    [JsonPropertyName("count")]
    public int Count { get; set; } = 3;
}

Leaves.Utils.Instance.saveJsoncWithComments(new MyConfig(), modPath, "config/config.jsonc");
```

```jsonc
{
    "count": 3 /* how many */
}
```

That is the whole minimum: one attribute, one call. Everything below is optional.

---

## The tags

The first four drive JSONC writing. `[AllowedIds]` is metadata only - the emitter ignores it;
`describeConfig` and `Utils.CheckAllowedIds` read it.

### `[Comment("text")]` - on a property

The comment written after that property. A single-line comment sits inline after the value (or
after the opening brace, for a container). A comment whose text spans **more than one line** is
written as a `/* */` block on its own lines just above the property instead - so a numbered list
or a small table in a `[Comment]` keeps its shape. Its lines are trimmed and re-indented to the
property's column; blank lines are kept.

**Repeated text is written once by default** - the same sentence on ten dictionary entries is
noise. Set `Dedupe = false` on a comment that must appear every time, such as a short label on
each entry of a container.

```csharp
public record Box
{
    [Comment("a count")]
    [JsonPropertyName("x")]
    public int X { get; set; } = 1;

    [Comment("a count")]
    [JsonPropertyName("y")]
    public int Y { get; set; } = 2;

    [Comment("always shown", Dedupe = false)]
    [JsonPropertyName("z")]
    public int Z { get; set; } = 3;

    [Comment("always shown", Dedupe = false)]
    [JsonPropertyName("w")]
    public int W { get; set; } = 4;
}
```

```jsonc
{
    "x": 1, /* a count */

    "y": 2,

    "z": 3, /* always shown */

    "w": 4 /* always shown */
}
```

#### Scope: the whole file, keyed on the exact text

De-duplication matches on the comment **string**, not on the property it came from, and the set
spans the entire document. So two unrelated properties that happen to be worded the same collapse
into one comment:

```csharp
public record Screen
{
    [Comment("the window")]
    [JsonPropertyName("window")]
    public Window Window { get; set; } = new();

    [Comment("the grid, unrelated to the window")]
    [JsonPropertyName("grid")]
    public Grid Grid { get; set; } = new();
}

public record Window
{
    [Comment("width in cells")]  [JsonPropertyName("w")] public int W { get; set; } = 4;
    [Comment("height in cells")] [JsonPropertyName("h")] public int H { get; set; } = 3;
}

public record Grid   // same wording, different type
{
    [Comment("width in cells")]  [JsonPropertyName("w")] public int W { get; set; } = 10;
    [Comment("height in cells")] [JsonPropertyName("h")] public int H { get; set; } = 8;
}
```

```jsonc
{
    "window": { /* the window */
        "w": 4, /* width in cells */
        "h": 3 /* height in cells */
    },

    "grid": { /* the grid, unrelated to the window */
        "w": 10,
        "h": 8
    }
}
```

Usually that is what you want - the sentence was already said. When it is not, either word the
second one differently ("width in grid cells") or mark it `Dedupe = false`.

### `[CompactObject]` - on a type

Write this type on a single line instead of expanding it. Use it for the `{ one or two fields }`
value type of a big dictionary so the file keeps a dense, tabular shape.

```csharp
public record Box
{
    [Comment("dyes by id")]
    [JsonPropertyName("dyes")]
    public required Dictionary<string, Dye> Dyes { get; set; }
}

[CompactObject]
public record Dye
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }
}

// new Box { Dyes = new() { ["bg"] = new Dye { Code = "#111" }, ["fg"] = new Dye { Code = "#eee" } } }
```

```jsonc
{
    "dyes": { /* dyes by id */
        "bg": {"code":"#111"},
        "fg": {"code":"#eee"}
    }
}
```

Without the tag, and with `[Comment("hex code")]` on `Code` (repeats are dropped, so only the
first entry carries it):

```jsonc
{
    "dyes": { /* dyes by id */
        "bg": {
            "code": "#111" /* hex code */
        },
        "fg": {
            "code": "#eee"
        }
    }
}
```

A `[CompactObject]` type is pure data - `[Comment]`s inside it are never written inline (there is
no line to put them on). Document those fields in the `[ConfigHeader]`, or via `[DescribeEntries]`.

### `[ConfigHeader("text")]` - on the root type

Written verbatim as a block comment above the opening brace. This is where multi-line prose goes.

```csharp
[ConfigHeader("box config.\ntwo knobs, nothing clever.")]
public record Box
{
    [Comment("how many")]
    [JsonPropertyName("count")]
    public int Count { get; set; } = 3;
}
```

```jsonc
/* box config.
   two knobs, nothing clever.
*/
{
    "count": 3 /* how many */
}
```

### `[DescribeEntries]` - on a dictionary or list property

Lists the element type's de-duplicated comments once, in a block above the property, instead of
on the first entry. Use it when "first entry wins" would look arbitrary.

```csharp
public record Box
{
    [Comment("parts by id")]
    [DescribeEntries]
    [JsonPropertyName("parts")]
    public required Dictionary<string, Part> Parts { get; set; }
}

public record Part
{
    [Comment("part width")]
    [JsonPropertyName("span")]
    public required int Span { get; set; }

    [Comment("draw odds")]
    [JsonPropertyName("pull")]
    public required double Pull { get; set; }

    [Comment("no edits")]
    [JsonPropertyName("stuck")]
    public required bool Stuck { get; set; }
}
```

```jsonc
{
    /* parts by id
       span  - part width
       pull  - draw odds
       stuck - no edits
    */
    "parts": {
        "a": {
            "span": 4,
            "pull": 0.5,
            "stuck": false
        },
        "b": {
            "span": 7,
            "pull": 0.2,
            "stuck": true
        }
    }
}
```

Drop `[DescribeEntries]` and the same types give first-entry-wins instead:

```jsonc
{
    "parts": { /* parts by id */
        "a": {
            "span": 4, /* part width */
            "pull": 0.5, /* draw odds */
            "stuck": false /* no edits */
        },
        "b": {
            "span": 7,
            "pull": 0.2,
            "stuck": true
        }
    }
}
```

**Only de-duplicated comments are hoisted.** A `Dedupe = false` comment is per-entry by
definition, so it stays on every entry - which is how you keep a short navigational label on each
one:

```csharp
public record Bin
{
    [Comment("bin scale, applied first")]
    [JsonPropertyName("scale")]
    public required double Scale { get; set; }

    [Comment("low band", Dedupe = false)]
    [JsonPropertyName("low")]
    public required Dictionary<string, Slot> Low { get; set; }

    [Comment("high band", Dedupe = false)]
    [JsonPropertyName("high")]
    public required Dictionary<string, Slot> High { get; set; }
}
```

```jsonc
{
    /* bins by id
       scale - bin scale, applied first
    */
    "bins": {
        "one": {
            "scale": 1.5,
            "low": { /* low band */
                "p": {"w":2}
            },
            "high": {} /* high band */
        },
        "two": {
            "scale": 1,
            "low": {}, /* low band */
            "high": { /* high band */
                "q": {"w":4}
            }
        }
    }
}
```

If the element type has nothing to hoist - a scalar like `Dictionary<string, int>`, or a type
whose comments are all `Dedupe = false` - no block is written and the property comments normally.

### `[AllowedIds(kind, ...ids)]` - on a property whose keys/values are item-DB ids

Declares which ids are valid as the **keys** of a `Dictionary` (or of every element dict of a
`List<Dictionary<MongoId, T>>` - the "duplicate keys allowed" shape, where the tag rides on the
outer list property and is pushed down onto each entry dict), or as the **value** of a `MongoId`
leaf / a `List<MongoId>`. Repeatable - the kinds OR together. The JSONC emitter does not read it;
it exists for two consumers:

- **`describeConfig`** copies the kinds onto the `ConfigSchemaNode` (`AllowedIds`), so the config
  editor offers a **pick button per kind** (Pick Item / Pick Category / Pick Trader), each opening a
  modal picker scoped to those kinds, and shows the resolved name beside the id box. An unannotated
  mongoId field still gets a single "ID Picker" (all kinds), with a warning that its id type is undefined.
- **`Utils.CheckAllowedIds(configObject)`** walks a loaded / posted config and returns a message
  for every tagged key/value that matches none of its kinds (empty list == clean). Call it on
  load (warn) or in a config POST handler (reject).

```csharp
public record LimitedTradersConfig
{
    [DescribeEntries]
    [AllowedIds(AllowedIdKind.Node)]     // any category node
    [AllowedIds(AllowedIdKind.Item)]     // ... or any real item template
    [JsonPropertyName("categories")]
    public required Dictionary<MongoId, CategoryEntry> Categories { get; set; }

    [AllowedIds(AllowedIdKind.Trader)]   // any trader id
    [JsonPropertyName("tradersToLimit")]
    public required List<MongoId> TradersToLimit { get; set; }
}
```

`AllowedIdKind` is `Item` (`_type == "Item"`), `Node` (`_type == "Node"`, a category — shown as
"Group" in the picker), and `Trader` (a key of the trader table). Item/Node resolve in the item DB;
Trader resolves in the trader table instead (traders live outside the item DB). (`DescendantOf` /
`ExactId` were considered and left out - add them back if a config ever needs a narrower constraint.)

`CheckAllowedIds` needs the item DB populated, so call it no earlier than the first `OnUpdate`
(WTT-style mods inject items after `PostDBModLoader`), not in `OnLoad`.

---

### `[Choices(...)]` - on a string leaf, or a dict property (constrains its keys)

Form-only, like `[AllowedIds]`: the emitter ignores it, the server never reads it (the config
still stores plain strings). Pass an explicit list, or an enum `Type` whose names become the
choices. A `string` leaf renders as a `<select>`; on a dictionary the keys render as a `<select>`
each (names already used by another row are greyed out; "+ Add entry" picks the first free one).
Surfaces as `Choices` on the `ConfigSchemaNode`.

```cs
[Choices(typeof(DamageEffectType))]        // -> "HeavyBleeding", "LightBleeding", ...
[JsonPropertyName("effects")]
public Dictionary<string, EffectEdit>? Effects { get; set; }

[Choices("scav", "pmc", "boss")]
[JsonPropertyName("spawnAs")]
public string SpawnAs { get; set; } = "scav";
```

Pass `useEnumValues: true` when the config must key by the enum's **integer** (e.g. an item's
`effects_damage`): the config stores/round-trips `"0"`, `"1"`, … while the `<select>` shows the
enum name. Surfaces as parallel `Choices` (values) + `ChoiceLabels` (names) on the node; the
emitter still ignores it, so pair it with a `resolveLabel` callback (see Entry labels) if you want
the name written as an inline `/* … */` comment on disk.

```cs
[Choices(typeof(DamageEffectType), true)]  // stores "0".."8", shows the name
```

---

### `[SourceField("path")]` / `[FetchSource("url")]` - "Fetch" button in the config editor

Form-only: the emitter ignores both, nothing server-side reads them. Together they let the config
editor pre-fill an entry from a fetched JSON document.

- `[FetchSource("/webui/db/item/{key}")]` on a **type** (or a dict-value / object property) puts a
  **Fetch** button on each entry of a dictionary of that type. `{key}` in the template is replaced
  with the entry's key. Surfaces as `FetchUrl`.
- `[SourceField("item._props.MedUseTime")]` on a **leaf** (or a container assigned wholesale, like
  an `effects` dict) is a dotted path into the fetched document. On Fetch, every `[SourceField]`
  under the entry is read from the response and written into the model. Surfaces as `SourceField`.

```cs
[FetchSource("/webui/db/item/{key}")]
public record MedEntry
{
    [SourceField("item._props.medUseTime")]
    [JsonPropertyName("medUseTime")]
    public double? MedUseTime { get; set; }

    [SourceField("item._props.effects_damage")]   // whole dict copied in
    [JsonPropertyName("effects")]
    public Dictionary<string, EffectEdit>? Effects { get; set; }
}
```

Fetch **commits** the values into the config (it does not show them as ghost placeholders). A leaf
whose path is missing from the response is left as-is.

---

### `[Optional]` - on a nullable object / list / dict property

Form-only, like `[AllowedIds]`: the emitter ignores it (a `null` member is already omitted), the
server never reads it. The web-UI config editor renders the property as an **enable/disable toggle**
instead of an always-open sub-tree. Off = the key is absent from the saved config; on = seeded with
a default (an object keeps its required scalars, nested containers; drops its optional scalars) and
rendered like any other container. Surfaces as `Optional` on the `ConfigSchemaNode`.

```cs
[Comment("Present => clone the source into a new item. Absent => edit it in place.")]
[Optional]
[JsonPropertyName("copyToNew")]
public CopySpec? CopyToNew { get; set; }
```

---

## Entry labels

Dictionary keys and list elements get their comment from a callback, not an attribute - an id
means nothing without a lookup. Labels are resolved at write time and are **never de-duplicated**,
so the same id keeps its label everywhere it appears.

`saveJsoncWithComments` takes an optional `Func<LabelRequest, string?>`. It is called once per
dictionary key and list element; return a string to use as the comment, or null.

`LabelRequest` gives you `Key`, `Target` (`DictionaryKey` / `ListElement`), `PropertyName` (the
enclosing dictionary/list property), and `Path` (the property chain above that, so you can tell
apart a name that appears at more than one nesting level).

### The built-in fallback

Any key your callback returns null for - and every key when you pass no callback - goes through
`defaultIdLabel`. It does **exactly** this, in order:

1. **Gate.** The key must be exactly 24 hex characters (`0-9 a-f A-F`). Anything else -> no label,
   no lookup. So `bigmap`, `factory4_day`, `weapon`, `3` all get nothing.
2. **English locale `"<key> Name"`.** This is the same table the game UI reads, so it already
   covers, keyed by their 24-hex id:
   - item template ids -> the item name (`"F-1 hand grenade"`)
   - handbook **category** ids -> the category name (`"Ammo"`, `"Food"`, `"Ammo container"`)
3. **Trader id.** If the key is a trader id, its nickname (`"Therapist"`). Traders have no locale
   `Name` entry, hence the separate step.
4. Otherwise, null.

You do **not** need a callback to get item, category, or trader names on a `Dictionary<MongoId,X>` -
that is what step 2 and 3 are. Write a callback only for keys the fallback can't reach:
non-24-hex keys (map codenames, plain words), quest ids (their locale key is `"<id> name"`,
lowercase, which step 2 does not match), or names in a language other than English.

```csharp
public record Box
{
    [Comment("tags")]
    [JsonPropertyName("tags")]
    public required List<string> Tags { get; set; }

    [Comment("slots by id")]
    [JsonPropertyName("slots")]
    public required Dictionary<string, Slot> Slots { get; set; }
}

var tagName  = new Dictionary<string, string> { ["red"] = "warm", ["blue"] = "cool" };
var slotName = new Dictionary<string, string> { ["s1"] = "primary", ["s2"] = "backup" };

Leaves.Utils.Instance.saveJsoncWithComments(data, modPath, "config/config.jsonc", request =>
{
    if (request.PropertyName == "tags")  return tagName.GetValueOrDefault(request.Key);
    if (request.PropertyName == "slots") return slotName.GetValueOrDefault(request.Key);
    return null;
});
```

```jsonc
{
    "tags": [ /* tags */
        "red", /* warm */
        "blue" /* cool */
    ],

    "slots": { /* slots by id */
        "s1": {"w":1}, /* primary */
        "s2": {"w":2} /* backup */
    }
}
```

The same key in two places keeps its label both times:

```jsonc
{
    "p": { /* first set */
        "k": 1 /* shared */
    },
    "q": { /* second set */
        "k": 2 /* shared */
    }
}
```

---

## Also read

- `[JsonPropertyName]` - the key written. Falls back to the naming policy on the options, else the
  C# property name (same rule System.Text.Json uses).
- `[JsonPropertyOrder]` - order in the file. Default 0, ties broken by declaration order.
- `[JsonIgnore]` - skipped.
- `[Range(min, max)]` - `System.ComponentModel.DataAnnotations`. **No effect on the file**; it
  feeds `describeConfig` and can be reused for `Validator.TryValidateObject` on a POST.

```csharp
public record Box
{
    [Comment("third")]
    [JsonPropertyOrder(2)]
    [JsonPropertyName("c")]
    public int C { get; set; } = 3;

    [Comment("first")]
    [JsonPropertyOrder(0)]
    [JsonPropertyName("a")]
    public int A { get; set; } = 1;

    [Comment("second")]
    [JsonPropertyOrder(1)]
    [JsonPropertyName("b")]
    public int B { get; set; } = 2;
}
```

```jsonc
{
    "a": 1, /* first */

    "b": 2, /* second */

    "c": 3 /* third */
}
```

Null-valued properties are skipped, so an optional field simply does not appear.

---

## describeConfig - the same attributes as a form schema

`Leaves.Utils.Instance.describeConfig<T>()` returns a `ConfigSchemaNode` tree for a web-UI form.

```csharp
public record Box
{
    [Comment("retry count")]
    [Range(0, 10)]
    [SchemaDefault(3)]
    [JsonPropertyName("retries")]
    public int Retries { get; set; } = 3;

    [Comment("fill ratio")]
    [Range(0.0, 1.0)]
    [JsonPropertyName("ratio")]
    public double Ratio { get; set; } = 0.5;
}
```

A C# initializer (`= 3`) is compiled into the constructor and is invisible to reflection, so the
walker cannot see it. Add `[SchemaDefault(3)]` when a freshly-added web-UI entry should start at
that value instead of `0` / `[Range]` minimum. It surfaces as `SchemaDefault` on the node and is
used **only** by the form - the server never reads it at load time, and it is unrelated to
`[RequiredMember]`. An enum default is normalised to its name string.

The written file ignores `[Range]`:

```jsonc
{
    "retries": 3, /* retry count */

    "ratio": 0.5 /* fill ratio */
}
```

`describeConfig<Box>()`, serialized as an endpoint would:

```json
{
  "Name": "",
  "Kind": "object",
  "Comment": null,
  "Required": false,
  "Min": null,
  "Max": null,
  "Children": [
    { "Name": "retries", "Kind": "int",    "Comment": "retry count", "Required": false, "Min": 0, "Max": 10, "Children": [], "Header": null, "EnumValues": null, "AllowedIds": null, "SchemaDefault": 3 },
    { "Name": "ratio",   "Kind": "number", "Comment": "fill ratio",  "Required": false, "Min": 0, "Max": 1,  "Children": [], "Header": null, "EnumValues": null, "AllowedIds": null, "SchemaDefault": null }
  ],
  "Header": null,
  "EnumValues": null
}
```

`Kind` is one of `object`, `dict`, `list`, `int`, `number`, `bool`, `string`, `enum`, `mongoId`
(`mongoId` is a string leaf on the wire — the tag just lets a form validate the 24-hex shape). A
`dict`'s single child is named `<entry>`; a `list`'s is `<item>`. `Header` is the root type's
`[ConfigHeader]` text (null everywhere else). `EnumValues` lists the allowed names of an `enum`
node (null for every other kind). `AllowedIds` is the `[AllowedIds]` kinds on that property - a
list of `"Item"` / `"Node"` strings, null when the property has no such tag. `SchemaDefault` is the
property's `[SchemaDefault]` value (null when absent), consumed only by the form to seed a new entry.

---

## What gets walked, and what is handed to System.Text.Json

The emitter writes objects, dictionaries and lists itself - that is how a comment lands on the
right line. Everything else is rendered by `JsonSerializer`, so custom converters still apply.

A value is handed over whole when it is:

- a scalar (primitive, enum, string, decimal, DateTime, DateTimeOffset, TimeSpan, Guid, Uri),
- a `[CompactObject]` type, or
- a type with a converter registered from outside `System.Text.Json` - SPT's `MongoId`, for one.

Otherwise a dictionary is walked as a dictionary, an `IEnumerable` as a list, and anything else as
an object. **No opt-in tag is needed to walk a nested type**; add a `[Comment]` and it shows up.

---

## Regeneration - what the file loses

The whole file is rewritten on every save:

- Layout is normalized: 4-space indent, `\n`, one entry per line, trailing newline. Top-level
  members are separated by a blank line; nested objects and dictionary entries stay compact.
- `//` comments become `/* */`.
- Comment text comes from the attributes, not from whatever the old file said.
- Key order is C# property order - pin it with `[JsonPropertyOrder]`.
- **Free-standing comments are lost.** A `// section header` attached to nothing has nowhere to
  live; fold it into a nearby `[Comment]` or the `[ConfigHeader]`.
- A multi-line `[Comment]` is re-emitted as a block just above its property (see `[Comment]`
  above); a single-line one stays inline. Whitespace inside a single-line comment is collapsed.

---

## API

`Leaves.Utils` (inject it, or `Leaves.Utils.Instance`):

```csharp
void saveJsoncWithComments<T>(T data, string modPath, string file);
void saveJsoncWithComments<T>(T data, string modPath, string file,
                              Func<LabelRequest, string?>? resolveLabel);

ConfigSchemaNode describeConfig<T>();
```

Throws `InvalidOperationException` if called before `JsonUtil` exists (i.e. before server startup).

### Standalone (no SPT)

Everything in this folder depends only on `System.Text.Json` and reflection. Copy the folder into
any project and call:

```csharp
public static class CommentedJson
{
    public static string Serialize<T>(T data, CommentedJsonSettings? settings = null);
}

public sealed class CommentedJsonSettings
{
    public Func<LabelRequest, string?>? ResolveLabel { get; init; }   // null => no entry labels
    public JsonSerializerOptions? BaseOptions { get; init; }          // leaf rendering; null => default
}

public readonly record struct LabelRequest(
    string PropertyName,           // json name of the enclosing dict/list property
    string Key,                    // dict key, or list element ToString()
    LabelTarget Target,            // DictionaryKey | ListElement
    IReadOnlyList<string> Path     // enclosing property names, outermost first, excluding PropertyName
);
```

`Serialize` returns the text; writing it is the caller's job. `Path` lets a resolver tell apart a
property name that appears at more than one nesting level.

`saveJsoncWithComments` is exactly this, with `BaseOptions` = SPT's non-indented options and
`ResolveLabel` = your `resolveLabel` (if any), falling back to the item/trader id lookup for
anything it returns null for.

---

## Files

| File | Role | SPT dep |
|---|---|---|
| `CommentedJsonAttributes.cs` | the tags (`[Comment]` … `[AllowedIds]`, `[SchemaDefault]`) | no |
| `CommentedJson.cs` | `Serialize` entry point, settings, `LabelRequest`, property naming/order | no |
| `CommentedJsonEmitter.cs` | the walker that writes the text | no |
| `CommentedJsonSchema.cs` | `ConfigSchema.Describe` + `ConfigSchemaNode` | no |
| `../Utils.CommentedJson.cs` | `saveJsoncWithComments` / `describeConfig`; SPT options + id resolver | yes |

`walkthrough.md` explains how the code works; `trace-example.md` follows one `Serialize` call
through the emitter step by step.

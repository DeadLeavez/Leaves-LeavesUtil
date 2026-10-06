using System.Text.Json.Serialization;

namespace Leaves;

// JSON shapes for WebUiHost's data endpoints. Kept nested and private since nothing else uses them.
public partial class WebUiHost
{
    // One row of /webui/db/index, one per item in the database.
    private class ItemIndexEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("short")] public string Short { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("parent")] public string Parent { get; set; } = "";
    }

    // /webui/db/item/{id}. Item is the raw template object, serialized with its own _id/_name/_props/... field names; locale and handbook are the resolved side tables for the same id.
    private class ItemDetail
    {
        [JsonPropertyName("item")] public object? Item { get; set; }
        [JsonPropertyName("locale")] public LocaleBlock? Locale { get; set; }
        [JsonPropertyName("handbook")] public HandbookBlock? Handbook { get; set; }
        [JsonPropertyName("modded")] public bool Modded { get; set; }
    }

    private class LocaleBlock
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("ShortName")] public string? ShortName { get; set; }
        [JsonPropertyName("Description")] public string? Description { get; set; }
    }

    private class HandbookBlock
    {
        [JsonPropertyName("ParentId")] public string? ParentId { get; set; }
        [JsonPropertyName("Price")] public double? Price { get; set; }
    }

    // One row of /webui/dev/loadorderJson: an IOnLoad component and the priority that decides when it runs.
    // index is the position in the sorted run order (1-based); priority is the raw Injectable TypePriority; stage names the OnLoadOrder band it falls in; assembly is the DLL (a mod name, or SPTarkov.Server.Core).
    private class LoadOrderEntry
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("priorityLabel")] public string PriorityLabel { get; set; } = "";
        [JsonPropertyName("stage")] public string Stage { get; set; } = "";
        [JsonPropertyName("class")] public string Class { get; set; } = "";
        [JsonPropertyName("assembly")] public string Assembly { get; set; } = "";
        [JsonPropertyName("injection")] public string Injection { get; set; } = "";
    }

    // One row of /webui/db/traders: a trader the config-editor picker can offer for an [AllowedIds(Trader)] field.
    private class TraderEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    }

    // One row of /webui/db/profiles: a stored profile the page can pick from.
    private class ProfileEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("level")] public int Level { get; set; }
    }

    // /webui/db/stash: the picked profile plus its stash contents as a template-id -> total-count map.
    private class StashResponse
    {
        [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
        [JsonPropertyName("items")] public Dictionary<string, int> Items { get; set; } = new();
    }

    // A possible neighbor mod (see the Neighbors table). external = open in a new tab with an absolute http://host:port/ url; defaultPort is the fallback port.
    private class NeighborDef
    {
        public string Folder { get; }
        public string Name { get; }
        public string Icon { get; }
        public string Path { get; }
        public bool External { get; }
        public int DefaultPort { get; }

        public NeighborDef(
            string folder,
            string name,
            string icon,
            string path,
            bool external,
            int defaultPort
        )
        {
            Folder = folder;
            Name = name;
            Icon = icon;
            Path = path;
            External = external;
            DefaultPort = defaultPort;
        }
    }

    // GET /webui/lock: whether the rail should lock protected entries, and the SHA-256 (hex) of the
    // configured password for the client to check against. hash is null when enabled is false.
    private class LockInfo
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }

    // One row of /webui/rail/neighbors: an installed neighbor mod for the rail to render.
    private class NeighborEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("icon")] public string Icon { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("external")] public bool External { get; set; }
    }

    // One row of /webui/config: a mod that registered a Kind="config" GET (get), and its POST
    // (save) if it registered one too. save == null means view-only.
    private class ConfigModEntry
    {
        [JsonPropertyName("mod")] public string Mod { get; set; } = "";
        [JsonPropertyName("get")] public string? Get { get; set; }
        [JsonPropertyName("save")] public string? Save { get; set; }
    }
}

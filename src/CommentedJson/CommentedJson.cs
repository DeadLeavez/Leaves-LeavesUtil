using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leaves;

// Whether a label is being resolved for a dictionary key or a list element.
public enum LabelTarget
{
    DictionaryKey,
    ListElement,
}

// Passed to CommentedJsonSettings.ResolveLabel once per dictionary key / list element.
// Path is the enclosing property chain, outermost first, not including PropertyName.
public readonly record struct LabelRequest(
    string PropertyName,
    string Key,
    LabelTarget Target,
    IReadOnlyList<string> Path
);

public sealed class CommentedJsonSettings
{
    // Turn a dictionary key or list element into an identity comment, or null for none.
    public Func<LabelRequest, string?>? ResolveLabel { get; init; }

    // Options used to render leaf values, so custom converters still apply. Must not be indented;
    // an indented instance is cloned. Null uses a shared default.
    public JsonSerializerOptions? BaseOptions { get; init; }
}

// Serializes an object to JSONC with comments taken from [Comment] attributes.
public static class CommentedJson
{
    public static string Serialize<T>(T data, CommentedJsonSettings? settings = null)
    {
        settings ??= new CommentedJsonSettings();
        JsonSerializerOptions leafOptions = LeafOptions.For(settings.BaseOptions);
        return new CommentedJsonEmitter(settings, leafOptions).Emit(data);
    }
}

// Leaf values are rendered by System.Text.Json, which must not indent them - the emitter owns
// all layout. Clones are cached per source so repeated calls don't rebuild STJ's type metadata.
internal static class LeafOptions
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> flattened = new();

    private static readonly JsonSerializerOptions fallback = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions For(JsonSerializerOptions? source)
    {
        if (source == null)
        {
            return fallback;
        }
        if (!source.WriteIndented)
        {
            return source;
        }
        return flattened.GetValue(source, static key => new JsonSerializerOptions(key) { WriteIndented = false });
    }
}

internal static class JsonNaming
{
    public static string Of(PropertyInfo prop, JsonNamingPolicy? policy)
    {
        JsonPropertyNameAttribute? explicitName = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (explicitName != null)
        {
            return explicitName.Name;
        }
        if (policy != null)
        {
            return policy.ConvertName(prop.Name);
        }
        return prop.Name;
    }

    // Properties in the order they are written: [JsonPropertyOrder], then declaration order.
    public static List<PropertyInfo> WritableProperties(Type type)
    {
        List<PropertyInfo> props = new();
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            {
                props.Add(prop);
            }
        }
        props.Sort(byOrderThenDeclaration);
        return props;
    }

    private static int byOrderThenDeclaration(PropertyInfo a, PropertyInfo b)
    {
        int orderA = a.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0;
        int orderB = b.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0;
        if (orderA != orderB)
        {
            return orderA.CompareTo(orderB);
        }
        return a.MetadataToken.CompareTo(b.MetadataToken);
    }
}

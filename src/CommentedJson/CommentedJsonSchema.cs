using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves;

// One node of a config type's shape, for a web-UI form. Children hold an object's properties, a
// dictionary's single "<entry>" template, or a list's single "<item>" template.
// Header is the root type's [ConfigHeader] text (null on every non-root node). EnumValues is the
// list of allowed names for an "enum" node (null for every other kind).
public sealed record ConfigSchemaNode(
    string Name,
    string Kind,
    string? Comment,
    bool Required,
    double? Min,
    double? Max,
    List<ConfigSchemaNode> Children,
    string? Header,
    List<string>? EnumValues,
    // The [AllowedIds] kinds on this property ("Item" / "Node"), null when it has none.
    List<string>? AllowedIds = null,
    // The property's [SchemaDefault], used ONLY by the schema -> web-UI form to seed a freshly-added
    // entry. Not a load-time fallback and not tied to [RequiredMember] - the server never reads it.
    // Null when the property has no [SchemaDefault] tag. A C# property initializer (= 300) is
    // invisible to reflection, so [SchemaDefault(300)] is the only way to carry that number to the
    // form. An enum default is normalised to its name string so it matches EnumValues.
    object? SchemaDefault = null,
    // The property carries [Percent]: its stored value is a 0..1 fraction the web-UI form shows and
    // edits as a 0..100 percentage. Min/Max above are still in fraction terms. Number/int kinds only.
    bool Percent = false,
    // The property carries [Optional]: the web-UI form renders this container as an enable/disable
    // toggle, absent from the config when off. Container kinds only.
    bool Optional = false,
    // The property's [Choices] values: a string leaf renders as a <select>; a dict node's keys are
    // constrained to these. Null when the property has no [Choices] tag.
    List<string>? Choices = null,
    // Parallel to Choices: the display label for each value, when [Choices(enum, useEnumValues:true)]
    // stores the enum integer but shows the name. Null (the usual case) means label == value.
    List<string>? ChoiceLabels = null,
    // The property's [SourceField] dotted path into a fetched document, for the web-UI "Fetch"
    // button. Null when the property has no [SourceField] tag.
    string? SourceField = null,
    // The [FetchSource] URL template (from the property or its type). Present on the node that owns
    // the "Fetch" button - a dict's "<entry>" value node, or an object node. Null otherwise.
    string? FetchUrl = null
);

// Walks a config type into ConfigSchemaNode, reading the same [Comment] the emitter writes plus
// [Range]. Kind is one of: object, dict, list, int, number, bool, string, enum, mongoId.
// (mongoId is a string leaf on the wire; the tag just lets the web-UI form validate the shape.)
public static class ConfigSchema
{
    public static ConfigSchemaNode Describe(Type type)
    {
        return Describe(type, null);
    }

    // leafOptions is the same JsonSerializerOptions the values are serialised with. It lets the
    // walker spot a type an outside converter owns (SPT's MongoId) and treat it as a "string" leaf
    // instead of reflecting its properties into a bogus object node.
    public static ConfigSchemaNode Describe(Type type, JsonSerializerOptions? leafOptions)
    {
        ConfigSchemaNode root = describe("", type, null, false, leafOptions);

        // [ConfigHeader] lives on the root config type only (the emitter writes it as the file's
        // top block comment). Surface it so the web-UI form can show it as an intro blurb.
        string? header = type.GetCustomAttribute<ConfigHeaderAttribute>()?.Text;
        if (header == null)
        {
            return root;
        }
        return root with { Header = header };
    }

    private static ConfigSchemaNode describe(
        string name,
        Type declared,
        PropertyInfo? property,
        bool required,
        JsonSerializerOptions? leafOptions
    )
    {
        Type type = Nullable.GetUnderlyingType(declared) ?? declared;

        string? comment = property?.GetCustomAttribute<CommentAttribute>()?.Text;
        RangeAttribute? range = property?.GetCustomAttribute<RangeAttribute>();
        List<string>? allowedIds = readAllowedIds(property);

        bool percent = property?.GetCustomAttribute<PercentAttribute>() != null;
        bool optional = property?.GetCustomAttribute<OptionalAttribute>() != null;
        ChoicesAttribute? choicesAttribute = property?.GetCustomAttribute<ChoicesAttribute>();
        List<string>? choices = readChoices(choicesAttribute);
        List<string>? choiceLabels = readChoiceLabels(choicesAttribute);
        string? sourceField = property?.GetCustomAttribute<SourceFieldAttribute>()?.Path;
        string? fetchUrl = property?.GetCustomAttribute<FetchSourceAttribute>()?.UrlTemplate
            ?? type.GetCustomAttribute<FetchSourceAttribute>()?.UrlTemplate;
        object? defaultValue = property?.GetCustomAttribute<SchemaDefaultAttribute>()?.Value;
        if (defaultValue != null && defaultValue.GetType().IsEnum)
        {
            // [SchemaDefault(SomeEnum.Foo)] boxes the enum; a modder's own enum may lack the string
            // converter SPT puts on its own, so hand the form the name to match EnumValues.
            defaultValue = defaultValue.ToString();
        }

        string kind = kindOf(type, leafOptions);
        List<ConfigSchemaNode> children = new();

        List<string>? enumValues = null;
        if (kind == "enum")
        {
            enumValues = new List<string>();
            foreach (string enumName in Enum.GetNames(type))
            {
                enumValues.Add(enumName);
            }
        }
        else if (kind == "object")
        {
            foreach (PropertyInfo child in JsonNaming.WritableProperties(type))
            {
                children.Add(describe(JsonNaming.Of(child, null), child.PropertyType, child, isRequired(child), leafOptions));
            }
        }
        else if (kind == "dict")
        {
            children.Add(describe("<entry>", type.GetGenericArguments()[1], null, false, leafOptions));
        }
        else if (kind == "list")
        {
            ConfigSchemaNode itemNode = describe("<item>", type.GetGenericArguments()[0], null, false, leafOptions);

            // [AllowedIds] on a List<Dictionary<MongoId, T>> means "constrain the KEYS of every
            // element dict". The property tag lands on this list node, so push it down onto the
            // <item> dict node where renderDict / CheckAllowedIds look for it.
            if (allowedIds != null && itemNode.Kind == "dict" && itemNode.AllowedIds == null)
            {
                itemNode = itemNode with { AllowedIds = allowedIds };
            }

            children.Add(itemNode);
        }

        return new ConfigSchemaNode(
            name,
            kind,
            comment,
            required,
            toDouble(range?.Minimum),
            toDouble(range?.Maximum),
            children,
            null,
            enumValues,
            allowedIds,
            defaultValue,
            percent,
            optional,
            choices,
            choiceLabels,
            sourceField,
            fetchUrl);
    }

    private static List<string>? readChoices(ChoicesAttribute? attribute)
    {
        if (attribute == null || attribute.Values.Length == 0)
        {
            return null;
        }
        return new List<string>(attribute.Values);
    }

    private static List<string>? readChoiceLabels(ChoicesAttribute? attribute)
    {
        if (attribute == null || attribute.Labels == null || attribute.Labels.Length == 0)
        {
            return null;
        }
        return new List<string>(attribute.Labels);
    }

    private static List<string>? readAllowedIds(PropertyInfo? property)
    {
        if (property == null)
        {
            return null;
        }

        List<string> kinds = new();
        foreach (AllowedIdsAttribute attribute in property.GetCustomAttributes<AllowedIdsAttribute>())
        {
            kinds.Add(attribute.Kind.ToString());
        }

        if (kinds.Count == 0)
        {
            return null;
        }
        return kinds;
    }

    private static string kindOf(Type type, JsonSerializerOptions? leafOptions)
    {
        if (type.IsEnum)
        {
            // SPT serialises enums as their string name (JsonStringEnumConverter is on nearly every
            // enum it ships); the form offers those names as a dropdown and they round-trip as-is.
            return "enum";
        }
        if (type == typeof(MongoId))
        {
            // A string on the wire, but tag it so the form can do a 24-hex shape check.
            return "mongoId";
        }
        if (type == typeof(bool))
        {
            return "bool";
        }
        if (type == typeof(string))
        {
            return "string";
        }
        if (type == typeof(int)
            || type == typeof(long)
            || type == typeof(short)
        )
        {
            return "int";
        }
        if (type == typeof(double)
            || type == typeof(float)
            || type == typeof(decimal)
        )
        {
            return "number";
        }
        // Types an outside converter renders as a single JSON string: SPT's MongoId, plus the BCL
        // value types System.Text.Json writes as strings. Reflecting their properties would produce
        // a nonsense object node (MongoId -> { IsEmpty, ExtensionData }).
        if (type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Uri)
        )
        {
            return "string";
        }
        if (hasOutsideConverter(type, leafOptions))
        {
            return "string";
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            return "dict";
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            return "list";
        }
        return "object";
    }

    // True when a converter from outside System.Text.Json owns this type's shape (SPT's MongoId is
    // registered on the options, not via a [JsonConverter] attribute, so only the live options can
    // tell). Mirrors CommentedJsonEmitter.hasCustomConverter.
    private static bool hasOutsideConverter(Type type, JsonSerializerOptions? leafOptions)
    {
        if (leafOptions == null)
        {
            return false;
        }

        JsonConverter? converter;
        try
        {
            converter = leafOptions.GetConverter(type);
        }
        catch (NotSupportedException)
        {
            return false;
        }
        if (converter == null)
        {
            return false;
        }

        string? ns = converter.GetType().Namespace;
        if (ns == null)
        {
            return true;
        }
        return !ns.StartsWith("System.Text.Json", StringComparison.Ordinal);
    }

    private static bool isRequired(PropertyInfo property)
    {
        return property.GetCustomAttribute<RequiredMemberAttribute>() != null;
    }

    private static double? toDouble(object? value)
    {
        if (value is IConvertible)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        return null;
    }
}

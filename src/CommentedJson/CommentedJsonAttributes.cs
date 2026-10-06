namespace Leaves;

// The tags that drive commented-JSONC writing, plus the two the emitter ignores but the schema
// walker reads ([AllowedIds], [SchemaDefault]). Full docs: CommentedJson/readme.md.

// The comment written after this property.
// Repeated text is written the first time only. Set Dedupe = false for a comment that has to
// appear on every occurrence, such as a short label on each entry of a container.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class CommentAttribute : Attribute
{
    public string Text { get; }
    public bool Dedupe { get; set; } = true;

    public CommentAttribute(string text)
    {
        Text = text;
    }
}

// Text written verbatim as a block comment above the opening brace. Root type only.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ConfigHeaderAttribute : Attribute
{
    public string Text { get; }

    public ConfigHeaderAttribute(string text)
    {
        Text = text;
    }
}

// Write this type on a single line instead of expanding it.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class CompactObjectAttribute : Attribute
{
}

// On a dictionary or list property: list the element type's dedupe-able comments once, in a
// block above the property, instead of repeating them on every entry.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class DescribeEntriesAttribute : Attribute
{
}

// Declares which item-database ids are valid here: the KEYS of a Dictionary (or of every element
// dict of a List<Dictionary<MongoId, T>>), or the VALUE of a MongoId leaf / a List<MongoId>
// element. Read by ConfigSchema (passed through to the web-UI
// form) and by Utils.CheckAllowedIds (server-side verification of a loaded / posted config).
// The JSONC emitter ignores it. Repeatable - the kinds OR together, so an id is accepted if it
// satisfies ANY attribute on the property.
//
//   [AllowedIds(AllowedIdKind.Node)]     // any category node
//   [AllowedIds(AllowedIdKind.Item)]     // any real item template
//   [AllowedIds(AllowedIdKind.Trader)]   // any trader id
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class AllowedIdsAttribute : Attribute
{
    public AllowedIdKind Kind { get; }

    public AllowedIdsAttribute(AllowedIdKind kind)
    {
        Kind = kind;
    }
}

// The value the schema hands the web-UI form to seed a freshly-added dict/list entry (put it on
// leaf properties - int / number / bool / string / enum / mongoId). A C# property initializer
// (= 300) is compiled into the constructor and is invisible to reflection, so this tag is the only
// way to carry that value to the form. It is NOT a load-time fallback and is unrelated to
// [RequiredMember] - the server never reads it; only ConfigSchema does, surfacing it as
// ConfigSchemaNode.SchemaDefault. An enum value is normalised to its name string on the wire.
// The JSONC emitter ignores it.
//
//   [SchemaDefault(300)]           // int / number
//   [SchemaDefault("scav")]        // string
//   [SchemaDefault(SomeEnum.Foo)]  // enum -> "Foo"
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SchemaDefaultAttribute : Attribute
{
    public object? Value { get; }

    public SchemaDefaultAttribute(object? value)
    {
        Value = value;
    }
}

// On a number / int property whose stored value is a 0..1 fraction: the web-UI form shows and edits
// it as a percentage (0..100 with a "%" suffix) and converts back to the fraction on save. The JSON
// on disk and everything server-side stay the raw fraction - this is display sugar for the form
// only. A [Range] on the same property, if present, is given in fraction terms too (0..1). Read by
// ConfigSchema (surfaced as ConfigSchemaNode.Percent); the JSONC emitter ignores it.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class PercentAttribute : Attribute
{
}

// A fixed set of allowed string values for the web-UI form. On a string leaf the field renders as a
// <select>; on a dictionary property the values constrain its KEYS (a <select> per key, like
// [AllowedIds]). Pass an explicit list, or an enum Type whose names become the choices (the config
// still stores plain strings). Emitter ignores it; nothing server-side reads it.
//
//   [Choices("scav", "pmc", "boss")]
//   [Choices(typeof(DamageEffectType))]         // stores + shows "HeavyBleeding", "LightBleeding", ...
//   [Choices(typeof(DamageEffectType), true)]   // STORES "0", "1", ... ; SHOWS the enum name
//
// The `useEnumValues` form is for a config whose on-disk shape must mirror something that keys by the
// enum's integer (e.g. an item's effects_damage). Values holds the stored strings; Labels holds the
// parallel display names. Labels is null for the name / explicit-list forms (label == value).
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ChoicesAttribute : Attribute
{
    public string[] Values { get; }
    public string[]? Labels { get; }

    public ChoicesAttribute(params string[] values)
    {
        Values = values;
    }

    public ChoicesAttribute(Type enumType)
        : this(enumType, false)
    {
    }

    public ChoicesAttribute(Type enumType, bool useEnumValues)
    {
        if (!enumType.IsEnum)
        {
            throw new ArgumentException(enumType + " is not an enum", nameof(enumType));
        }

        string[] names = Enum.GetNames(enumType);
        if (!useEnumValues)
        {
            Values = names;
            return;
        }

        // Enum.GetNames and Enum.GetValues are both ordered by underlying value, so the indexes line up.
        Array raw = Enum.GetValues(enumType);
        string[] values = new string[names.Length];
        for (int index = 0; index < names.Length; index++)
        {
            long numeric = Convert.ToInt64(raw.GetValue(index), System.Globalization.CultureInfo.InvariantCulture);
            values[index] = numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Values = values;
        Labels = names;
    }
}

// Ties a config property to a field on some fetched JSON document, for the web-UI form's "Fetch"
// button. On a leaf: Path is a dotted route into the fetched object (e.g. "_props.MedUseTime"). The
// button itself is declared by [FetchSource] on the containing type / dict-value. Form-only: the
// emitter ignores it and nothing server-side reads it.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SourceFieldAttribute : Attribute
{
    public string Path { get; }

    public SourceFieldAttribute(string path)
    {
        Path = path;
    }
}

// Put on a type (or a dict-value / object property): the web-UI form shows a "Fetch" button on each
// entry that GETs UrlTemplate and fills every descendant leaf carrying [SourceField] from the
// response. "{key}" in the template is replaced with the entry's dictionary key. Form-only: the
// emitter ignores it and nothing server-side reads it.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class FetchSourceAttribute : Attribute
{
    public string UrlTemplate { get; }

    public FetchSourceAttribute(string urlTemplate)
    {
        UrlTemplate = urlTemplate;
    }
}

// On a nullable object / list / dict property: the web-UI form renders it as an enable/disable
// toggle rather than an always-expanded sub-tree. Off = the property is absent from the config
// (the emitter already omits a null-valued member). Read by ConfigSchema (surfaced as
// ConfigSchemaNode.Optional); the JSONC emitter ignores it, and nothing server-side reads it.
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class OptionalAttribute : Attribute
{
}

public enum AllowedIdKind
{
    // Any item-DB entry whose _type is "Item".
    Item,

    // Any item-DB entry whose _type is "Node" (a category).
    Node,

    // Any trader id (a key of the trader table). Traders live outside the item DB.
    Trader,
}

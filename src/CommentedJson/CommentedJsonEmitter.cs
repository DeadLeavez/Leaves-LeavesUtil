using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leaves;

// Walks an object and writes JSONC directly, so a comment can simply be appended to the line it
// belongs on. Leaf values are handed to System.Text.Json so custom converters still apply.
internal sealed class CommentedJsonEmitter
{
    private const int IndentWidth = 4;

    private readonly CommentedJsonSettings settings;
    private readonly JsonSerializerOptions leafOptions;
    private readonly StringBuilder output = new();

    // Texts already written, so a repeat is dropped unless its comment set Dedupe = false.
    // Keyed on the text, not the property: identical wording anywhere in the file counts as a repeat.
    private readonly HashSet<string> writtenOnce = new();

    // Property names from the root down to the container being written.
    private readonly List<string> path = new();

    public CommentedJsonEmitter(
        CommentedJsonSettings settings,
        JsonSerializerOptions leafOptions
    )
    {
        this.settings = settings;
        this.leafOptions = leafOptions;
    }

    public string Emit(object? root)
    {
        if (root != null)
        {
            ConfigHeaderAttribute? header = root.GetType().GetCustomAttribute<ConfigHeaderAttribute>();
            if (header != null)
            {
                writeBlock(header.Text.Replace("\r\n", "\n").Split('\n'), 0);
            }
        }

        writeValue(root, 0, null, false, false);
        return output.ToString();
    }

    // Writes one value. The caller has already written the indent and "name": for it; this always
    // ends the line. suppressRepeats drops Dedupe comments outright, for entries of a
    // [DescribeEntries] container whose block already lists them.
    private void writeValue(
        object? value,
        int depth,
        string? comment,
        bool needsComma,
        bool suppressRepeats
    )
    {
        if (value == null)
        {
            writeInline("null", comment, needsComma);
            return;
        }

        switch (shapeOf(value))
        {
            case Shape.Dictionary:
                writeDictionary((IDictionary)value, depth, comment, needsComma, suppressRepeats);
                return;
            case Shape.List:
                writeList((IEnumerable)value, depth, comment, needsComma, suppressRepeats);
                return;
            case Shape.Object:
                writeObject(value, depth, comment, needsComma, suppressRepeats);
                return;
            default:
                writeInline(render(value), comment, needsComma);
                return;
        }
    }

    private void writeObject(
        object value,
        int depth,
        string? comment,
        bool needsComma,
        bool suppressRepeats
    )
    {
        List<Member> members = membersOf(value);
        if (members.Count == 0)
        {
            writeInline("{}", comment, needsComma);
            return;
        }

        openContainer('{', comment);

        for (int i = 0; i < members.Count; i++)
        {
            // A blank line between top-level knobs, the way a hand-written config is spaced.
            // Root only: nested objects and dictionary entries stay compact.
            if (i > 0 && depth == 0)
            {
                output.Append('\n');
            }

            Member member = members[i];

            // A block above the property replaces both its own comment and its entries' repeats.
            // With nothing to hoist there is no block, and the property comments normally.
            bool hoisted = false;
            if (member.Property.GetCustomAttribute<DescribeEntriesAttribute>() != null)
            {
                hoisted = writeEntriesBlock(member.Property, depth + 1);
            }

            string? memberComment = null;
            if (!hoisted)
            {
                memberComment = commentFor(member.Property, suppressRepeats);

                // A multi-line [Comment] is written as a block above the property line (like
                // [ConfigHeader] / [DescribeEntries]) rather than flattened into one inline
                // comment. commentFor already applied dedupe/suppression, so a null here means
                // nothing to write - block included.
                if (memberComment != null && rawCommentIsMultiLine(member.Property))
                {
                    List<string> blockLines = new();
                    foreach (string line in member.Property.GetCustomAttribute<CommentAttribute>()!.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        blockLines.Add(clean(line));
                    }
                    writeBlock(blockLines, depth + 1);
                    memberComment = null;
                }
            }

            output.Append(indent(depth + 1));
            output.Append(render(member.Name));
            output.Append(": ");

            path.Add(member.Name);
            writeValue(member.Value, depth + 1, memberComment, i < members.Count - 1, hoisted);
            path.RemoveAt(path.Count - 1);
        }

        closeContainer('}', depth, needsComma);
    }

    private void writeDictionary(
        IDictionary dictionary,
        int depth,
        string? comment,
        bool needsComma,
        bool suppressRepeats
    )
    {
        if (dictionary.Count == 0)
        {
            writeInline("{}", comment, needsComma);
            return;
        }

        openContainer('{', comment);

        int written = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            written++;
            string key = entry.Key.ToString() ?? "";

            output.Append(indent(depth + 1));
            output.Append(render(key));
            output.Append(": ");
            writeValue(
                entry.Value,
                depth + 1,
                labelFor(key, LabelTarget.DictionaryKey),
                written < dictionary.Count,
                suppressRepeats);
        }

        closeContainer('}', depth, needsComma);
    }

    private void writeList(
        IEnumerable list,
        int depth,
        string? comment,
        bool needsComma,
        bool suppressRepeats
    )
    {
        List<object?> items = new();
        foreach (object? item in list)
        {
            items.Add(item);
        }

        if (items.Count == 0)
        {
            writeInline("[]", comment, needsComma);
            return;
        }

        openContainer('[', comment);

        for (int i = 0; i < items.Count; i++)
        {
            string key = items[i]?.ToString() ?? "";
            output.Append(indent(depth + 1));
            writeValue(
                items[i],
                depth + 1,
                labelFor(key, LabelTarget.ListElement),
                i < items.Count - 1,
                suppressRepeats);
        }

        closeContainer(']', depth, needsComma);
    }

    // ============================================================
    // Line pieces
    // ============================================================

    private void writeInline(string text, string? comment, bool needsComma)
    {
        output.Append(text);
        if (needsComma)
        {
            output.Append(',');
        }
        appendComment(comment);
        output.Append('\n');
    }

    private void openContainer(char brace, string? comment)
    {
        output.Append(brace);
        appendComment(comment);
        output.Append('\n');
    }

    private void closeContainer(char brace, int depth, bool needsComma)
    {
        output.Append(indent(depth));
        output.Append(brace);
        if (needsComma)
        {
            output.Append(',');
        }
        output.Append('\n');
    }

    private void appendComment(string? comment)
    {
        if (comment == null)
        {
            return;
        }
        output.Append(" /* ");
        output.Append(collapseWhitespace(comment));
        output.Append(" */");
    }

    // An inline comment is one line, so a multi-line [Comment] flattened by clean() still carries
    // the source's line-leading indentation as runs of spaces. Squeeze every whitespace run to a
    // single space. Only for inline comments - writeBlock keeps its lines (and any table in them).
    private static string collapseWhitespace(string text)
    {
        StringBuilder squeezed = new(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    squeezed.Append(' ');
                }
            }
            else
            {
                squeezed.Append(c);
            }
            lastWasSpace = isSpace;
        }
        return squeezed.ToString().Trim();
    }

    private void writeBlock(IReadOnlyList<string> lines, int depth)
    {
        string pad = indent(depth);
        for (int i = 0; i < lines.Count; i++)
        {
            output.Append(pad);
            if (i == 0)
            {
                output.Append("/* ");
            }
            else
            {
                output.Append("   ");
            }
            output.Append(clean(lines[i]));
            output.Append('\n');
        }
        output.Append(pad);
        output.Append("*/\n");
    }

    // The property's own comment, then one aligned line per de-duplicated comment on the element
    // type. Comments marked Dedupe = false stay on every entry, so they are left out.
    // False when the element type has nothing to hoist; the caller then comments normally.
    private bool writeEntriesBlock(PropertyInfo property, int depth)
    {
        List<string> lines = new();

        CommentAttribute? own = property.GetCustomAttribute<CommentAttribute>();
        if (own != null)
        {
            // This comment lands in a block (writeBlock), which writes one line per entry - so a
            // multi-line [Comment] keeps its lines here, unlike the inline case that flattens.
            foreach (string ownLine in own.Text.Replace("\r\n", "\n").Split('\n'))
            {
                lines.Add(clean(ownLine));
            }
        }

        Type? elementType = entryTypeOf(property.PropertyType);
        if (elementType != null)
        {
            List<string> names = new();
            List<string> texts = new();
            int width = 0;

            foreach (PropertyInfo field in JsonNaming.WritableProperties(elementType))
            {
                CommentAttribute? comment = field.GetCustomAttribute<CommentAttribute>();
                if (comment == null || !comment.Dedupe)
                {
                    continue;
                }

                string name = JsonNaming.Of(field, leafOptions.PropertyNamingPolicy);
                names.Add(name);
                texts.Add(clean(comment.Text));
                if (name.Length > width)
                {
                    width = name.Length;
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                lines.Add("  " + names[i].PadRight(width) + " - " + texts[i]);
            }

            if (names.Count == 0)
            {
                return false;
            }
        }

        if (lines.Count == 0)
        {
            return false;
        }

        writeBlock(lines, depth);
        return true;
    }

    // ============================================================
    // Lookups
    // ============================================================

    // True when this property's [Comment] text spans more than one line - the author asking for a
    // block above the property rather than a flattened inline comment.
    private static bool rawCommentIsMultiLine(PropertyInfo property)
    {
        CommentAttribute? attribute = property.GetCustomAttribute<CommentAttribute>();
        if (attribute == null)
        {
            return false;
        }
        return attribute.Text.IndexOf('\n') >= 0;
    }

    private string? commentFor(PropertyInfo property, bool suppressRepeats)
    {
        CommentAttribute? attribute = property.GetCustomAttribute<CommentAttribute>();
        if (attribute == null)
        {
            return null;
        }

        string text = clean(attribute.Text);
        if (text.Length == 0)
        {
            return null;
        }

        if (!attribute.Dedupe)
        {
            return text;
        }
        if (suppressRepeats)
        {
            return null;
        }
        if (!writtenOnce.Add(text))
        {
            return null;
        }
        return text;
    }

    private string? labelFor(string key, LabelTarget target)
    {
        if (settings.ResolveLabel == null)
        {
            return null;
        }

        // A root-level container has no enclosing property, so PropertyName is empty.
        string propertyName = "";
        List<string> enclosing = new();
        if (path.Count > 0)
        {
            propertyName = path[^1];
            enclosing = path.GetRange(0, path.Count - 1);
        }
        string text = clean(settings.ResolveLabel(new LabelRequest(propertyName, key, target, enclosing)));
        if (text.Length == 0)
        {
            return null;
        }
        return text;
    }

    private readonly record struct Member(
        PropertyInfo Property,
        string Name,
        object Value
    );

    private List<Member> membersOf(object value)
    {
        List<Member> members = new();
        foreach (PropertyInfo property in JsonNaming.WritableProperties(value.GetType()))
        {
            object? memberValue = property.GetValue(value);
            if (memberValue == null)
            {
                continue;
            }
            members.Add(new Member(property, JsonNaming.Of(property, leafOptions.PropertyNamingPolicy), memberValue));
        }
        return members;
    }

    // Dictionary<K,V> -> V, List<T> -> T.
    private static Type? entryTypeOf(Type propertyType)
    {
        if (!propertyType.IsGenericType)
        {
            return null;
        }
        Type[] arguments = propertyType.GetGenericArguments();
        if (arguments.Length == 0)
        {
            return null;
        }
        return arguments[^1];
    }

    // ============================================================
    // Shape
    // ============================================================

    private enum Shape
    {
        Leaf,
        Object,
        Dictionary,
        List,
    }

    private Shape shapeOf(object value)
    {
        Type type = value.GetType();

        if (type.GetCustomAttribute<CompactObjectAttribute>() != null)
        {
            return Shape.Leaf;
        }
        if (isScalar(type))
        {
            return Shape.Leaf;
        }
        if (hasCustomConverter(type))
        {
            return Shape.Leaf;
        }
        if (value is IDictionary)
        {
            return Shape.Dictionary;
        }
        if (value is IEnumerable)
        {
            return Shape.List;
        }
        return Shape.Object;
    }

    private static bool isScalar(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
        {
            return true;
        }
        return type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid)
            || type == typeof(Uri);
    }

    // A converter from outside System.Text.Json owns this type's shape (SPT's MongoId is one), so
    // hand the whole value over instead of walking its properties.
    private bool hasCustomConverter(Type type)
    {
        JsonConverter? converter = leafOptions.GetConverter(type);
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

    // ============================================================
    // Text
    // ============================================================

    private string render(object value)
    {
        return JsonSerializer.Serialize(value, value.GetType(), leafOptions);
    }

    private static string indent(int depth)
    {
        return new string(' ', depth * IndentWidth);
    }

    // A comment can't contain "*/" and has to stay on one line.
    private static string clean(string? text)
    {
        if (text == null)
        {
            return "";
        }
        string result = text.Replace("*/", "* /");
        result = result.Replace('\r', ' ');
        result = result.Replace('\n', ' ');
        return result.Trim();
    }
}

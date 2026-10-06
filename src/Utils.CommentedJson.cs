using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Utils;

namespace Leaves;

// Commented-JSONC config writing. Full docs: CommentedJson/readme.md.
// Read side is unchanged - loadFile / GetJsonDataFromFile already skip comments.
public partial class Utils
{
    public void saveJsoncWithComments<T>(
        T data,
        string modPath,
        string file
    )
    {
        saveJsoncWithComments(data, modPath, file, null);
    }

    // resolveLabel turns a dictionary key / list element into a comment. When it is null, or
    // returns null for a key, that key falls back to defaultIdLabel (see it for exactly what
    // resolves for free - item names, handbook category names, trader names).
    public void saveJsoncWithComments<T>(
        T data,
        string modPath,
        string file,
        Func<LabelRequest, string?>? resolveLabel
    )
    {
        if (JsonUtil.JsonSerializerOptionsNoIndent == null)
        {
            throw new InvalidOperationException("JsonUtil not ready - call saveJsoncWithComments after server startup.");
        }

        CommentedJsonSettings settings = new()
        {
            BaseOptions = JsonUtil.JsonSerializerOptionsNoIndent,
            ResolveLabel = delegate (LabelRequest request)
            {
                string? custom = resolveLabel?.Invoke(request);
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    return custom;
                }
                return defaultIdLabel(request.Key);
            },
        };

        string text = CommentedJson.Serialize(data, settings);
        fileUtil.WriteFile(System.IO.Path.Combine(modPath, file), text);
    }

    public ConfigSchemaNode describeConfig<T>()
    {
        // Pass the live serializer options so the walker can spot outside-converter types (MongoId)
        // and emit them as string leaves rather than reflecting their CLR properties.
        return ConfigSchema.Describe(typeof(T), JsonUtil.JsonSerializerOptionsNoIndent);
    }

    // The label a bare id gets when no resolver handles it. Exactly:
    //   not a 24-hex string            -> null (map codenames, plain words, numbers: nothing)
    //   en locale "<key> Name"         -> item template names AND handbook category names
    //   key is a trader id             -> trader nickname (traders have no locale Name entry)
    //   otherwise                      -> null
    private string? defaultIdLabel(string key)
    {
        if (!looksLikeMongoId(key))
        {
            return null;
        }

        string? localeName = getLocale("en", key, " Name");
        if (!string.IsNullOrWhiteSpace(localeName))
        {
            return localeName;
        }

        MongoId id = new(key);
        if (databaseService.GetTraders().ContainsKey(id))
        {
            return getTraderNickname(id);
        }

        return null;
    }

    private static bool looksLikeMongoId(string value)
    {
        if (value.Length != 24)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}

using System.Collections;
using System.Reflection;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves;

// Server-side verification for the [AllowedIds] config attribute (see CommentedJson/readme.md).
// A mod calls CheckAllowedIds(config) after loading / on a config POST and logs or rejects the
// returned messages.
public partial class Utils
{
    // Walks every [AllowedIds]-tagged member reachable from `config` - dictionary KEYS (including
    // every element dict of a List<Dictionary<MongoId, T>>), and the VALUE of a MongoId leaf or a
    // List<MongoId> - and returns one message per id that satisfies
    // none of that member's specs. An empty list means the config is clean. Reflection plus
    // item-DB parent walks: meant for a load / POST check, not a hot path.
    public List<string> CheckAllowedIds(object? config)
    {
        List<string> problems = new List<string>();
        if (config == null)
        {
            return problems;
        }

        Dictionary<MongoId, TemplateItem> itemDB = databaseService.GetItems();
        HashSet<string> traderIds = new HashSet<string>();
        foreach (MongoId traderId in databaseService.GetTraders().Keys)
        {
            traderIds.Add(traderId.ToString());
        }
        HashSet<object> visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        WalkForAllowedIds(config, "", itemDB, traderIds, problems, visited);
        return problems;
    }

    private void WalkForAllowedIds(
        object? node,
        string path,
        Dictionary<MongoId, TemplateItem> itemDB,
        HashSet<string> traderIds,
        List<string> problems,
        HashSet<object> visited
    )
    {
        if (node == null)
        {
            return;
        }

        Type type = node.GetType();
        if (type.IsPrimitive || node is string || type == typeof(MongoId))
        {
            return;
        }

        // Configs are trees, but guard against a cyclic graph anyway.
        if (!visited.Add(node))
        {
            return;
        }

        if (node is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                WalkForAllowedIds(entry.Value, path + "/<entry>", itemDB, traderIds, problems, visited);
            }
            return;
        }

        if (node is IEnumerable enumerable)
        {
            int index = 0;
            foreach (object? element in enumerable)
            {
                WalkForAllowedIds(element, path + "/#" + index, itemDB, traderIds, problems, visited);
                index++;
            }
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch (Exception)
            {
                continue;
            }
            if (value == null)
            {
                continue;
            }

            string childPath = path + "/" + property.Name;

            List<AllowedIdsAttribute> specs = new List<AllowedIdsAttribute>(property.GetCustomAttributes<AllowedIdsAttribute>());
            if (specs.Count > 0)
            {
                CheckTaggedValue(value, specs, childPath, itemDB, traderIds, problems);
            }

            WalkForAllowedIds(value, childPath, itemDB, traderIds, problems, visited);
        }
    }

    // `value` is whatever an [AllowedIds]-tagged property returned: a Dictionary (check its keys),
    // a List<MongoId> (check each element), or a single MongoId / id string.
    private void CheckTaggedValue(
        object value,
        List<AllowedIdsAttribute> specs,
        string path,
        Dictionary<MongoId, TemplateItem> itemDB,
        HashSet<string> traderIds,
        List<string> problems
    )
    {
        if (value is IDictionary dictionary)
        {
            foreach (object? key in dictionary.Keys)
            {
                CheckOneId(key?.ToString(), specs, path + " key", itemDB, traderIds, problems);
            }
            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (object? element in enumerable)
            {
                // List<Dictionary<MongoId, T>>: the tag constrains each element dict's KEYS.
                if (element is IDictionary elementDictionary)
                {
                    foreach (object? key in elementDictionary.Keys)
                    {
                        CheckOneId(key?.ToString(), specs, path + " key", itemDB, traderIds, problems);
                    }
                    continue;
                }

                CheckOneId(element?.ToString(), specs, path + " entry", itemDB, traderIds, problems);
            }
            return;
        }

        CheckOneId(value.ToString(), specs, path, itemDB, traderIds, problems);
    }

    private void CheckOneId(
        string? id,
        List<AllowedIdsAttribute> specs,
        string where,
        Dictionary<MongoId, TemplateItem> itemDB,
        HashSet<string> traderIds,
        List<string> problems
    )
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        foreach (AllowedIdsAttribute spec in specs)
        {
            if (AllowedIdMatches(id, spec, itemDB, traderIds))
            {
                return;
            }
        }

        string? label = defaultIdLabel(id);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "unknown id";
        }
        problems.Add($"{where}: '{id}' ({label}) is not an allowed id here.");
    }

    private static bool AllowedIdMatches(
        string id,
        AllowedIdsAttribute spec,
        Dictionary<MongoId, TemplateItem> itemDB,
        HashSet<string> traderIds
    )
    {
        if (!MongoId.IsValidMongoId(id))
        {
            return false;
        }

        // Traders live in their own table, not the item DB, so check them before the item lookup.
        if (spec.Kind == AllowedIdKind.Trader)
        {
            return traderIds.Contains(id);
        }

        if (!itemDB.TryGetValue(new MongoId(id), out TemplateItem? template))
        {
            return false;
        }

        if (spec.Kind == AllowedIdKind.Item)
        {
            return template.Type == "Item";
        }
        if (spec.Kind == AllowedIdKind.Node)
        {
            return template.Type == "Node";
        }
        return false;
    }
}

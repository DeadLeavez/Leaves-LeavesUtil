using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace Leaves
{
    // Stateless on purpose: this class is a singleton shared across every mod, so each mod loads its own tier list via loadTierList and passes it into every other call here rather than any mod caching it as shared state.
    [Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
    public class TierList(
        DatabaseService databaseService,
        Utils leavesUtils)
    {
        public Dictionary<int, List<MongoId>> loadTierList(string file)
        {
            var tierList = leavesUtils.loadFile<Dictionary<int, List<MongoId>>>(file);
            var itemDB = databaseService.GetItems();

            // keep only ids that are real items; warn on the rest
            foreach (var tier in tierList.Keys.ToList())
            {
                var currentTier = tierList[tier];
                var validItems = new List<MongoId>();
                foreach (var item in currentTier)
                {
                    if (itemDB.ContainsKey(item))
                    {
                        validItems.Add(item);
                    }
                    else
                    {
                        leavesUtils.printColor($"[LeavesUtils] Found broken item in tierlist: {item}");
                    }
                }
                tierList[tier] = validItems;
            }

            return tierList;
        }

        // Nearest tier by absolute distance. Takes bare tier numbers so callers still building a tier list can reuse it before any items are assigned.
        public int getClosestTier(List<int> tiers, int currentTier)
        {
            int closestDistance = int.MaxValue;
            int closestTier = int.MaxValue;
            foreach (var tier in tiers)
            {
                int tempDistance = Math.Abs(currentTier - tier);
                if (tempDistance < closestDistance)
                {
                    closestDistance = tempDistance;
                    closestTier = tier;
                }
            }
            return closestTier;
        }

        public int getClosestTier(Dictionary<int, List<MongoId>> tierList, int currentTier)
        {
            return getClosestTier(tierList.Keys.ToList(), currentTier);
        }

        public MongoId getRandomItemFromTier(Dictionary<int, List<MongoId>> tierList, int tier)
        {
            if (!tierList.ContainsKey(tier))
            {
                tier = getClosestTier(tierList, tier);
            }

            var items = tierList[tier];
            return items[Random.Shared.Next(items.Count)];
        }

        public int getTierFromID(Dictionary<int, List<MongoId>> tierList, MongoId item)
        {
            foreach (var tier in tierList.Keys)
            {
                if (tier.ToString().Contains(item.ToString()))
                {
                    return tier;
                }
            }
            return -1;
        }
    }
}

using SPTarkov.Server.Core.Models.Common;

namespace Leaves
{
    // One line of a trade's cost: `Count` of item `Tpl`.
    // A trade's cost is a List<TradeEntry> - all of them required together (an AND barter).
    // For a plain cash sale, pass a single entry with a money tpl (e.g. Money.ROUBLES) or a currency-item tpl (USD/EUR/GP coin).
    public class TradeEntry
    {
        public MongoId Tpl;
        public double Count;

        public TradeEntry(MongoId tpl, double count)
        {
            Tpl = tpl;
            Count = count;
        }
    }
}

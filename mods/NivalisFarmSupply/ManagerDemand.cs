using System;
using System.Collections.Generic;

namespace NivalisFarmSupply;

// Supported game's manager policy: most recent trading hour in the last day,
// then matching recipe servings during the four hours preceding that hour.
public static class ManagerDemand
{
    public readonly record struct Sale(float Hours, int Count, bool MatchesRecipe);

    public static int Servings(float now, IReadOnlyList<Sale> sales)
    {
        float latest = 0;
        if (!float.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        foreach (var sale in sales)
        {
            if (!float.IsFinite(sale.Hours) || sale.Count < 0)
                throw new ArgumentOutOfRangeException(nameof(sales));
            if (now - sale.Hours < 24 && sale.Hours > latest)
                latest = sale.Hours;
        }
        if (latest == 0) return 0;
        long result = 0;
        foreach (var sale in sales)
            if (latest - sale.Hours <= 4 && sale.MatchesRecipe)
                result += sale.Count;
        if (result > int.MaxValue) throw new OverflowException("Restaurant demand exceeds signed range.");
        return (int)result;
    }

    public static int Missing(int occurrences, int servings, int stored, int incoming)
    {
        if (occurrences <= 0 || servings < 0 || stored < 0 || incoming < 0)
            throw new ArgumentOutOfRangeException("Invalid manager stock data.");
        long target = (long)occurrences * (servings == 0 ? 5 : servings);
        long missing = Math.Max(0, target - stored - incoming);
        if (missing > int.MaxValue) throw new OverflowException("Ingredient demand exceeds signed range.");
        return (int)missing;
    }

    // Later builds also restock any ingredient whose stock plus pending deliveries
    // is below a fixed level (10 observed live), before recipe demand is short.
    public static int TopUp(int threshold, int stored, int incoming)
    {
        if (threshold < 0 || stored < 0 || incoming < 0)
            throw new ArgumentOutOfRangeException("Invalid restock data.");
        return (int)Math.Max(0, (long)threshold - stored - incoming);
    }

    public static bool HasRoom(int? capacity, int count, int needed)
    {
        if (count < 0 || needed < 0 || capacity < 0) return false;
        return !capacity.HasValue || (long)count + needed <= capacity.Value;
    }
}

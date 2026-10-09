using System;

namespace NivalisFarmSupply;

// Since Update #4 each venue sets its own supply target (Lean, Normal, Extra or
// Recent Sales), read natively through VenueAreaGhost.GetSupplyTarget. Farm supply
// covers the gap between that target and stock plus pending deliveries.
public static class ManagerDemand
{
    public static int Shortfall(int target, int stored, int incoming)
    {
        if (target < 0 || stored < 0 || incoming < 0)
            throw new ArgumentOutOfRangeException("Invalid manager stock data.");
        return (int)Math.Max(0, (long)target - stored - incoming);
    }

    // A harvest is a whole unit (about 12 items). Small gaps can be left to
    // vendors so slow venues do not overstock; minimum 1 harvests any gap.
    public static bool ShouldHarvest(int shortfall, int minimum)
    {
        if (shortfall < 0 || minimum < 1) throw new ArgumentOutOfRangeException("Invalid harvest threshold.");
        return shortfall >= minimum;
    }

    public static bool HasRoom(int? capacity, int count, int needed)
    {
        if (count < 0 || needed < 0 || capacity < 0) return false;
        return !capacity.HasValue || (long)count + needed <= capacity.Value;
    }
}

using Cheeze.Managed;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Nivalis;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using Nivalis.Player;
using Nivalis.Locale.UI;

namespace NivalisFarmSupply;

[BepInPlugin(Guid, "Farm First Manager Supply", "0.4.0")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "cheeze.nivalis.farmsupply";
    private const string SupportedBinary = "B0AFDE3FFA5E0707E927360D496E4FEEC6810F9B405BB79D32E7170788F12A07";
    private const string SupportedEarlierBinary = "D7D7FEF8B76699AE6A9F70B111C239B013BA00261A02A85BDB394BDE38C46504";
    internal static Plugin Instance = null!;
    private ConfigEntry<bool> enabled = null!;
    private ConfigEntry<bool> verbose = null!;
    private ConfigEntry<bool> bestEffort = null!;
    private ConfigEntry<int> minimumShortfall = null!;
    private DateTime configStamp;
    private static bool supplying;
    private static bool inactive;
    private static bool supplyFaulted;
    private static bool hookObserved;
    private ConfigEntry<bool> showReceipts = null!;
    private readonly FarmSupplyLedger ledger = new();
    private readonly HashSet<string> skippedModules = new(StringComparer.Ordinal);
    private SaveSnapshotStore receiptStore = null!;
    private Il2CppSystem.Action afterLoad = null!;
    private readonly SaveSnapshotLoad receiptLoad = new();
    private string? pendingPath;
    private (IntPtr Player, IntPtr Items) world;
    private sealed class SaveContext(string name)
    {
        public readonly string Name = name;
        public string? Snapshot;
    }

    public override void Load()
    {
        Instance = this;
        Harmony? harmony = null;
        try
        {
            enabled = Config.Bind("Supply", "Enabled", true, "Supply managers from ripe crops in player-owned farms, then replant.");
            // [Supply] RestockThreshold (0.3.0) is retired: each venue's in-game supply
            // target now sets the restock level. The unbound entry is left untouched.
            minimumShortfall = Config.Bind("Supply", "MinimumShortfall", 1, new ConfigDescription(
                "Harvest only when a venue is short at least this many of an ingredient; smaller gaps are left to the manager's vendors. "
                + "Raise it to avoid whole harvests for small top-ups. Saving the file applies it at the next restock.",
                new AcceptableValueRange<int>(1, 1000)));
            verbose = Config.Bind("Debug", "Verbose", false, "Log farm demand and the manager's purchases for each recipe.");
            showReceipts = Config.Bind("Receipts", "ShowFarmSupply", true, "Add zero-cost farm-supply entries to venue finance details.");
            bestEffort = Config.Bind("Compatibility", "BestEffortAfterUpdates", true,
                "Try regenerated bindings on unverified game builds; disable farm supply after a compatibility failure.");
            configStamp = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
            harmony = new Harmony(Guid);
            LoadCompatible(harmony);
        }
        catch (Exception error)
        {
            inactive = true;
            try { harmony?.UnpatchSelf(); }
            catch (Exception rollback) { Log.LogWarning("Hook cleanup failed; remaining hooks are inactive. " + rollback.Message); }
            Log.LogError("Farm First could not initialize and is inactive. Normal gameplay continues. " + error);
        }
    }

    private void LoadCompatible(Harmony harmony)
    {
        string binary = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(binary);
        string digest = Convert.ToHexString(sha.ComputeHash(stream));
        bool verified = digest == SupportedBinary || digest == SupportedEarlierBinary;
        if (!verified && !bestEffort.Value)
        {
            Log.LogError($"Unsupported GameAssembly SHA256 {digest}. Farm supply is inactive; vendor purchasing is unchanged.");
            return;
        }
        if (!verified) Log.LogWarning($"Unverified GameAssembly SHA256 {digest}; attempting best-effort compatibility with regenerated bindings.");
        ManagedApiCompatibility.Validate(typeof(Plugin).Assembly);
        receiptStore = new SaveSnapshotStore(Path.Combine(Paths.ConfigPath, Guid, "receipts"));
        var method = AccessTools.DeclaredMethod(typeof(VenueAreaGhost), "TryPurchaseIngredients",
            new[] { typeof(IRecipe), typeof(float) });
        if (method == null) throw new MissingMethodException("VenueAreaGhost.TryPurchaseIngredients");
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePurchase)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterPurchase)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LocaleFinanceOverviewGainsAndCostsPanel), "Show"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterFinanceShow)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(SerializationManager), "Save"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSave)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSave)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(SerializationManager), "Load"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeLoad)));
        afterLoad = (Il2CppSystem.Action)(Action)AfterLoadCompleted;
        SerializationManager.OnPostLoad.Add(afterLoad);
        Log.LogInfo("Farm First Manager Supply 0.4.0 loaded. Venue supply targets; full harvests -> venue stock, shared produce/planting items supported; replant recovery and independent delivery receipts.");
    }

    private sealed class PurchaseTrace(string venue, string recipe, string setting, float barter)
    {
        public readonly string Venue = venue, Recipe = recipe, Setting = setting;
        public readonly float Barter = barter;
        public readonly List<(ItemType Type, int Target, int Stock, int Incoming)> Inputs = new();
    }

    private static bool BeforePurchase(VenueAreaGhost __instance, IRecipe recipe,
        float barterMultiplier, ref PurchaseTrace? __state)
    {
        SupplyFromFarms(__instance, recipe);
        __state = TracePurchase(__instance, recipe, barterMultiplier);
        return true;
    }

    // Read-only diagnostics: venue stock after farm supply, and what native
    // purchasing added for this recipe. Shows when the manager's rule moves.
    private static PurchaseTrace? TracePurchase(VenueAreaGhost venue, IRecipe recipe, float barter)
    {
        if (inactive || supplying || !Instance.verbose.Value || Instance.receiptLoad.Active) return null;
        try
        {
            if (!venue.PlayerOwned || recipe == null || venue.JointInventory == null) return null;
            int mealTarget = MealSupplyTarget(venue, recipe);
            var trace = new PurchaseTrace(venue.Venue.GetName(), recipe.Output.type.name,
                $"{venue.SupplyTarget}, mealTarget={mealTarget}, budget={venue.CurrentRestockBudget}/{venue.DailyRestockBudget}", barter);
            var incoming = Incoming(venue);
            var seen = new HashSet<IntPtr>();
            var inputs = recipe.Inputs;
            for (int i = 0; i < inputs.Length; i++)
            {
                var type = inputs[i].DefaultItem;
                if (type == null || !seen.Add(type.Pointer)) continue;
                trace.Inputs.Add((type, Math.Max(venue.GetSupplyTarget(type), mealTarget), venue.JointInventory.GetItemCount(type),
                    incoming.TryGetValue(type.Pointer, out int count) ? count : 0));
            }
            return trace;
        }
        catch (Exception error) { Instance.Log.LogWarning("Manager purchase trace unavailable. " + error.Message); return null; }
    }

    private static void AfterPurchase(VenueAreaGhost __instance, bool __result, PurchaseTrace? __state)
    {
        if (inactive || __state == null) return;
        try
        {
            var incoming = Incoming(__instance);
            var parts = new List<string>();
            foreach (var input in __state.Inputs)
            {
                int stock = __instance.JointInventory.GetItemCount(input.Type);
                int pending = incoming.TryGetValue(input.Type.Pointer, out int count) ? count : 0;
                parts.Add($"{input.Type.name}[target={input.Target}, stock={input.Stock}, incoming={input.Incoming}, "
                    + $"bought={pending - input.Incoming + stock - input.Stock}]");
            }
            Instance.Log.LogInfo($"Manager purchase: venue={__state.Venue}, recipe={__state.Recipe}, "
                + $"supply={__state.Setting}, barter={__state.Barter}, result={__result}: {string.Join("; ", parts)}");
        }
        catch (Exception error) { Instance.Log.LogWarning("Manager purchase trace failed. " + error.Message); }
    }

    // Apply settings saved while the game runs, before the next restock decision.
    private void ReloadConfigIfChanged()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
            if (stamp == configStamp) return;
            Config.Reload();
            // Reloading can rewrite the file; take the stamp afterwards so one save
            // produces one reload.
            configStamp = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
            Log.LogInfo($"Settings reloaded: Enabled={enabled.Value}, MinimumShortfall={minimumShortfall.Value}, Verbose={verbose.Value}.");
        }
        catch (Exception error) { Log.LogWarning("Settings reload failed; previous values stay in effect. " + error.Message); }
    }

    private static void SupplyFromFarms(VenueAreaGhost venue, IRecipe recipe)
    {
        if (!inactive) Instance.ReloadConfigIfChanged();
        if (inactive || supplyFaulted || !Instance.enabled.Value) return;
        if (Instance.receiptLoad.Active) return;
        if (supplying) return;
        try
        {
            if (!venue.PlayerOwned || !Singleton<PlayerManager>.InstanceExist() || !Singleton<GreenhouseManager>.InstanceExist()) return;
            if (!hookObserved)
            {
                hookObserved = true;
                Instance.Log.LogInfo("Player venue purchasing hook invoked.");
            }
            supplying = true;
            Instance.Supply(venue, recipe);
        }
        catch (Exception error)
        {
            supplyFaulted = true;
            if (error is MissingMemberException or TypeLoadException or BadImageFormatException
                or InvalidProgramException or EntryPointNotFoundException or AccessViolationException)
                inactive = true;
            Instance.Log.LogError("Farm supply disabled after an unexpected error. Normal vendor purchasing continues. "
                + (inactive ? "Compatibility callbacks are inactive. " : "Completed delivery receipts remain available. ") + error);
        }
        finally { supplying = false; }
    }

    private void Supply(VenueAreaGhost venue, IRecipe recipe)
    {
        bool receiptsAvailable = false;
        try { ObserveWorld(); receiptsAvailable = !receiptLoad.Quarantined; }
        catch (Exception error) { Log.LogWarning("Farm receipt world identity unavailable; supply continues. " + error.Message); }
        var player = Singleton<PlayerManager>.Instance.LocalPlayer;
        if (player == null || recipe == null || venue.JointInventory == null) return;
        var inventory = player.Inventory;
        var playerItems = inventory.Items;
        var inputs = new Dictionary<IntPtr, ItemType>();
        var recipeInputs = recipe.Inputs;
        for (int i = 0; i < recipeInputs.Length; i++)
        {
            var type = recipeInputs[i].DefaultItem;
            if (type != null) inputs[type.Pointer] = type;
        }
        // The venue's supply target (Lean, Normal, Extra or Recent Sales) is the
        // manager's own restock level. Include waiting and delayed deliveries so
        // farm supply does not duplicate items already on their way.
        var incoming = Incoming(venue);
        int mealTarget = MealSupplyTarget(venue, recipe);
        foreach (var type in inputs.Values)
        {
            int target = Math.Max(venue.GetSupplyTarget(type), mealTarget);
            int stored = venue.JointInventory.GetItemCount(type);
            int pending = incoming.TryGetValue(type.Pointer, out int count) ? count : 0;
            int missing = ManagerDemand.Shortfall(target, stored, pending);
            bool harvest = ManagerDemand.ShouldHarvest(missing, minimumShortfall.Value);
            if (verbose.Value) Log.LogInfo($"Farm demand: {type.name}, missing={missing}, target={target}, stock={stored}, incoming={pending}"
                + (missing > 0 && !harvest ? $", below minimum shortfall {minimumShortfall.Value}; left to vendors." : "."));
            if (!harvest) continue;
            var greenhouseManager = Singleton<GreenhouseManager>.Instance;
            var farms = greenhouseManager.Greenhouses;
            bool skipIngredient = false;
            for (int f = 0; f < farms.Length && missing > 0 && !skipIngredient; f++)
            {
                var farm = farms[f];
                if (farm == null || !farm.PlayerOwned) continue;
                var area = greenhouseManager.GetArea(farm);
                if (area == null || area.Modules == null) continue;
                for (int m = 0; m < area.Modules.Count && missing > 0; m++)
                {
                    var module = area.Modules[m].Value;
                    if (module == null || !module.ReadyForHarvest || !Same(module.PlantType, type)) continue;
                    string moduleId = module.Id;
                    if (string.IsNullOrWhiteSpace(moduleId)) throw new InvalidOperationException("Farm unit identity is unavailable.");
                    if (skippedModules.Contains(moduleId)) continue;
                    var plant = module.PlantType;
                    var seed = plant.Seeds;
                    bool sharedPlantingType = Same(plant, seed);
                    int yield = area.CalculateYield(module);
                    var destination = plant.RequiresRefridgeration
                        ? venue.JointInventory.RefridgeratedInventory : venue.JointInventory.NormalInventory;
                    if (seed == null || yield <= 0 || !CanHarvest(playerItems, plant, seed, yield)
                        || !CanAccept(destination, plant, 1))
                    {
                        if (verbose.Value) Log.LogInfo("Skipping ripe crop: seed or inventory capacity unavailable.");
                        continue;
                    }
                    var oldItems = Snapshot(playerItems, plant);
                    var oldSeeds = Snapshot(playerItems, seed);
                    string? receiptVenue = null, receiptIngredient = null, receiptName = null;
                    int receiptDay = 0;
                    bool receiptPrepared = false;
                    try
                    {
                        if (receiptsAvailable)
                        {
                            receiptVenue = venue.Venue.Guid;
                            receiptIngredient = plant.Guid;
                            receiptName = plant.Name;
                            receiptDay = TimeOfDayManager.GameplayGameDay;
                            if (string.IsNullOrWhiteSpace(receiptVenue) || string.IsNullOrWhiteSpace(receiptIngredient)
                                || string.IsNullOrWhiteSpace(receiptName) || receiptDay < 0) throw new FormatException("Invalid receipt identity.");
                            receiptPrepared = true;
                        }
                    }
                    catch (Exception error) { Log.LogWarning("Farm receipt identity unavailable; supply continues. " + error.Message); }
                    Log.LogInfo($"Harvest preflight passed: {plant.name}, yield={yield}, needed={missing}.");
                    if (!module.TryHarvestCrop(area, player))
                    {
                        if (!module.ReadyForHarvest) throw new InvalidOperationException("Harvest failed after changing crop state.");
                        continue;
                    }
                    var produceAfterHarvest = Snapshot(playerItems, plant);
                    var seedsAfterHarvest = Snapshot(playerItems, seed);
                    var observation = HarvestBatch.Observe(yield, sharedPlantingType,
                        oldItems, produceAfterHarvest, oldSeeds, seedsAfterHarvest);
                    string seedReturn = observation.SeedReturns?.ToString() ?? "unknown";
                    Log.LogInfo($"Harvest observed: {plant.name}, expectedYield={yield}, addedItems={observation.AddedItems}, plantingReturns={seedReturn}, sharedPlantingType={sharedPlantingType}.");
                    var replant = HarvestRecovery.Replant(
                        () => Snapshot(playerItems, seed), () => module.Planted,
                        () => Same(module.PlantType, plant), () => module.ReadyForHarvest,
                        () => module.TryPlantCrop(plant, inventory));
                    var available = HarvestBatch.AfterReplant(sharedPlantingType, replant.Replanted,
                        oldItems, produceAfterHarvest, Snapshot(playerItems, plant));
                    if (!observation.MatchesExpected || !replant.Replanted)
                    {
                        skippedModules.Add(moduleId);
                        skipIngredient = true;
                        Log.LogWarning($"Farm harvest skipped: {plant.name}, unit={moduleId}, expectedYield={yield}, addedItems={observation.AddedItems}, plantingReturns={seedReturn}, sharedPlantingType={sharedPlantingType}, replanted={replant.Replanted}, replantReason={replant.Reason}. No venue delivery; observed produce stays in player inventory. Unit skipped until reload; farm supply remains active and vendors cover shortages.");
                        break;
                    }
                    var fresh = NewItems(playerItems, plant, oldItems);
                    var currentNewIds = new HashSet<IntPtr>();
                    foreach (var item in fresh) currentNewIds.Add(item.Pointer);
                    if (!currentNewIds.SetEquals(available))
                        throw new InvalidOperationException("Harvest batch ownership changed before venue transfer.");
                    var sourcePort = new ContainerPort(playerItems);
                    var targetPort = new ContainerPort(destination);
                    int transferred = ItemTransfer.MoveAvailable(sourcePort, targetPort, fresh,
                        item => CanAccept(destination, plant, 1), item =>
                    {
                        missing = Math.Max(0, missing - 1);
                        try
                        {
                            if (receiptPrepared) ledger.Record(receiptVenue!, receiptIngredient!, receiptName!, receiptDay, 1);
                        }
                        catch (Exception error) { Log.LogWarning("Farm receipt recording failed; inventory transfer succeeded. " + error.Message); }
                    });
                    Log.LogInfo($"Farm harvest: {plant.name}, yield={yield}, availableAfterReplant={fresh.Count}, supplied={transferred}, playerOverflow={fresh.Count - transferred}, replanted=true, seedsConsumed=1.");
                }
            }
        }
    }

    private (IntPtr Player, IntPtr Items) CurrentWorld()
    {
        if (!Singleton<PlayerManager>.InstanceExist()) return default;
        var player = Singleton<PlayerManager>.Instance.LocalPlayer;
        return player == null ? default : (player.Pointer, player.Inventory.Items.Pointer);
    }

    private void ObserveWorld()
    {
        var observed = CurrentWorld();
        if (observed == default) return;
        if (world != default && world != observed)
        {
            ledger.Clear();
            skippedModules.Clear();
        }
        world = observed;
    }

    private static void AfterFinanceShow(LocaleFinanceOverviewGainsAndCostsPanel __instance,
        int fromDay, int toDayExclusive)
    {
        var mod = Instance;
        if (inactive) return;
        try
        {
            if (!mod.showReceipts.Value || mod.receiptLoad.Active || mod.receiptLoad.Quarantined || __instance._venue == null
                || __instance.gainsOnlyToggle.isOn) return;
            mod.ObserveWorld();
            foreach (var row in mod.ledger.Rows(__instance._venue.Guid, fromDay, toDayExclusive))
                __instance.AddInstance($"{row.Name} — farm supply ({row.Count})", 0);
        }
        catch (Exception error) { mod.Log.LogWarning("Farm receipt display failed; native statistics are unchanged. " + error.Message); }
    }

    private static void BeforeSave(string saveName, ref SaveContext? __state)
    {
        if (inactive) return;
        __state = new SaveContext(saveName);
        try
        {
            if (Instance.receiptLoad.Active || Instance.receiptLoad.Quarantined) return;
            Instance.ObserveWorld();
            __state.Snapshot = Instance.ledger.Snapshot();
        }
        catch (Exception error) { Instance.Log.LogWarning("Farm receipt snapshot failed; native saving continues. " + error.Message); }
    }

    private static void AfterSave(bool __result, SaveContext? __state)
    {
        if (inactive) return;
        try
        {
            if (__result && __state?.Snapshot != null)
            {
                Instance.receiptStore.Write(ResolveSavePath(__state.Name), __state.Snapshot);
                Instance.Log.LogInfo("Farm supply receipts saved with the completed game snapshot.");
            }
        }
        catch (Exception error) { Instance.Log.LogWarning("Farm receipt history was not saved; the game save is unchanged. " + error.Message); }
    }

    private static string ResolveSavePath(string saveName)
    {
        if (Path.GetFileName(saveName) != saveName) throw new IOException("Save name is not a local filename.");
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { saveName, saveName + ".sav" })
        {
            string candidate = Path.Combine(UnityEngine.Application.persistentDataPath, name);
            if (File.Exists(candidate)) candidates.Add(candidate);
        }
        if (candidates.Count != 1) throw new IOException("Save path is unavailable or ambiguous.");
        foreach (string candidate in candidates) return candidate;
        throw new IOException("Save path is unavailable.");
    }

    private static void BeforeLoad(string saveName)
    {
        if (inactive) return;
        var mod = Instance;
        mod.receiptLoad.Begin();
        mod.ledger.Clear();
        mod.skippedModules.Clear();
        mod.world = default;
        mod.pendingPath = null;
        if (mod.receiptLoad.Quarantined) return;
        try
        {
            mod.pendingPath = ResolveSavePath(saveName);
            string hash = SaveSnapshotStore.Hash(mod.pendingPath!);
            mod.receiptLoad.Stage(hash, mod.receiptStore.Read(hash));
        }
        catch (Exception error) { mod.Log.LogWarning("Farm receipt history unavailable for this load. " + error.Message); }
    }

    private void AfterLoadCompleted()
    {
        if (inactive) return;
        try
        {
            ledger.Clear();
            string? snapshot = receiptLoad.Complete(pendingPath == null ? null : SaveSnapshotStore.Hash(pendingPath));
            if (snapshot != null)
            {
                ledger.Restore(snapshot);
                Log.LogInfo("Farm supply receipts restored for the loaded game snapshot.");
            }
        }
        catch (Exception error) { ledger.Clear(); Log.LogWarning("Farm receipt history was not restored. " + error.Message); }
        finally
        {
            pendingPath = null;
            receiptLoad.Complete(null);
            try { world = CurrentWorld(); } catch { world = default; }
        }
    }

    // Under Recent Sales the manager restocks each recipe's ingredients to that
    // meal's recent sales (minimum 2); the game counts sales by meal, so an
    // ingredient alone only yields the minimum. Fixed settings give one value.
    private static int MealSupplyTarget(VenueAreaGhost venue, IRecipe recipe)
    {
        var meal = recipe.Output?.type;
        return meal == null ? 0 : venue.GetSupplyTarget(meal);
    }

    private static Dictionary<IntPtr, int> Incoming(VenueAreaGhost venue)
    {
        var result = new Dictionary<IntPtr, int>();
        var seenTasks = new HashSet<IntPtr>();
        var seenItems = new HashSet<IntPtr>();
        void AddItems(Il2CppSystem.Collections.Generic.Dictionary<ItemType, Il2CppSystem.Collections.Generic.List<ItemInstanceData>> items)
        {
            if (items == null) return;
            foreach (var pair in items)
            {
                int added = 0;
                for (int i = 0; i < pair.Value.Count; i++)
                    if (seenItems.Add(pair.Value[i].Pointer)) added++;
                result.TryGetValue(pair.Key.Pointer, out int prior);
                result[pair.Key.Pointer] = checked(prior + added);
            }
        }
        void AddTask(StaffTaskQueue.StaffTask task)
        {
            if (task == null || !seenTasks.Add(task.Pointer)) return;
            var delivery = task.TryCast<StaffTaskQueue.DeliverIngredientsTask>();
            if (delivery != null && !delivery.IsCompleted && !delivery.IsObsolete) AddItems(delivery.Items);
        }
        var queue = venue.TaskQueue;
        if (queue != null)
        {
            for (int i = 0; i < queue.Queue.Count; i++) AddTask(queue.Queue[i]);
            for (int i = 0; i < queue.QueueWaiting.Count; i++) AddTask(queue.QueueWaiting[i]);
            for (int i = 0; i < queue.DelayedQueue.Count; i++) AddTask(queue.DelayedQueue[i].Task);
        }
        // Update #4 removed the per-pass shopping list; items bought earlier in the
        // same BuyIngredients pass are visible only once queued as a delivery task.
        return result;
    }

    private static bool Same(Il2CppObjectBase? a, Il2CppObjectBase? b) =>
        a != null && b != null && a.Pointer == b.Pointer;

    private static List<ItemStack> Stacks(ItemContainer container, ItemType type)
    {
        // The native enumerator is a boxed LinkedList<T>.Enumerator. Calling its
        // inherited interface through IL2CPP interop failed in live gameplay.
        // Read the game's keyed node index instead, including every stack for
        // this type, and finish the managed snapshot before mutating inventory.
        var result = new List<ItemStack>();
        if (!container.TypeMap.TryGetValues(type, out var nodes) || nodes == null) return result;
        var ownerList = container._items;
        int ownerVersion = ownerList.version;
        int version = nodes._version;
        int count = nodes.Count;
        var seenNodes = new HashSet<IntPtr>();
        var seenStacks = new HashSet<IntPtr>();
        for (int i = 0; i < count; i++)
        {
            var node = nodes[i];
            if (node == null || !Same(node.List, ownerList) || !seenNodes.Add(node.Pointer))
                throw new InvalidOperationException("Inventory node index is inconsistent.");
            var stack = node.Value;
            if (stack == null || !Same(stack.Type, type) || !seenStacks.Add(stack.Pointer))
                throw new InvalidOperationException("Inventory type index is inconsistent.");
            result.Add(stack);
        }
        if (nodes.Count != count || nodes._version != version || ownerList.version != ownerVersion)
            throw new InvalidOperationException("Inventory index changed while taking a snapshot.");
        return result;
    }

    private static HashSet<IntPtr> Snapshot(ItemContainer container, ItemType type)
        => IdentitySnapshot.Capture(InstancePointers(container, type));

    private static IEnumerable<IntPtr> InstancePointers(ItemContainer container, ItemType type)
    {
        foreach (var stack in Stacks(container, type))
            if (Same(stack.Type, type))
                for (int i = 0; i < stack.Instances.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<ItemInstanceData>>().Count; i++)
                {
                    var item = stack.Instances[i];
                    if (item == null || !Same(item.Type, type))
                        throw new InvalidOperationException("Inventory instance identity or type is inconsistent.");
                    yield return item.Pointer;
                }
    }

    private static List<ItemInstanceData> NewItems(ItemContainer container, ItemType type, HashSet<IntPtr> before)
    {
        var result = new List<ItemInstanceData>();
        var seen = new HashSet<IntPtr>();
        foreach (var stack in Stacks(container, type))
            if (Same(stack.Type, type))
                for (int i = 0; i < stack.Instances.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<ItemInstanceData>>().Count; i++)
                {
                    var item = stack.Instances[i];
                    if (!seen.Add(item.Pointer)) throw new InvalidOperationException("Duplicate item identity in inventory snapshot.");
                    if (!before.Contains(item.Pointer)) result.Add(item);
                }
        return result;
    }

    private static bool CanAccept(ItemContainer container, ItemType type, int count) =>
        container != null && container.AcceptsItemType(type)
        && (container.Restriction == null || container.Restriction.CanAddItem(container, type, count));

    private static bool CanHarvest(ItemContainer container, ItemType crop, ItemType seed, int yield)
    {
        if (Same(crop, seed))
        {
            if (!CanAccept(container, crop, checked(yield + 2))) return false;
        }
        else if (!CanAccept(container, crop, yield) || !CanAccept(container, seed, 2)) return false;
        var restriction = container.Restriction;
        if (restriction == null) return true;
        // Individual preflights share the same starting capacity. Reserve combined
        // item and slot use before native harvest, whose add results are ignored.
        var maxItems = restriction.MaxItems;
        var maxSlots = restriction.MaxSlots;
        // TypeMap allows multiple stacks per type. Existing items do not prove
        // that this harvest can merge, so reserve two free slots conservatively.
        const int extraSlots = 2;
        return ManagerDemand.HasRoom(maxItems.HasValue ? maxItems.Value : null, container.ItemCount, checked(yield + 2))
            && ManagerDemand.HasRoom(maxSlots.HasValue ? maxSlots.Value : null, container.StackCount, extraSlots);
    }

    private sealed class ContainerPort(ItemContainer container) : IItemPort<ItemInstanceData>
    {
        public bool Contains(ItemInstanceData item) => Snapshot(container, item.Type).Contains(item.Pointer);
        public bool Remove(ItemInstanceData item) => container.TryTake(item);
        public bool Add(ItemInstanceData item) => container.TryAdd(item) == ItemCollectionOperationResult.Succeeded;
    }
}

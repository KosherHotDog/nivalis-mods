using Cheeze.Managed;
using System;
using System.Collections.Generic;
using System.IO;
using NivalisFarmSupply;

int checks = 0;
void Equal<T>(T expected, T actual, string name)
{
    checks++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{name}: expected {expected}, actual {actual}");
}
void Throws(Action action, string name)
{
    checks++;
    try { action(); } catch (Exception) { return; }
    throw new Exception(name + ": expected refusal");
}
Equal(0, ManagerDemand.Servings(100, Array.Empty<ManagerDemand.Sale>()), "no sales");
Equal(0, ManagerDemand.Servings(100, new[] { new ManagerDemand.Sale(76, 7, true) }), "24 hour boundary");
Equal(7, ManagerDemand.Servings(100, new[] { new ManagerDemand.Sale(76.1f, 7, true) }), "within day");
Equal(5, ManagerDemand.Servings(100, new[] {
    new ManagerDemand.Sale(99, 2, true), new ManagerDemand.Sale(95, 3, true),
    new ManagerDemand.Sale(94.9f, 20, true), new ManagerDemand.Sale(99, 100, false)
}), "four hour inclusive window and recipe match");
Equal(0, ManagerDemand.Servings(100, new[] {
    new ManagerDemand.Sale(99, 100, false), new ManagerDemand.Sale(94, 7, true)
}), "latest hour is across all recipes");
Equal(10, ManagerDemand.Missing(2, 0, 0, 0), "five servings fallback, repeated input");
Equal(7, ManagerDemand.Missing(2, 7, 4, 3), "stock and deliveries deducted");
Equal(0, ManagerDemand.Missing(1, 5, 3, 10), "excess pending stock");
Equal(0, ManagerDemand.Missing(1, 0, 5, 0), "exactly satisfied");
Throws(() => ManagerDemand.Missing(int.MaxValue, 2, 0, 0), "target overflow");
Throws(() => ManagerDemand.Missing(1, 1, -1, 0), "invalid stock");
Throws(() => ManagerDemand.Servings(float.NaN, Array.Empty<ManagerDemand.Sale>()), "invalid clock");
Throws(() => ManagerDemand.Servings(100, new[] { new ManagerDemand.Sale(99, -1, true) }), "invalid receipt");
Equal(4, ManagerDemand.TopUp(10, 6, 0), "below restock point tops up to threshold");
Equal(1, ManagerDemand.TopUp(10, 9, 0), "one below restock point");
Equal(0, ManagerDemand.TopUp(10, 10, 0), "restock point itself is not short");
Equal(0, ManagerDemand.TopUp(10, 5, 10), "pending deliveries satisfy restock point");
Equal(0, ManagerDemand.TopUp(0, 0, 0), "zero threshold disables top-up");
Equal(10, ManagerDemand.TopUp(10, 0, 0), "empty stock");
Throws(() => ManagerDemand.TopUp(-1, 0, 0), "invalid threshold");
Throws(() => ManagerDemand.TopUp(10, -1, 0), "invalid top-up stock");
Equal(4, Math.Max(ManagerDemand.Missing(1, 4, 6, 0), ManagerDemand.TopUp(10, 6, 0)),
    "live Ramen Noir Chicken: preemptive restock triggers farm supply");
Equal(0, Math.Max(ManagerDemand.Missing(1, 4, 16, 0), ManagerDemand.TopUp(10, 16, 0)),
    "stocked ingredient not harvested");
Equal(false, ManagerDemand.HasRoom(10, 7, 4), "combined crop and seeds exceed room");
Equal(true, ManagerDemand.HasRoom(10, 7, 3), "exact fit");
Equal(true, ManagerDemand.HasRoom(null, int.MaxValue, 2), "unlimited capacity");
Equal(false, ManagerDemand.HasRoom(int.MaxValue, int.MaxValue, 2), "capacity addition overflow");
Equal(false, ManagerDemand.HasRoom(1, 0, 2), "two new slots do not fit");

var source = new Port(42);
var target = new Port();
Equal(true, ItemTransfer.Move(source, target, 42), "normal transfer");
Equal(false, source.Contains(42), "source lost moved item");
Equal(true, target.Contains(42), "target has moved item");
Throws(() => ItemTransfer.Move(source, target, 42), "prevent duplicate movement");
source = new Port(42); target = new Port { Reject = true };
Equal(false, ItemTransfer.Move(source, target, 42), "full destination restored");
Equal(true, source.Contains(42), "rejected item remains owned");
source = new Port(42); target = new Port { ThrowAfterAdd = true };
Equal(true, ItemTransfer.Move(source, target, 42), "throw after commit recognized");
Equal(false, source.Contains(42), "no duplication after commit error");
source = new Port(42) { ThrowAfterRemove = true }; target = new Port();
Equal(false, ItemTransfer.Move(source, target, 42), "remove exception restores");
Equal(true, source.Contains(42), "ownership restored after remove error");
source = new Port(42); target = new Port { WrongResultAfterAdd = true };
Equal(true, ItemTransfer.Move(source, target, 42), "actual ownership overrides false result");
source = new Port(42) { Reject = true }; target = new Port { Reject = true };
Throws(() => ItemTransfer.Move(source, target, 42), "failed restoration refuses continuation");

foreach (int capacity in new[] { 0, 1, 2, 4 })
{
    source = new Port(10, 11, 12, 13); target = new Port();
    int recorded = 0;
    int moved = ItemTransfer.MoveAvailable(source, target, new[] { 10, 11, 12, 13 },
        item => target.Count < capacity, item => recorded++);
    Equal(capacity, moved, "batch fills available venue room");
    Equal(capacity, recorded, "records only committed items");
    Equal(4 - capacity, source.Count, "overflow remains owned by player");
    Equal(capacity, target.Count, "all fitting surplus goes to venue");
}
source = new Port(10, 11); target = new Port { Reject = true };
int rejectionRecords = 0;
Equal(0, ItemTransfer.MoveAvailable(source, target, new[] { 10, 11 }, _ => true, _ => rejectionRecords++), "batch rejection stops");
Equal(0, rejectionRecords, "rejected batch has no receipt");
Equal(2, source.Count, "rejected batch preserves inventory");

var ledger = new FarmSupplyLedger();
ledger.Record("venueA", "beef", "Beef", 7, 12);
ledger.Record("venueA", "beef", "Beef", 7, 2);
ledger.Record("venueA", "beef", "Beef", 8, 3);
ledger.Record("venueB", "beef", "Beef", 7, 100);
ledger.Record("venueA", "pork", "Pork", 7, 4);
Equal(14L, ledger.Rows("venueA", 7, 8)[0].Count, "today respects venue and half-open day range");
Equal(17L, ledger.Rows("venueA", 0, 9)[0].Count, "long range aggregates ingredient across days");
Equal(2, ledger.Rows("venueA", 7, 8).Count, "one row per ingredient");
Equal(0, ledger.Rows("venueA", 9, 10).Count, "unrelated days excluded");
string snapshot = ledger.Snapshot();
ledger.Clear(); ledger.Restore(snapshot);
Equal(snapshot, ledger.Snapshot(), "receipt snapshot round trip");
Throws(() => ledger.Restore("[{\"Venue\":\"\",\"Ingredient\":\"beef\",\"Name\":\"Beef\",\"Day\":7,\"Count\":1}]"), "invalid receipt rejected");
Equal(snapshot, ledger.Snapshot(), "invalid restore leaves prior receipts intact");
Throws(() => ledger.Record("venueA", "beef", "Beef", 7, 0), "no receipt for zero transferred");

var load = new SaveSnapshotLoad();
load.Begin(); load.Stage("A", "receiptsA");
Equal("receiptsA", load.Complete("A"), "matching completed load activates history");
load.Begin(); load.Stage("A", "receiptsA");
Equal<string?>(null, load.Complete("B"), "changed loaded save refuses stale receipts");
load.Begin();
Equal<string?>(null, load.Complete("A"), "missing sidecar restores no history");
load.Begin(); load.Stage("A", "receiptsA"); load.Begin(); load.Stage("B", "receiptsB");
Equal<string?>(null, load.Complete("A"), "overlapping load first completion quarantined");
Equal<string?>(null, load.Complete("B"), "overlapping load second completion quarantined");
Equal(true, load.Quarantined, "unattributable loads disable restoration for session");

string scratch = Path.Combine(Path.GetTempPath(), "nivalis-receipts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    string save = Path.Combine(scratch, "game.sav");
    var store = new SaveSnapshotStore(Path.Combine(scratch, "receipts"));
    File.WriteAllText(save, "branchA");
    string hashA = SaveSnapshotStore.Hash(save);
    store.Write(save, snapshot);
    Equal(snapshot, store.Read(hashA), "save-bound sidecar round trip");
    File.WriteAllText(save, "branchB");
    string hashB = SaveSnapshotStore.Hash(save);
    Equal<string?>(null, store.Read(hashB), "another branch cannot inherit history");
    store.Write(save, "[]");
    Equal(snapshot, store.Read(hashA), "older save branch remains recoverable");
    Equal("[]", store.Read(hashB), "new branch stores distinct history");
    store.Write(save, "[]");
    Throws(() => store.Write(save, snapshot), "byte-identical conflicting histories rejected");
    Equal<string?>(null, store.Read(hashB), "ambiguous same-byte history invalidated");
    File.WriteAllText(Path.Combine(scratch, "receipts", hashA + ".json"), "broken JSON");
    Throws(() => store.Read(hashA), "corrupt history refused");
    Throws(() => store.Read("../escape"), "digest path traversal rejected");
}
finally { Directory.Delete(scratch, true); }
ManagedApiCompatibility.Validate(typeof(FarmSupplyLedger).Assembly);
Equal(true, true, "compiled adapter member references resolve");
checks += CompatibilityFixtureRegression.Run(ManagedApiCompatibility.Validate);
checks += HarvestRecoveryChecks.Run();
checks += HarvestBatchChecks.Run();
Equal(2, IdentitySnapshot.Capture(new[] { (IntPtr)1, (IntPtr)2 }).Count, "unique indexed snapshot identities retained");
Throws(() => IdentitySnapshot.Capture(new[] { (IntPtr)1, (IntPtr)2, (IntPtr)1 }), "duplicate inventory identities are not silently deduplicated");
int seedAttempts = 0;
Throws(() => HarvestRecovery.Replant(() => IdentitySnapshot.Capture(new[] { (IntPtr)1, (IntPtr)1 }),
    () => false, () => true, () => false, () => { seedAttempts++; return true; }), "duplicate seed ownership prevents native replant");
Equal(0, seedAttempts, "invalid seed snapshot never mutates module");
bool plantedForDuplicateTest = false;
Throws(() => HarvestRecovery.Replant(() => IdentitySnapshot.Capture(plantedForDuplicateTest
        ? new[] { (IntPtr)2, (IntPtr)2 } : new[] { (IntPtr)1, (IntPtr)2 }),
    () => plantedForDuplicateTest, () => true, () => false,
    () => { plantedForDuplicateTest = true; return true; }), "post-replant duplicate seeds cannot fake exact-one consumption");
Console.WriteLine($"{checks} checks passed");

sealed class Port : IItemPort<int>
{
    readonly HashSet<int> items = new();
    public bool Reject, ThrowAfterAdd, ThrowAfterRemove, WrongResultAfterAdd;
    public int Count => items.Count;
    public Port(params int[] initial) { foreach (int i in initial) items.Add(i); }
    public bool Contains(int item) => items.Contains(item);
    public bool Remove(int item)
    {
        bool result = items.Remove(item);
        if (ThrowAfterRemove) throw new Exception("simulated remove error");
        return result;
    }
    public bool Add(int item)
    {
        if (Reject) return false;
        bool result = items.Add(item);
        if (ThrowAfterAdd) throw new Exception("simulated error after mutation");
        return WrongResultAfterAdd ? false : result;
    }
}

# Farm First Manager Supply

[Download on Nexus Mods](https://www.nexusmods.com/nivalisnights/mods/34) |
[GitHub releases](https://github.com/foobarto/nivalis-mods/releases)

An original BepInEx 6 IL2CPP plugin for Nivalis Nights. Managers use ripe crops in
player-owned greenhouses before their normal vendor purchasing runs. Harvested
modules are immediately replanted with the same crop, using one actual seed.

Version 0.3.0 follows the manager's preemptive restocking on Steam build
25738165: an ingredient is also supplied when venue stock is below the restock
threshold. Whole harvested batches go to venue storage until it is full, with
overflow kept in the player inventory, and separate zero-cost farm-supply
entries appear in venue finance details.

Verified Steam builds are **25653325** and **25673567**. Best-effort update
compatibility is enabled by default: on an unknown build, the plugin logs its
GameAssembly SHA-256 and checks compiled member references against the installed
regenerated bindings before installing hooks. This checks API compatibility;
it does not verify changed native gameplay rules.

## Behavior

- Runs when a player-owned venue's manager purchases recipe ingredients.
- Uses the game's recipe ingredients and two demand rules, taking the larger:
  - the earlier manager baseline: five servings without recent sales, otherwise
    matching sales near the most recent trading hour;
  - the restock point: build 25738165 managers restock any ingredient whose
    stock plus pending deliveries is below 10, before recipe demand is short.
    The threshold was observed in live purchases and is configurable.

  Current stock and pending deliveries reduce farm demand under both rules.
- Uses only ripe matching crops in properties the player owns, including rented
  farms recognized as owned by the game. Other farms remain untouched.
- Supports any crop whose produce is also its planting material. Shared item
  identities are counted once; native replanting consumes one real item. Only
  surviving new items go to the venue. Planting may spend an older player item,
  following the native planting rule; older items are never transferred as stock.
- Calls native harvest and planting methods. Normal yield, seed recovery, skill
  experience, crop discovery, freshness and growth rules remain in effect.
- Transfers the actual newly harvested ingredient instances into venue storage,
  with ownership checks and restoration if storage rejects a transfer. Existing
  items in the player's inventory are not taken for restocking.
- Sends the whole-unit harvest to venue storage while there is capacity. Only
  overflow that cannot fit remains in the player's inventory. A unit cannot be
  harvested fractionally; it is replanted once per harvest.
- Requires player inventory room for the entire yield plus two seeds and two free stack slots
  before harvesting. Full inventory or venue storage leaves the crop untouched.
- Runs before the normal vendor calculation, including when vendors or money
  are unavailable. The game's existing historical purchasing buffer and vendor
  selection rules remain in effect for remaining deficits.
- Makes no direct save edits. Harvest, planting and stock changes persist through
  normal game saves. Disabling the plugin leaves already completed actions intact.

The venue statistics panel still calculates ingredient expenses from recipe market
prices. Its Beef cost row can appear for farm-grown beef and does not establish
that a vendor purchase occurred. Use the farm harvest log and crop/storage state
to check this plugin's actions.

Farm deliveries recorded since version 0.2.0 appear separately as, for example,
`Beef — farm supply (12)` with **0.00**. Quantities count only items actually moved
into that venue, excluding separately typed seeds and player overflow. When
produce is also planting material, surviving newly harvested items delivered
as ingredients count toward the row; the consumed planting item does not.
Native expense rows, prices
and totals remain unchanged. Entries follow the selected venue and date range,
and are omitted from the gains-only filter. Earlier unrecorded harvests are not
backfilled.

Receipt history is stored in `BepInEx/config/cheeze.nivalis.farmsupply/receipts/`,
matched to the exact saved game bytes. It is captured only after a successful save
and restored after loading completes. Keep that folder when moving saves between
installations. Missing or damaged history produces no farm entries; game saves
retain their original format. Reporting failures do not block native saving or
inventory supply. Overlapping loads quarantine receipt history until restart.

## Install

Install **BepInEx 6 Unity IL2CPP win-x64** (tested with be.788), following the
[official installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html),
and launch once to generate `BepInEx/interop`. Under Proton, set the game's Steam
launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
Then close the game, copy `NivalisFarmSupply.dll` into
`BepInEx/plugins/NivalisFarmSupply/`, and restart.

Configuration is generated at `BepInEx/config/cheeze.nivalis.farmsupply.cfg`:

- `[Supply] Enabled = true`: enable farm supply. Set false to restore the normal
  purchasing route.
- `[Supply] RestockThreshold = 10`: also supply an ingredient when venue stock
  plus pending deliveries is below this amount. Match it to the manager's
  restock point; 0 uses only the recipe-demand rule.
- `[Debug] Verbose = false`: log farm demand per ingredient, skipped crops, and
  a `Manager purchase:` line per recipe showing stock, pending deliveries and the
  amount the manager bought. Use it to check the restock point after updates.
- `[Receipts] ShowFarmSupply = true`: show the separate zero-cost farm entries.
- `[Compatibility] BestEffortAfterUpdates = true`: attempt loading on unverified
  builds. Set false to allow only the two verified binary hashes.

Log: `BepInEx/LogOutput.log`. Each harvest reports crop, yield, transferred amount,
surplus, replanting and seed consumption. If initialization fails, the plugin
removes its hooks where possible and leaves any remaining callbacks inactive.
A harvest count mismatch logs the expected yield, observed produce and returned
seeds. The mod attempts one native same-crop replant with an available real seed,
then verifies the crop and exact seed ownership. Mismatched batches stay in player
inventory and get no delivery receipt. The affected unit is skipped until reload,
and vendors cover that ingredient for the current purchasing pass. Other units,
ingredients and receipt reporting remain active. A rejected replant with unchanged
seed ownership is handled the same way; a unit without seeds needs manual replanting.
Ambiguous ownership/crop mutations stop farm sourcing for the session and let
native purchasing continue, while completed delivery receipts remain available
for display and saving. Initialization or API compatibility failures deactivate
all callbacks. Reporting errors are caught
separately and do not block native UI, saving or loading. Restart after updating
or fixing the mod to retry. Already completed harvests and transfers remain.
BepInEx must itself support the new game version; native crashes cannot be caught
as managed compatibility errors.

Uninstall by removing only this plugin's folder. No game binaries are replaced.

## Build and checks

Run these commands from the repository root with the .NET 10 SDK installed.

```sh
dotnet build mods/NivalisFarmSupply/NivalisFarmSupply.csproj -c Release -p:GamePath="/path/to/Nivalis Nights"
dotnet build mods/NivalisFarmSupply/tests/Tests.csproj -c Release -m:1 --disable-build-servers
dotnet run --project mods/NivalisFarmSupply/tests/Tests.csproj -c Release --no-build --no-restore
```

The plugin targets the loader's .NET 6 runtime and references local installed
assemblies; no game or loader DLLs are distributed. The check harness uses .NET
10 and tests the shared demand and transfer logic without launching the game.

Runtime acceptance: observe a manager restock with ripe matching crops, confirm
venue stock increases, the used units contain the same crop at reset growth,
exactly one seed is spent per unit, and only unsatisfied ingredients are purchased
according to the game's normal policy. Also check empty farms, inventories without
space, already pending deliveries, and save/reload. Compilation and pure logic
checks alone do not establish these game effects.

## Validation

Version 0.3.0 passes 209 checks covering demand, restock threshold, capacity,
item ownership, save-bound receipts, removed API members, replant recovery, and
shared produce/seed identities. The restock point of 10 comes from 19 traced
manager recipe passes on Steam build 25738165: every 10-13 item restock started
at stock plus pending deliveries of 5-9, and no ingredient at exactly 10 was
restocked. One unexplained single-item increase (Lemons, total 17) was also seen. Live gameplay with 0.3.0 on that build confirmed 10 harvest and replant
cycles (Lemons, Cherries, Beef, Bok Choy, Chicken, Peppers, Straw Mushrooms, Thyme,
Wheat), all fully delivered, including ingredients the earlier rule alone would
have skipped, such as Lemons and Beef at stock 7. The manager then bought none
of those ingredients. Ingredients without a ripe unit were
still bought from vendors. That build still uses best-effort compatibility and
is not on the verified binary whitelist. Finance UI and save/reload were not
rechecked on 0.3.0.

Version 0.2.2 passed 199 checks. Live gameplay on Steam build 25680465 confirmed 20 completed harvest
and replant cycles without sourcing errors, including Lemons, Cherries, Blue Cheese,
and venue capacity overflow. That build still uses best-effort compatibility;
it has not been added to the verified binary whitelist.

The earlier 0.2.0 finance display showed Beef farm supply (12) at 0.00 with native
totals intact, and a saved Garlic receipt matched exact autosave bytes. Finance UI
and save/reload on 0.2.2, reporting after a later sourcing pause, all UI filters,
and list reuse remain separate live acceptance checks.

See [CHANGELOG.md](CHANGELOG.md) for release history.

## License

[MIT](../../LICENSE). Game and loader assemblies are not distributed.

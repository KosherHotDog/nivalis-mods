# Changelog

## 0.4.0

- Support game Update #4 (Steam build 25828494). 0.3.0 deactivated itself on
  that build because the per-pass shopping list and a purchase parameter were
  removed.
- Follow each venue's Venue Management Settings: farm supply restocks to the
  game's own Lean, Normal, Extra or Recent Sales target. Recent Sales uses the
  per-meal target the manager uses, not the ingredient-level minimum.
- Add `[Supply] MinimumShortfall` (default 1): smaller gaps are left to vendors
  so a whole harvest is not spent on a one-item top-up.
- Apply `Enabled`, `MinimumShortfall` and `Verbose` changes saved while the game
  runs, at the next restock.
- Retire `[Supply] RestockThreshold` and the copied demand rules; the in-game
  supply target replaces them.
- Verbose purchase traces show the venue setting, meal target, budget and the
  target per ingredient.

## 0.3.0

- Follow the manager's new preemptive restocking (Steam build 25738165): also
  supply an ingredient when venue stock plus pending deliveries is below
  `[Supply] RestockThreshold` (default 10, observed live; 0 disables). The
  earlier recipe-demand rule still applies, so supply never drops below 0.2.2.
- Verbose logging now records each manager purchase per recipe (stock, pending
  deliveries, amount bought) and the stock behind each farm demand decision.
- Add restock threshold checks (209 total checks).

## 0.2.2

- Support crops whose produce is also their planting material, including Lemons
  and Cherries, with identity checks before and after replanting.
- Transfer only surviving newly harvested items to venue inventory.
- Keep committed delivery receipts available if farm sourcing pauses after a failure.
- Add shared-type harvest and ownership regression checks (199 total checks).

## 0.2.1

- Log observed harvest counts and attempt one verified same-crop replant.
- Isolate recoverable unit failures while allowing native vendor purchasing.

## 0.2.0

- Fill venue storage with the whole harvest; retain only capacity overflow in
  player inventory.
- Add zero-cost farm-supply rows without changing native finance totals.
- Store optional receipt history in sidecars bound to exact save bytes.
- Attempt API-checked loading after updates, with native gameplay fallback.

## 0.1.1

- Read the keyed inventory stack index with ownership and version checks,
  avoiding the failing boxed IL2CPP linked-list enumerator.

## 0.1.0

- Initial farm-first manager supply and automatic same-crop replanting.

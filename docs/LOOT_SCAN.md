# Loot Scan decisions

What a scanned loot screen is decided from, and what it still cannot be. The recognizer that
turns pixels into named cells is in `docs/RECOGNITION.md`; this file is about the step after it.

## The path

`LootScanCaptureHandoff` receives a pixel-free reading of the frame. `LootScanRecommendationSource`
turns every named cell into the inputs `ExplainableRecommendationEngine` asks for, and
`LootScanDecisionService` plans the engine's advice against the carried grid. The verdicts are
TAKE, SWAP, LEAVE and REVIEW.

While icon matching is still running, each named cell appears on the Loot page as PENDING. The
final planner result updates and orders those same rows; cancelled and superseded scans cannot add
late items.

Until 2026-09-19 every real scan ended "valued, not decided". The source supplied catalog
prices, left everything else unread, and told the engine three things it had not checked: not
pinned, not protected, no item rule, each as a known "false" at full confidence. A pinned item
was shown as unpinned and weighed on price.

## What each input is read from

| Input | Read from | When it is unread |
| --- | --- | --- |
| Pin, protection, item rule, wishlist | The active V2 profile: `ProfileProgress.Pins` of kind `item`, `ItemOverrides`, `WishlistItemIds`. `LootScanProfileRules` is the one place that says what the texts mean. | No profile given: unread, never "false". A rule text this build does not know is unread too. |
| Quest and hideout needs | `LootScanNeedSource`: the requirement rows `ProfileNeedAggregationService` already holds, the V1 profile's progress, and the quest board. | The board cannot be read: the profile is reported partial. |
| Holdings to subtract from a need | `IObservedInventoryEvidenceReader`, when a snapshot exists for the same profile and data sync. | No stash scan yet: the engine says so and the answer stays a review. |
| Flea fee and net | `FleaMarketFee`, from the item's base price and the two rates in the items payload. | Rates or base price not synced. |
| How readily another copy is had | Trader buy offers and the last listing count, from the item's retained source row. | The payload carries no listing count for a flea item. |
| Raid phase | The raid clock the way the rest of the app counts it, in thirds. The player can pick it instead. | Not in a raid, or the map's length for this side is unknown. |
| Event state (Safe, Allergic, Untested) | `LootScanEventStateSource`: the running events in `IEventCatalog` and the results the Events page wrote into the active profile. An Allergic result in any running event wins. | No running event lists the item: a settled "outside every event". The event folder cannot be read: not claimed. |
| Risk | The player's setting in the workspace, "Normal" until changed, kept for the session. | Never. |
| Carried grid | The backpack on the in-raid Gear screen (`GearScreenLayoutReader`), on `CaptureAnalysis.CarriedGrid` to `LootScanFrame.CarriedGrid`. | No backpack in view, or its frame hidden (a tooltip): a wanted item reads "TAKE?" with what is missing. |

## How near a quest is

A quest the player is on, or has pinned, is current. Any other is as many steps ahead as the
longest chain of unfinished prerequisites in front of it, and at least one. The engine ignores
a quest beyond its five-step horizon, so on a fresh wipe the whole game is not a reason to take.
A quest that is not on the board has no distance and is treated as beyond the horizon.

An objective that accepts any of more than eight items ("hand over 50 barter items") counts only
once the player is on that quest. The catalog lists every accepted item as its own row, and one
such objective five quests ahead made nearly every item in the game a "future quest" take.

## Reaching the page

An armed intent is claimed by the next capture, and intake refuses a capture whose context
differs from the armed one. `CaptureIntakeContext.For` therefore submits an arriving frame in
the armed session's own context; before it did, the shell armed as "this-desktop" and the
watcher submitted as "desktop", so an armed Loot intent followed by the game's screenshot key
was refused every time. With nothing armed, a container screen detected during a raid is
measured as loot, and one named item with no lattice opens Intel instead of an empty scan.

## The flea fee

Photographed flea offers use the same versioned explainable policy as Loot and Intel. Their
condition, OCR evidence and alternate item reads stay attached; EUR/USD quotes use the catalog's
current rouble purchase rate and remain review-only when that rate is unavailable.

The game does not publish the formula. It is the one the community worked out and the EFT wiki
documents:

```text
fee = Q * (VO * Ti * 4^PO + VR * Tr * 4^PR)
```

VO is the base price and VR the asking price, for one item; Q is how many. PO is log10(VO / VR)
and PR is log10(VR / VO); whichever is positive is raised to the power 1.08. Ti and Tr are
`data.fleaMarket.sellOfferFeeRate` and `sellRequirementFeeRate` in the `/items` payload, both
0.05 when read on 2026-09-19. The asking price is the 24-hour average.

One point needs no trust in the curve: asking exactly the base price costs the base price times
the sum of the rates. `FleaMarketFeeTests` holds that and three worked values.

The Intelligence Center discount and the Hideout Management skill are not modelled. Both only
lower the fee, so the figure is the most the game would charge and the net is the least a
listing would return. A listing the fee would swallow is reported as "the flea does not sell
this", which lets the trader's offer be weighed alone.

The fee is a calculation and is as old as the oldest thing it was calculated from. A fee worked
out now from yesterday's price is refused like yesterday's price.

## How old a price is

The source stamps an item only when its market figures move. On 2026-09-19 every one of the
3,553 items with a flea average had been stamped within twelve hours, and 1,476 of 5,442 items,
everything with no flea listing, carried a stamp over a week old. The engine refuses a price
older than twelve hours, so dating everything by the stamp called a quarter of the catalog
expired for ever.

So the two kinds of fact are dated differently. A flea average and a listing count are market
figures and are as old as the item's stamp. What a trader pays, the base price, a trader's buy
offer and "the flea does not sell this" do not move with the market: they are as fresh as the
last items sync that confirmed them (`sync_state`), where one is recorded, and as old as the
stamp where none is.

## Obtainability

A band from two published facts, never a spawn rate:

- **Abundant**: a trader sells it for money and no quest gates the offer.
- **Available**: the last market scan saw ten or more listings.
- **Limited**: anything else.

"Scarce" is never claimed. A flea ban is a rule about selling and not a measure of rarity, and
nothing in the catalog measures rarity. On 2026-09-19 the payload held 5,442 items: 2,514
abundant, 626 available by this rule, and 1,290 flea-banned with no open trader offer, which a
"flea-banned means scarce" rule would have turned into 1,290 automatic takes.

## Advice under a review

Any missing evidence makes the engine's answer partial, and the planner turns a partial answer
into REVIEW. A TAKE verdict also claims a fit, so with the backpack unread no take can be a
verdict. Both are right for the contract and useless to a player if shown as a bare refusal, so
the workspace shows the engine's advice with what it lacks: `TAKE?`, the strongest reason
("Current quest", "Pinned", "Worth its squares"), and one sentence naming what is not known.
That sentence is built from the evaluation's own evidence reasons, not a fixed text.

LEAVE needs no fit and is a verdict today.

## The carried grid

The planner asked for a complete carried reconstruction. A reconstruction is partial as soon as
one cell has an attribute unread, and the game does not print found-in-raid on a carried item,
so no backpack read from pixels could ever have been planned against. It now tolerates an
unread name, count, rotation or attribute. A carried item nobody could name holds the squares
it was seen to cover, when that region is a whole number of cells, and is never offered for
dropping. A doubtful footprint, an overlap or doubtful geometry still stops planning.

The backpack is read from the in-raid Gear screen (2026-09-22, `docs/RECOGNITION.md`). On the
one real frame with loot open, a full Duffle, the ammo pack in the box came out SWAP: place at
row 3, column 2, drop the Poxeram, the one bag item named. Only the backpack's largest grid is
planned against; a "no fit" does not know about room in the rig or pockets.

## Changing the answer

The selected item can be pinned, wishlisted, or given an "always take" or "always leave" rule,
and the raid phase and risk are picked in the header. Each writes where the scan reads, then the
retained frame is decided again. No second screenshot is needed and the selection is kept.

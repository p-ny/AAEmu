# Hero System & Castle (Dominion/Siege) System — Database Changelog

This branch extracts only the Hero election/leadership system and the **new** (Hero/faction-owned)
castle system — Dominion claim/tax and Siege raid teams. It deliberately excludes the older
guild-owned Exeloch/Sungold castle system (that has its own, already-merged feature history), the
player-founded-Nation layer (a separate system built on top of Dominions — diplomacy, sovereigns,
national tax/monument — removed from this branch entirely), and every other unrelated fix that was
mixed into the same working tree.

`GuildDominionManager`/`IGuildDominionManager` are included only because `HousingManager` and
`AdvanceGuardTowerStep` branch on both castle systems at the same call site (a claim/build check has
to know which of the two owns the zone group before it can decide anything) — they are a compile-time
dependency here, not new guild-system feature work.

## MySQL (`aaemu_game`) — live save-state schema

All of these are new, additive migrations (`CREATE TABLE IF NOT EXISTS` / `ADD COLUMN`), applied
automatically by `MySqlDatabaseUpdater` on next World/Login boot when `Connections.AutoApplyUpdates`
is `true` (tracked per-script in the `updates` table, safe to run against an already-partially-applied
database). Files live in `SQL/updates/`, listed here in application order:

| File | What it adds |
|---|---|
| `2026-08-13_aaemu_game_dominion_lodestones.sql` | Seeds the 12 unclaimed "Archeum Lodestone" Guard Tower houses a live DB provisioned before this seed existed never got — without them there was nothing for the Purifying-Archeum declare-dominion skill (13661) to target. |
| `2026-08-13_aaemu_game_dominions.sql` | `dominions` table — the actual claim/ownership save-state for a zone group's castle (which `siege_zones`/`siege_plans` in the client data only describe as a schedule/template). Trimmed for this branch: the original migration also carried 6 `national_*` columns that exist solely to back the player-founded-Nation layer (never wired to accrual/payout even there) — dropped here along with that layer. |
| `2026-08-13_aaemu_game_dominions_guard_tower_step.sql` | `dominions.guard_tower_step` — how far a claimed territory's guard-tower blueprint chain has advanced. |
| `2026-08-13_aaemu_game_hero_election.sql` | `hero_candidates` (+ companion tables in the same migration) — live per-faction candidate/vote state for the current Hero election cycle. |
| `2026-08-13_aaemu_game_leadership_point.sql` | `characters.leadership_point` — the first Leadership stat column (later split further, see below). |
| `2026-08-13_aaemu_game_siege_raid_teams.sql` | `siege_raid_team_members` — offense/defense roster and running score counters for a zone group's siege. |
| `2026-08-14_aaemu_game_leadership_point_period.sql` | `characters.leadership_point_period` + one-time backfill so existing characters don't start ineligible to vote. |
| `2026-08-15_aaemu_game_hero_votes_pk.sql` | Fixes `hero_votes`' primary key (was missing `candidate_character_id`, so a multi-select ballot silently kept only the last pick). |
| `2026-08-15_aaemu_game_leadership_period_split.sql` | Splits leadership into the four figures the client actually reads separately: current period, previous (frozen) period, lifetime total, daily-cap tracker. |
| `2026-08-21_aaemu_game_dominions_castle_tier.sql` | `dominions.castle_tier` — Keep/Castle/Palace tier counter for the new system's guard-tower/castle-tier progression. |
| `2026-08-21_aaemu_game_dominions_faction_id.sql` | `dominions.faction_id` — the real ownership split: some zone groups can only be claimed by a faction's elected Hero (not an arbitrary guild); 0/unused on guild-owned rows. |
| `2026-08-24_aaemu_game_dominion_locked_zones.sql` | `dominion_locked_zones` — GM-controlled "no new claims" lock per zone group, for both castle systems (blocks new claims only; does not touch existing claims). |
| `2026-08-31_aaemu_game_faction_statues.sql` | `faction_statues` — construction/decay persistence for the 3 Hero capital-city Statue doodads (Nuia/Haranya/Pirate), which are ambient world doodads and don't otherwise round-trip through the generic `doodads` table. |
| `2026-08-31_aaemu_game_hero_bonus_progress.sql` | `character_hero_bonus_progress` — per-character progress toward the daily-activity Hero reward box. |
| `2026-08-31_aaemu_game_hero_dominion_points.sql` | `characters.dominion_point_weekly_given` / `last_dominion_point_give_time` — a serving Hero's weekly-capped Dominion Point distribution. |
| `2026-08-31_aaemu_game_hero_mobilization_order.sql` | `characters` columns for Mobilization Order issue counts/timestamp (today/total/last-issued — display only, no confirmed daily cap in the shipped data). |

Also touched by the same accumulated work but **not** included in this branch: the entire
player-founded-Nation layer (`2026-08-13_..._nation_relations.sql`, `2026-08-13_..._nations.sql`,
`2026-08-21_..._nations_alliance_relations.sql`, `2026-08-21_..._nations_name.sql`,
`2026-08-21_..._player_nation_system.sql` — a `nations` table sitting on top of `dominions`, diplomacy,
sovereigns, and a `NationManager` service; see "What this branch deliberately leaves out" below);
`2026-08-26_..._character_ability_sets.sql`, `2026-08-27_..._expedition_buffs.sql`,
`2026-08-27_..._expedition_interest.sql`, `2026-08-27_..._expedition_residence.sql`,
`2026-08-24_..._guild_dominions.sql` (old guild-owned castle schema), `2026-08-13/2026-08-19_..._housings_*`
(generic housing, unrelated) — each has its own column in `characters`/`expeditions` unrelated to
Hero/Castle.

## `compact.sqlite3` (client reference data) — data-level changes

These were applied directly against the shipped client database rather than as versioned `.sql`
scripts, since they're one-off data corrections rather than schema changes. Recorded here for
whoever picks this branch up, so the exact data state this code assumes is traceable:

- **`special_effects`**: 3 server-only synthetic rows added, since the corresponding skills ship with
  zero `skill_effects` rows in this client build — `special_effect_type_id` 197 (`AdvanceGuardTowerStep`,
  skill 41079, the shared use-skill for the 5 guard-tower wall/gate blueprint items) and 199
  (`DeclareMobilizationTimeState`, skills 40150/40175/43726/50166/50168, the 5 real Mobilization Order
  flag-declaration skills). `special_effect_type_id` 198 (`AdvanceCastleTier`, skill 33550) also exists
  in the data from earlier old-system work but its handler class is **not** part of this branch.
- **`doodad_func_hero_elections`**: rows added for the Hero-election interaction doodads — this table
  existed in the schema but had no loader reading it until `DoodadManager` was extended (see
  `DoodadFuncHeroElection.cs`'s own doc comment for the exact gap).
- **Faction Statue doodads** (Nuia `doodad_almighty` 10588, Haranya 10650, Pirate 10651): their
  `doodad_funcs.actual_func_type` rows were data-patched to `DoodadFuncFactionStatueDevote` /
  `DoodadFuncFactionStatueTimeGatedUse` so `DoodadManager`'s per-type-string loader dispatch (not
  generic reflection) actually populates a handler for them — confirmed live 2026-08-31 that without
  this the statue interaction silently did nothing.
- **Mobilization Order per-faction zone groups**: no compact.sqlite3 table carries this mapping (it's
  derived at runtime from each capital-city statue's own zone group, ground-truth confirmed live via
  teleport + `/pos` + the zones table: Nuia zone group 2, Haranya 4, Pirate 60 — see
  `HeroManager.MobilizationOrderZoneGroups`), so there is no client-data patch for it, only the code
  table.

## What this branch deliberately leaves out

- **Player-founded Nations.** A separate system built on top of a claimed Dominion: a founding quest
  chain converts an existing Dominion into a `nations` row with its own Sovereign, name, and
  nuia/haranya alliance-diplomacy stance, plus free-form nation-to-nation diplomacy
  (`/nationrelation`) and a "National Monument"/"National Tax" data model (present in the schema and
  in two real, pre-existing client opcodes — `SCNationalTaxRatePacket`/`SCNationalMonumentChangedPacket`
  — but never wired to any accrual/payout logic even before removal). Entirely removed for this
  branch: `NationManager`/`INationManager`, every `/*nation*`, `/disbandnation`, `/leavenation`,
  `/transfersovereign`, `/placemonument` command, `DeclareIndependence.cs` (the founding-effect
  stub), the `nations`/`nation_relations` SQL migrations, `DominionManager.TransferToFaction`/
  `TransferToGuild` and `GuildDominionManager.RemoveForTransfer`/`AdoptFromNationTransfer` (the
  guild↔nation ownership-transfer bridge those two methods existed solely to support), the 6
  `national_*` columns/fields on `DominionData`/the `dominions` table, `DominionManager.SetNationalMonument`,
  and `HeroGameData.CloneRewardsForNewFaction` (cloned Hero-reward rows onto a newly-founded nation's
  faction id — orphaned once nothing can found one). `Character.OriginFaction`/`IsTempFaction`
  persistence (added only to survive a nation's temporary-faction-switch mechanic across a relog) was
  reverted along with it. None of this touches the base Hero system: "nation" elsewhere in this
  branch's own comments (e.g. `HeroManager.ResolveNationFactionId`) refers to a Hero's top-level
  alliance faction (Nuia/Haranya/Pirate) — an unrelated, pre-existing concept, not this removed layer.
- The old (guild-owned) Exeloch/Sungold castle system's own feature work (`AdvanceCastleTier.cs`,
  `guild_dominions`/`guild_dominion_housings` schema) — separate history, not part of this branch's
  scope.
- The guild-war (Expedition War) feature — already shipped as its own PR (`feature/guild-system`,
  PR #1549), already merged upstream.
- Every other fix mixed into the same local working tree that isn't Hero/Castle-specific (item
  synthesis/awakening, combat-resource sync, skillsaver/actability, slave/boat zone-relay, follow
  system, merchant/quest fixes, etc.) — left on the main dev branch, out of scope here.

## Known open items (not addressed by this branch)

- Mobilization Order: the Accept-dialog does not appear on other online faction members' clients when
  a Mobilization Order is issued (broadcast is sent and confirmed received server-side; the client-side
  popup trigger for a *different* character than the issuer is unconfirmed).
- Mobilization Order: no cooldown between issuances has been implemented — deliberately left open,
  no data-driven value was found for it.
- `IsGuildDominionHousingTemplate` will return `false` for everything until the (currently
  non-existent) `guild_dominion_housings` table is created — flagged in `HousingGameData.cs`'s own
  comment, out of scope for this branch but relevant if picking up the old-system thread later.

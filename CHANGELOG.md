# Changelog

All notable changes to Cobalt Papers Please are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow semver.

## [Unreleased]

## [1.4.2] - 2026-10-02

Facepunch's Livestock update (2026-10-01) made its own Recast navmesh ("RustNav") the server
default and stopped baking the Unity navmesh (`-useOldNavmesh` restores it). Verified live on
the Oct 2 build before and after this release with a throwaway probe: the guards' engine-side
navigator binds the prefab's RustNav agent by itself, so checkpoint guards, patrols and hunters
kept walking on 1.4.1 (a 120 m road walk covered 88 m in 40 s), but every direct
`UnityEngine.AI.NavMesh` query in the plugin answered nothing.

### Fixed
- Every navmesh query now goes through Facepunch's `RustNavMeshHelpers`, which dispatches to
  whichever mesh the server booted with: the spawn snap, the walk and return-to-post
  destinations, the agent-type probe and `papers.cp navprobe`. Agent-typed queries (the Animal
  and Humanoid bakes) are a Unity-only concept and fall back to the untyped query under RustNav.
- The raw Unity `NavMeshAgent` on a guard is enabled only under `-useOldNavmesh`; on the RustNav
  default it stays off instead of logging "not on navmesh" errors for every guard.
- A guard brain that cannot initialise (no navigator, no RustNav agent) now logs why instead of
  standing silent.

### Changed
- Checkpoint and guard lines report the surface as `rustnav` (or `none`) on the new mesh; the
  stall line reads the RustNav agent's on-mesh state; `papers.cp navprobe` names the live mesh.

## [1.4.1] - 2026-09-27

### Changed
- A vault can no longer be opened with papers: the door refuses everyone, Citizens with a
  legit Cobalt ID included, and the only way in is a breach. `Vaults.OpenBands` now defaults to
  empty (it was `["Citizen"]`); a server that wants the old rule can list bands there again. An
  existing config keeps its value, so set `OpenBands` to `[]` by hand when upgrading. The refusal
  line is now "Cobalt property. No entry." (update `Vault.Refused` in `oxide/lang/en` too, since
  Oxide keeps an existing lang file's text).

### Fixed
- The flyer no longer says a Citizen may open a vault, or that Citizens are never searched
  (they are searched from tier 4).

## [1.4.0] - 2026-09-27

Decision 0011: Papers Please documents itself in the server's help menu and reaches out to two
sibling plugins. Live on the dev server with PublicWorks 2.9.0 and IslandTaxi 1.6.0; the
integration driver (`tools/PapersSelfTestM10.cs`) passed 15/15.

### Added
- A HelpMenu page through the `GetHelpInfo` hook: what Cobalt is, the bands with this server's
  thresholds, how a checkpoint works, how to earn standing back, and the player and admin
  commands (admin rows only for admins). The Public Works and Island Taxi notes appear only
  while those plugins are loaded. All text is in lang (`Help.*`).
- Civic work earns standing: finishing a Public Works repair contract is +10 (minor fault) or
  +20 (major), and paying for a day of a service is +2 once per player per day
  (`Reputation.PublicWorksRepairMinor`, `PublicWorksRepairMajor`, `PublicWorksPurchase`; needs
  PublicWorks 2.9.0 or newer, which raises `OnPublicWorksRepair` / `OnPublicWorksPurchase`).
- Island Taxi 1.6.0 reads `GetBand`: Citizens ride 10 % cheaper, the Wanted pay double, and an
  Enemy of the State is refused a car (configured in Island Taxi).
- The flyer (`PapersPlease-Flyer.pdf`).

## [1.3.0] - 2026-09-27

Milestone 9: foot patrols on the roads and the Cobalt ambush with its arrest (decision 0010),
plus the post-release amendments to Milestone 8 (peacekeeper spawners paused during the raid,
vaults and the chain from level 3 with heavies and sentries by level, the vault loot table,
curfew radius 75, the fence's lines). `PapersPlease.cs` 1.2.1 → 1.2.20 on the dev server,
released as 1.3.0. Live-verified 2026-09-20 → 2026-09-27 (`docs/reports/m9-live-test-2026-09-20.md`:
probe block P, patrols and the stop, the ambush and the arrest in-game; the server-side rows by
the M9 self-test driver, 13/13). Review and security scan (`docs/reports/2026-09-27-review.md`,
`-security-scan.md`) shipped in 1.3.0.

### Added
- The Outpost window pauses the compound's own peacekeeper spawners (`Sabotage.PauseVanillaSpawners`,
  on by default): a peacekeeper you drop during the run stays down until the window ends. Probed
  live 2026-09-20: thirteen one-man spawners on a 25–35 s clock; pausing them respawns nothing
  and touches nobody standing (1.2.1–1.2.2).
- Admin: `papers.cp spawners list|off|on|delay <seconds>` to list, pause, resume or re-time
  those spawners; everything is restored on window close, reload and unload.
- `docs/tiers-and-bands.md`: what every band and tier triggers, on one page.
- Vaults and the sabotage chain from **threat level 3** (decision 0009 §V′, 1.2.3): at level 3 the
  vault stands with two guards and the fence sells the bypass tool; at level 4 the vault's
  guards are heavies (`VaultHeavyFromTier`); at level 5 three heavies and two sentries of the
  vault's own flank the door (`VaultTurretsFromTier`, `VaultTurrets`, under the Outpost turret
  cap). Defaults `VaultFromTier` 3, `GuardsByTier` 0,0,2,2,3, `SabotageFromTier` 3.
- The vault's loot is a config table by level (`Vaults.LootByTier`, 1.2.4): item short name →
  amount per level, totals per vault split across its crates, replacing the crate's own loot
  (an empty level keeps it). Defaults: scrap, high-quality metal, HV rockets, rockets and C4 at
  2000 / 250 / 25 / 25 / 25 (level 3), 3000 / 500 / 50 / 50 / 50 (4), 4000 / 1000 / 100 / 100 /
  100 (5). One stack per item whatever the size — it moves to your inventory in one drag
  (1.2.5).

- Milestone 9 groundwork (decision 0010, 1.2.7–1.2.13): config sections `Patrols` and
  `Ambush` with the decided defaults, the first patrol and ambush lines and voice lists, the
  `Patrol` checkpoint kind, a guard's road walk (`GuardBrain.StartWalk`, hops between road
  points, stall skipping; a walking guard never self-idles), and the admin probe levers
  `papers.cp walk <m> [x y z] | movetrigger | ambushspot [player] | navprobe [player | x y z] |
  cupboard [player]`. The probe found no navmesh under the guards on this build; the sabotage
  squad's origin no longer requires one (it had been falling back to the switch's flank).

- **Patrols** (decision 0010 §A, 1.2.14–1.2.17): from level 2 two Cobalt guards walk a stretch
  of road (`PatrolsByTier` 0, 1, 1, 2, 3 under their own `PatrolGuardCap` of 6), the second four
  metres behind the lead, turning at each end; the stop rides with them — Neutral and worse who
  cross the lead's 8 m are pulled over with the same panel and verdicts as a gate (Citizens are
  waved on: "Move along, citizen"), vehicles get a patrol STOP prompt from 30 m; a wiped patrol
  comes back on a fresh road after the level's respawn time ("Cobalt patrol n has gone silent");
  after a 45-minute shift a patrol stands down and returns 15 minutes later elsewhere; nothing
  happens inside a safe zone. Admin: `papers.cp patrols [list | relocate <slot>]`.
- **The Cobalt ambush** (decision 0010 §C/§C′, 1.2.18–1.2.20): from level 3, every time Cobalt
  turns on an Enemy of the State (a gate or patrol verdict, an evasion, a run, a vault breach) is
  a sighting; with no ambush on them in the last 30 minutes and fewer than two squads out, they
  hear "Cobalt radio: a unit is coming for you" and 45 seconds later the level's squad (two at
  level 3, three with two heavies at 4, four heavies at 5 — `SquadByTier`, `SquadHeaviesByTier`)
  appears 80 m from wherever they are, out of their sight, and hunts them for five
  minutes — holding at a safe zone's edge and 40 m short of any tool cupboard's range they
  stand in, standing down on the timer, death, disconnect or a wipe-out. **The arrest**
  (decision 0010 §D): a target the squad downs is not finished — a hunter walks up, and five
  seconds later the player is stripped of everything, cuffed, hooded and left on the spawn beach
  with 30 health ("taken into custody"); the vanilla escape mini-game frees the cuffs. Admin:
  `papers.ambush status | dispatch <player> | stand`; config `Ambush` (level, bands, cooldown,
  delay, squad size, distance, minutes, standoff, `EnterBases`, `MaxSquads`, the radio line,
  `Arrest`, `ArrestSeconds`, `ArrestHealth`, `ArrestStrips`, `BroadcastArrest`).

### Fixed
- Post-release review (`docs/reports/2026-09-27-review.md`, 1.3.0): a patrol restored after a
  restart with no route spawned at the world origin — its respawn now waits for a road; a
  patrol whose lead died during a stop walked away from the halted player, who was booked as
  running it — a hand-over keeps the halt; a hunter's chase was held 40 m from where it began —
  the post follows the target; a target hiding in a safe zone made the hunters re-engage and
  drop him four times a second — a blocked target is left alone; a relocation reset the
  patrol's shift clock and could despawn a patrol in front of a player at its stop; a Citizen
  driver's passenger was halted in a car that had been waved on — the driver's band decides
  for the vehicle; three chases in a row read as three stalls; a guard drifted from its post
  never returned (no navmesh on this build); a downed player could be arrested through a wall
  and hunters held fire on a downed player even with the arrest off; the ambush cooldown
  started at dispatch even when no squad got out; the cupboard lookup ran eight times a
  second per hunter.

### Changed
- The fence's lines, reviewed with the owner (1.2.6): the tool offer no longer counts the
  compound's sentries the window will switch off ("what that leaves between you and the door"),
  the tool sale has its own three voice lines (`Voice.FenceTool`) instead of the forgery's, the
  cleaning offer says "marked scrap pays the same", the no-scrap line says how much more to bring,
  the forgery odds name the threat level, and the tool receipt says "once you flip it".
- Curfew radius 45 → **75 m** (`CurfewRadius`, 1.2.3): the Outpost wall gates stay inside the safe
  zone at night, so an armed Suspect no longer takes a wall gate in the open.

## [1.2.0] - 2026-09-20

Milestone 8: Cobalt vaults and the sabotage chain that opens the Outpost one (decision 0009,
§B′ as redesigned 2026-09-20). `PapersPlease.cs` 1.1.1 → 1.1.19 on the dev server, released as
1.2.0. Live-verified 2026-09-16 → 2026-09-20 (`docs/reports/m8-live-test-2026-09-16.md`: probe
block P, vault block V, sabotage block S in-game; the server-side rows by the self-test driver,
26/26 on 1.1.19 and 1.2.0). Post-release review and security scan
(`docs/reports/2026-09-20-review.md`, `-security-scan.md`) shipped in 1.2.0.

### Added
- Cobalt vaults from threat level 4: a 3×3 m armored room with a code-locked door, elite crates
  and two guards (three at level 5), placed once by an admin at a monument with `/papers vault
  add` (crate and guard spots with `crate`/`guard`; positions are monument-local and transfer
  between maps). Outpost has one inside the compound.
- The vault door opens for a Citizen carrying a Cobalt ID in their own name and for nobody else,
  admins included; forged, stolen and revoked papers are refused. The door shuts itself ten
  seconds after opening. Looting the crates costs reputation and tags the loot as before.
- Breaching the door (three C4; 1,000 hp) costs −15, flags you hostile, puts the vault and the
  monument's gates on alert for ten minutes and goes out server-wide; Cobalt rebuilds the door
  after twenty minutes and restocks the crates an hour after the last one is emptied. Both
  timers survive a restart. A door removed without a fight (an admin's `ent kill`) is replaced
  too, without a penalty.
- The sabotage chain from level 5: the Bandit Camp fence sells a **bypass tool** (150 scrap,
  Cobalt property, so a gate search takes it) and names the map grid of the substation. Used on
  the switch at the substation nearest Outpost it opens a ten-minute **vault window**: the
  Outpost safe zone drops, the compound's own sentries go offline, the floods go out and the
  sirens come on, every Outpost checkpoint goes on alert, the saboteur is hostile to every Cobalt
  gun for the window, a response team is radioed in and arrives at the substation twenty seconds
  later from sixty metres out, a red VAULT WINDOW countdown sits at the top of the screen, and
  the server hears about it. Flipping the switch without the tool does nothing. −5, once an
  hour; everything is restored when the window ends, on a tier drop, on reload and on unload,
  with a closing advisory.
- Admin: `/papers vault add|crate|guard|list|remove|tp|open|close|breach|restock`,
  `/papers sabotage switch|status|remove|tp|cut|restore`, console `papers.vault`,
  `papers.sabotage`, `papers.cp safezone off|on|status`, `papers.cp lights`, the probe levers
  `papers.cp vaultdoor|switch|terminal here`, `turrets on|off`, `loose`; config sections
  `Vaults` and `Sabotage`; vault state in `checkpoints.json`; the M8 self-test driver
  (`tools/PapersSelfTestM8.cs`, never shipped).

### Changed
- The fence's panel fits its text: the body uses the whole band between the header and the
  buttons and the font steps down as the text grows; the header names the monument ("Bandit
  Camp") instead of its prefab name.
- The fence no longer offers to "clean" a bypass tool (that would have stripped the tag the
  switch requires).
- Three `Sabotage` config descriptions were reworded for the redesign (`Sabotage`,
  `SabotageFromTier`, `Broadcast`): a config generated before 1.2.0 keeps its values only if
  the keys are renamed by hand; the placed switch pose is unaffected.

### Fixed
- Live test 2026-09-20: the window's response squad was told to target the saboteur in the
  frame it spawned, before its brain existed, and the exception left the safe zone down with no
  close timer and the compound's twenty sentries offline until the next load — the target is now
  kept until the brain is up, the close timer is armed first, and a load sweeps leftover offline
  sentries back online; a vault crate destroyed at a tier change re-entered the plugin and
  broke the reconcile before the switch came down; a vault that stood down below its level
  never came back on a rise; an early close left the window's alert on the Outpost gates and,
  within twenty seconds of the flip, still sent the squad; the squad's spawn point was sampled
  on the wrong navmesh; a map without Outpost would have treated every roadblock as an Outpost
  checkpoint; a reload with `DarkenMonumentLights` turned off mid-window left the lights dark;
  `papers.vault open` left the door open; the door's decay was disabled before spawn, where the
  engine re-reads it.
- Milestone 8 probe (1.1.3–1.1.5): the deployable door is spawned grounded and without decay
  (a stability entity with no support is gibbed on the spawn tick), quarter-turned to the
  captured yaw; the terminal is the static computer station.

### Removed
- The unread `Vaults.ExtraTagged` config key.

## [1.1.0] - 2026-09-16

Milestone 7: Cobalt identity papers, forgeries, stolen IDs and the Bandit Camp fence
(decision 0008). `PapersPlease.cs` 1.0.2 → 1.0.15 on the dev server, released as 1.1.0.
Live-verified 2026-09-13 → 2026-09-16 (`docs/reports/m7-live-test-2026-09-13.md`: server-side
rows by the self-test driver, blocks A–D in-game).

### Added
- Cobalt identity documents: a "Cobalt ID" note in your name with a serial, issued at a
  checkpoint with `/papers request` or from a TAKE / BUY button on the pass panel — free for
  Citizens, 20 scrap for Neutrals. A new ID revokes the old one; `/papers` reports the serial
  you hold and whether it is on you.
- A valid ID softens the verdict by one band, capped at Neutral: a Suspect with papers is
  searched instead of fined; an ID never skips the search. A Wanted holder is honoured one last
  time (fined as a Suspect) and the document is kept; papers lapse when standing falls to Wanted
  (config `LegitRevoke`: grace, immediate or never). Revoked papers and papers in someone else's
  name (−5) are seized at the gate.
- Forged papers, rolled at every scan (spotted 10 / 20 / 35 / 50 / 70 % at levels 1–5): seized
  for −10 below level 3, Wanted on the spot from level 3 (score −60, guards fire, server-wide
  line, threat bonus). A workbench forge route exists behind `ForgeAtWorkbench` (off).
- Stolen papers: about one dead checkpoint guard in ten carries a Cobalt ID in his own name,
  found by looting the body; treated as a forgery at a scan.
- The Bandit Camp fence: a passive, invulnerable trader placed with `/papers fence add` whose
  panel cleans every `[COBALT]` stack on you for 15 scrap per stack and sells forged papers for
  40 scrap; Cobalt-tagged scrap spends at par; an Enemy of the State is turned away. Each visit
  nudges the threat clock. Optional clothing and an unarmed belt; he keeps the placed height.
- Admin: `papers.id list|issue|forge|revoke|wipe`, `/papers fence add|remove|tp|list`,
  `papers.fence status|at|remove`; the ID registry in `oxide/data/PapersPlease/ids.json`
  (reset on wipe); voice lines for issued papers, spotted forgeries, laundering and a papers sale.

### Changed
- Roadblocks are never drawn within 100 m of a player (`PlayerClearance`); a reload had put one
  fifteen metres from a player and halted him on the spot.
- Guards offer papers: the pass panel stays up 20 s with the ID button when you hold none.

### Fixed
- The fence spawned on a Bandit Camp roof when the ground ray from above hit the walkway; a
  placed fence now keeps the admin's captured height.
- `/papers` claimed a valid ID was held after the note had been dropped; the line now reads the
  pockets like a scan does.
- The fence said "Cobalt property? Not any more" after selling only forged papers; a papers sale
  has its own lines.
- The stolen ID dropped on the ground beside the body was too easy to miss; it is now in the
  corpse's inventory.
- Post-release review (`docs/reports/2026-09-16-review.md`, 1.0.16): a double-click on the
  pass panel's ID button charged the fee twice and revoked the first note — a request is now
  refused while a valid ID is in your pockets, which also ends free re-issue spam; a finished
  encounter's panel timer could close the decision panel of a newer encounter at the next gate
  (a timeout, −5 and hostile) — it now leaves a live panel alone; the ID registry is flushed with
  the periodic save, not only on server save; the fee and the old ID's revocation now follow a
  successfully created note, at the gate and at the fence; a seizure line on the panel is no
  longer overwritten by the "ID checked" line; in `immediate` mode a legit ID held by an already
  Wanted player is seized rather than honoured, and an Enemy's legit ID is seized as revoked;
  `papers.id wipe` no longer treats a second word as a player name and is in the usage line;
  killing the fence is no longer booked as a Cobalt guard kill; a failed fence spawn is retried
  every minute up to five times; the fence's payment is verified before the tags are stripped or
  the papers handed over; worn Cobalt-tagged gear can be cleaned (the level-5 scan reads it);
  laundering keeps an item's own custom name; `/papers fence tp` no longer lands on a roof.

## [1.0.1] - 2026-09-12

Post-release review of Milestones 5 and 6 (`docs/reports/2026-09-12-review.md`,
`docs/reports/2026-09-12-security-scan.md`). No behaviour changes for players.

### Fixed
- A roadblock slot restored as fallen after a restart, whose respawn draw then found no road
  spot, would have spawned at the world origin; it now stays down and retries in 5 minutes.
- A roadblock refused by the guard cap stayed dead until the next level change; the 5-minute
  reconcile now retries it like a gate.
- `/papers gate prop` and `papers.cp propat` on a roadblock silently deleted the roadblock
  (props belong to gate templates); both now refuse anything but a gate.
- `papers.cp roadblocks` and `/papers gate list` threw with `RoadblocksByTier: []` (the way to
  switch roadblocks off).
- The remembered Outpost zone radius is restored whenever the live zone is found smaller on
  load, not only when it matches the current `CurfewRadius`, and is never overwritten by a
  smaller value.
- The dusk warning went to players up to 10 m outside the stock zone; only the vacated ring is
  warned now.
- A failure while restoring the zone on unload could skip the despawns; it is contained.
- A failed relocation draw no longer restarts the roadblock's hour (the next reconcile
  retries); a relocation clears its approach and run-over throttles with the cooldowns;
  `despawn` and `gate remove` mark the data file dirty.
- Fourth and later roadblock guards no longer stack on the third one's spot.
- The sentry turret is kept off the lamp power-fakery path; the chicane protection object is
  released on unload.

## [1.0.0] - 2026-09-12

Milestone 6: Outpost escalation — v1 complete (brief milestones 1–6, decision 0001).
`PapersPlease.cs` 0.6.0 → 0.6.6 on the live server, released as 1.0.0 (same code, version bump).
Server-side rows verified by the self-test driver 2026-09-10, player rows run live 2026-09-12
(`docs/reports/player-test-m5-m6-2026-09-10.md`).

### Added (Milestone 6)
- Outpost curfew: from threat level 3, between 18:00 and 06:00 in-game, the Outpost safe zone
  shrinks from 122 m to 45 m so the wall gates and approach roads are open ground until dawn;
  the wall-gate guards shoot Suspects and worse on sight while it lasts. Server-wide advisories at
  dusk and dawn, a warning to anyone standing in the vacated ring, and curfew lines on `/papers`
  and `/threat`. Everything is restored at dawn, on unload, on reload and when the level drops.
- Green Zone dressing at level 5: gate props now carry a minimum level, and three new kinds —
  sentry turret (peacekeeper: fires only at players Cobalt has flagged; capped at 6), siren (lit
  while the gate is on alert or under curfew) and floodlight (lit at night) — placed with
  `/papers gate prop <gate> turret|siren|flood 5`.
- Admin console: `papers.cp outpost`, `papers.cp curfew on|off|auto`, `papers.cp turret here|at`,
  `papers.cp roadblock at <x y z>`, `papers.cp propat`, `papers.cp kill <checkpoint>`.

### Fixed (Milestone 6)
- A fallen roadblock respawned by an admin came back at the same spot; it now moves like the
  timed respawn does.
- Floodlights vanished moments after spawning (the wall-light prefab destroys itself when a
  physics neighbour changes and no wall is behind it); the plugin now owns their lifetime.
- From the player test (0.6.4–0.6.6, 2026-09-12):
  - Running a guard over was not detected when the car is what hit them: a car's hurt trigger
    credits the driver, not the vehicle. The guard now recognises a mounted attacker whose
    weapon is a vehicle.
  - Chicane barricades disappeared one at a time. Every deployable barricade carries an NPC
    trigger that gibs it when an NPC walks in (so scientists can path through doors) — our
    guards are NPCs. The trigger is removed, barricades are immune to all damage but
    explosives, and every prop loses the ground-watch that killed searchlights on river banks.
  - Running the checkpoint and then hitting a guard on the way out charged −5 twice; one
    offence per pass now.
  - `/papers gate tp` put the body under the terrain at a rising road (anti-hack kick loop on
    every login); the drop point now snaps to the ground.
  - The `/papers` record line showed a raw colour tag: the client masks a number inside a
    colour tag. The band name is coloured instead of the score.
  - `papers.cp curfew on|off` says that the gates' safe-zone flags are re-read a second later.

## [0.5.7] - 2026-09-10

Milestone 5: roadblocks and the STOP rule — `PapersPlease.cs` 0.5.0 → 0.5.7 on the live server.
Server-side rows verified by the self-test driver 2026-09-10, player rows (vehicle, STOP rule,
run-over, layout, tier 4 guard, relocation) run live 2026-09-12 on the 0.6.x builds
(`docs/reports/m5-live-test-2026-09-09.md`, `docs/reports/player-test-m5-m6-2026-09-10.md`).
Fixes found in that run ship in 1.0.0.

### Added
- Roadblocks: Cobalt now sets up checkpoints at random spots on the main and secondary roads,
  sampled from the road network away from road ends, bends, slopes, water, monuments, other
  checkpoints, bases and rail lines, and only where the roadside is level enough for the guards
  to stand. Each has two guards beside the lane, a two-barricade chicane cars can thread slowly,
  and a searchlight. Roadblocks per level: 0 / 1 / 2 / 3 / 4, yielding to monument gates under a
  guard cap raised to 30; the third guard from level 4 covers the far side.
- Roadblocks move to a fresh spot every hour (deferred while a player is within 150 m or an
  encounter is running), and a fallen one returns somewhere else after the level's respawn time.
- The STOP rule: a vehicle with a real driver nearing a roadblock gets a chat line and a red
  **COBALT CHECKPOINT AHEAD — STOP** banner at 30 m. Inside the checkpoint the panel waits until
  the vehicle has stopped (under 1 m/s for 2 s, or the rider dismounts); driving on through
  counts as running the checkpoint (−5, hostile, server-wide line). Passengers are checked
  separately. Vehicles without a real driver (the Island Taxi) and trains are waved through.
- Running a guard over with a vehicle counts as running the checkpoint for the driver.
- Admin console: `papers.cp roadblocks`, `relocate [slot]`, `roadblock here` (a pinned test
  roadblock at the aim point), `roadsample [stay]` (draw a spot and teleport there, with the
  rejection tally). Config section `Roadblocks` (counts by level, relocation, sampler rules,
  approach radius, stop rule).
- Plugin API: `GetBand(string)` beside `GetBand(ulong)`; README consumer notes for the Island Taxi.

### Fixed
- The config file grew on every load: each list with defaults (contraband, voice lines, tier
  advisories) had the file's entries appended and written back — 30 copies after 30 loads.
  Lists are now replaced on load and an existing file is cleaned up once.

## [0.4.4] - 2026-09-09

Milestone 4: the threat clock and tier gating. Live-verified on the live server 2026-09-09 (test script
`docs/reports/m4-live-test-2026-09-09.md`; review `docs/reports/2026-09-09-review-2.md`).

### Added
- A real threat clock: a 0–100 value made of the wipe-age base (20 points per tier between the
  configured hour anchors, so the calendar tiers are unchanged) plus a violence bonus that
  decays half a point per hour and is capped at 15. Guard kills, checkpoint runs, base raids,
  evasions, Cobalt crate loots and scientist kills each add a configurable amount.
- Tier changes are announced server-wide with a Cobalt advisory per level (configurable), fire
  the `OnThreatTierChanged(old, new)` hook for other plugins, and take effect at checkpoints at
  once. The tier is remembered across restarts so the advisory is not repeated.
- `/threat` shows the tier, value, base, bonus and hours to the next level; admins can
  `/threat set <n>`, pin a tier with `/threat tier <1-5|off>` (persisted) or `/threat nudge <±n>`.
  `papers.threat` is the console twin with `simulate <hours>`.
- What the tier changes at gates: each gate has a minimum tier (`/papers gate tier <name> <n>`;
  gates above a lowered tier stand down at once and return when it rises); guards per gate are
  2/2/2/3/3 with the third guard a heavy scientist from tier 4, re-manned on the spot when the
  tier changes; worn clothing is searched from tier 5; a fallen gate returns after
  30/25/20/15/10 minutes by tier. Citizens searched and fines doubled from tier 4 now follow the
  real clock.
- `/papers` ends with the current threat level and what it means; `GetThreat()` API.
- Dev-server layout: Outpost gates tier 1, Harbour and Airfield gates tier 2.

### Fixed
- The nudge reply echoed the requested amount after the cap had clipped it.
- A tier crossing caused by a guard kill re-manned gates from inside the death hook; it now runs
  on the next tick. The threat file is written every five minutes rather than every minute.

## [0.3.11] - 2026-09-09

Milestone 3: monument gate checkpoints. Live-verified on the live server 2026-09-08/09 (test script and
results in `docs/reports/m3-live-test-2026-09-08.md`; review in `docs/reports/2026-09-09-review.md`).

### Added
- Cobalt guards: killable, uniformed custom-brain scientists that hold a post, track the nearest
  visitor, engage flagged players, chase no further than 40 m from their post, and idle when no
  player is within 100 m. Killing one costs −10 reputation; guards drop scientist loot.
- Gate placement in-game: `/papers gate add <name> [guards] [monument]` captures the admin's
  position and facing relative to the nearest monument, so placed gates transfer to the next map;
  `list`, `remove`, `tp`, `radius`, `prop` (barricades, searchlight) and `reload` round it out.
  `papers.cp monuments` shows which monument owns the spot you stand on.
- Checkpoint lifecycle: trigger volume, two flanking guards, optional props, a global guard cap
  (default 20), a minimum threat tier per gate, full teardown on unload, and a 20-minute respawn
  after the last guard dies that survives plugin reloads and server restarts.
- The "Papers, please" encounter: walking into a gate halts you with a voice line and a panel
  showing your band and verdict. Citizens pass (searched from tier 4); Neutrals are searched and
  may surrender contraband (+3) or refuse; Suspects pay a 50-scrap fine (+5, doubled from tier 4)
  or refuse; Wanted and Enemy players are shot on sight. Refusing, walking away or letting the
  20-second timer expire makes you hostile; running a checkpoint also costs −5.
- Contraband scan over main inventory and belt (worn items by config): the explosive/rocket/
  gunpowder list plus anything tagged `[COBALT]`; confiscated items are destroyed.
- Hostility that players can see: the native hostile flag (Outpost peacekeepers and sentries join
  in), a panel line stating the duration, `/papers` showing the seconds left, and a chat line when
  Cobalt stands down.
- Enemy alert: an Enemy sighting puts every gate within 300 m on alert for 5 minutes, during
  which Suspects and worse are engaged on sight.
- Monument perimeter: a guard-free watch around every gated monument. Entering it without
  clearing a gate in the last 10 minutes draws a warning (Neutral, tiers 1–3), a −3 penalty
  (tier 4+) or, for Suspects and worse, the hostile flag plus an alert on the monument's gates.
- Optional server-wide broadcasts when a checkpoint falls, a guard is killed, a checkpoint is run
  or a player is caught inside a monument; Cobalt voice lines are configurable.
- Admin console `papers.cp`: `list`, `respawn`, `despawn`, `guard` (debug spawn), `guards`,
  `cooldowns clear`, `alert`, `sweep` (orphaned Cobalt scientists), `log` (the plugin's own
  in-memory log, readable from the F1 console) and `status`.
- `/papers` also reports running checkpoint cooldowns.

### Changed
- Default guard cap raised from 12 to 20 (six gates on the dev server use 12).
- README documents checkpoints, gate placement and the new console commands.

### Fixed
- Gate placement attached an Airfield road gate to a nearby power substation (seven copies on the
  map, seven gates): the owning monument is now chosen by distance to its bounds, and small
  prefabs that report no bounds are penalised.
- A plugin reload no longer accuses players already inside a monument of evading its checkpoint.
- Alert log lines name the real cause (an evasion or an Enemy sighting).
- Gate names and guard counts from chat are validated; the wire barricade (hurt trigger) is no
  longer offered as a gate prop; per-player cooldown tables are pruned.

### Removed
- `PapersPleaseProbe.cs` taken off the dev server (Milestone 1 diagnostics; source stays in the
  repository for reference).

## [0.2.3] - 2026-09-08

### Added
- `PapersPlease.cs` 0.2.0–0.2.3 (Milestone 2): persisted reputation ledger with lazy decay and
  wipe reset; bands; detectors for vanilla scientist kills, tier-gated Cobalt crates (contents
  tagged `[COBALT]`), foreign-TC structure destruction, and explosive-armed partial-raid damage;
  `/papers` and `papers.rep` commands; throttled, summed notifications; plugin API
  (`GetReputation`, `GetBand`, `AdjustReputation`, `SetReputation`, `IsExempt`, `GetThreatTier`)
  and the `OnReputationChanged` hook. Live-tested 2026-09-08. (2026-09-07/08)

### Fixed
- 0.2.1: console args are `StringView[]`; permissions renamed `papersplease.admin` / `.exempt`.
- 0.2.2: a destroying blast charged only the −5 partial hit; the raid ledger now charges the
  difference so destruction always costs the full penalty. Throttled chat lines are summed.
- 0.2.3: one change path for detectors, admins and the API; exempt players move only on an
  explicit set/reset.

### Added (earlier)
- Project scaffold: brief, CLAUDE.md, PLAN.md, decision records 0001 (v1 scope, brief open
  questions) and 0002 (guard AI approach). (2026-09-07)
- Milestone 1 plan and assembly check against the 2026-09-04 build
  (`docs/reports/2026-09-07-assembly-check.md`). (2026-09-07)
- `PapersPleaseProbe.cs` 0.1.0 — throwaway Milestone 1 probe: posted custom-brain guard
  (`papers.probe.guard`), dormancy and engage switches, `TriggerBase` checkpoint trigger with
  vehicle rider resolution, "Papers, please" CUI, `MarkHostileFor` test, Outpost safe-zone
  dump/shrink/restore, wipe age, `[COBALT]` item tag + scan, barricade and searchlight spawns,
  status/log/clear. Compiled clean on the live server first deploy. Test script in
  `docs/reports/probe-2026-09-07.md`. (2026-09-07)
- Probe 0.1.1–0.1.10, same evening, driven by the live runs: `kill` via `Die()`, `sweep` for
  orphaned guards, 4 Hz think gate (`lastThinkTime`), `startHealth` after the swap, per-event
  trigger log throttle, guard hit logging, deferred `InSafeZone` read, guard trigger refresh on
  zone resize, damage-path instrumentation, in-memory guard log + version in status, and the
  root-cause fix `CopyPrefabFields` (players could not damage swapped guards: "Bone is invalid").
  Milestone 1 gate passed; findings summary in the probe report. (2026-09-07)

# Cobalt Papers Please

*"Checkpoints, papers, and a faction that cares who you are."*

Rust (Oxide) server plugin. Cobalt sets up checkpoints at monuments and on the roads and treats
every player according to a reputation score: Citizens walk through, Suspects get searched, Wanted
players get shot. As the wipe ages a global threat clock hardens the checkpoints, and Outpost goes
from safe zone to curfew to police state.

**Status: v1.4.0, all nine milestones built and verified in-game.** History in [CHANGELOG.md](CHANGELOG.md).

<!-- lpl:links -->
**[Download v1.4.0](https://github.com/lowpoplabs/PapersPlease/releases/latest)** · **Flyer:** [web](https://lowpoplabs.github.io/flyers/PapersPlease.html) / [PDF](PapersPlease-Flyer.pdf) · **[Changelog](CHANGELOG.md)** · **[Ko-fi](https://ko-fi.com/lowpoplabs)**
<!-- /lpl:links -->

## What it does

Reputation and the `/papers` record, monument gate checkpoints with guards and the Papers panel,
monument perimeters, the threat clock, roadblocks with the STOP rule, the Outpost curfew and
Green Zone dressing, Cobalt identity papers, forgeries, stolen IDs and the Bandit Camp fence,
Cobalt vaults and the sabotage chain that opens the Outpost one, foot patrols on the roads and
the Cobalt ambush with its arrest. With the sibling plugins loaded it also documents itself in
[HelpMenu](https://github.com/lowpoplabs/HelpMenu), lets
[PublicWorks](https://github.com/lowpoplabs/PublicWorks) repairs earn standing, and has
[Island Taxi](https://github.com/lowpoplabs/IslandTaxi) price fares by band. Each of those is optional.

## Installation

1. Drop `PapersPlease.cs` into `oxide/plugins/`.
2. `oxide/config/PapersPlease.json` is generated on first load; the defaults are what the sections
   below describe.
3. In game, place the gates, the fence, the vaults and the switch with the admin commands under
   [Admins](#admins). Nothing spawns until a gate is placed.

## Players

- `/papers` — your Cobalt record: score (−100..+100), band, and what checkpoints will do to you.
- Bands: **Citizen** ≥ 25 · **Neutral** −24..24 · **Suspect** −25..−59 · **Wanted** −60..−84 ·
  **Enemy of the State** ≤ −85. Standing drifts toward 0 by 1 point per hour.
- What costs reputation: killing scientists (−5), looting Cobalt property crates (−3; the loot
  is tagged `[COBALT]` and will not stack with clean items), destroying someone else's base
  structures with explosives (−15 per base per 5 min), explosive damage that does not destroy (−5).
- Military crates only count as Cobalt property from threat tier 3 (later in the wipe).

## Checkpoints

- Cobalt posts two guards at each placed gate (Outpost's roads, Airfield's roads, the Harbour
  bridges). Walking into the gate's 8 m trigger halts you: a voice line in chat and the
  **PAPERS, PLEASE** panel with your band and the verdict.
- **Citizens** are waved through (searched from tier 4; contraband is taken with a warning).
  **Neutrals** are searched: clean → pass; contraband → **Surrender** (+3, items destroyed) or
  **Refuse** (hostile). **Suspects** pay a fine of 50 scrap (100 from tier 4) or refuse.
  **Wanted** and **Enemy** are shot on sight; an Enemy sighting alerts every gate within 300 m.
- Contraband = explosives, rockets, launchers, gunpowder (config list) plus anything tagged
  `[COBALT]`, searched in your main inventory and belt.
- Walking out of the trigger before answering, or letting the 20 s timer run out, means you
  **ran the checkpoint**: −5 and hostile. Hostile lasts 120 s: the gate's guards, Outpost's
  peacekeepers and its sentries all shoot on sight, the HUD shows the red hostile icon, and chat
  tells you when Cobalt stands down. A cleared player is not re-halted at that gate for 2 min.
- Every gated monument has a **perimeter**: enter it without clearing a gate in the last 10 min
  (through a fence hole, over the water) and Cobalt notices. Neutrals get a warning (−3 from
  tier 4); Suspects and worse are flagged hostile and the monument's gates go on alert.
- Killing a guard costs −10. When a gate's last guard dies the checkpoint falls (server-wide
  line) and Cobalt rebuilds it after the level's respawn time (30 minutes at level 1 down to
  10 at level 5). Guards drop scientist loot.

## Papers

- Cobalt issues **identity documents**: a note item named "Cobalt ID" with your name and a
  serial (C-1, C-2 …). The identity lives server-side, keyed to that exact note, so an edited
  or copied note is just paper. Citizens get one free and Neutrals pay 20 scrap, either with
  `/papers request` while standing at a checkpoint (within 5 m of the trigger) or from the
  **TAKE / BUY COBALT ID** button the pass panel shows when you hold none. A new ID revokes
  the old one; `/papers` tells you which serial you hold and whether it is on you.
- Carry it in your main inventory or belt. At a scan a **valid ID is worth one band**, capped
  at Neutral: a Suspect with papers is searched instead of fined; a Neutral is searched as
  usual (an ID never skips the search); a Citizen gains nothing. A Wanted holder is honoured
  one last time (fined as a Suspect) and the document is kept: Cobalt revokes papers when the
  owner's standing falls to Wanted. Revoked papers and papers in someone else's name (−5) are
  seized at the next gate. An Enemy of the State gets no benefit from any paper.
- **Forged papers** come from the fence (below). They look identical and are rolled at every
  scan: spotted 10 / 20 / 35 / 50 / 70 % of the time at threat levels 1–5. Below level 3 a
  spotted forgery is seized for −10 and the normal verdict follows; from level 3 the holder is
  made **Wanted** on the spot (score set to −60, guards open fire, server-wide line).
- **Stolen papers**: about one dead checkpoint guard in ten carries a Cobalt ID in his own
  name; loot the body. It is treated as a forgery at a scan, so it is a gamble, not a pass.

## The fence

- A Bandit Camp trader (placed once by an admin with `/papers fence add`) who never draws a
  weapon and takes no damage. Walk up to him (4 m) and his panel lists every `[COBALT]` stack
  on you; **CLEAN** strips the tag from all of them for 15 scrap per stack (Cobalt-tagged scrap
  spends at par). Cleaned loot passes a checkpoint search like anything else.
- He also sells **forged papers** for 40 scrap to anyone but an Enemy of the State, who is
  turned away entirely. Every visit nudges Cobalt's threat clock a little.

## Vaults and the sabotage chain

- From threat level 3 Cobalt keeps **vaults**: a 3×3 m armored room with a code-locked door,
  crates inside and guards at the door, placed once by an admin at a monument. Outpost has one
  inside the compound; others can go anywhere. It grows with the level: two guards at 3, two
  heavies at 4, three heavies and two sentries of its own at 5. What it holds is a config table
  by level (`Vaults.LootByTier`): by default scrap, high-quality metal, HV rockets, rockets and
  C4 — 2000 / 250 / 25 / 25 / 25 at level 3, 3000 / 500 / 50 / 50 / 50 at 4, 4000 / 1000 /
  100 / 100 / 100 at 5, split across the vault's crates.
- The door opens for **nobody**, admins included, and no papers get you in: a vault has to be
  breached. Looting the crates costs reputation and tags the loot `[COBALT]`. (A server can
  list bands in `Vaults.OpenBands` to let a legit Cobalt ID in the holder's own name open the
  door for them; the default is none.)
- The door takes three C4 (1,000 hp). Breaching it costs −15, flags you hostile, puts the
  vault and the monument's gates on alert for 10 minutes, and goes out server-wide. Cobalt
  rebuilds the door after 20 minutes and restocks the crates an hour after the last one is
  emptied; the guards come back with the door. C4 cannot be armed inside a safe zone, which
  is what the chain below is for.
- From level 5 the Bandit Camp fence sells a **bypass tool** (150 scrap, tagged Cobalt property
  so a gate search takes it) and tells you which map grid the switch is in. Used on the switch
  at the substation nearest Outpost it opens a **ten-minute window**: the Outpost safe zone
  drops, the compound's own sentries go offline, the floods go out and the sirens come on,
  every Outpost checkpoint goes on alert, you are hostile to every Cobalt gun for the window,
  a response team is radioed in and arrives at the substation twenty seconds later, a red
  **VAULT WINDOW** countdown sits at the top of your screen, and the whole server hears about
  it. Flipping the switch without the tool does nothing ("the panel is sealed"). The window
  costs −5 and can be opened once an hour; when it ends everything is restored and the server
  hears that too.

## Patrols and the ambush

- From threat level 2 Cobalt walks the roads: **patrols** of two guards, one four metres behind
  the other, pacing a 300 m stretch of road end to end (one at levels 2 and 3, two at 4, three
  at 5). Anyone **Neutral or worse** who comes within 8 m of the lead is pulled over with the
  same panel and verdicts as a gate; a Citizen hears "Move along" and walks on. A vehicle gets
  "Cobalt patrol ahead" from 30 m and the STOP rule. Nothing happens inside a safe zone. A
  wiped patrol is announced and comes back on a fresh road after the level's respawn time;
  after a 45-minute shift a patrol stands down and returns 15 minutes later somewhere else.
- From level 3 the **ambush**: every time Cobalt turns on an **Enemy of the State** (a gate or
  patrol verdict, an evasion, a run, a vault breach) is a sighting. Once per 30 minutes per
  player, "Cobalt radio: a unit is coming for you", and 45 seconds later the level's squad (two
  guards at 3, three with two heavies at 4, four heavies at 5) appears 80 m from wherever you
  are by then, out of your sight, and hunts you for five minutes. They stop at the edge of a
  safe zone and 40 m short of any tool cupboard's range you stand in, and stand down when the
  time is up, when you die or log off, or when they are all dead.
- **Downed by the squad means arrested**, not killed: a hunter walks up, and five seconds later
  you are stripped of everything, cuffed, hooded and left on the spawn beach with 30 health —
  "taken into custody", server-wide. The cuffs come off the vanilla way.

## Roadblocks

- From threat level 2 Cobalt also sets up roadblocks on the main and secondary roads (1 at
  level 2, 2 at level 3, 3 at level 4, 4 at level 5), each with two guards beside the lane, a
  two-barricade chicane a car can thread at walking pace, and a searchlight; from level 4 a
  third, heavy guard covers the far side. They move to a fresh spot every hour (never while a
  player is within 150 m or someone is being checked) and a fallen one returns somewhere else.
- **Stop for the checkpoint.** A vehicle with a driver gets a chat line and a red
  **COBALT CHECKPOINT AHEAD — STOP** banner at 30 m. Inside the checkpoint the panel opens once
  the vehicle has stopped (under 1 m/s for 2 s, or the rider gets out). Driving on through,
  leaving before answering, or running a guard over all count as **running the checkpoint**:
  −5 and hostile, once per pass. Passengers are checked separately. Vehicles without a real
  driver and trains are waved through.
- The chicane cannot be shot or driven down; only explosives remove it.

## Threat level

- `/threat` shows Cobalt's threat level 1–5 and the 0–100 value behind it: a wipe-age base
  (tier 2 at 48 h, 3 at 120 h, 4 at 240 h, 5 at 400 h by default) plus a violence bonus that
  rises with guard kills, checkpoint runs, raids, evasions and Cobalt looting and drains by half
  a point per hour of quiet (capped at 15, so violence hurries the next level but never skips one).
- Every level change is announced server-wide. Levels change the gates: which monuments are
  gated (each gate has a minimum level), how many guards stand there (2 up to level 3, then 3
  with a heavy), whether Citizens are searched and fines doubled (level 4), whether worn gear is
  searched (level 5), and how fast a fallen gate returns (30 minutes at level 1 down to 10).
- `/papers` ends with the current level and what it means.

## Outpost

- From threat level 3 Outpost is under **curfew** at night (18:00–06:00 in-game): the safe zone
  shrinks from 122 m to 45 m, so the wall gates and the approach roads are open ground until
  dawn, and the wall-gate guards shoot Suspects and worse on sight. A server-wide advisory marks
  each transition; `/papers` and `/threat` say when the curfew is in effect; anyone standing in the
  vacated ring at dusk gets a warning.
- At level 5 the **Green Zone** dressing appears: sentry turrets, floodlights and sirens placed
  with `/papers gate prop <gate> turret|flood|siren 5`. Turrets are peacekeepers (they fire only at
  players Cobalt has flagged), sirens light while a gate is on alert or under curfew, floods at
  night. Everything vanishes again when the level drops and on unload.

## Admins

- Permissions: `papersplease.admin` (read/set anyone), `papersplease.exempt` (no reputation
  changes; reads as Citizen to other plugins). Server admins are not exempt by default.
- Chat: `/papers <player>`, `/papers set|add|reset <player> <n> [reason]`, `/papers top [n]`,
  `/papers reload` (config + all checkpoints). Threat: `/threat set <0-100>`, `/threat tier
  <1-5|off>` (pin, persisted — for tests and events), `/threat nudge <±n>`; console
  `papers.threat … | simulate <hours> | save`.
- Gate placement (chat, in-game): stand beside the road facing arriving traffic and run
  `/papers gate add <name> [guards] [monument]`; then `list`, `remove <name>`, `tp <name>`,
  `tier <name> <1-5>`, `radius <name> <2-60>`,
  `prop <name> <concrete|sandbags|metal|stone|cover.wood|light|turret|siren|flood> [tier]`
  (captures your position as the prop's offset; the tier is the level from which it appears,
  default 1), `reload`. Gates are stored as
  monument-local offsets, so a placed set transfers to the next map. Gates inside a safe zone
  are logged as "execution zone" (guards immune, players disarmed); outside is a fair fight.
- Fence placement (chat, in-game): stand where he should lean, facing his customers, and run
  `/papers fence add [name] [monument]`; then `list`, `remove <name>`, `tp <name>`. He keeps
  the height you stood at (Bandit's walkways) and comes back at once on `/papers reload`.
- Papers (console): `papers.id list [player] | issue <player> | forge <player> |
  revoke <player> | wipe` (wipe forgets every record for a test; the notes out there become
  plain paper) and `papers.fence status | at <x y z> <yaw> <name> | remove <name>`.
- Console / RCON: `papers.rep version | hooks | get <player> | set|add <player> <n> [reason] |
  reset <player> | top [n] | simulate <player> <hours> | save` and
  `papers.cp list | gate … | respawn|despawn <name> | guard [heavy] | guards | clearloose |
  cooldowns clear | alert <name> | monuments | roadblocks | relocate [slot] | roadblock here|at
  <x y z> | roadsample [stay] | outpost | curfew on|off|auto | turret here|at <x y z> | propat
  <gate> <kind> <tier> <x y z> | kill <name> | sweep | log [n] | status`. `papers.cp log` is the
  plugin's own in-memory log (everything the checkpoints decide), readable from the F1 console.
- Vaults (chat, in-game): aim at flat ground where the door should stand and run
  `/papers vault add <name> [monument]` (the room is built behind the door, away from you);
  then `crate <name>` / `guard <name>` where you stand to add crate and guard spots, `list`,
  `remove <name>`, `tp <name>`, and the test levers `open|close|breach|restock <name>`. Console:
  `papers.vault …` with the same words. Vault state (door rebuild and restock times) is in
  `checkpoints.json` and survives a restart.
- Sabotage (chat, in-game): stand at the substation nearest Outpost, aim at its panel and run
  `/papers sabotage switch [monument]`; then `status`, `remove switch`, `tp switch`, and the test
  levers `cut` (opens the window without the tool) and `restore` (ends it and clears the hour's
  cooldown). Console: `papers.sabotage …`. `papers.cp safezone off|on|status` drops or restores
  the Outpost safe zone on its own; `papers.cp lights` lists the compound's light entities;
  `papers.cp vaultdoor|switch here`, `turrets on|off` and `loose` are the Milestone 8 probe
  levers (loose props, cleared with `clearloose`).
- Patrols (console): `papers.cp patrols [list | relocate <slot>]` lists every slot with its
  road, route length, the lead's position and the leg he is on, or re-draws one; `papers.cp kill
  patrol-n` loses one (it comes back on a new road). Nothing to place: routes are drawn by the
  roadblock sampler.
- Ambush (console): `papers.ambush status | dispatch <player> | stand` — `dispatch` puts the
  level's squad on anyone regardless of band, tier or cooldown (a test lever), `stand` ends
  every squad. The probe levers `papers.cp walk <m> [x y z] | movetrigger | ambushspot [player] |
  navprobe [player | x y z] | cupboard [player]` are what the Milestone 9 probe used. `papers.cp spawners list|off|on|delay <s>`
  lists the compound's own NPC spawn groups (the peacekeepers: thirteen one-man spawners on a
  25–35 s clock) and pauses, resumes or re-times them; the window pauses them by itself
  (`Sabotage.PauseVanillaSpawners`, on by default) so a dropped peacekeeper stays down until it
  ends. Everything is put back on close, reload and unload.
- Config: `oxide/config/PapersPlease.json` (bands, penalties, decay, Cobalt crate list with tier
  gates, contraband list + tag, threat curve, checkpoint tuning — guard cap 30, trigger radius,
  fine, hostile/cooldown/timeout seconds, respawn minutes by level, alert radius, perimeter
  grace — the placed `Gates` with their props, the `Roadblocks` section (count by level,
  relocation minutes and clear radius, sampler clearances for road ends, bends, slopes, water,
  monuments, checkpoints, cupboards, rails and edge drops, approach radius, stop rule, skip
  driverless), the `Outpost` section (curfew level, hours, radius, Suspect hostility, turret
  cap), the `Papers` section (fees for Citizens and Neutrals, request range, what happens to a
  legit ID at Wanted — grace, immediate or never — the forgery odds by level, the level from
  which a spotted forgery means Wanted, the penalties, the stolen-ID chance, the optional
  workbench forge route), the `Fence` section (the placed `Fences`, name, clothing, unarmed,
  serve Enemies, trigger radius, scrap per stack, invulnerable, respawn minutes, forged-ID
  price), the `Vaults` section (the placed `Vaults`, the level they appear from, guards by
  level, which bands a legit ID opens the door for (none by default), breach penalty and alert, door rebuild and
  restock minutes, room or doorway mode), the `Sabotage` section (the level the switch appears
  from, the captured switch pose, the tool item, name and price, window minutes, what the
  window drops — safe zone, vanilla sentries, lights — the response squad's size, delay,
  distance and stay, the cut penalty and cooldown), the `Patrols` section (patrols by level,
  their own guard cap, route length, relocation minutes and clear radius, the best band still
  stopped, shift and gap minutes, the stall seconds) and the `Ambush` section (the level it
  starts, the sighting bands, cooldown, dispatch delay, squad and heavies by level, spawn
  distance, hunt minutes, the cupboard standoff and `EnterBases`, `MaxSquads`, the radio line,
  and the arrest: on/off, seconds, wake-up health, whether it strips, the broadcast) and the
  Cobalt voice lines). Data:
  `oxide/data/PapersPlease/reputation.json` (reset on wipe by default), `checkpoints.json`
  (destroyed-until times, vault door rebuild and restock times, the Outpost zone's stock
  radius), `threat.json` (violence bonus, pin, last announced level; reset on wipe) and
  `ids.json` (the ID registry by note uid; reset on wipe).

## Plugin API

`GetReputation(ulong)`, `GetBand(ulong)` → "Citizen".."Enemy" (exempt players read as Citizen;
`GetBand(string)` takes the SteamID as text), `AdjustReputation(ulong, int, string)` → new score,
`SetReputation(ulong, int, string)`, `IsExempt(ulong)`, `GetThreatTier()` → 1..5,
`GetThreat()` → 0..100. Hooks: `OnReputationChanged(ulong id, int oldScore, int newScore, string reason)`
and `OnThreatTierChanged(int oldTier, int newTier)`. Consumers declare
`[PluginReference] private Plugin PapersPlease;` and call `PapersPlease?.Call("GetBand", (ulong)player.userID)`
— pass a real `ulong` (the 2026 `userID` is a struct; cast it) or the id as a string. A missing
plugin returns null, so treat null as "Neutral". Island Taxi 1.6.0 reads `GetBand` to price fares;
PublicWorks 2.9.0 raises `OnPublicWorksRepair` and `OnPublicWorksPurchase`, which this plugin
listens for to award standing. HelpMenu reads the `GetHelpInfo` hook for the help page.

## Compatibility

- Built and tested on the September 2026 Rust update (build 2633.288) on Oxide. Optional
  integrations: HelpMenu, PublicWorks 2.9.0+, Island Taxi 1.6.0+. Nothing is required.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

MIT — see [LICENSE](LICENSE).

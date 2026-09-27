using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Cobalt Papers Please", "LowPopLabs", "1.4.1")]
    [Description("Cobalt runs checkpoints and keeps a reputation on every player. Milestone 6: Outpost curfew (night safe-zone shrink, Suspects hostile at the wall gates) and the tier-5 Green Zone dressing.")]
    public class PapersPlease : RustPlugin
    {
        // -----------------------------------------------------------------
        // Sections: Config · Lang · Lifecycle · ReputationStore (data + lazy decay) ·
        // ReputationService (bands, adjust, notify, hook) · Threat tier stub ·
        // Crime detectors (scientist kills, Cobalt crates, raids) · Guards (GuardNpc /
        // GuardBrain / CopyPrefabFields) · Checkpoints (templates, placement, lifecycle) ·
        // Commands · API
        // Conventions: no reflection, no LINQ in hooks, hot hooks subscribed only
        // while needed, every player-facing string through lang, everything spawned has
        // enableSaving=false and dies on Unload.
        // -----------------------------------------------------------------

        // Oxide warns unless permissions carry the plugin-name prefix (seen at first load).
        private const string PermAdmin = "papersplease.admin";
        private const string PermExempt = "papersplease.exempt";
        private const string DataFile = "PapersPlease/reputation";
        private const string CheckpointDataFile = "PapersPlease/checkpoints";
        private const string ThreatDataFile = "PapersPlease/threat";
        private const string RoamPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
        private const string HeavyPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
        private const string SearchLightPrefab = "assets/prefabs/deployable/search light/searchlight.deployed.prefab";
        private const string SentryPrefab = "assets/content/props/sentry_scientists/sentry.scientist.static.prefab";
        private const string SirenPrefab = "assets/prefabs/deployable/playerioents/lights/sirenlight/electric.sirenlight.deployed.prefab";
        private const string FloodPrefab = "assets/prefabs/deployable/playerioents/lights/simplelight.prefab";
        // Milestone 8 (decision 0009): the vault door and its lock, the sabotage switch, the vault crate (the terminal is a probe prop only).
        private const string VaultDoorPrefab = "assets/prefabs/building/door.hinged/door.hinged.toptier.prefab";
        private const string CodeLockPrefab = "assets/prefabs/locks/keypad/lock.code.prefab";
        private const string SwitchPrefab = "assets/prefabs/deployable/playerioents/simpleswitch/switch.prefab";
        // The static (monument) station: the deployable one spawns an IO child and dismounts anyone while it is
        // unpowered (probe 2026-09-18 "has no power"); the static variant has no child and needs nothing.
        private const string TerminalPrefab = "assets/prefabs/deployable/computerstation/computerstation.static.prefab";
        private const string EliteCratePrefab = "assets/bundled/prefabs/radtown/crate_elite.prefab";
        // The vault room (1.1.8): Outpost's building doors are static geometry, not entities, so the
        // vault is its own 3×3 m armored building — foundation, three walls, a doorway wall, a ceiling.
        private const string FoundationPrefab = "assets/prefabs/building core/foundation/foundation.prefab";
        private const string WallPrefab = "assets/prefabs/building core/wall/wall.prefab";
        private const string DoorwayPrefab = "assets/prefabs/building core/wall.doorway/wall.doorway.prefab";
        private const string CeilingPrefab = "assets/prefabs/building core/floor/floor.prefab";
        // World | Construction | Terrain — the ground-snap mask the probe verified live (2026-09-07).
        private const int GroundMask = 10551296;

        private static PapersPlease _instance;
        private ConfigData _config;
        private ReputationStore _store;
        private Timer _saveTimer;

        #region Config

        private class ConfigData
        {
            [JsonProperty("Bands (score thresholds; Neutral is everything between SuspectMax and CitizenMin)")]
            public BandConfig Bands = new BandConfig();

            [JsonProperty("Reputation")]
            public ReputationConfig Reputation = new ReputationConfig();

            [JsonProperty("CobaltCrates (container short prefab name -> minimum threat tier at which looting it is a crime)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> CobaltCrates = new Dictionary<string, int>
            {
                { "crate_elite", 1 },
                { "codelockedhackablecrate", 1 },
                { "heli_crate", 1 },
                { "bradley_crate", 1 },
                { "supply_drop", 1 },
                { "crate_normal_2", 3 }, // military crate: legal early game (decision 0003)
            };

            [JsonProperty("Contraband")]
            public ContrabandConfig Contraband = new ContrabandConfig();

            [JsonProperty("ThreatCurve (hours since wipe at which tiers 1..5 begin; the threat value is 20 points per tier, interpolated)")]
            public int[] TierHours = { 0, 48, 120, 240, 400 };

            [JsonProperty("Threat")]
            public ThreatConfig Threat = new ThreatConfig();

            [JsonProperty("Checkpoints")]
            public CheckpointConfig Checkpoints = new CheckpointConfig();

            [JsonProperty("Gates (placed in-game with /papers gate add; positions are monument-local and transfer between maps)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<GateTemplate> Gates = new List<GateTemplate>();

            [JsonProperty("Roadblocks (decision 0006)")]
            public RoadblockConfig Roadblocks = new RoadblockConfig();

            [JsonProperty("Outpost (decision 0007: curfew and the Green Zone)")]
            public OutpostConfig Outpost = new OutpostConfig();

            [JsonProperty("Papers (decision 0008: Cobalt IDs and forgeries)")]
            public PapersConfig Papers = new PapersConfig();

            [JsonProperty("Fence (decision 0008: the Bandit Camp fence launders [COBALT] loot)")]
            public FenceConfig Fence = new FenceConfig();

            [JsonProperty("Vaults (decision 0009: a locked Cobalt door in a monument doorway that a Citizen's ID opens)")]
            public VaultsConfig Vaults = new VaultsConfig();

            [JsonProperty("Sabotage (decision 0009 B': the fence's bypass tool on the substation switch opens the Outpost vault window)")]
            public SabotageConfig Sabotage = new SabotageConfig();

            [JsonProperty("Patrols (decision 0010: two Cobalt guards on foot walking a road, pulling over Neutral and worse)")]
            public PatrolsConfig Patrols = new PatrolsConfig();

            [JsonProperty("Ambush (decision 0010: a Wanted or Enemy sighting sends a squad after the player)")]
            public AmbushConfig Ambush = new AmbushConfig();

            [JsonProperty("VoiceLines (Cobalt guard chat lines; one is picked at random per moment)")]
            public VoiceConfig Voice = new VoiceConfig();
        }

        private class PapersConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("IdName (display name of the note item)")] public string IdName = "Cobalt ID";
            [JsonProperty("IdFeeCitizen (scrap a Citizen pays for a legit ID at a checkpoint; 0 = free)")] public int IdFeeCitizen = 0;
            [JsonProperty("IdFee (scrap a Neutral pays for a legit ID at a checkpoint)")] public int IdFee = 20;
            [JsonProperty("RequestRange (metres beyond a checkpoint's trigger where /papers request still counts as 'at the checkpoint')")] public float RequestRange = 5f;
            [JsonProperty("IdUpgradesEnemy (a valid ID never softens an Enemy verdict unless true)")] public bool IdUpgradesEnemy = false;
            [JsonProperty("NotYoursPenalty (carrying someone else's legit ID; the document is seized)")] public int NotYoursPenalty = -5;
            [JsonProperty("LegitRevoke (what happens to a legit ID when its owner is Wanted or worse: 'grace' = honoured one last time at the next scan, then seized; 'immediate' = revoked the moment the band falls; 'never')")] public string LegitRevoke = "grace";
            [JsonProperty("ForgeAtWorkbench (/papers forge at a workbench with ForgeRecipe; off = forged papers come only from the fence)")] public bool ForgeAtWorkbench = false;
            [JsonProperty("ForgeWorkbenchLevel")] public int ForgeWorkbenchLevel = 2;
            [JsonProperty("ForgeRecipe (item short name -> amount, taken from main + belt)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> ForgeRecipe = new Dictionary<string, int> { { "paper", 1 }, { "scrap", 25 }, { "cloth", 10 } };
            [JsonProperty("ForgeFailByTier (percent chance a forged ID is spotted at a scan, tiers 1..5)")] public int[] ForgeFailByTier = { 10, 20, 35, 50, 70 };
            [JsonProperty("ForgeWantedFromTier (from this tier a spotted forgery makes the holder Wanted; below it ForgeFailPenalty applies)")] public int ForgeWantedFromTier = 3;
            [JsonProperty("ForgeFailPenalty (reputation for a spotted forgery below ForgeWantedFromTier)")] public int ForgeFailPenalty = -10;
            [JsonProperty("ForgeFailSetsScore (a spotted forgery from ForgeWantedFromTier sets the score to at most this)")] public int ForgeFailSetsScore = -60;
            [JsonProperty("BroadcastForgery (server-wide line when a forgery is spotted from ForgeWantedFromTier)")] public bool BroadcastForgery = true;
            [JsonProperty("NudgeForgery (threat bonus per spotted forgery)")] public double NudgeForgery = 0.5;
            [JsonProperty("StolenIdChance (0-1: a dead checkpoint guard drops a forged-grade Cobalt ID)")] public double StolenIdChance = 0.10;
        }

        public class FenceTemplate
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Monument (prefab short name, e.g. bandit_town)")] public string Monument = "";
            [JsonProperty("LocalPosition (x y z relative to the monument)")] public string LocalPosition = "0 0 0";
            [JsonProperty("LocalYaw (degrees relative to the monument; the fence faces this way)")] public float LocalYaw;
        }

        private class FenceConfig
        {
            [JsonProperty("Fences (placed in-game with /papers fence add; positions are monument-local and transfer between maps)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<FenceTemplate> Fences = new List<FenceTemplate>();
            [JsonProperty("Name (display name)")] public string Name = "Fence";
            [JsonProperty("Heavy (heavy scientist variant)")] public bool Heavy = false;
            [JsonProperty("Clothing (item shortnames, optional @skinid, worn instead of the scientist suit; empty = keep the suit) — like the Island Taxi driver", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Clothing = new List<string> { "hat.cap", "sunglasses", "tshirt.long", "pants", "shoes.boots" };
            [JsonProperty("Unarmed (empty the belt so the fence never raises a weapon at customers)")] public bool Unarmed = true;
            [JsonProperty("ServeEnemies (false = an Enemy of the State is turned away: no papers, no laundering)")] public bool ServeEnemies = false;
            [JsonProperty("TriggerRadius (metres; walking this close opens the fence's panel)")] public float TriggerRadius = 4f;
            [JsonProperty("ScrapPerStack (price to strip the [COBALT] tag from one stack)")] public int ScrapPerStack = 15;
            [JsonProperty("Invulnerable (the fence takes no damage; nothing to gain by killing him)")] public bool Invulnerable = true;
            [JsonProperty("RespawnMinutes (after the fence is killed — only possible with Invulnerable off or an admin kill)")] public int RespawnMinutes = 20;
            [JsonProperty("NudgeLaundered (threat bonus per laundering visit)")] public double NudgeLaundered = 0.25;
            [JsonProperty("ForgedIdScrap (price of forged papers at the fence; 0 = the fence sells none) — Suspects and worse get papers here")] public int ForgedIdScrap = 40;
        }

        private class BandConfig
        {
            [JsonProperty("CitizenMin")] public int CitizenMin = 25;
            [JsonProperty("SuspectMax")] public int SuspectMax = -25;
            [JsonProperty("WantedMax")] public int WantedMax = -60;
            [JsonProperty("EnemyMax")] public int EnemyMax = -85;
        }

        private class ReputationConfig
        {
            [JsonProperty("ScientistKill")] public int ScientistKill = -5;
            [JsonProperty("GuardKill (Cobalt checkpoint guard)")] public int GuardKill = -10;
            [JsonProperty("RanCheckpoint (left a checkpoint before answering, or let the decision timer expire)")] public int RanCheckpoint = -5;
            [JsonProperty("Surrender (handed contraband to a checkpoint)")] public int Surrender = 3;
            [JsonProperty("FinePaid")] public int FinePaid = 5;
            [JsonProperty("EvadedCheckpoint (entered a gated monument without clearing its gate)")] public int Evaded = -3;
            [JsonProperty("CobaltCrateLooted")] public int CobaltCrate = -3;
            [JsonProperty("StructureDestroyed (foreign TC building)")] public int StructureDestroyed = -15;
            [JsonProperty("PartialRaidDamage (explosion damage that does not destroy)")] public int PartialRaid = -5;
            [JsonProperty("RaidCapMinutes (one hit per attacker per building in this window)")] public int RaidCapMinutes = 5;
            [JsonProperty("PartialRaidDetector (arm OnEntityTakeDamage after explosives are used)")] public bool PartialRaidDetector = true;
            [JsonProperty("PartialRaidWindowSeconds")] public int PartialRaidWindowSeconds = 60;
            [JsonProperty("CrateMemoryMinutes (a crate counts once within this window)")] public int CrateMemoryMinutes = 10;
            [JsonProperty("PublicWorksRepairMinor (finished a Public Works repair contract, minor fault; needs PublicWorks 2.9+)")] public int PublicWorksRepairMinor = 10;
            [JsonProperty("PublicWorksRepairMajor (finished a Public Works repair contract, major fault)")] public int PublicWorksRepairMajor = 20;
            [JsonProperty("PublicWorksPurchase (paid for a day of a Public Works service; once per player per UTC day)")] public int PublicWorksPurchase = 2;
            [JsonProperty("DecayPerHour (points toward 0 per hour, both directions)")] public double DecayPerHour = 1.0;
            [JsonProperty("ResetOnWipe")] public bool ResetOnWipe = true;
            [JsonProperty("NotifyThrottleSeconds")] public int NotifyThrottleSeconds = 5;
            [JsonProperty("SaveIntervalSeconds")] public int SaveIntervalSeconds = 60;
        }

        private class ContrabandConfig
        {
            [JsonProperty("Tag (display-name suffix marking Cobalt property)")] public string Tag = " [COBALT]";
            [JsonProperty("Items (short names; scanned at checkpoints)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Items = new List<string>
            {
                "explosive.timed", "explosive.satchel", "grenade.beancan", "grenade.f1", "rocket.basic",
                "rocket.hv", "rocket.fire", "ammo.rocket.basic", "ammo.rocket.hv", "ammo.rocket.fire",
                "rocket.launcher", "explosives", "gunpowder"
            };
            [JsonProperty("ScanWear (also search worn clothing)")] public bool ScanWear = false;
        }

        private class ThreatConfig
        {
            [JsonProperty("NudgeDecayPerHour (violence bonus drains toward 0 by this much per hour of quiet)")] public double NudgeDecayPerHour = 0.5;
            [JsonProperty("NudgeMax (the violence bonus never exceeds this many points; 20 = one tier)")] public double NudgeMax = 15;
            [JsonProperty("NudgeGuardKill")] public double NudgeGuardKill = 2;
            [JsonProperty("NudgeRanCheckpoint")] public double NudgeRanCheckpoint = 1;
            [JsonProperty("NudgeStructureDestroyed")] public double NudgeStructureDestroyed = 1;
            [JsonProperty("NudgeEvaded")] public double NudgeEvaded = 0.5;
            [JsonProperty("NudgeCobaltCrate")] public double NudgeCobaltCrate = 0.25;
            [JsonProperty("NudgeScientistKill")] public double NudgeScientistKill = 0.25;
            [JsonProperty("CheckIntervalSeconds (how often the tier is re-evaluated)")] public int CheckIntervalSeconds = 60;
        }

        public class CheckpointConfig
        {
            [JsonProperty("GuardCap (live Cobalt guards map-wide; gates beyond it stay unspawned, roadblocks yield to gates)")] public int GuardCap = 30;
            [JsonProperty("GuardsPerGate (fallback when GuardsByTier is empty; a gate's own Guards overrides both)")] public int GuardsPerGate = 2;
            [JsonProperty("GuardsByTier (guards per gate at tiers 1..5)")] public int[] GuardsByTier = { 2, 2, 2, 3, 3 };
            [JsonProperty("HeavyFromTier (from this tier every guard beyond the first two is a heavy scientist)")] public int HeavyFromTier = 4;
            [JsonProperty("ScanWearFromTier (worn clothing is searched from this tier; Contraband.ScanWear forces it at every tier)")] public int ScanWearFromTier = 5;
            [JsonProperty("RespawnMinutesByTier (minutes after the last guard dies, tiers 1..5; empty = RespawnMinutes)")] public int[] RespawnMinutesByTier = { 30, 25, 20, 15, 10 };
            [JsonProperty("GuardFlankMeters (guards stand this far left/right of the gate point, beside the lane)")] public float GuardFlank = 2.5f;
            [JsonProperty("TriggerRadius (default for new gates)")] public float TriggerRadius = 8f;
            [JsonProperty("GuardIdleRange (guards stop sensing/aiming when no real player is within this many metres)")] public float IdleRange = 100f;
            [JsonProperty("GuardAimRange (a posted guard tracks the nearest player within this range)")] public float AimRange = 25f;
            [JsonProperty("GuardCombatRange")] public float CombatRange = 30f;
            [JsonProperty("GuardLeashMeters (a hostile guard chases no further than this from its post)")] public float Leash = 40f;
            [JsonProperty("EncounterCooldownSeconds (a cleared player is not re-halted at the same gate for this long)")] public int EncounterCooldownSeconds = 120;
            [JsonProperty("DecisionTimeoutSeconds")] public int DecisionTimeoutSeconds = 20;
            [JsonProperty("HostileSeconds (MarkHostileFor duration)")] public int HostileSeconds = 120;
            [JsonProperty("FineScrap")] public int FineScrap = 50;
            [JsonProperty("FineDoubleFromTier")] public int FineDoubleFromTier = 4;
            [JsonProperty("CitizenScanFromTier")] public int CitizenScanFromTier = 4;
            [JsonProperty("RespawnMinutes (after the last guard at a gate dies)")] public int RespawnMinutes = 20;
            [JsonProperty("AlertRadiusMeters (an Enemy sighting alerts gates within this range)")] public float AlertRadius = 300f;
            [JsonProperty("AlertMinutes")] public int AlertMinutes = 5;
            [JsonProperty("BroadcastRunAndKill (server-wide chat line when a player runs a checkpoint or kills a guard)")] public bool BroadcastRunAndKill = true;
            [JsonProperty("BroadcastDestroyed (server-wide chat line when a checkpoint falls)")] public bool BroadcastDestroyed = true;
            [JsonProperty("TierCheckMinutes (how often unspawned gates re-check the threat tier)")] public int TierCheckMinutes = 5;
            [JsonProperty("PerimeterEnabled (a guard-free watch around every gated monument: entering it without clearing a gate = evaded)")] public bool PerimeterEnabled = true;
            [JsonProperty("PerimeterGraceSeconds (time to reach a gate after entering the perimeter before the check runs; repeats at this interval while inside)")] public int PerimeterGraceSeconds = 45;
            [JsonProperty("PerimeterClearanceMinutes (a finished gate encounter clears the player for the monument this long)")] public int PerimeterClearanceMinutes = 10;
            [JsonProperty("PerimeterVerdictCooldownSeconds (one evaded verdict per player per monument in this window)")] public int PerimeterVerdictCooldownSeconds = 300;
            [JsonProperty("PerimeterPenaltyFromTier (Citizens/Neutrals lose reputation for evading from this tier; below it they only get a warning)")] public int PerimeterPenaltyFromTier = 4;
        }

        private class RoadblockConfig
        {
            [JsonProperty("RoadblocksByTier (roadblocks on the map at tiers 1..5; they yield to gates under the guard cap)")] public int[] RoadblocksByTier = { 0, 1, 2, 3, 4 };
            [JsonProperty("RelocateMinutes")] public int RelocateMinutes = 60;
            [JsonProperty("RelocateClearMetres (a roadblock waits while a real player is this close or an encounter is running)")] public float RelocateClearMetres = 150f;
            [JsonProperty("RoadMinWidth (metres; main and secondary roads, not trails)")] public float RoadMinWidth = 8f;
            [JsonProperty("RoadMargin (metres from a road end)")] public float RoadMargin = 40f;
            [JsonProperty("StretchMetres (the straight, level stretch checked either side of the point)")] public float StretchMetres = 15f;
            [JsonProperty("MaxBendDegrees (heading change over the stretch around the point)")] public float MaxBendDegrees = 15f;
            [JsonProperty("MaxRise (metres of height change over the stretch)")] public float MaxRise = 2.5f;
            [JsonProperty("MonumentClearance (metres from any monument's bounds)")] public float MonumentClearance = 150f;
            [JsonProperty("CheckpointClearance (metres from any other checkpoint)")] public float CheckpointClearance = 300f;
            [JsonProperty("CupboardClearance (metres from any tool cupboard)")] public float CupboardClearance = 100f;
            [JsonProperty("PlayerClearance (metres from any real player; a roadblock never lands on someone — live 2026-09-14)")] public float PlayerClearance = 100f;
            [JsonProperty("RailClearance (metres from any rail line; trains tripped a roadblock's approach sphere live 2026-09-10)")] public float RailClearance = 30f;
            [JsonProperty("MaxEdgeDrop (metres the ground may fall or rise where the guards stand beside the lane; rejects bridges and embankments)")] public float MaxEdgeDrop = 2f;
            [JsonProperty("SampleRetries (at least 120 is applied)")] public int SampleRetries = 120;
            [JsonProperty("ApproachRadius (metres; vehicles get the STOP prompt here)")] public float ApproachRadius = 30f;
            [JsonProperty("StopSeconds (a vehicle must sit under StopSpeed this long to count as stopped)")] public float StopSeconds = 2f;
            [JsonProperty("StopSpeed (m/s)")] public float StopSpeed = 1f;
            [JsonProperty("SkipDriverlessVehicles (riders of vehicles with no real driver, e.g. the taxi, are waved through)")] public bool SkipDriverlessVehicles = true;
        }

        private class OutpostConfig
        {
            [JsonProperty("Monument (prefab short name of the escalating safe-zone monument)")] public string Monument = "compound";
            [JsonProperty("CurfewFromTier (the safe zone shrinks at night from this threat tier)")] public int CurfewFromTier = 3;
            [JsonProperty("CurfewStartHour (in-game hour, 0-24)")] public float CurfewStartHour = 18f;
            [JsonProperty("CurfewEndHour (in-game hour, 0-24)")] public float CurfewEndHour = 6f;
            [JsonProperty("CurfewRadius (metres; the safe zone's radius during curfew - 45 puts Outpost's wall gates outside it, 75 keeps them inside)")] public float CurfewRadius = 75f;
            [JsonProperty("CurfewSuspectHostile (the monument's gates treat Suspect or worse as hostile during curfew)")] public bool CurfewSuspectHostile = true;
            [JsonProperty("TurretCap (plugin-spawned sentry turrets map-wide, separate from the guard cap)")] public int TurretCap = 6;
            [JsonProperty("CheckIntervalSeconds (the dusk/dawn clock)")] public int CheckIntervalSeconds = 60;
        }

        public class GateTemplate
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Monument (prefab short name, e.g. compound, airfield_1)")] public string Monument = "";
            [JsonProperty("LocalPosition (x y z relative to the monument)")] public string LocalPosition = "0 0 0";
            [JsonProperty("LocalYaw (degrees relative to the monument; the gate faces the approach)")] public float LocalYaw;
            [JsonProperty("TriggerRadius (0 = default)")] public float TriggerRadius;
            [JsonProperty("Guards (0 = default)")] public int Guards;
            [JsonProperty("Heavy (heavy scientist variant)")] public bool Heavy;
            [JsonProperty("MinTier")] public int MinTier = 1;
            [JsonProperty("Searchlight (spawn one beside the right-hand guard)")] public bool Searchlight;
            [JsonProperty("Perimeter (watch the whole monument for players who skipped this gate)")] public bool Perimeter = true;
            [JsonProperty("PerimeterRadius (metres from the monument centre; 0 = from the monument bounds)")] public float PerimeterRadius;
            [JsonProperty("Props (barricades/lights placed with /papers gate prop; offsets relative to the gate)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PropTemplate> Props = new List<PropTemplate>();
        }

        public class PropTemplate
        {
            [JsonProperty("Kind (concrete | sandbags | metal | stone | cover.wood | light | turret | siren | flood)")] public string Kind = "concrete";
            [JsonProperty("Offset (x right, y up, z toward the approach)")] public string Offset = "0 0 0";
            [JsonProperty("Yaw (degrees relative to the gate)")] public float Yaw;
            [JsonProperty("MinTier (the prop appears from this threat tier; 1 = always)")] public int MinTier = 1;
        }

        public class VaultTemplate
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Monument (prefab short name, e.g. compound)")] public string Monument = "";
            [JsonProperty("DoorLocalPosition (x y z relative to the monument; the door's centre at floor level)")] public string DoorLocalPosition = "0 0 0";
            [JsonProperty("DoorLocalYaw (degrees relative to the monument; the door faces this way)")] public float DoorLocalYaw;
            [JsonProperty("CrateLocalPositions (one 'x y z' per crate, inside the door)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> CrateLocalPositions = new List<string>();
            [JsonProperty("GuardLocalPositions (one 'x y z' per post, outside the door)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> GuardLocalPositions = new List<string>();
            [JsonProperty("MinTier (0 = the Vaults section's VaultFromTier)")] public int MinTier;
        }

        private class VaultsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("VaultFromTier (doors, crates and guards exist from this threat tier)")] public int VaultFromTier = 3;
            [JsonProperty("VaultHeavyFromTier (from this tier every vault guard is a heavy scientist)")] public int VaultHeavyFromTier = 4;
            [JsonProperty("VaultTurretsFromTier (from this tier the vault has its own sentries flanking the door, under the Outpost TurretCap)")] public int VaultTurretsFromTier = 5;
            [JsonProperty("VaultTurrets (sentries per vault from VaultTurretsFromTier)")] public int VaultTurrets = 2;
            [JsonProperty("Vaults (placed in-game with /papers vault add; positions are monument-local and transfer between maps)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<VaultTemplate> Vaults = new List<VaultTemplate>();
            [JsonProperty("GuardsByTier (guards per vault, tiers 1..5)")] public int[] GuardsByTier = { 0, 0, 2, 2, 3 };
            [JsonProperty("OpenBands (a legit Cobalt ID in the holder's own name opens the door for these bands only)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> OpenBands = new List<string>(); // 1.4.1: empty — a vault is looted, never opened with papers (decision 0009 §2′)
            // Owner's call 2026-09-20: the vault's loot by tier — totals per vault, split evenly across
            // its crate spots, replacing the crate's own loot. An empty tier keeps the vanilla loot.
            [JsonProperty("LootByTier (what the vault holds at tiers 1..5: item short name -> amount, totals per vault split across its crates; empty = the crate's vanilla loot)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Dictionary<string, int>> LootByTier = DefaultVaultLoot();
            [JsonProperty("BreachPenalty (reputation for destroying a vault door)")] public int BreachPenalty = -15;
            [JsonProperty("NudgeBreach (threat bonus per breach)")] public double NudgeBreach = 2;
            [JsonProperty("AlertMinutes (the vault and its monument's gates stay on alert after a breach)")] public int AlertMinutes = 10;
            [JsonProperty("DoorRebuildMinutes (Cobalt replaces a destroyed door after this)")] public int DoorRebuildMinutes = 20;
            [JsonProperty("RestockMinutes (crates come back this long after the last one is emptied or destroyed)")] public int RestockMinutes = 60;
            [JsonProperty("BroadcastBreach (server-wide line when a vault is breached)")] public bool BroadcastBreach = true;
            [JsonProperty("Room (true = the vault is its own 3x3 m armored building at the pose, immortal walls, the door the only way in; false = the door alone, in an existing doorway)")] public bool Room = true;
            [JsonProperty("ReplaceMonumentDoor (doorway mode only: a monument door entity in the frame is removed while the vault stands and put back when it goes)")] public bool ReplaceMonumentDoor = true;
            [JsonProperty("ReplaceRadius (metres from the vault pose within which a monument door counts as 'in the frame')")] public float ReplaceRadius = 2.5f;

            public static List<Dictionary<string, int>> DefaultVaultLoot() => new List<Dictionary<string, int>>
            {
                new Dictionary<string, int>(),
                new Dictionary<string, int>(),
                new Dictionary<string, int> { { "scrap", 2000 }, { "metal.refined", 250 }, { "ammo.rocket.hv", 25 }, { "ammo.rocket.basic", 25 }, { "explosive.timed", 25 } },
                new Dictionary<string, int> { { "scrap", 3000 }, { "metal.refined", 500 }, { "ammo.rocket.hv", 50 }, { "ammo.rocket.basic", 50 }, { "explosive.timed", 50 } },
                new Dictionary<string, int> { { "scrap", 4000 }, { "metal.refined", 1000 }, { "ammo.rocket.hv", 100 }, { "ammo.rocket.basic", 100 }, { "explosive.timed", 100 } },
            };
        }

        private class SabotageConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("SabotageFromTier (the switch stands and the fence sells the tool from this threat tier; the Green Zone is tier 5)")] public int SabotageFromTier = 3;
            [JsonProperty("SwitchMonument (prefab short name of the substation the switch stands at; set by /papers sabotage switch)")] public string SwitchMonument = "";
            [JsonProperty("SwitchLocalPosition (x y z relative to that monument)")] public string SwitchLocalPosition = "0 0 0";
            [JsonProperty("SwitchLocalYaw")] public float SwitchLocalYaw;
            [JsonProperty("SwitchWorldPosition (where it was captured on this map; picks the right one of a repeated prefab, ignored on another map)")] public string SwitchWorldPosition = "0 0 0";
            [JsonProperty("ToolItem (item short name the fence sells as the bypass tool)")] public string ToolItem = "wiretool";
            [JsonProperty("ToolName (its name; the [COBALT] tag is added, so it is contraband at every gate)")] public string ToolName = "Cobalt Bypass Tool";
            [JsonProperty("ToolScrap (price at the fence; 0 = not sold)")] public int ToolScrap = 150;
            [JsonProperty("WindowMinutes (the vault window: safe zone down, vanilla sentries dark, floods off, the saboteur hostile)")] public int WindowMinutes = 10;
            [JsonProperty("DropsSafeZone (the window removes the Outpost safe zone)")] public bool DropsSafeZone = true;
            [JsonProperty("OfflineVanillaTurrets (the window switches the compound's own sentries offline; the plugin's stay up)")] public bool OfflineVanillaTurrets = true;
            [JsonProperty("ResponseGuards (guards sent to the substation after a cut)")] public int ResponseGuards = 2;
            [JsonProperty("ResponseMinutes (how long they stay)")] public int ResponseMinutes = 5;
            [JsonProperty("ResponseDelaySeconds (the saboteur is warned at the flip; the squad appears this many seconds later)")] public int ResponseDelaySeconds = 20;
            [JsonProperty("ResponseDistance (metres from the switch, on the Outpost side, where the squad appears and runs in from)")] public float ResponseDistance = 60f;
            [JsonProperty("PauseVanillaSpawners (the window pauses the compound's own NPC spawn groups: no peacekeeper respawns until it ends)")] public bool PauseVanillaSpawners = true;
            [JsonProperty("CutPenalty (reputation for cutting the power)")] public int CutPenalty = -5;
            [JsonProperty("CooldownMinutes (one cut per this)")] public int CooldownMinutes = 60;
            [JsonProperty("NudgeCut (threat bonus when the grid is sabotaged)")] public double NudgeCut = 2;
            [JsonProperty("Broadcast (server-wide lines when the window opens and closes)")] public bool Broadcast = true;
            [JsonProperty("DarkenMonumentLights (the compound's own light entities go out with the plugin's floods during a cut)")] public bool DarkenMonumentLights = true;
        }

        // Milestone 9 (decision 0010, decided 2026-09-20): foot patrols on sampled road routes.
        private class PatrolsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("PatrolsByTier (patrols walking the roads at tiers 1..5)")] public int[] PatrolsByTier = { 0, 1, 1, 2, 3 };
            [JsonProperty("PatrolGuardCap (patrol guards map-wide, separate from the checkpoint guard cap)")] public int PatrolGuardCap = 6;
            [JsonProperty("RouteLength (metres of road a patrol walks back and forth)")] public float RouteLength = 300f;
            [JsonProperty("RelocateMinutes (a route is re-drawn after this when no player is within ClearRadius)")] public int RelocateMinutes = 60;
            [JsonProperty("ClearRadius (metres; a route is not re-drawn with a player this close)")] public float ClearRadius = 100f;
            [JsonProperty("MonumentBias (metres; a road point this close to a gated monument with fewer gates than roads is preferred)")] public float MonumentBias = 150f;
            [JsonProperty("StopFromBand (the best band a patrol still stops: Neutral = Citizens walk on, Citizen = everyone is stopped)")] public string StopFromBand = "Neutral";
            [JsonProperty("PatrolMinutes (a patrol walks this long, then stands down)")] public int PatrolMinutes = 45;
            [JsonProperty("PatrolGapMinutes (it is back on a new route after this)")] public int PatrolGapMinutes = 15;
            [JsonProperty("WalkStallSeconds (no progress toward the next road point for this long skips it; three skips re-draw the route)")] public int WalkStallSeconds = 10;
        }

        // Milestone 9 (decision 0010): the Cobalt ambush from tier 3.
        private class AmbushConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("AmbushFromTier (sightings send squads from this threat tier)")] public int AmbushFromTier = 3;
            [JsonProperty("Bands (a verdict in these bands is a sighting; the owner: Enemies of the State only)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Bands = new List<string> { "Enemy" };
            [JsonProperty("CooldownMinutes (one ambush per player per this)")] public int CooldownMinutes = 30;
            [JsonProperty("DispatchDelaySeconds (the radio line comes at the sighting; the squad this many seconds later)")] public int DispatchDelaySeconds = 45;
            [JsonProperty("SquadByTier (guards per squad at tiers 1..5; not under the guard cap)")] public int[] SquadByTier = { 0, 0, 2, 3, 4 };
            [JsonProperty("SquadHeaviesByTier (how many of them are heavy scientists, tiers 1..5)")] public int[] SquadHeaviesByTier = { 0, 0, 0, 2, 4 };
            [JsonProperty("SpawnDistance (metres from the player's current position, out of their sight)")] public float SpawnDistance = 80f;
            [JsonProperty("HuntMinutes (the squad hunts this long, then stands down)")] public int HuntMinutes = 5;
            [JsonProperty("BaseStandoff (metres the squad keeps from any tool cupboard whose range holds the target)")] public float BaseStandoff = 40f;
            [JsonProperty("EnterBases (true = no cupboard standoff)")] public bool EnterBases = false;
            [JsonProperty("MaxSquads (ambush squads map-wide at once)")] public int MaxSquads = 2;
            [JsonProperty("RadioLine (the target is told a unit is coming)")] public bool RadioLine = true;
            // Decision 0010 §D (owner, 2026-09-27): a downed target is arrested, not finished.
            [JsonProperty("Arrest (a target the squad downs is cuffed, hooded, stripped and left at the spawn beach instead of killed)")] public bool Arrest = true;
            [JsonProperty("ArrestSeconds (a hunter must be at arm's reach of the downed target this long)")] public int ArrestSeconds = 5;
            [JsonProperty("ArrestHealth (health the arrested player wakes up with on the beach)")] public float ArrestHealth = 30f;
            [JsonProperty("ArrestStrips (everything on the arrested player is taken)")] public bool ArrestStrips = true;
            [JsonProperty("BroadcastArrest (server-wide line when someone is taken into custody)")] public bool BroadcastArrest = true;
        }

        private class VoiceConfig
        {
            [JsonProperty("Halt", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Halt = new List<string> { "Papers, please.", "Halt. Cobalt checkpoint. Papers.", "Stop right there. Identification." };
            [JsonProperty("Pass", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Pass = new List<string> { "Move along, citizen.", "You're clear. Go.", "Cobalt thanks you for your cooperation." };
            [JsonProperty("Contraband", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Contraband = new List<string> { "What's this, then? Hand it over.", "Contraband. Surrender it, now.", "That's Cobalt property. Give it up." };
            [JsonProperty("Fine", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Fine = new List<string> { "You're on our list. That'll cost you.", "Suspects pay to pass. Scrap, now.", "Cobalt remembers you. Pay the fine." };
            [JsonProperty("Hostile", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Hostile = new List<string> { "Hostile! Open fire!", "That's them! Take them down!", "Wanted target at the gate, engage!" };
            [JsonProperty("Ran", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Ran = new List<string> { "Runner! Stop them!", "They're running the checkpoint!", "Don't let them through!" };
            [JsonProperty("Evaded", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Evaded = new List<string> { "Someone's inside the fence. Find them.", "Perimeter breach. All units, eyes open.", "They skipped the gate. Cobalt does not forget." };
            [JsonProperty("Tier (one broadcast per tier 1..5 when the threat clock reaches it)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Tier = new List<string>
            {
                "Cobalt advisory: threat level 1. Routine checkpoints are in effect. Carry your papers.",
                "Cobalt advisory: threat level 2. Checkpoints will search travellers for contraband.",
                "Cobalt advisory: threat level 3. Known suspects will be fined at every gate. Cooperate.",
                "Cobalt advisory: threat level 4. All travellers are subject to search. Fines are doubled.",
                "Cobalt advisory: threat level 5. Curfew conditions. Resistance will be met with force."
            };
            [JsonProperty("TierDown (broadcast when the threat level falls)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> TierDown = new List<string> { "Cobalt advisory: threat level lowered to {0}. Checkpoints stand easy." };
            [JsonProperty("Destroyed", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Destroyed = new List<string> { "Cobalt checkpoint {0} has fallen. Reinforcements are on their way.", "Checkpoint {0} is down. Cobalt will be back." };
            [JsonProperty("CurfewStart (broadcast when the Outpost curfew begins)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> CurfewStart = new List<string> { "Cobalt advisory: curfew is in effect at Outpost. The safe zone is reduced to the compound until dawn. Suspects will be engaged at the gates." };
            [JsonProperty("CurfewEnd (broadcast when it ends)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> CurfewEnd = new List<string> { "Cobalt advisory: curfew lifted. The Outpost safe zone is restored." };
            [JsonProperty("Papers (a legit ID is issued)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Papers = new List<string> { "Processed. Carry it at all times, citizen.", "Your document. Do not lose it; replacements cost the same.", "Stamped. Cobalt thanks you for your cooperation." };
            [JsonProperty("Forgery (a forged ID is spotted)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Forgery = new List<string> { "This serial does not exist. Hands where I can see them.", "Forged papers. You just made my day.", "Nice try. Cobalt does not issue documents in crayon." };
            [JsonProperty("Fence (the Bandit Camp fence, on a deal)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Fence = new List<string> { "Clean as the day it was made. Never saw you.", "Cobalt property? Not any more.", "Pleasure. Tell nobody." };
            [JsonProperty("PatrolHalt (a patrol pulling someone over)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> PatrolHalt = new List<string> { "Hold it. Cobalt patrol. Papers.", "Stop where you are. Routine stop.", "You. Off the road. Papers, please." };
            [JsonProperty("PatrolLost (broadcast when a patrol is wiped out)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> PatrolLost = new List<string> { "Cobalt patrol {0} has gone silent. Units are being reassigned.", "Contact lost with Cobalt patrol {0}. Travellers on that road: stay off it." };
            [JsonProperty("AmbushRadio (the target, when a squad is sent)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> AmbushRadio = new List<string> { "Cobalt radio: a unit is coming for you.", "Cobalt radio: your position is known. A unit is on its way.", "Cobalt radio: unit dispatched. Don't run — it's worse when you run." };
            [JsonProperty("AmbushStoodDown (the target, when the squad gives up)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> AmbushStoodDown = new List<string> { "Cobalt radio: unit standing down. This time.", "Cobalt radio: lost contact. They'll be back." };
            [JsonProperty("FenceTool (the fence, on selling the bypass tool)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> FenceTool = new List<string> { "Wire it in, walk away, and don't look back at the substation.", "Ten minutes of Cobalt looking the other way. Don't waste them talking to me.", "If they take that off you at a gate, we never met." };
            [JsonProperty("FenceForge (the fence, on selling forged papers)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> FenceForge = new List<string> { "Ink's still wet. Don't wave it at anyone who reads.", "Good enough from arm's length. Keep it at arm's length.", "You were never here, and neither was that serial." };
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null) throw new Exception("config empty");
            }
            catch (Exception ex)
            {
                PrintWarning($"Config unreadable ({ex.Message}); writing defaults.");
                LoadDefaultConfig();
            }
            if (_config.TierHours == null || _config.TierHours.Length == 0) _config.TierHours = new[] { 0, 48, 120, 240, 400 };
            if (_config.Checkpoints == null) _config.Checkpoints = new CheckpointConfig();
            if (_config.Threat == null) _config.Threat = new ThreatConfig();
            if (_config.Roadblocks == null) _config.Roadblocks = new RoadblockConfig();
            if (_config.Roadblocks.RoadblocksByTier == null) _config.Roadblocks.RoadblocksByTier = new int[0];
            if (_config.Outpost == null) _config.Outpost = new OutpostConfig();
            if (_config.Vaults == null) _config.Vaults = new VaultsConfig();
            if (_config.Vaults.Vaults == null) _config.Vaults.Vaults = new List<VaultTemplate>();
            if (_config.Vaults.GuardsByTier == null) _config.Vaults.GuardsByTier = new int[0];
            if (_config.Vaults.LootByTier == null) _config.Vaults.LootByTier = VaultsConfig.DefaultVaultLoot();
            while (_config.Vaults.LootByTier.Count < 5) _config.Vaults.LootByTier.Add(new Dictionary<string, int>());
            if (_config.Vaults.OpenBands == null) _config.Vaults.OpenBands = new List<string>();
            if (_config.Sabotage == null) _config.Sabotage = new SabotageConfig();
            if (_config.Patrols == null) _config.Patrols = new PatrolsConfig();
            if (_config.Patrols.PatrolsByTier == null) _config.Patrols.PatrolsByTier = new int[0];
            if (_config.Ambush == null) _config.Ambush = new AmbushConfig();
            if (_config.Ambush.Bands == null) _config.Ambush.Bands = new List<string>();
            if (_config.Ambush.SquadByTier == null) _config.Ambush.SquadByTier = new int[0];
            if (_config.Ambush.SquadHeaviesByTier == null) _config.Ambush.SquadHeaviesByTier = new int[0];
            if (_config.Checkpoints.GuardsByTier == null) _config.Checkpoints.GuardsByTier = new int[0];
            if (_config.Checkpoints.RespawnMinutesByTier == null) _config.Checkpoints.RespawnMinutesByTier = new int[0];
            if (_config.Gates == null) _config.Gates = new List<GateTemplate>();
            if (_config.Voice == null) _config.Voice = new VoiceConfig();
            foreach (var g in _config.Gates) if (g.Props == null) g.Props = new List<PropTemplate>();
            // 0.5.7: Config.ReadObject appends the file's entries to every list that has a default
            // initialiser (Json.NET reuses the existing list), and SaveConfig wrote the doubled list
            // back — 30 copies of the contraband list on the dev server after 30 loads (live
            // 2026-09-10). Replace on the attributes stops it; this pass repairs a file an older build wrote.
            Dedupe(_config.Contraband.Items);
            var v = _config.Voice;
            Dedupe(v.Halt); Dedupe(v.Pass); Dedupe(v.Contraband); Dedupe(v.Fine); Dedupe(v.Hostile);
            Dedupe(v.Ran); Dedupe(v.Evaded); Dedupe(v.Tier); Dedupe(v.TierDown); Dedupe(v.Destroyed);
            Dedupe(v.CurfewStart); Dedupe(v.CurfewEnd); Dedupe(v.Papers); Dedupe(v.Forgery); Dedupe(v.Fence); Dedupe(v.FenceForge); Dedupe(v.FenceTool);
            Dedupe(v.PatrolHalt); Dedupe(v.PatrolLost); Dedupe(v.AmbushRadio); Dedupe(v.AmbushStoodDown);
            RebuildContrabandSet();
            SaveConfig();
        }

        // Order-preserving: the first copy of each line stays (Voice.Tier is indexed by tier).
        private static void Dedupe(List<string> list)
        {
            if (list == null || list.Count < 2) return;
            var seen = new HashSet<string>();
            for (var i = 0; i < list.Count; i++)
            {
                if (seen.Add(list[i] ?? "")) continue;
                list.RemoveAt(i--);
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Band.Citizen"] = "CITIZEN",
                ["Band.Neutral"] = "NEUTRAL",
                ["Band.Suspect"] = "SUSPECT",
                ["Band.Wanted"] = "WANTED",
                ["Band.Enemy"] = "ENEMY OF THE STATE",
                ["Desc.Citizen"] = "Cobalt checkpoints wave you through.",
                ["Desc.Neutral"] = "Cobalt checkpoints will search you for contraband.",
                ["Desc.Suspect"] = "Cobalt checkpoints will search you and demand a fine.",
                ["Desc.Wanted"] = "Cobalt shoots on sight and dispatches patrols.",
                ["Desc.Enemy"] = "Cobalt shoots on sight and puts every nearby checkpoint on alert.",
                // The band is coloured, not the score: the client masked "<color=#ffd27f>-20</color>"
                // to "<color=#**********" (live 2026-09-12). Numbers stay outside rich-text tags.
                ["Papers.Self"] = "<color=#8fc1ff>COBALT RECORD</color> — {0}: reputation {1} (<color=#ffd27f>{2}</color>). {3} Standing drifts toward 0 by {4}/hour.",
                ["Papers.Other"] = "<color=#8fc1ff>COBALT RECORD</color> — {0}: reputation {1} ({2}).",
                ["Papers.Exempt"] = "<color=#8fc1ff>COBALT RECORD</color> — you are exempt from Cobalt attention.",
                ["Papers.Cooldown"] = "Checkpoint {0} will not stop you again for {1} s.",
                ["Papers.Tier"] = "<color=#8fc1ff>COBALT THREAT</color> level {0}: {1}",
                ["Tier.1"] = "routine checkpoints; Citizens are waved through.",
                ["Tier.2"] = "checkpoints search Neutrals for contraband.",
                ["Tier.3"] = "Suspects are fined at every gate; more monuments are gated; Outpost is under curfew at night.",
                ["Tier.4"] = "everyone is searched, fines are doubled, gates carry a heavy.",
                ["Tier.5"] = "curfew conditions: worn gear is searched, fallen gates return in minutes, Outpost is a fortified Green Zone.",
                ["Papers.Curfew"] = "<color=#8fc1ff>COBALT CURFEW</color> in effect at Outpost: the safe zone is reduced to the compound walls and Suspects are shot at the gates.",
                ["Threat.Curfew"] = " Curfew in effect at Outpost.",
                ["Curfew.Warn"] = "<color=#8fc1ff>Cobalt curfew.</color> The Outpost safe zone has shrunk to the compound walls. You are outside it.",
                ["Broadcast.Curfew"] = "<color=#8fc1ff>[COBALT]</color> {0}",
                ["Gate.Tier"] = "Gate '{0}' now needs threat tier {1}; {2}.",
                ["Threat.Status"] = "<color=#8fc1ff>COBALT THREAT</color> — tier <color=#ffd27f>{0}</color>, {1:0.0}/100 (wipe age {2:0} h → {3:0.0}, violence {4:+0.0;-0.0}){5}. {6}",
                ["Threat.Next"] = "Tier {0} in about {1:0.0} h.",
                ["Threat.Max"] = "Cobalt is at full strength.",
                ["Threat.Pinned"] = " [pinned by an admin]",
                ["Threat.Set"] = "Threat set to {0:0.0} (violence bonus now {1:+0.0;-0.0}); tier {2}.",
                ["Threat.Pin"] = "Threat tier pinned at {0} until /threat tier off.",
                ["Threat.Unpin"] = "Threat pin released; the clock reads tier {0}.",
                ["Threat.Nudged"] = "Violence bonus {0:+0.0;-0.0} (now {1:+0.0;-0.0}, capped at ±NudgeMax) → {2:0.0}/100, tier {3}.",
                ["Threat.Usage"] = "/threat · /threat set <0-100> · /threat tier <1-5|off> · /threat nudge <±n> (admin)",
                ["Papers.Hostile"] = "<color=#ff7066>Cobalt considers you HOSTILE</color>{0} — guards, peacekeepers and sentries shoot on sight.",
                ["Papers.HostileFor"] = " for another {0} s",
                ["Notify.StoodDown"] = "<color=#8fc1ff>Cobalt has stood down.</color> You are no longer a target.",
                ["Papers.Set"] = "Set {0} to {1} ({2}).",
                ["Papers.Added"] = "Adjusted {0} by {1} to {2} ({3}).",
                ["Papers.Reset"] = "Reset {0} to 0.",
                ["Papers.Top"] = "Cobalt's ledger, {0} record(s):",
                ["Papers.TopLine"] = "  {0}: {1} ({2})",
                ["Papers.NoPlayer"] = "No player matches '{0}'.",
                ["Papers.NoPermission"] = "Cobalt does not take orders from you.",
                ["Papers.Usage"] = "/papers · /papers <player> · /papers set|add|reset <player> <n> [reason] · /papers top [n] · /papers gate … · /papers fence … · /papers vault … · /papers sabotage … · /papers reload (admin)",
                ["Papers.Reloaded"] = "Papers Please config reloaded; {0} checkpoint(s) respawned.",
                ["Notify.Change"] = "<color=#8fc1ff>Cobalt noted that.</color> {0}{1} for {2} (now {3}).",
                ["Notify.Band"] = "<color=#8fc1ff>Cobalt has reclassified you:</color> you are now <color=#ffd27f>{0}</color>.",
                ["Reason.ScientistKill"] = "killing a Cobalt scientist",
                ["Reason.GuardKill"] = "killing a Cobalt checkpoint guard",
                ["Reason.RanCheckpoint"] = "running a Cobalt checkpoint",
                ["Reason.Surrender"] = "surrendering contraband",
                ["Reason.FinePaid"] = "paying a Cobalt fine",
                ["Reason.Evaded"] = "evading a Cobalt checkpoint",
                ["Reason.CobaltCrate"] = "looting Cobalt property",
                ["Reason.StructureDestroyed"] = "raiding",
                ["Reason.PartialRaid"] = "explosive damage to a base",
                ["Reason.Admin"] = "an administrative decision",
                ["Reason.PublicWorksRepair"] = "repairing the island's utilities",
                ["Reason.PublicWorksPurchase"] = "funding Public Works",
                // HelpMenu page (GetHelpInfo); HowTo and Notes are one line per \n
                ["Help.Title"] = "Cobalt Papers Please",
                ["Help.Description"] = "Cobalt runs checkpoints at monuments and on the roads, keeps a record on everyone, and tightens its grip as the wipe goes on. Your standing decides whether a guard waves you through, fines you, or opens fire.",
                ["Help.HowTo"] = "Type /papers to see your standing, your band and anything Cobalt still holds against you.\nBands run on a score from -100 to 100: Citizen from {0}, Suspect at {1} or below, Wanted at {2}, Enemy of the State at {3}. Everyone else is Neutral.\nWalk up to a checkpoint and stop when told. The guard reads your band and searches your inventory for contraband.\nAnswer the panel: surrender the contraband, pay the fine, or refuse. Running a checkpoint or letting the timer run out costs standing and can turn the guards on you.\nKilling Cobalt scientists, looting Cobalt crates and raiding other players' bases cost standing; surrendering contraband and paying fines earn it back. The score drifts toward zero over time.\nA Cobalt ID (/papers request at a checkpoint) softens a scan by one band. The fence at Bandit Camp sells forgeries, which a guard may spot.\nType /threat to see how tense the island is. The higher the tier, the more checkpoints, the stricter the guards, and the harder Outpost clamps down.",
                ["Help.Notes"] = "Wanted and Enemy players are shot on sight at checkpoints. Enemies of the State are hunted by Cobalt squads, and a downed Enemy is arrested: stripped, cuffed and dropped on the beach.\nOutpost runs a curfew at night once the threat is high enough.",
                ["Help.NotePublicWorks"] = "Civic work counts: a Public Works repair contract earns +{0} (minor) or +{1} (major) standing, and funding a service earns a little once a day.",
                ["Help.NoteTaxi"] = "Island Taxi checks your record: Citizens ride cheaper, the Wanted pay double, and Enemies walk.",
                ["Help.Cmd.Papers"] = "Your standing, band, threat tier and cooldowns",
                ["Help.Cmd.Request"] = "Get a Cobalt ID at a checkpoint (free for Citizens, {0} scrap for Neutrals)",
                ["Help.Cmd.Forge"] = "Forge an ID at a level {0} workbench",
                ["Help.Cmd.Threat"] = "The island's threat tier and what it means",
                ["Help.Cmd.AdminRep"] = "Admin: read or change a player's standing, list the worst records",
                ["Help.Cmd.AdminThreat"] = "Admin: pin the threat tier or release it",
                ["Help.Cmd.AdminPlace"] = "Admin: place and manage gates, the fence, vaults and the sabotage switch",
                ["Help.Cmd.AdminConsole"] = "Admin console: checkpoint and ambush status and levers",
                // Gate placement / admin
                ["Gate.Usage"] = "/papers gate add <name> [guards] [monument] · list · remove <name> · tp <name> · tier <name> <1-5> · radius <name> <2-60> · prop <name> <concrete|sandbags|metal|stone|cover.wood|light|turret|siren|flood> [minTier] · reload",
                ["Gate.Radius"] = "Gate '{0}' trigger radius set to {1} m; gate respawned.",
                ["Gate.NoMonument"] = "No monument found near you.",
                ["Gate.Exists"] = "A gate named '{0}' already exists.",
                ["Gate.Added"] = "Gate '{0}' saved at {1}, local {2} yaw {3:0} ({4:0} m from its origin). InSafeZone={5}. {6} instance(s) of that monument on this map. Nearby: {7}. Wrong monument? /papers gate remove {0}, then /papers gate add {0} <guards> <monument>.",
                ["Gate.Removed"] = "Gate '{0}' removed.",
                ["Gate.NotFound"] = "No gate named '{0}'.",
                ["Gate.Teleported"] = "Teleported to gate '{0}'.",
                ["Gate.PropAdded"] = "Prop '{0}' added to gate '{1}' at offset {2} (yaw {3:0}); gate respawned.",
                ["Gate.ListHeader"] = "{0} gate template(s), {1} checkpoint instance(s), {2}/{3} guards live, tier {4}:",
                ["Gate.ListLine"] = "  {0} @ {1} local {2} yaw {3:0} guards={4} r={5} minTier={6} props={7} | {8}",
                ["Gate.ListNone"] = "No gates placed yet. Stand at the spot facing the approach and run /papers gate add <name>.",
                ["Gate.Respawned"] = "Checkpoint '{0}' respawned ({1}).",
                ["Gate.Despawned"] = "Checkpoint '{0}' despawned.",
                // Papers CUI + voice
                ["Chat.Voice"] = "<color=#8fc1ff>Cobalt Guard:</color> {0}",
                ["Ui.Title"] = "PAPERS, PLEASE",
                ["Ui.Gate"] = "Cobalt checkpoint — {0}",
                ["Ui.Subject"] = "{0} · {1}",
                ["Ui.PassNoScan"] = "Citizens are not searched. Move along.",
                ["Ui.Clean"] = "Search complete. Nothing found. Move along.",
                ["Ui.Confiscated"] = "Contraband confiscated: {0}.\nConsider this a warning, citizen.",
                ["Ui.Contraband"] = "Search found: {0}.\nSurrender it to Cobalt, or refuse and face the guards.",
                ["Ui.FineBody"] = "You are a known {0}. Fine to pass: {1} scrap (you carry {2}).{3}",
                ["Ui.FineContraband"] = "\nSearch found: {0} — it will be confiscated.",
                ["Ui.Surrendered"] = "Surrendered: {0}.\nCobalt notes your cooperation.",
                ["Ui.Fined"] = "Fine of {0} scrap paid.{1}\nMove along.",
                ["Ui.FinedContraband"] = " Confiscated: {0}.",
                ["Ui.Hostile"] = "Cobalt has declared you HOSTILE.\nGuards, peacekeepers and sentries shoot on sight for {0} s.",
                ["Ui.Ran"] = "You ran a Cobalt checkpoint.\nGuards, peacekeepers and sentries shoot on sight for {0} s.",
                ["Ui.NoScrap"] = "You cannot pay {0} scrap. Decide before the guards do.",
                ["Ui.Timer"] = "You have {0} seconds to decide. Walking away counts as running.",
                ["Ui.Nothing"] = "nothing",
                ["Ui.More"] = " and {0} more",
                ["Ui.BtnSurrender"] = "SURRENDER",
                ["Ui.BtnPay"] = "PAY {0} SCRAP",
                ["Ui.BtnRefuse"] = "REFUSE",
                ["Ui.Closing"] = "this notice closes on its own",
                ["Ui.Stop"] = "COBALT CHECKPOINT AHEAD — STOP",
                // Milestone 7: papers and the fence (decision 0008)
                ["Papers.Disabled"] = "Cobalt papers are not in use on this server.",
                ["Papers.RequestNotCitizen"] = "Cobalt issues identity documents to Citizens and Neutrals only. Your standing: {0}.",
                ["Papers.IssuedFree"] = "<color=#8fc1ff>Cobalt ID issued</color> at {0}, no charge. Carry it: a valid ID is worth one band at every scan while your standing holds.",
                ["Ui.IdOffer"] = "No papers on you. Cobalt can issue an ID now{0}.",
                ["Ui.IdOfferFee"] = " for {0} scrap",
                ["Ui.IdOfferFree"] = ", free of charge",
                ["Ui.BtnIdFree"] = "TAKE COBALT ID — FREE",
                ["Ui.BtnIdBuy"] = "BUY COBALT ID — {0} SCRAP",
                ["Ui.BtnClose"] = "CLOSE",
                ["Papers.RequestNoGate"] = "Apply at a Cobalt checkpoint: stand at a gate or roadblock and ask again.",
                ["Papers.RequestNoScrap"] = "The processing fee is {0} scrap. You do not have it.",
                ["Papers.RequestHeld"] = "You already hold a valid Cobalt ID (serial C-{0}). Cobalt replaces documents that are lost, not ones in your pocket.",
                ["Papers.RequestFailed"] = "Cobalt could not issue a document right now. Nothing was charged.",
                ["Papers.Issued"] = "<color=#8fc1ff>Cobalt ID issued</color> at {1} for {0} scrap. Carry it: a valid ID is worth one band at every scan while your standing holds.",
                ["Papers.ForgeNoBench"] = "Forging papers needs a level {0} workbench within reach.",
                ["Papers.ForgeNoItems"] = "Forging papers needs: {0}.",
                ["Papers.Forged"] = "You forged a Cobalt ID ({0}). Every scan is a gamble, and the odds get worse as Cobalt's threat level rises.",
                ["Papers.IdHeld.Valid"] = "\n<color=#8fc1ff>Papers:</color> you hold a valid Cobalt ID (serial C-{0}).",
                ["Papers.IdHeld.Forged"] = "\n<color=#8fc1ff>Papers:</color> you hold forged papers ({0}); a scan spots them {1}% of the time at this level.",
                ["Papers.IdHeld.NotOnYou"] = "\n<color=#8fc1ff>Papers:</color> your Cobalt ID (serial C-{0}) is not on you — a scan will not see it.",
                ["Papers.IdHeld.None"] = "\n<color=#8fc1ff>Papers:</color> none. Citizens (free) and Neutrals ({0} scrap) can apply at any checkpoint: /papers request, or the button when a gate passes you.",
                ["Papers.Id.Valid"] = "ID checked: valid.",
                ["Papers.Id.Upgraded"] = "ID checked: valid — treated as {1} rather than {0}.",
                ["Papers.Id.Seized"] = "{0} document(s) seized ({1}).",
                ["Papers.Id.ForgerySeized"] = "FORGED papers seized. Cobalt is watching you.",
                ["Papers.Id.Forgery"] = "FORGERY.",
                ["Papers.Id.LastUse"] = "Papers retained: your standing no longer qualifies.",
                ["Ui.Forgery"] = "FORGED PAPERS.\nYou are now WANTED. Guards, peacekeepers and sentries shoot on sight for {0} s.",
                ["Broadcast.Forgery"] = "{0} presented forged papers at {1} and is now Wanted.",
                ["Reason.Forgery"] = "presenting forged papers",
                ["Reason.NotYours"] = "carrying someone else's papers",
                ["Notify.IdRevoked"] = "<color=#8fc1ff>Cobalt has revoked your identity document.</color> Your standing no longer qualifies.",
                ["Id.Usage"] = "papers.id list [player] | issue <player> | forge <player> | revoke <player> | wipe",
                ["Id.Issued"] = "Gave {0} a {1} Cobalt ID (serial C-{2}).",
                ["Id.Revoked"] = "Revoked {0} legit ID(s) of {1}.",
                ["Id.ListLine"] = "C-{0} {1} owner={2} {3}",
                ["Fence.Title"] = "THE FENCE",
                ["Fence.Header"] = "{0} — {1}",
                ["Fence.NoTagged"] = "Nothing on you is marked Cobalt property. Come back when you have something worth cleaning.",
                ["Fence.NoScrap"] = "{0} marked stack(s) on you. Cleaning is {1} scrap and you're carrying {2}. Come back with {3} more.",
                ["Fence.Offer"] = "{0} marked stack(s): {3}.\nCleaning is {1} scrap; you're carrying {2}, and marked scrap pays the same. Nobody at a Cobalt checkpoint will know.",
                ["Fence.BtnLaunder"] = "CLEAN — {0} SCRAP",
                ["Fence.BtnClose"] = "LEAVE",
                ["Fence.TooHot"] = "Not you. An Enemy of the State walks in here and Cobalt burns the whole camp. Get out.",
                ["Fence.ForgeLine"] = "Papers? Cobalt ID, good enough to wave at a gate: {0} scrap. Spotted {1}% of the time at this threat level — your risk.",
                ["Fence.BtnForge"] = "FORGED PAPERS — {0} SCRAP",
                ["Fence.ForgeDone"] = "Forged papers, {0} scrap. Spotted {1}% of the time at this threat level. Don't come crying to me.",
                ["Fence.ToolLine"] = "The vault? Bypass tool, {0} scrap. Wire it into the switch at the substation in {4} and Outpost's safe zone and sentries drop for {1} minutes. What that leaves between you and the door: {2} guards, {3} Cobalt turrets, and three charges' worth of steel. It's Cobalt property — get searched with it and it's gone.",
                ["Fence.BtnTool"] = "BYPASS TOOL — {0} SCRAP",
                ["Fence.ToolDone"] = "Bypass tool, {0} scrap. The substation in {2}. You've got {1} minutes once you flip it. I was never here.",
                ["Papers.ForgeAtFence"] = "Cobalt paper is not something you make at a bench. Ask around Bandit Camp.",
                ["Fence.Done"] = "{0} stack(s) cleaned for {1} scrap. Pleasure doing business.",
                ["Fence.Usage"] = "/papers fence add [name] [monument] | remove <name> | tp <name> | list",
                ["Fence.Added"] = "Fence '{0}' placed at {1} (local {2}, yaw {3:0}), {4:0} m from the monument origin.",
                ["Fence.Removed"] = "Fence '{0}' removed.",
                ["Fence.NotFound"] = "No fence named '{0}'.",
                ["Fence.Exists"] = "A fence named '{0}' already exists.",
                ["Fence.ListNone"] = "No fences placed. Stand at Bandit Camp and run /papers fence add.",
                ["Fence.ListLine"] = "{0} @ {1} ({2}) — {3}",
                ["Fence.Teleported"] = "Teleported to fence '{0}'.",
                ["Vault.Refused"] = "<color=#8fc1ff>Cobalt property.</color> No entry.",
                ["Vault.Usage"] = "/papers vault add <name> [monument] (aim at flat ground where the vault door should stand: the armored room is built behind it, away from you) · crate <name> · guard <name> (where you stand) · list · remove <name> · tp <name> · open|close|breach|restock <name> (admin)",
                ["Vault.Added"] = "Vault '{0}' built at {1} (door local {2}, yaw {3:0}), {4:0} m from the monument origin. Crate and guard spots: /papers vault crate|guard {0}. {5}",
                ["Vault.CrateAdded"] = "Crate spot {2} added to vault '{0}' at local {1}.",
                ["Vault.GuardAdded"] = "Guard post {2} added to vault '{0}' at local {1}.",
                ["Vault.Removed"] = "Vault '{0}' removed.",
                ["Vault.NotFound"] = "No vault named '{0}'.",
                ["Vault.Exists"] = "A gate or vault named '{0}' already exists.",
                ["Vault.NoMonument"] = "No monument near enough to own that spot.",
                ["Vault.ListNone"] = "No vaults placed. Stand in a monument doorway and run /papers vault add <name>.",
                ["Vault.ListLine"] = "  {0} @ {1} ({2}) crates={3} posts={4} minTier={5} | {6}",
                ["Vault.Teleported"] = "Teleported to vault '{0}'.",
                ["Vault.Opened"] = "<color=#8fc1ff>Cobalt vault.</color> Papers accepted, citizen. Everything inside is still Cobalt property.",
                ["Broadcast.VaultBreach"] = "Cobalt advisory: the vault at {0} has been breached by {1}. Cobalt forces are responding.",
                ["Broadcast.OutpostOpen"] = "Cobalt advisory: the Outpost grid has been sabotaged by {0}. The safe zone and the sentries are down for {1} minutes. Cobalt forces are engaging.",
                ["Broadcast.OutpostClosed"] = "Cobalt advisory: the Outpost grid is restored. The safe zone and the sentries are back.",
                ["Reason.VaultBreach"] = "breaching a Cobalt vault",
                ["Reason.PowerCut"] = "sabotaging Cobalt's grid",
                ["Notify.WindowStart"] = "<color=#8fc1ff>Outpost is open.</color> The safe zone and the sentries are down for {0} minutes, and every Cobalt gun knows your name. Go.",
                ["Notify.NeedTool"] = "<color=#8fc1ff>The panel is sealed.</color> Cobalt's grid takes a bypass tool. Ask around Bandit Camp.",
                ["Notify.ResponseInbound"] = "<color=#8fc1ff>Cobalt radio:</color> substation breach — a response team is on its way. About {0} seconds.",
                ["Ui.Window"] = "VAULT WINDOW  {0}",
                ["Patrol.Halt"] = "<color=#8fc1ff>Cobalt patrol.</color> Stop and show your papers.",
                ["Patrol.WavedOn"] = "<color=#8fc1ff>Cobalt patrol.</color> Move along, citizen.",
                ["Patrol.Stop"] = "<color=#8fc1ff>Cobalt patrol ahead.</color> Stop your vehicle and wait for the officers.",
                ["Ui.PatrolStop"] = "COBALT PATROL AHEAD — STOP",
                ["Ambush.Arrested"] = "<color=#8fc1ff>Cobalt:</color> you are under arrest. Everything on you is Cobalt property now. You will wake up on the beach.",
                ["Broadcast.Arrested"] = "Cobalt advisory: {0} has been taken into custody.",
                ["Ambush.Radio"] = "<color=#8fc1ff>Cobalt radio:</color> a unit is coming for you.",
                ["Ambush.StoodDown"] = "<color=#8fc1ff>Cobalt radio:</color> unit standing down.",
                ["Ambush.Usage"] = "papers.ambush status | dispatch <player> | stand (tests)",
                ["Sabotage.Usage"] = "/papers sabotage switch [monument] (aim at the substation's panel: the switch goes at your aim point facing you) · status · remove switch · tp switch · cut | restore (tests)",
                ["Sabotage.SwitchSet"] = "Sabotage switch set at {0} (monument '{1}', local {2}); it stands from threat level {3}.",
                ["Sabotage.Removed"] = "Sabotage {0} removed.",
                ["Sabotage.NotSet"] = "No sabotage {0} placed yet.",
                ["Sabotage.Teleported"] = "Teleported to the sabotage {0}.",
                ["Chat.Stop"] = "<color=#8fc1ff>Cobalt checkpoint ahead.</color> Stop your vehicle at the barricades and wait for inspection.",
                ["Chat.StopNow"] = "<color=#8fc1ff>Cobalt Guard:</color> Stop the vehicle. Driving on counts as running the checkpoint.",
                ["Perimeter.Warn"] = "<color=#8fc1ff>Cobalt noticed</color> you entered {0} without clearing its checkpoint. Use the road next time.",
                ["Perimeter.Penalty"] = "<color=#8fc1ff>Cobalt noticed</color> you entered {0} without clearing its checkpoint.",
                ["Perimeter.Hostile"] = "<color=#8fc1ff>Cobalt has flagged you</color> for evading its checkpoint at {0}. The guards are looking for you.",
                // Broadcasts
                ["Broadcast.Evaded"] = "<color=#8fc1ff>[COBALT]</color> {0} was caught inside {1} without clearing the checkpoint.",
                ["Broadcast.Destroyed"] = "<color=#8fc1ff>[COBALT]</color> {0}",
                ["Broadcast.Tier"] = "<color=#8fc1ff>[COBALT]</color> {0}",
                ["Broadcast.GuardKilled"] = "<color=#8fc1ff>[COBALT]</color> {0} killed a Cobalt guard at checkpoint {1}.",
                ["Broadcast.Ran"] = "<color=#8fc1ff>[COBALT]</color> {0} ran the Cobalt checkpoint at {1}.",
            }, this);
        }

        private string L(string key, string userId = null, params object[] args)
        {
            var msg = lang.GetMessage(key, this, userId);
            return args.Length == 0 ? msg : string.Format(msg, args);
        }

        private void Broadcast(string key, params object[] args)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && p.IsConnected) p.ChatMessage(L(key, p.UserIDString, args));
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            _instance = this;
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermExempt, this);
            Unsubscribe(nameof(OnEntityTakeDamage)); // armed only after explosives are used (decision 0003 §4)
        }

        private void OnServerInitialized()
        {
            _store = new ReputationStore(this);
            _store.Load();
            _threat = new ThreatClock(this);
            _threat.Load();
            _saveTimer = timer.Every(Mathf.Max(15, _config.Reputation.SaveIntervalSeconds), () => { _store.Save(false); SaveIds(false); });
            LoadCheckpointData();
            LoadIds();
            InitOutpost();
            SpawnAllCheckpoints();
            SpawnPerimeters();
            SpawnAllFences();
            OutpostTick(false); // apply tonight's curfew silently if the load happens at night
            _tierTimer = timer.Every(Mathf.Max(1, _config.Checkpoints.TierCheckMinutes) * 60f, ReconcileCheckpoints);
            _threatTimer = timer.Every(Mathf.Max(10, _config.Threat.CheckIntervalSeconds), () => _threat.Tick());
            _outpostTimer = timer.Every(Mathf.Max(10, _config.Outpost.CheckIntervalSeconds), () => OutpostTick(true));
            _patrolTimer = timer.Every(0.25f, PatrolTick); // the patrol spheres follow the lead at 4 Hz; a no-op with no patrol standing
            Puts($"Cobalt Papers Please {Version} loaded: {_store.Count} reputation record(s), threat tier {CurrentTier()} ({HoursSinceWipe():0.0} h since wipe), {_checkpoints.Count} checkpoint(s) resolved, {LiveGuardCount()} guard(s) live.");
        }

        private void OnServerSave()
        {
            _store?.Save(false);
            _threat?.Save(false);
            SaveCheckpointData(false);
            SaveIds(false);
        }

        private void Unload()
        {
            _saveTimer?.Destroy();
            _disarmTimer?.Destroy();
            _tierTimer?.Destroy();
            _threatTimer?.Destroy();
            _outpostTimer?.Destroy();
            _patrolTimer?.Destroy();
            foreach (var p in _pending.Values) p.Flush?.Destroy();
            _pending.Clear();
            // Stock zone back before anything else goes — but never let it abort the despawns
            // below, which are what keep a reload free of orphans (review 2026-09-12).
            if (_curfewOn || _zoneDown) { try { _zoneDown = false; SetCurfew(false, "unload", false); } catch (Exception ex) { GLog($"[outpost] curfew restore on unload failed: {ex.Message}"); } }
            DespawnPerimeters();
            DespawnAllCheckpoints("unload");
            DespawnAllFences();
            foreach (var g in new List<GuardNpc>(_looseGuards)) DespawnGuard(g);
            if (_chicaneProtection != null) { UnityEngine.Object.Destroy(_chicaneProtection); _chicaneProtection = null; } // one ScriptableObject per reload otherwise
            _looseGuards.Clear();
            foreach (var e in _looseProps) if (e != null && !e.IsDestroyed) e.Kill();
            _looseProps.Clear();
            DestroyProbeTrigger();
            try { DespawnSabotage("unload"); } catch (Exception ex) { GLog($"[sabotage] despawn on unload failed: {ex.Message}"); }
            try { DespawnAllSquads("unload"); } catch (Exception ex) { GLog($"[ambush] stand-down on unload failed: {ex.Message}"); }
            foreach (var player in BasePlayer.activePlayerList) { CuiHelper.DestroyUi(player, UiMain); CuiHelper.DestroyUi(player, UiStop); CuiHelper.DestroyUi(player, UiFence); }
            SaveCheckpointData(true);
            SaveIds(true);
            _threat?.Save(true);
            _store?.Save(true);
            _instance = null;
        }

        #endregion

        #region ReputationStore (data + lazy decay)

        private class RepRecord
        {
            public int Score;
            public DateTime LastChangeUtc;
        }

        private class LedgerData
        {
            public string WipeId;
            public Dictionary<ulong, RepRecord> Records = new Dictionary<ulong, RepRecord>();
        }

        private class ReputationStore
        {
            private readonly PapersPlease _plugin;
            private LedgerData _data = new LedgerData();
            private bool _dirty;

            public ReputationStore(PapersPlease plugin) { _plugin = plugin; }

            public int Count => _data.Records.Count;

            public void Load()
            {
                try
                {
                    _data = Interface.Oxide.DataFileSystem.ReadObject<LedgerData>(DataFile) ?? new LedgerData();
                }
                catch (Exception ex)
                {
                    _plugin.PrintWarning($"Reputation data unreadable ({ex.Message}); starting empty.");
                    _data = new LedgerData();
                }
                if (_data.Records == null) _data.Records = new Dictionary<ulong, RepRecord>();
                var wipe = SaveRestore.WipeId;
                if (!string.IsNullOrEmpty(wipe) && _data.WipeId != wipe)
                {
                    if (_plugin._config.Reputation.ResetOnWipe && _data.Records.Count > 0)
                    {
                        _plugin.Puts($"New wipe detected ({wipe}); clearing {_data.Records.Count} reputation record(s).");
                        _data.Records.Clear();
                    }
                    _data.WipeId = wipe;
                    _dirty = true;
                }
            }

            public void Save(bool force)
            {
                if (!_dirty && !force) return;
                try
                {
                    Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    _plugin.PrintError($"Reputation save failed: {ex.Message}");
                }
            }

            // Lazy decay (decision 0003 §5): pull the stored score toward 0 by the hours elapsed
            // since the last change, keeping the fractional remainder in the timestamp so decay
            // is exact across restarts without any per-player timer.
            public int Read(ulong id)
            {
                RepRecord rec;
                if (!_data.Records.TryGetValue(id, out rec)) return 0;
                var rate = _plugin._config.Reputation.DecayPerHour;
                if (rate <= 0 || rec.Score == 0) return rec.Score;
                var hours = (DateTime.UtcNow - rec.LastChangeUtc).TotalHours;
                if (hours <= 0) return rec.Score;
                var steps = (int)Math.Floor(hours * rate);
                if (steps <= 0) return rec.Score;
                var magnitude = Math.Abs(rec.Score);
                var applied = Math.Min(steps, magnitude);
                rec.Score = rec.Score > 0 ? rec.Score - applied : rec.Score + applied;
                rec.LastChangeUtc = rec.LastChangeUtc.AddHours(applied / rate);
                if (rec.Score == 0) _data.Records.Remove(id); // a clean slate needs no record
                _dirty = true;
                return rec.Score;
            }

            public void Write(ulong id, int score)
            {
                if (score == 0) { _data.Records.Remove(id); _dirty = true; return; }
                RepRecord rec;
                if (!_data.Records.TryGetValue(id, out rec)) { rec = new RepRecord(); _data.Records[id] = rec; }
                rec.Score = score;
                rec.LastChangeUtc = DateTime.UtcNow;
                _dirty = true;
            }

            // Debug helper for the decay test: pretend the last change was `hours` ago.
            public bool BackDate(ulong id, double hours)
            {
                RepRecord rec;
                if (!_data.Records.TryGetValue(id, out rec)) return false;
                rec.LastChangeUtc = rec.LastChangeUtc.AddHours(-hours);
                _dirty = true;
                return true;
            }

            public List<KeyValuePair<ulong, int>> Snapshot()
            {
                var list = new List<KeyValuePair<ulong, int>>(_data.Records.Count);
                foreach (var id in new List<ulong>(_data.Records.Keys)) list.Add(new KeyValuePair<ulong, int>(id, Read(id)));
                return list;
            }
        }

        #endregion

        #region ReputationService (bands, adjust, notify, hook)

        public enum Band { Citizen, Neutral, Suspect, Wanted, Enemy }

        private static bool IsRealPlayerId(ulong id) => id.IsSteamId();
        private static bool IsRealPlayer(BasePlayer bp) => bp != null && ((ulong)bp.userID).IsSteamId();

        private bool IsExemptId(ulong id) => permission.UserHasPermission(id.ToString(), PermExempt);

        private Band BandOf(int score)
        {
            var b = _config.Bands;
            if (score >= b.CitizenMin) return Band.Citizen;
            if (score <= b.EnemyMax) return Band.Enemy;
            if (score <= b.WantedMax) return Band.Wanted;
            if (score <= b.SuspectMax) return Band.Suspect;
            return Band.Neutral;
        }

        private Band BandOfPlayer(ulong id) => IsExemptId(id) ? Band.Citizen : BandOf(ReadScore(id));

        private string BandName(Band band, string userId = null) => L("Band." + band, userId);
        private string BandDesc(Band band, string userId = null) => L("Desc." + band, userId);

        private int ReadScore(ulong id) => _store.Read(id);

        // Detector path: the reason is a lang key, localised for the player.
        private int Adjust(ulong id, int delta, string reasonKey, bool absolute = false) =>
            ApplyChange(id, delta, L(reasonKey, id.ToString()), absolute);

        // Every change goes through here: clamp, exemption, store, hook, notification.
        // Exempt players only move on an explicit set/reset (absolute), never on a relative
        // adjustment from a detector, an admin `add`, or another plugin.
        private int ApplyChange(ulong id, int delta, string reasonText, bool absolute)
        {
            if (!IsRealPlayerId(id) || _store == null) return 0;
            if (!absolute && IsExemptId(id)) return ReadScore(id);
            var old = ReadScore(id);
            var next = Mathf.Clamp(absolute ? delta : old + delta, -100, 100);
            if (next == old) return old;
            _store.Write(id, next);
            // Milestone 7: a legit ID lapses when its owner falls to Wanted (decision 0008 §8) —
            // only the 'immediate' mode acts here; 'grace' waits for the next scan (owner's call, 2026-09-14).
            if (_config.Papers.Enabled && LegitRevokeMode() == "immediate" && next <= _config.Bands.WantedMax && old > _config.Bands.WantedMax && RevokeLegitIds(id, "wanted") > 0)
            {
                var holder = BasePlayer.FindByID(id);
                if (holder != null && holder.IsConnected) holder.ChatMessage(L("Notify.IdRevoked", holder.UserIDString));
            }
            Interface.CallHook("OnReputationChanged", id, old, next, reasonText);
            Notify(id, old, next, reasonText);
            return next;
        }

        private readonly Dictionary<ulong, float> _lastNotify = new Dictionary<ulong, float>();
        private class PendingNotify { public int Delta; public string Reason; public int Score; public Timer Flush; }
        private readonly Dictionary<ulong, PendingNotify> _pending = new Dictionary<ulong, PendingNotify>();

        // One chat line per player per throttle window; changes inside the window are summed and
        // flushed as a single line when it ends (a destroying blast is −5 then −10: the player
        // must see the −10 land, not just infer it later from /papers).
        private void Notify(ulong id, int old, int next, string reason)
        {
            var player = BasePlayer.FindByID(id);
            if (player == null || !player.IsConnected) return;
            var oldBand = BandOf(old); var newBand = BandOf(next);
            var now = Time.realtimeSinceStartup;
            var window = Mathf.Max(1, _config.Reputation.NotifyThrottleSeconds);
            float last;
            var throttled = _lastNotify.TryGetValue(id, out last) && now - last < window;
            if (!throttled)
            {
                _lastNotify[id] = now;
                SendChange(player, next - old, reason, next);
            }
            else
            {
                PendingNotify p;
                if (!_pending.TryGetValue(id, out p)) { p = new PendingNotify(); _pending[id] = p; }
                p.Delta += next - old; p.Reason = reason; p.Score = next;
                if (p.Flush == null)
                {
                    var remaining = Mathf.Max(0.5f, window - (now - last));
                    p.Flush = timer.Once(remaining, () => FlushPending(id));
                }
            }
            if (newBand != oldBand)
                player.ChatMessage(L("Notify.Band", player.UserIDString, BandName(newBand, player.UserIDString)));
        }

        private void FlushPending(ulong id)
        {
            PendingNotify p;
            if (!_pending.TryGetValue(id, out p)) return;
            _pending.Remove(id);
            var player = BasePlayer.FindByID(id);
            if (player == null || !player.IsConnected || p.Delta == 0) return;
            _lastNotify[id] = Time.realtimeSinceStartup;
            SendChange(player, p.Delta, p.Reason, p.Score);
        }

        private void SendChange(BasePlayer player, int delta, string reason, int score) =>
            player.ChatMessage(L("Notify.Change", player.UserIDString, delta > 0 ? "+" : "", delta, reason, score));

        #endregion

        #region ThreatClock (decision 0005: wipe-age base + decaying violence nudge, tier = value / 20)

        private ThreatClock _threat;
        private Timer _threatTimer;

        private double HoursSinceWipe()
        {
            var created = SaveRestore.SaveCreatedTime;
            if (created == default(DateTime)) return 0;
            return Math.Max(0, (DateTime.UtcNow - created.ToUniversalTime()).TotalHours);
        }

        private int CurrentTier() => _threat != null ? _threat.Tier : TierFromValue(BaseThreat(HoursSinceWipe()));

        private static int TierFromValue(double value) => Mathf.Clamp(1 + (int)Math.Floor(value / 20.0), 1, 5);

        // Piecewise-linear through (TierHours[i], 20*i); past the last anchor the last segment's
        // slope continues to 100. With the defaults tier n still begins exactly at TierHours[n-1].
        private double BaseThreat(double hours)
        {
            var anchors = _config.TierHours;
            if (anchors == null || anchors.Length == 0) return 0;
            if (hours <= anchors[0]) return 0;
            for (var i = 1; i < anchors.Length && i < 5; i++)
            {
                if (hours <= anchors[i])
                {
                    var span = Math.Max(1, anchors[i] - anchors[i - 1]);
                    return 20.0 * (i - 1) + 20.0 * (hours - anchors[i - 1]) / span;
                }
            }
            var last = Math.Min(anchors.Length, 5) - 1;
            var slope = last > 0 ? 20.0 / Math.Max(1, anchors[last] - anchors[last - 1]) : 20.0 / 48.0;
            return Math.Min(100.0, 20.0 * last + (hours - anchors[last]) * slope);
        }

        private class ThreatData
        {
            public string WipeId;
            public double Nudge;
            public DateTime NudgeUpdatedUtc = DateTime.UtcNow;
            public int PinnedTier;
            public int LastTier;
        }

        private class ThreatClock
        {
            private readonly PapersPlease _plugin;
            private ThreatData _data = new ThreatData();
            private bool _dirty;

            public ThreatClock(PapersPlease plugin) { _plugin = plugin; }

            public void Load()
            {
                try { _data = Interface.Oxide.DataFileSystem.ReadObject<ThreatData>(ThreatDataFile) ?? new ThreatData(); }
                catch (Exception ex) { _plugin.PrintWarning($"Threat data unreadable ({ex.Message}); starting fresh."); _data = new ThreatData(); }
                var wipe = SaveRestore.WipeId;
                if (!string.IsNullOrEmpty(wipe) && _data.WipeId != wipe)
                {
                    if (_data.WipeId != null) _plugin.Puts($"New wipe detected ({wipe}); threat nudge, pin and tier memory cleared.");
                    _data = new ThreatData { WipeId = wipe };
                    _dirty = true;
                }
                if (_data.LastTier == 0) { _data.LastTier = Tier; _dirty = true; }
            }

            public void Save(bool force)
            {
                if (!_dirty && !force) return;
                try { Interface.Oxide.DataFileSystem.WriteObject(ThreatDataFile, _data); _dirty = false; }
                catch (Exception ex) { _plugin.PrintError($"Threat save failed: {ex.Message}"); }
            }

            public double Hours => _plugin.HoursSinceWipe();
            public double Base => _plugin.BaseThreat(Hours);
            public int PinnedTier => _data.PinnedTier;
            public int LastTier { get { return _data.LastTier; } set { if (_data.LastTier != value) { _data.LastTier = value; _dirty = true; } } }

            // Lazy decay toward 0 (both signs), exact in wall-clock hours so restarts and empty
            // servers count as quiet time.
            public double Nudge
            {
                get
                {
                    var rate = Math.Max(0, _plugin._config.Threat.NudgeDecayPerHour);
                    var now = DateTime.UtcNow;
                    var hours = (now - _data.NudgeUpdatedUtc).TotalHours;
                    if (rate > 0 && hours > 0 && _data.Nudge != 0)
                    {
                        var drain = Math.Min(Math.Abs(_data.Nudge), rate * hours);
                        _data.Nudge += _data.Nudge > 0 ? -drain : drain;
                        _dirty = true;
                    }
                    _data.NudgeUpdatedUtc = now;
                    return _data.Nudge;
                }
            }

            public double Value => Math.Max(0, Math.Min(100, Base + Nudge));

            public int Tier => _data.PinnedTier > 0 ? Mathf.Clamp(_data.PinnedTier, 1, 5) : TierFromValue(Value);

            public double AddNudge(double delta)
            {
                var max = Math.Max(0, _plugin._config.Threat.NudgeMax);
                _data.Nudge = Math.Max(-max, Math.Min(max, Nudge + delta));
                _data.NudgeUpdatedUtc = DateTime.UtcNow;
                _dirty = true;
                return _data.Nudge;
            }

            // Make the value read `target` now (within NudgeMax); it decays normally afterwards.
            public double SetValue(double target)
            {
                var wanted = Math.Max(0, Math.Min(100, target)) - Base;
                _data.Nudge = 0;
                return AddNudge(wanted);
            }

            public void Pin(int tier) { _data.PinnedTier = tier; _dirty = true; }

            // Debug: pretend the last nudge change was `hours` ago.
            public void BackDate(double hours) { _data.NudgeUpdatedUtc = _data.NudgeUpdatedUtc.AddHours(-hours); _dirty = true; }

            // Hours until the next tier at the current nudge (the nudge decays, so this is a floor).
            public double HoursToTier(int tier)
            {
                if (tier > 5) return -1;
                var need = 20.0 * (tier - 1) - Nudge;
                var h = Hours;
                for (var probe = h; probe <= h + 24 * 60; probe += 0.25)
                    if (_plugin.BaseThreat(probe) >= need) return Math.Max(0, probe - h);
                return -1;
            }

            private int _ticks;

            public void Tick()
            {
                _plugin.CheckTierChange();
                // The lazy decay dirties the record on every read; flush it every fifth tick
                // (5 min at the default interval) rather than every minute. Unload/save still flush.
                if (++_ticks % 5 == 0) Save(false);
            }
        }

        private string ThreatStatus(string viewerId)
        {
            var tier = _threat.Tier;
            var pinned = _threat.PinnedTier > 0 ? L("Threat.Pinned", viewerId) : "";
            var next = tier >= 5 ? L("Threat.Max", viewerId) : "";
            if (tier < 5 && _threat.PinnedTier == 0)
            {
                var h = _threat.HoursToTier(tier + 1);
                next = h >= 0 ? L("Threat.Next", viewerId, tier + 1, h) : L("Threat.Max", viewerId);
            }
            return L("Threat.Status", viewerId, tier, _threat.Value, _threat.Hours, _threat.Base, _threat.Nudge, pinned, next)
                + (_curfewOn ? L("Threat.Curfew", viewerId) : "");
        }

        // Shared by /threat and papers.threat; returns the reply. Non-admins only get the status.
        private string RunThreat(string[] args, int first, bool admin, string viewerId)
        {
            var sub = args.Length > first ? args[first].ToLowerInvariant() : "show";
            if (sub == "show" || sub == "status") return ThreatStatus(viewerId);
            if (!admin) return L("Papers.NoPermission", viewerId);
            var arg1 = args.Length > first + 1 ? args[first + 1].ToLowerInvariant() : null;
            double n;
            switch (sub)
            {
                case "set":
                    if (arg1 == null || !double.TryParse(arg1, out n)) return L("Threat.Usage", viewerId);
                    var nudge = _threat.SetValue(n);
                    OnTierMaybeChanged();
                    return L("Threat.Set", viewerId, _threat.Value, nudge, _threat.Tier);
                case "tier":
                    if (arg1 == "off" || arg1 == "0")
                    {
                        _threat.Pin(0);
                        OnTierMaybeChanged();
                        return L("Threat.Unpin", viewerId, _threat.Tier);
                    }
                    int t;
                    if (arg1 == null || !int.TryParse(arg1, out t) || t < 1 || t > 5) return L("Threat.Usage", viewerId);
                    _threat.Pin(t);
                    OnTierMaybeChanged();
                    return L("Threat.Pin", viewerId, t);
                case "nudge":
                    if (arg1 == null || !double.TryParse(arg1, out n)) return L("Threat.Usage", viewerId);
                    var before = _threat.Nudge;
                    var after = _threat.AddNudge(n);
                    OnTierMaybeChanged();
                    return L("Threat.Nudged", viewerId, after - before, after, _threat.Value, _threat.Tier);
                default:
                    return L("Threat.Usage", viewerId);
            }
        }

        private void OnTierMaybeChanged()
        {
            CheckTierChange();
            _threat.Save(true);
        }

        // 4.2: every nudge comes through here so a tier change is noticed at once.
        private void Nudge(double amount, string why)
        {
            if (_threat == null || amount == 0) return;
            var bonus = _threat.AddNudge(amount);
            CLog($"[threat] {amount:+0.##;-0.##} ({why}) → bonus {bonus:+0.0;-0.0}, value {_threat.Value:0.0}, tier {_threat.Tier}.");
            CheckTierChange();
        }

        // 4.3: broadcast, hook, and an immediate checkpoint reconcile on every tier change.
        // LastTier persists so a restart does not re-announce the tier the server went down on.
        private void CheckTierChange()
        {
            if (_threat == null) return;
            var tier = _threat.Tier;
            var old = _threat.LastTier;
            if (tier == old) return;
            _threat.LastTier = tier;
            var line = tier > old
                ? (_config.Voice.Tier != null && _config.Voice.Tier.Count >= tier ? _config.Voice.Tier[tier - 1] : $"Cobalt threat level {tier}.")
                : string.Format(Pick(_config.Voice.TierDown, "Cobalt threat level lowered to {0}."), tier);
            CLog($"[threat] tier {old} → {tier} (value {_threat.Value:0.0}{(_threat.PinnedTier > 0 ? ", pinned" : "")}).");
            Broadcast("Broadcast.Tier", line);
            Interface.CallHook("OnThreatTierChanged", old, tier);
            // A nudge can arrive from inside OnEntityDeath (a guard kill); re-manning gates kills
            // and spawns guards, so it runs on the next tick, outside any hook.
            NextTick(ReconcileCheckpoints);
            _threat.Save(true);
        }

        [ChatCommand("threat")]
        private void CmdThreat(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            player.ChatMessage(RunThreat(args ?? new string[0], 0, IsAdmin(player), player.UserIDString));
        }

        [ConsoleCommand("papers.threat")]
        private void CmdThreatConsole(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            var admin = caller == null || IsAdmin(caller);
            var viewer = caller?.UserIDString;
            var args = ConsoleArgs(arg);
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
            switch (sub)
            {
                case "simulate":
                {
                    double hours;
                    if (!admin) { arg.ReplyWith(L("Papers.NoPermission", viewer)); return; }
                    if (args.Length < 2 || !double.TryParse(args[1], out hours)) { arg.ReplyWith("papers.threat simulate <hours>  — back-dates the violence bonus to test decay"); return; }
                    _threat.BackDate(hours);
                    arg.ReplyWith($"Back-dated the nudge by {hours} h → {ThreatStatus(viewer)}");
                    return;
                }
                case "save":
                    if (!admin) { arg.ReplyWith(L("Papers.NoPermission", viewer)); return; }
                    _threat.Save(true);
                    arg.ReplyWith("Saved.");
                    return;
                case "help":
                    arg.ReplyWith("papers.threat [show] | set <0-100> | tier <1-5|off> | nudge <±n> | simulate <hours> | save");
                    return;
                default:
                    arg.ReplyWith(RunThreat(args, 0, admin, viewer));
                    return;
            }
        }

        #endregion

        #region Crime detectors

        // Attribution: the player behind a HitInfo, or the owner of the initiator (turret, vehicle).
        private static ulong AttackerId(HitInfo info)
        {
            if (info == null) return 0;
            var p = info.InitiatorPlayer;
            if (p != null && IsRealPlayerId((ulong)p.userID)) return (ulong)p.userID;
            var ent = info.Initiator;
            if (ent != null && IsRealPlayerId(ent.OwnerID)) return ent.OwnerID;
            return 0;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            var attacker = AttackerId(info);
            var vault = entity is Door ? VaultOfDoor(entity) : null;
            if (vault != null) OnVaultBreached(vault, attacker, info?.WeaponPrefab?.ShortPrefabName ?? "?");
            if (_looseProps.Contains(entity))
            {
                // Task 8.1 probe (a): the loose vault door under explosives.
                CLog($"[probe] loose {entity.ShortPrefabName} died (killer {(attacker != 0 ? attacker.ToString() : "none")}, weapon {info?.WeaponPrefab?.ShortPrefabName ?? "?"}).");
                _looseProps.Remove(entity);
            }

            // Scientist kills: exact vanilla type only (other plugins' subclasses are not Cobalt).
            if (entity is ScientistNPC)
            {
                var guard = entity as GuardNpc;
                if (guard != null)
                {
                    // The fence is Bandit's, not Cobalt's (decision 0008 §4): no rep, no clock (review 2026-09-16).
                    if (attacker != 0 && !guard.Passive)
                    {
                        Adjust(attacker, _config.Reputation.GuardKill, "Reason.GuardKill");
                        Nudge(_config.Threat.NudgeGuardKill, "guard killed");
                        if (_config.Checkpoints.BroadcastRunAndKill && guard.Checkpoint != null)
                            Broadcast("Broadcast.GuardKilled", info?.InitiatorPlayer?.displayName ?? attacker.ToString(), guard.Checkpoint.Name);
                    }
                    OnGuardDied(guard, attacker);
                }
                else if (attacker != 0 && entity.GetType() == typeof(ScientistNPC))
                {
                    Adjust(attacker, _config.Reputation.ScientistKill, "Reason.ScientistKill");
                    Nudge(_config.Threat.NudgeScientistKill, "scientist killed");
                }
                return;
            }

            // A real player dying mid-encounter clears it without penalty (task 3.10).
            var victim = entity as BasePlayer;
            if (victim != null && IsRealPlayer(victim))
            {
                Encounter e;
                if (_encounters.TryGetValue((ulong)victim.userID, out e)) Abort(e, "died");
                return;
            }

            if (attacker == 0 || info == null) return;

            // Raids, always-on half: a foreign-TC structure destroyed by explosives.
            if ((entity is BuildingBlock || entity is Door || entity is BuildingPrivlidge) && info.damageTypes.Has(global::Rust.DamageType.Explosion))
            {
                uint buildingId;
                if (!IsForeignTcStructure(entity, attacker, out buildingId)) return;
                ChargeRaid(attacker, buildingId, _config.Reputation.StructureDestroyed, "Reason.StructureDestroyed");
                Nudge(_config.Threat.NudgeStructureDestroyed, "structure destroyed");
            }
        }

        // Per attacker+building ledger for one cap window (decision 0003 §4). A destroying blast
        // damages the structure first, so the partial hit lands before the death; charging the
        // *difference* makes "partial then destroyed" cost exactly StructureDestroyed, "destroyed"
        // alone the same, and "partial" alone PartialRaid — never less than the worst outcome
        // (0.2.1 charged −5 and then capped the −15; live 2026-09-08).
        private class RaidEntry { public float Until; public int Charged; }
        private readonly Dictionary<string, RaidEntry> _raidLedger = new Dictionary<string, RaidEntry>();

        private void ChargeRaid(ulong attacker, uint buildingId, int penalty, string reasonKey)
        {
            var key = attacker + ":" + buildingId;
            var now = Time.realtimeSinceStartup;
            RaidEntry entry;
            if (!_raidLedger.TryGetValue(key, out entry) || now >= entry.Until)
            {
                entry = new RaidEntry();
                _raidLedger[key] = entry;
            }
            var remaining = Math.Abs(penalty) - entry.Charged;
            if (remaining <= 0) return;
            entry.Charged += remaining;
            entry.Until = now + _config.Reputation.RaidCapMinutes * 60f;
            Adjust(attacker, -remaining, reasonKey);
            if (_raidLedger.Count > 256)
            {
                var dead = new List<string>();
                foreach (var kv in _raidLedger) if (kv.Value.Until < now) dead.Add(kv.Key);
                foreach (var k in dead) _raidLedger.Remove(k);
            }
        }

        // True when the structure belongs to a building whose dominating tool cupboard does NOT
        // authorise the attacker. No TC (plugin-spawned, decorative) → not a raid.
        private static bool IsForeignTcStructure(BaseCombatEntity entity, ulong attacker, out uint buildingId)
        {
            buildingId = 0;
            var decay = entity as DecayEntity;
            if (decay == null) return false;
            var building = decay.GetBuilding();
            if (building == null) return false;
            buildingId = building.ID;
            var priv = entity as BuildingPrivlidge ?? building.GetDominatingBuildingPrivilege();
            if (priv == null) return false;
            return !priv.IsAuthed(attacker);
        }

        // Cobalt crates: dock once per container instance and tag the contents so whatever is
        // taken is Cobalt property at the next checkpoint (decision 0003 §3).
        private readonly Dictionary<ulong, float> _crateMemory = new Dictionary<ulong, float>();

        // Decision 0011: civic work through Public Works (2.9+) earns standing.
        private void OnPublicWorksRepair(BasePlayer fixer, string service, bool major)
        {
            if (fixer == null || !IsRealPlayerId((ulong)fixer.userID)) return;
            var delta = major ? _config.Reputation.PublicWorksRepairMajor : _config.Reputation.PublicWorksRepairMinor;
            if (delta != 0) Adjust((ulong)fixer.userID, delta, "Reason.PublicWorksRepair");
        }

        // Once per player per UTC day, so topping up every service is not a standing farm.
        // In memory only: a reload can grant one more on the same day.
        private readonly Dictionary<ulong, DateTime> _pwPurchaseDay = new Dictionary<ulong, DateTime>();

        private void OnPublicWorksPurchase(BasePlayer player, string service, int scrap)
        {
            if (player == null || _config.Reputation.PublicWorksPurchase == 0) return;
            var id = (ulong)player.userID;
            if (!IsRealPlayerId(id)) return;
            DateTime last;
            var today = DateTime.UtcNow.Date;
            if (_pwPurchaseDay.TryGetValue(id, out last) && last == today) return;
            _pwPurchaseDay[id] = today;
            Adjust(id, _config.Reputation.PublicWorksPurchase, "Reason.PublicWorksPurchase");
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || !IsRealPlayerId((ulong)player.userID)) return;
            var container = entity as StorageContainer;
            if (container == null || container.inventory == null) return;
            int minTier;
            if (!_config.CobaltCrates.TryGetValue(entity.ShortPrefabName, out minTier)) return;
            if (CurrentTier() < minTier) return;
            var netId = entity.net != null ? entity.net.ID.Value : 0UL;
            var now = Time.realtimeSinceStartup;
            float until;
            if (netId != 0 && _crateMemory.TryGetValue(netId, out until) && now < until) return;
            if (netId != 0) _crateMemory[netId] = now + _config.Reputation.CrateMemoryMinutes * 60f;
            if (_crateMemory.Count > 256)
            {
                var dead = new List<ulong>();
                foreach (var kv in _crateMemory) if (kv.Value < now) dead.Add(kv.Key);
                foreach (var k in dead) _crateMemory.Remove(k);
            }
            TagContainer(container.inventory);
            Adjust((ulong)player.userID, _config.Reputation.CobaltCrate, "Reason.CobaltCrate");
            Nudge(_config.Threat.NudgeCobaltCrate, "Cobalt crate looted");
        }

        private int TagContainer(ItemContainer inv)
        {
            var tag = _config.Contraband.Tag;
            var n = 0;
            foreach (var item in inv.itemList)
            {
                if (item == null || IsTagged(item)) continue;
                var baseName = string.IsNullOrEmpty(item.name) ? item.info.displayName.english : item.name;
                item.name = baseName + tag;
                item.MarkDirty();
                n++;
            }
            return n;
        }

        private bool IsTagged(Item item) => item != null && !string.IsNullOrEmpty(item.name) && item.name.EndsWith(_config.Contraband.Tag);

        // The name without the tag: a custom name the item had before it was tagged survives a
        // laundering; a stock name goes back to null (review 2026-09-16).
        private string StripTag(Item item)
        {
            if (!IsTagged(item)) return item?.name;
            var bare = item.name.Substring(0, item.name.Length - _config.Contraband.Tag.Length);
            return string.IsNullOrEmpty(bare) || bare == item.info.displayName.english ? null : bare;
        }

        // Contraband short names as a set (rebuilt on config load; scanned by the checkpoint Scanner).
        private readonly HashSet<string> _contraband = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void RebuildContrabandSet()
        {
            _contraband.Clear();
            if (_config.Contraband?.Items == null) return;
            foreach (var s in _config.Contraband.Items) if (!string.IsNullOrEmpty(s)) _contraband.Add(s.Trim());
        }

        // Raids, armed half: OnEntityTakeDamage is expensive to leave on, so it is subscribed only
        // for a window after any real player uses an explosive, then dropped again.
        private Timer _disarmTimer;
        private bool _armed;

        private void OnExplosiveThrown(BasePlayer player, BaseEntity entity, ThrownWeapon item) => Arm(player);
        private void OnExplosiveDropped(BasePlayer player, BaseEntity entity, ThrownWeapon item) => Arm(player);
        private void OnRocketLaunched(BasePlayer player, BaseEntity entity) => Arm(player);

        private void Arm(BasePlayer player)
        {
            if (!_config.Reputation.PartialRaidDetector || player == null || !IsRealPlayerId((ulong)player.userID)) return;
            if (!_armed) { Subscribe(nameof(OnEntityTakeDamage)); _armed = true; }
            _disarmTimer?.Destroy();
            _disarmTimer = timer.Once(Mathf.Max(10, _config.Reputation.PartialRaidWindowSeconds), Disarm);
        }

        private void Disarm()
        {
            if (_armed) { Unsubscribe(nameof(OnEntityTakeDamage)); _armed = false; }
            _disarmTimer = null;
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            if (!(entity is BuildingBlock || entity is Door)) return;
            if (!info.damageTypes.Has(global::Rust.DamageType.Explosion)) return;
            var attacker = AttackerId(info);
            if (attacker == 0) return;
            uint buildingId;
            if (!IsForeignTcStructure(entity, attacker, out buildingId)) return;
            ChargeRaid(attacker, buildingId, _config.Reputation.PartialRaid, "Reason.PartialRaid");
        }

        #endregion

        #region Guards (GuardNpc / GuardBrain / CopyPrefabFields — probe recipe, decision 0002)

        // In-memory log surfaced by `papers.cp log` — the tester cannot see server Puts and the
        // Oxide log on the share can freeze (2026-09-07).
        private readonly List<string> _clog = new List<string>();
        private const int LogCap = 300;

        private void CLog(string line)
        {
            _clog.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_clog.Count > LogCap) _clog.RemoveAt(0);
            Puts(line);
        }

        private static void GLog(string line)
        {
            if (_instance != null) _instance.CLog(line);
        }

        // Rust 2633.288 removed BaseEntity.SetFlag; flags change inside a scope now.
        private static void SetFlagNet(BaseEntity entity, BaseEntity.Flags flag, bool value)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                scope.Set(flag, value);
            }
        }

        private static string V(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";

        public class GuardNpc : ScientistNPC
        {
            public Vector3 PostPos;
            public Vector3 PostFacing = Vector3.forward;
            public float AimRange = 25f;
            public float CombatRange = 30f;
            public float Leash = 40f;
            public float IdleRange = 100f;
            public int ThinkCount;
            public string GuardName = "Cobalt Guard";
            public Checkpoint Checkpoint;
            public bool Passive;             // the fence: posts, faces, never engages (Milestone 7)

            // ScientistNPC computes displayName per read; a field never sticks.
            public override string displayName =>
                string.IsNullOrEmpty(GuardName) ? base.displayName : GuardName;

            public override void AttackerInfo(ProtoBuf.PlayerLifeStory.DeathInfo info)
            {
                info.attackerName = GuardName;
                info.attackerSteamID = (ulong)userID;
            }

            public override void OnAttacked(HitInfo info)
            {
                base.OnAttacked(info);
                var attacker = info?.InitiatorPlayer;
                if (attacker != null && attacker != this && !attacker.IsDestroyed)
                    GetComponent<GuardBrain>()?.NotifyAttacked(attacker);
                // A car's hurt trigger credits the DRIVER as the initiator and carries the vehicle
                // only as WeaponPrefab (GroundVehicle.GetPlayerDamageInitiator, live 2026-09-12:
                // a bump killed a guard with killer=<driver> and no run-over line). Boats and
                // helicopters still arrive with the vehicle itself as the initiator.
                var vehicle = info?.Initiator as BaseVehicle;
                if (vehicle == null && attacker != null && info.WeaponPrefab is BaseVehicle) vehicle = attacker.GetMountedVehicle();
                if (vehicle != null && _instance != null) _instance.OnGuardHitByVehicle(this, vehicle);
            }
        }

        // agentTypeID of the navmesh surface that answers at a given spot ('Animal' off-monument
        // on the live server; the Humanoid bake exists only inside monuments). Probed per spawn
        // because gates sit at monument edges where either may answer.
        public static int AgentTypeAt(Vector3 near, out string surfaceName)
        {
            surfaceName = "none";
            for (var i = 0; i < NavMesh.GetSettingsCount(); i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                var filter = new NavMeshQueryFilter { agentTypeID = s.agentTypeID, areaMask = NavMesh.AllAreas };
                NavMeshHit hit;
                if (NavMesh.SamplePosition(near, out hit, 6f, filter))
                {
                    surfaceName = NavMesh.GetSettingsNameFromID(s.agentTypeID);
                    return s.agentTypeID;
                }
            }
            return int.MinValue;
        }

        public class GuardBrain : ScientistBrain
        {
            private GuardNpc _npc;
            private NavMeshAgent _agent;
            private float _lastMoveTick;
            private BasePlayer _target;
            private BasePlayer _pendingTarget; // a ForceTarget that arrived before InitializeAI
            private string _pendingWhy;
            private float _nextFireAt;
            private float _nextPathAt;
            private float _lastLosAt;
            private float _engageStartAt;
            private float _nextIdleCheckAt;
            private bool _idle;
            private bool _relaxed;
            private const float EngageTimeout = 120f;
            private const float NoLosTimeout = 20f;
            public string StateName = "post";
            public string Surface = "?";
            // The encounter's current subject: a posted guard aims at them before anyone else.
            public BasePlayer Watch;

            public override void AddStates()
            {
                states = new Dictionary<AIState, BasicAIState>(); // no AIDesign graph — Think is manual
            }

            public override void InitializeAI()
            {
                // No base call: the vanilla path needs AIInformationZone move points.
                _npc = GetComponent<GuardNpc>();
                ThinkMode = AIThinkMode.Interval;
                thinkRate = 0.25f;
                Navigator = GetComponent<BaseNavigator>();
                _agent = GetComponent<NavMeshAgent>();
                if (_npc == null || Navigator == null || _agent == null) return;
                _agent.enabled = false;
                var typeId = AgentTypeAt(_npc.transform.position, out Surface);
                if (typeId != int.MinValue && _npc.NavAgent != null)
                {
                    _npc.NavAgent.agentTypeID = typeId;
                    _npc.NavAgent.areaMask = NavMesh.AllAreas;
                }
                _agent.speed = 5f;
                _agent.acceleration = 8f;
                _agent.angularSpeed = 120f;
                Navigator.MaxWaterDepth = 0.5f;
                _agent.updatePosition = false;
                _agent.updateRotation = false;
                NavMeshHit snap;
                if (NavMesh.SamplePosition(_npc.transform.position, out snap, 6f, NavMesh.AllAreas))
                    _npc.transform.position = snap.position;
                _agent.enabled = true;
                Navigator.SetNavMeshEnabled(true);
                Navigator.PlaceOnNavMesh(0f);
                _lastMoveTick = UnityEngine.Time.realtimeSinceStartup;
                InvokeRandomized(MovementPump, 1f, 0.1f, 0.01f);
                Senses.Init(owner: _npc, brain: this, memoryDuration: 10f, range: 60f, targetLostRange: 120f,
                    visionCone: -1f, checkVision: false, checkLOS: false, ignoreNonVisionSneakers: true,
                    listenRange: 20f, hostileTargetsOnly: false, senseFriendlies: false,
                    ignoreSafeZonePlayers: false, senseTypes: EntityType.Player, refreshKnownLOS: false);
                SetRelaxed(true);
                if (_pendingTarget != null)
                {
                    var t = _pendingTarget; var why = _pendingWhy ?? "checkpoint";
                    _pendingTarget = null; _pendingWhy = null;
                    Engage(t, why);
                }
            }

            // InvokeRandomized delegates outlive a killed entity — cancel before despawn.
            public void StopPump() => CancelInvoke(MovementPump);

            public void NotifyAttacked(BasePlayer attacker)
            {
                if (_npc == null || _npc.IsDestroyed || attacker == null || attacker.IsDead()) return;
                if (!IsRealPlayer(attacker)) return;
                Engage(attacker, "attacked");
            }

            public void ForceTarget(BasePlayer player) => Engage(player, "checkpoint");

            // Milestone 9 (decision 0010 §A): a road route to walk — a patrol's lead, or the 9.1 probe's
            // walker. Null = a posted guard as before. A hostile interrupts the walk (HostileTick) and
            // the walk resumes when the target is gone; a stop pauses it. Progress and stalls are logged.
            private List<Vector3> _walk;
            private int _walkIndex, _walkSkips;
            private float _walkStallSeconds = 10f, _walkBestDist = float.MaxValue, _walkLastProgressAt, _walkLoggedAt;
            private Vector3 _walkLastPos;
            public float WalkedMetres;
            public string WalkNote = "";
            public bool Walking => _walk != null;
            public int WalkIndex => _walkIndex;
            public int WalkCount => _walk?.Count ?? 0;

            public void StartWalk(List<Vector3> points, float stallSeconds)
            {
                _walk = points != null && points.Count > 0 ? points : null;
                _walkIndex = 0; _walkSkips = 0; WalkedMetres = 0f; _walkLastPos = Vector3.zero;
                _walkStallSeconds = Mathf.Max(3f, stallSeconds);
                _walkBestDist = float.MaxValue; _walkLastProgressAt = UnityEngine.Time.realtimeSinceStartup; _walkLoggedAt = 0f;
                WalkNote = _walk == null ? "no points" : "walking";
                if (_walk != null) StateName = "walk";
            }

            public void StopWalk(string why)
            {
                if (_walk == null) return;
                _walk = null; WalkNote = why;
                if (_npc != null && !_npc.IsDestroyed) _npc.PostPos = _npc.transform.position; // the walk ends where it ends
                ReturnToPost(why);
            }

            // A patrol pulling someone over stands where it is until the encounter ends; the
            // route is kept and the stall clock does not run while halted (task 9.4).
            private bool _walkPaused;
            public void PauseWalk() { _walkPaused = true; }
            public void ResumeWalk() { _walkPaused = false; _walkLastProgressAt = UnityEngine.Time.realtimeSinceStartup; _walkBestDist = float.MaxValue; }

            private void WalkTick(float now)
            {
                if (_walkPaused)
                {
                    if (StateName != "halt") { StateName = "halt"; Navigator.Stop(); SetRelaxed(false); }
                    _walkLastProgressAt = now;
                    if (Watch != null && !Watch.IsDestroyed && !Watch.IsDead())
                    {
                        var look = Watch.eyes.position - _npc.eyes.position;
                        if (look.sqrMagnitude > 0.01f) _npc.SetAimDirection(look.normalized);
                    }
                    return;
                }
                if (_walkIndex >= _walk.Count)
                {
                    GLog($"guard '{_npc.GuardName}' walked the route: {WalkedMetres:0} m, {_walkSkips} skip(s) at the end.");
                    StopWalk("route done");
                    return;
                }
                var target = _walk[_walkIndex];
                var pos = _npc.transform.position;
                var d = Vector3.Distance(pos, target);
                if (_walkLastPos != Vector3.zero) WalkedMetres += Vector3.Distance(pos, _walkLastPos);
                _walkLastPos = pos;
                if (d < _walkBestDist - 0.25f) { _walkBestDist = d; _walkLastProgressAt = now; }
                if (d <= 2.5f) { _walkIndex++; _walkBestDist = float.MaxValue; _walkLastProgressAt = now; _walkSkips = 0; return; }
                if (now - _walkLastProgressAt > _walkStallSeconds)
                {
                    _walkSkips++;
                    string surf; AgentTypeAt(target, out surf);
                    GLog($"guard '{_npc.GuardName}' stalled {d:0} m short of point {_walkIndex} at {V(target)} (surface '{surf}', on navmesh={(_agent != null && _agent.isOnNavMesh)}); skipping ({_walkSkips} in a row).");
                    _walkIndex++; _walkBestDist = float.MaxValue; _walkLastProgressAt = now;
                    if (_walkSkips >= 3) { GLog($"guard '{_npc.GuardName}' walk abandoned after three skips, {WalkedMetres:0} m walked."); StopWalk("three skips"); }
                    return;
                }
                // One short line a minute per walker (the 9.1 probe's 10 s instrumented line proved
                // there is no navmesh under a guard; three patrols made it spam).
                if (now - _walkLoggedAt > 60f)
                {
                    _walkLoggedAt = now;
                    GLog($"guard '{_npc.GuardName}' walk: {WalkedMetres:0} m, point {_walkIndex}/{_walk.Count}, {d:0} m to it.");
                }
                if (StateName != "walk") { StateName = "walk"; SetRelaxed(true); }
                if (now >= _nextPathAt)
                {
                    _nextPathAt = now + 1f;
                    NavMeshHit hit;
                    var dest = NavMesh.SamplePosition(target, out hit, 6f, NavMesh.AllAreas) ? hit.position : target;
                    Navigator.SetDestination(dest, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                }
            }

            // The second guard of a patrol keeps `gap` metres behind the lead (decision 0010 §A):
            // hops of ≤ 8 m toward a point behind the lead, stands and faces him when close enough.
            private GuardNpc _followLead;
            private float _followGap = 4f;

            public void StartFollow(GuardNpc lead, float gap)
            {
                _followLead = lead; _followGap = Mathf.Max(2f, gap);
                StateName = "follow";
            }

            public void StopFollow(string why)
            {
                if (_followLead == null) return;
                _followLead = null;
                if (_npc != null && !_npc.IsDestroyed) _npc.PostPos = _npc.transform.position;
                ReturnToPost(why);
            }

            private void FollowTick(float now)
            {
                if (_followLead == null || _followLead.IsDestroyed || _followLead.IsDead()) { StopFollow("lead gone"); return; }
                var lead = _followLead.transform.position;
                var me = _npc.transform.position;
                var d = Vector3.Distance(me, lead);
                if (d <= _followGap + 1f)
                {
                    if (StateName != "follow") { StateName = "follow"; SetRelaxed(true); }
                    if (now >= _nextPathAt) { _nextPathAt = now + 0.5f; Navigator.Stop(); }
                    var look = lead - _npc.eyes.position; look.y = 0f;
                    if (look.sqrMagnitude > 0.01f) _npc.SetAimDirection(look.normalized);
                    return;
                }
                StateName = "follow";
                if (now >= _nextPathAt)
                {
                    _nextPathAt = now + 0.5f;
                    var toward = lead - me; toward.y = 0f;
                    var hop = me + toward.normalized * Mathf.Min(8f, Mathf.Max(1f, d - _followGap));
                    Navigator.SetDestination(SnapToGround(hop), d > _followGap + 12f ? BaseNavigator.NavigationSpeed.Fast : BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                }
            }

            // The ambush hunt (decision 0010 §C): the post follows the target so the leash never
            // holds the squad back; a dropped engagement (timeout, no line of sight) is taken up
            // again at once, so the squad homes in on the target's live position until the plugin
            // stands it down. A target inside a safe zone or, unless EnterBases, inside a cupboard's
            // range is not approached: the squad holds at BaseStandoff from the cupboard.
            private BasePlayer _hunt;
            private float _huntStandoff = 40f;
            private bool _huntEnterBases;
            private float _huntHoldLoggedAt;
            private BuildingPrivlidge _privCache;
            private float _privCachedAt;
            public bool Hunting => _hunt != null;

            public void StartHunt(BasePlayer target, float standoff, bool enterBases)
            {
                _hunt = target; _huntStandoff = Mathf.Max(5f, standoff); _huntEnterBases = enterBases;
                StateName = "hunt";
            }

            public void StopHunt(string why)
            {
                if (_hunt == null) return;
                _hunt = null;
                _target = null;
                if (_npc != null && !_npc.IsDestroyed) _npc.PostPos = _npc.transform.position;
                ReturnToPost(why);
            }

            // True while the hunted target is somewhere the squad must not go; `holdAt` is where to stand.
            private bool HuntBlocked(BasePlayer target, out Vector3 holdAt, out string why)
            {
                holdAt = _npc.transform.position; why = null;
                if (target.InSafeZone()) { why = "safe zone"; return true; }
                if (_huntEnterBases) return false;
                // GetBuildingPrivilege is a physics overlap: once a second per hunter, not four times (review 2026-09-27).
                var nowP = UnityEngine.Time.realtimeSinceStartup;
                if (nowP - _privCachedAt > 1f) { _privCache = target.GetBuildingPrivilege(); _privCachedAt = nowP; }
                var priv = _privCache;
                if (priv == null || priv.IsDestroyed) return false;
                var from = _npc.transform.position - priv.transform.position; from.y = 0f;
                if (from.sqrMagnitude < 0.01f) from = -_npc.transform.forward;
                holdAt = SnapToGround(priv.transform.position + from.normalized * _huntStandoff);
                why = "cupboard range";
                return true;
            }

            private void HuntTick(float now)
            {
                if (_hunt == null || _hunt.IsDestroyed || _hunt.IsDead() || !_hunt.IsConnected) { StopHunt("target gone"); return; }
                _npc.PostPos = _hunt.transform.position;
                Vector3 holdAt; string why;
                if (HuntBlocked(_hunt, out holdAt, out why))
                {
                    var d = Vector3.Distance(_npc.transform.position, holdAt);
                    if (d > 3f && why == "cupboard range")
                    {
                        StateName = "hunt-hold";
                        if (now >= _nextPathAt) { _nextPathAt = now + 0.5f; var toward = holdAt - _npc.transform.position; toward.y = 0f; Navigator.SetDestination(SnapToGround(_npc.transform.position + toward.normalized * Mathf.Min(8f, d)), BaseNavigator.NavigationSpeed.Normal, 0f, 0f); }
                    }
                    else
                    {
                        if (StateName != "hunt-hold") { StateName = "hunt-hold"; Navigator.Stop(); }
                        var look = _hunt.eyes.position - _npc.eyes.position;
                        if (look.sqrMagnitude > 0.01f) _npc.SetAimDirection(look.normalized);
                    }
                    if (now - _huntHoldLoggedAt > 30f) { _huntHoldLoggedAt = now; GLog($"guard '{_npc.GuardName}' holds off {_hunt.displayName} ({why})."); }
                    return;
                }
                Engage(_hunt, "hunt"); // HostileTick takes it from here: chase (hops toward the live position), combat in range
            }

            private void Engage(BasePlayer target, string why)
            {
                if (target == null || target.IsDestroyed) return;
                // InitializeAI runs from Unity's Start(), the frame after Spawn: a ForceTarget in the
                // spawning frame (the response squad, live 2026-09-20 11:25) finds _npc null. Keep the
                // target and take it up the moment the brain is up.
                if (_npc == null) { _pendingTarget = target; _pendingWhy = why; return; }
                if (_npc.Passive) return; // the fence never fights (decision 0008 §1)
                var now = UnityEngine.Time.realtimeSinceStartup;
                if (_target == target) return;
                _target = target;
                _engageStartAt = now;
                _lastLosAt = now;
                _idle = false;
                StateName = "chase";
                SetRelaxed(false);
                GLog($"guard '{_npc.GuardName}' engaging {target.displayName} ({why}).");
            }

            private void MovementPump()
            {
                var now = UnityEngine.Time.realtimeSinceStartup;
                var delta = now - _lastMoveTick;
                _lastMoveTick = now;
                Navigator?.Think(delta);
                if (_npc == null || _npc.IsDestroyed) return;
                // Ground snap (FakeFriends 5A2): the Animal bake floats a few tenths above the surface.
                var p = _npc.ServerPosition;
                RaycastHit ground;
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.6f, Vector3.down, out ground, 4f, GroundMask))
                {
                    var dy = ground.point.y - p.y;
                    if (Mathf.Abs(dy) > 0.03f)
                    {
                        p.y = Mathf.Abs(dy) > 0.5f ? ground.point.y : Mathf.MoveTowards(p.y, ground.point.y, 1.5f * delta);
                        _npc.ServerPosition = p;
                    }
                }
            }

            public override void Think(float delta)
            {
                if (!ConVar.AI.think) return;
                // ShouldServerThink() gates on lastThinkTime + thinkRate, and only the base Think
                // stamps it. Without this line the brain thinks every server tick (~40 Hz observed
                // live 2026-09-07 with thinks≈39/s), not the intended 4 Hz.
                lastThinkTime = UnityEngine.Time.time;
                if (_npc == null || _npc.IsDestroyed || Navigator == null) return;
                var now = UnityEngine.Time.realtimeSinceStartup;

                // Self-idle (probe 1.3: custom-brain guards never go dormant). With no real player
                // within IdleRange there is nobody to sense or aim at; only the 4 Hz stamp runs.
                if (now >= _nextIdleCheckAt)
                {
                    _nextIdleCheckAt = now + 2f;
                    // A walking guard (a patrol, the 9.1 walker) never idles: the walk is the point (probe 2026-09-20: the walker stood still 1,700 m from the only player).
                    _idle = _target == null && _walk == null && _followLead == null && _hunt == null && !AnyRealPlayerWithin(_npc.transform.position, _npc.IdleRange);
                    if (_idle && StateName != "post") { StateName = "post"; Navigator.Stop(); SetRelaxed(true); }
                }
                if (_idle && _target == null) return;

                _npc.ThinkCount++;
                Senses.Update();

                if (_target != null && (_target.IsDestroyed || _target.IsDead() || _target.IsSleeping()))
                {
                    _target = null;
                    ReturnToPost("target gone");
                }
                if (_target == null) AcquireHostile();

                if (_target != null) { HostileTick(now); return; }
                if (_hunt != null) { HuntTick(now); return; }
                if (_walk != null) { WalkTick(now); return; }
                if (_followLead != null) { FollowTick(now); return; }
                PostTick(now);
            }

            private static bool AnyRealPlayerWithin(Vector3 pos, float range)
            {
                var r2 = range * range;
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !IsRealPlayer(p) || p.IsDead()) continue;
                    if ((p.transform.position - pos).sqrMagnitude <= r2) return true;
                }
                return false;
            }

            // A hostile-flagged real player inside range is engaged without being shot first —
            // the checkpoint enforcement model (probe 1.6b). On Alert the reach grows ×1.5 and
            // Suspect-or-worse bands count as hostile too (decision 0004 §4, Enemy alert).
            private void AcquireHostile()
            {
                if (_npc.Passive) return;
                var memory = Senses?.Memory?.All;
                if (memory == null) return;
                var alert = _npc.Checkpoint != null && _npc.Checkpoint.IsAlert;
                var reach = (_npc.CombatRange + 10f) * (alert ? 1.5f : 1f);
                BasePlayer best = null; var bestDist = float.MaxValue;
                foreach (var m in memory)
                {
                    var bp = m.Entity as BasePlayer;
                    if (!IsRealPlayer(bp) || bp.IsDestroyed || bp.IsDead() || bp.IsSleeping()) continue;
                    if (!bp.IsHostile() && !(alert && _instance != null && _instance.IsSuspectOrWorse((ulong)bp.userID))) continue;
                    if (_hunt != null && bp == _hunt) { Vector3 h; string w; if (HuntBlocked(bp, out h, out w)) continue; } // a blocked hunt target is not re-acquired every tick (review 2026-09-27)
                    var d = Vector3.Distance(_npc.transform.position, bp.transform.position);
                    if (d < bestDist) { bestDist = d; best = bp; }
                }
                if (best != null && bestDist <= reach) Engage(best, alert ? "alert" : "IsHostile() in range");
            }

            private void PostTick(float now)
            {
                var drift = Vector3.Distance(_npc.transform.position, _npc.PostPos);
                if (drift > 1.5f)
                {
                    if (StateName != "return") { StateName = "return"; SetRelaxed(true); }
                    if (now >= _nextPathAt)
                    {
                        _nextPathAt = now + 1f;
                        // No navmesh on this build (probe 2026-09-20): a miss must still send the guard home.
                        NavMeshHit hit;
                        var home = NavMesh.SamplePosition(_npc.PostPos, out hit, 6f, NavMesh.AllAreas) ? hit.position : _npc.PostPos;
                        Navigator.SetDestination(home, BaseNavigator.NavigationSpeed.Normal, 0f, 0f);
                    }
                    return;
                }
                if (StateName != "post")
                {
                    StateName = "post";
                    Navigator.Stop();
                    SetRelaxed(true);
                    if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                }
                // Face the encounter subject, else the nearest real player in aim range, else the post heading.
                BasePlayer watch = null; var watchDist = _npc.AimRange;
                if (Watch != null && !Watch.IsDestroyed && !Watch.IsDead() && Vector3.Distance(_npc.transform.position, Watch.transform.position) <= _npc.AimRange * 2f)
                    watch = Watch;
                else
                {
                    Watch = null;
                    var memory = Senses?.Memory?.All;
                    if (memory != null)
                    {
                        foreach (var m in memory)
                        {
                            var bp = m.Entity as BasePlayer;
                            if (!IsRealPlayer(bp) || bp.IsDestroyed || bp.IsDead()) continue;
                            var d = Vector3.Distance(_npc.transform.position, bp.transform.position);
                            if (d < watchDist) { watchDist = d; watch = bp; }
                        }
                    }
                }
                var dir = watch != null
                    ? (watch.eyes.position - _npc.eyes.position).normalized
                    : _npc.PostFacing;
                _npc.SetAimDirection(dir);
            }

            // Ambush hunters fan out: each one's chase point sits a few metres to the side of the
            // target so three do not stack on one spot (owner, 2026-09-27: "two of them stuck together").
            public Vector3 HuntLateral;

            private void HostileTick(float now)
            {
                var aimPoint = _target.eyes != null ? _target.eyes.position - Vector3.up * 0.15f : _target.CenterPoint();
                var dist = Vector3.Distance(_npc.transform.position, _target.transform.position);
                var visible = _npc.IsVisible(aimPoint);
                if (visible) _lastLosAt = now;
                if (_hunt != null && _target == _hunt)
                {
                    // The post follows the target every tick so the leash never binds a hunter, and a
                    // target inside a safe zone or a cupboard's range ends the chase at once (review
                    // 2026-09-27: the post froze at the engagement point; the blocked check sat too late).
                    _npc.PostPos = _target.transform.position;
                    Vector3 holdAtNow; string whyBlocked;
                    if (HuntBlocked(_target, out holdAtNow, out whyBlocked)) { _target = null; Navigator.Stop(); return; }
                }
                // A downed target is not finished off by a hunter: the squad closes to arm's reach and
                // the plugin makes the arrest (decision 0010 §D). With Arrest off, hunters fight on.
                if (_hunt != null && _target == _hunt && _target.IsWounded() && _instance != null && _instance._config.Ambush.Arrest)
                {
                    _lastLosAt = now; _engageStartAt = now;
                    if (dist > 2.5f)
                    {
                        StateName = "arrest";
                        if (now >= _nextPathAt) { _nextPathAt = now + 0.5f; var toward = _target.transform.position - _npc.transform.position; toward.y = 0f; Navigator.SetDestination(SnapToGround(_npc.transform.position + toward.normalized * Mathf.Min(8f, dist - 1.5f)), BaseNavigator.NavigationSpeed.Normal, 0f, 0f); }
                    }
                    else
                    {
                        if (StateName != "arrest") { StateName = "arrest"; Navigator.Stop(); }
                        _npc.SetAimDirection((aimPoint - _npc.eyes.position).normalized);
                    }
                    return;
                }
                if (now - _engageStartAt > EngageTimeout || now - _lastLosAt > NoLosTimeout)
                {
                    _target = null;
                    ReturnToPost("timeout");
                    return;
                }
                if (dist <= _npc.CombatRange && visible)
                {
                    StateName = "combat";
                    Navigator.Stop();
                    if (_npc.modelState != null && _npc.modelState.ducked != dist > 12f) SetDucked(dist > 12f);
                    _npc.SetAimDirection((aimPoint - _npc.eyes.position).normalized);
                    if (now >= _nextFireAt)
                    {
                        _nextFireAt = now + UnityEngine.Random.Range(0.4f, 0.9f);
                        _npc.EquipWeapon();
                        _npc.ShotTest(dist);
                    }
                }
                else
                {
                    // Leash: never chase further than Leash metres from the post (a gate stays manned).
                    var targetFromPost = Vector3.Distance(_target.transform.position, _npc.PostPos);
                    if (targetFromPost > _npc.Leash)
                    {
                        StateName = "hold";
                        Navigator.Stop();
                        if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                        _npc.SetAimDirection((aimPoint - _npc.eyes.position).normalized);
                        return;
                    }
                    StateName = "chase";
                    if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                    if (now >= _nextPathAt)
                    {
                        _nextPathAt = now + 1f;
                        Navigator.SetDestination(_target.transform.position + (_hunt != null ? HuntLateral : Vector3.zero), BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                    }
                }
            }

            private void ReturnToPost(string why)
            {
                StateName = "return";
                // A chase leaves the walk's stall clock untouched; refresh it so the resumed walk
                // does not skip a point for time spent fighting (review 2026-09-27).
                if (_walk != null) { _walkLastProgressAt = UnityEngine.Time.realtimeSinceStartup; _walkBestDist = float.MaxValue; }
                if (_npc.modelState != null && _npc.modelState.ducked) SetDucked(false);
                _nextPathAt = 0f;
                GLog($"guard '{_npc.GuardName}' returning to post ({why}).");
            }

            private void SetDucked(bool v)
            {
                if (_npc == null || _npc.IsDestroyed || _npc.modelState == null) return;
                _npc.modelState.ducked = v;
                _npc.SendNetworkUpdate();
            }

            private void SetRelaxed(bool v)
            {
                if (_relaxed == v || _npc == null || _npc.IsDestroyed) return;
                _relaxed = v;
                _npc.SetPlayerFlag(BasePlayer.PlayerFlags.Relaxed, v);
            }

            public string Describe() =>
                $"state={StateName}{(_idle ? "(idle)" : "")} target={(_target != null ? _target.displayName : "-")} surface={Surface}";
        }

        // ROOT CAUSE of "guard takes no player damage" (probe 1.2/1.6, 2026-09-07): the component
        // swap (AddComponent<GuardNpc> + DestroyImmediate(proto)) creates a fresh component whose
        // prefab-serialized fields are all default. With `skeletonProperties` null,
        // BaseCombatEntity.SkeletonLookup returns HitArea -1, and BasePlayer.OnProjectileAttack
        // rejects every player bullet as "Bone is invalid" before OnAttacked. The same swap also
        // loses model, protection, the prefab loadout, loot slots, death/chatter effects, corpse
        // prefab and collider dimensions. Copy them all — explicit list, no reflection.
        private static void CopyPrefabFields(ScientistNPC src, ScientistNPC dst)
        {
            // BaseEntity
            dst.prefabID = src.prefabID;
            dst.bounds = src.bounds;
            dst.model = src.model;
            dst.impactEffect = src.impactEffect;
            // BaseCombatEntity
            dst.skeletonProperties = src.skeletonProperties;
            dst.baseProtection = src.baseProtection;
            dst.propDirection = src.propDirection;
            dst.deployableCorpsePrefab = src.deployableCorpsePrefab;
            dst.spawnDeployableCorpseOnDeath = src.spawnDeployableCorpseOnDeath;
            dst.faction = src.faction;
            dst.startHealth = src.startHealth;
            dst.sendsHitNotification = src.sendsHitNotification;
            dst.sendsMeleeHitNotification = src.sendsMeleeHitNotification;
            dst.markAttackerHostile = src.markAttackerHostile;
            dst.ShowHealthInfo = src.ShowHealthInfo;
            // BasePlayer
            dst.playerColliderStanding = src.playerColliderStanding;
            dst.playerColliderDucked = src.playerColliderDucked;
            dst.playerColliderCrawling = src.playerColliderCrawling;
            dst.playerColliderLyingDown = src.playerColliderLyingDown;
            dst.playerRigidbody = src.playerRigidbody;
            dst.cachedProtection = src.cachedProtection;
            dst.fallDamageEffect = src.fallDamageEffect;
            dst.drownEffect = src.drownEffect;
            dst.DeathIconOverride = src.DeathIconOverride;
            // NPCPlayer
            dst.loadouts = src.loadouts;
            dst.NavAgent = src.NavAgent;
            dst.MovementTickStartDelay = src.MovementTickStartDelay;
            dst.damageScale = src.damageScale;
            dst.shortRange = src.shortRange;
            dst.attackLengthMaxShortRangeScale = src.attackLengthMaxShortRangeScale;
            // HumanNPC
            dst.LootSpawnSlots = src.LootSpawnSlots;
            dst.AdditionalLosBlockingLayer = src.AdditionalLosBlockingLayer;
            dst.aimConeScale = src.aimConeScale;
            // ScientistNPC
            dst.RadioChatterEffects = src.RadioChatterEffects;
            dst.DeathEffects = src.DeathEffects;
            dst.IdleChatterRepeatRange = src.IdleChatterRepeatRange;
            dst.radioChatterType = src.radioChatterType;
            dst.deathStatName = src.deathStatName;
        }

        // Debug guards from `papers.cp guard` belong to no checkpoint; tracked so Unload removes them.
        private readonly List<GuardNpc> _looseGuards = new List<GuardNpc>();

        private GuardNpc SpawnGuard(Vector3 pos, Vector3 facing, bool heavy, Checkpoint owner, bool passive = false, string name = null)
        {
            var prefab = heavy ? HeavyPrefab : RoamPrefab;
            var proto = GameManager.server.CreateEntity(prefab, pos, Quaternion.identity, startActive: false) as ScientistNPC;
            if (proto == null) { CLog($"[guard] CreateEntity returned null for {prefab}"); return null; }
            var protoBrain = proto.GetComponent<ScientistBrain>();
            var npc = proto.gameObject.AddComponent<GuardNpc>();
            proto.gameObject.AddComponent<GuardBrain>();
            CopyPrefabFields(proto, npc);      // every prefab-serialized field the swap drops (public, no reflection)
            UnityEngine.Object.DestroyImmediate(proto, true);
            UnityEngine.Object.DestroyImmediate(protoBrain, true);
            npc.enableSaving = false;
            npc.LegacyNavigation = false;      // gates Navigator.Init (FakeFriends recipe)
            npc.syncPosition = true;           // gates movement + client sync
            npc.PostPos = pos;
            npc.PostFacing = facing;
            npc.Checkpoint = owner;
            npc.GuardName = !string.IsNullOrEmpty(name) ? name : (heavy ? "Cobalt Heavy" : "Cobalt Guard");
            npc.Passive = passive;
            npc.AimRange = _config.Checkpoints.AimRange;
            npc.CombatRange = _config.Checkpoints.CombatRange;
            npc.Leash = _config.Checkpoints.Leash;
            npc.IdleRange = _config.Checkpoints.IdleRange;
            var hp = npc.startHealth > 0f ? npc.startHealth : (heavy ? 250f : 120f);
            npc.startHealth = hp;              // HumanNPC.MaxHealth() returns this field
            npc.gameObject.AwakeFromInstantiate();
            npc.Spawn();
            npc.InitializeHealth(hp, hp);
            // With `loadouts` copied the prefab kit equips itself; arm explicitly only if it did not.
            if (npc.inventory?.containerBelt != null && npc.inventory.containerBelt.itemList.Count == 0)
            {
                var rifle = ItemManager.CreateByName(heavy ? "lmg.m249" : "rifle.ak", 1);
                if (rifle != null && !rifle.MoveToContainer(npc.inventory.containerBelt)) rifle.Remove();
                CLog($"[guard] prefab loadout did not equip; armed '{npc.GuardName}' by hand.");
            }
            // Live 2026-09-08: the copied loadout filled the belt but the guards stood naked.
            // Dress them in the scientist suit when the wear container came up empty.
            var wear = npc.inventory?.containerWear;
            if (wear != null && wear.itemList.Count == 0)
            {
                var suit = ItemManager.CreateByName(heavy ? "heavy.plate.jacket" : "hazmatsuit_scientist", 1);
                if (suit != null && !suit.MoveToContainer(wear)) suit.Remove();
                if (heavy)
                {
                    var pants = ItemManager.CreateByName("heavy.plate.pants", 1);
                    if (pants != null && !pants.MoveToContainer(wear)) pants.Remove();
                }
                npc.SendNetworkUpdate();
            }
            npc.SetAimDirection(facing);
            if (owner == null && !passive) _looseGuards.Add(npc); // a fence is tracked by its Fence, not as a loose guard
            return npc;
        }

        private static void DespawnGuard(GuardNpc g)
        {
            if (g == null) return;
            g.GetComponent<GuardBrain>()?.StopPump();
            try { if (!g.IsDestroyed) g.Kill(); }
            catch { /* ResetState→LookupPrefab NRE on swapped scientists (FakeFriends probe caveat) */ }
            if (g != null && g.gameObject != null) UnityEngine.Object.DestroyImmediate(g.gameObject);
        }

        private int LiveGuardCount()
        {
            var n = 0;
            foreach (var cp in _checkpoints.Values) { if (cp.Kind == CheckpointKind.Patrol) continue; n += cp.LiveGuards(); } // patrols have their own cap (decision 0010 §A)
            foreach (var g in _looseGuards) if (g != null && !g.IsDestroyed) n++;
            return n;
        }

        private int PatrolGuardsLive()
        {
            var n = 0;
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Patrol) n += cp.LiveGuards();
            return n;
        }

        private bool IsSuspectOrWorse(ulong id) => !IsExemptId(id) && BandOf(ReadScore(id)) >= Band.Suspect;

        #endregion

        #region Checkpoints (templates, placement, lifecycle — decision 0004)

        public enum CheckpointState { Unspawned, Active, Destroyed, OverCap, BelowTier }
        public enum CheckpointKind { Gate, Roadblock, Vault, Patrol }

        private readonly Dictionary<string, Checkpoint> _checkpoints = new Dictionary<string, Checkpoint>(StringComparer.OrdinalIgnoreCase);
        private Timer _tierTimer;

        // Persisted per vault instance: when a breached door and an emptied vault come back (survives restarts).
        public class VaultState { public DateTime DoorRebuildAtUtc; public DateTime RestockAtUtc; }

        private class CheckpointData
        {
            public Dictionary<string, DateTime> DestroyedUntil = new Dictionary<string, DateTime>();
            // Milestone 8: a breached door and an emptied vault come back on timers that survive a restart.
            public Dictionary<string, VaultState> Vaults = new Dictionary<string, VaultState>();
            // The Outpost safe zone's stock radius, remembered the first time the plugin sees it so
            // a load that finds the zone already shrunk (a build that died mid-curfew) restores it.
            public float OutpostZoneRadius;
        }
        private CheckpointData _cpData = new CheckpointData();
        private bool _cpDirty;

        private void LoadCheckpointData()
        {
            try { _cpData = Interface.Oxide.DataFileSystem.ReadObject<CheckpointData>(CheckpointDataFile) ?? new CheckpointData(); }
            catch (Exception ex) { PrintWarning($"Checkpoint data unreadable ({ex.Message}); starting empty."); _cpData = new CheckpointData(); }
            if (_cpData.DestroyedUntil == null) _cpData.DestroyedUntil = new Dictionary<string, DateTime>();
            if (_cpData.Vaults == null) _cpData.Vaults = new Dictionary<string, VaultState>();
        }

        private void SaveCheckpointData(bool force)
        {
            if (!_cpDirty && !force) return;
            try { Interface.Oxide.DataFileSystem.WriteObject(CheckpointDataFile, _cpData); _cpDirty = false; }
            catch (Exception ex) { PrintError($"Checkpoint save failed: {ex.Message}"); }
        }

        private static bool TryParseVector(string text, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(text)) return false;
            var parts = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            float x, y, z;
            if (parts.Length != 3 || !float.TryParse(parts[0], out x) || !float.TryParse(parts[1], out y) || !float.TryParse(parts[2], out z)) return false;
            result = new Vector3(x, y, z);
            return true;
        }

        private static string VecText(Vector3 v) => $"{v.x:0.###} {v.y:0.###} {v.z:0.###}";

        private static string MonumentShortName(MonumentInfo m)
        {
            var n = m.name ?? "";
            var slash = n.LastIndexOf('/');
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".prefab")) n = n.Substring(0, n.Length - 7);
            return n;
        }

        private static List<MonumentInfo> MonumentsNamed(string shortName)
        {
            var list = new List<MonumentInfo>();
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null || string.IsNullOrEmpty(shortName)) return list;
            foreach (var m in monuments)
                if (m != null && string.Equals(MonumentShortName(m), shortName, StringComparison.OrdinalIgnoreCase)) list.Add(m);
            return list;
        }

        // Which monument owns a spot: the smallest monument whose bounds contain it, else the one
        // with the nearest origin. Nearest-origin alone attached an Airfield road gate to a small
        // power substation 66 m away (seven of them on the map — seven gates; live 2026-09-08).
        private static bool HasBounds(MonumentInfo m) => m.Bounds.size.sqrMagnitude > 1f;

        // Distance from a world point to the monument's bounds box (0 inside). Monuments that
        // report no bounds (substations and other small roadside prefabs, live 2026-09-08) fall
        // back to origin distance plus a penalty, so a big monument nearby always wins.
        private static float MonumentScore(MonumentInfo m, Vector3 pos)
        {
            if (!HasBounds(m)) return Vector3.Distance(m.transform.position, pos) + 150f;
            var local = m.transform.InverseTransformPoint(pos);
            return Mathf.Sqrt(m.Bounds.SqrDistance(local));
        }

        // True when the point is inside the monument: its bounds box, or a sphere of `radius`
        // around its origin when it reports no bounds.
        private static bool InMonument(MonumentInfo m, Vector3 pos, float radius) =>
            HasBounds(m) ? m.IsInBounds(pos) : Vector3.Distance(m.transform.position, pos) <= radius;

        private static MonumentInfo NearestMonument(Vector3 pos, string needle, out float dist, out string alternatives)
        {
            MonumentInfo best = null; dist = float.MaxValue; alternatives = "";
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return null;
            var bestScore = float.MaxValue;
            var near = new List<KeyValuePair<float, MonumentInfo>>();
            foreach (var m in monuments)
            {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(needle) && MonumentShortName(m).IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var score = MonumentScore(m, pos);
                if (score < 600f) near.Add(new KeyValuePair<float, MonumentInfo>(score, m));
                if (score < bestScore) { bestScore = score; best = m; }
            }
            if (best != null) dist = Vector3.Distance(best.transform.position, pos);
            near.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder();
            for (var i = 0; i < near.Count && i < 5; i++)
            {
                if (i > 0) sb.Append(", ");
                var m = near[i].Value;
                sb.Append(MonumentShortName(m)).Append(' ').Append(Vector3.Distance(m.transform.position, pos).ToString("0")).Append(" m");
                sb.Append(HasBounds(m) ? (m.IsInBounds(pos) ? " (inside its bounds)" : $" ({near[i].Key:0} m from its bounds)") : " (no bounds)");
            }
            alternatives = sb.ToString();
            return best;
        }

        // Monument prefabs are identical on every seed, so a pose captured relative to the
        // monument transform transfers between maps (the Island Taxi poster pattern).
        private static void WorldToMonument(MonumentInfo m, Vector3 worldPos, float worldYaw, out Vector3 local, out float localYaw)
        {
            local = Quaternion.Inverse(m.transform.rotation) * (worldPos - m.transform.position);
            localYaw = Mathf.Repeat(worldYaw - m.transform.eulerAngles.y, 360f);
        }

        private static void MonumentToWorld(MonumentInfo m, Vector3 local, float localYaw, out Vector3 worldPos, out Quaternion worldRot)
        {
            worldPos = m.transform.rotation * local + m.transform.position;
            worldRot = Quaternion.Euler(0f, m.transform.eulerAngles.y + localYaw, 0f);
        }

        private static Vector3 SnapToGround(Vector3 p)
        {
            RaycastHit hit;
            if (UnityEngine.Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out hit, 12f, GroundMask)) return hit.point;
            p.y = TerrainMeta.HeightMap.GetHeight(p);
            return p;
        }

        private static float YawOf(Vector3 forward)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) return 0f;
            return Quaternion.LookRotation(forward.normalized).eulerAngles.y;
        }

        // Full protection for every damage type except Explosion (Scale clamps each entry to
        // [-1, 1], so 1 = no damage, 0 = vanilla). Built once, on first use.
        private static ProtectionProperties _chicaneProtection;
        private static ProtectionProperties ChicaneProtection
        {
            get
            {
                if (_chicaneProtection == null)
                {
                    _chicaneProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
                    _chicaneProtection.Add(1f);
                    _chicaneProtection.Add(global::Rust.DamageType.Explosion, -1f);
                }
                return _chicaneProtection;
            }
        }

        private static string PropPrefab(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "light": return SearchLightPrefab;
                case "turret": return SentryPrefab;
                case "siren": return SirenPrefab;
                case "flood": return FloodPrefab;
                case "concrete": case "sandbags": case "metal": case "stone": case "cover.wood":
                    return $"assets/prefabs/deployable/barricades/barricade.{kind.ToLowerInvariant()}.prefab";
                default: return null;
            }
        }

        public class CheckpointTrigger : TriggerBase
        {
            public Checkpoint Owner;
        }

        // One placed gate at one monument instance: pose, trigger, guards, props, state.
        public class RemovedDoor { public string Prefab; public Vector3 Position; public Quaternion Rotation; }

        public class Checkpoint
        {
            public string Name;
            public CheckpointKind Kind = CheckpointKind.Gate;
            public GateTemplate Template;
            public MonumentInfo Monument;
            public RoadSample Road;          // roadblocks only
            public bool Pinned;              // admin-forced roadblock: never trimmed or relocated
            public float PlacedAt;
            public Vector3 Position;
            public Quaternion Rotation;
            public CheckpointState State = CheckpointState.Unspawned;
            public string StateNote = "";
            public DateTime DestroyedUntilUtc;
            public float AlertUntil;
            public bool Curfew;              // Outpost gates during the night curfew: Suspect+ hostile (decision 0007 §3)
            public bool InSafeZone;
            public string Surface = "?";
            public readonly List<GuardNpc> Guards = new List<GuardNpc>();
            public readonly List<BaseEntity> Props = new List<BaseEntity>();
            // Prop kind per spawned entity ("siren", "flood", …): the light rules key on the kind
            // we asked for, not on the entity's class (the simplelight prefab is not a SimpleLight —
            // self-test 2026-09-10).
            public readonly Dictionary<BaseEntity, string> PropKinds = new Dictionary<BaseEntity, string>();
            public GameObject TriggerGo;
            public GameObject ApproachGo;
            // Milestone 9 patrols (decision 0010 §A): the road route walked back and forth, the point
            // the lead is heading for, the direction along the route, and who leads (net id).
            public List<Vector3> Route;
            public int RouteDir = 1;
            public ulong PatrolLead;
            public float ShiftStartedAt;     // the PatrolMinutes clock: set when the slot is born or returns, not by a relocation
            public readonly Dictionary<ulong, float> WarnedUntil = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, float> RunOverUntil = new Dictionary<ulong, float>();
            public SearchLight Light;
            public Timer RespawnTimer;
            // Milestone 8 (decision 0009): a vault is a checkpoint with a door instead of a trigger.
            public VaultTemplate Vault;
            public Door Door;
            public CodeLock Lock;
            public readonly List<LootContainer> Crates = new List<LootContainer>();
            public readonly List<RemovedDoor> RemovedDoors = new List<RemovedDoor>(); // the monument's own door(s), put back on despawn
            public DateTime DoorRebuildAtUtc, RestockAtUtc;
            public Timer DoorTimer, RestockTimer;
            public bool Despawning;          // our own Kill() of the props must not read as a breach or an emptied vault
            // Per-player state lives here (encounters in Phase B).
            public readonly Dictionary<ulong, float> CooldownUntil = new Dictionary<ulong, float>();

            public Vector3 Forward => Rotation * Vector3.forward;
            public Vector3 Right => Rotation * Vector3.right;
            public bool IsAlert => Curfew || AlertUntil > UnityEngine.Time.realtimeSinceStartup;
            public bool IsActive => State == CheckpointState.Active;

            public int LiveGuards()
            {
                var n = 0;
                foreach (var g in Guards) if (g != null && !g.IsDestroyed) n++;
                return n;
            }

            public int SpawnedGuards;
            public int SpawnedTier;

            public int GuardCount(CheckpointConfig cfg, int tier)
            {
                if (Template.Guards > 0) return Template.Guards;
                var byTier = cfg.GuardsByTier;
                if (byTier != null && byTier.Length > 0) return Mathf.Clamp(byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1], 1, 8);
                return cfg.GuardsPerGate;
            }
            public float Radius(CheckpointConfig cfg) => Template.TriggerRadius > 0 ? Template.TriggerRadius : cfg.TriggerRadius;
        }

        // Resolve every template against the map's monuments into checkpoint instances.
        private List<Checkpoint> ResolveTemplate(GateTemplate t)
        {
            var result = new List<Checkpoint>();
            Vector3 local;
            if (t == null || string.IsNullOrEmpty(t.Name) || !TryParseVector(t.LocalPosition, out local)) return result;
            var monuments = MonumentsNamed(t.Monument);
            for (var i = 0; i < monuments.Count; i++)
            {
                Vector3 pos; Quaternion rot;
                MonumentToWorld(monuments[i], local, t.LocalYaw, out pos, out rot);
                result.Add(new Checkpoint
                {
                    Name = monuments.Count == 1 ? t.Name : $"{t.Name}#{i + 1}",
                    Template = t,
                    Monument = monuments[i],
                    Position = SnapToGround(pos),
                    Rotation = rot,
                });
            }
            if (monuments.Count == 0) CLog($"[cp] gate '{t.Name}': no monument named '{t.Monument}' on this map.");
            return result;
        }

        private void SpawnAllCheckpoints()
        {
            foreach (var t in _config.Gates)
                foreach (var cp in ResolveTemplate(t))
                {
                    if (_checkpoints.ContainsKey(cp.Name)) { CLog($"[cp] duplicate checkpoint name '{cp.Name}' skipped."); continue; }
                    _checkpoints[cp.Name] = cp;
                    DateTime until;
                    if (_cpData.DestroyedUntil.TryGetValue(cp.Name, out until) && until > DateTime.UtcNow)
                    {
                        cp.DestroyedUntilUtc = until;
                        cp.State = CheckpointState.Destroyed;
                        cp.StateNote = $"destroyed until {until:HH:mm} UTC";
                        ScheduleRespawn(cp);
                        continue;
                    }
                    TrySpawn(cp);
                }
            if (_config.Vaults.Enabled)
                foreach (var v in _config.Vaults.Vaults)
                    foreach (var cp in ResolveVault(v))
                    {
                        if (_checkpoints.ContainsKey(cp.Name)) { CLog($"[vault] duplicate checkpoint name '{cp.Name}' skipped."); continue; }
                        _checkpoints[cp.Name] = cp;
                        TrySpawn(cp);
                    }
            FillRoadblocks(CurrentTier());
            FillPatrols(CurrentTier());
            ReconcileSabotage();
        }

        private void DespawnAllCheckpoints(string why)
        {
            foreach (var cp in _checkpoints.Values) Despawn(cp, why);
            _checkpoints.Clear();
        }

        // Unspawned gates re-check the tier and the guard cap periodically (and on demand).
        private void ReconcileCheckpoints()
        {
            var tier = CurrentTier();
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.State == CheckpointState.Active && cp.Template.MinTier > tier)
                {
                    Despawn(cp, "below tier");
                    cp.State = CheckpointState.BelowTier; cp.StateNote = $"needs tier {cp.Template.MinTier}";
                    CLog($"[cp] '{cp.Name}' stood down: tier {tier} < {cp.Template.MinTier}.");
                }
            }
            TrimRoadblocks(tier);
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.State != CheckpointState.Active || cp.SpawnedTier == tier) continue;
                var want = GuardsFor(cp, tier);
                var heavyChange = (cp.SpawnedTier >= _config.Checkpoints.HeavyFromTier) != (tier >= _config.Checkpoints.HeavyFromTier) && want > 2;
                var propChange = PropsAtTier(cp.Template, cp.SpawnedTier) != PropsAtTier(cp.Template, tier);
                if (want == cp.SpawnedGuards && !heavyChange && !propChange) { cp.SpawnedTier = tier; continue; }
                Despawn(cp, "tier changed");
                CLog($"[cp] '{cp.Name}' re-manned for tier {tier}: {cp.SpawnedGuards} → {want} guard(s), {PropsAtTier(cp.Template, tier)} of {cp.Template.Props.Count} prop(s).");
                TrySpawn(cp);
            }
            foreach (var cp in _checkpoints.Values)
                // Gates retry below-tier and over-cap; a roadblock refused by the cap retries too,
                // or its slot stays dead until the tier changes (review 2026-09-12).
                // A vault retries like a gate (self-test 2026-09-20 12:15: 'outpost-vault' stood down at
                // tier 3 and stayed "BelowTier" at tier 5 until a reload — the tool went off sale with it).
                if ((cp.Kind != CheckpointKind.Roadblock && (cp.State == CheckpointState.BelowTier || cp.State == CheckpointState.OverCap))
                    || (cp.Kind == CheckpointKind.Roadblock && cp.State == CheckpointState.OverCap && cp.Road != null)) TrySpawn(cp);
            FillRoadblocks(tier);
            RelocateDueRoadblocks();
            TrimPatrols(tier);
            FillPatrols(tier);
            RelocateDuePatrols();
            OutpostTick(true); // a tier change can start or end the curfew
            ReconcileSabotage();
            var now = Time.realtimeSinceStartup;
            foreach (var cp in _checkpoints.Values) { PruneExpired(cp.CooldownUntil, now); PruneExpired(cp.RunOverUntil, now); PruneExpired(cp.WarnedUntil, now); }
            foreach (var per in _perimeters) { PruneExpired(per.ClearedUntil, now); PruneExpired(per.VerdictCooldownUntil, now); }
            PruneExpired(_hostileUntil, now);
        }

        private static void PruneExpired(Dictionary<ulong, float> map, float now)
        {
            if (map.Count == 0) return;
            var dead = new List<ulong>();
            foreach (var kv in map) if (kv.Value <= now) dead.Add(kv.Key);
            foreach (var k in dead) map.Remove(k);
        }

        private bool TrySpawn(Checkpoint cp)
        {
            var cfg = _config.Checkpoints;
            if (cp.State == CheckpointState.Active) return true;
            if (cp.Template.MinTier > CurrentTier())
            {
                cp.State = CheckpointState.BelowTier; cp.StateNote = $"needs tier {cp.Template.MinTier}";
                return false;
            }
            var need = GuardsFor(cp, CurrentTier());
            if (cp.Kind == CheckpointKind.Patrol)
            {
                // Patrols count under their own cap so tier 5 still has them (decision 0010 §A).
                var pcap = Mathf.Max(0, _config.Patrols.PatrolGuardCap);
                if (PatrolGuardsLive() + need > pcap)
                {
                    cp.State = CheckpointState.OverCap; cp.StateNote = $"patrol guard cap {pcap} reached";
                    CLog($"[patrol] '{cp.Name}' not spawned: {cp.StateNote}.");
                    return false;
                }
                Spawn(cp);
                return true;
            }
            if (LiveGuardCount() + need > cfg.GuardCap)
            {
                cp.State = CheckpointState.OverCap; cp.StateNote = $"guard cap {cfg.GuardCap} reached";
                CLog($"[cp] '{cp.Name}' not spawned: {cp.StateNote}.");
                return false;
            }
            Spawn(cp);
            return true;
        }

        private void Spawn(Checkpoint cp)
        {
            var cfg = _config.Checkpoints;
            var t = cp.Template;
            var flank = cp.Kind == CheckpointKind.Roadblock && cp.Road != null ? cp.Road.Width / 2f + 1.5f : cfg.GuardFlank;
            if (cp.Kind == CheckpointKind.Vault) { SpawnVault(cp); return; }
            var tier = CurrentTier();
            var count = cp.GuardCount(cfg, tier);
            cp.PlacedAt = Time.realtimeSinceStartup;
            cp.SpawnedGuards = count;
            cp.SpawnedTier = tier;
            // Guards stand beside the lane, alternating right/left, facing the approach. From
            // HeavyFromTier every guard beyond the first two is a heavy (decision 0005 §5).
            for (var i = 0; i < count; i++)
            {
                var side = (i % 2 == 0) ? 1f : -1f;
                var lateral = flank * side * (1 + i / 2);
                var facing = cp.Forward;
                var gp = cp.Position + cp.Right * lateral;
                if (cp.Kind == CheckpointKind.Roadblock && i >= 2)
                {
                    // Roadblock: the third guard stands 6 m along on the right and covers the other
                    // way; any further guards step 3 m along and alternate sides (review 2026-09-12).
                    var extra = i - 2;
                    gp = cp.Position + cp.Right * flank * (extra % 2 == 0 ? 1f : -1f) + cp.Forward * (6f + 3f * extra);
                    facing = -cp.Forward;
                }
                gp = SnapToGround(gp);
                var heavy = t.Heavy || (i >= 2 && tier >= cfg.HeavyFromTier);
                var g = SpawnGuard(gp, facing, heavy, cp);
                if (g == null) continue;
                cp.Guards.Add(g);
                var brain = g.GetComponent<GuardBrain>();
                if (brain != null) cp.Surface = brain.Surface;
            }
            // Trigger: a metre up so a standing player's collider is inside; keyed on the player entity.
            var go = new GameObject($"PapersPleaseGate:{cp.Name}");
            go.layer = (int)global::Rust.Layer.Trigger;
            go.transform.position = cp.Position + Vector3.up;
            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = cp.Radius(cfg);
            var trig = go.AddComponent<CheckpointTrigger>();
            trig.Owner = cp;
            trig.InterestLayers = global::Rust.Layers.Mask.Player_Server;
            trig.OnEntityEnterTrigger = e => OnGateEnter(cp, e as BaseEntity);
            trig.OnEntityLeaveTrigger = e => OnGateLeave(cp, e as BaseEntity);
            cp.TriggerGo = go;
            if (cp.Kind == CheckpointKind.Roadblock || cp.Kind == CheckpointKind.Patrol)
            {
                // Approach sphere on the vehicle layers: the STOP prompt (decision 0006 §5).
                var ago = new GameObject($"PapersPleaseApproach:{cp.Name}");
                ago.layer = (int)global::Rust.Layer.Trigger;
                ago.transform.position = cp.Position + Vector3.up;
                var acol = ago.AddComponent<SphereCollider>();
                acol.isTrigger = true;
                acol.radius = Mathf.Max(cp.Radius(cfg) + 5f, _config.Roadblocks.ApproachRadius);
                var atrig = ago.AddComponent<CheckpointTrigger>();
                atrig.Owner = cp;
                atrig.InterestLayers = global::Rust.Layers.Mask.Vehicle_Large | global::Rust.Layers.Mask.Vehicle_Detailed | global::Rust.Layers.Mask.Vehicle_World;
                atrig.OnEntityEnterTrigger = e => OnApproachEnter(cp, e as BaseEntity);
                cp.ApproachGo = ago;
            }
            // Props; those with a MinTier above the clock wait for the tier reconcile (decision 0007 §5).
            foreach (var p in t.Props)
            {
                Vector3 off;
                if (p.MinTier > tier) continue;
                if (!TryParseVector(p.Offset, out off)) continue;
                var pos = SnapToGround(cp.Position + cp.Rotation * off);
                SpawnProp(cp, p.Kind, pos, cp.Rotation * Quaternion.Euler(0f, p.Yaw, 0f));
            }
            if (cp.Kind == CheckpointKind.Roadblock && cp.Road != null)
            {
                // Chicane: each barricade blocks half the lane, 4 m apart, so cars must slow but can pass.
                var half = Mathf.Max(1.5f, cp.Road.Width / 4f);
                SpawnProp(cp, "concrete", SnapToGround(cp.Position - cp.Forward * 4f - cp.Right * half), cp.Rotation);
                SpawnProp(cp, "concrete", SnapToGround(cp.Position + cp.Forward * 4f + cp.Right * half), cp.Rotation);
            }
            if (t.Searchlight)
                SpawnProp(cp, "light", SnapToGround(cp.Position + cp.Right * (flank + 1.5f)), cp.Rotation);
            cp.State = CheckpointState.Active;
            cp.StateNote = "";
            cp.DestroyedUntilUtc = default(DateTime);
            cp.Curfew = _curfewOn && cp.Kind == CheckpointKind.Gate && cp.Monument == _outpost && _config.Outpost.CurfewSuspectHostile;
            if (cp.Kind == CheckpointKind.Patrol) StartPatrolWalk(cp, "spawned");
            if (_cpData.DestroyedUntil.Remove(cp.Name)) _cpDirty = true;
            // InSafeZone reads trigger membership, which physics fills in a beat after spawn (probe 1.6).
            timer.Once(1f, () =>
            {
                if (cp.Guards.Count == 0 || cp.Guards[0] == null || cp.Guards[0].IsDestroyed) return;
                cp.InSafeZone = cp.Guards[0].InSafeZone();
                CLog($"[cp] '{cp.Name}' at {V(cp.Position)}: {cp.LiveGuards()} guard(s), surface '{SurfaceOf(cp)}', InSafeZone={cp.InSafeZone} ({(cp.InSafeZone ? "execution zone: guards immune, players disarmed" : "fair fight")}).");
            });
        }

        private BaseEntity SpawnProp(Checkpoint cp, string kind, Vector3 pos, Quaternion rot)
        {
            var prefab = PropPrefab(kind);
            if (prefab == null) { CLog($"[cp] '{cp.Name}': unknown prop kind '{kind}'."); return null; }
            if (prefab == SentryPrefab && LiveTurretCount() >= _config.Outpost.TurretCap)
            {
                CLog($"[cp] '{cp.Name}': turret not spawned, turret cap {_config.Outpost.TurretCap} reached.");
                return null;
            }
            var ent = GameManager.server.CreateEntity(prefab, pos, rot);
            if (ent == null) { CLog($"[cp] '{cp.Name}': CreateEntity returned null for {prefab}."); return null; }
            ent.enableSaving = false;
            var lowerKind = kind.ToLowerInvariant();
            // Every deployable prop carries GroundWatch + DestroyOnGroundMissing: the next physics
            // change beside it (a guard or turret spawning, a car shoving past, a corpse) re-checks
            // for support and kills anything the check cannot see — wall lights on terrain
            // (self-test 2026-09-10), a searchlight on a river bank and a chicane barricade beside
            // a bumped guard (live 2026-09-12). The plugin owns their lifetime.
            foreach (var c in ent.GetComponents<DestroyOnGroundMissing>()) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in ent.GetComponents<GroundWatch>()) UnityEngine.Object.DestroyImmediate(c);
            var barricade = ent as Barricade;
            // Barricade.ServerInit hangs an NPCBarricadeTriggerBox on every barricade that
            // "canNpcSmash": any NPC entering it gibs the barricade so scientists can path
            // through doors. Our guards are NPCs — each chase or return to post through the
            // chicane cost a barricade (live 2026-09-12, three builds in a row). No box.
            if (barricade != null) barricade.canNpcSmash = false;
            ent.Spawn();
            if (barricade != null)
            {
                if (barricade.NpcTriggerBox != null) UnityEngine.Object.Destroy(barricade.NpcTriggerBox.gameObject);
                // A car's hurt trigger (damage × speed per tick) and DecayEntity rot would take
                // the rest. The plugin owns the chicane's lifetime: immune to everything but
                // explosives. Set after Spawn — ResetState copies the prefab's protection back.
                barricade.baseProtection = ChicaneProtection;
            }
            cp.Props.Add(ent);
            cp.PropKinds[ent] = lowerKind;
            var turret = ent as NPCAutoTurret;
            if (turret != null) CLog($"[cp] '{cp.Name}': sentry turret at {V(pos)} online={turret.IsOnline()} peacekeeper={turret.PeacekeeperMode()} range={turret.sightRange:0}.");
            var io = turret == null ? ent as IOEntity : null; // the sentry is online on its own; keep it off the lamp path (review 2026-09-12)
            if (io != null)
            {
                // Plugin lights need no generator: fake the power input, then switch on (probe 1.10).
                // Sirens are lit only while the gate is on alert or under curfew, floods only at night.
                SetLit(io, WantLit(cp, kind));
                var light = ent as SearchLight;
                if (light != null)
                {
                    cp.Light = light;
                    light.SetTargetAimpoint(cp.Position + cp.Forward * 12f + Vector3.up);
                    light.SendNetworkUpdate();
                }
            }
            return ent;
        }

        private void Despawn(Checkpoint cp, string why)
        {
            cp.RespawnTimer?.Destroy(); cp.RespawnTimer = null;
            cp.DoorTimer?.Destroy(); cp.DoorTimer = null;
            cp.RestockTimer?.Destroy(); cp.RestockTimer = null;
            DespawnProps(cp);
            foreach (var g in new List<GuardNpc>(cp.Guards)) DespawnGuard(g);
            cp.Guards.Clear();
            OnCheckpointGone(cp, why);
            if (cp.State == CheckpointState.Active) { cp.State = CheckpointState.Unspawned; cp.StateNote = why; }
        }

        private static void DespawnProps(Checkpoint cp)
        {
            cp.Despawning = true;
            if (cp.TriggerGo != null) { UnityEngine.Object.Destroy(cp.TriggerGo); cp.TriggerGo = null; }
            if (cp.ApproachGo != null) { UnityEngine.Object.Destroy(cp.ApproachGo); cp.ApproachGo = null; }
            // A snapshot: killing a vault crate re-enters OnEntityKill → OnVaultCrateGone, which
            // removes it from this list ("Collection was modified" at the tier 5 → 4 reconcile with
            // the window open, live 2026-09-20 12:02 — the switch never came down at tier 4).
            foreach (var e in new List<BaseEntity>(cp.Props))
            {
                if (e == null || e.IsDestroyed) continue;
                // One prop refusing to die must not leave the rest standing (self-test 2026-09-10).
                try { e.Kill(); }
                catch (Exception ex) { GLog($"[cp] '{cp.Name}': prop {e.ShortPrefabName} would not die: {ex.Message}"); }
            }
            cp.Props.Clear();
            cp.PropKinds.Clear();
            cp.Light = null;
            cp.Door = null; cp.Lock = null; cp.Crates.Clear();
            RestoreMonumentDoors(cp);
            cp.Despawning = false;
        }

        // Guard death: the checkpoint falls when its last guard dies (decision 0004 §7).
        private void OnGuardDied(GuardNpc guard, ulong attacker)
        {
            var cp = guard.Checkpoint;
            guard.GetComponent<GuardBrain>()?.StopPump();
            if (cp == null)
            {
                if (guard.Passive) OnFenceDied(guard, attacker); else _looseGuards.Remove(guard);
                return;
            }
            cp.Guards.Remove(guard);
            CLog($"[cp] '{cp.Name}' guard died (killer {(attacker != 0 ? attacker.ToString() : "none")}); {cp.LiveGuards()} left.");
            // Milestone 7: the stolen-ID roll (decision 0008 §11) runs in OnCorpsePopulate, on the corpse.
            if (cp.State != CheckpointState.Active) return;
            if (cp.LiveGuards() > 0) return;
            if (cp.Kind == CheckpointKind.Vault) { ScheduleVaultGuards(cp, RespawnMinutesNow()); return; } // the door stays (decision 0009 §5)
            NextTick(() => DestroyCheckpoint(cp));
        }

        private void DestroyCheckpoint(Checkpoint cp)
        {
            if (cp.State != CheckpointState.Active) return;
            var minutes = RespawnMinutesNow();
            cp.State = CheckpointState.Destroyed;
            cp.DestroyedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
            cp.StateNote = $"destroyed until {cp.DestroyedUntilUtc:HH:mm} UTC";
            _cpData.DestroyedUntil[cp.Name] = cp.DestroyedUntilUtc; _cpDirty = true;
            SaveCheckpointData(true);
            DespawnProps(cp);
            OnCheckpointGone(cp, "destroyed");
            CLog($"[cp] '{cp.Name}' destroyed; respawn in {minutes} min.");
            if (_config.Checkpoints.BroadcastDestroyed) Broadcast("Broadcast.Destroyed", string.Format(Pick(cp.Kind == CheckpointKind.Patrol ? _config.Voice.PatrolLost : _config.Voice.Destroyed, "Checkpoint {0} has fallen."), cp.Name));
            ScheduleRespawn(cp);
        }

        private int RespawnMinutesNow()
        {
            var byTier = _config.Checkpoints.RespawnMinutesByTier;
            if (byTier != null && byTier.Length > 0) return Mathf.Max(1, byTier[Mathf.Clamp(CurrentTier(), 1, byTier.Length) - 1]);
            return Mathf.Max(1, _config.Checkpoints.RespawnMinutes);
        }

        private void ScheduleRespawn(Checkpoint cp)
        {
            cp.RespawnTimer?.Destroy();
            var seconds = (float)Math.Max(5, (cp.DestroyedUntilUtc - DateTime.UtcNow).TotalSeconds);
            cp.RespawnTimer = timer.Once(seconds, () =>
            {
                cp.RespawnTimer = null;
                if (!_checkpoints.ContainsKey(cp.Name)) return;
                cp.State = CheckpointState.Unspawned;
                if (cp.Kind == CheckpointKind.Patrol)
                {
                    // A lost patrol comes back on a fresh road; a miss keeps the old route. A slot
                    // restored as Destroyed after a restart has no route at all: with no draw either it
                    // would spawn at the world origin (review 2026-09-27) — stay down, try again in 5 min.
                    cp.ShiftStartedAt = Time.realtimeSinceStartup;
                    if (!MoveRoute(cp, "respawn") && cp.Route == null)
                    {
                        cp.State = CheckpointState.Destroyed;
                        cp.DestroyedUntilUtc = DateTime.UtcNow.AddMinutes(5);
                        CLog($"[patrol] '{cp.Name}' respawn deferred: no road and no previous route; retry in 5 min.");
                        ScheduleRespawn(cp);
                        return;
                    }
                }
                if (cp.Kind == CheckpointKind.Roadblock && !cp.Pinned && !MoveRoadblock(cp) && cp.Road == null)
                {
                    // A slot restored as Destroyed has no pose; with no draw either, spawning would
                    // put it at the world origin (review 2026-09-12). Stay down, try again in 5 min.
                    cp.State = CheckpointState.Destroyed;
                    cp.DestroyedUntilUtc = DateTime.UtcNow.AddMinutes(5);
                    CLog($"[cp] '{cp.Name}' respawn deferred: no road spot and no previous pose; retry in 5 min.");
                    ScheduleRespawn(cp);
                    return;
                }
                if (TrySpawn(cp)) CLog($"[cp] '{cp.Name}' respawned{(cp.Kind == CheckpointKind.Roadblock ? " at " + V(cp.Position) : "")}.");
            });
        }

        private static string Pick(List<string> lines, string fallback)
        {
            if (lines == null || lines.Count == 0) return fallback;
            return lines[UnityEngine.Random.Range(0, lines.Count)];
        }

        #endregion

        #region Encounters (state machine · Scanner · Enforcer · CUI — decision 0004 §3–5)

        public enum EncounterState { Approach, Halted, Passed, Surrendered, Fined, Hostile, Ran }
        public enum Offer { None, Surrender, Fine }

        public class Encounter
        {
            public Checkpoint Gate;
            public BasePlayer Player;
            public ulong Id;
            public EncounterState State = EncounterState.Halted;
            public Band Band;
            public int Tier;
            public Offer Offer;
            public int Fine;
            public List<Item> Findings = new List<Item>();
            public Timer Deadline;
            public Timer AutoClose;
            public Timer LightTimer;
            public Timer Poll;
            public Vector3 LastPos;
            public float StillSince = -1f;
            public bool UiOpen;
            public Band RealBand;            // before any ID upgrade (Milestone 7)
            public string IdNote;            // the panel's ID line, if papers were checked
        }

        // One encounter per player at a time; a halted player straddling two triggers is halted once.
        private readonly Dictionary<ulong, Encounter> _encounters = new Dictionary<ulong, Encounter>();

        private void OnGateEnter(Checkpoint cp, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null || !IsRealPlayer(bp) || !cp.IsActive) return;
            if (bp.IsDead() || bp.IsSleeping() || !bp.IsConnected) return;
            var id = (ulong)bp.userID;
            if (IsExemptId(id)) return;
            if (cp.Kind == CheckpointKind.Patrol)
            {
                // Decision 0010 §A: a patrol never stops anyone inside a safe zone, and waves on
                // anyone whose band is better than StopFromBand (the owner: Neutral and worse are stopped).
                if (bp.InSafeZone()) return;
                // Everyone aboard a vehicle is judged by its driver's band, so a Citizen driver's
                // Neutral passenger is not halted in a car that was waved on (review 2026-09-27).
                var judged = id;
                var mounted = bp.GetMountedVehicle();
                if (mounted != null)
                {
                    var drv = DriverOf(mounted.VehicleParent() ?? mounted);
                    if (drv != null && IsRealPlayer(drv)) judged = (ulong)drv.userID;
                }
                var band = BandOfPlayer(judged);
                Band from;
                if (!Enum.TryParse(_config.Patrols.StopFromBand, true, out from)) from = Band.Neutral;
                if (band < from)
                {
                    var nowW = Time.realtimeSinceStartup;
                    float warned;
                    if (!(cp.WarnedUntil.TryGetValue(id, out warned) && warned > nowW))
                    {
                        cp.WarnedUntil[id] = nowW + 60f;
                        bp.ChatMessage(L("Patrol.WavedOn", bp.UserIDString));
                        CLog($"[patrol] '{cp.Name}' waved on {bp.displayName} ({band}).");
                    }
                    return;
                }
            }
            if (_encounters.ContainsKey(id)) return;
            float until;
            if (cp.CooldownUntil.TryGetValue(id, out until) && until > Time.realtimeSinceStartup)
            {
                CLog($"[gate] '{cp.Name}' ENTER {bp.displayName}: in cooldown ({until - Time.realtimeSinceStartup:0} s left).");
                return;
            }
            var vehicle = bp.GetMountedVehicle();
            var onRoad = cp.Kind == CheckpointKind.Roadblock || cp.Kind == CheckpointKind.Patrol; // both stop vehicles with the STOP rule
            if (vehicle != null && onRoad && IsRailVehicle(vehicle))
            {
                // A train cannot leave its rails to stop at a chicane; the sampler keeps roadblocks
                // RailClearance away, and a rail crossing inside the sphere is not a checkpoint run.
                CLog($"[gate] '{cp.Name}' waved through {bp.displayName}: {vehicle.ShortPrefabName} is a rail vehicle.");
                return;
            }
            if (vehicle != null && onRoad && _config.Roadblocks.SkipDriverlessVehicles && IsDriverless(vehicle))
            {
                CLog($"[gate] '{cp.Name}' waved through {bp.displayName}: {vehicle.ShortPrefabName} has no real driver.");
                return;
            }
            StartEncounter(cp, bp, vehicle != null && onRoad);
        }

        // Task 5.6: a guard hit by a vehicle with a real driver = that driver ran the checkpoint,
        // once per driver per checkpoint per encounter cooldown. A running encounter is resolved
        // as Ran so the flow stays in one place; otherwise the penalty is applied directly.
        private void OnGuardHitByVehicle(GuardNpc guard, BaseVehicle vehicle)
        {
            var cp = guard.Checkpoint;
            if (cp == null || !cp.IsActive) return;
            var root = vehicle.VehicleParent() ?? vehicle;
            var driver = DriverOf(root);
            if (driver == null || !IsRealPlayer(driver) || IsExemptId((ulong)driver.userID)) return;
            var id = (ulong)driver.userID;
            var now = Time.realtimeSinceStartup;
            float until;
            if (cp.RunOverUntil.TryGetValue(id, out until) && until > now) return;
            cp.RunOverUntil[id] = now + Mathf.Max(1, _config.Checkpoints.EncounterCooldownSeconds);
            Encounter e;
            if (_encounters.TryGetValue(id, out e) && e.Gate == cp && (e.State == EncounterState.Approach || e.State == EncounterState.Halted))
            {
                Ran(e, $"ran over a guard with {root.ShortPrefabName}");
                return;
            }
            CLog($"[gate] '{cp.Name}' {driver.displayName} ran over a guard with {root.ShortPrefabName}.");
            Adjust(id, _config.Reputation.RanCheckpoint, "Reason.RanCheckpoint");
            Nudge(_config.Threat.NudgeRanCheckpoint, "guard run over");
            MakeHostile(cp, driver);
            Voice(driver, _config.Voice.Ran);
            if (_config.Checkpoints.BroadcastRunAndKill) Broadcast("Broadcast.Ran", driver.displayName, cp.Name);
        }

        private static BasePlayer DriverOf(BaseVehicle vehicle)
        {
            if (vehicle == null) return null;
            var driver = vehicle.GetDriver();
            if (driver == null)
            {
                var parent = vehicle.VehicleParent();
                if (parent != null) driver = parent.GetDriver();
            }
            return driver;
        }

        private static bool IsDriverless(BaseVehicle vehicle)
        {
            var driver = DriverOf(vehicle);
            return driver == null || !IsRealPlayer(driver);
        }

        // Trains and wagons (TrainCar : BaseVehicle on the 2026-09 assembly) — never road traffic.
        private static bool IsRailVehicle(BaseVehicle vehicle) =>
            vehicle is TrainCar || vehicle.VehicleParent() is TrainCar;

        // Task 5.4: a vehicle with a real driver entering the approach sphere gets the STOP prompt,
        // once per vehicle per checkpoint per 30 s (modules and chassis all fire ENTER).
        private void OnApproachEnter(Checkpoint cp, BaseEntity ent)
        {
            var vehicle = ent as BaseVehicle;
            if (vehicle == null || !cp.IsActive) return;
            var root = vehicle.VehicleParent() ?? vehicle;
            var driver = DriverOf(root);
            var netId = root.net != null ? root.net.ID.Value : 0UL;
            var now = Time.realtimeSinceStartup;
            float until;
            if (cp.WarnedUntil.TryGetValue(netId, out until) && until > now) return;
            cp.WarnedUntil[netId] = now + 30f;
            if (IsRailVehicle(root))
            {
                // Live 2026-09-10 08:02: a locomotive on rails beside road #13 tripped the sphere.
                CLog($"[gate] '{cp.Name}' approach: {root.ShortPrefabName} is a rail vehicle — ignored.");
                return;
            }
            if (driver == null || !IsRealPlayer(driver))
            {
                if (_config.Roadblocks.SkipDriverlessVehicles) CLog($"[gate] '{cp.Name}' approach: {root.ShortPrefabName} with no real driver — waved through.");
                return;
            }
            var riders = new List<BasePlayer>();
            root.GetMountedPlayers(riders);
            if (!riders.Contains(driver)) riders.Add(driver);
            foreach (var r in riders)
            {
                if (!IsRealPlayer(r) || !r.IsConnected || IsExemptId((ulong)r.userID)) continue;
                if (cp.Kind == CheckpointKind.Patrol)
                {
                    // A patrol has no barricade, and waves on the bands it does not stop (owner, 2026-09-27).
                    Band from; if (!Enum.TryParse(_config.Patrols.StopFromBand, true, out from)) from = Band.Neutral;
                    if (BandOfPlayer((ulong)driver.userID) < from) continue; // the driver's band decides for the whole vehicle
                    r.ChatMessage(L("Patrol.Stop", r.UserIDString));
                    ShowStopBanner(r, L("Ui.PatrolStop", r.UserIDString));
                    continue;
                }
                r.ChatMessage(L("Chat.Stop", r.UserIDString));
                ShowStopBanner(r);
            }
            CLog($"[gate] '{cp.Name}' approach: {root.ShortPrefabName} driven by {driver.displayName}, {riders.Count} rider(s) warned.");
        }

        private const string UiStop = "PapersPlease.Stop";

        private void ShowStopBanner(BasePlayer bp, string text = null)
        {
            CuiHelper.DestroyUi(bp, UiStop);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.55 0.1 0.1 0.9" },
                RectTransform = { AnchorMin = "0.35 0.86", AnchorMax = "0.65 0.93" },
                CursorEnabled = false
            }, "Overlay", UiStop);
            ui.Add(new CuiLabel
            {
                Text = { Text = text ?? L("Ui.Stop", bp.UserIDString), FontSize = 20, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiStop);
            CuiHelper.AddUi(bp, ui);
            var id = (ulong)bp.userID;
            timer.Once(6f, () => { var p = BasePlayer.FindByID(id); if (p != null && p.IsConnected) CuiHelper.DestroyUi(p, UiStop); });
        }

        private void OnGateLeave(Checkpoint cp, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null || !IsRealPlayer(bp)) return;
            Encounter e;
            if (!_encounters.TryGetValue((ulong)bp.userID, out e) || e.Gate != cp) return;
            if (e.State == EncounterState.Halted) Ran(e, "left the trigger");
            else if (e.State == EncounterState.Approach) Ran(e, "drove through without stopping");
        }

        private void OnCheckpointGone(Checkpoint cp, string why)
        {
            foreach (var e in new List<Encounter>(_encounters.Values))
                if (e.Gate == cp) Abort(e, why);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            Encounter e;
            if (_encounters.TryGetValue((ulong)player.userID, out e)) Abort(e, "disconnected");
        }

        private void StartEncounter(Checkpoint cp, BasePlayer bp, bool approach)
        {
            var id = (ulong)bp.userID;
            var e = new Encounter { Gate = cp, Player = bp, Id = id, Band = BandOfPlayer(id), Tier = CurrentTier() };
            _encounters[id] = e;
            if (approach)
            {
                // Task 5.5: no panel until the vehicle has stopped; speed is the rider's own movement.
                e.State = EncounterState.Approach;
                e.LastPos = bp.transform.position;
                bp.ChatMessage(L("Chat.StopNow", bp.UserIDString));
                CLog($"[gate] '{cp.Name}' approach by {bp.displayName} in {bp.GetMountedVehicle()?.ShortPrefabName ?? "?"}; waiting for a stop.");
                e.Poll = timer.Every(0.5f, () => ApproachPoll(e));
                return;
            }
            HaltEncounter(e);
        }

        private void ApproachPoll(Encounter e)
        {
            var bp = e.Player;
            if (e.State != EncounterState.Approach) { e.Poll?.Destroy(); e.Poll = null; return; }
            if (bp == null || bp.IsDestroyed || !bp.IsConnected || bp.IsDead()) { Abort(e, "gone while approaching"); return; }
            var now = Time.realtimeSinceStartup;
            var speed = Vector3.Distance(bp.transform.position, e.LastPos) / 0.5f;
            e.LastPos = bp.transform.position;
            var dismounted = bp.GetMountedVehicle() == null;
            if (speed <= _config.Roadblocks.StopSpeed || dismounted)
            {
                if (e.StillSince < 0f) e.StillSince = now;
                if (now - e.StillSince >= _config.Roadblocks.StopSeconds || dismounted)
                {
                    e.Poll?.Destroy(); e.Poll = null;
                    CLog($"[gate] '{e.Gate.Name}' {bp.displayName} stopped ({(dismounted ? "dismounted" : $"{speed:0.0} m/s")}); halting.");
                    HaltEncounter(e);
                }
            }
            else e.StillSince = -1f;
        }

        // The enforcer: band × tier → outcome (decision 0004 §4), from a standing start.
        private void HaltEncounter(Encounter e)
        {
            var cfg = _config.Checkpoints;
            var cp = e.Gate;
            var bp = e.Player;
            var id = e.Id;
            e.State = EncounterState.Halted;
            e.Band = BandOfPlayer(id);
            e.Tier = CurrentTier();
            foreach (var g in cp.Guards)
            {
                var brain = g != null && !g.IsDestroyed ? g.GetComponent<GuardBrain>() : null;
                if (brain != null && (brain.Watch == null || brain.Watch.IsDestroyed)) brain.Watch = bp;
            }
            if (cp.Light != null && !cp.Light.IsDestroyed)
            {
                e.LightTimer = timer.Every(0.5f, () =>
                {
                    if (cp.Light == null || cp.Light.IsDestroyed || bp == null || bp.IsDestroyed) { e.LightTimer?.Destroy(); e.LightTimer = null; return; }
                    cp.Light.SetTargetAimpoint(bp.eyes.position);
                    cp.Light.SendNetworkUpdate();
                });
            }
            if (cp.Kind == CheckpointKind.Patrol) { PausePatrol(cp); Voice(bp, _config.Voice.PatrolHalt); }
            else Voice(bp, _config.Voice.Halt);
            CLog($"[gate] '{cp.Name}' halted {bp.displayName} band={e.Band} tier={e.Tier} alert={cp.IsAlert} mounted={(bp.GetMounted() != null)}");

            // Milestone 7: papers are checked first — a valid ID softens the band by one, a
            // spotted forgery ends the encounter here (decision 0008 §7, §10).
            e.RealBand = e.Band;
            if (ApplyPapers(e)) return;

            if (e.Band == Band.Enemy) { AlertCheckpoints(cp.Position, bp.displayName, "Enemy sighted"); Hostile(e, "enemy on sight"); return; }
            if (e.Band == Band.Wanted) { Hostile(e, "wanted on sight"); return; }
            if (cp.IsAlert && e.Band >= Band.Suspect) { Hostile(e, cp.Curfew ? "suspect under curfew" : "suspect during alert"); return; }
            if (e.Band == Band.Citizen)
            {
                if (e.Tier < cfg.CitizenScanFromTier) { Pass(e, L("Ui.PassNoScan", bp.UserIDString)); return; }
                Scan(bp, e.Findings);
                if (e.Findings.Count == 0) { Pass(e, L("Ui.Clean", bp.UserIDString)); return; }
                var summary = Summarize(e.Findings, bp.UserIDString);
                var taken = Confiscate(e.Findings);
                CLog($"[gate] '{cp.Name}' confiscated {taken} item(s) from citizen {bp.displayName}.");
                Pass(e, L("Ui.Confiscated", bp.UserIDString, summary));
                return;
            }
            Scan(bp, e.Findings);
            if (e.Band == Band.Neutral)
            {
                if (e.Findings.Count == 0) { Pass(e, L("Ui.Clean", bp.UserIDString)); return; }
                e.Offer = Offer.Surrender;
                Voice(bp, _config.Voice.Contraband);
                ShowChoice(e, L("Ui.Contraband", bp.UserIDString, Summarize(e.Findings, bp.UserIDString)));
                return;
            }
            // Suspect: pay or refuse; contraband goes with the fine.
            e.Offer = Offer.Fine;
            e.Fine = cfg.FineScrap * (e.Tier >= cfg.FineDoubleFromTier ? 2 : 1);
            var scrapHave = ScrapCount(bp);
            var extra = e.Findings.Count > 0 ? L("Ui.FineContraband", bp.UserIDString, Summarize(e.Findings, bp.UserIDString)) : "";
            Voice(bp, _config.Voice.Fine);
            ShowChoice(e, L("Ui.FineBody", bp.UserIDString, BandName(e.Band, bp.UserIDString), e.Fine, scrapHave, extra));
        }

        private void ShowChoice(Encounter e, string body)
        {
            var uid = e.Player.UserIDString;
            var seconds = Mathf.Max(5, _config.Checkpoints.DecisionTimeoutSeconds);
            e.Deadline?.Destroy();
            e.Deadline = timer.Once(seconds, () => { if (e.State == EncounterState.Halted) Ran(e, "decision timeout"); });
            var buttons = new List<UiButton>();
            if (e.Offer == Offer.Surrender) buttons.Add(new UiButton { Label = L("Ui.BtnSurrender", uid), Command = "papers.cp.choice surrender", Color = "0.2 0.5 0.25 1" });
            else buttons.Add(new UiButton { Label = L("Ui.BtnPay", uid, e.Fine), Command = "papers.cp.choice pay", Color = "0.2 0.45 0.55 1" });
            buttons.Add(new UiButton { Label = L("Ui.BtnRefuse", uid), Command = "papers.cp.choice refuse", Color = "0.55 0.2 0.2 1" });
            OpenUi(e, body + "\n" + L("Ui.Timer", uid, seconds), buttons, true);
        }

        // Fired by the CUI buttons as the player; not admin-gated (only touches the caller's own encounter).
        [ConsoleCommand("papers.cp.choice")]
        private void CmdChoice(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            Encounter e;
            if (!_encounters.TryGetValue((ulong)player.userID, out e) || e.State != EncounterState.Halted) return;
            var choice = arg.GetString(0, "").ToLowerInvariant();
            if (choice == "refuse") { Hostile(e, "refused"); return; }
            if (choice == "surrender" && e.Offer == Offer.Surrender) { Surrender(e); return; }
            if (choice == "pay" && e.Offer == Offer.Fine) { PayFine(e); return; }
        }

        // ---- outcomes ----

        private void Pass(Encounter e, string body)
        {
            e.State = EncounterState.Passed;
            Voice(e.Player, _config.Voice.Pass);
            CLog($"[gate] '{e.Gate.Name}' passed {e.Player.displayName} ({e.Findings.Count} item(s) found).");
            ResultWithOffer(e, body);
        }

        private void Surrender(Encounter e)
        {
            var summary = Summarize(e.Findings, e.Player.UserIDString);
            var n = Confiscate(e.Findings);
            e.State = EncounterState.Surrendered;
            Adjust(e.Id, _config.Reputation.Surrender, "Reason.Surrender");
            Voice(e.Player, _config.Voice.Pass);
            CLog($"[gate] '{e.Gate.Name}' {e.Player.displayName} surrendered {n} item(s).");
            ResultWithOffer(e, L("Ui.Surrendered", e.Player.UserIDString, summary));
        }

        private void PayFine(Encounter e)
        {
            var bp = e.Player;
            var scrap = ItemManager.FindItemDefinition("scrap");
            if (scrap == null || bp.inventory.GetAmount(scrap.itemid) < e.Fine)
            {
                bp.ChatMessage(L("Ui.NoScrap", bp.UserIDString, e.Fine));
                return; // still halted; the deadline keeps running
            }
            bp.inventory.Take(null, scrap.itemid, e.Fine);
            var summary = e.Findings.Count > 0 ? L("Ui.FinedContraband", bp.UserIDString, Summarize(e.Findings, bp.UserIDString)) : "";
            Confiscate(e.Findings);
            e.State = EncounterState.Fined;
            Adjust(e.Id, _config.Reputation.FinePaid, "Reason.FinePaid");
            Voice(bp, _config.Voice.Pass);
            CLog($"[gate] '{e.Gate.Name}' {bp.displayName} paid {e.Fine} scrap.");
            OpenUi(e, L("Ui.Fined", bp.UserIDString, e.Fine, summary), null, false);
            Finish(e, true);
        }

        private void Hostile(Encounter e, string why, string body = null)
        {
            e.State = EncounterState.Hostile;
            MakeHostile(e.Gate, e.Player);
            Voice(e.Player, _config.Voice.Hostile);
            CLog($"[gate] '{e.Gate.Name}' hostile on {e.Player.displayName} ({why}).");
            OpenUi(e, body ?? L("Ui.Hostile", e.Player.UserIDString, Mathf.Max(10, _config.Checkpoints.HostileSeconds)), null, false);
            Finish(e, true);
        }

        private void Ran(Encounter e, string why)
        {
            e.State = EncounterState.Ran;
            // One "ran" per pass: hitting a guard on the way out is the same offence, not a second
            // −5 (live 2026-09-12: run at :20, run-over at :34, charged twice).
            e.Gate.RunOverUntil[e.Id] = Time.realtimeSinceStartup + Mathf.Max(1, _config.Checkpoints.EncounterCooldownSeconds);
            Adjust(e.Id, _config.Reputation.RanCheckpoint, "Reason.RanCheckpoint");
            Nudge(_config.Threat.NudgeRanCheckpoint, "checkpoint run");
            MakeHostile(e.Gate, e.Player);
            Voice(e.Player, _config.Voice.Ran);
            CLog($"[gate] '{e.Gate.Name}' {e.Player.displayName} ran the checkpoint ({why}).");
            if (_config.Checkpoints.BroadcastRunAndKill) Broadcast("Broadcast.Ran", e.Player.displayName, e.Gate.Name);
            OpenUi(e, L("Ui.Ran", e.Player.UserIDString, Mathf.Max(10, _config.Checkpoints.HostileSeconds)), null, false);
            Finish(e, true);
        }

        // Hostile = the native flag (Outpost peacekeepers and sentries join in) + every guard at the gate.
        private void MakeHostile(Checkpoint cp, BasePlayer bp)
        {
            FlagHostile(bp);
            foreach (var g in cp.Guards)
                if (g != null && !g.IsDestroyed) g.GetComponent<GuardBrain>()?.ForceTarget(bp);
        }

        private readonly Dictionary<ulong, float> _hostileUntil = new Dictionary<ulong, float>();

        private void FlagHostile(BasePlayer bp)
        {
            var seconds = Mathf.Max(10, _config.Checkpoints.HostileSeconds);
            var id = (ulong)bp.userID;
            bp.MarkHostileFor(seconds);
            var until = Time.realtimeSinceStartup + seconds;
            _hostileUntil[id] = until;
            RecordSighting(bp); // Milestone 9: a Wanted/Enemy turned on by Cobalt is a sighting (decision 0010 §C)
            timer.Once(seconds + 1f, () =>
            {
                float stored;
                if (!_hostileUntil.TryGetValue(id, out stored) || stored != until) return; // re-flagged since
                _hostileUntil.Remove(id);
                var p = BasePlayer.FindByID(id);
                if (p != null && p.IsConnected && !p.IsHostile()) p.ChatMessage(L("Notify.StoodDown", p.UserIDString));
            });
        }

        private string HostileLine(BasePlayer bp)
        {
            if (bp == null || !bp.IsHostile()) return "";
            float until;
            var known = _hostileUntil.TryGetValue((ulong)bp.userID, out until) && until > Time.realtimeSinceStartup;
            var forText = known ? L("Papers.HostileFor", bp.UserIDString, Mathf.CeilToInt(until - Time.realtimeSinceStartup)) : "";
            return "\n" + L("Papers.Hostile", bp.UserIDString, forText);
        }

        // An Enemy sighting puts every gate within AlertRadius on Alert (decision 0004 §4).
        private void AlertCheckpoints(Vector3 origin, string who, string why)
        {
            var until = Time.realtimeSinceStartup + Mathf.Max(1, _config.Checkpoints.AlertMinutes) * 60f;
            var n = 0;
            foreach (var cp in _checkpoints.Values)
            {
                if (Vector3.Distance(cp.Position, origin) > _config.Checkpoints.AlertRadius) continue;
                cp.AlertUntil = until; n++;
            }
            CLog($"[alert] {who} ({why}); {n} checkpoint(s) on alert for {_config.Checkpoints.AlertMinutes} min.");
            UpdatePropLights();
        }

        private void Finish(Encounter e, bool cooldown)
        {
            e.Deadline?.Destroy(); e.Deadline = null;
            e.LightTimer?.Destroy(); e.LightTimer = null;
            e.Poll?.Destroy(); e.Poll = null;
            if (cooldown) e.Gate.CooldownUntil[e.Id] = Time.realtimeSinceStartup + Mathf.Max(1, _config.Checkpoints.EncounterCooldownSeconds);
            StampCleared(e.Gate.Monument, e.Id);
            _encounters.Remove(e.Id);
            if (e.Gate.Kind == CheckpointKind.Patrol) ResumePatrol(e.Gate);
            foreach (var g in e.Gate.Guards)
            {
                var brain = g != null && !g.IsDestroyed ? g.GetComponent<GuardBrain>() : null;
                if (brain != null && brain.Watch == e.Player) brain.Watch = null;
            }
            if (e.UiOpen)
            {
                e.AutoClose?.Destroy();
                e.AutoClose = timer.Once(5f, () => CloseUi(e));
            }
        }

        // Death, disconnect, or the checkpoint going away mid-encounter: no penalty, no cooldown.
        private void Abort(Encounter e, string why)
        {
            e.Deadline?.Destroy(); e.Deadline = null;
            e.LightTimer?.Destroy(); e.LightTimer = null;
            e.Poll?.Destroy(); e.Poll = null;
            e.AutoClose?.Destroy(); e.AutoClose = null;
            _encounters.Remove(e.Id);
            if (e.Gate.Kind == CheckpointKind.Patrol) ResumePatrol(e.Gate);
            CloseUi(e);
            CLog($"[gate] '{e.Gate.Name}' encounter with {e.Player?.displayName} aborted ({why}).");
        }

        // ---- Scanner (task 3.5) ----

        private bool IsContraband(Item item) =>
            item != null && item.info != null && (IsTagged(item) || _contraband.Contains(item.info.shortname));

        private void Scan(BasePlayer bp, List<Item> found)
        {
            if (bp.inventory == null) return;
            ScanContainer(bp.inventory.containerMain, found);
            ScanContainer(bp.inventory.containerBelt, found);
            if (_config.Contraband.ScanWear || CurrentTier() >= _config.Checkpoints.ScanWearFromTier) ScanContainer(bp.inventory.containerWear, found);
        }

        private void ScanContainer(ItemContainer c, List<Item> found)
        {
            if (c == null) return;
            foreach (var item in c.itemList) if (IsContraband(item)) found.Add(item);
        }

        private string Summarize(List<Item> items, string viewerId)
        {
            if (items.Count == 0) return L("Ui.Nothing", viewerId);
            var sb = new StringBuilder();
            for (var i = 0; i < items.Count && i < 6; i++)
            {
                var it = items[i];
                if (i > 0) sb.Append(", ");
                sb.Append(string.IsNullOrEmpty(it.name) ? it.info.displayName.english : it.name);
                if (it.amount > 1) sb.Append(" x").Append(it.amount);
            }
            if (items.Count > 6) sb.Append(L("Ui.More", viewerId, items.Count - 6));
            return sb.ToString();
        }

        // Confiscated contraband is destroyed ("surrendered to Cobalt"); a stash is v1.x.
        private static int Confiscate(List<Item> items)
        {
            var n = 0;
            foreach (var it in items)
            {
                if (it == null || it.parent == null) continue;
                it.RemoveFromContainer();
                it.Remove();
                n++;
            }
            items.Clear();
            return n;
        }

        private static int ScrapCount(BasePlayer bp)
        {
            var scrap = ItemManager.FindItemDefinition("scrap");
            return scrap != null && bp.inventory != null ? bp.inventory.GetAmount(scrap.itemid) : 0;
        }

        private void Voice(BasePlayer bp, List<string> lines)
        {
            var line = Pick(lines, null);
            if (string.IsNullOrEmpty(line) || bp == null || !bp.IsConnected) return;
            bp.ChatMessage(L("Chat.Voice", bp.UserIDString, line));
        }

        // ---- CUI (task 3.9) ----

        private class UiButton { public string Label; public string Command; public string Color; }

        private void OpenUi(Encounter e, string body, List<UiButton> buttons, bool cursor)
        {
            var bp = e.Player;
            if (bp == null || !bp.IsConnected) return;
            var uid = bp.UserIDString;
            CuiHelper.DestroyUi(bp, UiMain);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.06 0.07 0.09 0.96" },
                RectTransform = { AnchorMin = "0.32 0.30", AnchorMax = "0.68 0.70" },
                CursorEnabled = cursor
            }, "Overlay", UiMain);
            ui.Add(new CuiLabel
            {
                Text = { Text = L("Ui.Title", uid), FontSize = 24, Align = TextAnchor.MiddleCenter, Color = "0.55 0.75 1 1" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 0.98" }
            }, UiMain);
            ui.Add(new CuiLabel
            {
                Text = { Text = L("Ui.Gate", uid, e.Gate.Name) + "\n" + L("Ui.Subject", uid, bp.displayName, BandName(e.Band, uid)) + (string.IsNullOrEmpty(e.IdNote) ? "" : "\n" + e.IdNote), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.04 0.72", AnchorMax = "0.96 0.86" }
            }, UiMain);
            var stateColor = e.State == EncounterState.Hostile || e.State == EncounterState.Ran ? "1 0.45 0.4 1" : "0.88 0.88 0.88 1";
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = stateColor },
                RectTransform = { AnchorMin = "0.05 0.30", AnchorMax = "0.95 0.70" }
            }, UiMain);
            if (buttons != null && buttons.Count > 0)
            {
                var w = 0.84f / buttons.Count;
                for (var i = 0; i < buttons.Count; i++)
                {
                    var x0 = 0.08f + i * w + 0.02f; var x1 = 0.08f + (i + 1) * w - 0.02f;
                    ui.Add(new CuiButton
                    {
                        Button = { Command = buttons[i].Command, Color = buttons[i].Color },
                        RectTransform = { AnchorMin = $"{x0:0.###} 0.08", AnchorMax = $"{x1:0.###} 0.24" },
                        Text = { Text = buttons[i].Label, FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                    }, UiMain);
                }
            }
            else
            {
                ui.Add(new CuiLabel
                {
                    Text = { Text = L("Ui.Closing", uid), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.5 0.5 0.5 1" },
                    RectTransform = { AnchorMin = "0.05 0.08", AnchorMax = "0.95 0.20" }
                }, UiMain);
            }
            CuiHelper.AddUi(bp, ui);
            e.UiOpen = true;
        }

        private void CloseUi(Encounter e)
        {
            e.AutoClose?.Destroy(); e.AutoClose = null;
            if (!e.UiOpen) return;
            e.UiOpen = false;
            // A finished encounter's late timer must not tear down the panel of a newer one at the
            // next gate — a decision panel destroyed under the player ran out as "timeout" (review 2026-09-16).
            if (UiBusy(e.Id, e)) return;
            if (e.Player != null && e.Player.IsConnected) CuiHelper.DestroyUi(e.Player, UiMain);
        }

        // True while a live encounter other than `except` has the main panel open for this player.
        private bool UiBusy(ulong id, Encounter except)
        {
            Encounter cur;
            return _encounters.TryGetValue(id, out cur) && cur != except && cur.UiOpen;
        }

        #endregion

        #region Papers and the fence (Milestone 7, decision 0008)

        // ---- The ID registry: identity lives server-side, keyed by the note's item uid ----

        private const string IdDataFile = "PapersPlease/ids";
        private const string UiFence = "PapersPlease.Fence";

        private class IdRecord
        {
            public ulong OwnerId;            // 0 for a stolen (guard) ID: whoever holds it may try it
            public string OwnerName;
            public DateTime IssuedUtc;
            public bool Forged;
            public bool Stolen;
            public bool Revoked;
            public int Serial;
        }

        private class IdData
        {
            public string WipeId;
            public int NextSerial = 1;
            public Dictionary<string, IdRecord> Ids = new Dictionary<string, IdRecord>();
        }

        private IdData _ids = new IdData();
        private bool _idsDirty;

        private void LoadIds()
        {
            try { _ids = Interface.Oxide.DataFileSystem.ReadObject<IdData>(IdDataFile) ?? new IdData(); }
            catch (Exception ex) { PrintWarning($"ID registry unreadable ({ex.Message}); starting fresh."); _ids = new IdData(); }
            if (_ids.Ids == null) _ids.Ids = new Dictionary<string, IdRecord>();
            var wipe = SaveRestore.WipeId;
            if (!string.IsNullOrEmpty(wipe) && _ids.WipeId != wipe)
            {
                if (_ids.WipeId != null) Puts($"New wipe detected ({wipe}); {_ids.Ids.Count} Cobalt ID record(s) cleared.");
                _ids = new IdData { WipeId = wipe };
                _idsDirty = true;
            }
        }

        private void SaveIds(bool force)
        {
            if (!_idsDirty && !force) return;
            try { Interface.Oxide.DataFileSystem.WriteObject(IdDataFile, _ids); _idsDirty = false; }
            catch (Exception ex) { PrintError($"ID registry save failed: {ex.Message}"); }
        }

        private static string IdKey(Item item) => item.uid.Value.ToString();

        private IdRecord FindId(Item item)
        {
            if (item == null || item.info == null || item.info.shortname != "note") return null;
            IdRecord r;
            return _ids.Ids.TryGetValue(IdKey(item), out r) ? r : null;
        }

        private static string IdKindText(IdRecord r) => r.Revoked ? "revoked" : r.Stolen ? "stolen" : r.Forged ? "forged" : "legit";

        // A note named "Cobalt ID" whose text is flavour; the registry entry is what a scan trusts.
        private Item CreateId(ulong ownerId, string ownerName, bool forged, bool stolen)
        {
            var item = ItemManager.CreateByName("note", 1);
            if (item == null) return null;
            var rec = new IdRecord { OwnerId = ownerId, OwnerName = ownerName ?? "", IssuedUtc = DateTime.UtcNow, Forged = forged, Stolen = stolen, Serial = _ids.NextSerial++ };
            _ids.Ids[IdKey(item)] = rec;
            _idsDirty = true;
            item.name = _config.Papers.IdName;
            item.text = $"COBALT IDENTITY DOCUMENT\nHolder: {rec.OwnerName}\nSerial: C-{rec.Serial:00000}\nIssued: {rec.IssuedUtc:yyyy-MM-dd} at threat level {CurrentTier()}\n\nPresent at any Cobalt checkpoint on request.";
            item.MarkDirty();
            return item;
        }

        private static void GiveOrDrop(BasePlayer bp, Item item)
        {
            if (item == null || bp == null) return;
            if (bp.inventory == null || !bp.inventory.GiveItem(item))
                item.Drop(bp.transform.position + Vector3.up * 1.2f + bp.eyes.BodyForward() * 0.5f, Vector3.up * 0.5f);
        }

        private static int TakeItems(BasePlayer bp, string shortname, int amount)
        {
            if (amount <= 0 || bp.inventory == null) return 0;
            var def = ItemManager.FindItemDefinition(shortname);
            return def == null ? 0 : bp.inventory.Take(null, def.itemid, amount);
        }

        private int ForgeFailChance(int tier)
        {
            var arr = _config.Papers.ForgeFailByTier;
            if (arr == null || arr.Length == 0) return 0;
            return Mathf.Clamp(arr[Mathf.Clamp(tier, 1, arr.Length) - 1], 0, 100);
        }

        private int RevokeLegitIds(ulong owner, string why, string keep = null)
        {
            var n = 0;
            foreach (var kv in _ids.Ids)
            {
                var r = kv.Value;
                if (r.OwnerId != owner || r.Forged || r.Revoked || kv.Key == keep) continue;
                r.Revoked = true; n++;
            }
            if (n > 0) { _idsDirty = true; CLog($"[papers] revoked {n} legit ID(s) of {owner} ({why})."); }
            return n;
        }

        private Checkpoint CheckpointNear(Vector3 pos, float extra)
        {
            Checkpoint best = null; var bestD = float.MaxValue;
            foreach (var cp in _checkpoints.Values)
            {
                if (!cp.IsActive) continue;
                var d = Vector3.Distance(pos, cp.Position);
                if (d <= cp.Radius(_config.Checkpoints) + extra && d < bestD) { bestD = d; best = cp; }
            }
            return best;
        }

        // Who may buy a legit ID and for how much: Citizens (IdFeeCitizen, 0 = free) and Neutrals
        // (IdFee); anyone worse is refused (-1). Owner's call, 2026-09-14.
        private int IdFeeFor(Band band) =>
            band == Band.Citizen ? Mathf.Max(0, _config.Papers.IdFeeCitizen) : band == Band.Neutral ? Mathf.Max(0, _config.Papers.IdFee) : -1;

        // The legit, unrevoked ID in this player's own name that is actually on them (main + belt),
        // or null. The offer and the request key off the pockets, not the registry: a note lost to a
        // corpse or a furnace must not block a replacement forever (review 2026-09-16).
        private IdRecord CarriedLegitId(BasePlayer bp)
        {
            if (bp == null || bp.inventory == null) return null;
            var carried = new List<Item>();
            CollectIds(bp.inventory.containerMain, carried, this);
            CollectIds(bp.inventory.containerBelt, carried, this);
            IdRecord best = null;
            foreach (var it in carried)
            {
                var r = FindId(it);
                if (r == null || r.Revoked || r.Forged || r.OwnerId != (ulong)bp.userID) continue;
                if (best == null || r.Serial > best.Serial) best = r;
            }
            return best;
        }

        // /papers request, or the pass panel's button — a Citizen or Neutral at a checkpoint gets a legit ID (decision 0008 §6).
        private string RequestId(BasePlayer bp)
        {
            var cfg = _config.Papers; var uid = bp.UserIDString; var id = (ulong)bp.userID;
            if (!cfg.Enabled) return L("Papers.Disabled", uid);
            var band = BandOfPlayer(id);
            var fee = IdFeeFor(band);
            if (fee < 0) return L("Papers.RequestNotCitizen", uid, BandName(band, uid));
            var cp = CheckpointNear(bp.transform.position, Mathf.Max(0f, cfg.RequestRange));
            if (cp == null) return L("Papers.RequestNoGate", uid);
            if (fee > 0 && ScrapCount(bp) < fee) return L("Papers.RequestNoScrap", uid, fee);
            var held = CarriedLegitId(bp);
            if (held != null) return L("Papers.RequestHeld", uid, held.Serial); // no double charge, no free spam
            // The note first, the fee and the revocation only once it exists (review 2026-09-16).
            var item = CreateId(id, bp.displayName, false, false);
            if (item == null) return L("Papers.RequestFailed", uid);
            if (fee > 0) TakeItems(bp, "scrap", fee);
            var replaced = RevokeLegitIds(id, "replaced", IdKey(item));
            GiveOrDrop(bp, item);
            var rec = FindId(item);
            CLog($"[papers] '{cp.Name}' issued ID C-{(rec != null ? rec.Serial : 0)} to {bp.displayName} ({band}) for {fee} scrap ({replaced} older revoked).");
            Voice(bp, _config.Voice.Papers);
            return fee > 0 ? L("Papers.Issued", uid, fee, cp.Name) : L("Papers.IssuedFree", uid, cp.Name);
        }

        // The pass panel offers papers to a Citizen or Neutral who holds none (owner's call, 2026-09-14):
        // the result stays up 20 s with an ID button and a close button instead of closing itself.
        private void ResultWithOffer(Encounter e, string body)
        {
            var bp = e.Player; var uid = bp.UserIDString;
            var fee = _config.Papers.Enabled ? IdFeeFor(e.RealBand) : -1;
            if (fee < 0 || CarriedLegitId(bp) != null) { OpenUi(e, body, null, false); Finish(e, true); return; }
            var buttons = new List<UiButton>
            {
                new UiButton { Label = fee > 0 ? L("Ui.BtnIdBuy", uid, fee) : L("Ui.BtnIdFree", uid), Command = "papers.cp.id", Color = "0.2 0.45 0.55 1" },
                new UiButton { Label = L("Ui.BtnClose", uid), Command = "papers.cp.close", Color = "0.3 0.3 0.3 1" },
            };
            OpenUi(e, body + "\n" + L("Ui.IdOffer", uid, fee > 0 ? L("Ui.IdOfferFee", uid, fee) : L("Ui.IdOfferFree", uid)), buttons, true);
            Finish(e, true);
            e.AutoClose?.Destroy();
            e.AutoClose = timer.Once(20f, () => CloseUi(e));
        }

        // Fired by the pass panel's ID button as the player; RequestId re-checks band, place, fee and
        // the pockets (a second click while the first note is already on them is refused).
        [ConsoleCommand("papers.cp.id")]
        private void CmdTakeId(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp == null) return;
            var id = (ulong)bp.userID;
            if (UiBusy(id, null)) return; // a live encounter owns the panel
            var text = RequestId(bp);
            OpenPanel(bp, UiMain, L("Ui.Title", bp.UserIDString), L("Ui.Subject", bp.UserIDString, bp.displayName, BandName(BandOfPlayer(id), bp.UserIDString)), text, null, false, "0.55 0.75 1 1");
            timer.Once(6f, () => { if (bp != null && bp.IsConnected && !UiBusy(id, null)) CuiHelper.DestroyUi(bp, UiMain); });
        }

        [ConsoleCommand("papers.cp.close")]
        private void CmdCloseUi(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp != null) CuiHelper.DestroyUi(bp, UiMain);
        }

        private string RecipeText(string viewerId)
        {
            var sb = new StringBuilder();
            foreach (var kv in _config.Papers.ForgeRecipe)
            {
                if (sb.Length > 0) sb.Append(", ");
                var def = ItemManager.FindItemDefinition(kv.Key);
                sb.Append(def != null ? def.displayName.english : kv.Key).Append(" x").Append(kv.Value);
            }
            return sb.ToString();
        }

        // /papers forge — at a workbench, with the recipe, anyone can make a forged ID (decision 0008 §9).
        private string ForgeId(BasePlayer bp)
        {
            var cfg = _config.Papers; var uid = bp.UserIDString; var id = (ulong)bp.userID;
            if (!cfg.Enabled) return L("Papers.Disabled", uid);
            if (!cfg.ForgeAtWorkbench) return L("Papers.ForgeAtFence", uid); // the fence sells them (owner's call, 2026-09-14)
            if (bp.currentCraftLevel < cfg.ForgeWorkbenchLevel) return L("Papers.ForgeNoBench", uid, cfg.ForgeWorkbenchLevel);
            if (bp.inventory == null) return L("Papers.ForgeNoItems", uid, RecipeText(uid));
            var missing = new StringBuilder();
            foreach (var kv in cfg.ForgeRecipe)
            {
                var def = ItemManager.FindItemDefinition(kv.Key);
                if (def == null) continue;
                var have = bp.inventory.GetAmount(def.itemid);
                if (have < kv.Value) { if (missing.Length > 0) missing.Append(", "); missing.Append(def.displayName.english).Append(" x").Append(kv.Value - have); }
            }
            if (missing.Length > 0) return L("Papers.ForgeNoItems", uid, missing.ToString());
            foreach (var kv in cfg.ForgeRecipe) TakeItems(bp, kv.Key, kv.Value);
            var item = CreateId(id, bp.displayName, true, false);
            GiveOrDrop(bp, item);
            var rec = item != null ? FindId(item) : null;
            CLog($"[papers] {bp.displayName} forged ID C-{(rec != null ? rec.Serial : 0)} at craft level {bp.currentCraftLevel:0} (tier {CurrentTier()}, {ForgeFailChance(CurrentTier())}% to be spotted).");
            return L("Papers.Forged", uid, RecipeText(uid));
        }

        // A dead checkpoint guard may carry papers in his own name — forged-grade for whoever takes
        // them (§11). The engine raises OnCorpsePopulate from NPCPlayer.CreateCorpse after the corpse's
        // containers are cleared and before the scientist loot is dealt, so the note goes into the
        // corpse's main container and is found by looting the body (1.0.15; a ground drop was too easy
        // to miss). Returning null lets the stock loot follow. Fires once per NPC death — not hot.
        private object OnCorpsePopulate(NPCPlayer npc, NPCPlayerCorpse corpse)
        {
            var guard = npc as GuardNpc;
            if (guard == null || guard.Passive || guard.Checkpoint == null || corpse == null) return null;
            LeaveStolenId(guard, corpse);
            return null;
        }

        private void LeaveStolenId(GuardNpc guard, NPCPlayerCorpse corpse)
        {
            var cfg = _config.Papers;
            if (!cfg.Enabled || cfg.StolenIdChance <= 0) return;
            if (UnityEngine.Random.value >= cfg.StolenIdChance) return;
            var item = CreateId(0, guard.GuardName, true, true);
            if (item == null) return;
            var main = corpse.containers != null && corpse.containers.Length > 0 ? corpse.containers[0] : null;
            var where = "on his corpse";
            if (main == null || !item.MoveToContainer(main))
            {
                item.Drop(corpse.transform.position + Vector3.up * 0.8f, Vector3.up * 0.5f);
                where = "beside his corpse (no room)";
            }
            var rec = FindId(item);
            CLog($"[papers] '{guard.GuardName}' left stolen papers C-{(rec != null ? rec.Serial : 0)} {where}.");
        }

        private static void CollectIds(ItemContainer c, List<Item> ids, PapersPlease plugin)
        {
            if (c == null) return;
            foreach (var item in c.itemList) if (plugin.FindId(item) != null) ids.Add(item);
        }

        // Runs at every halt before the verdict. Returns true when the encounter ended here (a
        // spotted forgery from ForgeWantedFromTier). Otherwise e.Band may have been softened by one.
        private bool ApplyPapers(Encounter e)
        {
            var cfg = _config.Papers;
            var bp = e.Player;
            if (!cfg.Enabled || bp == null || bp.inventory == null) return false;
            var ids = new List<Item>();
            CollectIds(bp.inventory.containerMain, ids, this);
            CollectIds(bp.inventory.containerBelt, ids, this);
            if (ids.Count == 0) return false;
            var uid = bp.UserIDString;
            var cp = e.Gate;
            Item best = null; IdRecord bestRec = null;
            var seized = new List<Item>(); var reasons = new List<string>(); var notYours = false;
            foreach (var it in ids)
            {
                var rec = FindId(it);
                if (rec.Revoked) { seized.Add(it); _ids.Ids.Remove(IdKey(it)); reasons.Add("revoked"); continue; }
                if (!rec.Stolen && rec.OwnerId != e.Id) { seized.Add(it); _ids.Ids.Remove(IdKey(it)); reasons.Add("not yours"); notYours = true; continue; }
                if (best == null || (bestRec.Forged && !rec.Forged)) { best = it; bestRec = rec; } // a legit one beats a forgery
            }
            if (seized.Count > 0)
            {
                _idsDirty = true;
                var n = Confiscate(seized);
                CLog($"[papers] '{cp.Name}' seized {n} document(s) from {bp.displayName} ({string.Join(", ", reasons.ToArray())}).");
                if (notYours) Adjust(e.Id, cfg.NotYoursPenalty, "Reason.NotYours");
                e.IdNote = L("Papers.Id.Seized", uid, n, string.Join(", ", reasons.ToArray()));
            }
            if (best == null) return false;
            var seizedNote = e.IdNote; // kept in front of whatever the usable document earns (review 2026-09-16)
            if (bestRec.Forged)
            {
                var fail = ForgeFailChance(e.Tier);
                var roll = UnityEngine.Random.Range(0, 100);
                if (roll < fail)
                {
                    _ids.Ids.Remove(IdKey(best)); _idsDirty = true;
                    Confiscate(new List<Item> { best });
                    Nudge(cfg.NudgeForgery, "forgery spotted");
                    Voice(bp, _config.Voice.Forgery);
                    if (e.Tier >= cfg.ForgeWantedFromTier)
                    {
                        CLog($"[papers] '{cp.Name}' FORGERY: {bp.displayName} presented {IdKindText(bestRec)} papers C-{bestRec.Serial}; roll {roll} < {fail}% at tier {e.Tier} → Wanted.");
                        if (ReadScore(e.Id) > cfg.ForgeFailSetsScore) Adjust(e.Id, cfg.ForgeFailSetsScore, "Reason.Forgery", absolute: true);
                        e.Band = BandOfPlayer(e.Id);
                        e.IdNote = L("Papers.Id.Forgery", uid);
                        if (cfg.BroadcastForgery) Broadcast("Broadcast.Forgery", bp.displayName, cp.Name);
                        Hostile(e, "forged papers", L("Ui.Forgery", uid, Mathf.Max(10, _config.Checkpoints.HostileSeconds)));
                        return true;
                    }
                    CLog($"[papers] '{cp.Name}' forgery seized: {bp.displayName} presented {IdKindText(bestRec)} papers C-{bestRec.Serial}; roll {roll} < {fail}% at tier {e.Tier} → {cfg.ForgeFailPenalty}.");
                    Adjust(e.Id, cfg.ForgeFailPenalty, "Reason.Forgery");
                    e.Band = BandOfPlayer(e.Id);
                    e.IdNote = L("Papers.Id.ForgerySeized", uid);
                    return false;
                }
                CLog($"[papers] '{cp.Name}' {bp.displayName}'s {IdKindText(bestRec)} papers C-{bestRec.Serial} passed (roll {roll} >= {fail}% at tier {e.Tier}).");
            }
            var real = e.Band;
            var mode = LegitRevokeMode();
            // A legit ID whose owner is already an outlaw: in 'immediate' mode it lapsed the moment the
            // band fell (a document issued after that, or a mode switch, must not be a standing
            // discount); an Enemy's earns nothing in any mode but 'never'. Seized as revoked (review 2026-09-16).
            if (!bestRec.Forged && real >= Band.Wanted && (mode == "immediate" || (real == Band.Enemy && !cfg.IdUpgradesEnemy && mode != "never")))
            {
                _ids.Ids.Remove(IdKey(best)); _idsDirty = true;
                Confiscate(new List<Item> { best });
                e.IdNote = (seizedNote != null ? seizedNote + " " : "") + L("Papers.Id.Seized", uid, 1, "revoked");
                bp.ChatMessage(L("Notify.IdRevoked", uid));
                CLog($"[papers] '{cp.Name}' legit papers C-{bestRec.Serial} seized: {bp.displayName} is {real} ({mode}).");
                return false;
            }
            // One band up, but never past Neutral: an ID softens the verdict, it never removes the
            // search (observed 2026-09-14 — a Neutral's 20-scrap ID was a contraband pass below tier 4).
            if (real > Band.Neutral && (real != Band.Enemy || cfg.IdUpgradesEnemy)) e.Band = (Band)((int)real - 1);
            e.IdNote = (seizedNote != null ? seizedNote + " " : "") + (e.Band != real ? L("Papers.Id.Upgraded", uid, BandName(real, uid), BandName(e.Band, uid)) : L("Papers.Id.Valid", uid));
            CLog($"[papers] '{cp.Name}' {bp.displayName} holds {IdKindText(bestRec)} papers C-{bestRec.Serial}: {real} → {e.Band}.");
            // 'grace' (decision 0008 §8, owner's call 2026-09-14): a Wanted holder's legit papers count this
            // once, then the guards keep them — the next pass is the Wanted verdict.
            if (!bestRec.Forged && real >= Band.Wanted && mode == "grace")
            {
                _ids.Ids.Remove(IdKey(best)); _idsDirty = true;
                Confiscate(new List<Item> { best });
                e.IdNote += " " + L("Papers.Id.LastUse", uid);
                bp.ChatMessage(L("Notify.IdRevoked", uid));
                CLog($"[papers] '{cp.Name}' legit papers C-{bestRec.Serial} honoured one last time and seized ({bp.displayName} is {real}).");
            }
            return false;
        }

        private string LegitRevokeMode()
        {
            var m = (_config.Papers.LegitRevoke ?? "grace").Trim().ToLowerInvariant();
            return m == "immediate" || m == "never" ? m : "grace";
        }

        // The /papers line: what is actually in the player's main + belt (a scan looks there), with
        // "on file but not on you" when the registry knows an ID the pockets do not hold
        // (observed 2026-09-14: it said "you hold" after the note had been dropped).
        private string IdHeldLine(ulong id, string viewerId)
        {
            var cfg = _config.Papers;
            if (!cfg.Enabled) return "";
            IdRecord carriedLegit = null; var carriedForged = 0;
            var bp = BasePlayer.FindByID(id);
            if (bp != null && bp.inventory != null)
            {
                var carried = new List<Item>();
                CollectIds(bp.inventory.containerMain, carried, this);
                CollectIds(bp.inventory.containerBelt, carried, this);
                foreach (var it in carried)
                {
                    var r = FindId(it);
                    if (r == null || r.Revoked) continue;
                    if (!r.Forged && r.OwnerId == id) { if (carriedLegit == null || r.Serial > carriedLegit.Serial) carriedLegit = r; }
                    else if (r.Forged && (r.Stolen || r.OwnerId == id)) carriedForged++;
                }
            }
            if (carriedLegit != null) return L("Papers.IdHeld.Valid", viewerId, carriedLegit.Serial);
            if (carriedForged > 0) return L("Papers.IdHeld.Forged", viewerId, carriedForged, ForgeFailChance(CurrentTier()));
            IdRecord onFile = null;
            foreach (var kv in _ids.Ids)
            {
                var r = kv.Value;
                if (r.OwnerId == id && !r.Forged && !r.Revoked && (onFile == null || r.Serial > onFile.Serial)) onFile = r;
            }
            if (onFile != null) return L("Papers.IdHeld.NotOnYou", viewerId, onFile.Serial);
            return L("Papers.IdHeld.None", viewerId, cfg.IdFee);
        }

        [ConsoleCommand("papers.id")]
        private void CmdId(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            var viewer = caller?.UserIDString;
            var args = ConsoleArgs(arg);
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            ulong id = 0; string name = null;
            var wantsTarget = sub == "list" || sub == "issue" || sub == "forge" || sub == "revoke";
            if (wantsTarget && args.Length > 1 && !FindTarget(args[1], out id, out name)) { arg.ReplyWith(L("Papers.NoPlayer", viewer, args[1])); return; }
            switch (sub)
            {
                case "list":
                {
                    var sb = new StringBuilder($"ids: {_ids.Ids.Count} record(s), next serial C-{_ids.NextSerial}, tier {CurrentTier()} spots forgeries {ForgeFailChance(CurrentTier())}%");
                    var shown = 0;
                    foreach (var kv in _ids.Ids)
                    {
                        var r = kv.Value;
                        if (id != 0 && r.OwnerId != id) continue;
                        if (shown++ >= 40) { sb.Append("\n  …"); break; }
                        sb.Append('\n').Append("  ").Append(L("Id.ListLine", viewer, r.Serial, IdKindText(r), r.OwnerName + (r.OwnerId != 0 ? $" ({r.OwnerId})" : ""), $"issued {r.IssuedUtc:MM-dd HH:mm} UTC, uid {kv.Key}"));
                    }
                    arg.ReplyWith(sb.ToString());
                    return;
                }
                case "issue":
                case "forge":
                {
                    if (id == 0) { arg.ReplyWith(L("Id.Usage", viewer)); return; }
                    // A sleeper's inventory takes items too — the self-test driver issues to the tester's body.
                    var target = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
                    if (target == null) { arg.ReplyWith(L("Papers.NoPlayer", viewer, args[1])); return; }
                    var forged = sub == "forge";
                    if (!forged) RevokeLegitIds(id, "replaced by admin");
                    var item = CreateId(id, target.displayName, forged, false);
                    GiveOrDrop(target, item);
                    var rec = item != null ? FindId(item) : null;
                    CLog($"[papers] admin gave {target.displayName} a {(forged ? "forged" : "legit")} ID C-{(rec != null ? rec.Serial : 0)}.");
                    arg.ReplyWith(L("Id.Issued", viewer, target.displayName, forged ? "forged" : "legit", rec != null ? rec.Serial : 0));
                    return;
                }
                case "revoke":
                {
                    if (id == 0) { arg.ReplyWith(L("Id.Usage", viewer)); return; }
                    arg.ReplyWith(L("Id.Revoked", viewer, RevokeLegitIds(id, "admin"), name));
                    return;
                }
                case "wipe":
                {
                    // Test reset: forget every record; the notes out there become plain paper to a scan.
                    var n = _ids.Ids.Count;
                    _ids.Ids.Clear(); _ids.NextSerial = 1; _idsDirty = true; SaveIds(true);
                    CLog($"[papers] registry wiped by admin ({n} record(s)).");
                    arg.ReplyWith($"Wiped {n} ID record(s); serials restart at C-1.");
                    return;
                }
                default:
                    arg.ReplyWith(L("Id.Usage", viewer));
                    return;
            }
        }

        // ---- The fence: a passive NPC at Bandit Camp that strips the [COBALT] tag for scrap ----

        public class FenceTrigger : TriggerBase
        {
            public Fence Owner;
        }

        public class Fence
        {
            public string Name;
            public FenceTemplate Template;
            public MonumentInfo Monument;
            public Vector3 Position;
            public Quaternion Rotation;
            public GuardNpc Npc;
            public GameObject TriggerGo;
            public Timer RespawnTimer;
            public int SpawnFailures;
            public int Deals;
            public bool Alive => Npc != null && !Npc.IsDestroyed && !Npc.IsDead();
        }

        private readonly List<Fence> _fences = new List<Fence>();
        private readonly Dictionary<ulong, Fence> _fenceVisitors = new Dictionary<ulong, Fence>();

        private void SpawnAllFences()
        {
            foreach (var t in _config.Fence.Fences)
            {
                Vector3 local;
                if (t == null || string.IsNullOrEmpty(t.Name) || !TryParseVector(t.LocalPosition, out local)) continue;
                var monuments = MonumentsNamed(t.Monument);
                if (monuments.Count == 0) { CLog($"[fence] '{t.Name}': no monument named '{t.Monument}' on this map."); continue; }
                for (var i = 0; i < monuments.Count; i++)
                {
                    Vector3 pos; Quaternion rot;
                    MonumentToWorld(monuments[i], local, t.LocalYaw, out pos, out rot);
                    // The captured pose already carries the admin's feet height; SnapToGround's ray from
                    // +3 m hit a Bandit Camp roof and put the fence on it (live 2026-09-14). Keep the
                    // height, floored at the terrain so a map shift cannot bury him.
                    pos.y = Mathf.Max(pos.y, TerrainMeta.HeightMap.GetHeight(pos));
                    var f = new Fence { Name = monuments.Count == 1 ? t.Name : $"{t.Name}#{i + 1}", Template = t, Monument = monuments[i], Position = pos, Rotation = rot };
                    _fences.Add(f);
                    SpawnFence(f);
                }
            }
        }

        private void SpawnFence(Fence f)
        {
            var cfg = _config.Fence;
            f.RespawnTimer?.Destroy(); f.RespawnTimer = null;
            var npc = SpawnGuard(f.Position, f.Rotation * Vector3.forward, cfg.Heavy, null, true, cfg.Name);
            if (npc == null)
            {
                // Retry a few times a minute apart rather than staying gone until a reload (review 2026-09-16).
                var again = ++f.SpawnFailures <= 5;
                CLog($"[fence] '{f.Name}': spawn failed{(again ? "; retry in 60 s" : "; giving up until a reload")}.");
                if (again) f.RespawnTimer = timer.Once(60f, () => { f.RespawnTimer = null; if (_fences.Contains(f)) SpawnFence(f); });
                return;
            }
            f.SpawnFailures = 0;
            // After Spawn (ResetState copies the prefab's protection back): the fence takes no damage.
            if (cfg.Invulnerable) npc.baseProtection = ProtectionProperties.immortalProtection;
            DressFence(npc);
            f.Npc = npc;
            if (f.TriggerGo == null)
            {
                var go = new GameObject($"PapersPleaseFence:{f.Name}");
                go.layer = (int)global::Rust.Layer.Trigger;
                go.transform.position = f.Position + Vector3.up;
                var col = go.AddComponent<SphereCollider>();
                col.isTrigger = true;
                col.radius = Mathf.Max(2f, cfg.TriggerRadius);
                var trig = go.AddComponent<FenceTrigger>();
                trig.Owner = f;
                trig.InterestLayers = global::Rust.Layers.Mask.Player_Server;
                trig.OnEntityEnterTrigger = e => OnFenceEnter(f, e as BaseEntity);
                trig.OnEntityLeaveTrigger = e => OnFenceLeave(f, e as BaseEntity);
                f.TriggerGo = go;
            }
            CLog($"[fence] '{f.Name}' at {V(f.Position)} ({MonumentShortName(f.Monument)}): r={cfg.TriggerRadius:0} m, {cfg.ScrapPerStack} scrap per stack, InSafeZone={npc.InSafeZone()}.");
        }

        // The fence's look: config clothing ("shortname@skinid", the Island Taxi driver's recipe)
        // replaces the scientist suit, and an unarmed belt keeps him from aiming at customers.
        private void DressFence(GuardNpc npc)
        {
            var cfg = _config.Fence;
            if (npc == null || npc.inventory == null) return;
            if (cfg.Clothing != null && cfg.Clothing.Count > 0 && npc.inventory.containerWear != null)
            {
                var wear = npc.inventory.containerWear;
                foreach (var old in new List<Item>(wear.itemList)) { old.RemoveFromContainer(); old.Remove(); }
                foreach (var spec0 in cfg.Clothing)
                {
                    if (string.IsNullOrWhiteSpace(spec0)) continue;
                    var spec = spec0.Trim(); ulong skin = 0;
                    var at = spec.IndexOf('@');
                    if (at > 0) { ulong.TryParse(spec.Substring(at + 1), out skin); spec = spec.Substring(0, at); }
                    var item = ItemManager.CreateByName(spec, 1, skin);
                    if (item == null) { CLog($"[fence] clothing item '{spec0}' does not exist; skipped."); continue; }
                    if (!item.MoveToContainer(wear)) item.Remove();
                }
            }
            if (cfg.Unarmed && npc.inventory.containerBelt != null)
            {
                foreach (var old in new List<Item>(npc.inventory.containerBelt.itemList)) { old.RemoveFromContainer(); old.Remove(); }
            }
            npc.SendNetworkUpdate();
        }

        private void DespawnAllFences()
        {
            foreach (var f in _fences)
            {
                f.RespawnTimer?.Destroy(); f.RespawnTimer = null;
                if (f.Npc != null) { DespawnGuard(f.Npc); f.Npc = null; }
                if (f.TriggerGo != null) { UnityEngine.Object.Destroy(f.TriggerGo); f.TriggerGo = null; }
            }
            _fences.Clear();
            foreach (var kv in _fenceVisitors)
            {
                var p = BasePlayer.FindByID(kv.Key);
                if (p != null && p.IsConnected) CuiHelper.DestroyUi(p, UiFence);
            }
            _fenceVisitors.Clear();
        }

        private Fence FindFence(string name)
        {
            foreach (var f in _fences) if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        private void OnFenceDied(GuardNpc guard, ulong attacker)
        {
            Fence f = null;
            foreach (var x in _fences) if (x.Npc == guard) { f = x; break; }
            if (f == null) return;
            f.Npc = null;
            var minutes = Mathf.Max(1, _config.Fence.RespawnMinutes);
            CLog($"[fence] '{f.Name}' killed (killer {(attacker != 0 ? attacker.ToString() : "none")}); back in {minutes} min.");
            foreach (var kv in new List<KeyValuePair<ulong, Fence>>(_fenceVisitors))
                if (kv.Value == f) { _fenceVisitors.Remove(kv.Key); var p = BasePlayer.FindByID(kv.Key); if (p != null && p.IsConnected) CuiHelper.DestroyUi(p, UiFence); }
            f.RespawnTimer = timer.Once(minutes * 60f, () => { f.RespawnTimer = null; if (_fences.Contains(f)) SpawnFence(f); });
        }

        private void OnFenceEnter(Fence f, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null || !IsRealPlayer(bp) || !bp.IsConnected || bp.IsDead() || bp.IsSleeping()) return;
            if (!f.Alive) return; // nobody home
            _fenceVisitors[(ulong)bp.userID] = f;
            ShowFencePanel(bp, f, null);
        }

        private void OnFenceLeave(Fence f, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null) return;
            var id = (ulong)bp.userID;
            Fence cur;
            if (_fenceVisitors.TryGetValue(id, out cur) && cur == f)
            {
                _fenceVisitors.Remove(id);
                if (bp.IsConnected) CuiHelper.DestroyUi(bp, UiFence);
            }
        }

        // Stacks the fence cleans: everything tagged except scrap — tagged scrap is money here
        // (owner's call 2026-09-14: "Cobalt scrap cleans Cobalt gear, on par").
        private List<Item> TaggedStacks(BasePlayer bp)
        {
            var list = new List<Item>();
            if (bp.inventory == null) return list;
            // The bypass tool is tagged on purpose (contraband at a gate) and must not be "cleaned"
            // here: the fence would strip the tag off his own sale and the switch would refuse it
            // (review 2026-09-20).
            if (bp.inventory.containerMain != null) foreach (var it in bp.inventory.containerMain.itemList) if (IsTagged(it) && !IsScrap(it) && !IsBypassTool(it)) list.Add(it);
            if (bp.inventory.containerBelt != null) foreach (var it in bp.inventory.containerBelt.itemList) if (IsTagged(it) && !IsScrap(it) && !IsBypassTool(it)) list.Add(it);
            if (bp.inventory.containerWear != null) foreach (var it in bp.inventory.containerWear.itemList) if (IsTagged(it) && !IsScrap(it) && !IsBypassTool(it)) list.Add(it); // a tier-5 scan reads wear
            return list;
        }

        private static bool IsScrap(Item it) => it != null && it.info != null && it.info.shortname == "scrap";

        // The fence takes tagged scrap first, at par, then clean scrap for the rest. Returns what was
        // actually taken; the callers refuse the deal on a shortfall (review 2026-09-16).
        private int FencePay(BasePlayer bp, int amount)
        {
            if (amount <= 0) return 0;
            if (bp.inventory == null) return 0;
            var tagged = new List<Item>();
            if (bp.inventory.containerMain != null) foreach (var it in bp.inventory.containerMain.itemList) if (IsScrap(it) && IsTagged(it)) tagged.Add(it);
            if (bp.inventory.containerBelt != null) foreach (var it in bp.inventory.containerBelt.itemList) if (IsScrap(it) && IsTagged(it)) tagged.Add(it);
            var left = amount; var fromTagged = 0;
            foreach (var it in tagged)
            {
                if (left <= 0) break;
                var take = Mathf.Min(it.amount, left);
                left -= take; fromTagged += take;
                if (take >= it.amount) { it.RemoveFromContainer(); it.Remove(); }
                else { it.amount -= take; it.MarkDirty(); }
            }
            var fromClean = left > 0 ? TakeItems(bp, "scrap", left) : 0;
            if (fromTagged > 0) CLog($"[fence] {bp.displayName} paid {fromTagged} of {amount} scrap in Cobalt scrap.");
            if (fromTagged + fromClean < amount) CLog($"[fence] {bp.displayName} paid {fromTagged + fromClean} of {amount} scrap — short; deal refused.");
            return fromTagged + fromClean;
        }

        // An Enemy of the State is too hot for the grey market (owner's call, 2026-09-14): no trade at all.
        private bool FenceRefuses(BasePlayer bp) =>
            !_config.Fence.ServeEnemies && BandOfPlayer((ulong)bp.userID) == Band.Enemy;

        private void ShowFencePanel(BasePlayer bp, Fence f, string result)
        {
            var cfg = _config.Fence; var uid = bp.UserIDString;
            if (FenceRefuses(bp))
            {
                var leave = new List<UiButton> { new UiButton { Label = L("Fence.BtnClose", uid), Command = "papers.fence.close", Color = "0.3 0.3 0.3 1" } };
                OpenPanel(bp, UiFence, L("Fence.Title", uid), L("Fence.Header", uid, MonumentDisplay(f.Monument), f.Name) + "\n" + L("Ui.Subject", uid, bp.displayName, BandName(Band.Enemy, uid)), L("Fence.TooHot", uid), leave, true, "0.9 0.8 0.55 1");
                return;
            }
            var stacks = TaggedStacks(bp);
            var price = stacks.Count * Mathf.Max(0, cfg.ScrapPerStack);
            var have = ScrapCount(bp);
            var buttons = new List<UiButton>();
            string body;
            if (result != null) body = result;
            else if (stacks.Count == 0) body = L("Fence.NoTagged", uid);
            else if (have < price) body = L("Fence.NoScrap", uid, stacks.Count, price, have, price - have);
            else
            {
                body = L("Fence.Offer", uid, stacks.Count, price, have, Summarize(stacks, uid));
                buttons.Add(new UiButton { Label = L("Fence.BtnLaunder", uid, price), Command = "papers.fence.launder", Color = "0.45 0.35 0.15 1" });
            }
            // Forged papers for sale (owner's call 2026-09-14: Suspects and Wanted get their ID here).
            var forgePrice = _config.Papers.Enabled ? Mathf.Max(0, cfg.ForgedIdScrap) : 0;
            if (forgePrice > 0 && result == null)
            {
                body += "\n" + L("Fence.ForgeLine", uid, forgePrice, ForgeFailChance(CurrentTier()));
                if (have >= forgePrice) buttons.Add(new UiButton { Label = L("Fence.BtnForge", uid, forgePrice), Command = "papers.fence.forge", Color = "0.35 0.25 0.45 1" });
            }
            // The bypass tool (decision 0009 §B as rewritten 2026-09-20): the way into the Outpost vault.
            var toolPrice = ToolOnSale() ? Mathf.Max(0, _config.Sabotage.ToolScrap) : 0;
            if (toolPrice > 0 && result == null)
            {
                int guards, turrets; OutpostDefenders(out guards, out turrets);
                body += "\n" + L("Fence.ToolLine", uid, toolPrice, _config.Sabotage.WindowMinutes, guards, turrets, SwitchGrid());
                if (have >= toolPrice) buttons.Add(new UiButton { Label = L("Fence.BtnTool", uid, toolPrice), Command = "papers.fence.tool", Color = "0.45 0.2 0.2 1" });
            }
            buttons.Add(new UiButton { Label = L("Fence.BtnClose", uid), Command = "papers.fence.close", Color = "0.3 0.3 0.3 1" });
            var header = L("Fence.Header", uid, MonumentDisplay(f.Monument), f.Name) + "\n" + L("Ui.Subject", uid, bp.displayName, BandName(BandOfPlayer((ulong)bp.userID), uid));
            OpenPanel(bp, UiFence, L("Fence.Title", uid), header, body, buttons, true, "0.9 0.8 0.55 1");
        }

        // The checkpoint panel's layout for any other Cobalt-style notice.
        private void OpenPanel(BasePlayer bp, string panel, string title, string header, string body, List<UiButton> buttons, bool cursor, string titleColor)
        {
            if (bp == null || !bp.IsConnected) return;
            CuiHelper.DestroyUi(bp, panel);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.06 0.07 0.09 0.96" },
                RectTransform = { AnchorMin = "0.32 0.30", AnchorMax = "0.68 0.70" },
                CursorEnabled = cursor
            }, "Overlay", panel);
            ui.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 24, Align = TextAnchor.MiddleCenter, Color = titleColor },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 0.98" }
            }, panel);
            ui.Add(new CuiLabel
            {
                Text = { Text = header, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.04 0.72", AnchorMax = "0.96 0.86" }
            }, panel);
            // A long body (the fence with cleaning + papers + the tool on offer, live 2026-09-20)
            // ran out of the box: use the whole band between the header and the buttons and
            // step the font down as the text grows.
            var bodyLen = body?.Length ?? 0;
            ui.Add(new CuiLabel
            {
                Text = { Text = body, FontSize = bodyLen > 520 ? 11 : bodyLen > 380 ? 12 : bodyLen > 260 ? 13 : 14, Align = TextAnchor.MiddleCenter, Color = "0.88 0.88 0.88 1" },
                RectTransform = { AnchorMin = "0.04 0.26", AnchorMax = "0.96 0.72" }
            }, panel);
            if (buttons != null && buttons.Count > 0)
            {
                var w = 0.84f / buttons.Count;
                for (var i = 0; i < buttons.Count; i++)
                {
                    var x0 = 0.08f + i * w + 0.02f; var x1 = 0.08f + (i + 1) * w - 0.02f;
                    ui.Add(new CuiButton
                    {
                        Button = { Command = buttons[i].Command, Color = buttons[i].Color },
                        RectTransform = { AnchorMin = $"{x0:0.###} 0.08", AnchorMax = $"{x1:0.###} 0.24" },
                        Text = { Text = buttons[i].Label, FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                    }, panel);
                }
            }
            CuiHelper.AddUi(bp, ui);
        }

        // Fired by the fence panel's buttons as the player; only touches the caller's own visit.
        [ConsoleCommand("papers.fence.launder")]
        private void CmdFenceLaunder(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp == null) return;
            Fence f;
            if (!_fenceVisitors.TryGetValue((ulong)bp.userID, out f) || !f.Alive) return;
            if (Vector3.Distance(bp.transform.position, f.Position) > _config.Fence.TriggerRadius + 3f) return;
            if (FenceRefuses(bp)) { ShowFencePanel(bp, f, null); return; }
            var cfg = _config.Fence; var uid = bp.UserIDString;
            var stacks = TaggedStacks(bp);
            var price = stacks.Count * Mathf.Max(0, cfg.ScrapPerStack);
            if (stacks.Count == 0 || ScrapCount(bp) < price) { ShowFencePanel(bp, f, null); return; }
            if (FencePay(bp, price) < price) { ShowFencePanel(bp, f, null); return; }
            foreach (var it in stacks) { it.name = StripTag(it); it.MarkDirty(); }
            f.Deals++;
            Nudge(cfg.NudgeLaundered, "laundered");
            CLog($"[fence] '{f.Name}' laundered {stacks.Count} stack(s) for {price} scrap from {bp.displayName}.");
            Voice(bp, _config.Voice.Fence);
            ShowFencePanel(bp, f, L("Fence.Done", uid, stacks.Count, price));
        }

        [ConsoleCommand("papers.fence.close")]
        private void CmdFenceClose(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp != null) CuiHelper.DestroyUi(bp, UiFence);
        }

        // The fence's other trade: forged papers for scrap, to anyone (decision 0008 §9 as amended 2026-09-14).
        // On sale while the chain is armed and an Outpost vault stands.
        private bool ToolOnSale()
        {
            if (!SabotageArmed || _config.Sabotage.ToolScrap <= 0 || _sabSwitch == null || _sabSwitch.IsDestroyed) return false;
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Vault && cp.IsActive && AtOutpost(cp)) return true;
            return false;
        }

        private bool IsBypassTool(Item item) =>
            item != null && item.info != null && item.info.shortname == _config.Sabotage.ToolItem && IsTagged(item) && !string.IsNullOrEmpty(item.name) && item.name.StartsWith(_config.Sabotage.ToolName);

        private Item FindBypassTool(BasePlayer bp)
        {
            if (bp == null || bp.inventory == null) return null;
            var active = bp.GetActiveItem();
            if (IsBypassTool(active)) return active;
            if (bp.inventory.containerBelt != null) foreach (var it in bp.inventory.containerBelt.itemList) if (IsBypassTool(it)) return it;
            if (bp.inventory.containerMain != null) foreach (var it in bp.inventory.containerMain.itemList) if (IsBypassTool(it)) return it;
            return null;
        }

        // The map grid of the switch's substation (owner's call 2026-09-20: the fence has to say where).
        private string SwitchGrid() => _sabSwitch != null && !_sabSwitch.IsDestroyed ? MapHelper.PositionToString(_sabSwitchPos) : "?";

        [ConsoleCommand("papers.fence.tool")]
        private void CmdFenceTool(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp == null) return;
            Fence f;
            if (!_fenceVisitors.TryGetValue((ulong)bp.userID, out f) || !f.Alive) return;
            if (Vector3.Distance(bp.transform.position, f.Position) > _config.Fence.TriggerRadius + 3f) return;
            if (FenceRefuses(bp)) { ShowFencePanel(bp, f, null); return; }
            var cfg = _config.Sabotage; var price = Mathf.Max(0, cfg.ToolScrap); var uid = bp.UserIDString;
            if (!ToolOnSale() || ScrapCount(bp) < price) { ShowFencePanel(bp, f, null); return; }
            var item = ItemManager.CreateByName(cfg.ToolItem, 1);
            if (item == null) { CLog($"[fence] the bypass tool item '{cfg.ToolItem}' does not exist."); ShowFencePanel(bp, f, null); return; }
            item.name = cfg.ToolName + _config.Contraband.Tag; // tagged: contraband at every gate on the way
            item.MarkDirty();
            if (FencePay(bp, price) < price) { item.Remove(); ShowFencePanel(bp, f, null); return; }
            GiveOrDrop(bp, item);
            f.Deals++;
            Nudge(_config.Fence.NudgeLaundered, "bypass tool sold");
            CLog($"[fence] '{f.Name}' sold a bypass tool to {bp.displayName} ({BandOfPlayer((ulong)bp.userID)}) for {price} scrap.");
            Voice(bp, _config.Voice.FenceTool);
            ShowFencePanel(bp, f, L("Fence.ToolDone", uid, price, cfg.WindowMinutes, SwitchGrid()));
        }

        [ConsoleCommand("papers.fence.forge")]
        private void CmdFenceForge(ConsoleSystem.Arg arg)
        {
            var bp = arg.Player();
            if (bp == null) return;
            Fence f;
            if (!_fenceVisitors.TryGetValue((ulong)bp.userID, out f) || !f.Alive) return;
            if (Vector3.Distance(bp.transform.position, f.Position) > _config.Fence.TriggerRadius + 3f) return;
            if (FenceRefuses(bp)) { ShowFencePanel(bp, f, null); return; }
            var price = Mathf.Max(0, _config.Fence.ForgedIdScrap); var uid = bp.UserIDString;
            if (!_config.Papers.Enabled || price <= 0 || ScrapCount(bp) < price) { ShowFencePanel(bp, f, null); return; }
            // The note first, the scrap only once it exists (review 2026-09-16).
            var item = CreateId((ulong)bp.userID, bp.displayName, true, false);
            if (item == null) { ShowFencePanel(bp, f, null); return; }
            if (FencePay(bp, price) < price) { _ids.Ids.Remove(IdKey(item)); _idsDirty = true; item.Remove(); ShowFencePanel(bp, f, null); return; }
            GiveOrDrop(bp, item);
            var rec = FindId(item);
            f.Deals++;
            Nudge(_config.Fence.NudgeLaundered, "forged papers sold");
            CLog($"[fence] '{f.Name}' sold forged papers C-{(rec != null ? rec.Serial : 0)} to {bp.displayName} ({BandOfPlayer((ulong)bp.userID)}) for {price} scrap; {ForgeFailChance(CurrentTier())}% to be spotted at tier {CurrentTier()}.");
            Voice(bp, _config.Voice.FenceForge);
            ShowFencePanel(bp, f, L("Fence.ForgeDone", uid, price, ForgeFailChance(CurrentTier())));
        }

        private string DescribeFences()
        {
            if (_config.Fence.Fences.Count == 0) return L("Fence.ListNone", null);
            var sb = new StringBuilder($"fences: {_fences.Count} placed, {_config.Fence.ScrapPerStack} scrap per stack, r={_config.Fence.TriggerRadius:0} m");
            foreach (var f in _fences)
            {
                var status = f.Alive ? $"alive, {f.Deals} deal(s)" : f.RespawnTimer != null ? "dead, respawn pending" : "not spawned";
                sb.Append('\n').Append("  ").Append(L("Fence.ListLine", null, f.Name, V(f.Position), MonumentShortName(f.Monument), status));
            }
            return sb.ToString();
        }

        private void RespawnFences()
        {
            DespawnAllFences();
            SpawnAllFences();
        }

        // /papers fence add [name] [monument] | remove <name> | tp <name> | list — `player` may be null from the console.
        private string RunFence(string[] args, int first, BasePlayer player, string viewerId)
        {
            var sub = args.Length > first ? args[first].ToLowerInvariant() : "list";
            var name = args.Length > first + 1 ? args[first + 1] : null;
            switch (sub)
            {
                case "list":
                case "status":
                    return DescribeFences();
                case "add":
                {
                    if (player == null) return L("Fence.Usage", viewerId);
                    name = string.IsNullOrEmpty(name) ? "fence" : name;
                    if (!IsGateName(name)) return L("Fence.Usage", viewerId);
                    foreach (var t in _config.Fence.Fences) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return L("Fence.Exists", viewerId, name);
                    var needle = args.Length > first + 2 ? args[first + 2] : null;
                    float dist; string alternatives;
                    var m = NearestMonument(player.transform.position, needle, out dist, out alternatives);
                    if (m == null) return L("Gate.NoMonument", viewerId);
                    var pos = player.transform.position;
                    var yaw = YawOf(player.eyes.BodyForward());
                    Vector3 local; float localYaw;
                    WorldToMonument(m, pos, yaw, out local, out localYaw);
                    var ft = new FenceTemplate { Name = name, Monument = MonumentShortName(m), LocalPosition = VecText(local), LocalYaw = (float)Math.Round(localYaw, 1) };
                    _config.Fence.Fences.Add(ft);
                    SaveConfig();
                    CLog($"[fence] '{name}' captured at {V(pos)} yaw {yaw:0} → monument '{ft.Monument}' local {ft.LocalPosition} yaw {ft.LocalYaw:0}; {dist:0} m from the monument origin.");
                    RespawnFences();
                    return L("Fence.Added", viewerId, name, ft.Monument, ft.LocalPosition, ft.LocalYaw, dist);
                }
                case "at":
                {
                    // Self-test / console placement: papers.fence at <x> <y> <z> [yaw] [name] — the nearest monument owns it.
                    float x, y, z, yaw = 0f;
                    if (args.Length < first + 4 || !float.TryParse(args[first + 1], out x) || !float.TryParse(args[first + 2], out y) || !float.TryParse(args[first + 3], out z)
                        || (args.Length > first + 4 && !float.TryParse(args[first + 4], out yaw)))
                        return "papers.fence at <x> <y> <z> [yaw] [name]";
                    var atName = args.Length > first + 5 ? args[first + 5] : "fence";
                    if (!IsGateName(atName)) return L("Fence.Usage", viewerId);
                    foreach (var t in _config.Fence.Fences) if (string.Equals(t.Name, atName, StringComparison.OrdinalIgnoreCase)) return L("Fence.Exists", viewerId, atName);
                    float dist; string alternatives;
                    var pos = new Vector3(x, y, z);
                    var m = NearestMonument(pos, null, out dist, out alternatives);
                    if (m == null) return L("Gate.NoMonument", viewerId);
                    Vector3 local; float localYaw;
                    WorldToMonument(m, pos, yaw, out local, out localYaw);
                    var ft = new FenceTemplate { Name = atName, Monument = MonumentShortName(m), LocalPosition = VecText(local), LocalYaw = (float)Math.Round(localYaw, 1) };
                    _config.Fence.Fences.Add(ft);
                    SaveConfig();
                    CLog($"[fence] '{atName}' placed by console at {V(pos)} yaw {yaw:0} → monument '{ft.Monument}' local {ft.LocalPosition}; {dist:0} m from the monument origin.");
                    RespawnFences();
                    return L("Fence.Added", viewerId, atName, ft.Monument, ft.LocalPosition, ft.LocalYaw, dist);
                }
                case "remove":
                {
                    if (string.IsNullOrEmpty(name)) return L("Fence.Usage", viewerId);
                    var idx = _config.Fence.Fences.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) return L("Fence.NotFound", viewerId, name);
                    _config.Fence.Fences.RemoveAt(idx);
                    SaveConfig();
                    RespawnFences();
                    return L("Fence.Removed", viewerId, name);
                }
                case "tp":
                {
                    if (player == null || string.IsNullOrEmpty(name)) return L("Fence.Usage", viewerId);
                    var f = FindFence(name);
                    if (f == null) return L("Fence.NotFound", viewerId, name);
                    // No ground ray: Bandit's roofs catch it (the 1.0.10 fence fix); the fence's own height is right.
                    var spot = f.Position + f.Rotation * Vector3.forward * 3f;
                    spot.y = Mathf.Max(f.Position.y, TerrainMeta.HeightMap.GetHeight(spot)) + 0.5f;
                    player.Teleport(spot);
                    return L("Fence.Teleported", viewerId, f.Name);
                }
                default:
                    return L("Fence.Usage", viewerId);
            }
        }

        [ConsoleCommand("papers.fence")]
        private void CmdFence(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            arg.ReplyWith(RunFence(ConsoleArgs(arg), 0, caller, caller?.UserIDString));
        }

        #endregion

        #region Milestone 8 probe hooks (task 8.1 — decision 0009)

        // The engine asks before every hand on a code lock (CodeLock.OnTryToOpen/Close). Only our own
        // loose probe door answers here; a returned bool is the verdict. Admins open it, anyone else is
        // refused and logged — the vault rule (Citizen + legit ID) arrives with task 8.3.
        private object CanUseLockedEntity(BasePlayer player, BaseLock baseLock)
        {
            if (player == null || baseLock == null) return null;
            var parent = baseLock.GetParentEntity();
            if (parent == null) return null;
            // Task 8.3 (decision 0009 §2): a vault door opens for a player whose band is in OpenBands
            // and who carries a legit Cobalt ID in their own name. OpenBands is empty by default since
            // 1.4.1 (§2′, owner 2026-09-27): a vault has to be breached; the band rule stays as an option. Everyone else — no papers, forged or
            // stolen papers, the wrong band, admins included — is refused with a line and no keypad;
            // admins open it with papers.vault open. A returned bool ends the engine's own check.
            var vault = VaultOfDoor(parent);
            if (vault != null)
            {
                var id = (ulong)player.userID;
                var band = BandOfPlayer(id);
                var held = CarriedLegitId(player);
                var bandOk = false;
                foreach (var b in _config.Vaults.OpenBands) if (string.Equals(b, band.ToString(), StringComparison.OrdinalIgnoreCase)) { bandOk = true; break; }
                if (held != null && bandOk)
                {
                    CLog($"[vault] '{vault.Name}' opened for {player.displayName} ({band}, papers C-{held.Serial}).");
                    player.ChatMessage(L("Vault.Opened", player.UserIDString));
                    return true;
                }
                CLog($"[vault] '{vault.Name}' refused {player.displayName} ({band}, {(held != null ? $"papers C-{held.Serial} but the band" : "no legit papers")}).");
                player.ChatMessage(L("Vault.Refused", player.UserIDString));
                return false;
            }
            if (!_looseProps.Contains(parent)) return null;
            var admin = IsAdmin(player);
            CLog($"[probe] CanUseLockedEntity: {player.displayName} on the probe door (hp {parent.Health():0}/{parent.MaxHealth():0}) → {(admin ? "opened (admin)" : "refused")}.");
            if (!admin) player.ChatMessage(L("Vault.Refused", player.UserIDString));
            return admin;
        }

        // A vault door left open is an open vault: it closes itself 10 s after opening (Door.CloseRequest).
        private void OnDoorOpened(Door door, BasePlayer player)
        {
            if (door == null || VaultOfDoor(door) == null) return;
            door.Invoke(door.CloseRequest, 10f);
        }

        // Fires before the flip: the sabotage switch runs the window rule; a loose probe switch is logged; every other switch is ignored.
        private void OnSwitchToggle(ElectricSwitch sw, BasePlayer player)
        {
            if (sw == null || player == null) return;
            if (sw == _sabSwitch) { OnPowerCut(player); return; }
            if (!_looseProps.Contains(sw)) return;
            CLog($"[probe] OnSwitchToggle: {player.displayName} flipped the probe switch (was {(sw.IsOn() ? "on" : "off")}).");
        }

        private void OnEntityMounted(BaseMountable mountable, BasePlayer player)
        {
            if (mountable == null || player == null || !_looseProps.Contains(mountable)) return;
            CLog($"[probe] OnEntityMounted: {player.displayName} at the probe terminal.");
        }

        private void OnEntityDismounted(BaseMountable mountable, BasePlayer player)
        {
            if (mountable == null || player == null || !_looseProps.Contains(mountable)) return;
            CLog($"[probe] OnEntityDismounted: {player.displayName} left the probe terminal (alive={(player.IsAlive() ? "yes" : "no")}).");
        }

        // Kill() skips OnEntityDeath (stability, ground watch, decay gib): log it for loose props so a
        // silent removal never hides again. Returns at once unless the probe has something out.
        private void OnEntityKill(BaseNetworkable entity)
        {
            var e = entity as BaseEntity;
            if (e == null) return;
            var crate = e as LootContainer;
            if (crate != null)
            {
                var cp = VaultOfCrate(crate);
                if (cp != null) { OnVaultCrateGone(cp, crate); return; }
            }
            // A vault door removed without a death (an admin's `ent kill`, another plugin): no
            // penalty, but Cobalt still replaces it (review 2026-09-20). A breach clears cp.Door
            // before killing it, and the plugin's own despawn runs under Despawning.
            var door = e as Door;
            if (door != null)
            {
                var cp = VaultOfDoor(door);
                if (cp != null && cp.Door == door)
                {
                    cp.Door = null; cp.Lock = null; cp.Props.Remove(door); cp.PropKinds.Remove(door);
                    if (!cp.Despawning && cp.State == CheckpointState.Active && cp.DoorTimer == null)
                    {
                        ScheduleDoorRebuild(cp, DateTime.UtcNow.AddMinutes(Mathf.Max(1, _config.Vaults.DoorRebuildMinutes)), "door removed");
                        CLog($"[vault] '{cp.Name}' door removed by Kill() (no attacker); door back in {_config.Vaults.DoorRebuildMinutes} min.");
                    }
                    return;
                }
            }
            if (_looseProps.Count == 0 || !_looseProps.Contains(e)) return;
            CLog($"[probe] loose {e.ShortPrefabName} removed by Kill() at {V(e.transform.position)} (hp {e.Health():0}) — no death event.");
        }

        #endregion

        #region Vaults (task 8.2 — decision 0009 §A: a locked Cobalt door in a monument doorway)

        private int VaultGuardCount(int tier)
        {
            var byTier = _config.Vaults.GuardsByTier;
            if (byTier == null || byTier.Length == 0) return 2;
            return Mathf.Clamp(byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1], 0, 8);
        }

        private int GuardsFor(Checkpoint cp, int tier) =>
            cp.Kind == CheckpointKind.Vault ? VaultGuardCount(tier) : cp.GuardCount(_config.Checkpoints, tier);

        private int VaultMinTier(VaultTemplate v) => v.MinTier > 0 ? v.MinTier : Mathf.Max(1, _config.Vaults.VaultFromTier);

        // One checkpoint per monument occurrence, like a gate. The synthetic GateTemplate keeps the
        // shared lifecycle (tier gating, cap, listing) working without a second code path.
        private List<Checkpoint> ResolveVault(VaultTemplate v)
        {
            var result = new List<Checkpoint>();
            Vector3 local;
            if (v == null || string.IsNullOrEmpty(v.Name) || !TryParseVector(v.DoorLocalPosition, out local)) return result;
            var monuments = MonumentsNamed(v.Monument);
            if (monuments.Count == 0) { CLog($"[vault] '{v.Name}': no monument named '{v.Monument}' on this map."); return result; }
            var synth = new GateTemplate { Name = v.Name, Monument = v.Monument, LocalPosition = v.DoorLocalPosition, LocalYaw = v.DoorLocalYaw, MinTier = VaultMinTier(v), Props = new List<PropTemplate>() };
            for (var i = 0; i < monuments.Count; i++)
            {
                Vector3 pos; Quaternion rot;
                MonumentToWorld(monuments[i], local, v.DoorLocalYaw, out pos, out rot);
                // The captured pose carries the doorway's floor height; no ground snap (Bandit's roofs, 1.0.10).
                pos.y = Mathf.Max(pos.y, TerrainMeta.HeightMap.GetHeight(pos));
                result.Add(new Checkpoint { Name = monuments.Count == 1 ? v.Name : $"{v.Name}#{i + 1}", Kind = CheckpointKind.Vault, Template = synth, Vault = v, Monument = monuments[i], Position = pos, Rotation = rot });
            }
            return result;
        }

        private void SpawnVault(Checkpoint cp)
        {
            var tier = CurrentTier();
            cp.PlacedAt = Time.realtimeSinceStartup;
            cp.SpawnedGuards = VaultGuardCount(tier);
            cp.SpawnedTier = tier;
            if (_config.Vaults.Room) SpawnVaultRoom(cp); else RemoveMonumentDoor(cp);
            SpawnVaultGuards(cp);
            // Owner's call 2026-09-20: from VaultTurretsFromTier the vault has its own sentries, just
            // outside the room's front corners, facing the approach (SpawnProp applies the TurretCap).
            // A tier change re-spawns the vault (the guard count differs between 4 and 5), so they
            // come and go with the tier like the guards.
            if (tier >= _config.Vaults.VaultTurretsFromTier)
            {
                var turrets = Mathf.Clamp(_config.Vaults.VaultTurrets, 0, 4);
                for (var i = 0; i < turrets; i++)
                {
                    var side = (i % 2 == 0) ? 1f : -1f;
                    var tp = SnapToGround(cp.Position + cp.Forward * 1f + cp.Right * (2.5f + 1.5f * (i / 2)) * side);
                    SpawnProp(cp, "turret", tp, cp.Rotation);
                }
            }
            // A breached door and an emptied vault stay that way across a reload or restart (decision 0009 §6).
            VaultState st; _cpData.Vaults.TryGetValue(cp.Name, out st);
            if (st != null && st.DoorRebuildAtUtc > DateTime.UtcNow) ScheduleDoorRebuild(cp, st.DoorRebuildAtUtc, "restored"); else SpawnVaultDoor(cp);
            if (st != null && st.RestockAtUtc > DateTime.UtcNow) ScheduleRestock(cp, st.RestockAtUtc, "restored"); else SpawnVaultCrates(cp);
            cp.State = CheckpointState.Active;
            cp.StateNote = "";
            cp.DestroyedUntilUtc = default(DateTime);
            if (_cpData.DestroyedUntil.Remove(cp.Name)) _cpDirty = true;
            timer.Once(1f, () =>
            {
                if (cp.State != CheckpointState.Active) return;
                if (cp.Guards.Count > 0 && cp.Guards[0] != null && !cp.Guards[0].IsDestroyed) cp.InSafeZone = cp.Guards[0].InSafeZone();
                CLog($"[vault] '{cp.Name}' at {V(cp.Position)}: {(_config.Vaults.Room ? "room, " : "")}door {(cp.Door != null && !cp.Door.IsDestroyed ? $"{cp.Door.Health():0} hp, locked={(cp.Lock != null && !cp.Lock.IsDestroyed && cp.Lock.IsLocked())}" : "missing")}, {cp.Crates.Count} crate(s), {cp.LiveGuards()} guard(s), surface '{SurfaceOf(cp)}', InSafeZone={cp.InSafeZone}.");
            });
        }

        // Guard posts from the template (monument-local, where the admin stood), else flanking the
        // door 2.5 m out on the approach side. Guards face the approach like a gate's.
        private void SpawnVaultGuards(Checkpoint cp)
        {
            var tier = CurrentTier();
            var count = VaultGuardCount(tier);
            var posts = new List<Vector3>();
            foreach (var text in cp.Vault.GuardLocalPositions)
            {
                Vector3 l; if (!TryParseVector(text, out l)) continue;
                Vector3 w; Quaternion r; MonumentToWorld(cp.Monument, l, 0f, out w, out r);
                posts.Add(w);
            }
            for (var i = 0; i < count; i++)
            {
                Vector3 gp;
                if (i < posts.Count) gp = posts[i];
                else
                {
                    var side = (i % 2 == 0) ? 1f : -1f;
                    gp = SnapToGround(cp.Position + cp.Forward * 2.5f + cp.Right * (1.5f + 1.5f * (i / 2)) * side);
                }
                var heavy = tier >= _config.Vaults.VaultHeavyFromTier; // owner's call 2026-09-20: every vault guard is a heavy from tier 4
                var g = SpawnGuard(gp, cp.Forward, heavy, cp);
                if (g == null) continue;
                cp.Guards.Add(g);
                var brain = g.GetComponent<GuardBrain>();
                if (brain != null) cp.Surface = brain.Surface;
            }
        }

        // The last guard fell: the door and the crates stay, Cobalt sends new guards after the
        // tier's respawn time (or retries in 5 min under the cap). Nothing persisted — a restart
        // re-mans at once.
        private void ScheduleVaultGuards(Checkpoint cp, int minutes)
        {
            cp.RespawnTimer?.Destroy();
            CLog($"[vault] '{cp.Name}' guards down; new guards in {minutes} min.");
            cp.RespawnTimer = timer.Once(Mathf.Max(1, minutes) * 60f, () =>
            {
                cp.RespawnTimer = null;
                if (cp.State != CheckpointState.Active || cp.LiveGuards() > 0) return;
                if (LiveGuardCount() + VaultGuardCount(CurrentTier()) > _config.Checkpoints.GuardCap) { ScheduleVaultGuards(cp, 5); return; }
                SpawnVaultGuards(cp);
                CLog($"[vault] '{cp.Name}' re-manned: {cp.LiveGuards()} guard(s).");
            });
        }

        // Door : AnimatedBuildingBlock : StabilityEntity — grounded (support 1) or the spawn-tick
        // stability check gibs it; decay off (no cupboard); quarter-turn (panel along local X);
        // ground watch stripped from children; the code lock in the lock slot, locked, random code
        // nobody is told: the plugin answers CanUseLockedEntity (task 8.3). Probe 2026-09-18.
        private void SpawnVaultDoor(Checkpoint cp)
        {
            var ent = GameManager.server.CreateEntity(VaultDoorPrefab, cp.Position, cp.Rotation * Quaternion.Euler(0f, 90f, 0f));
            var door = ent as Door;
            if (door == null) { CLog($"[vault] '{cp.Name}': CreateEntity returned {(ent == null ? "null" : ent.GetType().Name)} for the door."); if (ent != null) ent.Kill(); return; }
            door.enableSaving = false;
            foreach (var c in door.GetComponentsInChildren<DestroyOnGroundMissing>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in door.GetComponentsInChildren<GroundWatch>(true)) UnityEngine.Object.DestroyImmediate(c);
            door.canNpcOpen = false;
            door.grounded = true;
            door.Spawn();
            door.decay = null; // after Spawn: DecayEntity.ServerInit re-reads the prefab's decay (as the blocks do; review 2026-09-20)
            cp.Props.Add(door); cp.PropKinds[door] = "door";
            cp.Door = door;
            var codeLock = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero, Quaternion.identity) as CodeLock;
            if (codeLock == null) { CLog($"[vault] '{cp.Name}': the code lock did not spawn; the door is unlocked."); return; }
            codeLock.enableSaving = false;
            codeLock.SetParent(door, door.GetSlotAnchorName(BaseEntity.Slot.Lock));
            codeLock.Spawn();
            door.SetSlot(BaseEntity.Slot.Lock, codeLock);
            codeLock.code = UnityEngine.Random.Range(1000, 9999).ToString();
            SetFlagNet(codeLock, BaseEntity.Flags.Locked, true);
            cp.Lock = codeLock;
        }

        // An elite crate per crate pose (default: one, 2 m behind the door). It is Cobalt property
        // for whoever loots it (decision 0009 §3); an emptied crate destroys itself — task 8.4 restocks.
        private void SpawnVaultCrates(Checkpoint cp)
        {
            var spots = new List<Vector3>();
            foreach (var text in cp.Vault.CrateLocalPositions)
            {
                Vector3 l; if (!TryParseVector(text, out l)) continue;
                Vector3 w; Quaternion r; MonumentToWorld(cp.Monument, l, 0f, out w, out r);
                spots.Add(w);
            }
            if (spots.Count == 0) spots.Add(cp.Position - cp.Forward * 1.5f); // the room's centre (or 1.5 m inside a doorway)
            foreach (var spot in spots)
            {
                var ent = GameManager.server.CreateEntity(EliteCratePrefab, spot, cp.Rotation);
                var crate = ent as LootContainer;
                if (crate == null) { CLog($"[vault] '{cp.Name}': CreateEntity returned {(ent == null ? "null" : ent.GetType().Name)} for the crate."); if (ent != null) ent.Kill(); continue; }
                crate.enableSaving = false;
                crate.Spawn();
                cp.Props.Add(crate); cp.PropKinds[crate] = "crate";
                cp.Crates.Add(crate);
                FillVaultCrate(cp, crate, cp.Crates.Count - 1, spots.Count);
            }
        }

        // The tier's loot table (owner's call 2026-09-20): the totals are per vault and split evenly
        // across the crate spots (the remainder on the first crate); the crate's own loot is cleared.
        // One stack per item, whatever the size: the server accepts over-sized stacks and a player
        // moves the whole stack in one drag (the owner's experience on other servers).
        private readonly HashSet<string> _badLootLogged = new HashSet<string>();

        private void FillVaultCrate(Checkpoint cp, LootContainer crate, int index, int crateCount)
        {
            var tier = Mathf.Clamp(CurrentTier(), 1, 5);
            var table = _config.Vaults.LootByTier;
            if (table == null || table.Count < tier || table[tier - 1] == null || table[tier - 1].Count == 0) return; // vanilla loot
            if (crate.inventory == null) return;
            var loot = table[tier - 1];
            crate.inventory.Clear();
            ItemManager.DoRemoves();
            var wanted = new List<KeyValuePair<ItemDefinition, int>>();
            foreach (var kv in loot)
            {
                if (kv.Value <= 0) continue;
                var def = ItemManager.FindItemDefinition(kv.Key);
                if (def == null)
                {
                    if (_badLootLogged.Add(kv.Key)) CLog($"[vault] LootByTier: no item named '{kv.Key}' (tier {tier}); skipped.");
                    continue;
                }
                var share = kv.Value / Mathf.Max(1, crateCount) + (index == 0 ? kv.Value % Mathf.Max(1, crateCount) : 0);
                if (share <= 0) continue;
                wanted.Add(new KeyValuePair<ItemDefinition, int>(def, share));
            }
            if (wanted.Count == 0) return;
            if (crate.inventory.capacity < wanted.Count) { crate.inventory.capacity = wanted.Count; crate.panelName = "generic_resizable"; }
            var summary = new List<string>();
            foreach (var w in wanted)
            {
                var item = ItemManager.Create(w.Key, w.Value);
                if (item == null) continue;
                if (!item.MoveToContainer(crate.inventory)) { item.Remove(); continue; }
                summary.Add($"{w.Key.shortname}×{w.Value}");
            }
            crate.SendNetworkUpdate();
            CLog($"[vault] '{cp.Name}' crate {index + 1}/{crateCount} stocked for tier {tier}: {string.Join(", ", summary)}.");
        }

        // A 3×3 m armored building at the pose: the doorway wall stands at cp.Position with its face
        // toward the approach (cp.Forward), the foundation centre 1.5 m behind it, two side walls, a
        // back wall, a floor piece as the ceiling. CopyPaste's recipe (live on this server): definition
        // + grade + grounded + building id before Spawn, health/skin after; then no rotate, no demolish,
        // immortal, no decay — the door is the only way in. Wall yaw convention is WallYawOffset if
        // the live look says otherwise. Outpost's building doors are static geometry (2026-09-19), so
        // the vault brings its own building.
        private const float WallYawOffset = 90f; // live 2026-09-19: at 0 every wall stood across its edge; the wall panel runs along local Z, the door's along X

        private void SpawnVaultRoom(Checkpoint cp)
        {
            var id = BuildingManager.server.NewBuildingID();
            var centre = cp.Position - cp.Forward * 1.5f;
            SpawnBlock(cp, id, FoundationPrefab, centre, cp.Rotation);
            SpawnBlock(cp, id, DoorwayPrefab, cp.Position, cp.Rotation * Quaternion.Euler(0f, WallYawOffset, 0f));
            SpawnBlock(cp, id, WallPrefab, centre - cp.Forward * 1.5f, Quaternion.LookRotation(-cp.Forward) * Quaternion.Euler(0f, WallYawOffset, 0f));
            SpawnBlock(cp, id, WallPrefab, centre + cp.Right * 1.5f, Quaternion.LookRotation(cp.Right) * Quaternion.Euler(0f, WallYawOffset, 0f));
            SpawnBlock(cp, id, WallPrefab, centre - cp.Right * 1.5f, Quaternion.LookRotation(-cp.Right) * Quaternion.Euler(0f, WallYawOffset, 0f));
            SpawnBlock(cp, id, CeilingPrefab, centre + Vector3.up * 3f, cp.Rotation);
        }

        private BuildingBlock SpawnBlock(Checkpoint cp, uint buildingId, string prefab, Vector3 pos, Quaternion rot)
        {
            var ent = GameManager.server.CreateEntity(prefab, pos, rot);
            var block = ent as BuildingBlock;
            if (block == null) { CLog($"[vault] '{cp.Name}': CreateEntity returned {(ent == null ? "null" : ent.GetType().Name)} for {prefab}."); if (ent != null) ent.Kill(); return null; }
            block.enableSaving = false;
            block.blockDefinition = PrefabAttribute.server.Find<Construction>(block.prefabID);
            block.SetGrade(BuildingGrade.Enum.TopTier);
            block.grounded = true;
            block.AttachToBuilding(buildingId);
            block.Spawn();
            block.SetHealthToMax();
            block.UpdateSkin();
            block.StopBeingRotatable();
            block.StopBeingDemolishable();
            block.baseProtection = ProtectionProperties.immortalProtection;
            block.decay = null;
            block.SendNetworkUpdate();
            cp.Props.Add(block); cp.PropKinds[block] = "block";
            return block;
        }

        // Outpost's buildings all have their own doors (owner, 2026-09-18). The vault takes the frame:
        // any monument door within ReplaceRadius of the pose is removed and remembered, and put back
        // when the vault despawns (unload, reload, tier drop, remove). Map entities come back from the
        // map prefab on every boot, so a restart restores the original before the vault removes it again.
        private void RemoveMonumentDoor(Checkpoint cp)
        {
            if (!_config.Vaults.ReplaceMonumentDoor) return;
            var victims = new List<Door>();
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var d = e as Door;
                if (d == null || d.IsDestroyed || d == cp.Door || VaultOfDoor(d) != null || _looseProps.Contains(d)) continue;
                if (Vector3.Distance(d.transform.position, cp.Position) > Mathf.Max(0.5f, _config.Vaults.ReplaceRadius)) continue;
                victims.Add(d);
            }
            foreach (var d in victims)
            {
                cp.RemovedDoors.Add(new RemovedDoor { Prefab = d.PrefabName, Position = d.transform.position, Rotation = d.transform.rotation });
                CLog($"[vault] '{cp.Name}' took the frame: monument door {d.ShortPrefabName} at {V(d.transform.position)} removed until the vault goes.");
                d.Kill();
            }
        }

        private static void RestoreMonumentDoors(Checkpoint cp)
        {
            foreach (var r in cp.RemovedDoors)
            {
                try
                {
                    var e = GameManager.server.CreateEntity(r.Prefab, r.Position, r.Rotation);
                    if (e == null) continue;
                    e.enableSaving = false; // the map prefab respawns its own on the next boot
                    e.Spawn();
                }
                catch (Exception ex) { GLog($"[vault] '{cp.Name}': could not put back {r.Prefab}: {ex.Message}"); }
            }
            cp.RemovedDoors.Clear();
        }

        // The monument door nearest a point, if any is within the replace radius: the placement
        // tool snaps the vault pose to it so the vault door sits exactly in the frame.
        private Door MonumentDoorNear(Vector3 pos)
        {
            Door best = null; var bestD = Mathf.Max(0.5f, _config.Vaults.ReplaceRadius);
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var d = e as Door;
                if (d == null || d.IsDestroyed || VaultOfDoor(d) != null || _looseProps.Contains(d)) continue;
                var dist = Vector3.Distance(d.transform.position, pos);
                if (dist < bestD) { bestD = dist; best = d; }
            }
            return best;
        }

        // The door fell (decision 0009 §4): the breacher pays the raid figure and is flagged, the
        // vault and its monument's gates go on alert with the guards on the breacher, the server
        // hears of it, and Cobalt rebuilds the door after DoorRebuildMinutes. The crates stay: they
        // are the prize, and Cobalt property to whoever loots them.
        private void OnVaultBreached(Checkpoint cp, ulong attacker, string weapon)
        {
            var cfg = _config.Vaults;
            if (cp.Door != null) { cp.Props.Remove(cp.Door); cp.PropKinds.Remove(cp.Door); }
            cp.Door = null; cp.Lock = null;
            if (cp.Despawning) return;
            var who = attacker != 0 ? BasePlayer.FindByID(attacker) : null;
            var name = who != null ? who.displayName : (attacker != 0 ? attacker.ToString() : "nobody");
            if (attacker != 0 && IsRealPlayerId(attacker)) Adjust(attacker, cfg.BreachPenalty, "Reason.VaultBreach");
            Nudge(cfg.NudgeBreach, "vault breached");
            var until = Time.realtimeSinceStartup + Mathf.Max(1, cfg.AlertMinutes) * 60f;
            var alerted = 0;
            foreach (var other in _checkpoints.Values) if (other == cp || other.Monument == cp.Monument) { other.AlertUntil = until; alerted++; }
            UpdatePropLights();
            if (who != null && who.IsConnected && IsRealPlayer(who))
            {
                FlagHostile(who);
                foreach (var g in cp.Guards) if (g != null && !g.IsDestroyed) g.GetComponent<GuardBrain>()?.ForceTarget(who);
            }
            CLog($"[vault] '{cp.Name}' BREACHED by {name} ({weapon}): {cfg.BreachPenalty} rep, {alerted} checkpoint(s) on alert for {cfg.AlertMinutes} min, door back in {cfg.DoorRebuildMinutes} min.");
            if (cfg.BroadcastBreach) Broadcast("Broadcast.VaultBreach", MonumentShortName(cp.Monument), name);
            ScheduleDoorRebuild(cp, DateTime.UtcNow.AddMinutes(Mathf.Max(1, cfg.DoorRebuildMinutes)), "breach");
        }

        private VaultState StateOf(Checkpoint cp)
        {
            VaultState st;
            if (!_cpData.Vaults.TryGetValue(cp.Name, out st)) { st = new VaultState(); _cpData.Vaults[cp.Name] = st; }
            return st;
        }

        private void SaveVaultState(Checkpoint cp)
        {
            var st = StateOf(cp);
            st.DoorRebuildAtUtc = cp.DoorRebuildAtUtc; st.RestockAtUtc = cp.RestockAtUtc;
            if (st.DoorRebuildAtUtc == default(DateTime) && st.RestockAtUtc == default(DateTime)) _cpData.Vaults.Remove(cp.Name);
            _cpDirty = true; SaveCheckpointData(true);
        }

        private void ScheduleDoorRebuild(Checkpoint cp, DateTime at, string why)
        {
            cp.DoorTimer?.Destroy();
            cp.DoorRebuildAtUtc = at;
            SaveVaultState(cp);
            var seconds = (float)Math.Max(5, (at - DateTime.UtcNow).TotalSeconds);
            if (why == "restored") CLog($"[vault] '{cp.Name}' door still down after the reload; back at {at:HH:mm} UTC.");
            cp.DoorTimer = timer.Once(seconds, () =>
            {
                cp.DoorTimer = null;
                cp.DoorRebuildAtUtc = default(DateTime);
                SaveVaultState(cp);
                if (cp.State != CheckpointState.Active || (cp.Door != null && !cp.Door.IsDestroyed)) return;
                SpawnVaultDoor(cp);
                CLog($"[vault] '{cp.Name}' door rebuilt and locked.");
            });
        }

        private void ScheduleRestock(Checkpoint cp, DateTime at, string why)
        {
            cp.RestockTimer?.Destroy();
            cp.RestockAtUtc = at;
            SaveVaultState(cp);
            var seconds = (float)Math.Max(5, (at - DateTime.UtcNow).TotalSeconds);
            CLog($"[vault] '{cp.Name}' emptied ({why}); restock at {at:HH:mm} UTC.");
            cp.RestockTimer = timer.Once(seconds, () => { cp.RestockTimer = null; RestockVault(cp, "timer"); });
        }

        // Fresh crates and a shut door; the crates that are still there go first (an admin restock).
        private void RestockVault(Checkpoint cp, string why)
        {
            cp.RestockTimer?.Destroy(); cp.RestockTimer = null;
            cp.RestockAtUtc = default(DateTime);
            SaveVaultState(cp);
            if (cp.State != CheckpointState.Active) return;
            cp.Despawning = true;
            foreach (var c in new List<LootContainer>(cp.Crates)) { if (c != null && !c.IsDestroyed) { cp.Props.Remove(c); cp.PropKinds.Remove(c); c.Kill(); } }
            cp.Crates.Clear();
            cp.Despawning = false;
            SpawnVaultCrates(cp);
            if (cp.Door != null && !cp.Door.IsDestroyed && cp.Door.IsOpen()) cp.Door.SetOpen(false);
            CLog($"[vault] '{cp.Name}' restocked ({why}): {cp.Crates.Count} crate(s).");
        }

        // An elite crate destroys itself when emptied, and dies to damage; both end in Kill(). When the
        // last one goes, Cobalt restocks after RestockMinutes.
        private void OnVaultCrateGone(Checkpoint cp, LootContainer crate)
        {
            cp.Crates.Remove(crate); cp.Props.Remove(crate); cp.PropKinds.Remove(crate);
            if (cp.Despawning || cp.State != CheckpointState.Active) return;
            var left = 0;
            foreach (var c in cp.Crates) if (c != null && !c.IsDestroyed) left++;
            if (left > 0) { CLog($"[vault] '{cp.Name}' crate gone; {left} left."); return; }
            if (cp.RestockTimer == null) ScheduleRestock(cp, DateTime.UtcNow.AddMinutes(Mathf.Max(1, _config.Vaults.RestockMinutes)), "last crate gone");
        }

        private Checkpoint VaultOfCrate(BaseEntity crate)
        {
            if (crate == null) return null;
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Vault && cp.Crates.Contains(crate as LootContainer)) return cp;
            return null;
        }

        private Checkpoint VaultOfDoor(BaseEntity door)
        {
            if (door == null) return null;
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Vault && cp.Door == door) return cp;
            return null;
        }

        private VaultTemplate FindVault(string name)
        {
            foreach (var v in _config.Vaults.Vaults) if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        private void RespawnVault(VaultTemplate v)
        {
            foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
                if (cp.Vault == v) { Despawn(cp, "respawn"); _checkpoints.Remove(cp.Name); }
            if (!_config.Vaults.Enabled) return;
            foreach (var cp in ResolveVault(v))
            {
                if (_checkpoints.ContainsKey(cp.Name)) continue;
                _checkpoints[cp.Name] = cp;
                TrySpawn(cp);
            }
        }

        private string DescribeVaults(string viewerId)
        {
            if (_config.Vaults.Vaults.Count == 0) return L("Vault.ListNone", viewerId);
            var sb = new StringBuilder($"vaults: {_config.Vaults.Vaults.Count} placed, from tier {_config.Vaults.VaultFromTier}, guards by tier [{string.Join(", ", Array.ConvertAll(_config.Vaults.GuardsByTier ?? new int[0], x => x.ToString()))}], tier {CurrentTier()}");
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.Kind != CheckpointKind.Vault) continue;
                var status = cp.State == CheckpointState.Active
                    ? $"active door={(cp.Door != null && !cp.Door.IsDestroyed ? $"{cp.Door.Health():0}hp" : (cp.DoorRebuildAtUtc > DateTime.UtcNow ? $"gone, back {cp.DoorRebuildAtUtc:HH:mm}Z" : "gone"))} crates={cp.Crates.Count}{(cp.RestockAtUtc > DateTime.UtcNow ? $" (restock {cp.RestockAtUtc:HH:mm}Z)" : "")} guards={cp.LiveGuards()} alert={cp.IsAlert} inSafeZone={cp.InSafeZone}"
                    : $"{cp.State} {cp.StateNote}";
                sb.Append('\n').Append(L("Vault.ListLine", viewerId, cp.Name, V(cp.Position), MonumentShortName(cp.Monument), cp.Vault.CrateLocalPositions.Count, cp.Vault.GuardLocalPositions.Count, VaultMinTier(cp.Vault), status));
            }
            foreach (var v in _config.Vaults.Vaults)
            {
                var placed = false;
                foreach (var cp in _checkpoints.Values) if (cp.Vault == v) { placed = true; break; }
                if (!placed) sb.Append('\n').Append($"  {v.Name} @ {v.Monument} local {v.DoorLocalPosition} yaw {v.DoorLocalYaw:0} | no such monument on this map");
            }
            return sb.ToString();
        }

        // /papers vault … (chat, admin) and papers.vault … (console). The door goes at the aim point
        // with the admin's facing (stand in the doorway facing the approach); crates and guard posts
        // are where the admin stands; everything is monument-local and transfers between maps.
        private string RunVault(string[] args, int first, BasePlayer player, string viewerId)
        {
            var sub = args.Length > first ? args[first].ToLowerInvariant() : "list";
            var name = args.Length > first + 1 ? args[first + 1] : null;
            switch (sub)
            {
                case "list":
                case "status":
                    return DescribeVaults(viewerId);
                case "add":
                {
                    if (player == null || string.IsNullOrEmpty(name) || !IsGateName(name)) return L("Vault.Usage", viewerId);
                    if (FindVault(name) != null || FindTemplate(name) != null || _checkpoints.ContainsKey(name)) return L("Vault.Exists", viewerId, name);
                    var needle = args.Length > first + 2 ? args[first + 2] : null;
                    var pos = AimPoint(player);
                    // Room mode: the door goes at the aim point facing you and the room extends away from
                    // you, so the approach (cp.Forward) is back toward where you stand. Doorway mode keeps
                    // the gate convention: stand in the doorway facing the approach.
                    var yaw = _config.Vaults.Room ? YawOf(-player.eyes.BodyForward()) : YawOf(player.eyes.BodyForward());
                    // Doorway mode, aimed at the building's own door: the vault takes its exact pose (and
                    // its frame, RemoveMonumentDoor). The approach side is whichever of the door's two
                    // facings is nearer the admin's own facing.
                    var frame = !_config.Vaults.Room && _config.Vaults.ReplaceMonumentDoor ? MonumentDoorNear(pos) ?? MonumentDoorNear(player.transform.position) : null;
                    var snapped = "";
                    if (frame != null)
                    {
                        pos = frame.transform.position;
                        var doorYaw = YawOf(frame.transform.forward);
                        var a = Mathf.Repeat(doorYaw - 90f, 360f); var b = Mathf.Repeat(doorYaw + 90f, 360f);
                        yaw = Mathf.Abs(Mathf.DeltaAngle(a, yaw)) <= Mathf.Abs(Mathf.DeltaAngle(b, yaw)) ? a : b;
                        snapped = $" Snapped to the monument door {frame.ShortPrefabName} in that frame.";
                    }
                    float dist; string alternatives;
                    var m = NearestMonument(pos, needle, out dist, out alternatives);
                    if (m == null) return L("Vault.NoMonument", viewerId);
                    Vector3 local; float localYaw;
                    WorldToMonument(m, pos, yaw, out local, out localYaw);
                    var v = new VaultTemplate { Name = name, Monument = MonumentShortName(m), DoorLocalPosition = VecText(local), DoorLocalYaw = (float)Math.Round(localYaw, 1) };
                    _config.Vaults.Vaults.Add(v);
                    SaveConfig();
                    CLog($"[vault] '{name}' door captured at {V(pos)} yaw {yaw:0} → monument '{v.Monument}' local {v.DoorLocalPosition} yaw {v.DoorLocalYaw:0}; {dist:0} m from the monument origin.");
                    RespawnVault(v);
                    return L("Vault.Added", viewerId, name, v.Monument, v.DoorLocalPosition, v.DoorLocalYaw, dist, alternatives) + snapped;
                }
                case "crate":
                case "guard":
                {
                    if (player == null || string.IsNullOrEmpty(name)) return L("Vault.Usage", viewerId);
                    var v = FindVault(name);
                    if (v == null) return L("Vault.NotFound", viewerId, name);
                    var monuments = MonumentsNamed(v.Monument);
                    if (monuments.Count == 0) return L("Vault.NoMonument", viewerId);
                    var m = monuments[0];
                    var best = float.MaxValue;
                    foreach (var mi in monuments) { var d = Vector3.Distance(mi.transform.position, player.transform.position); if (d < best) { best = d; m = mi; } }
                    Vector3 local; float localYaw;
                    WorldToMonument(m, player.transform.position, 0f, out local, out localYaw);
                    var list = sub == "crate" ? v.CrateLocalPositions : v.GuardLocalPositions;
                    list.Add(VecText(local));
                    SaveConfig();
                    RespawnVault(v);
                    return L(sub == "crate" ? "Vault.CrateAdded" : "Vault.GuardAdded", viewerId, name, VecText(local), list.Count);
                }
                case "remove":
                {
                    var v = name != null ? FindVault(name) : null;
                    if (v == null) return L("Vault.NotFound", viewerId, name ?? "");
                    foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
                        if (cp.Vault == v) { Despawn(cp, "removed"); _checkpoints.Remove(cp.Name); if (_cpData.Vaults.Remove(cp.Name)) _cpDirty = true; }
                    _config.Vaults.Vaults.Remove(v);
                    SaveConfig();
                    return L("Vault.Removed", viewerId, v.Name);
                }
                case "open":
                case "close":
                {
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null || cp.Kind != CheckpointKind.Vault) return L("Vault.NotFound", viewerId, name ?? "");
                    if (cp.Door == null || cp.Door.IsDestroyed) return $"'{cp.Name}': no door standing.";
                    var opening = sub == "open";
                    cp.Door.SetOpen(opening);
                    if (opening) cp.Door.Invoke(cp.Door.CloseRequest, 10f); // SetOpen skips OnDoorOpened: the same 10 s self-close as a player's opening
                    CLog($"[vault] '{cp.Name}' door {(opening ? "opened" : "closed")} by admin.");
                    return $"'{cp.Name}' door {(opening ? "opened" : "closed")}.";
                }
                case "breach":
                {
                    // Test: the door falls as if to explosives, credited to the caller (or nobody from the console).
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null || cp.Kind != CheckpointKind.Vault) return L("Vault.NotFound", viewerId, name ?? "");
                    if (cp.Door == null || cp.Door.IsDestroyed) return $"'{cp.Name}': no door standing.";
                    var door = cp.Door;
                    OnVaultBreached(cp, player != null ? (ulong)player.userID : 0, "admin");
                    door.Kill();
                    return $"'{cp.Name}' breached; door back in {_config.Vaults.DoorRebuildMinutes} min.";
                }
                case "restock":
                {
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null || cp.Kind != CheckpointKind.Vault) return L("Vault.NotFound", viewerId, name ?? "");
                    RestockVault(cp, "admin");
                    if (cp.Door == null && cp.DoorTimer != null) { cp.DoorTimer.Destroy(); cp.DoorTimer = null; cp.DoorRebuildAtUtc = default(DateTime); SaveVaultState(cp); SpawnVaultDoor(cp); }
                    return $"'{cp.Name}' restocked: {cp.Crates.Count} crate(s), door {(cp.Door != null ? "standing" : "missing")}.";
                }
                case "tp":
                {
                    if (player == null) return L("Vault.Usage", viewerId);
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null || cp.Kind != CheckpointKind.Vault) return L("Vault.NotFound", viewerId, name ?? "");
                    var spot = cp.Position + cp.Forward * 3f;
                    spot.y = Mathf.Max(cp.Position.y, TerrainMeta.HeightMap.GetHeight(spot)) + 0.5f;
                    player.Teleport(spot);
                    return L("Vault.Teleported", viewerId, cp.Name);
                }
                default:
                    return L("Vault.Usage", viewerId);
            }
        }

        [ConsoleCommand("papers.vault")]
        private void CmdVault(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            arg.ReplyWith(RunVault(ConsoleArgs(arg), 0, caller, caller?.UserIDString));
        }

        #endregion

        #region Sabotage (tasks 8.5–8.6 — decision 0009 §B′: the bypass tool on the substation switch opens the Outpost vault window)

        private ElectricSwitch _sabSwitch;
        private Vector3 _sabSwitchPos;
        private Quaternion _sabSwitchRot;
        private bool _sabSwitchMissingLogged;
        private float _darkUntil;
        private float _cutCooldownUntil;
        private Timer _darkTimer;
        private readonly List<GuardNpc> _responseGuards = new List<GuardNpc>();
        private Timer _responseTimer;
        private readonly Dictionary<IOEntity, bool> _darkenedLights = new Dictionary<IOEntity, bool>(); // monument lights we switched off, and whether they were on
        private readonly List<NPCAutoTurret> _vanillaTurretsOff = new List<NPCAutoTurret>(); // the compound's own sentries we switched offline
        private readonly Dictionary<SpawnGroup, bool> _pausedSpawners = new Dictionary<SpawnGroup, bool>();       // the compound's NPC spawn groups we paused (isSpawnerActive before)
        private readonly Dictionary<SpawnGroup, Vector2> _spawnerDelays = new Dictionary<SpawnGroup, Vector2>(); // respawnDelayMin/Max before a probe override
        private ulong _saboteur;
        private Timer _windowTick;
        private const string UiWindow = "PapersPlease.Window";

        private bool IsDark => _darkUntil > Time.realtimeSinceStartup;
        // A checkpoint at Outpost. Roadblocks carry no monument, so a bare `cp.Monument == _outpost`
        // matches every roadblock on a map without Outpost (review 2026-09-20).
        private bool AtOutpost(Checkpoint cp) => _outpost != null && cp.Monument == _outpost;
        private float _windowAlertUntil;     // the alert stamp the window put on the Outpost checkpoints; cleared with the window
        private bool _revivedOnce;           // the sentry sweep runs once per plugin instance
        private int _defenderTurretsCached; // OutpostDefenders: the compound's own sentry count, refreshed every 30 s
        private float _defenderTurretsAt;
        private bool SabotageArmed => _config.Sabotage.Enabled && CurrentTier() >= _config.Sabotage.SabotageFromTier;

        // The switch stands at its substation from SabotageFromTier and goes below it; called from the
        // tier reconcile, the spawn-all and the reload. The window (8.6) opens with the fence's tool.
        private void ReconcileSabotage()
        {
            var cfg = _config.Sabotage;
            var want = SabotageArmed && !string.IsNullOrEmpty(cfg.SwitchMonument);
            if (want && (_sabSwitch == null || _sabSwitch.IsDestroyed)) SpawnSabotageSwitch();
            else if (!want && _sabSwitch != null) DespawnSabotage(SabotageArmed ? "switch removed" : "below tier");
            if (!IsDark) ReviveVanillaTurrets();
        }

        // The compound's own sentries are monument entities: `SetIsOnline(false)` outlives a plugin
        // reload that never restored them (1.1.16 on 2026-09-20 left the Outpost sentries offline
        // after a window whose close timer was never armed). With no window open, every vanilla
        // sentry at Outpost found offline goes back online — nothing but this plugin turns them off.
        private void ReviveVanillaTurrets()
        {
            if (_outpost == null || _vanillaTurretsOff.Count > 0 || _revivedOnce) return; // a previous instance's leftover: once is enough
            _revivedOnce = true;
            var mine = new HashSet<BaseEntity>();
            foreach (var cp in _checkpoints.Values) foreach (var e in cp.Props) mine.Add(e);
            var n = 0;
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var t = e as NPCAutoTurret;
                if (t == null || t.IsDestroyed || mine.Contains(t) || _looseProps.Contains(t) || !InMonument(_outpost, t.transform.position, 150f)) continue;
                if (t.IsOnline()) continue;
                t.SetIsOnline(true); n++;
            }
            if (n > 0) CLog($"[sabotage] {n} vanilla sentr{(n == 1 ? "y" : "ies")} at Outpost found offline with no window open; back online.");
        }

        // The compound's own NPC spawn groups (the peacekeepers). Probe 2026-09-20 (finding 4, the
        // live assembly): a SpawnGroup respawns on a LocalClock with delta (respawnDelayMin +
        // respawnDelayMax) / 2 ÷ spawn.player_scale (10–20 s by default), Spawn() is a no-op while
        // isSpawnerActive is false (SetIsSpawningActive), and the group records its Monument at
        // awake — so the match is exact and pausing is one public call. Nothing here is saved.
        private List<SpawnGroup> OutpostSpawners()
        {
            var list = new List<SpawnGroup>();
            if (_outpost == null) return list;
            foreach (var g in UnityEngine.Object.FindObjectsOfType<SpawnGroup>())
            {
                if (g == null) continue;
                if (g.Monument != _outpost && !InMonument(_outpost, g.transform.position, 150f)) continue;
                if (!g.DoesGroupContainNPCs() || !g.WantsTimedSpawn()) continue; // the tunnel dwellers under the compound never respawn
                list.Add(g);
            }
            return list;
        }

        private int PauseOutpostSpawners(bool pause)
        {
            var n = 0;
            if (pause)
            {
                foreach (var g in OutpostSpawners())
                {
                    if (_pausedSpawners.ContainsKey(g)) continue;
                    _pausedSpawners[g] = g.isSpawnerActive;
                    g.SetIsSpawningActive(false); n++;
                }
            }
            else
            {
                foreach (var kv in _pausedSpawners) { if (kv.Key == null) continue; kv.Key.SetIsSpawningActive(kv.Value); n++; }
                _pausedSpawners.Clear();
            }
            return n;
        }

        private int OverrideSpawnerDelay(float seconds)
        {
            var n = 0;
            foreach (var g in OutpostSpawners())
            {
                if (!_spawnerDelays.ContainsKey(g)) _spawnerDelays[g] = new Vector2(g.respawnDelayMin, g.respawnDelayMax);
                if (seconds <= 0f) { var o = _spawnerDelays[g]; g.respawnDelayMin = o.x; g.respawnDelayMax = o.y; }
                else { g.respawnDelayMin = seconds; g.respawnDelayMax = seconds * 1.5f; }
                n++;
            }
            if (seconds <= 0f) _spawnerDelays.Clear();
            return n;
        }

        private void RestoreSpawners(string why)
        {
            var resumed = PauseOutpostSpawners(false);
            var delays = 0;
            foreach (var kv in _spawnerDelays) { if (kv.Key == null) continue; kv.Key.respawnDelayMin = kv.Value.x; kv.Key.respawnDelayMax = kv.Value.y; delays++; }
            _spawnerDelays.Clear();
            if (resumed > 0 || delays > 0) CLog($"[sabotage] Outpost spawn groups restored ({why}): {resumed} resumed, {delays} delay(s) put back.");
        }

        private string DescribeSpawners()
        {
            var groups = OutpostSpawners();
            var sb = new StringBuilder($"Outpost NPC spawn groups: {groups.Count} (spawn.player_scale scales the delay)");
            foreach (var g in groups)
            {
                var names = new List<string>();
                if (g.prefabs != null)
                    foreach (var p in g.prefabs)
                    {
                        if (p == null || p.prefab == null || !p.prefab.isValid) continue;
                        var path = p.prefab.resourcePath ?? "";
                        names.Add($"{path.Substring(path.LastIndexOf('/') + 1)}×{p.weight}");
                    }
                sb.Append('\n').Append($"  {g.GetType().Name} '{g.category}' at {V(g.transform.position)}: pop {g.currentPopulation}/{g.maxPopulation}, {g.numToSpawnPerTickMin}-{g.numToSpawnPerTickMax} per tick, respawn {g.respawnDelayMin:0}-{g.respawnDelayMax:0} s (delta {g.GetSpawnDelta():0} s), active={g.isSpawnerActive}{(_pausedSpawners.ContainsKey(g) ? " (paused by us)" : "")}{(_spawnerDelays.ContainsKey(g) ? " (delay overridden)" : "")}, points {g.SpawnPointCount}, [{string.Join(", ", names)}]");
            }
            return sb.ToString();
        }

        // Of several monuments with the name (substations and decor prefabs repeat): the occurrence
        // whose copy of the local pose lands within 30 m of where the admin captured it (live
        // 2026-09-19: "the switch went somewhere else" — another ue_oasis_c nearer Outpost); on
        // another map, or with no capture, the one nearest Outpost.
        private MonumentInfo SabotageMonument(string name, string localPose = null, string worldPose = null)
        {
            var monuments = MonumentsNamed(name);
            if (monuments.Count == 0) return null;
            Vector3 local, world;
            if (monuments.Count > 1 && localPose != null && worldPose != null && TryParseVector(localPose, out local) && TryParseVector(worldPose, out world) && world.sqrMagnitude > 1f)
            {
                MonumentInfo hit = null; var hitD = 30f;
                foreach (var m in monuments)
                {
                    Vector3 pos; Quaternion rot; MonumentToWorld(m, local, 0f, out pos, out rot);
                    var d = Vector3.Distance(pos, world);
                    if (d < hitD) { hitD = d; hit = m; }
                }
                if (hit != null) return hit;
            }
            var best = monuments[0];
            if (_outpost != null)
            {
                var bestD = float.MaxValue;
                foreach (var m in monuments) { var d = Vector3.Distance(m.transform.position, _outpost.transform.position); if (d < bestD) { bestD = d; best = m; } }
            }
            return best;
        }

        // The monument nearest a point by its origin, bounds or not, within `radius` — the fallback
        // for tiny prefabs like substations that the bounds scorer penalises.
        private static MonumentInfo MonumentByOrigin(Vector3 pos, float radius)
        {
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return null;
            MonumentInfo best = null; var bestD = radius;
            foreach (var m in monuments)
            {
                if (m == null) continue;
                var d = Vector3.Distance(m.transform.position, pos);
                if (d < bestD) { bestD = d; best = m; }
            }
            return best;
        }

        private void SpawnSabotageSwitch()
        {
            var cfg = _config.Sabotage;
            Vector3 local;
            if (!TryParseVector(cfg.SwitchLocalPosition, out local)) return;
            var m = SabotageMonument(cfg.SwitchMonument, cfg.SwitchLocalPosition, cfg.SwitchWorldPosition);
            if (m == null)
            {
                if (!_sabSwitchMissingLogged) { CLog($"[sabotage] no monument named '{cfg.SwitchMonument}' on this map; no switch."); _sabSwitchMissingLogged = true; }
                return;
            }
            Vector3 pos; Quaternion rot;
            MonumentToWorld(m, local, cfg.SwitchLocalYaw, out pos, out rot);
            pos.y = Mathf.Max(pos.y, TerrainMeta.HeightMap.GetHeight(pos));
            var ent = GameManager.server.CreateEntity(SwitchPrefab, pos, rot);
            var sw = ent as ElectricSwitch;
            if (sw == null) { CLog($"[sabotage] CreateEntity returned {(ent == null ? "null" : ent.GetType().Name)} for the switch."); if (ent != null) ent.Kill(); return; }
            sw.enableSaving = false;
            foreach (var c in sw.GetComponentsInChildren<DestroyOnGroundMissing>(true)) UnityEngine.Object.DestroyImmediate(c);
            foreach (var c in sw.GetComponentsInChildren<GroundWatch>(true)) UnityEngine.Object.DestroyImmediate(c);
            sw.Spawn();
            sw.baseProtection = ProtectionProperties.immortalProtection; // the panel is not the target; the grid is
            _sabSwitch = sw; _sabSwitchPos = pos; _sabSwitchRot = rot;
            CLog($"[sabotage] switch at {V(pos)} ({MonumentShortName(m)}), armed from tier {cfg.SabotageFromTier} (now {CurrentTier()}); window = {cfg.WindowMinutes} min, {cfg.ResponseGuards} guard(s) for {cfg.ResponseMinutes} min, {cfg.CutPenalty} rep, one cut per {cfg.CooldownMinutes} min.");
        }

        private void DespawnSabotage(string why)
        {
            RestorePower(why); // guards itself: a no-op with nothing to restore
            DarkenMonumentLights(false);
            _darkTimer?.Destroy(); _darkTimer = null;
            DespawnResponse();
            if (_sabSwitch != null) { if (!_sabSwitch.IsDestroyed) _sabSwitch.Kill(); _sabSwitch = null; CLog($"[sabotage] switch removed ({why})."); }
        }

        // The switch (decision 0009 §B, rewritten 2026-09-20): a flip with the fence's bypass tool
        // on you opens the vault window; without it the panel is sealed. The hook fires before the
        // flip; the switch is reset when the window ends. `admin` skips the tool (papers.sabotage cut).
        private void OnPowerCut(BasePlayer player, bool admin = false)
        {
            var cfg = _config.Sabotage;
            if (player == null || !IsRealPlayer(player)) return;
            var now = Time.realtimeSinceStartup;
            if (!SabotageArmed) { CLog($"[sabotage] {player.displayName} flipped the switch below tier {cfg.SabotageFromTier}; nothing happens."); ResetSwitchLater(); return; }
            if (IsDark || now < _cutCooldownUntil)
            {
                CLog($"[sabotage] {player.displayName} flipped the switch during the {(IsDark ? "window" : "cooldown")}; ignored.");
                ResetSwitchLater();
                return;
            }
            var tool = admin ? null : FindBypassTool(player);
            if (!admin && tool == null)
            {
                CLog($"[sabotage] {player.displayName} flipped the switch without a bypass tool; sealed.");
                player.ChatMessage(L("Notify.NeedTool", player.UserIDString));
                ResetSwitchLater();
                return;
            }
            if (tool != null) { tool.RemoveFromContainer(); tool.Remove(); }
            StartWindow(player, admin ? "admin" : "tool");
        }

        // The vault window: the safe zone down, the compound's own sentries offline, the plugin's
        // floods off and sirens on, every Outpost checkpoint on alert, the saboteur hostile for the
        // whole window (vanilla scientists, sentries and guards engage on sight), a response at the
        // substation, a countdown on the saboteur's screen, a server-wide line.
        private void StartWindow(BasePlayer player, string how)
        {
            var cfg = _config.Sabotage;
            var now = Time.realtimeSinceStartup;
            var seconds = Mathf.Max(1, cfg.WindowMinutes) * 60f;
            _darkUntil = now + seconds;
            _cutCooldownUntil = now + Mathf.Max(1, cfg.CooldownMinutes) * 60f;
            _saboteur = (ulong)player.userID;
            // The close timer is armed before anything that can throw: on 2026-09-20 11:25 an
            // exception half-way through left the zone down with no timer to bring it back.
            _darkTimer?.Destroy();
            _darkTimer = timer.Once(seconds, () => { _darkTimer = null; RestorePower("timer"); });
            _windowTick?.Destroy();
            _windowTick = timer.Every(1f, WindowTick);
            UpdatePropLights();
            DarkenMonumentLights(true);
            if (cfg.DropsSafeZone) SetZoneDown(true, "sabotage");
            var offline = OfflineVanillaTurrets(true);
            var paused = cfg.PauseVanillaSpawners ? PauseOutpostSpawners(true) : 0;
            var alerted = 0;
            _windowAlertUntil = now + seconds;
            foreach (var cp in _checkpoints.Values) if (AtOutpost(cp)) { cp.AlertUntil = _windowAlertUntil; alerted++; }
            UpdatePropLights();
            player.MarkHostileFor(seconds);
            _hostileUntil[_saboteur] = now + seconds;
            Adjust(_saboteur, cfg.CutPenalty, "Reason.PowerCut");
            Nudge(cfg.NudgeCut, "grid sabotaged");
            try { SpawnResponse(player); }
            catch (Exception ex) { CLog($"[sabotage] response squad failed: {ex.GetType().Name}: {ex.Message}"); }
            player.ChatMessage(L("Notify.WindowStart", player.UserIDString, cfg.WindowMinutes));
            if (cfg.Broadcast) Broadcast("Broadcast.OutpostOpen", player.displayName, cfg.WindowMinutes);
            CLog($"[sabotage] OUTPOST OPEN by {player.displayName} ({how}): {cfg.WindowMinutes} min window, safe zone {(cfg.DropsSafeZone ? "down" : "kept")}, {offline} vanilla sentr{(offline == 1 ? "y" : "ies")} offline, {paused} spawn group(s) paused, {alerted} checkpoint(s) on alert, {cfg.CutPenalty} rep, {Mathf.Clamp(cfg.ResponseGuards, 0, 6)} guard(s) responding in {Mathf.Clamp(cfg.ResponseDelaySeconds, 0, 300)} s from {cfg.ResponseDistance:0} m; next window possible in {cfg.CooldownMinutes} min. Lights: {DescribeOutpostLights()}.");
            WindowTick();
        }

        private void WindowTick()
        {
            var bp = _saboteur != 0 ? BasePlayer.FindByID(_saboteur) : null;
            if (!IsDark || bp == null || !bp.IsConnected) { if (bp != null && bp.IsConnected) CuiHelper.DestroyUi(bp, UiWindow); return; }
            var left = Mathf.Max(0f, _darkUntil - Time.realtimeSinceStartup);
            var text = L("Ui.Window", bp.UserIDString, $"{(int)(left / 60f):00}:{(int)(left % 60f):00}");
            CuiHelper.DestroyUi(bp, UiWindow);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel { Image = { Color = "0.5 0.05 0.05 0.85" }, RectTransform = { AnchorMin = "0.40 0.92", AnchorMax = "0.60 0.97" } }, "Hud", UiWindow);
            ui.Add(new CuiLabel { Text = { Text = text, FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 0.9 0.9 1" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" } }, UiWindow);
            CuiHelper.AddUi(bp, ui);
        }

        // The compound's own sentries (NPCAutoTurret entities inside the Outpost bounds that are not
        // the plugin's): offline for the window, back online after; the plugin's own stay up.
        private int OfflineVanillaTurrets(bool off)
        {
            if (!_config.Sabotage.OfflineVanillaTurrets || _outpost == null) return 0;
            var n = 0;
            if (off)
            {
                var mine = new HashSet<BaseEntity>();
                foreach (var cp in _checkpoints.Values) foreach (var e in cp.Props) mine.Add(e);
                foreach (var e in BaseNetworkable.serverEntities)
                {
                    var t = e as NPCAutoTurret;
                    if (t == null || t.IsDestroyed || mine.Contains(t) || _looseProps.Contains(t) || !InMonument(_outpost, t.transform.position, 150f)) continue;
                    if (!t.IsOnline()) continue;
                    t.SetIsOnline(false); _vanillaTurretsOff.Add(t); n++;
                }
            }
            else
            {
                foreach (var t in _vanillaTurretsOff) { if (t == null || t.IsDestroyed) continue; t.SetIsOnline(true); n++; }
                _vanillaTurretsOff.Clear();
            }
            return n;
        }

        // Live counts for the fence's warning: Cobalt guards and turrets at Outpost (gates, vault, the compound's own sentries).
        private void OutpostDefenders(out int guards, out int turrets)
        {
            guards = 0; turrets = 0;
            var mine = new HashSet<BaseEntity>();
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.Monument != _outpost || !cp.IsActive) continue;
                guards += cp.LiveGuards();
                foreach (var e in cp.Props) { mine.Add(e); if (e is NPCAutoTurret && !e.IsDestroyed) turrets++; }
            }
            if (_outpost == null) return;
            // The compound's own sentries count only if the window leaves them up: with
            // OfflineVanillaTurrets on, the fence's "what's waiting" must not promise 20 sentries
            // that will be dark (owner's line review 2026-09-20).
            if (_config.Sabotage.OfflineVanillaTurrets) return;
            // A full entity scan per panel draw is too much for a button (review 2026-09-20) — the
            // count is refreshed every 30 s; the plugin's own are excluded.
            var now = Time.realtimeSinceStartup;
            if (now - _defenderTurretsAt > 30f)
            {
                _defenderTurretsAt = now; _defenderTurretsCached = 0;
                foreach (var e in BaseNetworkable.serverEntities)
                {
                    var t = e as NPCAutoTurret;
                    if (t == null || t.IsDestroyed || mine.Contains(t) || _looseProps.Contains(t) || !InMonument(_outpost, t.transform.position, 150f)) continue;
                    _defenderTurretsCached++;
                }
            }
            turrets += _defenderTurretsCached;
        }

        private void ResetSwitchLater()
        {
            timer.Once(1f, () => { if (_sabSwitch != null && !_sabSwitch.IsDestroyed && _sabSwitch.IsOn()) _sabSwitch.SetSwitch(false); });
        }

        // The window ends (timer, admin, reload, unload): zone back, sentries online, lights, HUD off.
        private void RestorePower(string why)
        {
            // "Open" means any residue of a window, not only a live clock: a window whose close timer
            // never ran (2026-09-20 11:25) must still be closable by the admin and on reload/unload.
            var wasOpen = IsDark || _zoneDown || _vanillaTurretsOff.Count > 0 || _pausedSpawners.Count > 0 || _saboteur != 0 || _darkTimer != null || _windowTick != null;
            if (!wasOpen && why != "unload" && why != "reload") return;
            _darkUntil = 0f;
            _darkTimer?.Destroy(); _darkTimer = null;
            _windowTick?.Destroy(); _windowTick = null;
            _responseSpawnTimer?.Destroy(); _responseSpawnTimer = null; // a squad not yet out stays home
            if (_windowAlertUntil > 0f)
            {
                // The window's own alert ends with it; a breach alert (a different stamp) stays.
                foreach (var cp in _checkpoints.Values) if (AtOutpost(cp) && cp.AlertUntil == _windowAlertUntil) cp.AlertUntil = 0f;
                _windowAlertUntil = 0f;
            }
            if (why == "admin") _cutCooldownUntil = 0f; // a test restore also frees the next window
            var bp = _saboteur != 0 ? BasePlayer.FindByID(_saboteur) : null;
            if (bp != null && bp.IsConnected) CuiHelper.DestroyUi(bp, UiWindow);
            _saboteur = 0;
            UpdatePropLights();
            DarkenMonumentLights(false);
            var online = OfflineVanillaTurrets(false);
            RestoreSpawners(why); // the window's pause and any probe delay override
            if (_zoneDown) SetZoneDown(false, why == "unload" || why == "reload" ? why : "window over");
            if (_sabSwitch != null && !_sabSwitch.IsDestroyed && _sabSwitch.IsOn()) _sabSwitch.SetSwitch(false);
            if (wasOpen) CLog($"[sabotage] OUTPOST CLOSED ({why}): {online} vanilla sentr{(online == 1 ? "y" : "ies")} back online. Lights: {DescribeOutpostLights()}.");
            // Every close the players can see gets the advisory (a tier drop closed the 12:02 window silently).
            if (wasOpen && _config.Sabotage.Broadcast && why != "unload" && why != "reload") Broadcast("Broadcast.OutpostClosed");
        }

        // The compound's own light entities (whatever the monument prefab spawned as IOEntities with a
        // light-like name, inside its bounds, not ours). Live 2026-09-19: with the plugin's floods off
        // the compound was still lit by its own beams. Off with the cut, back with the restore; the
        // original state is remembered per entity. If the monument's wiring re-powers them, the
        // listing (papers.cp lights) will show it and the next step is an OnEntityTakeDamage-style
        // dynamic hook on their power updates.
        private static bool LooksLikeALight(BaseEntity e)
        {
            var n = e.ShortPrefabName ?? "";
            return (n.Contains("light") || n.Contains("lamp") || n.Contains("flood") || n.Contains("spot")) && !n.Contains("siren") && !n.Contains("turret") && !n.Contains("switch");
        }

        private List<IOEntity> MonumentLights()
        {
            var list = new List<IOEntity>();
            if (_outpost == null) return list;
            var mine = new HashSet<BaseEntity>();
            foreach (var cp in _checkpoints.Values) foreach (var e in cp.Props) mine.Add(e);
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var io = e as IOEntity;
                if (io == null || io.IsDestroyed || mine.Contains(io) || _looseProps.Contains(io)) continue;
                if (!InMonument(_outpost, io.transform.position, 150f) || !LooksLikeALight(io)) continue;
                list.Add(io);
            }
            return list;
        }

        private void DarkenMonumentLights(bool dark)
        {
            // The restore branch ignores the flag: a reload with the flag turned off mid-window must
            // still bring back whatever this instance darkened (review 2026-09-20).
            if (dark && !_config.Sabotage.DarkenMonumentLights) return;
            if (dark)
            {
                var n = 0;
                foreach (var io in MonumentLights())
                {
                    if (_darkenedLights.ContainsKey(io)) continue;
                    _darkenedLights[io] = io.HasFlag(BaseEntity.Flags.On);
                    SetLit(io, false); n++;
                }
                CLog($"[sabotage] {n} monument light(s) switched off at Outpost.");
            }
            else
            {
                var n = 0;
                foreach (var kv in _darkenedLights)
                {
                    if (kv.Key == null || kv.Key.IsDestroyed) continue;
                    SetLit(kv.Key, kv.Value); n++;
                }
                if (_darkenedLights.Count > 0) CLog($"[sabotage] {n} monument light(s) restored at Outpost.");
                _darkenedLights.Clear();
            }
        }

        // The Outpost gates' floods, sirens and searchlights and whether each is lit — the proof of a cut.
        private string DescribeOutpostLights()
        {
            int floodOn = 0, floodOff = 0, sirenOn = 0, sirenOff = 0, lightOn = 0, lightOff = 0;
            foreach (var cp in _checkpoints.Values)
            {
                if (!cp.IsActive || cp.Monument != _outpost) continue;
                foreach (var e in cp.Props)
                {
                    string kind; var io = e as IOEntity;
                    if (io == null || io.IsDestroyed || !cp.PropKinds.TryGetValue(e, out kind)) continue;
                    var on = io.HasFlag(BaseEntity.Flags.On);
                    if (kind == "flood") { if (on) floodOn++; else floodOff++; }
                    else if (kind == "siren") { if (on) sirenOn++; else sirenOff++; }
                    else if (kind == "light") { if (on) lightOn++; else lightOff++; }
                }
            }
            return $"floods {floodOn} on / {floodOff} off, sirens {sirenOn} on / {sirenOff} off, searchlights {lightOn} on / {lightOff} off (night={IsNightNow()}, dark={IsDark})";
        }

        // Loose guards at the substation, on the saboteur, gone after ResponseMinutes (or with clearloose).
        // Owner's call 2026-09-20 (finding 7): a squad popping up beside the switch is no fight. The saboteur
        // is warned at the flip; ResponseDelaySeconds later the squad appears ResponseDistance metres
        // out on the Outpost side and runs in — the switch is their post, so the leash is measured
        // from it and they chase anyone near it.
        private void SpawnResponse(BasePlayer target)
        {
            var cfg = _config.Sabotage;
            DespawnResponse();
            var n = Mathf.Clamp(cfg.ResponseGuards, 0, 6);
            if (n == 0) { CLog("[sabotage] no response: ResponseGuards 0."); return; }
            var delay = Mathf.Clamp(cfg.ResponseDelaySeconds, 0, 300);
            target.ChatMessage(L("Notify.ResponseInbound", target.UserIDString, delay));
            var who = (ulong)target.userID;
            _responseSpawnTimer?.Destroy();
            _responseSpawnTimer = timer.Once(delay, () => { _responseSpawnTimer = null; SpawnResponseNow(who); });
        }

        private Timer _responseSpawnTimer; // the delayed squad (cancelled with the response)

        private void SpawnResponseNow(ulong who)
        {
            var cfg = _config.Sabotage;
            if (_sabSwitch == null || _sabSwitch.IsDestroyed || !IsDark) return; // the switch or the window went before they arrived
            var n = Mathf.Clamp(cfg.ResponseGuards, 0, 6);
            var target = BasePlayer.FindByID(who);
            // Not under the guard cap: at tier 5 the cap is full of gates and roadblocks (live 2026-09-19,
            // "no response: guard cap 30"), and this squad lives ResponseMinutes.
            var origin = ResponseOrigin(Mathf.Clamp(cfg.ResponseDistance, 0f, 200f));
            var toward = _sabSwitchPos - origin; toward.y = 0f;
            if (toward.sqrMagnitude < 0.01f) toward = _sabSwitchRot * Vector3.forward;
            toward.Normalize();
            var right = Vector3.Cross(Vector3.up, toward);
            var spawned = 0;
            for (var i = 0; i < n; i++)
            {
                var side = (i % 2 == 0) ? 1f : -1f;
                var gp = SnapToGround(origin + right * (1.5f + 1.5f * (i / 2)) * side);
                var g = SpawnGuard(gp, toward, false, null);
                if (g == null) continue;
                g.PostPos = _sabSwitchPos; // their post is the switch: they run in, then hold it
                _responseGuards.Add(g);
                spawned++;
                if (target != null && target.IsConnected && !target.IsDead()) g.GetComponent<GuardBrain>()?.ForceTarget(target);
            }
            CLog($"[sabotage] response: {spawned} guard(s) at {V(origin)}, {Vector3.Distance(origin, _sabSwitchPos):0} m from the switch, running in{(target != null ? " on " + target.displayName : "")}.");
            _responseTimer?.Destroy();
            _responseTimer = timer.Once(Mathf.Max(1, cfg.ResponseMinutes) * 60f, () => { _responseTimer = null; DespawnResponse(); CLog("[sabotage] response stood down."); });
        }

        // A navmesh point `distance` m from the switch toward Outpost (or along the switch's forward
        // with no Outpost); falls back to two thirds, one third, then the switch's own flank.
        private Vector3 ResponseOrigin(float distance)
        {
            var dir = _outpost != null ? _outpost.transform.position - _sabSwitchPos : _sabSwitchRot * Vector3.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
            foreach (var f in new[] { 1f, 0.66f, 0.33f })
            {
                var d = distance * f;
                if (d < 4f) break;
                var candidate = SnapToGround(_sabSwitchPos + dir * d);
                // No navmesh test (probe 2026-09-20 16:22: there is none under the guards; they move in
                // straight hops): dry ground within 10 m of the switch's height.
                if (!IsUnderwater(candidate) && Mathf.Abs(candidate.y - _sabSwitchPos.y) <= 10f) return candidate;
            }
            return SnapToGround(_sabSwitchPos + _sabSwitchRot * Vector3.forward * 3f);
        }

        private static bool IsUnderwater(Vector3 p) => WaterLevel.GetWaterDepth(p, true, true) > 0.3f;

        private void DespawnResponse()
        {
            _responseSpawnTimer?.Destroy(); _responseSpawnTimer = null;
            _responseTimer?.Destroy(); _responseTimer = null;
            foreach (var g in _responseGuards) { if (g == null || g.IsDestroyed) continue; _looseGuards.Remove(g); DespawnGuard(g); }
            _responseGuards.Clear();
        }

        private string DescribeSabotage(string viewerId)
        {
            var cfg = _config.Sabotage;
            var sb = new StringBuilder($"sabotage: {(cfg.Enabled ? "enabled" : "disabled")}, from tier {cfg.SabotageFromTier} (now {CurrentTier()}, {(SabotageArmed ? "armed" : "not armed")})");
            sb.Append('\n').Append("  switch: ").Append(string.IsNullOrEmpty(cfg.SwitchMonument) ? "not placed" : $"{cfg.SwitchMonument} local {cfg.SwitchLocalPosition} yaw {cfg.SwitchLocalYaw:0} — {(_sabSwitch != null && !_sabSwitch.IsDestroyed ? $"standing at {V(_sabSwitchPos)} (grid {SwitchGrid()}), on={_sabSwitch.IsOn()}" : "not spawned")}");
            int guards, turrets; OutpostDefenders(out guards, out turrets);
            sb.Append('\n').Append($"  tool: {cfg.ToolName} ({cfg.ToolItem}) {cfg.ToolScrap} scrap at the fence — {(ToolOnSale() ? "on sale" : "not on sale (needs the switch, an Outpost vault and tier " + cfg.SabotageFromTier + ")")}; Outpost defenders now: {guards} guard(s), {turrets} turret(s)");
            var now = Time.realtimeSinceStartup;
            sb.Append('\n').Append("  window: ").Append(IsDark ? $"OPEN, {(_darkUntil - now) / 60f:0.0} min left, saboteur {_saboteur}, zone {(_zoneDown ? "down" : "up")}, {_vanillaTurretsOff.Count} vanilla sentr{(_vanillaTurretsOff.Count == 1 ? "y" : "ies")} offline" : "closed").Append(now < _cutCooldownUntil ? $"; next possible in {(_cutCooldownUntil - now) / 60f:0.0} min" : "");
            sb.Append('\n').Append($"  response: {_responseGuards.Count} guard(s) out");
            sb.Append('\n').Append("  lights: ").Append(DescribeOutpostLights());
            return sb.ToString();
        }

        private string RunSabotage(string[] args, int first, BasePlayer player, string viewerId)
        {
            var cfg = _config.Sabotage;
            var sub = args.Length > first ? args[first].ToLowerInvariant() : "status";
            var what = args.Length > first + 1 ? args[first + 1].ToLowerInvariant() : null;
            switch (sub)
            {
                case "status":
                case "list":
                    return DescribeSabotage(viewerId);
                case "switch":
                {
                    if (player == null) return L("Sabotage.Usage", viewerId);
                    var pos = AimPoint(player);
                    var facing = player.transform.position - pos; facing.y = 0f;
                    var yaw = facing.sqrMagnitude > 0.01f ? YawOf(facing) : YawOf(player.eyes.BodyForward());
                    float dist = 0f; string alternatives = "";
                    MonumentInfo m;
                    {
                        // Substations are tiny and boundless: the bounds scorer prefers a big or a decor
                        // prefab hundreds of metres away (ue_oasis_c, live 2026-09-19). A given name wins;
                        // then any 'substation'; then the nearest monument origin within 80 m; then the scorer.
                        m = !string.IsNullOrEmpty(what) ? NearestMonument(pos, what, out dist, out alternatives) : null;
                        if (m == null && string.IsNullOrEmpty(what)) m = NearestMonument(pos, "substation", out dist, out alternatives);
                        if (m == null) m = MonumentByOrigin(pos, 80f);
                        if (m == null) m = NearestMonument(pos, null, out dist, out alternatives);
                        if (m != null) dist = Vector3.Distance(m.transform.position, pos);
                    }
                    if (m == null) return L("Vault.NoMonument", viewerId);
                    Vector3 local; float localYaw;
                    WorldToMonument(m, pos, yaw, out local, out localYaw);
                    cfg.SwitchMonument = MonumentShortName(m); cfg.SwitchLocalPosition = VecText(local); cfg.SwitchLocalYaw = (float)Math.Round(localYaw, 1); cfg.SwitchWorldPosition = VecText(pos);
                    SaveConfig();
                    _sabSwitchMissingLogged = false;
                    DespawnSabotage("re-placed"); ReconcileSabotage();
                    CLog($"[sabotage] switch captured at {V(pos)} yaw {yaw:0} → monument '{MonumentShortName(m)}' local {VecText(local)} yaw {localYaw:0}.");
                    return L("Sabotage.SwitchSet", viewerId, V(pos), MonumentShortName(m), VecText(local), cfg.SabotageFromTier)
                        + $" Owner {dist:0} m from its origin; {(_sabSwitch != null && !_sabSwitch.IsDestroyed ? $"standing at {V(_sabSwitchPos)}" : "not spawned (below tier?)")}. Nearby: {(string.IsNullOrEmpty(alternatives) ? "-" : alternatives)}";
                }
                case "remove":
                {
                    if (what == "switch") { cfg.SwitchMonument = ""; cfg.SwitchLocalPosition = "0 0 0"; SaveConfig(); DespawnSabotage("removed"); return L("Sabotage.Removed", viewerId, "switch"); }
                    return L("Sabotage.Usage", viewerId);
                }
                case "tp":
                {
                    if (player == null) return L("Sabotage.Usage", viewerId);
                    Vector3 spot;
                    if (what == "switch")
                    {
                        if (_sabSwitch == null || _sabSwitch.IsDestroyed) return L("Sabotage.NotSet", viewerId, "switch (or below tier)");
                        spot = _sabSwitchPos + _sabSwitchRot * Vector3.forward * 2f;
                    }
                    else return L("Sabotage.Usage", viewerId);
                    spot.y = Mathf.Max(spot.y, TerrainMeta.HeightMap.GetHeight(spot)) + 0.5f;
                    player.Teleport(spot);
                    return L("Sabotage.Teleported", viewerId, what);
                }
                case "cut":
                {
                    if (player == null) return L("Sabotage.Usage", viewerId);
                    if (_sabSwitch == null || _sabSwitch.IsDestroyed) return L("Sabotage.NotSet", viewerId, "switch (or below tier)");
                    OnPowerCut(player, true); // no tool needed from the console
                    return DescribeSabotage(viewerId);
                }
                case "restore":
                    RestorePower("admin");
                    return DescribeSabotage(viewerId);
                default:
                    return L("Sabotage.Usage", viewerId);
            }
        }

        [ConsoleCommand("papers.sabotage")]
        private void CmdSabotage(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            arg.ReplyWith(RunSabotage(ConsoleArgs(arg), 0, caller, caller?.UserIDString));
        }

        #endregion

        #region Perimeter (task 3.12 — a guard-free watch around every gated monument)

        // A road gate owns the road, not the monument: fence holes bypass it. The perimeter is one
        // trigger sphere per gated monument. Entering it starts a grace clock; when the clock runs
        // out with the player inside the monument bounds, at no gate, and without a recent gate
        // clearance, they have evaded the checkpoint: Suspect+ are flagged and the gates alerted,
        // Citizens/Neutrals get a warning (low tier) or a small penalty (PerimeterPenaltyFromTier+).

        public class PerimeterTrigger : TriggerBase
        {
            public Perimeter Owner;
        }

        public class Perimeter
        {
            public MonumentInfo Monument;
            public string Name;
            public Vector3 Centre;
            public float Radius;
            public GameObject Go;
            public float CreatedAt;
            public readonly Dictionary<ulong, float> ClearedUntil = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, float> VerdictCooldownUntil = new Dictionary<ulong, float>();
            public readonly Dictionary<ulong, Timer> Watching = new Dictionary<ulong, Timer>();
        }

        private readonly List<Perimeter> _perimeters = new List<Perimeter>();

        private Perimeter FindPerimeter(MonumentInfo m)
        {
            if (m == null) return null;
            foreach (var p in _perimeters) if (p.Monument == m) return p;
            return null;
        }

        private static string MonumentDisplay(MonumentInfo m)
        {
            if (m == null) return "?";
            var phrase = m.displayPhrase != null ? m.displayPhrase.english : null;
            return string.IsNullOrEmpty(phrase) ? MonumentShortName(m) : phrase.Trim();
        }

        private bool MonumentHasActiveGate(MonumentInfo m)
        {
            foreach (var cp in _checkpoints.Values) if (cp.Monument == m && cp.IsActive) return true;
            return false;
        }

        private void SpawnPerimeters()
        {
            if (!_config.Checkpoints.PerimeterEnabled) return;
            foreach (var cp in _checkpoints.Values)
            {
                if (!cp.Template.Perimeter || cp.Monument == null || FindPerimeter(cp.Monument) != null) continue;
                var radius = 0f;
                foreach (var other in _checkpoints.Values)
                    if (other.Monument == cp.Monument && other.Template.PerimeterRadius > radius) radius = other.Template.PerimeterRadius;
                var m = cp.Monument;
                if (radius <= 0f) radius = HasBounds(m) ? Mathf.Clamp(m.Bounds.extents.magnitude, 60f, 400f) : 80f;
                var per = new Perimeter
                {
                    Monument = m,
                    Name = MonumentShortName(m),
                    Centre = m.transform.TransformPoint(m.Bounds.center),
                    Radius = radius,
                    CreatedAt = Time.realtimeSinceStartup,
                };
                var go = new GameObject($"PapersPleasePerimeter:{per.Name}");
                go.layer = (int)global::Rust.Layer.Trigger;
                go.transform.position = per.Centre;
                var col = go.AddComponent<SphereCollider>();
                col.isTrigger = true;
                col.radius = radius;
                var trig = go.AddComponent<PerimeterTrigger>();
                trig.Owner = per;
                trig.InterestLayers = global::Rust.Layers.Mask.Player_Server;
                trig.OnEntityEnterTrigger = e => OnPerimeterEnter(per, e as BaseEntity);
                trig.OnEntityLeaveTrigger = e => OnPerimeterLeave(per, e as BaseEntity);
                per.Go = go;
                _perimeters.Add(per);
                CLog($"[perimeter] {per.Name} ({MonumentDisplay(m)}) centre {V(per.Centre)} r={radius:0} m, bounds {(HasBounds(m) ? V(m.Bounds.size) : "none (sphere fallback)")}.");
            }
        }

        private void DespawnPerimeters()
        {
            foreach (var per in _perimeters)
            {
                foreach (var t in per.Watching.Values) t?.Destroy();
                per.Watching.Clear();
                if (per.Go != null) UnityEngine.Object.Destroy(per.Go);
                per.Go = null;
            }
            _perimeters.Clear();
        }

        private void StampCleared(MonumentInfo m, ulong id)
        {
            var per = FindPerimeter(m);
            if (per == null) return;
            per.ClearedUntil[id] = Time.realtimeSinceStartup + Mathf.Max(1, _config.Checkpoints.PerimeterClearanceMinutes) * 60f;
            StopWatching(per, id);
        }

        private static void StopWatching(Perimeter per, ulong id)
        {
            Timer t;
            if (!per.Watching.TryGetValue(id, out t)) return;
            t?.Destroy();
            per.Watching.Remove(id);
        }

        private void OnPerimeterEnter(Perimeter per, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null || !IsRealPlayer(bp) || !bp.IsConnected || bp.IsDead()) return;
            var id = (ulong)bp.userID;
            if (IsExemptId(id) || per.Watching.ContainsKey(id)) return;
            var now = Time.realtimeSinceStartup;
            float until;
            if (per.ClearedUntil.TryGetValue(id, out until) && until > now) return;
            if (per.VerdictCooldownUntil.TryGetValue(id, out until) && until > now) return;
            if (now - per.CreatedAt < 5f)
            {
                // Already inside when the perimeter appeared (reload, gate added): not an evasion.
                per.ClearedUntil[id] = now + Mathf.Max(1, _config.Checkpoints.PerimeterClearanceMinutes) * 60f;
                CLog($"[perimeter] {per.Name}: {bp.displayName} was already inside at creation; cleared.");
                return;
            }
            var grace = Mathf.Max(10, _config.Checkpoints.PerimeterGraceSeconds);
            per.Watching[id] = timer.Every(grace, () => PerimeterCheck(per, bp));
            CLog($"[perimeter] {per.Name} ENTER {bp.displayName}; check in {grace} s.");
        }

        private void OnPerimeterLeave(Perimeter per, BaseEntity ent)
        {
            var bp = ent as BasePlayer;
            if (bp == null || !IsRealPlayer(bp)) return;
            StopWatching(per, (ulong)bp.userID);
        }

        private void PerimeterCheck(Perimeter per, BasePlayer bp)
        {
            if (bp == null || bp.IsDestroyed || !bp.IsConnected || bp.IsDead()) { if (bp != null) StopWatching(per, (ulong)bp.userID); return; }
            var id = (ulong)bp.userID;
            var now = Time.realtimeSinceStartup;
            float until;
            if (per.ClearedUntil.TryGetValue(id, out until) && until > now) { StopWatching(per, id); return; }
            if (per.VerdictCooldownUntil.TryGetValue(id, out until) && until > now) { StopWatching(per, id); return; }
            if (_encounters.ContainsKey(id)) return;                       // standing at a gate right now
            if (!InMonument(per.Monument, bp.transform.position, per.Radius)) return;   // in the sphere, not yet in the monument (the road outside)
            if (!MonumentHasActiveGate(per.Monument)) { StopWatching(per, id); return; } // no gate to clear = nothing to evade
            StopWatching(per, id);
            per.VerdictCooldownUntil[id] = now + Mathf.Max(30, _config.Checkpoints.PerimeterVerdictCooldownSeconds);
            EvadedVerdict(per, bp);
        }

        private void EvadedVerdict(Perimeter per, BasePlayer bp)
        {
            var id = (ulong)bp.userID;
            var band = BandOfPlayer(id);
            var tier = CurrentTier();
            var where = MonumentDisplay(per.Monument);
            Nudge(_config.Threat.NudgeEvaded, "checkpoint evaded");
            if (band >= Band.Suspect)
            {
                CLog($"[perimeter] {per.Name}: {bp.displayName} evaded as {band} at tier {tier} -> hostile + alert.");
                Adjust(id, _config.Reputation.Evaded, "Reason.Evaded");
                FlagHostile(bp);
                var until = Time.realtimeSinceStartup + Mathf.Max(1, _config.Checkpoints.AlertMinutes) * 60f;
                foreach (var cp in _checkpoints.Values) if (cp.Monument == per.Monument) cp.AlertUntil = until;
                AlertCheckpoints(bp.transform.position, bp.displayName, $"{band} evaded the {per.Name} checkpoint");
                Voice(bp, _config.Voice.Evaded);
                bp.ChatMessage(L("Perimeter.Hostile", bp.UserIDString, where));
                if (_config.Checkpoints.BroadcastRunAndKill) Broadcast("Broadcast.Evaded", bp.displayName, where);
                return;
            }
            if (tier >= _config.Checkpoints.PerimeterPenaltyFromTier)
            {
                CLog($"[perimeter] {per.Name}: {bp.displayName} evaded as {band} at tier {tier} -> penalty.");
                Adjust(id, _config.Reputation.Evaded, "Reason.Evaded");
                bp.ChatMessage(L("Perimeter.Penalty", bp.UserIDString, where));
                return;
            }
            CLog($"[perimeter] {per.Name}: {bp.displayName} evaded as {band} at tier {tier} -> warning only.");
            bp.ChatMessage(L("Perimeter.Warn", bp.UserIDString, where));
        }

        #endregion

        #region Roadblocks (decision 0006) — road sampler (task 5.1)

        public class RoadSample
        {
            public Vector3 Position;
            public Vector3 Forward;
            public float Width;
            public int RoadIndex;
            public int PointIndex;
        }

        // Straight, level, wide road stretch away from road ends, monuments, other checkpoints,
        // tool cupboards and water. Candidates are pooled over every eligible road (so long roads
        // dominate the draw) and the stretch is measured in metres, not points: live 2026-09-09,
        // ±3 points was a few metres and 40 of 40 draws failed on bend/end/slope.
        private RoadSample SampleRoad(out string report)
        {
            var cfg = _config.Roadblocks;
            var counts = new Dictionary<string, int>();
            Action<string> reject = why => { int n; counts.TryGetValue(why, out n); counts[why] = n + 1; };
            var roads = TerrainMeta.Path?.Roads;
            var eligible = new List<PathList>();
            var pool = 0; var spacingSum = 0f; var spacingN = 0;
            if (roads != null)
                foreach (var r in roads)
                {
                    if (r == null || r.Path == null || r.Path.Points == null || r.Width < cfg.RoadMinWidth || r.Path.Points.Length < 4) continue;
                    eligible.Add(r);
                    pool += r.Path.Points.Length;
                    if (r.Path.Points.Length > 1) { spacingSum += r.Path.Length / (r.Path.Points.Length - 1); spacingN++; }
                }
            if (eligible.Count == 0) { report = "no road on this map is wide enough"; return null; }
            var spacing = spacingN > 0 ? spacingSum / spacingN : 0f;
            var cupboards = CupboardPositions();
            var rails = RailPoints();
            RoadSample found = null;
            for (var attempt = 0; attempt < Mathf.Max(120, cfg.SampleRetries) && found == null; attempt++)
            {
                // Uniform over the pooled points, so a road's share of draws matches its length.
                var pick = UnityEngine.Random.Range(0, pool);
                PathList road = null; var i = 0;
                foreach (var r in eligible)
                {
                    if (pick < r.Path.Points.Length) { road = r; i = pick; break; }
                    pick -= r.Path.Points.Length;
                }
                if (road == null) continue;
                var pts = road.Path.Points;
                var p = pts[i];
                if (Vector3.Distance(p, pts[0]) < cfg.RoadMargin || Vector3.Distance(p, pts[pts.Length - 1]) < cfg.RoadMargin) { reject("near a road end"); continue; }
                int a, b;
                if (!Stretch(pts, i, cfg.StretchMetres, out a, out b)) { reject("short road"); continue; }
                var before = pts[i] - pts[a]; var after = pts[b] - pts[i];
                before.y = 0f; after.y = 0f;
                if (before.sqrMagnitude < 0.01f || after.sqrMagnitude < 0.01f) { reject("degenerate segment"); continue; }
                if (Vector3.Angle(before, after) > cfg.MaxBendDegrees) { reject("bend"); continue; }
                if (Mathf.Abs(pts[b].y - pts[a].y) > cfg.MaxRise) { reject("slope"); continue; }
                if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(p) > p.y + 0.1f) { reject("water"); continue; }
                string who;
                if (NearMonument(p, cfg.MonumentClearance, out who)) { reject("monument:" + who); continue; }
                if (NearCheckpoint(p, cfg.CheckpointClearance)) { reject("checkpoint"); continue; }
                if (NearRealPlayer(p, cfg.PlayerClearance)) { reject("player"); continue; }
                if (NearAny(cupboards, p, cfg.CupboardClearance)) { reject("cupboard"); continue; }
                if (NearAny(rails, p, cfg.RailClearance)) { reject("rail"); continue; }
                var fwd = pts[b] - pts[a]; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) { reject("degenerate segment"); continue; }
                fwd.Normalize();
                // Task 5.7 height check: the ground where the guards will stand (Width/2 + 1.5 m off
                // the centre line, both sides) must be level with the road — a bridge deck's edge or
                // an embankment drops the guard into the water or down the bank.
                var ground = SnapToGround(p);
                var right = Vector3.Cross(Vector3.up, fwd);
                var flank = road.Width / 2f + 1.5f;
                var dropL = Mathf.Abs(SnapToGround(p - right * flank).y - ground.y);
                var dropR = Mathf.Abs(SnapToGround(p + right * flank).y - ground.y);
                if (Mathf.Max(dropL, dropR) > cfg.MaxEdgeDrop) { reject("edge drop"); continue; }
                found = new RoadSample
                {
                    Position = ground,
                    Forward = fwd,
                    Width = road.Width,
                    RoadIndex = roads.IndexOf(road),
                    PointIndex = i,
                };
            }
            var sb = new StringBuilder();
            sb.Append(eligible.Count).Append(" eligible road(s), ").Append(pool).Append(" points (").Append(spacing.ToString("0.0")).Append(" m apart), ").Append(cupboards.Count).Append(" cupboard(s), ").Append(rails.Count).Append(" rail point(s); rejections:");
            if (counts.Count == 0) sb.Append(" none");
            foreach (var kv in counts) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            report = sb.ToString();
            return found;
        }

        // Indices `metres` before and after point i along the path; false if the road is too short.
        private static bool Stretch(Vector3[] pts, int i, float metres, out int a, out int b)
        {
            a = i; b = i;
            var d = 0f;
            while (a > 0 && d < metres) { d += Vector3.Distance(pts[a], pts[a - 1]); a--; }
            if (d < metres) return false;
            d = 0f;
            while (b < pts.Length - 1 && d < metres) { d += Vector3.Distance(pts[b], pts[b + 1]); b++; }
            return d >= metres;
        }

        // A monument with map-sized bounds (live 2026-09-09: one entry rejected every draw) is
        // judged by origin distance like a boundless one; `who` names the rejecter for the tally.
        private static bool NearMonument(Vector3 p, float clearance, out string who)
        {
            who = null;
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return false;
            foreach (var m in monuments)
            {
                if (m == null) continue;
                var near = false;
                if (HasBounds(m) && m.Bounds.size.magnitude < 900f)
                {
                    var local = m.transform.InverseTransformPoint(p);
                    near = Mathf.Sqrt(m.Bounds.SqrDistance(local)) < clearance;
                }
                else near = Vector3.Distance(m.transform.position, p) < clearance * 0.5f;
                if (near) { who = MonumentShortName(m); return true; }
            }
            return false;
        }

        private static bool NearRealPlayer(Vector3 p, float clearance)
        {
            if (clearance <= 0f) return false;
            foreach (var bp in BasePlayer.activePlayerList)
                if (IsRealPlayer(bp) && !bp.IsDead() && Vector3.Distance(bp.transform.position, p) < clearance) return true;
            return false;
        }

        private bool NearCheckpoint(Vector3 p, float clearance)
        {
            foreach (var cp in _checkpoints.Values)
            {
                // A destroyed roadblock slot waiting to respawn has no pose yet (Position is the
                // origin); it must not fence off the middle of the map.
                if (cp.Kind == CheckpointKind.Roadblock && cp.Road == null) continue;
                if (Vector3.Distance(cp.Position, p) < clearance) return true;
            }
            return false;
        }

        // Every rail point on the map, pooled once per sampling batch like the cupboards.
        private static List<Vector3> RailPoints()
        {
            var list = new List<Vector3>();
            var rails = TerrainMeta.Path?.Rails;
            if (rails == null) return list;
            foreach (var r in rails)
            {
                if (r == null || r.Path == null || r.Path.Points == null) continue;
                list.AddRange(r.Path.Points);
            }
            return list;
        }

        private static bool NearAny(List<Vector3> points, Vector3 p, float clearance)
        {
            var c2 = clearance * clearance;
            foreach (var q in points) if ((q - p).sqrMagnitude < c2) return true;
            return false;
        }

        // ---- roadblock slots (task 5.2) ----

        private static string RoadblockSlot(int i) => $"roadblock-{i}";

        private int GateGuardsWanted(int tier)
        {
            var n = 0;
            foreach (var cp in _checkpoints.Values)
                if (cp.Kind == CheckpointKind.Gate && (cp.State == CheckpointState.Active || cp.Template.MinTier <= tier && cp.State != CheckpointState.Destroyed))
                    n += cp.GuardCount(_config.Checkpoints, tier);
            return n;
        }

        // Roadblocks yield to gates: as many as the tier wants, but only what the cap leaves
        // after every eligible gate is manned (decision 0006 §3).
        private int RoadblocksWanted(int tier)
        {
            var byTier = _config.Roadblocks.RoadblocksByTier;
            if (byTier == null || byTier.Length == 0) return 0;
            var want = Mathf.Max(0, byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1]);
            var byGuards = _config.Checkpoints.GuardsByTier;
            var per = byGuards != null && byGuards.Length > 0
                ? Mathf.Clamp(byGuards[Mathf.Clamp(tier, 1, byGuards.Length) - 1], 1, 8)
                : Mathf.Max(1, _config.Checkpoints.GuardsPerGate);
            var room = (_config.Checkpoints.GuardCap - GateGuardsWanted(tier)) / per;
            return Mathf.Clamp(Mathf.Min(want, room), 0, 16);
        }

        private List<Checkpoint> RoadblockSlots()
        {
            var list = new List<Checkpoint>();
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Roadblock && !cp.Pinned) list.Add(cp);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        private void TrimRoadblocks(int tier)
        {
            var wanted = RoadblocksWanted(tier);
            var slots = RoadblockSlots();
            for (var i = slots.Count - 1; i >= 0 && slots.Count > wanted; i--)
            {
                var cp = slots[i];
                Despawn(cp, "roadblock trimmed");
                _checkpoints.Remove(cp.Name);
                slots.RemoveAt(i);
                CLog($"[road] '{cp.Name}' removed: {wanted} roadblock(s) wanted at tier {tier} under the cap.");
            }
        }

        private void FillRoadblocks(int tier)
        {
            var wanted = RoadblocksWanted(tier);
            var slots = RoadblockSlots();
            var index = 1;
            while (slots.Count < wanted && index <= 32)
            {
                var name = RoadblockSlot(index++);
                if (_checkpoints.ContainsKey(name)) continue;
                var cp = NewRoadblock(name);
                if (cp == null) break; // no road spot found this round; the next reconcile retries
                slots.Add(cp);
            }
        }

        private Checkpoint NewRoadblock(string name)
        {
            var cp = new Checkpoint
            {
                Name = name,
                Kind = CheckpointKind.Roadblock,
                Template = new GateTemplate { Name = name, Monument = "road", Searchlight = true, Perimeter = false },
            };
            DateTime until;
            if (_cpData.DestroyedUntil.TryGetValue(name, out until) && until > DateTime.UtcNow)
            {
                _checkpoints[name] = cp;
                cp.DestroyedUntilUtc = until;
                cp.State = CheckpointState.Destroyed;
                cp.StateNote = $"destroyed until {until:HH:mm} UTC";
                ScheduleRespawn(cp);
                return cp;
            }
            if (!MoveRoadblock(cp)) return null;
            _checkpoints[name] = cp;
            TrySpawn(cp);
            return cp;
        }

        private bool MoveRoadblock(Checkpoint cp)
        {
            string report;
            var sample = SampleRoad(out report);
            if (sample == null) { CLog($"[road] no spot for '{cp.Name}': {report}"); return false; }
            cp.Road = sample;
            cp.Position = sample.Position;
            cp.Rotation = Quaternion.LookRotation(sample.Forward);
            CLog($"[road] '{cp.Name}' at {V(cp.Position)} on road #{sample.RoadIndex} (w={sample.Width:0.0}); {report}");
            return true;
        }

        // ---- relocation (task 5.3) ----

        private bool RoadblockBusy(Checkpoint cp, out string why)
        {
            why = null;
            foreach (var e in _encounters.Values) if (e.Gate == cp) { why = "encounter in progress"; return true; }
            var r2 = _config.Roadblocks.RelocateClearMetres * _config.Roadblocks.RelocateClearMetres;
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && IsRealPlayer(p) && !p.IsDead() && (p.transform.position - cp.Position).sqrMagnitude <= r2) { why = $"{p.displayName} within {_config.Roadblocks.RelocateClearMetres:0} m"; return true; }
            return false;
        }

        // Runs on the 5-minute reconcile: a roadblock older than RelocateMinutes moves as soon as
        // nobody is near it and no encounter is running there.
        private void RelocateDueRoadblocks()
        {
            var due = Mathf.Max(5, _config.Roadblocks.RelocateMinutes) * 60f;
            var now = Time.realtimeSinceStartup;
            foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
            {
                if (cp.Kind != CheckpointKind.Roadblock || cp.Pinned || cp.State != CheckpointState.Active) continue;
                if (now - cp.PlacedAt < due) continue;
                string why;
                if (RoadblockBusy(cp, out why)) { CLog($"[road] '{cp.Name}' relocation deferred: {why}."); continue; }
                RelocateRoadblock(cp, "due");
            }
        }

        // Sample first so a miss keeps the old spot; then move guards, props and trigger.
        private bool RelocateRoadblock(Checkpoint cp, string why)
        {
            string report;
            var sample = SampleRoad(out report);
            // A miss does not restart the hour: the next 5-minute reconcile draws again (review 2026-09-12).
            if (sample == null) { CLog($"[road] '{cp.Name}' stays: no new spot ({report})."); return false; }
            var from = cp.Position;
            Despawn(cp, "relocating");
            cp.Road = sample;
            cp.Position = sample.Position;
            cp.Rotation = Quaternion.LookRotation(sample.Forward);
            cp.CooldownUntil.Clear(); cp.WarnedUntil.Clear(); cp.RunOverUntil.Clear(); // a fresh site
            cp.State = CheckpointState.Unspawned;
            var ok = TrySpawn(cp);
            CLog($"[road] '{cp.Name}' relocated ({why}) {V(from)} → {V(cp.Position)} on road #{sample.RoadIndex}{(ok ? "" : " — not spawned: " + cp.StateNote)}.");
            return ok;
        }

        private string DescribeRoadblocks(string viewerId)
        {
            var sb = new StringBuilder();
            var tier = CurrentTier();
            var byTier = _config.Roadblocks.RoadblocksByTier;
            var byTierNow = byTier.Length == 0 ? 0 : byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1]; // [] = roadblocks off (review 2026-09-12)
            sb.Append($"roadblocks: {RoadblockSlots().Count} of {RoadblocksWanted(tier)} wanted at tier {tier} (byTier {byTierNow}, gates want {GateGuardsWanted(tier)} of {_config.Checkpoints.GuardCap} guards)");
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.Kind != CheckpointKind.Roadblock) continue;
                var age = cp.State == CheckpointState.Active ? (Time.realtimeSinceStartup - cp.PlacedAt) / 60f : 0f;
                var road = cp.Road != null ? $"road #{cp.Road.RoadIndex} w={cp.Road.Width:0.0}" : "no road";
                var status = cp.State == CheckpointState.Active ? $"active guards={cp.LiveGuards()} age={age:0} min" : $"{cp.State} {cp.StateNote}";
                sb.Append('\n').Append($"  {cp.Name}{(cp.Pinned ? " (pinned)" : "")} @ {V(cp.Position)} {road} | {status}");
            }
            return sb.ToString();
        }

        // One pass over the entity list per sampling batch (not per retry).
        private static List<Vector3> CupboardPositions()
        {
            var list = new List<Vector3>();
            foreach (var e in BaseNetworkable.serverEntities)
            {
                var priv = e as BuildingPrivlidge;
                if (priv != null && !priv.IsDestroyed) list.Add(priv.transform.position);
            }
            return list;
        }

        #endregion

        #region Outpost (decision 0007 — curfew, night hostility, Green Zone dressing)

        private TriggerSafeZone _zone;
        private SphereCollider _zoneSphere;
        private float _zoneOriginalRadius;
        private MonumentInfo _outpost;
        private bool _curfewOn;
        private int _curfewForce = -1;   // -1 follows the clock and the tier; 0/1 forced by an admin (not persisted)
        private bool _zoneDown;          // decision 0009 §12: the Outpost safe zone shrunk to nothing (the vault window; papers.cp safezone off for tests). Not persisted; restored on unload/reload.
        private Timer _outpostTimer;
        // Loose props from `papers.cp turret here`; killed by clearloose and on Unload.
        private readonly List<BaseEntity> _looseProps = new List<BaseEntity>();

        // The sphere TriggerSafeZone inside the configured monument's bounds (Outpost has exactly
        // one, r=122.1 m, offset from the monument origin — probe 1.7).
        private void InitOutpost()
        {
            _outpost = null; _zone = null; _zoneSphere = null; _curfewOn = false;
            var name = _config.Outpost.Monument;
            var monuments = MonumentsNamed(name);
            if (monuments.Count == 0) { CLog($"[outpost] no monument named '{name}' on this map; curfew disabled."); return; }
            _outpost = monuments[0];
            foreach (var z in TriggerSafeZone.allSafeZones)
            {
                if (z == null) continue;
                var sphere = z.triggerCollider as SphereCollider;
                if (sphere == null || !InMonument(_outpost, z.transform.position, 150f)) continue;
                _zone = z; _zoneSphere = sphere;
                break;
            }
            if (_zone == null) { CLog($"[outpost] {name}: no sphere safe zone inside its bounds; curfew disabled."); return; }
            var cfg = _config.Outpost;
            // A build that died mid-curfew leaves the collider shrunk in memory; the remembered
            // stock radius puts it back before it is taken as the original.
            // The prefab's stock radius never shrinks, so a live radius below the remembered one is
            // a leftover curfew whatever CurfewRadius is today (review 2026-09-12: matching the
            // current CurfewRadius missed a changed config and then overwrote the memory).
            var stored = _cpData.OutpostZoneRadius;
            if (stored > 5f && stored > _zoneSphere.radius + 0.05f)
            {
                CLog($"[outpost] safe zone found shrunk ({_zoneSphere.radius:0.0} m) on load; restoring the remembered {stored:0.0} m.");
                _zoneSphere.radius = stored;
            }
            _zoneOriginalRadius = _zoneSphere.radius;
            if (_zoneOriginalRadius > _cpData.OutpostZoneRadius + 0.01f) { _cpData.OutpostZoneRadius = _zoneOriginalRadius; _cpDirty = true; }
            CLog($"[outpost] {name}: safe zone at {V(_zone.transform.position)} r={_zoneOriginalRadius:0.0} m; curfew from tier {cfg.CurfewFromTier}, {cfg.CurfewStartHour:0}:00-{cfg.CurfewEndHour:0}:00 in-game → r={cfg.CurfewRadius:0} m.");
        }

        private static bool IsNight(float hour, float start, float end) =>
            start > end ? (hour >= start || hour < end) : (hour >= start && hour < end);

        private bool IsNightNow() => IsNight(ConVar.Env.time, _config.Outpost.CurfewStartHour, _config.Outpost.CurfewEndHour);

        private bool CurfewWanted()
        {
            if (_zone == null) return false;
            if (_curfewForce >= 0) return _curfewForce == 1;
            return CurrentTier() >= _config.Outpost.CurfewFromTier && IsNightNow();
        }

        // 60 s tick, after every checkpoint reconcile, and on demand: start or end the curfew when
        // the clock, the tier or an admin says so; keep the alert sirens and night floods current.
        // The zone falls or returns: the curfew resize re-applied with the flag (decision 0009 §12).
        private void SetZoneDown(bool down, string why)
        {
            if (_zone == null || _zoneSphere == null) { _zoneDown = false; return; }
            if (_zoneDown == down) return;
            _zoneDown = down;
            SetCurfew(_curfewOn, why, false);
        }

        private void OutpostTick(bool announce)
        {
            if (_zone != null)
            {
                var want = CurfewWanted();
                if (want != _curfewOn) SetCurfew(want, _curfewForce >= 0 ? "admin" : (want ? "dusk" : "dawn"), announce);
            }
            UpdatePropLights();
        }

        private void SetCurfew(bool on, string why, bool announce)
        {
            if (_zone == null || _zoneSphere == null) { _curfewOn = false; return; }
            var cfg = _config.Outpost;
            _curfewOn = on;
            var radius = _zoneDown ? 0.5f : (on ? Mathf.Clamp(cfg.CurfewRadius, 5f, _zoneOriginalRadius) : _zoneOriginalRadius);
            _zoneSphere.radius = radius;
            var centre = _zone.transform.position;
            var reach = _zoneOriginalRadius + 10f;
            var refreshed = 0; var warned = 0;
            // Membership only changes on enter/exit; a resize needs an explicit refresh for everyone
            // inside the old sphere — players and our guards alike (ApartmentComplexUnlock pattern).
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected) continue;
                var d = Vector3.Distance(p.transform.position, centre);
                if (d > reach) continue;
                p.ForceUpdateTriggers();
                refreshed++;
                // Warn only the vacated ring: inside the stock zone but outside the curfew one
                // (the +10 m reach is for the trigger refresh, not the message — review 2026-09-12).
                if (on && announce && d > radius && d <= _zoneOriginalRadius && IsRealPlayer(p)) { p.ChatMessage(L("Curfew.Warn", p.UserIDString)); warned++; }
            }
            foreach (var cp in _checkpoints.Values)
            {
                foreach (var g in cp.Guards)
                    if (g != null && !g.IsDestroyed && Vector3.Distance(g.transform.position, centre) <= reach) { g.ForceUpdateTriggers(); refreshed++; }
                if (cp.Kind == CheckpointKind.Gate && cp.Monument == _outpost) cp.Curfew = on && cfg.CurfewSuspectHostile;
            }
            CLog($"[outpost] curfew {(on ? "ON" : "off")}{(_zoneDown ? ", SAFE ZONE DOWN" : "")} ({why}): safe zone r={radius:0.0} m; {refreshed} entity(s) refreshed, {warned} player(s) warned in the vacated ring.");
            if (announce)
                Broadcast("Broadcast.Curfew", Pick(on ? _config.Voice.CurfewStart : _config.Voice.CurfewEnd, on ? "Cobalt advisory: curfew in effect at Outpost." : "Cobalt advisory: curfew lifted."));
            timer.Once(1f, () =>
            {
                foreach (var cp in _checkpoints.Values)
                {
                    if (cp.Monument != _outpost || !cp.IsActive || cp.Guards.Count == 0 || cp.Guards[0] == null || cp.Guards[0].IsDestroyed) continue;
                    cp.InSafeZone = cp.Guards[0].InSafeZone();
                    CLog($"[outpost] '{cp.Name}' InSafeZone={cp.InSafeZone} with the curfew {(on ? "on" : "off")}.");
                }
            });
        }

        // ---- props with a minimum tier, sirens and floods (task 6.4) ----

        private static int PropsAtTier(GateTemplate t, int tier)
        {
            var n = 0;
            foreach (var p in t.Props) if (p.MinTier <= tier) n++;
            return n;
        }

        private int LiveTurretCount()
        {
            var n = 0;
            foreach (var cp in _checkpoints.Values)
                foreach (var e in cp.Props) if (e is NPCAutoTurret && !e.IsDestroyed) n++;
            foreach (var e in _looseProps) if (e is NPCAutoTurret && !e.IsDestroyed) n++;
            return n;
        }

        private bool WantLit(Checkpoint cp, string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "siren": return cp.IsAlert || (IsDark && AtOutpost(cp));
                case "flood": return IsNightNow() && !(IsDark && AtOutpost(cp));
                default: return !(IsDark && AtOutpost(cp)); // searchlights: always on, except at Outpost while the window is open
            }
        }

        private static void SetLit(IOEntity io, bool on)
        {
            io.UpdateHasPower(on ? Mathf.Max(25, io.ConsumptionAmount() + 1) : 0, 0);
            SetFlagNet(io, BaseEntity.Flags.On, on);
        }

        private void UpdatePropLights()
        {
            foreach (var cp in _checkpoints.Values)
            {
                if (!cp.IsActive) continue;
                foreach (var e in cp.Props)
                {
                    var io = e as IOEntity;
                    string kind;
                    if (io == null || io.IsDestroyed || !cp.PropKinds.TryGetValue(e, out kind) || (kind != "siren" && kind != "flood" && kind != "light")) continue;
                    var want = WantLit(cp, kind);
                    if (io.HasFlag(BaseEntity.Flags.On) != want) SetLit(io, want);
                }
            }
        }

        private string DescribeOutpost()
        {
            var cfg = _config.Outpost;
            if (_zone == null || _zoneSphere == null) return $"outpost: no sphere safe zone for '{cfg.Monument}' on this map; curfew disabled. turrets={LiveTurretCount()}/{cfg.TurretCap}";
            var hour = ConVar.Env.time;
            var to = _curfewOn ? cfg.CurfewEndHour : cfg.CurfewStartHour;
            var wait = Mathf.Repeat(to - hour, 24f);
            var mode = _curfewForce < 0 ? "auto" : (_curfewForce == 1 ? "forced on" : "forced off");
            var sb = new StringBuilder();
            sb.Append($"outpost: {MonumentShortName(_outpost)} safe zone r={_zoneSphere.radius:0.0} m (stock {_zoneOriginalRadius:0.0}, curfew {cfg.CurfewRadius:0}) | curfew={(_curfewOn ? "ON" : "off")} mode={mode} tier={CurrentTier()} (curfew from {cfg.CurfewFromTier}) time={hour:0.00} night={IsNightNow()} next {(_curfewOn ? "dawn" : "dusk")} in {wait:0.0} in-game h | turrets={LiveTurretCount()}/{cfg.TurretCap}");
            foreach (var cp in _checkpoints.Values)
            {
                if (cp.Monument != _outpost) continue;
                var above = cp.Template.Props.Count - PropsAtTier(cp.Template, CurrentTier());
                sb.Append('\n').Append($"  {cp.Name}: {cp.State} curfew={cp.Curfew} alert={cp.IsAlert} inSafeZone={cp.InSafeZone} props={cp.Props.Count} live{(above > 0 ? $", {above} above tier" : "")}");
            }
            return sb.ToString();
        }

        #endregion

        #region Checkpoint placement tool (task 3.2)

        private static string SurfaceOf(Checkpoint cp)
        {
            foreach (var g in cp.Guards)
            {
                var brain = g != null && !g.IsDestroyed ? g.GetComponent<GuardBrain>() : null;
                if (brain != null) return brain.Surface;
            }
            return cp.Surface;
        }

        private Checkpoint FindCheckpoint(string name)
        {
            Checkpoint cp;
            if (_checkpoints.TryGetValue(name, out cp)) return cp;
            foreach (var kv in _checkpoints) if (kv.Key.StartsWith(name + "#", StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // Letters, digits, '-', '_' only: names become dictionary keys, GameObject names and the
        // "name#n" instance suffix, and arrive from chat.
        private static bool IsGateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 32) return false;
            foreach (var c in name) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
            return true;
        }

        private GateTemplate FindTemplate(string name)
        {
            foreach (var t in _config.Gates) if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private void RespawnTemplate(GateTemplate t)
        {
            foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
                if (cp.Template == t) { Despawn(cp, "respawn"); _checkpoints.Remove(cp.Name); }
            foreach (var cp in ResolveTemplate(t))
            {
                if (_checkpoints.ContainsKey(cp.Name)) continue;
                _checkpoints[cp.Name] = cp;
                TrySpawn(cp);
            }
            SpawnPerimeters();
        }

        private PropTemplate AddProp(Checkpoint cp, string kind, int minTier, Vector3 offset, float yaw)
        {
            var p = new PropTemplate { Kind = kind, Offset = VecText(offset), Yaw = (float)Math.Round(Mathf.Repeat(yaw, 360f), 1), MinTier = minTier };
            cp.Template.Props.Add(p);
            SaveConfig();
            RespawnTemplate(cp.Template);
            return p;
        }

        // `here` = the caller's aim point and facing; `at <x> <y> <z> [yaw]` = console coordinates.
        private static bool SpotFromArgs(string[] args, string mode, BasePlayer caller, out Vector3 pos, out Vector3 forward)
        {
            pos = Vector3.zero; forward = Vector3.forward;
            if (string.Equals(mode, "here", StringComparison.OrdinalIgnoreCase) && caller != null)
            {
                pos = AimPoint(caller);
                forward = caller.eyes.BodyForward(); forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                return true;
            }
            float x, y, z, yaw = 0f;
            if (!string.Equals(mode, "at", StringComparison.OrdinalIgnoreCase) || args.Length < 5
                || !float.TryParse(args[2], out x) || !float.TryParse(args[3], out y) || !float.TryParse(args[4], out z)
                || (args.Length > 5 && !float.TryParse(args[5], out yaw))) return false;
            pos = SnapToGround(new Vector3(x, y, z));
            forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            return true;
        }

        private int RespawnAllCheckpoints()
        {
            if (_curfewOn || _zoneDown) { _zoneDown = false; SetCurfew(false, "reload", false); }
            DespawnPerimeters();
            DespawnAllCheckpoints("reload");
            DespawnAllFences();
            InitOutpost();
            DespawnSabotage("reload");
            DespawnAllSquads("reload");
            SpawnAllCheckpoints();
            SpawnPerimeters();
            SpawnAllFences();
            OutpostTick(false);
            ReconcileSabotage();
            return _checkpoints.Count;
        }

        // Shared by /papers gate … and papers.cp gate …; `player` may be null from the server console.
        private string RunGate(string[] args, int first, BasePlayer player, string viewerId)
        {
            var sub = args.Length > first ? args[first].ToLowerInvariant() : "list";
            var name = args.Length > first + 1 ? args[first + 1] : null;
            switch (sub)
            {
                case "list":
                {
                    if (_config.Gates.Count == 0) return L("Gate.ListNone", viewerId);
                    var sb = new StringBuilder(L("Gate.ListHeader", viewerId, _config.Gates.Count, _checkpoints.Count, LiveGuardCount(), _config.Checkpoints.GuardCap, CurrentTier()));
                    foreach (var cp in _checkpoints.Values)
                    {
                        if (cp.Kind != CheckpointKind.Gate) continue;
                        var status = cp.State == CheckpointState.Active
                            ? $"active guards={cp.LiveGuards()} alert={cp.IsAlert}{(cp.Curfew ? " (curfew)" : "")} inSafeZone={cp.InSafeZone} surface={SurfaceOf(cp)}"
                            : $"{cp.State} {cp.StateNote}";
                        sb.Append('\n').Append(L("Gate.ListLine", viewerId, cp.Name, V(cp.Position), cp.Template.LocalPosition, cp.Template.LocalYaw,
                            cp.GuardCount(_config.Checkpoints, CurrentTier()), cp.Radius(_config.Checkpoints), cp.Template.MinTier, cp.Template.Props.Count + (cp.Template.Searchlight ? 1 : 0), status));
                    }
                    sb.Append('\n').Append(DescribeRoadblocks(viewerId));
                    foreach (var per in _perimeters)
                        sb.Append('\n').Append($"  perimeter {per.Name} centre {V(per.Centre)} r={per.Radius:0} watching={per.Watching.Count} cleared={per.ClearedUntil.Count}");
                    foreach (var t in _config.Gates)
                    {
                        var placed = false;
                        foreach (var cp in _checkpoints.Values) if (cp.Template == t) { placed = true; break; }
                        if (!placed) sb.Append('\n').Append($"  {t.Name} @ {t.Monument} local {t.LocalPosition} yaw {t.LocalYaw:0} | no such monument on this map");
                    }
                    return sb.ToString();
                }
                case "add":
                {
                    if (player == null || string.IsNullOrEmpty(name) || !IsGateName(name)) return L("Gate.Usage", viewerId);
                    if (FindTemplate(name) != null) return L("Gate.Exists", viewerId, name);
                    int guards = 0; string needle = null;
                    if (args.Length > first + 2 && !int.TryParse(args[first + 2], out guards)) needle = args[first + 2];
                    if (args.Length > first + 3) needle = args[first + 3];
                    guards = Mathf.Clamp(guards, 0, 8);
                    float dist; string alternatives;
                    var m = NearestMonument(player.transform.position, needle, out dist, out alternatives);
                    if (m == null) return L("Gate.NoMonument", viewerId);
                    var pos = player.transform.position;
                    var yaw = YawOf(player.eyes.BodyForward());
                    Vector3 local; float localYaw;
                    WorldToMonument(m, pos, yaw, out local, out localYaw);
                    var t = new GateTemplate
                    {
                        Name = name,
                        Monument = MonumentShortName(m),
                        LocalPosition = VecText(local),
                        LocalYaw = (float)Math.Round(localYaw, 1),
                        Guards = guards,
                    };
                    _config.Gates.Add(t);
                    SaveConfig();
                    var inZone = player.InSafeZone();
                    CLog($"[gate] '{name}' captured at {V(pos)} yaw {yaw:0} → monument '{t.Monument}' local {t.LocalPosition} yaw {t.LocalYaw:0}; InSafeZone={inZone}; {dist:0} m from the monument origin.");
                    RespawnTemplate(t);
                    var instances = 0;
                    foreach (var cp in _checkpoints.Values) if (cp.Template == t) instances++;
                    return L("Gate.Added", viewerId, name, t.Monument, t.LocalPosition, t.LocalYaw, dist, inZone, instances, alternatives);
                }
                case "remove":
                {
                    var t = name != null ? FindTemplate(name) : null;
                    if (t == null) return L("Gate.NotFound", viewerId, name ?? "");
                    foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
                        if (cp.Template == t) { Despawn(cp, "removed"); _checkpoints.Remove(cp.Name); if (_cpData.DestroyedUntil.Remove(cp.Name)) _cpDirty = true; }
                    _config.Gates.Remove(t);
                    _cpDirty = true;
                    SaveConfig();
                    DespawnPerimeters();
                    SpawnPerimeters();
                    return L("Gate.Removed", viewerId, t.Name);
                }
                case "tp":
                {
                    if (player == null) return L("Gate.Usage", viewerId);
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) return L("Gate.NotFound", viewerId, name ?? "");
                    // Outside the trigger, on the approach side, on the ground there — not at the
                    // gate's height: 14 m along a rising road put the body 0.6 m under the terrain
                    // and the anti-hack kicked on every login (live 2026-09-12).
                    var spot = SnapToGround(cp.Position + cp.Forward * 14f);
                    spot.y = Mathf.Max(spot.y, TerrainMeta.HeightMap.GetHeight(spot)) + 0.5f;
                    player.Teleport(spot);
                    return L("Gate.Teleported", viewerId, cp.Name);
                }
                case "prop":
                {
                    if (player == null) return L("Gate.Usage", viewerId);
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) return L("Gate.NotFound", viewerId, name ?? "");
                    // Props live on gate templates in the config; a roadblock's template is
                    // throw-away and RespawnTemplate would delete the roadblock (review 2026-09-12).
                    if (cp.Kind != CheckpointKind.Gate) return L("Gate.NotFound", viewerId, name ?? "");
                    var kind = args.Length > first + 2 ? args[first + 2].ToLowerInvariant() : "concrete";
                    if (PropPrefab(kind) == null) return L("Gate.Usage", viewerId);
                    var minTier = 1;
                    if (args.Length > first + 3 && (!int.TryParse(args[first + 3], out minTier) || minTier < 1 || minTier > 5)) return L("Gate.Usage", viewerId);
                    var offset = Quaternion.Inverse(cp.Rotation) * (player.transform.position - cp.Position);
                    var yaw = Mathf.Repeat(YawOf(player.eyes.BodyForward()) - cp.Rotation.eulerAngles.y, 360f);
                    var p = AddProp(cp, kind, minTier, offset, yaw);
                    return L("Gate.PropAdded", viewerId, kind, cp.Name, p.Offset, p.Yaw) + (minTier > 1 ? $" MinTier={minTier}." : "");
                }
                case "tier":
                {
                    var t = name != null ? FindTemplate(name) : null;
                    if (t == null) return L("Gate.NotFound", viewerId, name ?? "");
                    int tier;
                    if (args.Length <= first + 2 || !int.TryParse(args[first + 2], out tier) || tier < 1 || tier > 5) return L("Gate.Usage", viewerId);
                    t.MinTier = tier;
                    SaveConfig();
                    ReconcileCheckpoints();
                    var state = "";
                    foreach (var cp in _checkpoints.Values) if (cp.Template == t) { state = cp.State == CheckpointState.Active ? $"active with {cp.LiveGuards()} guard(s)" : $"{cp.State} {cp.StateNote}"; break; }
                    return L("Gate.Tier", viewerId, t.Name, tier, state);
                }
                case "radius":
                {
                    var t = name != null ? FindTemplate(name) : null;
                    if (t == null) return L("Gate.NotFound", viewerId, name ?? "");
                    float r;
                    if (args.Length <= first + 2 || !float.TryParse(args[first + 2], out r) || r < 2f || r > 60f) return L("Gate.Usage", viewerId);
                    t.TriggerRadius = r;
                    SaveConfig();
                    RespawnTemplate(t);
                    return L("Gate.Radius", viewerId, t.Name, r);
                }
                case "reload":
                {
                    LoadConfig();
                    var n = RespawnAllCheckpoints();
                    return L("Papers.Reloaded", viewerId, n);
                }
                default:
                    return L("Gate.Usage", viewerId);
            }
        }

        #endregion

        #region Commands

        private const string UiMain = "PapersPlease.Papers";

        private bool IsAdmin(BasePlayer player) =>
            player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));

        private bool FindTarget(string arg, out ulong id, out string name)
        {
            id = 0; name = arg;
            if (string.IsNullOrEmpty(arg)) return false;
            ulong parsed;
            if (ulong.TryParse(arg, out parsed) && parsed.IsSteamId())
            {
                id = parsed;
                var bp = BasePlayer.FindByID(parsed) ?? BasePlayer.FindSleeping(parsed);
                name = bp != null ? bp.displayName : (covalence.Players.FindPlayerById(arg)?.Name ?? arg);
                return true;
            }
            var online = BasePlayer.Find(arg);
            if (online != null && IsRealPlayerId((ulong)online.userID)) { id = (ulong)online.userID; name = online.displayName; return true; }
            var known = covalence.Players.FindPlayer(arg);
            if (known != null && ulong.TryParse(known.Id, out parsed) && parsed.IsSteamId()) { id = parsed; name = known.Name; return true; }
            return false;
        }

        private string Describe(ulong id, string name, string viewerId, bool self)
        {
            if (self && IsExemptId(id)) return L("Papers.Exempt", viewerId);
            var score = ReadScore(id);
            var band = BandOf(score);
            return self
                ? L("Papers.Self", viewerId, name, score, BandName(band, viewerId), BandDesc(band, viewerId), _config.Reputation.DecayPerHour) + IdHeldLine(id, viewerId)
                : L("Papers.Other", viewerId, name, score, BandName(band, viewerId)) + IdHeldLine(id, viewerId);
        }

        private string TierLine(string viewerId)
        {
            var tier = CurrentTier();
            return "\n" + L("Papers.Tier", viewerId, tier, L("Tier." + tier, viewerId));
        }

        // Task 3.10: /papers shows any running encounter cooldowns.
        private string CooldownLines(ulong id, string viewerId)
        {
            var sb = new StringBuilder();
            var now = Time.realtimeSinceStartup;
            foreach (var cp in _checkpoints.Values)
            {
                float until;
                if (cp.CooldownUntil.TryGetValue(id, out until) && until > now)
                    sb.Append('\n').Append(L("Papers.Cooldown", viewerId, cp.Name, Mathf.CeilToInt(until - now)));
            }
            return sb.ToString();
        }

        [ChatCommand("papers")]
        private void CmdPapers(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var uid = player.UserIDString;
            if (args == null || args.Length == 0)
            {
                player.ChatMessage(Describe((ulong)player.userID, player.displayName, uid, true) + TierLine(uid) + (_curfewOn ? "\n" + L("Papers.Curfew", uid) : "") + HostileLine(player) + CooldownLines((ulong)player.userID, uid));
                return;
            }
            var sub = args[0].ToLowerInvariant();
            if (sub == "reload")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                LoadConfig();
                var n = RespawnAllCheckpoints();
                player.ChatMessage(L("Papers.Reloaded", uid, n));
                return;
            }
            if (sub == "gate")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                player.ChatMessage(RunGate(args, 1, player, uid));
                return;
            }
            // Milestone 7: papers for everyone, the fence for admins (decision 0008).
            if (sub == "request") { player.ChatMessage(RequestId(player)); return; }
            if (sub == "forge") { player.ChatMessage(ForgeId(player)); return; }
            if (sub == "fence")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                player.ChatMessage(RunFence(args, 1, player, uid));
                return;
            }
            if (sub == "vault")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                player.ChatMessage(RunVault(args, 1, player, uid));
                return;
            }
            if (sub == "sabotage")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                player.ChatMessage(RunSabotage(args, 1, player, uid));
                return;
            }
            if (sub == "set" || sub == "add" || sub == "reset" || sub == "top")
            {
                if (!IsAdmin(player)) { player.ChatMessage(L("Papers.NoPermission", uid)); return; }
                player.ChatMessage(RunAdmin(sub, args, 1, uid));
                return;
            }
            if (!IsAdmin(player)) { player.ChatMessage(L("Papers.Usage", uid)); return; }
            ulong id; string name;
            if (!FindTarget(args[0], out id, out name)) { player.ChatMessage(L("Papers.NoPlayer", uid, args[0])); return; }
            player.ChatMessage(Describe(id, name, uid, false));
        }

        // Shared by /papers (admin forms) and papers.rep: args[first] is the player for set/add/reset.
        private string RunAdmin(string sub, string[] args, int first, string viewerId)
        {
            if (sub == "top")
            {
                int want;
                var n = args.Length > first && int.TryParse(args[first], out want) ? Mathf.Clamp(want, 1, 50) : 10;
                var rows = _store.Snapshot();
                rows.Sort((a, b) => a.Value.CompareTo(b.Value)); // worst first — that is who Cobalt cares about
                var sb = new StringBuilder(L("Papers.Top", viewerId, rows.Count));
                for (var i = 0; i < rows.Count && i < n; i++)
                {
                    var p = covalence.Players.FindPlayerById(rows[i].Key.ToString());
                    sb.Append('\n').Append(L("Papers.TopLine", viewerId, p?.Name ?? rows[i].Key.ToString(), rows[i].Value, BandName(BandOf(rows[i].Value), viewerId)));
                }
                return sb.ToString();
            }
            if (args.Length <= first) return L("Papers.Usage", viewerId);
            ulong id; string name;
            if (!FindTarget(args[first], out id, out name)) return L("Papers.NoPlayer", viewerId, args[first]);
            if (sub == "reset")
            {
                Adjust(id, 0, "Reason.Admin", absolute: true);
                return L("Papers.Reset", viewerId, name);
            }
            int n2;
            if (args.Length <= first + 1 || !int.TryParse(args[first + 1], out n2)) return L("Papers.Usage", viewerId);
            var reason = args.Length > first + 2 ? string.Join(" ", args, first + 2, args.Length - first - 2) : L("Reason.Admin", viewerId);
            if (sub == "set")
            {
                var v = ApplyChange(id, n2, reason, true);
                return L("Papers.Set", viewerId, name, v, reason);
            }
            var v2 = ApplyChange(id, n2, reason, false);
            return L("Papers.Added", viewerId, name, n2, v2, reason);
        }

        // arg.Args is StringView[] on the 2026-09 build — materialise plain strings.
        private static string[] ConsoleArgs(ConsoleSystem.Arg arg)
        {
            var count = arg.Args != null ? arg.Args.Length : 0;
            var args = new string[count];
            for (var i = 0; i < count; i++) args[i] = arg.GetString(i, "");
            return args;
        }

        [ConsoleCommand("papers.rep")]
        private void CmdRep(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            var viewer = caller?.UserIDString;
            var args = ConsoleArgs(arg);
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "version":
                    arg.ReplyWith($"Cobalt Papers Please v{Version} | records={_store?.Count ?? 0} tier={CurrentTier()} threat={_threat?.Value ?? 0:0.0} hoursSinceWipe={HoursSinceWipe():0.0} armed={_armed} checkpoints={_checkpoints.Count} guards={LiveGuardCount()}/{_config.Checkpoints.GuardCap}");
                    return;
                case "hooks":
                    arg.ReplyWith($"OnEntityTakeDamage armed={_armed}; detector enabled={_config.Reputation.PartialRaidDetector}; window={_config.Reputation.PartialRaidWindowSeconds}s");
                    return;
                case "get":
                {
                    ulong id; string name;
                    if (args.Length < 2 || !FindTarget(args[1], out id, out name)) { arg.ReplyWith(L("Papers.NoPlayer", viewer, args.Length > 1 ? args[1] : "")); return; }
                    arg.ReplyWith(Describe(id, name, viewer, false) + (IsExemptId(id) ? " [exempt]" : ""));
                    return;
                }
                case "simulate":
                {
                    ulong id; string name; double hours;
                    if (args.Length < 3 || !FindTarget(args[1], out id, out name) || !double.TryParse(args[2], out hours)) { arg.ReplyWith("papers.rep simulate <player> <hours>  — back-dates the record to test decay"); return; }
                    var ok = _store.BackDate(id, hours);
                    arg.ReplyWith(ok ? $"Back-dated {name} by {hours} h → now reads {ReadScore(id)}." : $"{name} has no record (score 0).");
                    return;
                }
                case "set": case "add": case "reset": case "top":
                    arg.ReplyWith(RunAdmin(sub, args, 1, viewer));
                    return;
                case "save":
                    _store.Save(true);
                    arg.ReplyWith("Saved.");
                    return;
                default:
                    arg.ReplyWith("papers.rep version | hooks | get <player> | set|add <player> <n> [reason] | reset <player> | top [n] | simulate <player> <hours> | save");
                    return;
            }
        }

        // Where the caller is looking (solid hit within 40 m), else 5 m ahead on the terrain.
        private static Vector3 AimPoint(BasePlayer caller)
        {
            RaycastHit hit;
            if (UnityEngine.Physics.Raycast(caller.eyes.HeadRay(), out hit, 40f, global::Rust.Layers.Solid))
                return hit.point;
            var pos = caller.transform.position + caller.eyes.BodyForward() * 5f;
            pos.y = TerrainMeta.HeightMap.GetHeight(pos);
            return pos;
        }

        #region Ambush (Milestone 9, tasks 9.6–9.7 — decision 0010 §C: a sighting sends a squad)

        private class Squad
        {
            public ulong Target;
            public string TargetName;
            public readonly List<GuardNpc> Guards = new List<GuardNpc>();
            public Timer Spawn;      // the dispatch delay, then the safe-zone waits
            public Timer Timeout;    // HuntMinutes after the squad is out
            public Timer Poll;       // every 2 s: wiped out?
            public float DispatchedAt, OutAt, DeadlineAt;
            public float ArrestAt;   // the target went down with a hunter close: the arrest lands at this time
            public Vector3 Origin;
            public bool Out => OutAt > 0f;
        }

        private readonly List<Squad> _squads = new List<Squad>();
        private readonly Dictionary<ulong, float> _ambushCooldownUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Vector3> _lastSighting = new Dictionary<ulong, Vector3>();

        private bool AmbushArmed => _config.Ambush.Enabled && CurrentTier() >= _config.Ambush.AmbushFromTier;

        // Owner 2026-09-27: the squad grows with the tier — 2 regulars at 3, 3 with two heavies at 4, 4 heavies at 5.
        private int SquadFor(int tier)
        {
            var a = _config.Ambush.SquadByTier;
            if (a == null || a.Length == 0) return 3;
            return Mathf.Clamp(a[Mathf.Clamp(tier, 1, a.Length) - 1], 0, 8);
        }

        private int SquadHeaviesFor(int tier)
        {
            var a = _config.Ambush.SquadHeaviesByTier;
            if (a == null || a.Length == 0) return 0;
            return Mathf.Clamp(a[Mathf.Clamp(tier, 1, a.Length) - 1], 0, 8);
        }

        private bool AmbushBand(Band band)
        {
            var bands = _config.Ambush.Bands;
            if (bands == null) return false;
            foreach (var b in bands) { Band parsed; if (Enum.TryParse(b, true, out parsed) && parsed == band) return true; }
            return false;
        }

        private Squad SquadOn(ulong id)
        {
            foreach (var s in _squads) if (s.Target == id) return s;
            return null;
        }

        // Every time Cobalt turns on a player (FlagHostile) whose band is a sighting band.
        private void RecordSighting(BasePlayer bp)
        {
            if (bp == null || !IsRealPlayer(bp)) return;
            var id = (ulong)bp.userID;
            if (IsExemptId(id)) return;
            var band = BandOfPlayer(id);
            if (!AmbushBand(band)) return;
            _lastSighting[id] = bp.transform.position;
            if (!AmbushArmed) return;
            var now = Time.realtimeSinceStartup;
            float until;
            if (_ambushCooldownUntil.TryGetValue(id, out until) && until > now) { CLog($"[ambush] sighting of {bp.displayName} ({band}) at {V(bp.transform.position)}; no squad, cooldown {(until - now) / 60f:0.0} min left."); return; }
            if (SquadOn(id) != null) return;
            if (_squads.Count >= Mathf.Max(1, _config.Ambush.MaxSquads)) { CLog($"[ambush] sighting of {bp.displayName} ({band}); no squad, {_squads.Count} already out (MaxSquads)."); return; }
            Dispatch(bp, band, "sighting");
        }

        private void Dispatch(BasePlayer bp, Band band, string why)
        {
            var cfg = _config.Ambush;
            var now = Time.realtimeSinceStartup;
            var id = (ulong)bp.userID;
            var delay = Mathf.Clamp(cfg.DispatchDelaySeconds, 0, 600);
            var squad = new Squad { Target = id, TargetName = bp.displayName, DispatchedAt = now, DeadlineAt = now + delay + Mathf.Max(1, cfg.HuntMinutes) * 60f };
            _squads.Add(squad);
            if (cfg.RadioLine) bp.ChatMessage(Pick(_config.Voice.AmbushRadio, L("Ambush.Radio", bp.UserIDString)));
            CLog($"[ambush] {why}: {bp.displayName} ({band}) at {V(bp.transform.position)}; a squad of {SquadFor(CurrentTier())} ({SquadHeaviesFor(CurrentTier())} heavy) in {delay} s, {cfg.SpawnDistance:0} m out of sight, {cfg.HuntMinutes} min; next possible for them in {cfg.CooldownMinutes} min.");
            squad.Spawn = timer.Once(delay, () => { squad.Spawn = null; SpawnSquad(squad); });
        }

        private void SpawnSquad(Squad squad)
        {
            var cfg = _config.Ambush;
            var now = Time.realtimeSinceStartup;
            var target = BasePlayer.FindByID(squad.Target);
            if (target == null || !target.IsConnected || target.IsDead()) { StandDown(squad, "target gone before the squad was out"); return; }
            if (now >= squad.DeadlineAt) { StandDown(squad, "target stayed out of reach"); return; }
            if (target.InSafeZone())
            {
                // Not into a safe zone: wait at the radio, try again in 30 s until the deadline.
                CLog($"[ambush] {target.displayName} is inside a safe zone; the squad waits 30 s.");
                squad.Spawn = timer.Once(30f, () => { squad.Spawn = null; SpawnSquad(squad); });
                return;
            }
            Vector3 spot; string report;
            if (!AmbushSpot(target, Mathf.Max(20f, cfg.SpawnDistance), out spot, out report))
            {
                var back = -target.eyes.HeadForward(); back.y = 0f; if (back.sqrMagnitude < 0.01f) back = Vector3.forward;
                spot = SnapToGround(target.transform.position + back.normalized * Mathf.Max(20f, cfg.SpawnDistance));
            }
            var toward = target.transform.position - spot; toward.y = 0f;
            if (toward.sqrMagnitude < 0.01f) toward = Vector3.forward;
            toward.Normalize();
            var right = Vector3.Cross(Vector3.up, toward);
            var tier = CurrentTier();
            var n = Mathf.Clamp(SquadFor(tier), 1, 8);
            var heavies = Mathf.Clamp(SquadHeaviesFor(tier), 0, n);
            for (var i = 0; i < n; i++)
            {
                var side = (i % 2 == 0) ? 1f : -1f;
                var gp = SnapToGround(spot + right * (1.5f * ((i + 1) / 2)) * side);
                var heavy = i >= n - heavies; // the heavies are the last ones out
                var g = SpawnGuard(gp, toward, heavy, null, false, heavy ? "Cobalt Heavy" : "Cobalt Hunter");
                if (g == null) continue;
                squad.Guards.Add(g);
                var hb = g.GetComponent<GuardBrain>();
                if (hb != null)
                {
                    hb.HuntLateral = right * (3f * ((i + 1) / 2)) * side; // 0, −3, +3, −6 … metres to the side of the target
                    hb.StartHunt(target, cfg.BaseStandoff, cfg.EnterBases);
                }
            }
            squad.Origin = spot; squad.OutAt = now;
            _ambushCooldownUntil[squad.Target] = now + Mathf.Max(1, cfg.CooldownMinutes) * 60f; // the cooldown starts when a squad is actually out (review 2026-09-27)
            CLog($"[ambush] squad of {squad.Guards.Count} out on {target.displayName} at {V(spot)}, {Vector3.Distance(spot, target.transform.position):0} m ({report}).");
            squad.Timeout?.Destroy();
            squad.Timeout = timer.Once(Mathf.Max(1, cfg.HuntMinutes) * 60f, () => { squad.Timeout = null; StandDown(squad, "timeout"); });
            squad.Poll?.Destroy();
            squad.Poll = timer.Every(2f, () => PollSquad(squad));
        }

        private void PollSquad(Squad squad)
        {
            if (!_squads.Contains(squad)) { squad.Poll?.Destroy(); squad.Poll = null; return; }
            var alive = 0;
            foreach (var g in squad.Guards) if (g != null && !g.IsDestroyed && !g.IsDead()) alive++;
            if (alive == 0) { StandDown(squad, "wiped out"); return; }
            var target = BasePlayer.FindByID(squad.Target);
            if (target == null || !target.IsConnected || target.IsDead()) { StandDown(squad, target == null || !target.IsConnected ? "target left" : "target dead"); return; }
            // The arrest (decision 0010 §D): the target is down and a hunter is at arm's reach for ArrestSeconds.
            var cfg = _config.Ambush;
            if (!cfg.Arrest) return;
            var now = Time.realtimeSinceStartup;
            if (!target.IsWounded() || target.IsRestrained) { squad.ArrestAt = 0f; return; }
            var near = false;
            // Arm's reach with a line of sight: a hunter stalled against a wall 4 m from a target who
            // crawled indoors does not make an arrest through it (review 2026-09-27).
            foreach (var g in squad.Guards) if (g != null && !g.IsDestroyed && !g.IsDead() && Vector3.Distance(g.transform.position, target.transform.position) <= 6f && g.IsVisible(target.CenterPoint())) { near = true; break; }
            if (!near) { squad.ArrestAt = 0f; return; }
            if (squad.ArrestAt <= 0f) { squad.ArrestAt = now + Mathf.Max(1, cfg.ArrestSeconds); CLog($"[ambush] {target.displayName} is down with a hunter at arm's reach; arrest in {cfg.ArrestSeconds} s."); return; }
            if (now >= squad.ArrestAt) Arrest(squad, target);
        }

        // Cuffed, hooded, stripped, and left on the beach — the vanilla restraint recipe
        // (Handcuffs.SV_HandcuffVictim): the cuffs item in belt slot 0, the IsRestrained flag,
        // Handcuffs.SetLocked (locks the inventory); the hood is the item Handcuffs itself names.
        private void Arrest(Squad squad, BasePlayer target)
        {
            var cfg = _config.Ambush;
            var name = target.displayName;
            var stripped = 0;
            if (cfg.ArrestStrips && target.inventory != null)
            {
                foreach (var c in new[] { target.inventory.containerMain, target.inventory.containerBelt, target.inventory.containerWear })
                {
                    if (c == null) continue;
                    foreach (var it in new List<Item>(c.itemList)) { it.RemoveFromContainer(); it.Remove(); stripped++; }
                }
            }
            target.StopWounded();
            target.health = Mathf.Clamp(cfg.ArrestHealth, 5f, 100f);
            var spawn = ServerMgr.FindSpawnPoint(target);
            var beach = spawn != null ? spawn.pos : target.transform.position;
            target.Teleport(beach);
            var hood = ItemManager.CreateByItemID(Handcuffs.PrisonerHoodItemID, 1);
            var hooded = hood != null && target.inventory?.containerWear != null && hood.MoveToContainer(target.inventory.containerWear);
            if (hood != null && !hooded) hood.Remove();
            var cuffed = false;
            var cuffs = ItemManager.CreateByName("handcuffs", 1);
            if (cuffs != null && target.inventory?.containerBelt != null)
            {
                var slot0 = target.inventory.containerBelt.GetSlot(0);
                if (slot0 != null && !slot0.MoveToContainer(target.inventory.containerMain)) slot0.Remove();
                if (cuffs.MoveToContainer(target.inventory.containerBelt))
                {
                    target.SetPlayerFlag(BasePlayer.PlayerFlags.IsRestrained, true);
                    target.SendNetworkUpdateImmediate();
                    cuffs.SetFlag(global::Item.Flag.IsOn, true);
                    cuffs.MarkDirty();
                    var held = cuffs.GetHeldEntity() as Handcuffs;
                    if (held != null) { held.SetLocked(true, target, cuffs); cuffed = true; }
                    else { target.SetPlayerFlag(BasePlayer.PlayerFlags.IsRestrained, false); CLog("[ambush] the handcuffs item has no Handcuffs entity; left uncuffed."); }
                }
                else cuffs.Remove();
            }
            target.ChatMessage(L("Ambush.Arrested", target.UserIDString));
            if (cfg.BroadcastArrest) Broadcast("Broadcast.Arrested", name);
            CLog($"[ambush] {name} ARRESTED by the squad: {stripped} item(s) taken, {(cuffed ? "cuffed" : "not cuffed")}, {(hooded ? "hooded" : "no hood")}, left at {V(beach)} with {target.health:0} hp.");
            StandDown(squad, "arrest");
        }

        private void StandDown(Squad squad, string why)
        {
            squad.Spawn?.Destroy(); squad.Spawn = null;
            squad.Timeout?.Destroy(); squad.Timeout = null;
            squad.Poll?.Destroy(); squad.Poll = null;
            var alive = 0;
            foreach (var g in squad.Guards)
            {
                if (g == null || g.IsDestroyed) continue;
                if (!g.IsDead()) alive++;
                _looseGuards.Remove(g);
                DespawnGuard(g); // the brain dies with the guard; no "returning to post" line first (review 2026-09-27)
            }
            squad.Guards.Clear();
            _squads.Remove(squad);
            var target = BasePlayer.FindByID(squad.Target);
            if (squad.Out && why != "wiped out" && why != "arrest" && why != "unload" && why != "reload" && target != null && target.IsConnected)
                target.ChatMessage(Pick(_config.Voice.AmbushStoodDown, L("Ambush.StoodDown", target.UserIDString)));
            CLog($"[ambush] squad on {squad.TargetName} stood down ({why}){(squad.Out ? $" after {(Time.realtimeSinceStartup - squad.OutAt) / 60f:0.0} min, {alive} still standing" : " before it was out")}.");
        }

        private void DespawnAllSquads(string why)
        {
            foreach (var s in new List<Squad>(_squads)) StandDown(s, why);
        }

        private string DescribeAmbush()
        {
            var cfg = _config.Ambush;
            var now = Time.realtimeSinceStartup;
            var sb = new StringBuilder($"ambush: {(cfg.Enabled ? "enabled" : "disabled")} from tier {cfg.AmbushFromTier} (now {CurrentTier()}, {(AmbushArmed ? "armed" : "not armed")}); bands [{string.Join(", ", cfg.Bands ?? new List<string>())}]; squad {SquadFor(CurrentTier())} ({SquadHeaviesFor(CurrentTier())} heavy) at this tier after {cfg.DispatchDelaySeconds} s, {cfg.SpawnDistance:0} m out, {cfg.HuntMinutes} min, standoff {cfg.BaseStandoff:0} m{(cfg.EnterBases ? " (enters bases)" : "")}, cooldown {cfg.CooldownMinutes} min, max {cfg.MaxSquads}");
            sb.Append('\n').Append($"  squads out: {_squads.Count}");
            foreach (var s in _squads)
            {
                var alive = 0; foreach (var g in s.Guards) if (g != null && !g.IsDestroyed && !g.IsDead()) alive++;
                sb.Append('\n').Append($"  on {s.TargetName}: {(s.Out ? $"{alive} alive, out {(now - s.OutAt) / 60f:0.0} min from {V(s.Origin)}" : $"dispatched, spawning in {Mathf.Max(0f, s.DispatchedAt + cfg.DispatchDelaySeconds - now):0} s")}");
            }
            var cool = 0; foreach (var kv in _ambushCooldownUntil) if (kv.Value > now) cool++;
            sb.Append('\n').Append($"  players on cooldown: {cool}; sightings remembered: {_lastSighting.Count}");
            return sb.ToString();
        }

        [ConsoleCommand("papers.ambush")]
        private void CmdAmbush(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            var viewer = caller?.UserIDString;
            var args = ConsoleArgs(arg);
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    arg.ReplyWith(DescribeAmbush());
                    return;
                case "dispatch":
                {
                    // Test: a squad on a named player (or the caller), whatever their band, tier or cooldown.
                    var who = args.Length > 1 ? FindAnyPlayer(args[1]) : caller;
                    if (who == null) { arg.ReplyWith(L("Ambush.Usage", viewer)); return; }
                    if (SquadOn((ulong)who.userID) != null) { arg.ReplyWith($"A squad is already on {who.displayName}."); return; }
                    Dispatch(who, BandOfPlayer((ulong)who.userID), "admin dispatch");
                    arg.ReplyWith(DescribeAmbush());
                    return;
                }
                case "stand":
                    DespawnAllSquads("admin");
                    arg.ReplyWith(DescribeAmbush());
                    return;
                default:
                    arg.ReplyWith(L("Ambush.Usage", viewer));
                    return;
            }
        }

        #endregion

        #region Patrols (Milestone 9, tasks 9.2–9.5 — decision 0010 §A: two guards walking a road, the stop, the shift)

        private Timer _patrolTimer;
        private static string PatrolSlot(int i) => $"patrol-{i}";

        private int PatrolsWanted(int tier)
        {
            var cfg = _config.Patrols;
            if (!cfg.Enabled) return 0;
            var byTier = cfg.PatrolsByTier;
            if (byTier == null || byTier.Length == 0) return 0;
            var want = Mathf.Max(0, byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1]);
            var room = Mathf.Max(0, cfg.PatrolGuardCap) / 2;
            return Mathf.Clamp(Mathf.Min(want, room), 0, 8);
        }

        private List<Checkpoint> PatrolSlots()
        {
            var list = new List<Checkpoint>();
            foreach (var cp in _checkpoints.Values) if (cp.Kind == CheckpointKind.Patrol) list.Add(cp);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        private void TrimPatrols(int tier)
        {
            var wanted = PatrolsWanted(tier);
            var slots = PatrolSlots();
            for (var i = slots.Count - 1; i >= 0 && slots.Count > wanted; i--)
            {
                var cp = slots[i];
                Despawn(cp, "patrol trimmed");
                _checkpoints.Remove(cp.Name);
                slots.RemoveAt(i);
                CLog($"[patrol] '{cp.Name}' removed: {wanted} patrol(s) wanted at tier {tier}.");
            }
        }

        private void FillPatrols(int tier)
        {
            var wanted = PatrolsWanted(tier);
            var slots = PatrolSlots();
            var index = 1;
            while (slots.Count < wanted && index <= 16)
            {
                var name = PatrolSlot(index++);
                if (_checkpoints.ContainsKey(name)) continue;
                var cp = NewPatrol(name);
                if (cp == null) break; // no road this round; the next reconcile retries
                slots.Add(cp);
            }
        }

        private Checkpoint NewPatrol(string name)
        {
            var cp = new Checkpoint
            {
                Name = name,
                Kind = CheckpointKind.Patrol,
                Template = new GateTemplate { Name = name, Monument = "road", Guards = 2, Searchlight = false, Perimeter = false, TriggerRadius = 8f },
            };
            DateTime until;
            if (_cpData.DestroyedUntil.TryGetValue(name, out until) && until > DateTime.UtcNow)
            {
                _checkpoints[name] = cp;
                cp.DestroyedUntilUtc = until;
                cp.State = CheckpointState.Destroyed;
                cp.StateNote = $"lost until {until:HH:mm} UTC";
                ScheduleRespawn(cp);
                return cp;
            }
            if (!MoveRoute(cp, "new")) return null;
            cp.ShiftStartedAt = Time.realtimeSinceStartup;
            _checkpoints[name] = cp;
            TrySpawn(cp);
            return cp;
        }

        // A route: the roadblock sampler picks a straight, level, clear centre; the route is that
        // road's points RouteLength/2 either way, clipped at the road's ends. The lead walks it
        // end to end and back; the plugin does not re-check the sampler's rules per point — a
        // stall (WalkStallSeconds) skips a point and three skips re-draw the route.
        private bool MoveRoute(Checkpoint cp, string why)
        {
            string report;
            var sample = SampleRoad(out report);
            if (sample == null) { CLog($"[patrol] no route for '{cp.Name}' ({why}): {report}"); return false; }
            var roads = TerrainMeta.Path?.Roads;
            if (roads == null || sample.RoadIndex < 0 || sample.RoadIndex >= roads.Count) return false;
            var pts = roads[sample.RoadIndex]?.Path?.Points;
            if (pts == null || pts.Length < 2) return false;
            var centre = Mathf.Clamp(sample.PointIndex, 0, pts.Length - 1);
            var half = Mathf.Max(20f, _config.Patrols.RouteLength / 2f);
            var route = new List<Vector3> { pts[centre] };
            var len = 0f; var i = centre;
            while (len < half && i - 1 >= 0) { len += Vector3.Distance(pts[i], pts[i - 1]); i--; route.Insert(0, pts[i]); }
            len = 0f; i = centre;
            while (len < half && i + 1 < pts.Length) { len += Vector3.Distance(pts[i], pts[i + 1]); i++; route.Add(pts[i]); }
            if (route.Count < 3) { CLog($"[patrol] '{cp.Name}': road #{sample.RoadIndex} too short for a route ({route.Count} points)."); return false; }
            cp.Route = route; cp.RouteDir = 1;
            cp.Road = sample;
            cp.Position = sample.Position;
            cp.Rotation = Quaternion.LookRotation(sample.Forward);
            cp.PatrolLead = 0;
            CLog($"[patrol] '{cp.Name}' route ({why}): road #{sample.RoadIndex}, {route.Count} points, {RouteMetres(route):0} m, centre {V(sample.Position)}.");
            return true;
        }

        private static float RouteMetres(List<Vector3> route)
        {
            var m = 0f;
            for (var i = 1; i < route.Count; i++) m += Vector3.Distance(route[i - 1], route[i]);
            return m;
        }

        private GuardNpc PatrolLeadOf(Checkpoint cp)
        {
            foreach (var g in cp.Guards) if (g != null && !g.IsDestroyed && !g.IsDead()) return g;
            return null;
        }

        // The lead walks from the route point nearest him to the end in RouteDir; the other guard follows.
        private void StartPatrolWalk(Checkpoint cp, string why)
        {
            if (cp.Route == null || cp.Route.Count < 2) return;
            var lead = PatrolLeadOf(cp);
            if (lead == null) return;
            var brain = lead.GetComponent<GuardBrain>();
            if (brain == null) return;
            var pos = lead.transform.position;
            var nearest = 0; var best = float.MaxValue;
            for (var i = 0; i < cp.Route.Count; i++) { var d = (cp.Route[i] - pos).sqrMagnitude; if (d < best) { best = d; nearest = i; } }
            var list = new List<Vector3>();
            for (var i = nearest + cp.RouteDir; i >= 0 && i < cp.Route.Count; i += cp.RouteDir) list.Add(cp.Route[i]);
            if (list.Count == 0)
            {
                cp.RouteDir = -cp.RouteDir;
                for (var i = nearest + cp.RouteDir; i >= 0 && i < cp.Route.Count; i += cp.RouteDir) list.Add(cp.Route[i]);
                if (list.Count == 0) return;
            }
            cp.PatrolLead = lead.net?.ID.Value ?? 0;
            brain.StartWalk(list, _config.Patrols.WalkStallSeconds);
            foreach (var g in cp.Guards)
            {
                if (g == null || g.IsDestroyed || g == lead) continue;
                var fb = g.GetComponent<GuardBrain>();
                if (fb != null) { fb.StopWalk("following"); fb.StartFollow(lead, 4f); }
            }
            CLog($"[patrol] '{cp.Name}' walking ({why}): {list.Count} point(s) {(cp.RouteDir > 0 ? "up" : "down")} the route from point {nearest}.");
            // A hand-over during a stop (the lead died) must not walk the sphere away from the halted
            // player — that would read as them running the checkpoint (review 2026-09-27).
            foreach (var e in _encounters.Values) if (e.Gate == cp) { brain.PauseWalk(); break; }
        }

        // 4 Hz while any patrol stands: the spheres and the checkpoint pose follow the lead; a
        // finished route turns round; a stalled route (three skips) is re-drawn; a dead lead hands
        // over to the other guard.
        private void PatrolTick()
        {
            foreach (var cp in new List<Checkpoint>(_checkpoints.Values)) // a snapshot: relocation and hand-over run inside (review 2026-09-27)
            {
                if (cp.Kind != CheckpointKind.Patrol || cp.State != CheckpointState.Active) continue;
                var lead = PatrolLeadOf(cp);
                if (lead == null) continue;
                var pos = lead.transform.position;
                cp.Position = pos;
                var fwd = lead.transform.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f) cp.Rotation = Quaternion.LookRotation(fwd.normalized);
                if (cp.TriggerGo != null) cp.TriggerGo.transform.position = pos + Vector3.up;
                if (cp.ApproachGo != null) cp.ApproachGo.transform.position = pos + Vector3.up;
                var id = lead.net?.ID.Value ?? 0;
                if (id != cp.PatrolLead) { StartPatrolWalk(cp, "new lead"); continue; }
                var brain = lead.GetComponent<GuardBrain>();
                if (brain == null || brain.Walking) continue;
                var s = brain.StateName;
                if (s == "chase" || s == "combat" || s == "hold" || s == "return") continue; // busy or still coming back from a fight
                if (brain.WalkNote == "three skips") { RelocatePatrol(cp, "stalled"); continue; }
                cp.RouteDir = -cp.RouteDir;
                StartPatrolWalk(cp, "turn");
            }
        }

        // The stop (task 9.4): the lead stands and faces the subject for the encounter; the
        // follower already stands when close to the lead. Resumed by Finish/Abort.
        private void PausePatrol(Checkpoint cp)
        {
            var lead = PatrolLeadOf(cp);
            var brain = lead?.GetComponent<GuardBrain>();
            if (brain != null) brain.PauseWalk();
        }

        private void ResumePatrol(Checkpoint cp)
        {
            foreach (var e in _encounters.Values) if (e.Gate == cp) return; // another subject is still at the stop
            var lead = PatrolLeadOf(cp);
            var brain = lead?.GetComponent<GuardBrain>();
            if (brain != null) brain.ResumeWalk();
        }

        private bool PatrolBusy(Checkpoint cp, out string why)
        {
            why = null;
            foreach (var e in _encounters.Values) if (e.Gate == cp) { why = "encounter in progress"; return true; }
            var r = Mathf.Max(10f, _config.Patrols.ClearRadius);
            var r2 = r * r;
            foreach (var p in BasePlayer.activePlayerList)
                if (p != null && IsRealPlayer(p) && !p.IsDead() && (p.transform.position - cp.Position).sqrMagnitude <= r2) { why = $"{p.displayName} within {r:0} m"; return true; }
            return false;
        }

        private void RelocateDuePatrols()
        {
            var due = Mathf.Max(5, _config.Patrols.RelocateMinutes) * 60f;
            var now = Time.realtimeSinceStartup;
            var shift = Mathf.Max(5, _config.Patrols.PatrolMinutes) * 60f;
            foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
            {
                if (cp.Kind != CheckpointKind.Patrol || cp.State != CheckpointState.Active) continue;
                // Two clocks (review 2026-09-27): the shift runs from the slot's birth or return
                // (ShiftStartedAt) and is not reset by a relocation; the route age (PlacedAt) is.
                var onShift = now - cp.ShiftStartedAt;
                var routeAge = now - cp.PlacedAt;
                var shiftOver = onShift >= shift;
                if (!shiftOver && routeAge < due) continue;
                string why;
                if (PatrolBusy(cp, out why)) { CLog($"[patrol] '{cp.Name}' {(shiftOver ? "stand-down" : "relocation")} deferred: {why}."); continue; }
                if (shiftOver) StandDownPatrol(cp); else RelocatePatrol(cp, "due");
            }
        }

        // The shift is over (PatrolMinutes): the patrol goes home and a fresh one walks a new road
        // after PatrolGapMinutes — the same booking as a lost patrol (DestroyedUntil, persisted),
        // without the broadcast (task 9.5).
        private void StandDownPatrol(Checkpoint cp)
        {
            var gap = Mathf.Max(1, _config.Patrols.PatrolGapMinutes);
            Despawn(cp, "stand-down");
            cp.State = CheckpointState.Destroyed;
            cp.DestroyedUntilUtc = DateTime.UtcNow.AddMinutes(gap);
            cp.StateNote = $"stood down until {cp.DestroyedUntilUtc:HH:mm} UTC";
            _cpData.DestroyedUntil[cp.Name] = cp.DestroyedUntilUtc; _cpDirty = true;
            CLog($"[patrol] '{cp.Name}' stood down after its shift; back on a new road in {gap} min.");
            ScheduleRespawn(cp);
        }

        // Draw first so a miss keeps the old route and the guards standing; a busy patrol (an
        // encounter, a player close) keeps walking — a stalled one just turns round (review 2026-09-27).
        private bool RelocatePatrol(Checkpoint cp, string why)
        {
            string busy;
            if (why != "admin" && PatrolBusy(cp, out busy))
            {
                CLog($"[patrol] '{cp.Name}' relocation ({why}) deferred: {busy}.");
                if (why == "stalled") { cp.RouteDir = -cp.RouteDir; StartPatrolWalk(cp, "turn back"); }
                return false;
            }
            var from = cp.Position;
            if (!MoveRoute(cp, why))
            {
                if (why == "stalled") { cp.RouteDir = -cp.RouteDir; StartPatrolWalk(cp, "turn back"); }
                return false;
            }
            Despawn(cp, "relocating");
            cp.CooldownUntil.Clear(); cp.WarnedUntil.Clear(); cp.RunOverUntil.Clear();
            cp.State = CheckpointState.Unspawned;
            var ok = TrySpawn(cp);
            CLog($"[patrol] '{cp.Name}' relocated ({why}) {V(from)} → {V(cp.Position)}{(ok ? "" : " — not spawned: " + cp.StateNote)}.");
            return ok;
        }

        private string DescribePatrols()
        {
            var tier = CurrentTier();
            var cfg = _config.Patrols;
            var byTier = cfg.PatrolsByTier ?? new int[0];
            var byTierNow = byTier.Length == 0 ? 0 : byTier[Mathf.Clamp(tier, 1, byTier.Length) - 1];
            var sb = new StringBuilder($"patrols: {PatrolSlots().Count} of {PatrolsWanted(tier)} wanted at tier {tier} (byTier {byTierNow}, cap {cfg.PatrolGuardCap} guards, live {PatrolGuardsLive()}); route {cfg.RouteLength:0} m, relocate every {cfg.RelocateMinutes} min when clear");
            foreach (var cp in PatrolSlots())
            {
                var lead = PatrolLeadOf(cp);
                var brain = lead?.GetComponent<GuardBrain>();
                var walk = brain == null ? "-" : brain.Walking ? $"walking point {brain.WalkIndex}/{brain.WalkCount} ({brain.WalkedMetres:0} m this leg, {(cp.RouteDir > 0 ? "up" : "down")})" : $"{brain.StateName} ({brain.WalkNote})";
                var age = (Time.realtimeSinceStartup - cp.PlacedAt) / 60f;
                sb.Append('\n').Append($"  {cp.Name}: {(cp.State == CheckpointState.Active ? $"active guards={cp.LiveGuards()} age={age:0} min" : $"{cp.State} {cp.StateNote}")}; road #{cp.Road?.RoadIndex ?? -1}, {(cp.Route == null ? 0 : cp.Route.Count)} points {(cp.Route == null ? 0 : RouteMetres(cp.Route)):0} m; lead at {V(cp.Position)}: {walk}");
            }
            return sb.ToString();
        }

        #endregion

        // Milestone 9 probe helpers (task 9.1). A route = consecutive road points from the nearest
        // road, toward its farther end, up to `metres` long.
        private bool RoadRouteFrom(Vector3 from, float metres, out List<Vector3> route, out int roadIndex, out float length)
        {
            route = null; roadIndex = -1; length = 0f;
            var roads = TerrainMeta.Path?.Roads;
            if (roads == null) return false;
            var best = 60f * 60f; var bestRoad = -1; var bestI = -1;
            for (var r = 0; r < roads.Count; r++)
            {
                var pts = roads[r]?.Path?.Points;
                if (pts == null) continue;
                for (var i = 0; i < pts.Length; i++) { var d2 = (pts[i] - from).sqrMagnitude; if (d2 < best) { best = d2; bestRoad = r; bestI = i; } }
            }
            if (bestRoad < 0) return false;
            var points = roads[bestRoad].Path.Points;
            var step = (points.Length - 1 - bestI) >= bestI ? 1 : -1;
            route = new List<Vector3> { points[bestI] };
            var at = bestI;
            while (length < metres)
            {
                var next = at + step;
                if (next < 0 || next >= points.Length) break;
                length += Vector3.Distance(points[at], points[next]);
                route.Add(points[next]); at = next;
            }
            roadIndex = bestRoad;
            return route.Count > 1;
        }

        // A player by name or SteamID, online first, then asleep (the driver works on the owner's sleeper).
        private static BasePlayer FindAnyPlayer(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;
            var bp = BasePlayer.Find(nameOrId);
            if (bp != null) return bp;
            ulong id;
            if (ulong.TryParse(nameOrId, out id)) { var s = BasePlayer.FindSleeping(id); if (s != null) return s; }
            foreach (var s in BasePlayer.sleepingPlayerList)
                if (s != null && !string.IsNullOrEmpty(s.displayName) && s.displayName.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0) return s;
            return null;
        }

        // "Can a guard stand here": the plain AllAreas sample InitializeAI relies on, within 6 m; the
        // surface name from the per-agent query when it answers, "default" when only the plain one does.
        private bool OnNavmesh(Vector3 p, out string surface)
        {
            // Probe 2026-09-20 15:59: the plain sample and the registered-settings loop both answered
            // nothing at points where guards walk; the navmesh is built for the agent type the
            // guards' own NavMeshAgent carries (from the prefab), so that filter is asked first.
            NavMeshHit hit;
            var guardAgent = GuardAgentTypeId();
            if (guardAgent != int.MinValue && NavMesh.SamplePosition(p, out hit, 6f, new NavMeshQueryFilter { agentTypeID = guardAgent, areaMask = NavMesh.AllAreas }))
            {
                surface = "guard-agent " + GuardAgentName(guardAgent);
                return true;
            }
            var plain = NavMesh.SamplePosition(p, out hit, 6f, NavMesh.AllAreas);
            var agent = AgentTypeAt(p, out surface);
            if (agent == int.MinValue) surface = plain ? "default" : "none";
            return plain || agent != int.MinValue;
        }

        // The agent type the guards actually navigate with: the first live guard's NavMeshAgent.
        private int GuardAgentTypeId()
        {
            foreach (var g in _looseGuards) if (g != null && !g.IsDestroyed && g.NavAgent != null) return g.NavAgent.agentTypeID;
            foreach (var cp in _checkpoints.Values) foreach (var g in cp.Guards) if (g != null && !g.IsDestroyed && g.NavAgent != null) return g.NavAgent.agentTypeID;
            return int.MinValue;
        }

        private static string GuardAgentName(int id)
        {
            var n = NavMesh.GetSettingsNameFromID(id);
            return string.IsNullOrEmpty(n) ? $"#{id}" : $"'{n}' (#{id})";
        }

        private GuardNpc FindWalker()
        {
            GuardNpc last = null;
            foreach (var g in _looseGuards)
            {
                if (g == null || g.IsDestroyed || g.GuardName != "Cobalt Walker") continue;
                last = g;
                var b = g.GetComponent<GuardBrain>();
                if (b != null && b.Walking) return g;
            }
            return last;
        }

        private GameObject _probeTriggerGo;
        private Timer _probeTriggerTimer;
        private void DestroyProbeTrigger()
        {
            _probeTriggerTimer?.Destroy(); _probeTriggerTimer = null;
            if (_probeTriggerGo != null) { UnityEngine.Object.Destroy(_probeTriggerGo); _probeTriggerGo = null; }
        }

        // Decision 0010 §C: a navmesh point `distance` m from the target that the target cannot see,
        // starting behind them and sweeping round in 45° steps; the report says what each try found.
        private bool AmbushSpot(BasePlayer target, float distance, out Vector3 spot, out string report)
        {
            var origin = target.transform.position;
            var back = -target.eyes.HeadForward(); back.y = 0f;
            if (back.sqrMagnitude < 0.01f) back = Vector3.forward; back.Normalize();
            var sb = new StringBuilder();
            var firstNav = Vector3.zero; var haveNav = false;
            // Three rings (the distance, three quarters, one and a half) × eight directions: the
            // probe found no navmesh at exactly 80 m around the owner's base (2026-09-20).
            foreach (var ring in new[] { 1f, 0.75f, 1.5f })
            for (var i = 0; i < 8; i++)
            {
                var dir = Quaternion.Euler(0f, i * 45f, 0f) * back;
                var candidate = SnapToGround(origin + dir * distance * ring);
                // No navmesh test: on this server the guards' agent is never on a navmesh and every
                // sample fails where they demonstrably walk (probe 2026-09-20 16:22); BaseNavigator
                // moves them in straight hops. What matters is ground, water, a cliff and sight.
                if (IsUnderwater(candidate)) { sb.Append($"try {i}: water; "); continue; }
                if (Mathf.Abs(candidate.y - origin.y) > 10f) { sb.Append($"try {i}: {Mathf.Abs(candidate.y - origin.y):0} m of height; "); continue; }
                if (!haveNav) { haveNav = true; firstNav = candidate; }
                if (target.IsVisible(candidate + Vector3.up * 1.6f)) { sb.Append($"try {i}: visible; "); continue; }
                spot = candidate;
                report = sb + $"try {i}: {V(candidate)}, {Vector3.Distance(candidate, origin):0} m, out of sight.";
                return true;
            }
            spot = firstNav;
            report = sb + (haveNav ? $"no hidden point; fallback {V(firstNav)} (visible)." : "no dry, level ground at those distances.");
            return haveNav;
        }

        [ConsoleCommand("papers.cp")]
        private void CmdCheckpoint(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !IsAdmin(caller)) { arg.ReplyWith(L("Papers.NoPermission", caller.UserIDString)); return; }
            var viewer = caller?.UserIDString;
            var args = ConsoleArgs(arg);
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            var name = args.Length > 1 ? args[1] : null;
            switch (sub)
            {
                case "list":
                    arg.ReplyWith(RunGate(new[] { "list" }, 0, caller, viewer));
                    return;
                case "gate":
                    arg.ReplyWith(RunGate(args, 1, caller, viewer));
                    return;
                case "respawn":
                {
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) { arg.ReplyWith(L("Gate.NotFound", viewer, name ?? "")); return; }
                    Despawn(cp, "respawn");
                    cp.State = CheckpointState.Unspawned;
                    _cpData.DestroyedUntil.Remove(cp.Name); _cpDirty = true;
                    // A fallen roadblock returns somewhere else (decision 0006 §4) — by admin as by timer.
                    if (cp.Kind == CheckpointKind.Roadblock && !cp.Pinned && !MoveRoadblock(cp) && cp.Road == null)
                    {
                        cp.State = CheckpointState.Destroyed; cp.StateNote = "no road spot and no previous pose";
                        arg.ReplyWith($"'{cp.Name}': no road spot found and no previous pose — left Destroyed (the reconcile retries)."); return;
                    }
                    var ok = TrySpawn(cp);
                    arg.ReplyWith(L("Gate.Respawned", viewer, cp.Name, ok ? $"{cp.LiveGuards()} guard(s)" : cp.StateNote) + (cp.Kind == CheckpointKind.Roadblock ? $" at {V(cp.Position)}" : ""));
                    return;
                }
                case "despawn":
                {
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) { arg.ReplyWith(L("Gate.NotFound", viewer, name ?? "")); return; }
                    Despawn(cp, "despawned by admin");
                    if (cp.Kind == CheckpointKind.Roadblock) { _checkpoints.Remove(cp.Name); if (_cpData.DestroyedUntil.Remove(cp.Name)) _cpDirty = true; }
                    arg.ReplyWith(L("Gate.Despawned", viewer, cp.Name));
                    return;
                }
                case "guard":
                {
                    // Task 3.1 debug: a loose guard at the aim point facing the caller.
                    if (caller == null) { arg.ReplyWith("Run in-game."); return; }
                    var heavy = string.Equals(name, "heavy", StringComparison.OrdinalIgnoreCase);
                    if (LiveGuardCount() + 1 > _config.Checkpoints.GuardCap) { arg.ReplyWith($"Guard cap {_config.Checkpoints.GuardCap} reached."); return; }
                    var pos = AimPoint(caller);
                    var facing = caller.transform.position - pos; facing.y = 0f;
                    facing = facing.sqrMagnitude > 0.01f ? facing.normalized : Vector3.forward;
                    var g = SpawnGuard(pos, facing, heavy, null);
                    if (g == null) { arg.ReplyWith("Spawn failed — see papers.cp log."); return; }
                    var brain = g.GetComponent<GuardBrain>();
                    var msg = $"Spawned '{g.GuardName}' hp={g.health:0}/{g.MaxHealth():0} at {V(pos)} surface='{brain?.Surface}' skeleton={(g.skeletonProperties != null)} belt={g.inventory?.containerBelt?.itemList.Count ?? -1} wear={g.inventory?.containerWear?.itemList.Count ?? -1}. Shoot it from outside any safe zone; the kill should cost you {_config.Reputation.GuardKill}.";
                    CLog("[guard] " + msg);
                    arg.ReplyWith(msg);
                    return;
                }
                case "guards":
                {
                    var sb = new StringBuilder();
                    var i = 0;
                    foreach (var cp in _checkpoints.Values)
                        foreach (var g in cp.Guards) sb.AppendLine(DescribeGuard(i++, g, cp.Name, caller));
                    _looseGuards.RemoveAll(x => x == null || x.IsDestroyed);
                    foreach (var g in _looseGuards) sb.AppendLine(DescribeGuard(i++, g, "(loose)", caller));
                    arg.ReplyWith(sb.Length == 0 ? "No guards live." : sb.ToString());
                    return;
                }
                case "clearloose":
                {
                    var n = _looseGuards.Count;
                    foreach (var g in new List<GuardNpc>(_looseGuards)) DespawnGuard(g);
                    _looseGuards.Clear();
                    var m = _looseProps.Count;
                    foreach (var e in _looseProps) if (e != null && !e.IsDestroyed) e.Kill();
                    _looseProps.Clear();
                    arg.ReplyWith($"Removed {n} loose guard(s) and {m} loose prop(s).");
                    return;
                }
                case "outpost":
                    arg.ReplyWith(DescribeOutpost());
                    return;
                case "curfew":
                {
                    // Test override: on / off / auto (follow the clock and the tier). Not persisted.
                    var mode = (name ?? "").ToLowerInvariant();
                    if (mode == "on") _curfewForce = 1;
                    else if (mode == "off") _curfewForce = 0;
                    else if (mode == "auto") _curfewForce = -1;
                    else { arg.ReplyWith("papers.cp curfew on|off|auto"); return; }
                    OutpostTick(true);
                    // The gates' inSafeZone flags are re-read 1 s after the resize (SetCurfew), so
                    // this reply still shows the old values (live 2026-09-12); say so.
                    arg.ReplyWith(DescribeOutpost() + "\n  (gate inSafeZone flags re-read in 1 s — see papers.cp outpost)");
                    return;
                }
                case "lights":
                {
                    // Task 8.5 diagnostic: every IOEntity inside the Outpost bounds that looks like a light, with its state.
                    if (_outpost == null) { arg.ReplyWith("No Outpost on this map."); return; }
                    var sb = new StringBuilder($"Outpost lights (monument's own, within its bounds): dark={IsDark}, night={IsNightNow()}");
                    var n = 0;
                    foreach (var e in BaseNetworkable.serverEntities)
                    {
                        var io = e as IOEntity;
                        if (io == null || io.IsDestroyed || !InMonument(_outpost, io.transform.position, 150f)) continue;
                        if (!LooksLikeALight(io) && !(io is SearchLight)) continue;
                        if (n++ >= 40) { sb.Append("\n  …"); break; }
                        sb.Append('\n').Append($"  {io.ShortPrefabName} ({io.GetType().Name}) @ {V(io.transform.position)} on={io.HasFlag(BaseEntity.Flags.On)} power={io.HasFlag(BaseEntity.Flags.Reserved8)} ours={(_darkenedLights.ContainsKey(io) ? "darkened" : "no")}");
                    }
                    sb.Append('\n').Append($"  {n} light-like entity(s). Plugin props: {DescribeOutpostLights()}");
                    arg.ReplyWith(sb.ToString());
                    return;
                }
                case "patrols":
                {
                    // Milestone 9: the patrol slots, their routes and where each lead is; `relocate <slot>` re-draws one.
                    var mode = (name ?? "list").ToLowerInvariant();
                    if (mode == "relocate" && args.Length > 2)
                    {
                        Checkpoint pcp;
                        if (!_checkpoints.TryGetValue(args[2], out pcp) || pcp.Kind != CheckpointKind.Patrol) { arg.ReplyWith($"No patrol named '{args[2]}'."); return; }
                        RelocatePatrol(pcp, "admin");
                    }
                    else if (mode != "list") { arg.ReplyWith("papers.cp patrols [list | relocate <slot>]"); return; }
                    arg.ReplyWith(DescribePatrols());
                    return;
                }
                case "spawners":
                {
                    // Probe 2026-09-20 (finding 4): the compound's own NPC spawn groups and the levers on them.
                    var mode = (name ?? "list").ToLowerInvariant();
                    if (mode == "off") { var n = PauseOutpostSpawners(true); CLog($"[probe] {n} Outpost spawn group(s) paused by admin."); }
                    else if (mode == "on") { RestoreSpawners("admin"); }
                    else if (mode == "delay")
                    {
                        float s;
                        if (args.Length < 3 || !float.TryParse(args[2], out s)) { arg.ReplyWith("papers.cp spawners delay <seconds> (0 = put the originals back)"); return; }
                        var n = OverrideSpawnerDelay(s);
                        CLog($"[probe] {n} Outpost spawn group(s) respawn delay {(s <= 0f ? "restored" : $"set to {s:0}-{s * 1.5f:0} s")} by admin.");
                    }
                    else if (mode != "list") { arg.ReplyWith("papers.cp spawners list|off|on|delay <seconds>"); return; }
                    arg.ReplyWith(DescribeSpawners());
                    return;
                }
                case "walk":
                {
                    // Probe 9.1(a): a loose guard walks the nearest road from the admin for N metres; progress, stalls and surfaces in the log.
                    // papers.cp walk <metres> [x y z] — from the admin's position, or from the given point (the driver).
                    float metres; if (name == null || !float.TryParse(name, out metres)) metres = 200f;
                    Vector3 from; float fx, fy, fz;
                    if (args.Length > 4 && float.TryParse(args[2], out fx) && float.TryParse(args[3], out fy) && float.TryParse(args[4], out fz)) from = new Vector3(fx, fy, fz);
                    else if (caller != null) from = caller.transform.position;
                    else { arg.ReplyWith("papers.cp walk <metres> [x y z] — from the console give a point."); return; }
                    List<Vector3> route; int roadIndex; float length;
                    if (!RoadRouteFrom(from, Mathf.Clamp(metres, 20f, 2000f), out route, out roadIndex, out length)) { arg.ReplyWith("No road within 60 m of that point."); return; }
                    var start = SnapToGround(route[0]);
                    var dir = route[1] - route[0]; dir.y = 0f;
                    var g = SpawnGuard(start, dir.normalized, false, null, false, "Cobalt Walker");
                    if (g == null) { arg.ReplyWith("The walker did not spawn."); return; }
                    g.GetComponent<GuardBrain>()?.StartWalk(route, _config.Patrols.WalkStallSeconds);
                    CLog($"[probe] walk: 'Cobalt Walker' at {V(start)} on road #{roadIndex}, {route.Count} point(s), {length:0} m; 'walk:' and 'stalled' lines follow.");
                    arg.ReplyWith($"Walker on road #{roadIndex}: {route.Count} points, {length:0} m. Progress in papers.cp log; 'papers.cp clearloose' removes him.");
                    return;
                }
                case "movetrigger":
                {
                    // Probe 9.1(b): an 8 m trigger sphere that follows the walker; ENTER/LEAVE lines for real players.
                    var walker = FindWalker();
                    if (walker == null) { arg.ReplyWith("No walker: run papers.cp walk first."); return; }
                    DestroyProbeTrigger();
                    var go = new GameObject("PapersPlease.ProbeTrigger");
                    go.layer = (int)global::Rust.Layer.Trigger;
                    go.transform.position = walker.transform.position + Vector3.up;
                    var col = go.AddComponent<SphereCollider>(); col.isTrigger = true; col.radius = 8f;
                    var trig = go.AddComponent<CheckpointTrigger>();
                    trig.InterestLayers = global::Rust.Layers.Mask.Player_Server | global::Rust.Layers.Mask.Vehicle_Large | global::Rust.Layers.Mask.Vehicle_Detailed | global::Rust.Layers.Mask.Vehicle_World;
                    trig.OnEntityEnterTrigger = e => { var bp = e as BasePlayer; if (bp != null && IsRealPlayer(bp)) CLog($"[probe] moving trigger: {bp.displayName} ENTER at {V(bp.transform.position)}, walker at {V(walker.transform.position)} ({Vector3.Distance(bp.transform.position, walker.transform.position):0.0} m)."); else if (e is BaseVehicle) CLog($"[probe] moving trigger: vehicle {e.ShortPrefabName} ENTER."); };
                    trig.OnEntityLeaveTrigger = e => { var bp = e as BasePlayer; if (bp != null && IsRealPlayer(bp)) CLog($"[probe] moving trigger: {bp.displayName} LEAVE at {V(bp.transform.position)}, walker at {V(walker.transform.position)} ({Vector3.Distance(bp.transform.position, walker.transform.position):0.0} m)."); };
                    _probeTriggerGo = go;
                    _probeTriggerTimer = timer.Every(0.25f, () =>
                    {
                        if (walker == null || walker.IsDestroyed || _probeTriggerGo == null) { DestroyProbeTrigger(); return; }
                        _probeTriggerGo.transform.position = walker.transform.position + Vector3.up;
                    });
                    CLog("[probe] moving trigger attached to the walker (8 m, 4 Hz).");
                    arg.ReplyWith("An 8 m trigger now follows the walker; walk into it and out again, then read papers.cp log.");
                    return;
                }
                case "ambushspot":
                {
                    // Probe 9.1(c): a point SpawnDistance from the admin, per-agent navmesh, out of their sight; a marker guard stands there.
                    // papers.cp ambushspot [player] — on the admin, or on a named player or sleeper (the driver).
                    var subject = name != null ? FindAnyPlayer(name) : caller;
                    if (subject == null) { arg.ReplyWith("papers.cp ambushspot [player] — no such player online or asleep."); return; }
                    string report; Vector3 spot;
                    var found = AmbushSpot(subject, _config.Ambush.SpawnDistance, out spot, out report);
                    CLog($"[probe] ambushspot for {subject.displayName} at {V(subject.transform.position)}: {report}");
                    if (found)
                    {
                        var toward = subject.transform.position - spot; toward.y = 0f;
                        var marker = SpawnGuard(spot, toward.normalized, false, null, false, "Cobalt Marker");
                        arg.ReplyWith($"{report}\nA 'Cobalt Marker' stands there{(marker == null ? " (spawn failed)" : "")}; can you see him from here? 'papers.cp clearloose' removes him.");
                    }
                    else arg.ReplyWith(report);
                    return;
                }
                case "navprobe":
                {
                    // Probe 9.1(c) diagnostic: what the navmesh answers around a player — per ring, how many of
                    // eight directions the plain AllAreas sample and the per-agent query accept, and the surfaces seen.
                    // papers.cp navprobe [player | x y z]
                    Vector3 o; string label; float nx, ny, nz;
                    if (args.Length > 3 && float.TryParse(args[1], out nx) && float.TryParse(args[2], out ny) && float.TryParse(args[3], out nz)) { o = new Vector3(nx, ny, nz); label = "the point"; }
                    else
                    {
                        var who = name != null ? FindAnyPlayer(name) : caller;
                        if (who == null) { arg.ReplyWith("papers.cp navprobe [player | x y z] — no such player online or asleep."); return; }
                        o = who.transform.position; label = who.displayName;
                    }
                    var guardAgent = GuardAgentTypeId();
                    var sbn = new StringBuilder($"[probe] navprobe around {label} at {V(o)} (guards' agent type {(guardAgent == int.MinValue ? "unknown: no live guard" : GuardAgentName(guardAgent))}; registered settings {NavMesh.GetSettingsCount()}):");
                    foreach (var radius in new[] { 0f, 20f, 40f, 80f, 120f, 200f })
                    {
                        var plainHits = 0; var agentHits = 0; var guardHits = 0; var surfaces = new HashSet<string>();
                        var dirs = radius <= 0f ? 1 : 8;
                        for (var i = 0; i < dirs; i++)
                        {
                            var p = SnapToGround(o + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * radius);
                            NavMeshHit hit;
                            if (NavMesh.SamplePosition(p, out hit, 6f, NavMesh.AllAreas)) plainHits++;
                            string surf; if (AgentTypeAt(p, out surf) != int.MinValue) { agentHits++; surfaces.Add(surf); }
                            if (guardAgent != int.MinValue && NavMesh.SamplePosition(p, out hit, 6f, new NavMeshQueryFilter { agentTypeID = guardAgent, areaMask = NavMesh.AllAreas })) guardHits++;
                        }
                        sbn.Append($"\n  r={radius:0} m: guard-agent {guardHits}/{dirs}, plain {plainHits}/{dirs}, registered {agentHits}/{dirs}{(surfaces.Count > 0 ? " [" + string.Join(", ", surfaces) + "]" : "")}");
                    }
                    var text = sbn.ToString();
                    CLog(text); arg.ReplyWith(text);
                    return;
                }
                case "cupboard":
                {
                    // Probe 9.1(d): the admin's building privilege and the nearest cupboard.
                    // papers.cp cupboard [player] — the admin, or a named player or sleeper (the driver).
                    var who = name != null ? FindAnyPlayer(name) : caller;
                    if (who == null) { arg.ReplyWith("papers.cp cupboard [player] — no such player online or asleep."); return; }
                    var priv = who.GetBuildingPrivilege();
                    BuildingPrivlidge nearest = null; var best = float.MaxValue;
                    foreach (var e in BaseNetworkable.serverEntities)
                    {
                        var p = e as BuildingPrivlidge;
                        if (p == null || p.IsDestroyed) continue;
                        var d = Vector3.Distance(p.transform.position, who.transform.position);
                        if (d < best) { best = d; nearest = p; }
                    }
                    var line = $"[probe] cupboard: {who.displayName} at {V(who.transform.position)} is {(priv != null ? "INSIDE the range of the cupboard at " + V(priv.transform.position) + (priv.IsAuthed(who) ? " (authed)" : " (not authed)") : "outside every cupboard's range")}; nearest cupboard {(nearest != null ? $"{best:0} m away at {V(nearest.transform.position)}" : "none on the map")}.";
                    CLog(line); arg.ReplyWith(line);
                    return;
                }
                case "safezone":
                {
                    // Test switch for decision 0009 §12: the Outpost safe zone shrunk to nothing / restored. Not persisted.
                    var mode = (name ?? "status").ToLowerInvariant();
                    if (_zone == null) { arg.ReplyWith("No Outpost safe zone on this map."); return; }
                    if (mode == "off") SetZoneDown(true, "admin");
                    else if (mode == "on") SetZoneDown(false, "admin");
                    else if (mode != "status") { arg.ReplyWith("papers.cp safezone off|on|status"); return; }
                    arg.ReplyWith($"Outpost safe zone: r={_zoneSphere.radius:0.0} m ({(_zoneDown ? "DOWN" : _curfewOn ? "curfew" : "stock")}), stock {_zoneOriginalRadius:0.0} m. Players inside were refreshed; the SAFE ZONE badge follows on their next move.");
                    return;
                }
                case "turret":
                {
                    // Task 6.0 probe: a loose sentry turret at the aim point (or at <x y z> from the console), facing you.
                    Vector3 pos, facing;
                    if (!SpotFromArgs(args, name, caller, out pos, out facing)) { arg.ReplyWith("papers.cp turret here | at <x> <y> <z> [yaw]  — spawn a loose Cobalt sentry (papers.cp clearloose removes it)"); return; }
                    if (LiveTurretCount() >= _config.Outpost.TurretCap) { arg.ReplyWith($"Turret cap {_config.Outpost.TurretCap} reached."); return; }
                    if (caller != null) { facing = caller.transform.position - pos; facing.y = 0f; }
                    var rot = facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing.normalized) : Quaternion.identity;
                    var ent = GameManager.server.CreateEntity(SentryPrefab, pos, rot);
                    if (ent == null) { arg.ReplyWith($"CreateEntity returned null for {SentryPrefab} — the static sentry is not spawnable; fall back to the deployable turret (task 6.0)."); return; }
                    ent.enableSaving = false;
                    ent.Spawn();
                    _looseProps.Add(ent);
                    var turret = ent as NPCAutoTurret;
                    var msg = $"Spawned {ent.ShortPrefabName} ({ent.GetType().Name}) at {V(pos)}: online={turret?.IsOnline()} peacekeeper={turret?.PeacekeeperMode()} sightRange={turret?.sightRange:0} hp={ent.Health():0}. Get flagged (Wanted at a gate) and walk past it; it must ignore Cobalt guards. papers.cp clearloose removes it.";
                    CLog("[outpost] " + msg);
                    arg.ReplyWith(msg);
                    return;
                }
                case "vaultdoor":
                case "switch":
                case "terminal":
                {
                    // Task 8.1 probe: a loose vault door (armored, code lock in the lock slot), sabotage switch or
                    // terminal at the aim point (or at <x y z> from the console), facing you. papers.cp clearloose removes them.
                    Vector3 pos, facing;
                    if (!SpotFromArgs(args, name, caller, out pos, out facing)) { arg.ReplyWith($"papers.cp {sub} here | at <x> <y> <z> [yaw]"); return; }
                    if (caller != null) { facing = caller.transform.position - pos; facing.y = 0f; }
                    var rot = facing.sqrMagnitude > 0.01f ? Quaternion.LookRotation(facing.normalized) : Quaternion.identity;
                    var prefab = sub == "vaultdoor" ? VaultDoorPrefab : sub == "switch" ? SwitchPrefab : TerminalPrefab;
                    // A door prefab's panel lies along its local X: LookRotation(facing) puts it across the
                    // doorway (probe 2026-09-18, "rotated 90 deg"). Quarter-turn it so the panel faces you.
                    if (sub == "vaultdoor") rot = rot * Quaternion.Euler(0f, 90f, 0f);
                    var ent = GameManager.server.CreateEntity(prefab, pos, rot);
                    if (ent == null) { arg.ReplyWith($"CreateEntity returned null for {prefab}."); return; }
                    ent.enableSaving = false;
                    foreach (var c in ent.GetComponentsInChildren<DestroyOnGroundMissing>(true)) UnityEngine.Object.DestroyImmediate(c);
                    foreach (var c in ent.GetComponentsInChildren<GroundWatch>(true)) UnityEngine.Object.DestroyImmediate(c);
                    var door = ent as Door;
                    if (door != null)
                    {
                        door.canNpcOpen = false;
                        // Door : AnimatedBuildingBlock : StabilityEntity. ServerInit runs UpdateStability →
                        // StabilityCheck, and a door with no building block has zero support, so the engine
                        // Kill(Gib)s it on the spawn tick (probe 2026-09-18: six doors gone at once, no death
                        // event). grounded = true makes SupportValue 1 like a foundation; decay = null keeps
                        // a door with no cupboard from rotting. The vault door (task 8.2) needs the same.
                        door.grounded = true;
                        door.decay = null;
                    }
                    ent.Spawn();
                    _looseProps.Add(ent);
                    var spawned = ent;
                    NextTick(() => { if (spawned == null || spawned.IsDestroyed) CLog($"[probe] {spawned?.ShortPrefabName ?? prefab} was destroyed within a tick of spawning."); });
                    var extra = "";
                    if (door != null)
                    {
                        var codeLock = GameManager.server.CreateEntity(CodeLockPrefab, Vector3.zero, Quaternion.identity) as CodeLock;
                        if (codeLock != null)
                        {
                            codeLock.enableSaving = false;
                            codeLock.SetParent(door, door.GetSlotAnchorName(BaseEntity.Slot.Lock));
                            codeLock.Spawn();
                            door.SetSlot(BaseEntity.Slot.Lock, codeLock);
                            codeLock.code = UnityEngine.Random.Range(1000, 9999).ToString();
                            SetFlagNet(codeLock, BaseEntity.Flags.Locked, true);
                            extra = $" lock={codeLock.ShortPrefabName} locked={codeLock.IsLocked()} code={codeLock.code}";
                        }
                        else extra = " (code lock: CreateEntity returned null)";
                    }
                    var msg = $"Spawned {ent.ShortPrefabName} ({ent.GetType().Name}) at {V(pos)} hp={ent.Health():0}/{ent.MaxHealth():0}{extra}. Loose prop #{_looseProps.Count}; papers.cp loose lists them, clearloose removes them.";
                    CLog("[probe] " + msg);
                    arg.ReplyWith(msg);
                    return;
                }
                case "turrets":
                {
                    // Task 8.1 probe (d): every Cobalt sentry on|off through AutoTurret.SetIsOnline.
                    var mode = (name ?? "").ToLowerInvariant();
                    if (mode != "on" && mode != "off") { arg.ReplyWith("papers.cp turrets on|off"); return; }
                    var n = 0;
                    foreach (var e in _looseProps) { var t = e as NPCAutoTurret; if (t != null && !t.IsDestroyed) { t.SetIsOnline(mode == "on"); n++; } }
                    foreach (var cp in _checkpoints.Values) foreach (var e in cp.Props) { var t = e as NPCAutoTurret; if (t != null && !t.IsDestroyed) { t.SetIsOnline(mode == "on"); n++; } }
                    CLog($"[probe] turrets {mode}: {n} sentr{(n == 1 ? "y" : "ies")}.");
                    arg.ReplyWith($"Set {n} sentr{(n == 1 ? "y" : "ies")} {mode}. Get flagged and walk past one: offline must not fire.");
                    return;
                }
                case "loose":
                {
                    var sb = new StringBuilder($"loose: {_looseGuards.Count} guard(s), {_looseProps.Count} prop(s)");
                    foreach (var e in _looseProps)
                    {
                        if (e == null || e.IsDestroyed) { sb.Append("\n  (destroyed)"); continue; }
                        var t = e as NPCAutoTurret; var d = e as Door; var sw = e as ElectricSwitch;
                        sb.Append('\n').Append("  ").Append(e.ShortPrefabName).Append(" @ ").Append(V(e.transform.position)).Append($" hp={e.Health():0}/{e.MaxHealth():0}");
                        if (t != null) sb.Append($" online={t.IsOnline()}");
                        if (d != null) sb.Append($" open={d.IsOpen()} locked={d.IsLocked()}");
                        if (sw != null) sb.Append($" on={sw.IsOn()}");
                    }
                    arg.ReplyWith(sb.ToString());
                    return;
                }
                case "sweep":
                {
                    // Orphan cleanup: Cobalt-named scientists that are NOT this build's GuardNpc (probe
                    // leftovers, guards from a previous assembly). Live gate guards are never touched.
                    var victims = new List<ScientistNPC>();
                    foreach (var e in BaseNetworkable.serverEntities)
                    {
                        var sci = e as ScientistNPC;
                        if (sci == null || sci.IsDestroyed || sci is GuardNpc) continue;
                        if (string.IsNullOrEmpty(sci.displayName) || !sci.displayName.StartsWith("Cobalt ")) continue;
                        victims.Add(sci);
                    }
                    var swept = 0;
                    foreach (var sci in victims)
                    {
                        try { sci.Die(new HitInfo(sci, sci, global::Rust.DamageType.Suicide, 10000f)); }
                        catch { try { if (!sci.IsDestroyed) sci.Kill(); } catch { } }
                        swept++;
                    }
                    CLog($"[sweep] removed {swept} orphaned Cobalt scientist(s) map-wide.");
                    arg.ReplyWith($"Swept {swept} orphaned Cobalt-named scientist(s) map-wide (live gate guards untouched).");
                    return;
                }
                case "roadblocks":
                    arg.ReplyWith(DescribeRoadblocks(viewer));
                    return;
                case "relocate":
                {
                    // Admin relocation ignores the age and the clear check (encounters there are aborted without penalty).
                    var moved = 0; var skipped = 0;
                    foreach (var cp in new List<Checkpoint>(_checkpoints.Values))
                    {
                        if (cp.Kind != CheckpointKind.Roadblock || cp.Pinned) continue;
                        if (name != null && !string.Equals(cp.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (cp.State != CheckpointState.Active) { skipped++; continue; }
                        if (RelocateRoadblock(cp, "admin")) moved++; else skipped++;
                    }
                    arg.ReplyWith($"Relocated {moved} roadblock(s), {skipped} skipped (not active or no new spot). {DescribeRoadblocks(viewer)}");
                    return;
                }
                case "roadblock":
                {
                    // `papers.cp roadblock here`: a pinned roadblock at the aim point, facing the way you face;
                    // `papers.cp roadblock at <x> <y> <z> [yaw]` does the same from the console (self-test).
                    Vector3 pos, fwd;
                    if (!SpotFromArgs(args, name, caller, out pos, out fwd)) { arg.ReplyWith("papers.cp roadblock here | at <x> <y> <z> [yaw]  — force a pinned roadblock (remove with papers.cp despawn <name>)"); return; }
                    var n = 1;
                    while (_checkpoints.ContainsKey($"roadblock-here-{n}")) n++;
                    var cp = new Checkpoint
                    {
                        Name = $"roadblock-here-{n}",
                        Kind = CheckpointKind.Roadblock,
                        Pinned = true,
                        Template = new GateTemplate { Name = $"roadblock-here-{n}", Monument = "road", Searchlight = true, Perimeter = false },
                        Road = new RoadSample { Position = SnapToGround(pos), Forward = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward, Width = 10f, RoadIndex = -1 },
                    };
                    cp.Position = cp.Road.Position;
                    cp.Rotation = Quaternion.LookRotation(cp.Road.Forward);
                    _checkpoints[cp.Name] = cp;
                    var ok = TrySpawn(cp);
                    arg.ReplyWith(ok ? $"Roadblock '{cp.Name}' at {V(cp.Position)} with {cp.LiveGuards()} guard(s), chicane and light. papers.cp despawn {cp.Name} removes it." : $"Not spawned: {cp.StateNote}");
                    return;
                }
                case "propat":
                {
                    // Self-test / console placement: papers.cp propat <gate> <kind> <minTier> <x> <y> <z> [yaw]
                    // — offset relative to the gate (x right, y up, z toward the approach), like /papers gate prop.
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null || cp.Kind != CheckpointKind.Gate) { arg.ReplyWith(L("Gate.NotFound", viewer, name ?? "")); return; } // gates only, see /papers gate prop
                    int minTier; float x, y, z, yaw = 0f;
                    if (args.Length < 7 || PropPrefab(args[2]) == null || !int.TryParse(args[3], out minTier) || minTier < 1 || minTier > 5
                        || !float.TryParse(args[4], out x) || !float.TryParse(args[5], out y) || !float.TryParse(args[6], out z)
                        || (args.Length > 7 && !float.TryParse(args[7], out yaw)))
                    { arg.ReplyWith("papers.cp propat <gate> <kind> <1-5> <x> <y> <z> [yaw]"); return; }
                    var p = AddProp(cp, args[2].ToLowerInvariant(), minTier, new Vector3(x, y, z), yaw);
                    arg.ReplyWith(L("Gate.PropAdded", viewer, p.Kind, cp.Name, p.Offset, p.Yaw) + (minTier > 1 ? $" MinTier={minTier}." : ""));
                    return;
                }
                case "kill":
                {
                    // Test helper: every guard at a checkpoint dies (no killer) — exercises the destroyed/respawn path.
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) { arg.ReplyWith(L("Gate.NotFound", viewer, name ?? "")); return; }
                    var n = 0;
                    foreach (var g in new List<GuardNpc>(cp.Guards))
                    {
                        if (g == null || g.IsDestroyed) continue;
                        try { g.Die(new HitInfo(g, g, global::Rust.DamageType.Suicide, 10000f)); n++; }
                        catch (Exception ex) { CLog($"[cp] kill '{cp.Name}': {ex.Message}"); }
                    }
                    arg.ReplyWith($"Killed {n} guard(s) at '{cp.Name}'; the checkpoint falls on the next tick.");
                    return;
                }
                case "roadsample":
                {
                    string report;
                    var sample = SampleRoad(out report);
                    if (sample == null) { arg.ReplyWith($"No road spot found after {_config.Roadblocks.SampleRetries} tries. {report}"); return; }
                    var msg = $"Road spot at {V(sample.Position)} heading {V(sample.Forward)} on road #{sample.RoadIndex} (width {sample.Width:0.0} m, point {sample.PointIndex}). {report}";
                    CLog("[road] " + msg);
                    if (caller != null && !string.Equals(name, "stay", StringComparison.OrdinalIgnoreCase))
                        caller.Teleport(sample.Position - sample.Forward * 12f + Vector3.up * 0.5f);
                    arg.ReplyWith(msg + (caller != null ? " Teleported 12 m short of it, facing along the road." : ""));
                    return;
                }
                case "monuments":
                {
                    if (caller == null) { arg.ReplyWith("Run in-game."); return; }
                    float d; string alts;
                    var m = NearestMonument(caller.transform.position, null, out d, out alts);
                    arg.ReplyWith($"Owner here: {(m != null ? MonumentShortName(m) : "none")} ({d:0} m). Nearby: {alts}");
                    return;
                }
                case "cooldowns":
                {
                    // Test helper: forget every post-encounter cooldown so a gate halts again at once.
                    var n = 0;
                    foreach (var cp in _checkpoints.Values) { n += cp.CooldownUntil.Count; cp.CooldownUntil.Clear(); }
                    arg.ReplyWith($"Cleared {n} encounter cooldown(s).");
                    return;
                }
                case "alert":
                {
                    var cp = name != null ? FindCheckpoint(name) : null;
                    if (cp == null) { arg.ReplyWith(L("Gate.NotFound", viewer, name ?? "")); return; }
                    cp.AlertUntil = Time.realtimeSinceStartup + _config.Checkpoints.AlertMinutes * 60f;
                    UpdatePropLights();
                    arg.ReplyWith($"'{cp.Name}' on alert for {_config.Checkpoints.AlertMinutes} min.");
                    return;
                }
                case "log":
                {
                    int want;
                    var n = args.Length > 1 && int.TryParse(args[1], out want) ? Mathf.Clamp(want, 1, LogCap) : 30;
                    var start = Mathf.Max(0, _clog.Count - n);
                    var sb = new StringBuilder();
                    for (var i = start; i < _clog.Count; i++) sb.AppendLine(_clog[i]);
                    arg.ReplyWith(sb.Length == 0 ? "(log empty)" : sb.ToString());
                    return;
                }
                case "status":
                    arg.ReplyWith($"Cobalt Papers Please v{Version} | tier={CurrentTier()} checkpoints={_checkpoints.Count} guards={LiveGuardCount()}/{_config.Checkpoints.GuardCap} loose={_looseGuards.Count} gates(config)={_config.Gates.Count} perimeters={_perimeters.Count}");
                    return;
                default:
                    arg.ReplyWith("papers.cp list | gate add <name> [guards] [monument] | gate remove|tp <name> | gate prop <name> <kind> [tier] | gate tier <name> <1-5> | gate radius <name> <m> | gate reload | respawn <name> | despawn <name> | guard [heavy] | guards | clearloose | cooldowns clear | alert <name> | monuments | roadblocks | relocate [slot] | roadblock here | roadsample [stay] | propat <gate> <kind> <1-5> <x> <y> <z> [yaw] | kill <name> | outpost | curfew on|off|auto | turret here|at <x> <y> <z> | sweep | log [n] | status | vaultdoor|switch|terminal here | turrets on|off | loose | safezone off|on|status | lights");
                    return;
            }
        }

        private static string DescribeGuard(int i, GuardNpc g, string owner, BasePlayer caller)
        {
            if (g == null || g.IsDestroyed) return $"#{i} {owner}: destroyed";
            var brain = g.GetComponent<GuardBrain>();
            var drift = Vector3.Distance(g.transform.position, g.PostPos);
            var toCaller = caller != null ? Vector3.Distance(g.transform.position, caller.transform.position) : -1f;
            return $"#{i} {owner} '{g.GuardName}' hp={g.health:0}/{g.MaxHealth():0} thinks={g.ThinkCount} drift={drift:0.00}m inSafeZone={g.InSafeZone()} distToYou={toCaller:0.0} {brain?.Describe()}";
        }

        #endregion

        #region API (Oxide plugin calls)

        // int GetReputation(ulong id)
        private int GetReputation(ulong id) => IsRealPlayerId(id) && _store != null ? ReadScore(id) : 0;

        // string GetBand(ulong id) — "Citizen" | "Neutral" | "Suspect" | "Wanted" | "Enemy"; exempt players read as Citizen
        private string GetBand(ulong id)
        {
            if (!IsRealPlayerId(id) || _store == null) return Band.Neutral.ToString();
            if (IsExemptId(id)) return Band.Citizen.ToString();
            return BandOf(ReadScore(id)).ToString();
        }

        // string GetBand(string userId) — the same for callers that hold the SteamID as text (RCON, covalence)
        private string GetBand(string userId)
        {
            ulong id;
            return ulong.TryParse(userId, out id) ? GetBand(id) : Band.Neutral.ToString();
        }

        // int AdjustReputation(ulong id, int delta, string reason) — returns the new score
        private int AdjustReputation(ulong id, int delta, string reason) => ApplyChange(id, delta, reason ?? L("Reason.Admin"), false);

        // void SetReputation(ulong id, int score, string reason)
        private void SetReputation(ulong id, int score, string reason) => ApplyChange(id, score, reason ?? L("Reason.Admin"), true);

        // bool IsExempt(ulong id)
        private bool IsExempt(ulong id) => IsRealPlayerId(id) && IsExemptId(id);

        // int GetThreatTier() — 1..5 from the threat clock (admin pin wins)
        private int GetThreatTier() => CurrentTier();

        // double GetThreat() — the 0..100 threat value (wipe-age base + violence bonus)
        private double GetThreat() => _threat != null ? _threat.Value : BaseThreat(HoursSinceWipe());

        // object GetHelpInfo() — the HelpMenu plugin's entry (its hook schema); the text is lang,
        // the numbers come from the live config so the page never drifts from the server.
        [HookMethod("GetHelpInfo")]
        private object GetHelpInfo()
        {
            var b = _config.Bands;
            var r = _config.Reputation;
            var commands = new List<Dictionary<string, object>>
            {
                HelpCommand("/papers", "", L("Help.Cmd.Papers"), 0),
                HelpCommand("/papers", "request", L("Help.Cmd.Request", null, _config.Papers.IdFee), 0),
                HelpCommand("/threat", "", L("Help.Cmd.Threat"), 0),
            };
            if (_config.Papers.ForgeAtWorkbench)
                commands.Add(HelpCommand("/papers", "forge", L("Help.Cmd.Forge", null, _config.Papers.ForgeWorkbenchLevel), 0));
            commands.Add(HelpCommand("/papers", "<player> | set|add|reset <player> <n> | top [n]", L("Help.Cmd.AdminRep"), 2));
            commands.Add(HelpCommand("/threat", "tier <1-5>|off", L("Help.Cmd.AdminThreat"), 2));
            commands.Add(HelpCommand("/papers", "gate|fence|vault|sabotage ...", L("Help.Cmd.AdminPlace"), 2));
            commands.Add(HelpCommand("papers.cp / papers.ambush", "status", L("Help.Cmd.AdminConsole"), 2));
            var notes = new List<string>(L("Help.Notes").Split('\n'));
            if (plugins.Find("PublicWorks") != null && (r.PublicWorksRepairMinor != 0 || r.PublicWorksRepairMajor != 0))
                notes.Add(L("Help.NotePublicWorks", null, r.PublicWorksRepairMinor, r.PublicWorksRepairMajor));
            if (plugins.Find("IslandTaxi") != null) notes.Add(L("Help.NoteTaxi"));
            return new Dictionary<string, object>
            {
                ["Plugin"] = Name,
                ["Title"] = L("Help.Title"),
                ["Description"] = L("Help.Description"),
                ["Commands"] = commands,
                ["HowTo"] = new List<string>(L("Help.HowTo", null, b.CitizenMin, b.SuspectMax, b.WantedMax, b.EnemyMax).Split('\n')),
                ["Notes"] = notes,
                ["Order"] = 20,
            };
        }

        private static Dictionary<string, object> HelpCommand(string command, string args, string description, int authLevel) =>
            new Dictionary<string, object> { ["Command"] = command, ["Args"] = args, ["Description"] = description, ["Permission"] = "", ["AuthLevel"] = authLevel };

        #endregion
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Server side of the tutorial's opening (owner's design, step 4b, from the
    /// client's own stage script): populates the map's static zombies, roaming
    /// zombies and barricades, then drives stages 2 (Move) to 6, and respawns the
    /// player. Runs on the game thread, after each GameManager update.
    /// </summary>
    public sealed class TutorialDirector
    {
        private const BindingFlags All = GameRuntime.All;
        public const float StaticAggroRange = 150f;
        public const float RoamingScatter = 40f;
        public const float MoveDistance = 30f;   // a short real move shows the paddle near the start (owner, 4h)
        public const float PaddleTouchRange = 20f;
        public const float FenceNearRange = 250f;
        public const float RespawnSeconds = 5f;
        /// <summary>The companion comes back this long after she dies (ours; the client has no rule for it).</summary>
        public const float CompanionReviveSeconds = 5f;
        /// <summary>the player respawns where she stood this long before she died: near the fight, on walkable ground (ours).</summary>
        public const float RespawnLookback = 4f;
        public const int FenceGroup = 3002, ElectricGroup = 3003, WoodenGroup = 10103;
        public const int PaddleSchematic = 1005;
        public const float BarricadeFallbackHp = 100f;
        public const int ShotgunSchematic = 1009;
        public const float HeroStrikeInterval = 2.5f, SupplyDropRadius = 30f;
        /// <summary>Share of the player's damage the wooden barricade takes in stage 12 (owner: she must not brute-force it).</summary>
        public const float PlayerBarricadeDamage = 0.10f;
        public const float PaddleNoticeRange = 60f;
        public static float MinStageSeconds = 1f;   // the offline probe teleports and sets this to 0
        public const float BarricadeReach = 25f;
        public const float PullerTriggerRange = 120f, PullerNearRange = 200f, PullerStrikeRange = 40f;
        public const float HookRetrySeconds = 1f;
        public const float GeneratorRange = 150f, PreBarricadeRange = 120f, AmbushRange = 120f, StorageRange = 120f;
        public static readonly float[] StoragePoint = { 493f, 3660f };   // stage 13's marker (owner's design)
        public const int AmbushId = 10103;
        // Stages 14-24 (the client's tutorial script; trigger distances and timings are our guesses).
        public const int StorageId = 10038;
        public const float StorageDefendSeconds = 30f, StorageRespawnMin = 1f, StorageRespawnMax = 2f;
        public const int StorageMaxWalkers = 5;
        public const int SupplyTotal = 20, SupplyPerPickup = 5;
        public const float EnemyRange = 200f, TruckRange = 150f, EndDelay = 3f;
        public static readonly float[] TruckPoint = { -181f, 3353f };   // stages 17, 20, 21 in the client's script
        /// <summary>Hint counter: TutorialState.HintBandage = BANDAGE (1 shown, 2 done).</summary>
        private const string HintBandage = "TutorialState.HintBandage";
        public static readonly float[] AmbushPoint = { 1077f, 3792f };   // stage 11's marker in the client's script
        public const float CompanionNear = 30f, CompanionFar = 60f, CompanionGuardRange = 80f, CompanionAttackRange = 10f, CompanionAttackInterval = 1f;
        /// <summary>Hint counters: TutorialState.HintAbility = ABILITY (2 shown, 3 done), TutorialState.HintDash = DASH (1 shown, 2 done).</summary>
        private const string HintAbility = "TutorialState.HintAbility", HintDash = "TutorialState.HintDash";
        /// <summary>Hint counters in the tutorial state: 1 = ATTACK / ATTACK2, 2 = done.</summary>
        private const string HintAttack = "TutorialState.HintAttack", HintAttack2 = "TutorialState.HintAttack2";

        private readonly GameRuntime _g;
        private readonly Action<string> _log;
        private readonly object _player;
        private readonly SpawnPoint _spawn;
        private readonly Random _random = new Random();

        public readonly List<object> Zombies = new List<object>();
        public readonly Dictionary<int, List<object>> BarricadeGroups = new Dictionary<int, List<object>>();
        private object _paddle;
        private float[] _paddleAt, _fenceAt, _moveFrom, _companionAt, _pullerAt, _pullerTriggerAt;
        public object Companion, Puller, Generator;
        private float[] _generatorAt, _preBarricadeAt;

        /// <summary>Event_Ambush 10103 and its Spawn_EventEntities (linked by ID in the map).</summary>
        public sealed class AmbushSpot { public float X, Y, DX, DY; public object State; }
        public readonly List<AmbushSpot> AmbushPoints = new List<AmbushSpot>();
        public readonly List<object> AmbushZombies = new List<object>();
        public readonly List<AmbushSpot> StorageSpots = new List<AmbushSpot>();
        public readonly List<object> StorageZombies = new List<object>();
        public object StorageRoom, RivalA, RivalB, Floater;
        public readonly List<object> Pickups = new List<object>();
        public readonly List<object> Truckers = new List<object>();
        private float[] _storageAt, _enemyAt, _floaterAt;
        private readonly List<float[]> _truckerAt = new List<float[]>();
        private float _stageSince = -1f, _nextStorageSpawn = -1f;
        public int AmbushMaxWalkers, AmbushMaxSpecials, AmbushSpawned;
        private float _ambushRateMin = 0.5f, _ambushRateMax = 1f, _nextAmbushSpawn = -1f, _ambushSecond = -1f;
        private int _ambushThisSecond;
        private bool _hooking;
        private float _lastHookTry = -100f, _companionLastAttack = -100f, _time;
        public float[] FenceAt { get { return _fenceAt; } }
        /// <summary>Called once when the tutorial reaches stage 24.</summary>
        public Action OnCompleted;
        private float _diedAt = -1f, _companionDiedAt = -1f, _lastSample = -100f;
        /// <summary>Where the player was while alive, sampled every 0.5 s (time, x, y), last 30 s.</summary>
        private readonly List<float[]> _trail = new List<float[]>();

        public TutorialDirector(GameRuntime g, object player, SpawnPoint spawn, Action<string> log)
        {
            _g = g;
            _player = player;
            _spawn = spawn;
            _log = log;
        }

        // ---- world ----

        /// <summary>Spawns the map's static zombies, roaming zombies and barricades.</summary>
        public void PopulateWorld()
        {
            int statics = 0, roaming = 0;
            foreach (object o in _g.MapObjects())
            {
                string kind = o.GetType().Name;
                float[] p = _g.MapPosition(o);
                float[] d = _g.MapDirection(o);
                if (kind == "Spawn_Static")
                {
                    object z = _g.SpawnNpc(GameRuntime.PlainZombieType, p[0], p[1], GameRuntime.ZombieTeam, d);
                    object state = o.GetType().GetProperty("SpawnState", All).GetValue(o, null);
                    try { z.GetType().GetProperty("SpawnState_Current", All).SetValue(z, state, null); }
                    catch (Exception e) { _log("could not set the spawn state " + state + ": " + GameRuntime.Unwrap(e).Message); }
                    ServerHooks.AggroRange[z] = StaticAggroRange;
                    Zombies.Add(z);
                    statics++;
                }
                else if (kind == "Spawn_RoamingZombies")
                {
                    int min = (int)o.GetType().GetField("Walkers_Min").GetValue(o), max = (int)o.GetType().GetField("Walkers_Max").GetValue(o);
                    int count = _random.Next(min, max + 1);
                    for (int i = 0; i < count; i++)
                    {
                        double a = _random.NextDouble() * Math.PI * 2, r = _random.NextDouble() * RoamingScatter;
                        Zombies.Add(_g.SpawnNpc(GameRuntime.PlainZombieType, p[0] + (float)(Math.Cos(a) * r), p[1] + (float)(Math.Sin(a) * r), GameRuntime.ZombieTeam, d));
                        roaming++;
                    }
                }
                else if (kind == "Spawn_Destructible")
                {
                    int group = (int)o.GetType().GetField("GroupID").GetValue(o);
                    object barricade = SpawnBarricade(o, p, d);
                    if (!BarricadeGroups.ContainsKey(group)) BarricadeGroups[group] = new List<object>();
                    BarricadeGroups[group].Add(barricade);
                    if (group == FenceGroup) _fenceAt = p;
                }
                else if (kind == "Event_Ambush" && Convert.ToInt32(GameRuntime.MapValue(o, "ID")) == AmbushId)
                {
                    AmbushMaxWalkers = Convert.ToInt32(GameRuntime.MapValue(o, "MaxActiveWalkers"));
                    AmbushMaxSpecials = Convert.ToInt32(GameRuntime.MapValue(o, "MaxActiveSpecials"));
                    _ambushRateMin = Convert.ToSingle(GameRuntime.MapValue(o, "RespawnRateMin"));
                    _ambushRateMax = Convert.ToSingle(GameRuntime.MapValue(o, "RespawnRateMax"));
                    _log("tutorial: ambush " + AmbushId + ": MaxActiveWalkers " + AmbushMaxWalkers + ", MaxActiveSpecials " + AmbushMaxSpecials +
                         ", RespawnRate " + _ambushRateMin + "-" + _ambushRateMax + " s, RespawnSpecials " + GameRuntime.MapValue(o, "RespawnSpecials"));
                }
                else if (kind == "Spawn_EventEntities" && Convert.ToInt32(GameRuntime.MapValue(o, "ID")) == AmbushId)
                    AmbushPoints.Add(new AmbushSpot { X = p[0], Y = p[1], DX = d[0], DY = d[1], State = GameRuntime.MapValue(o, "SpawnState") });
                else if (kind == "Spawn_EventEntities" && Convert.ToInt32(GameRuntime.MapValue(o, "ID")) == StorageId)
                    StorageSpots.Add(new AmbushSpot { X = p[0], Y = p[1], DX = d[0], DY = d[1], State = GameRuntime.MapValue(o, "SpawnState") });
                else if (kind == "Event_StorageRoom")
                    _storageAt = new[] { p[0], p[1] };
                else if (kind == "Event_Assault" && Convert.ToInt32(GameRuntime.MapValue(o, "Floaters_Max")) > 0)
                    _floaterAt = new[] { p[0], p[1] };
                else if (kind == "Trigger_TutorialLightingGenerator")
                    _generatorAt = new[] { p[0], p[1] };
                else if (kind == "Spawn_Tutorial")
                {
                    string type = o.GetType().GetField("TutorialType").GetValue(o).ToString();
                    float[] at = new[] { p[0], p[1], d[0], d[1] };
                    if (type == "PADDLE") _paddleAt = at;
                    else if (type == "NPC") _companionAt = at;
                    else if (type == "PULLER") _pullerAt = at;
                    else if (type == "TRIGGER_PULLER") _pullerTriggerAt = at;
                    else if (type == "PREBARRICADE") _preBarricadeAt = at;
                    else if (type == "ENEMY") _enemyAt = at;
                    else if (type == "NPC_TRUCK") _truckerAt.Add(at);
                }
            }
            // Each group's pieces share one health pool through the game's own barricade group
            // (BarricadeGroup: ctor(group id, hp); BarricadeGroup.Add adds a piece; a hit on one piece sets the others
            // to the same health). The client never builds these, so the server must.
            Type groupType = R.Type("BarricadeGroup");
            foreach (var kv in BarricadeGroups)
            {
                object group = groupType.GetConstructor(All, null, new[] { typeof(int), typeof(int) }, null).Invoke(new object[] { kv.Key, (int)BarricadeFallbackHp });
                foreach (object piece in kv.Value) groupType.GetMethod(R.Name("BarricadeGroup.Add"), All).Invoke(group, new[] { piece });
            }
            foreach (object piece in Group(ElectricGroup))
                piece.GetType().GetMethod(R.Name("Barricade.MakeInvulnerable"), All).Invoke(piece, null);
            _log("tutorial: ambush " + AmbushId + " has " + AmbushPoints.Count + " Spawn_EventEntities points; storage room " + StorageId + " has " + StorageSpots.Count +
                 ", ENEMY " + (_enemyAt != null) + ", Floater assault " + (_floaterAt != null) + ", NPC_TRUCK " + _truckerAt.Count);
            SpawnTruckers();
            SpawnPaddle();   // lying there from the start (owner, 4i); stage 2 -> 3 still marks the move
            ServerHooks.DamageFilter = (entity, changer, value) =>
            {
                if (_g.TutorialStage != 12 || !Group(WoodenGroup).Contains(entity) || changer == null) return value;
                object owner = changer == _player ? _player : GetField(changer, "Owner") ?? (changer.GetType().GetProperty("Owner", All) == null ? null : changer.GetType().GetProperty("Owner", All).GetValue(changer, null));
                return owner == _player ? value * PlayerBarricadeDamage : value;
            };
            // Ambush walkers go for whichever of the player and Companion is nearer.
            ServerHooks.ChooseTarget = (npc, player) =>
            {
                if (Companion == null || Destroyed(Companion) || !AmbushZombies.Contains(npc)) return null;
                float[] n = ServerHooks.Position(npc), pa = ServerHooks.Position(player), pb = ServerHooks.Position(Companion);
                return Distance(n, pb[0], pb[1]) < Distance(n, pa[0], pa[1]) ? Companion : null;
            };
            _log("tutorial: populated " + statics + " static zombies, " + roaming + " roaming zombies, barricades " +
                 string.Join(", ", BarricadeGroups.Select(kv => kv.Key + " x" + kv.Value.Count).ToArray()) + " (electric group invulnerable)");
        }

        /// <summary>A barricade piece from the pool, set up like the game's horde spawner does.</summary>
        private object SpawnBarricade(object map, float[] p, float[] d)
        {
            Type t = R.Type("Barricade");
            object b = _g.TakeFromPool(t);
            Type mt = map.GetType();
            b.GetType().GetField("Position", All).SetValue(b, _g.Vector2(p[0], p[1]));
            b.GetType().GetField("SpawnPosition", All).SetValue(b, _g.Vector2(p[0], p[1]));
            t.GetField(R.Name("Barricade.Facing"), All).SetValue(b, _g.Vector2(d[0], d[1]));
            FieldInfo part = t.GetField(R.Name("Barricade.Part"), All);
            part.SetValue(b, Enum.Parse(part.FieldType, mt.GetField("Part").GetValue(map).ToString()));
            t.GetField(R.Name("Barricade.Kind"), All).SetValue(b, mt.GetField("BarricadeType").GetValue(map));
            // The map says Team2. The fence and wooden pieces are spawned Neutral so a Team2
            // player can break them; the electric fence keeps Team2 so her hits don't count
            // (owner's design, steps 4b and 4d).
            int groupId = (int)mt.GetField("GroupID").GetValue(map);
            string teamName = groupId == ElectricGroup ? "Team2" : "Neutral";
            FieldInfo team = t.GetField(R.Name("Barricade.Team"), All);
            team.SetValue(b, Enum.Parse(team.FieldType, teamName));
            b.GetType().GetProperty("TeamId", All).SetValue(b, Enum.Parse(team.FieldType, teamName), null);
            _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { b, null, null });
            float hp = ServerHooks.GetStat(b, "MaxHealth");
            string source = "its own default";
            // The client has no default for map barricades (only the horde spawner sets hp,
            // to max float); the original server chose it. Fallback, open question.
            if (hp <= 0) { hp = BarricadeFallbackHp; source = "fallback: the client has no default"; }
            t.GetMethod(R.Name("Barricade.SetHealth"), All).Invoke(b, new object[] { hp });
            _log("tutorial: barricade " + ServerHooks.Describe(b) + " " + mt.GetField("BarricadeType").GetValue(map) + " " + mt.GetField("Part").GetValue(map) +
                 " group " + mt.GetField("GroupID").GetValue(map) + " at (" + p[0].ToString("0") + ", " + p[1].ToString("0") + "), hp " + hp + " (" + source +
                 "), Health now " + ServerHooks.GetStat(b, "Health"));
            return b;
        }

        private List<object> Group(int id)
        {
            List<object> l;
            return BarricadeGroups.TryGetValue(id, out l) ? l : new List<object>();
        }

        // ---- stages ----

        /// <summary>One step after the game's own update.</summary>
        public void Tick(float time)
        {
            _time = time;
            Respawn(time);
            ReviveCompanion(time);
            int stage = _g.TutorialStage;
            float[] a = ServerHooks.Position(_player);
            // Each stage lasts at least MinStageSeconds so the client sees it (its hints are shown
            // and hidden per stage; 15 -> 16 -> 17 in one tick left "Remain close" on screen).
            if (_stageSince >= 0f && time - _stageSince < MinStageSeconds && stage != 15 && stage != 12)
            {
                if (stage == 14) WatchCapture();
                if (_g.TutorialStage >= 8 && Companion != null) CompanionAi(time);
                return;
            }
            // Stage 2 counts movement from where the player stands when it starts: she can walk during stage 1.
            if (stage != 2) _moveFrom = null;
            else if (_moveFrom == null) _moveFrom = new[] { a[0], a[1] };
            if (stage == 2 && (Distance(a, _moveFrom[0], _moveFrom[1]) >= MoveDistance || (_paddleAt != null && Distance(a, _paddleAt[0], _paddleAt[1]) <= PaddleNoticeRange)))
                SetStage(3, "the player moved " + MoveDistance + " units or reached the paddle");
            else if (stage == 3 && _paddle != null)
            {
                if (Distance(a, _paddleAt[0], _paddleAt[1]) <= PaddleTouchRange) PickUpPaddle();
            }
            else if (stage == 4 && _fenceAt != null && Distance(a, _fenceAt[0], _fenceAt[1]) <= FenceNearRange)
            {
                _g.SetTutorialField(HintAttack2, 1);
                SetStage(5, "the player is within " + FenceNearRange + " units of the fence");
            }
            else if (stage == 5 && Group(FenceGroup).Count > 0 && Group(FenceGroup).All(Destroyed))
            {
                _g.SetTutorialField(HintAttack, 2);
                _g.SetTutorialField(HintAttack2, 2);
                SetStage(6, "the fence is destroyed");
            }
            else if (stage == 6 && Puller != null && Companion != null)
            {
                float[] pu = ServerHooks.Position(Puller);
                if (!_hooking && (Distance(a, _pullerTriggerAt[0], _pullerTriggerAt[1]) <= PullerTriggerRange || Distance(a, pu[0], pu[1]) <= PullerNearRange))
                {
                    _hooking = true;
                    _log("tutorial: the player triggered the Puller (" + Distance(a, _pullerTriggerAt[0], _pullerTriggerAt[1]).ToString("0") + " from the trigger, " +
                         Distance(a, pu[0], pu[1]).ToString("0") + " from the Puller)");
                }
                if (_hooking) HookCompanion(time);
            }
            else if (stage == 7 && Puller != null && Destroyed(Puller))
                SetStage(8, "the Puller is dead");
            else if (stage == 8 && _generatorAt != null && Distance(a, _generatorAt[0], _generatorAt[1]) <= GeneratorRange)
                SetStage(9, "the player is within " + GeneratorRange + " units of the generator");
            else if (stage == 9 && Generator != null && !(bool)Generator.GetType().GetProperty("IsActive", All).GetValue(Generator, null))
                SetStage(10, "the generator is destroyed");
            else if (stage == 10 && _preBarricadeAt != null && Distance(a, _preBarricadeAt[0], _preBarricadeAt[1]) <= PreBarricadeRange)
                SetStage(11, "the player is within " + PreBarricadeRange + " units of PREBARRICADE");
            else if (stage == 11 && Distance(a, AmbushPoint[0], AmbushPoint[1]) <= AmbushRange)
                SetStage(12, "the player is within " + AmbushRange + " units of (1077, 3792)");
            else if (stage == 12)
            {
                Ambush(time);
                BandageHint();
                if (Group(WoodenGroup).Count > 0 && Group(WoodenGroup).All(Destroyed))
                    SetStage(13, "the wooden barricade is destroyed");
            }
            else if (stage == 13 && Distance(a, StoragePoint[0], StoragePoint[1]) <= StorageRange)
                SetStage(14, "the player is within " + StorageRange + " units of (493, 3660)");
            else if (stage == 14 && StorageRoom != null)
            {
                WatchCapture();
                if (GetField(StorageRoom, "PerformedTriggerCapture").Equals(PlayerTeamId()))
                    SetStage(15, "the player triggered the capture");
            }
            else if (stage == 15)
            {
                StorageDefense(time);
                if (time - _stageSince >= StorageDefendSeconds) SetStage(16, "the player held the storage room for " + StorageDefendSeconds + " s");
            }
            else if (stage == 16 && CarriedSupplies() > 0)
                SetStage(17, "the player picked up supplies (" + CarriedSupplies() + ")");
            else if (stage == 17 && _enemyAt != null && Distance(a, _enemyAt[0], _enemyAt[1]) <= EnemyRange)
                SetStage(18, "the player is within " + EnemyRange + " units of the ENEMY marker");
            else if (stage == 18 && (Gone(RivalA) || Gone(RivalB)))
                SetStage(19, "one of the two rivals is dead");
            else if (stage == 19 && Gone(RivalA) && Gone(RivalB))
                SetStage(20, "the two rivals are dead");
            else if (stage == 20 && Distance(a, TruckPoint[0], TruckPoint[1]) <= TruckRange)
                SetStage(21, "the player is within " + TruckRange + " units of the truck");
            else if (stage == 21)
            {
                WatchThrownSupplies();
                if (TeamSupplies() > 0) SetStage(22, "supplies delivered (team " + TeamSupplies() + ")");
            }
            else if (stage == 22 && Floater != null && Gone(Floater))
                SetStage(23, "the Floater is dead");
            else if (stage == 23 && time - _stageSince >= EndDelay)
                SetStage(24, "end of the tutorial");
            if (_g.TutorialStage >= 8 && Companion != null) CompanionAi(time);
        }

        // ---- the rescue (stages 6 to 8) ----

        /// <summary>Companion (tutorial NPC, Team2) and the Puller (Team3) at their map spawns, both idle.</summary>
        private void SpawnRescue()
        {
            if (_companionAt == null || _pullerAt == null || _pullerTriggerAt == null) { _log("tutorial: the map lacks the NPC, PULLER or TRIGGER_PULLER spawn"); return; }
            Companion = SpawnCompanion(_companionAt[0], _companionAt[1], _companionAt[2], _companionAt[3]);
            // She stands until she becomes the companion (stage 8): undirected, she wanders off
            // out of the hook's reach (seen in game, 14:15).
            ServerHooks.SetField(Companion, "_Acceleration", 0f);
            Puller = _g.SpawnNpc(R.Name("Zombie.Puller"), _pullerAt[0], _pullerAt[1], GameRuntime.ZombieTeam, new[] { _pullerAt[2], _pullerAt[3] });
            ServerHooks.Passive.Add(Puller);
            ServerHooks.AttackAbility[Puller] = Ability("PullerStrike");
            _log("tutorial: Companion " + ServerHooks.Describe(Companion) + " at (" + _companionAt[0] + ", " + _companionAt[1] + "), Health " + ServerHooks.GetStat(Companion, "Health") +
                 ", abilities " + Abilities(Companion) + "; Puller " + ServerHooks.Describe(Puller) + " at (" + _pullerAt[0] + ", " + _pullerAt[1] + "), Health " +
                 ServerHooks.GetStat(Puller, "Health") + "/" + ServerHooks.GetStat(Puller, "MaxHealth") + ", abilities " + Abilities(Puller) + ", buffs " + Buffs(Puller));
        }

        /// <summary>The companion (role Tutorial.CompanionNpc, Team2), with the tutorial NPC type's own movement setup.</summary>
        private object SpawnCompanion(float x, float y, float dx, float dy)
        {
            object companion = _g.SpawnNpc(R.Name("Tutorial.CompanionNpc"), x, y, GameRuntime.PlayerTeam, new[] { dx, dy });
            // The tutorial NPC type's own movement setup (Tutorial.NpcBase.SetupMovement: walk/sprint acceleration,
            // friction, max speed). Nothing in the client calls it, so the server must; it is
            // also what Companion's sync sends. Then apply the walk values as ResetNPC does.
            R.Type("Tutorial.NpcBase").GetMethod(R.Name("Tutorial.NpcBase.SetupMovement"), All, null, Type.EmptyTypes, null).Invoke(companion, null);
            ServerHooks.SetField(companion, "_Friction", ServerHooks.GetFieldValue(companion, R.Name("Zombie.Base.Friction")));
            return companion;
        }

        /// <summary>
        /// Ours (owner, run 4): the client has no rule for the companion dying, so 5 s after she
        /// dies a new Companion stands up near the player (where the player stood a moment ago) and carries on as
        /// the companion. Before stage 8 she comes back at her map spawn for the rescue.
        /// </summary>
        private void ReviveCompanion(float time)
        {
            if (Companion == null || _g.TutorialStage >= 24) return;
            if (!Destroyed(Companion)) { _companionDiedAt = -1f; return; }
            if (_companionDiedAt < 0f) { _companionDiedAt = time; _log("tutorial: Companion died; she comes back near the player in " + CompanionReviveSeconds + " s"); return; }
            if (time - _companionDiedAt < CompanionReviveSeconds) return;
            _companionDiedAt = -1f;
            object old = Companion;
            ServerHooks.ForgetNpc(old);
            float[] at;
            if (_g.TutorialStage >= 8) { float[] a = ServerHooks.Position(_player); at = TrailPoint(time - 2f) ?? a; at = new[] { at[0], at[1], 0f, 1f }; }
            else at = _companionAt;
            Companion = SpawnCompanion(at[0], at[1], at[2], at[3]);
            if (_g.TutorialStage >= 8)
            {
                Companion.GetType().GetProperty("PetOwner", All).SetValue(Companion, _player, null);
                ServerHooks.SetField(Companion, "_Acceleration", ServerHooks.GetFieldValue(Companion, R.Name("Zombie.Base.Acceleration")));
            }
            else ServerHooks.SetField(Companion, "_Acceleration", 0f);
            _log("tutorial: Companion revived " + ServerHooks.Describe(Companion) + " at (" + at[0].ToString("0") + ", " + at[1].ToString("0") + "), " +
                 Distance(ServerHooks.Position(_player), at[0], at[1]).ToString("0") + " units from the player, Health " + ServerHooks.GetStat(Companion, "Health") +
                 (_g.TutorialStage >= 8 ? ", companion again" : ", at her rescue spot"));
        }

        /// <summary>The Puller hooks Companion; retried until Companion carries the hook buff, then stage 7.</summary>
        private void HookCompanion(float time)
        {
            if (Buffs(Companion).Contains(R.Short("Buff.PullerHook")))
            {
                _log("tutorial: Companion has the hook buff (" + Buffs(Companion) + ")");
                Puller.GetType().GetMethod("UnlockGoalTarget", All).Invoke(Puller, null);
                ServerHooks.Passive.Remove(Puller);
                ServerHooks.HoldPosition.Add(Puller);
                ServerHooks.AggroRange[Puller] = PullerStrikeRange;
                ServerHooks.StrikeRange[Puller] = PullerStrikeRange;
                SetStage(7, "the Puller hooked Companion");
                return;
            }
            // Keep the Puller facing Companion through the wind-up: left alone, an NPC turns
            // toward the nearest enemy (the player) and the hook flies at her instead.
            float[] pu = ServerHooks.Position(Puller), b = ServerHooks.Position(Companion);
            float dx = b[0] - pu[0], dy = b[1] - pu[1], d = Math.Max((float)Math.Sqrt(dx * dx + dy * dy), 0.001f);
            if (Puller.GetType().GetProperty("GoalTarget", All).GetValue(Puller, null) != Companion)
            {
                Puller.GetType().GetMethod("LockGoalTarget", All).Invoke(Puller, new[] { Companion });
                Puller.GetType().GetProperty("UseGoalTarget", All).SetValue(Puller, false, null);
            }
            _g.SetAim(Puller, dx / d, dy / d);
            if (time - _lastHookTry < HookRetrySeconds) return;
            _lastHookTry = time;
            object dir = _g.Vector2(dx / d, dy / d);
            object hook = Ability("PullerHook");
            bool cast = (bool)Puller.GetType().GetMethod("Attack", All, null, new[] { dir.GetType(), hook.GetType() }, null).Invoke(Puller, new[] { dir, hook });
            _log("tutorial: the Puller casts PullerHook at Companion (" + d.ToString("0") + " units)" + (cast ? "" : " (refused)"));
        }

        /// <summary>Companion follows the player at 30-60 units and attacks zombies within 80 units of her.</summary>
        private void CompanionAi(float time)
        {
            if (Destroyed(Companion)) return;
            float[] a = ServerHooks.Position(_player), b = ServerHooks.Position(Companion);
            // Stage 12 (transmission TutorialBarricade01: "keep the zombies off me while I break
            // down the barricade"): she works on the wooden barricade until the group breaks.
            if (_g.TutorialStage == 12)
            {
                object piece = Group(WoodenGroup).Where(x => !Destroyed(x)).OrderBy(x => { float[] q = ServerHooks.Position(x); return Distance(b, q[0], q[1]); }).FirstOrDefault();
                if (piece != null) { Strike(piece, time, "the barricade"); return; }
            }
            object target = null;
            float best = CompanionGuardRange;
            foreach (object z in Zombies.Concat(new[] { Puller }))
            {
                if (z == null || Destroyed(z)) continue;
                // Only awake zombies: ones still lying or sitting in their spawn pose are left alone.
                PropertyInfo pose = z.GetType().GetProperty("SpawnState_Current", All);
                if (pose != null && Convert.ToInt32(pose.GetValue(z, null)) != 0) continue;
                float[] zp = ServerHooks.Position(z);
                float dz = Distance(a, zp[0], zp[1]);
                if (dz <= best) { best = dz; target = z; }
            }
            if (target != null) { Strike(target, time, null); return; }
            float da = Distance(b, a[0], a[1]);
            if (da > CompanionFar) ServerHooks.Steer(Companion, _player);
            else if (da < CompanionNear) ServerHooks.StopSteering(Companion);
        }

        /// <summary>Companion walks to a live target and swings only when it is within reach, her aim on it.</summary>
        private void Strike(object target, float time, string what)
        {
            float[] a = ServerHooks.Position(_player), b = ServerHooks.Position(Companion), t = ServerHooks.Position(target);
            float dx = t[0] - b[0], dy = t[1] - b[1], d = Math.Max((float)Math.Sqrt(dx * dx + dy * dy), 0.001f);
            ServerHooks.Steer(Companion, target);
            _g.SetAim(Companion, dx / d, dy / d);
            object ability;
            // A barricade's collision keeps her about 17 units from its centre: allow a longer reach for it.
            float reach = what == "the barricade" ? BarricadeReach : CompanionAttackRange;
            if (d > reach || time - _companionLastAttack < CompanionAttackInterval || !ServerHooks.AttackAbility.TryGetValue(Companion, out ability)) return;
            _companionLastAttack = time;
            object dir = _g.Vector2(dx / d, dy / d);
            float before = ServerHooks.GetStat(target, "Health");
            bool accepted = (bool)Companion.GetType().GetMethod("Attack", All, null, new[] { dir.GetType(), ability.GetType() }, null).Invoke(Companion, new[] { dir, ability });
            float ax = a[0] - b[0], ay = a[1] - b[1], ad = Math.Max((float)Math.Sqrt(ax * ax + ay * ay), 0.001f);
            double offPlayer = Math.Acos(Math.Max(-1, Math.Min(1, dx / d * ax / ad + dy / d * ay / ad))) * 180 / Math.PI;
            _log("tutorial: Companion attacks " + (what ?? ServerHooks.Describe(target)) + " " + ServerHooks.Describe(target) + " at " + d.ToString("0") + " units with " + ability +
                 (accepted ? "" : " (refused)") + ", target Health " + before + "; " + offPlayer.ToString("0") + " deg off the player (" + ad.ToString("0") + " units away)");
        }

        /// <summary>The lighting generator at its trigger; its own class sets its hp and what can hurt it.</summary>
        private void SpawnGenerator()
        {
            if (_generatorAt == null) { _log("tutorial: the map lacks Trigger_TutorialLightingGenerator"); return; }
            Type t = R.Type("Generator");
            object gen = _g.TakeFromPool(t);
            object at = _g.Vector2(_generatorAt[0], _generatorAt[1]);
            ServerHooks.SetField(gen, "Position", at);
            ServerHooks.SetField(gen, "SpawnPosition", at);
            PropertyInfo team = gen.GetType().GetProperty("TeamId", All);
            team.SetValue(gen, Enum.Parse(team.PropertyType, "Neutral"), null);
            _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { gen, null, null });
            Generator = gen;
            _log("tutorial: generator " + ServerHooks.Describe(gen) + " at (" + _generatorAt[0] + ", " + _generatorAt[1] + "), Health " +
                 ServerHooks.GetStat(gen, "Health") + "/" + ServerHooks.GetStat(gen, "MaxHealth"));
        }

        /// <summary>The electric fence goes down: each piece made vulnerable (Barricade.MakeVulnerable) and set to 0 hp; it stays as a broken, walkable barricade.</summary>
        private void FenceOff()
        {
            foreach (object piece in Group(ElectricGroup))
            {
                piece.GetType().GetMethod(R.Name("Barricade.MakeVulnerable"), All).Invoke(piece, null);
                _g.SetStat(piece, "Health", 0f);
            }
            _log("tutorial: electric fence off: " + string.Join(", ", Group(ElectricGroup).Select(x => ServerHooks.Describe(x) + " Health " + ServerHooks.GetStat(x, "Health") +
                 " collision " + x.GetType().GetProperty("HasStaticCollision", All).GetValue(x, null)).ToArray()));
        }

        /// <summary>
        /// Ambush 10103 during stage 12: keep up to MaxActiveWalkers of its walkers alive,
        /// one every RespawnRateMin..Max s at a random one of its points, in that point's pose.
        /// The AI aggroes them on the player and clears the pose (WakeUp). No specials: MaxActiveSpecials is 0.
        /// </summary>
        private void Ambush(float time)
        {
            if (AmbushPoints.Count == 0 || AmbushMaxWalkers <= 0) return;
            if (_ambushSecond < 0f) _ambushSecond = time;
            if (time - _ambushSecond >= 1f)
            {
                if (_ambushThisSecond > 0)
                    _log("tutorial: ambush: " + _ambushThisSecond + " spawned in the last second, " + AmbushZombies.Count(z => !Destroyed(z)) + " alive");
                _ambushThisSecond = 0;
                _ambushSecond = time;
            }
            if (_nextAmbushSpawn < 0f) _nextAmbushSpawn = time;
            if (time < _nextAmbushSpawn) return;
            AmbushZombies.RemoveAll(Destroyed);
            if (AmbushZombies.Count >= AmbushMaxWalkers) return;
            AmbushSpot pt = AmbushPoints[_random.Next(AmbushPoints.Count)];
            try
            {
                object z = _g.SpawnNpc(GameRuntime.PlainZombieType, pt.X, pt.Y, GameRuntime.ZombieTeam, new[] { pt.DX, pt.DY });
                if (pt.State != null)
                {
                    PropertyInfo ss = z.GetType().GetProperty("SpawnState_Current", All);
                    ss.SetValue(z, Enum.ToObject(ss.PropertyType, Convert.ToInt32(pt.State)), null);
                }
                AmbushZombies.Add(z);
                Zombies.Add(z);
                AmbushSpawned++;
                _ambushThisSecond++;
            }
            catch (Exception e) { _log("tutorial: ambush spawn failed: " + GameRuntime.Unwrap(e).Message); }
            _nextAmbushSpawn = time + _ambushRateMin + (float)_random.NextDouble() * (_ambushRateMax - _ambushRateMin);
        }

        /// <summary>BANDAGE hint: shown the first time the player is below half health in stage 12, done when she casts a bandage.</summary>
        private void BandageHint()
        {
            int hint = _g.GetTutorialField(HintBandage);
            if (hint == 0 && ServerHooks.GetStat(_player, "Health") < 0.5f * ServerHooks.GetStat(_player, "MaxHealth"))
            {
                _g.SetTutorialField(HintBandage, 1);
                _log("tutorial: BANDAGE hint shown (the player at " + ServerHooks.GetStat(_player, "Health") + "/" + ServerHooks.GetStat(_player, "MaxHealth") + ")");
            }
            else if (hint == 1)
            {
                object bar = _player.GetType().GetProperty("AbilityBar", All).GetValue(_player, null);
                object casting = bar == null ? null : bar.GetType().GetProperty("CastingAbility", All).GetValue(bar, null);
                object id = casting == null ? null : casting.GetType().GetProperty("AbilityIdentifier", All).GetValue(casting, null);
                if (id != null && id.ToString().Contains("Bandage"))
                {
                    _g.SetTutorialField(HintBandage, 2);
                    _log("tutorial: BANDAGE hint done (" + id + ")");
                }
            }
        }

        // ---- stages 14-24 ----

        /// <summary>The storage room entity for Event_StorageRoom 10038; it links itself to the game mode's StorageRoomPoint by ID (StorageRoom.Link).</summary>
        private void SpawnStorageRoom()
        {
            Type t = _g.Game("ConductorGameLogic.Entities.GameModeObjects.StorageRoom");
            object room = _g.TakeFromPool(t);
            t.GetField("ID", All).SetValue(room, (ushort)StorageId);
            _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { room, null, null });
            if (t.GetField("StorageRoomPoint", All).GetValue(room) == null) t.GetMethod(R.Name("StorageRoom.Link"), All).Invoke(room, null);
            StorageRoom = room;
            ServerHooks.PressNote = () =>
            {
                float[] r = ServerHooks.Vector(room.GetType().GetProperty("Position", All).GetValue(room, null)), a = ServerHooks.Position(_player);
                return "storage room " + Distance(a, r[0], r[1]).ToString("0.0") + " units away";
            };
            object point = t.GetField("StorageRoomPoint", All).GetValue(room);
            _log("tutorial: storage room " + ServerHooks.Describe(room) + " ID " + StorageId + ", linked to its point " + (point != null) +
                 (point == null ? "" : " at (" + string.Join(", ", ServerHooks.Vector(GetField(point, R.Name("MapPoint.Position"))).Select(v => v.ToString("0")).ToArray()) + ")"));
        }

        private object _captureBuff;

        /// <summary>
        /// The capture channel (CaptureFlag, slot 19) applies buff Buff.CaptureChannel, which calls
        /// GameMode.TriggerCapturePerformed once its age reaches Buff.CaptureChannel.Needed (5 s). The buff starts a
        /// few frames after the channel, and the channel ends at buff age ~4.9 s on our 30 Hz
        /// tick, removing the buff just before it fires. When a capture buff ends that close
        /// to Buff.CaptureChannel.Needed, still within its range (Buff.CaptureChannel.Range) of the room, we call the handler it would have.
        /// </summary>
        private void WatchCapture()
        {
            if (_captureBuff == null)
            {
                IEnumerable list = (IEnumerable)_player.GetType().GetProperty("BuffList", All).GetValue(_player, null);
                _captureBuff = list == null ? null : list.Cast<object>().FirstOrDefault(b => b != null && b.GetType().Name == R.Short("Buff.CaptureChannel"));
                if (_captureBuff != null) _log("tutorial: the player started the capture channel");
                return;
            }
            if ((bool)_captureBuff.GetType().GetProperty("IsActive").GetValue(_captureBuff, null)) return;
            float age = (float)_captureBuff.GetType().GetProperty("Age").GetValue(_captureBuff, null);
            float needed = (float)GetField(_captureBuff, R.Name("Buff.CaptureChannel.Needed")), range = (float)GetField(_captureBuff, R.Name("Buff.CaptureChannel.Range"));
            object buff = _captureBuff;
            _captureBuff = null;
            float[] a = ServerHooks.Position(_player), r = ServerHooks.Vector(StorageRoom.GetType().GetProperty("Position", All).GetValue(StorageRoom, null));
            bool close = Distance(a, r[0], r[1]) <= range;
            if (age >= needed - 0.25f && close && !GetField(StorageRoom, "PerformedTriggerCapture").Equals(PlayerTeamId()))
            {
                _g.GameMode.GetType().GetMethod("TriggerCapturePerformed", All).Invoke(_g.GameMode, new[] { _player });
                _log("tutorial: capture channel ended at age " + age.ToString("0.00") + " of " + needed + " s; completed the capture (tick timing)");
            }
            else _log("tutorial: capture channel ended at age " + age.ToString("0.00") + " of " + needed + " s, " + (close ? "in range" : "out of range") + "; no capture");
        }

        // ---- delivery (stage 21) ----

        /// <summary>The game mode's delivery point (TutorialSupplyPoint, from TruckerSpawnPoint): its position and radius.</summary>
        private object DeliveryPoint()
        {
            IEnumerable list = (IEnumerable)_g.GameMode.GetType().GetProperty("StaticMapObjects", All).GetValue(_g.GameMode, null);
            return list == null ? null : list.Cast<object>().FirstOrDefault(o => o != null && o.GetType().Name == R.Short("TutorialSupplyPoint"));
        }

        private bool InDeliveryCircle(float[] at, out float d, out float radius)
        {
            object point = DeliveryPoint();
            d = 9999f; radius = 0f;
            if (point == null) return false;
            float[] c = ServerHooks.Vector(GetField(point, R.Name("MapPoint.Position")));
            radius = Math.Max((float)GetField(point, R.Name("TutorialSupplyPoint.Radius")), 20f);
            d = Distance(at, c[0], c[1]);
            return d <= radius;
        }

        private readonly HashSet<object> _seenThrows = new HashSet<object>();

        /// <summary>Fallback (ours): supplies thrown (ThrowSupplies, SupplyThrow) that land in the delivery circle count as delivered.</summary>
        private void WatchThrownSupplies()
        {
            foreach (object o in _g.ActiveSynchronizables(new HashSet<object>()))
            {
                if (o == null || o.GetType().Name != R.Short("SupplyThrow") || _seenThrows.Contains(o)) continue;
                float[] landing = ServerHooks.Vector(GetField(o, R.Name("SupplyThrow.Landing")));
                int amount = Convert.ToInt32(GetField(o, R.Name("SupplyThrow.Amount")));
                float d, radius;
                if (amount <= 0 || !InDeliveryCircle(landing, out d, out radius)) continue;
                _seenThrows.Add(o);
                object supplies = Supplies();
                MethodInfo credit = supplies.GetType().GetMethod(R.Name("Supplies.AddDelivered"), All);
                credit.Invoke(supplies, new[] { Enum.Parse(credit.GetParameters()[0].ParameterType, GameRuntime.PlayerTeam), (object)amount, Activator.CreateInstance(credit.GetParameters()[2].ParameterType) });
                _log("tutorial: " + amount + " supplies thrown into the delivery circle (" + d.ToString("0.0") + " units); counted as delivered (team now " + TeamSupplies() + ")");
            }
        }

        /// <summary>Stage 15: walkers from the storage room's Spawn_EventEntities (ID 10038) attack while the player holds it.</summary>
        private void StorageDefense(float time)
        {
            if (StorageSpots.Count == 0) return;
            if (_nextStorageSpawn < 0f) _nextStorageSpawn = time;
            if (time < _nextStorageSpawn) return;
            StorageZombies.RemoveAll(Destroyed);
            if (StorageZombies.Count < StorageMaxWalkers)
            {
                AmbushSpot pt = StorageSpots[_random.Next(StorageSpots.Count)];
                try
                {
                    object z = _g.SpawnNpc(GameRuntime.PlainZombieType, pt.X, pt.Y, GameRuntime.ZombieTeam, new[] { pt.DX, pt.DY });
                    if (pt.State != null)
                    {
                        PropertyInfo ss = z.GetType().GetProperty("SpawnState_Current", All);
                        ss.SetValue(z, Enum.ToObject(ss.PropertyType, Convert.ToInt32(pt.State)), null);
                    }
                    StorageZombies.Add(z);
                    Zombies.Add(z);
                    _log("tutorial: storage room wave: " + StorageZombies.Count + " alive");
                }
                catch (Exception e) { _log("tutorial: storage spawn failed: " + GameRuntime.Unwrap(e).Message); }
            }
            _nextStorageSpawn = time + StorageRespawnMin + (float)_random.NextDouble() * (StorageRespawnMax - StorageRespawnMin);
        }

        /// <summary>
        /// Stage 16: supply pickups (SupplyPickup, the ground pickup the game's own drop uses:
        /// position and amount fields) around the storage room. The pickup base collects itself
        /// when a player of a matching team walks over it (in its own UpdateGameObject).
        /// </summary>
        private void DropSupplies()
        {
            float[] at = _storageAt ?? StoragePoint;
            // The player must be registered with the supplies system (Supplies.Register) or the
            // pickups find no slot for her (Supplies.Carried returns -1) and give nothing.
            object supplies = Supplies();
            Array slots = (Array)GetField(supplies, R.Name("Supplies.Slots"));
            ushort me = (ushort)_player.GetType().GetProperty("IndexPlayer", All).GetValue(_player, null);
            bool registered = slots != null && slots.Cast<object>().Any(x => x != null && (int)GetField(x, R.Name("SupplySlot.Player")) == me);
            if (!registered) supplies.GetType().GetMethod(R.Name("Supplies.Register"), All).Invoke(supplies, new[] { _player });
            slots = (Array)GetField(supplies, R.Name("Supplies.Slots"));
            _log("tutorial: supplies initialised " + GetField(supplies, R.Name("Supplies.Initialised")) + ", the player (player " + me + ") registered before " + registered + ", slots " +
                 (slots == null ? "null" : string.Join(",", slots.Cast<object>().Select(x => x == null ? "-" : GetField(x, R.Name("SupplySlot.Player")) + ":" + GetField(x, R.Name("SupplySlot.Amount"))).ToArray())));
            object balance = _g.GameManagerType.GetField("BalanceData", All).GetValue(null);
            _log("tutorial: supplies: the player carries " + CarriedSupplies() + ", capacity " + GetField(GetField(balance, R.Name("AbilityManager.Balance")), R.Name("Balance.CarryCapacity")));
            Type t = R.Type("SupplyPickup");
            int count = SupplyTotal / SupplyPerPickup;
            object last = null;
            for (int i = 0; i < count; i++)
            {
                object pickup = _g.TakeFromPool(t);
                double angle = 2 * Math.PI * i / count;
                ServerHooks.SetField(pickup, R.Name("Pickup.Position"), _g.Vector2(at[0] + (float)Math.Cos(angle) * SupplyDropRadius, at[1] + (float)Math.Sin(angle) * SupplyDropRadius));
                ServerHooks.SetField(pickup, R.Name("SupplyPickup.Amount"), (byte)SupplyPerPickup);
                _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { pickup, null, null });
                last = pickup;
                Pickups.Add(pickup);
            }
            _log("tutorial: dropped " + count + " supply pickups of " + SupplyPerPickup + " around the storage room (ImpSupplies " +
                 _g.GameMode.GetType().GetProperty("ImpSupplies", All).GetValue(_g.GameMode, null) + ", pickup radius " + (last == null ? "?" : GetField(last, R.Name("Pickup.Radius")) + ", team " + GetField(last, R.Name("Pickup.Team")) + ", active " + last.GetType().GetProperty("IsActive", All).GetValue(last, null)) + ")");
        }

        private object Supplies() { return _g.GameMode.GetType().GetProperty("SupplyData", All).GetValue(_g.GameMode, null); }

        public int CarriedSupplies()
        {
            object supplies = Supplies();
            ushort index = (ushort)_player.GetType().GetProperty("IndexPlayer", All).GetValue(_player, null);
            return (int)supplies.GetType().GetMethod(R.Name("Supplies.Carried"), All).Invoke(supplies, new object[] { (int)index });
        }

        public int TeamSupplies()
        {
            object supplies = Supplies();
            MethodInfo m = supplies.GetType().GetMethod(R.Name("Supplies.Delivered"), All);   // delivered so far
            return (int)m.Invoke(supplies, new[] { Enum.Parse(m.GetParameters()[0].ParameterType, GameRuntime.PlayerTeam) });
        }

        /// <summary>Stage 18: the two rival hero NPCs (roles Tutorial.RivalNpcA and RivalNpcB) on Team1 at the ENEMY marker; the AI sends them at the player.</summary>
        private void SpawnEnemies()
        {
            if (_enemyAt == null) { _log("tutorial: the map lacks the ENEMY spawn"); return; }
            RivalA = SpawnTutorialNpc(R.Name("Tutorial.RivalNpcA"), _enemyAt[0] - 15f, _enemyAt[1], "Team1");
            RivalB = SpawnTutorialNpc(R.Name("Tutorial.RivalNpcB"), _enemyAt[0] + 15f, _enemyAt[1], "Team1");
            // Every hero hit staggered the player: at the default 1 s each, the two of them kept the
            // player stun-locked until death (17:09:59-17:10:40). Slower, alternating strikes (ours),
            // and since run 4 their hits carry no stun or knockback at all (owner's call).
            foreach (object hero in new[] { RivalA, RivalB })
            {
                ServerHooks.StrikeInterval[hero] = HeroStrikeInterval;
                ServerHooks.StrikeRange[hero] = 10f;
            }
            ServerHooks.LastAttack[RivalB] = _time + HeroStrikeInterval / 2f - HeroStrikeInterval;
            ServerHooks.BlockControl = (source, target) => target == _player && (source == RivalA || source == RivalB);
            _log("tutorial: the rivals' stun-type effects and knockback on the player are blocked");
            _log("tutorial: RivalA " + ServerHooks.Describe(RivalA) + " (" + Abilities(RivalA) + ", Health " + ServerHooks.GetStat(RivalA, "Health") + ") and RivalB " +
                 ServerHooks.Describe(RivalB) + " (" + Abilities(RivalB) + ", Health " + ServerHooks.GetStat(RivalB, "Health") + ") at the ENEMY marker on Team1");
        }

        /// <summary>A tutorial hero NPC (Tutorial.NpcBase) with its own movement setup, as for the companion.</summary>
        private object SpawnTutorialNpc(string type, float x, float y, string team)
        {
            object npc = _g.SpawnNpc(type, x, y, team, _enemyAt == null ? null : new[] { _enemyAt[2], _enemyAt[3] });
            R.Type("Tutorial.NpcBase").GetMethod(R.Name("Tutorial.NpcBase.SetupMovement"), All, null, Type.EmptyTypes, null).Invoke(npc, null);
            ServerHooks.SetField(npc, "_Acceleration", ServerHooks.GetFieldValue(npc, R.Name("Zombie.Base.Acceleration")));
            ServerHooks.SetField(npc, "_Friction", ServerHooks.GetFieldValue(npc, R.Name("Zombie.Base.Friction")));
            return npc;
        }

        /// <summary>Stage 22: the Floater from the map's Floater Event_Assault, on Team3.</summary>
        private void SpawnFloater()
        {
            float[] at = _floaterAt ?? new[] { TruckPoint[0] - 50f, TruckPoint[1] + 80f };
            Floater = _g.SpawnNpc(R.Name("Zombie.Floater"), at[0], at[1], GameRuntime.ZombieTeam, null);
            _log("tutorial: Floater " + ServerHooks.Describe(Floater) + " at (" + at[0].ToString("0") + ", " + at[1].ToString("0") + "), Health " + ServerHooks.GetStat(Floater, "Health") + ", abilities " + Abilities(Floater));
        }

        /// <summary>The two truckers (NPC_TRUCK markers), Team2, standing by the truck.</summary>
        private void SpawnTruckers()
        {
            foreach (float[] at in _truckerAt)
            {
                try
                {
                    // The trucker is a plain game object (Position, Direction), not a character.
                    object t = _g.TakeFromPool(_g.Game("ConductorGameLogic.Entities.Characters.Trucker"));
                    ServerHooks.SetField(t, "Position", _g.Vector2(at[0], at[1]));
                    ServerHooks.SetField(t, "Direction", _g.Vector2(at[2], at[3]));
                    PropertyInfo team = t.GetType().GetProperty("TeamId", All);
                    team.SetValue(t, Enum.Parse(team.PropertyType, GameRuntime.PlayerTeam), null);
                    _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { t, null, null });
                    Truckers.Add(t);
                }
                catch (Exception e) { _log("tutorial: trucker spawn failed: " + GameRuntime.Unwrap(e).ToString().Split((char)10).Take(4).Aggregate((x, y) => x + " | " + y)); }
            }
            if (Truckers.Count > 0) _log("tutorial: " + Truckers.Count + " truckers at the truck");
        }

        private static bool Gone(object o) { return o != null && Destroyed(o); }

        private void SetField(object o, string name, string enumValue)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(o, Enum.Parse(f.FieldType, enumValue)); return; }
            }
        }

        private object Ability(string name) { return Enum.Parse(_g.Game("ConductorGameLogic.Abilities.AbilityIdentifier"), name); }

        public static string Abilities(object npc)
        {
            object bar = npc.GetType().GetProperty("AbilityBar", All).GetValue(npc, null);
            Array slots = bar == null ? null : (Array)bar.GetType().GetProperty("AbilitySlots", All).GetValue(bar, null);
            return slots == null ? "-" : string.Join(", ", slots.Cast<object>().Where(x => x != null)
                .Select(x => x.GetType().GetProperty("AbilityIdentifier", All).GetValue(x, null).ToString()).ToArray());
        }

        public static string Buffs(object character)
        {
            IEnumerable list = (IEnumerable)character.GetType().GetProperty("BuffList", All).GetValue(character, null);
            return list == null ? "" : string.Join(", ", list.Cast<object>().Where(x => x != null).Select(x => x.GetType().Name).ToArray());
        }

        private void SetStage(int stage, string why)
        {
            int from = _g.TutorialStage;
            _g.TutorialStage = stage;
            _stageSince = _time;
            if (stage == 4) _g.SetTutorialField(HintAttack, 1);
            _log("tutorial stage " + from + " -> " + stage + " (" + why + ")");
            if (stage == 6)
            {
                _g.SetTutorialField(HintAbility, 2);
                SpawnRescue();
            }
            else if (stage == 7)
            {
                _g.SetTutorialField(HintAbility, 3);
                _g.SetTutorialField(HintDash, 1);
            }
            else if (stage == 8)
            {
                _g.SetTutorialField(HintDash, 2);
                GiveShotgun();
                Companion.GetType().GetProperty("PetOwner", All).SetValue(Companion, _player, null);
                ServerHooks.SetField(Companion, "_Acceleration", ServerHooks.GetFieldValue(Companion, R.Name("Zombie.Base.Acceleration")));
                _log("tutorial: Companion is the player's companion");
            }
            else if (stage == 9)
            {
                // The client refuses weapon swaps in the tutorial until the game mode's TutorialMode.SwapWeaponsAllowed is
                // set (GameManagerClient.SendSwapWeapon); only the server sets it, through the
                // game mode sync. Stage 9 is where the client shows the SwitchWeapon hint.
                _g.SetTutorialFlag("TutorialMode.SwapWeaponsAllowed", true);
                _log("tutorial: weapon swapping allowed");
                SpawnGenerator();
            }
            else if (stage == 10)
                FenceOff();
            else if (stage == 14)
                SpawnStorageRoom();
            else if (stage == 15)
            {
                _nextStorageSpawn = -1f;
                // The client offers TriggerCapture only while the room's DenyRoomHolder is not the
                // player's team (StorageRoomPoint); the room is synced, so this ends the X prompt.
                object team = PlayerTeamId();
                ServerHooks.SetField(StorageRoom, "DenyRoomHolder", team);
                _log("tutorial: storage room captured; DenyRoomHolder = " + team + " (the player's team), so X no longer offers the capture");
            }
            else if (stage == 16)
            {
                DropSupplies();
            }
            else if (stage == 18)
                SpawnEnemies();
            else if (stage == 22)
                SpawnFloater();
            else if (stage == 24)
            {
                // The client's UnityClient.DrawConnectionStatus resets the black screen fader's
                // alpha every frame unless GameMode.IsCompleted is set (it is synced in
                // GameMode.Serialize). With it unset, the stage-24 fade to black never shows and
                // the tutorial mode's quit (fader alpha 1, then 5 s, ShutDownApplication) never runs.
                _g.GameMode.GetType().GetProperty("IsCompleted", All).SetValue(_g.GameMode, true, null);
                _log("tutorial: game mode IsCompleted = true, so the client's stage-24 fade to black and quit can run");
                if (OnCompleted != null) OnCompleted();
            }
            else if (stage == 13)
            {
                if (_g.GetTutorialField(HintBandage) != 2) _g.SetTutorialField(HintBandage, 2);
                _log("tutorial: ambush stops spawning (" + AmbushSpawned + " spawned in all, " + AmbushZombies.Count(z => !Destroyed(z)) + " still alive)");
            }
        }

        private void SpawnPaddle()
        {
            if (_paddleAt == null) { _log("tutorial: no PADDLE spawn in the map"); return; }
            Type t = R.Type("WeaponPickup");
            object paddle = _g.TakeFromPool(t);
            // Pickups are not entities: their position is the pickup base's own field #B
            // (the pickup base's position, radius and team fields; from the client's types).
            ServerHooks.SetField(paddle, R.Name("Pickup.Position"), _g.Vector2(_paddleAt[0], _paddleAt[1]));
            _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { paddle, null, null });
            _paddle = paddle;
            _log("tutorial: paddle pickup " + ServerHooks.Describe(paddle) + " at (" + _paddleAt[0] + ", " + _paddleAt[1] + "), radius " +
                 GetField(paddle, R.Name("Pickup.Radius")) + ", team " + GetField(paddle, R.Name("Pickup.Team")) + ", synchronizable " + _g.SynchronizableIndex(paddle));
        }

        private void PickUpPaddle()
        {
            GivePaddle();
            _log("tutorial: the player picked up the paddle");
            SetStage(4, "paddle picked up");
        }

        /// <summary>Gives the paddle as the game does (EquipHaxWeaponNew slot 0, schematic 1005) and tells the client.</summary>
        private void GivePaddle()
        {
            _player.GetType().GetField("ReceivedTutorialPaddle", All).SetValue(_player, true);
            _player.GetType().GetMethod("EquipHaxWeaponNew", All).Invoke(_player, new object[] { 0, PaddleSchematic, true });
            SendClientInfo();
        }

        /// <summary>The shotgun in slot 1, not equipped (as the original server gave it).</summary>
        private void GiveShotgun()
        {
            _player.GetType().GetField("ReceivedTutorialShotgun", All).SetValue(_player, true);
            _player.GetType().GetMethod("EquipHaxWeaponNew", All).Invoke(_player, new object[] { 1, ShotgunSchematic, false });
            _log("tutorial: the player received the shotgun");
            SendClientInfo();
        }

        /// <summary>The ClientInfo game message (01, 00, 07, 00, then the client info), reliable.</summary>
        private void SendClientInfo()
        {
            if (ServerHooks.QueueReliable == null || ServerHooks.Client0 == null) return;
            GameBuffer m = GameBuffer.Create();
            m.Write((byte)1);
            m.Write((byte)0);
            m.Write((byte)7);
            m.Write((byte)0);
            ServerHooks.Client0.GetType().GetMethod("ClientInfoServerToClientSerialize", All).Invoke(ServerHooks.Client0, new[] { m.Buffer, (object)true });
            ServerHooks.QueueReliable(m.Buffer);
            object client = _player.GetType().GetProperty("Client", All).GetValue(_player, null);
            object cid = ServerHooks.GetFieldValue(ServerHooks.Client0, "ClientInfoData");
            Func<string, string> uq = f => { object u = ServerHooks.GetFieldValue(cid, f); return ServerHooks.GetFieldValue(u, "SchematicID") + (ServerHooks.GetFieldValue(u, "Guid") == null ? "" : " (guid)"); };
            _log("tutorial: sent ClientInfo (" + m.ToBytes().Length + " bytes, player client is Client0: " + ReferenceEquals(client, ServerHooks.Client0) + ", melee " + uq(R.Name("ClientInfo.Melee")) + ", ranged " + uq(R.Name("ClientInfo.Ranged")) + ", changed " + ServerHooks.GetFieldValue(cid, R.Name("ClientInfo.Gadget3")) + "): " + BitConverter.ToString(m.ToBytes()));
        }

        // ---- respawn ----

        /// <summary>
        /// The game mode's respawn is empty in this build: respawn the player 5 s after she dies, where
        /// she stood RespawnLookback s before dying (near the fight, owner run 4); the level's spawn
        /// point only if there is no trail.
        /// </summary>
        private void Respawn(float time)
        {
            bool dead = (bool)_player.GetType().GetProperty("IsDead", All).GetValue(_player, null);
            if (!dead)
            {
                _diedAt = -1f;
                if (time - _lastSample >= 0.5f)
                {
                    float[] p = ServerHooks.Position(_player);
                    _trail.Add(new[] { time, p[0], p[1] });
                    _lastSample = time;
                    while (_trail.Count > 0 && _trail[0][0] < time - 30f) _trail.RemoveAt(0);
                }
                return;
            }
            if (_diedAt < 0f) { _diedAt = time; _log("tutorial: the player died; respawning in " + RespawnSeconds + " s"); return; }
            if (time - _diedAt < RespawnSeconds) return;
            float[] back = TrailPoint(_diedAt - RespawnLookback);
            object at = back == null ? _g.Vector2(_spawn.X, _spawn.Y) : _g.Vector2(back[0], back[1]);
            _player.GetType().GetField("SpawnPosition", All).SetValue(_player, at);
            // Teleport, not a bare Position write: collision would pull her back to her last free position.
            _g.Game("ConductorGameLogic.Entities.Entity").GetMethod("Teleport", All).Invoke(_player, new object[] { at, true });
            _player.GetType().GetMethod("Respawn", All, null, Type.EmptyTypes, null).Invoke(_player, null);
            _diedAt = -1f;
            _log("tutorial: the player respawned " + (back == null ? "at the spawn point" : "at (" + back[0].ToString("0") + ", " + back[1].ToString("0") + "), where she was " + RespawnLookback + " s before dying") + ", Health " + ServerHooks.GetStat(_player, "Health") + "/" + ServerHooks.GetStat(_player, "MaxHealth") +
                 ", dead " + _player.GetType().GetProperty("IsDead", All).GetValue(_player, null));
            if ((bool)_player.GetType().GetField("ReceivedTutorialPaddle", All).GetValue(_player)) GivePaddle();
            if ((bool)_player.GetType().GetField("ReceivedTutorialShotgun", All).GetValue(_player)) GiveShotgun();
        }

        // ---- helpers ----

        /// <summary>the player's sampled position at or just after the given time (the oldest one if earlier); null with no samples.</summary>
        private float[] TrailPoint(float when)
        {
            if (_trail.Count == 0) return null;
            float[] p = _trail.FirstOrDefault(q => q[0] >= when) ?? _trail[_trail.Count - 1];
            return new[] { p[1], p[2] };
        }

        private object PlayerTeamId()
        {
            return _player.GetType().GetProperty("TeamId", All).GetValue(_player, null);
        }

        private static object GetField(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static bool Destroyed(object o)
        {
            return (bool)o.GetType().GetProperty("IsDead", All).GetValue(o, null) || !(bool)o.GetType().GetProperty("IsActive", All).GetValue(o, null);
        }

        private static float Distance(float[] a, float x, float y)
        {
            float dx = a[0] - x, dy = a[1] - y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

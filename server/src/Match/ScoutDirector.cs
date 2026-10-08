using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Practice (the hub's solo Scout missions, game mode ScoutMission on maps 15/16/17). The
    /// client's ScoutMission hooks are empty, so the whole mission script is ours (owner's design,
    /// 2026-10-06); what the client shows comes from its own data:
    /// - Goals: ScoutMission.LoadGameMode sets the supplies goal per map (balance data: Outpost 60, Lab 30, Club 30).
    /// - Objectives: RaidEventViewSettings (sharedassets1) has, for the scout events 17 ScoutCaptureFlag,
    ///   18 ScoutKillLooter, 19 ScoutStealSupplies and 20 ScoutReturnToTruck, the objective types
    ///   MoveTo (1) and Objective1 (2) only; the VisualID is the option index (return: 0 Club, 1 Lab, 2 Outpost).
    /// - Interactions: steal is an interaction point of type GrapSupplies (0), the flag one of type Flag (3);
    ///   the client asks with GameMessage 29 (StartInteraction: ushort id, ushort second value).
    /// Numbers marked "stand-in" have no source.
    /// </summary>
    public sealed class ScoutDirector
    {
        private const BindingFlags All = GameRuntime.All;
        public const int StartInteractionMessage = 29, ObjectiveUpdateMessage = 30, TeamFinishedMessage = 4;
        public const int StealSupplies = 19, KillLooter = 18, CaptureFlag = 17, ReturnToTruck = 20;
        public const int MoveTo = 1, Objective1 = 2;
        public const float NearEvent = 120f, NearTruck = 80f, TransmissionRange = 60f, InteractRange = 30f;
        /// <summary>Stand-in: how long the steal and the flag capture take.</summary>
        public const float InteractSeconds = 5f;
        public const float RespawnSeconds = 5f, StaticAggroRange = 150f, AmbushRange = 200f, AmbushSeconds = 30f;
        public static string LooterType { get { return R.Name("Zombie.Looter"); } }
        public const int SupplyPerPickup = 5;
        /// <summary>
        /// Stand-in barricade health (the original is lost; the client has no default). The game shows a
        /// barricade as broken once Health drops below 80% (balance data) but only removes its collision at
        /// Health 1 or less, so a low pool keeps the two close (owner, Lab playtest: 100 felt wrong).
        /// </summary>
        public const float BarricadeHp = 30f;
        /// <summary>Supplies the server dropped land within this many units of the spot (pickups collect within 4).</summary>
        public const float DropRadius = 8f;
        /// <summary>Stand-in safety net: supplies the server dropped that stay uncollected this long make it drop the shortfall at the truck.</summary>
        public const float UncollectedSeconds = 20f;
        /// <summary>Transmission points of the event within this distance of its target play only after the event is done (ours).</summary>
        public const float EventAreaRange = 250f;
        public static float MinStepSeconds = 1f;   // the offline probe sets 0

        private readonly GameRuntime _g;
        private readonly object _player;
        private readonly SpawnPoint _spawn;
        private readonly Action<string> _log;
        private readonly Random _random = new Random();
        public readonly int Map, Event, ReturnVisual, Goal;
        public string Step = "start";
        public float[] EventAt, TruckAt;
        public object Looter;
        public readonly List<object> Zombies = new List<object>();
        private readonly List<float[]> _respawns = new List<float[]>();
        private readonly List<object> _transmissions = new List<object>();
        private readonly HashSet<object> _played = new HashSet<object>();
        private readonly List<float[]> _eventSpots = new List<float[]>();   // Spawn_EventEntities: x, y, dx, dy, state
        private float[] _ambushAt, _assaultAt;
        private int _ambushMax;
        private float _assaultRadius, _ambushUntil = -1f, _nextAmbush;
        private float[] _assaultTrigger;
        public float[] AssaultAt { get { return _assaultAt; } }
        private readonly List<string> _assaultRoster = new List<string>();
        private readonly List<float[]> _triggerPositions = new List<float[]>();
        /// <summary>Plain zombies placed at map start (stand-in); the rest of the pool is kept for the ambush and assault.</summary>
        public const int PopulationCap = 55;
        public static string ButcherType { get { return R.Name("Zombie.Butcher"); } }
        public static string FloaterType { get { return R.Name("Zombie.Floater"); } }
        public static string WalkingBombType { get { return R.Name("Zombie.WalkingBomb"); } }
        /// <summary>
        /// Veterans (Zombie.Veteran): the old "Elite Walkers", renamed in v0.6. Patch v0.5.2 notes elites in the Scout
        /// missions; the reworked practice maps in this client cache no elites but 6 Veterans (pool probe), so
        /// they stand in (owner's choice, 2026-10-07). Stand-in counts: 3 with each assault, every 4th ambush walker.
        /// </summary>
        public static string VeteranType { get { return R.Name("Zombie.Veteran"); } }
        public const int AssaultVeterans = 3, AmbushVeteranEvery = 4;
        private int _ambushSpawns;
        /// <summary>An Event_TriggerPosition within this distance of an assault starts it too (ours: the map doesn't link them by ID).</summary>
        public const float AssaultTriggerPairRange = 150f;
        private bool _ambushDone, _assaultDone;
        private float _time, _stepSince, _interactStart = -1f, _diedAt = -1f;
        private float[] _diedPos;
        private bool _won;
        public Action OnWon;

        public ScoutDirector(GameRuntime g, object player, SpawnPoint spawn, Action<string> log)
        {
            _g = g;
            _player = player;
            _spawn = spawn;
            _log = log;
            Map = g.MapIndex;
            Event = Map == GameRuntime.ScoutOutpostMap ? StealSupplies : Map == GameRuntime.ScoutLabMap ? KillLooter : CaptureFlag;
            ReturnVisual = Map == GameRuntime.ScoutClubMap ? 0 : Map == GameRuntime.ScoutLabMap ? 1 : 2;
            object settings = Supplies().GetType().GetProperty("Settings", All).GetValue(Supplies(), null);
            Goal = Convert.ToInt32(GetField(settings, R.Name("ScoutSettings.Goal")));
        }

        // ---- world ----

        public void PopulateWorld()
        {
            int statics = 0, barricades = 0;
            var groups = new Dictionary<int, List<object>>();
            foreach (object o in _g.MapObjects())
            {
                string kind = o.GetType().Name;
                float[] p = _g.MapPosition(o), d = _g.MapDirection(o);
                if (kind == "Spawn_Static")
                {
                    object z = SpawnZombie(GameRuntime.PlainZombieType, p, d, o.GetType().GetProperty("SpawnState", All).GetValue(o, null));
                    if (z != null) { ServerHooks.AggroRange[z] = StaticAggroRange; statics++; }
                }
                else if (kind == "Spawn_EventEntities")
                    _eventSpots.Add(new[] { p[0], p[1], d[0], d[1], Convert.ToSingle(Convert.ToInt32(o.GetType().GetProperty("SpawnState", All).GetValue(o, null))) });
                else if (kind == "Spawn_Destructible")
                {
                    object b = SpawnBarricade(o, p, d);
                    int group = (int)o.GetType().GetField("GroupID").GetValue(o);
                    if (!groups.ContainsKey(group)) groups[group] = new List<object>();
                    groups[group].Add(b);
                    barricades++;
                }
                else if (kind == "Event_RaidEvent") EventAt = p;
                else if (kind == "TruckerSpawnPoint") TruckAt = p;
                else if (kind == "Spawn_RaidRespawn") _respawns.Add(p);
                else if (kind == "TransmissionSpawnPoint") _transmissions.Add(o);
                else if (kind == "Event_Ambush")
                {
                    _ambushAt = p;
                    _ambushMax = Convert.ToInt32(GameRuntime.MapValue(o, "MaxActiveWalkers"));
                }
                else if (kind == "Event_Assault")
                {
                    _assaultAt = p;
                    _assaultRadius = Convert.ToSingle(GameRuntime.MapValue(o, "TriggerRadius"));
                    _assaultRoster.Clear();
                    // The assault's roster from the map: walkers and its specials (Butcher Zombie.Butcher,
                    // Floater Zombie.Floater, walking bomb Zombie.WalkingBomb: the types behind GameObjectBase.IsButcher,
                    // IsWalkingBomb, and the tutorial's Floater).
                    foreach (var kv in new[] { new KeyValuePair<string, string>("Walkers", GameRuntime.PlainZombieType), new KeyValuePair<string, string>("Butchers", ButcherType),
                                               new KeyValuePair<string, string>("Floaters", FloaterType), new KeyValuePair<string, string>("WalkingBombs", WalkingBombType) })
                    {
                        int min = Convert.ToInt32(GameRuntime.MapValue(o, kv.Key + "_Min")), max = Convert.ToInt32(GameRuntime.MapValue(o, kv.Key + "_Max"));
                        int n = _random.Next(min, max + 1);
                        for (int i = 0; i < n; i++) _assaultRoster.Add(kv.Value);
                    }
                    for (int i = 0; i < AssaultVeterans; i++) _assaultRoster.Add(VeteranType);
                }
                else if (kind == "Event_TriggerPosition") _triggerPositions.Add(p);
            }
            Type groupType = R.Type("BarricadeGroup");
            foreach (var kv in groups)
            {
                object group = groupType.GetConstructor(All, null, new[] { typeof(int), typeof(int) }, null).Invoke(new object[] { kv.Key, (int)BarricadeHp });
                foreach (object piece in kv.Value) groupType.GetMethod(R.Name("BarricadeGroup.Add"), All).Invoke(group, new[] { piece });
            }
            // Map event spawns: one zombie per point in its spawn pose; our AI wakes them as the player comes near (ours).
            // The map's pool holds about 70-77 plain zombies; keep a reserve for the ambush and assault walkers.
            int events = 0;
            foreach (float[] s in _eventSpots)
            {
                if (statics + events >= PopulationCap) break;
                if (SpawnZombie(GameRuntime.PlainZombieType, s, new[] { s[2], s[3] }, (int)s[4]) != null) events++;
            }
            RegisterSupplies();
            _log("scout: map " + Map + " (" + _g.MapName + "), event " + EventName(Event) + " at " + Fmt(EventAt) + ", truck " + Fmt(TruckAt) + ", supplies goal " + Goal +
                 "; " + statics + " static and " + events + " event-point zombies, " + barricades + " barricade pieces in " + groups.Count + " groups, " + _respawns.Count +
                 " respawn points, " + _transmissions.Count + " transmission points, ambush " + Fmt(_ambushAt) + " (" + _ambushMax + "), assault " + Fmt(_assaultAt));
            ServerHooks.OnGameMessage = OnGameMessage;
        }

        private bool _poolEmptyLogged;

        private object SpawnZombie(string type, float[] p, float[] d, object state)
        {
            if (_poolEmptyLogged && type == GameRuntime.PlainZombieType) return null;
            try
            {
                object z = _g.SpawnNpc(type, p[0], p[1], GameRuntime.ZombieTeam, d);
                if (state != null)
                {
                    PropertyInfo ss = z.GetType().GetProperty("SpawnState_Current", All);
                    ss.SetValue(z, Enum.ToObject(ss.PropertyType, Convert.ToInt32(state)), null);
                }
                Zombies.Add(z);
                return z;
            }
            catch (Exception e)
            {
                if (type == GameRuntime.PlainZombieType)
                {
                    _poolEmptyLogged = true;
                    _log("scout: no more plain zombies in the pool (" + Zombies.Count + " spawned): " + GameRuntime.Unwrap(e).Message);
                }
                else _log("scout: spawning " + type + " failed: " + GameRuntime.Unwrap(e).Message);
                return null;
            }
        }

        /// <summary>A barricade piece set up as in the tutorial; Neutral so the player can break it.</summary>
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
            FieldInfo team = t.GetField(R.Name("Barricade.Team"), All);
            team.SetValue(b, Enum.Parse(team.FieldType, "Neutral"));
            b.GetType().GetProperty("TeamId", All).SetValue(b, Enum.Parse(team.FieldType, "Neutral"), null);
            _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { b, null, null });
            float hp = ServerHooks.GetStat(b, "MaxHealth");
            if (hp <= 0) hp = BarricadeHp;
            t.GetMethod(R.Name("Barricade.SetHealth"), All).Invoke(b, new object[] { hp });
            return b;
        }

        // ---- steps ----

        public void Tick(float time)
        {
            _time = time;
            Respawn(time);
            if (_won) return;
            float[] a = ServerHooks.Position(_player);
            Transmissions(a);
            Ambush(time, a);
            Assault(a);
            if (Step == "start") { SetStep("go to the event", "match start"); return; }
            if (time - _stepSince < MinStepSeconds) return;
            if (Step == "go to the event" && EventAt != null && Distance(a, EventAt) <= NearEvent)
                SetStep("event", "the player is within " + NearEvent + " units of the event");
            else if (Step == "event")
            {
                if (Event == KillLooter && Looter != null && Destroyed(Looter))
                {
                    // The game's own drop (Supplies.DropFromKill) gives 3 (balance data); Lab has no supply spawn
                    // points, so the rest of the goal is dropped where the Looter fell (stand-in).
                    int rest = Goal - 3;
                    if (rest > 0) DropPickups(ServerHooks.Position(Looter), rest);
                    SetStep("return", "the Looter is dead; " + Math.Max(rest, 0) + " supplies dropped there besides the game's own drop (stand-in)");
                }
                else if (Event != KillLooter) Interaction(time, a);
            }
            else if (Step == "return" && TruckAt != null && Distance(a, TruckAt) <= NearTruck && _returnObjective == MoveTo)
                Objective(ReturnToTruck, ReturnVisual, Objective1, true, "the player reached the truck");
            if (Step == "return") SupplySafetyNet(time);
            if (Step == "return" && TeamSupplies() >= Goal) Win();
        }

        private int _returnObjective;
        private readonly HashSet<int> _spoken = new HashSet<int>();

        private void SetStep(string step, string why)
        {
            string from = Step;
            Step = step;
            _stepSince = _time;
            _log("scout step " + from + " -> " + step + " (" + why + ")");
            if (step == "go to the event")
            {
                Objective(Event, 0, MoveTo, false, null, true);
            }
            else if (step == "event")
            {
                if (Event == KillLooter)
                {
                    Looter = SpawnZombie(LooterType, EventAt, new[] { 0f, 1f }, null);
                    if (Looter != null)
                    {
                        Looter.GetType().GetProperty("SpawnState_Current", All).SetValue(Looter, Enum.ToObject(Looter.GetType().GetProperty("SpawnState_Current", All).PropertyType, 0), null);
                        _log("scout: Looter " + ServerHooks.Describe(Looter) + " at " + Fmt(EventAt) + ", Health " + ServerHooks.GetStat(Looter, "Health") + ", IsLooter " +
                             Looter.GetType().GetProperty("IsLooter", All).GetValue(Looter, null));
                    }
                    Objective(Event, 0, Objective1, false);
                }
                else
                {
                    AddInteraction(Event == StealSupplies ? 0 : 3);
                    Objective(Event, 0, Objective1, false);
                }
            }
            else if (step == "return")
            {
                Interactions().Clear();
                _returnObjective = 0;
                Objective(ReturnToTruck, ReturnVisual, MoveTo, true);
            }
        }

        // ---- objectives (ScoutMode.Lists lists, ObjectiveUpdate) ----

        private object Lists() { return GetField(_g.GameMode, R.Name("ScoutMode.Lists")); }
        private IList Interactions() { return (IList)GetField(Lists(), R.Name("ScoutLists.Interactions")); }
        private IList Markers() { return (IList)GetField(Lists(), R.Name("ScoutLists.Markers")); }
        private IList States() { return (IList)GetField(Lists(), R.Name("ScoutLists.States")); }

        /// <summary>
        /// Sets the current objective (ScoutLists.States holds just it), a minimap marker at its place (ScoutLists.Markers), and
        /// sends ObjectiveUpdate (GameMessage 30) built with the game's own CrossroadsGUIUpdate.
        /// </summary>
        private void Objective(int evt, int visual, int objective, bool supplies, string why = null, bool newMission = false)
        {
            if (evt == ReturnToTruck) _returnObjective = objective;
            Type st = _g.Game("ConductorGameLogic.View.RaidEventObjectiveState");
            object state = Activator.CreateInstance(st);
            SetEnum(state, "EventType", evt);
            st.GetField("VisualID").SetValue(state, (ushort)visual);
            SetEnum(state, "Objective", objective);
            if (supplies)
            {
                SetOptional(state, "ProgressUnit", Enum.ToObject(OptionalArg(st, "ProgressUnit"), 2));   // ObjectiveProgressUnit.Supplies
                SetOptional(state, "ProgressValue", Convert.ChangeType(Goal, OptionalArg(st, "ProgressValue")));
            }
            IList states = States();
            states.Clear();
            states.Add(state);
            IList markers = Markers();
            markers.Clear();
            float[] at = evt == ReturnToTruck ? TruckAt : EventAt;
            if (at != null)
            {
                Type mt = R.Type("ScoutMarker");
                object marker = Activator.CreateInstance(mt);
                FieldInfo kind = mt.GetField(R.Name("ScoutMarker.Kind"), All);
                kind.SetValue(marker, Enum.ToObject(kind.FieldType, 0));
                mt.GetField(R.Name("ScoutMarker.Position"), All).SetValue(marker, _g.Vector2(at[0], at[1]));
                markers.Add(marker);
            }
            object update = Activator.CreateInstance(_g.Game("ConductorGameLogic.View.CrossroadsGUIUpdate"));
            if (newMission) update.GetType().GetMethod("AddNewMissionData", All).Invoke(update, null);
            update.GetType().GetMethod("AddObjectiveUpdate", All).Invoke(update, new[] { state });
            // Event 4 (start new objective: the objective log takes the new state) and 3 (minimap).
            update.GetType().GetMethod("AddObjectiveLogUpdateDelay", All).Invoke(update, new object[] { false });
            update.GetType().GetMethod("AddMinimapUpdateDelay", All).Invoke(update, null);
            // The event's Objective transmission once, with its first objective. Not for the return:
            // for Lab its package is the Looter intro again (RaidEventViewSettings), and the truck's own
            // transmission points (NotEnoughSupplies / DeliverSupplies) speak there.
            bool speak = evt != ReturnToTruck && _spoken.Add(evt);
            if (speak)
            {
                object transmission = Enum.ToObject(_g.Game("ConductorGameLogic.View.TransmissionType"), 1);   // Objective
                update.GetType().GetMethod("AddTransmissionData", All, null, new[] { _g.Game("ConductorGameLogic.View.RaidEventType"), transmission.GetType(), typeof(int), typeof(float) }, null)
                    .Invoke(update, new[] { Enum.ToObject(_g.Game("ConductorGameLogic.View.RaidEventType"), evt), transmission, visual, 0f });
            }
            SendUpdate(update);
            _log("scout: objective " + EventName(evt) + " visual " + visual + " " + (objective == MoveTo ? "MoveTo" : "Objective1") + (supplies ? ", supplies " + Goal : "") +
                 ", marker at " + Fmt(at) + (why == null ? "" : " (" + why + ")"));
        }

        private void SendUpdate(object update)
        {
            MethodInfo serialize = update.GetType().GetMethod("Serialize", All);
            ServerHooks.SendGameMessage(ObjectiveUpdateMessage, m => serialize.Invoke(update, new[] { m.Buffer }));
        }

        /// <summary>
        /// The map's transmission points (EventType, TransmissionType, VisualID) play once when the player comes
        /// near one. The map doesn't say when in the event a point belongs (ours, an inference from the
        /// Outpost playtest: "It's a trap" played on the way in): the event's points along the route play
        /// before the event is done, its points within EventAreaRange of the target only after it (the
        /// steal, the Looter kill, the flag), and the return-to-truck points during the return.
        /// </summary>
        private void Transmissions(float[] a)
        {
            bool done = Step == "return";
            foreach (object t in _transmissions)
            {
                if (_played.Contains(t)) continue;
                int evt = Convert.ToInt32(GameRuntime.MapValue(t, "EventType"));
                float[] at = _g.MapPosition(t);
                bool nearTarget = EventAt != null && Distance(at, EventAt) <= EventAreaRange;
                bool allowed = evt == ReturnToTruck ? done : evt == Event && (nearTarget ? done : !done);
                if (!allowed || Distance(a, at) > TransmissionRange) continue;
                string type = GameRuntime.MapValue(t, "TransmissionType").ToString();
                if (evt == ReturnToTruck && (type == "NotEnoughSupplies" || type == "DeliverSupplies") && (CarriedSupplies() + TeamSupplies() >= Goal) != (type == "DeliverSupplies")) continue;
                _played.Add(t);
                int visual = Convert.ToInt32(GameRuntime.MapValue(t, "VisualID"));
                object update = Activator.CreateInstance(_g.Game("ConductorGameLogic.View.CrossroadsGUIUpdate"));
                Type rt = _g.Game("ConductorGameLogic.View.RaidEventType"), tt = _g.Game("ConductorGameLogic.View.TransmissionType");
                update.GetType().GetMethod("AddTransmissionData", All, null, new[] { rt, tt, typeof(int), typeof(float) }, null)
                    .Invoke(update, new[] { Enum.ToObject(rt, evt), Enum.Parse(tt, type), visual, 0f });
                SendUpdate(update);
                _log("scout: transmission " + type + " for " + EventName(evt) + " visual " + visual + " at " + Fmt(_g.MapPosition(t)));
            }
        }

        // ---- interactions (steal, flag) ----

        private void AddInteraction(int type)
        {
            Type it = _g.Game("ConductorGameLogic.View.InteractionPoint");
            object p = Activator.CreateInstance(it);
            it.GetField("ID").SetValue(p, (ushort)1);
            it.GetField("VisualID").SetValue(p, (ushort)0);
            it.GetField("Position").SetValue(p, _g.Vector2(EventAt[0], EventAt[1]));
            it.GetField("Direction").SetValue(p, _g.Vector2(0f, 1f));
            SetEnum(p, "Event", Event);
            SetEnum(p, "Type", type);
            SetEnum(p, "State", 1);   // Allowed
            Interactions().Clear();
            Interactions().Add(p);
            _log("scout: interaction point 1, type " + (type == 0 ? "GrapSupplies" : "Flag") + " at " + Fmt(EventAt));
        }

        private void SetInteraction(int state, float progress)
        {
            IList list = Interactions();
            if (list.Count == 0) return;
            object p = list[0];
            SetEnum(p, "State", state);
            p.GetType().GetField("Progress").SetValue(p, progress);
            list[0] = p;   // a struct: write it back
        }

        private bool OnGameMessage(int type, GameBuffer b)
        {
            if (type != StartInteractionMessage) return false;
            ushort id = b.ReadUShort(), second = b.ReadUShort();
            float[] a = ServerHooks.Position(_player);
            bool ok = Step == "event" && Interactions().Count > 0 && EventAt != null && Distance(a, EventAt) <= InteractRange + 20f;
            _log("scout: StartInteraction (" + id + ", " + second + ") at " + Distance(a, EventAt ?? a).ToString("0.0") + " units" + (ok ? "; started" : "; refused"));
            if (ok && _interactStart < 0f) { _interactStart = _time; SetInteraction(3, 0f); }
            return true;
        }

        private void Interaction(float time, float[] a)
        {
            if (_interactStart < 0f) return;
            bool dead = (bool)_player.GetType().GetProperty("IsDead", All).GetValue(_player, null);
            if (dead || Distance(a, EventAt) > InteractRange + 20f)
            {
                _interactStart = -1f;
                SetInteraction(1, 0f);
                _log("scout: interaction interrupted");
                return;
            }
            float progress = Math.Min(1f, (time - _interactStart) / InteractSeconds);
            SetInteraction(progress >= 1f ? 4 : 3, progress);
            if (progress < 1f) return;
            _interactStart = -1f;
            if (Event == StealSupplies)
            {
                Supplies().GetType().GetMethod(R.Name("Supplies.Give"), All).Invoke(Supplies(), new object[] { _player, Goal });
                SetStep("return", "the player stole the supplies (" + Goal + "; carried now " + CarriedSupplies() + ")");
            }
            else
            {
                DropPickups(EventAt, Goal);
                SetStep("return", "the player secured the flag; " + Goal + " supplies dropped there (stand-in amount)");
            }
        }

        // ---- supplies ----

        /// <summary>
        /// Stand-in safety net (ours, owner's spec after the Lab softlock): if supplies the server
        /// dropped stay uncollected for UncollectedSeconds, or carried + delivered + everything on the
        /// ground can no longer reach the goal, the missing amount is dropped by the truck.
        /// </summary>
        private void SupplySafetyNet(float time)
        {
            int have = CarriedSupplies() + TeamSupplies();
            // Not while the player is dead: her carried supplies are being dropped (Supplies.DropOnDeath) and the
            // new pickups only count once they are active.
            if (have >= Goal || TruckAt == null || (bool)_player.GetType().GetProperty("IsDead", All).GetValue(_player, null)) { _unreachableSince = -1f; return; }
            var ground = _g.ActiveSynchronizables(new HashSet<object>()).Where(o => o.GetType().Name == R.Short("SupplyPickup")).ToList();
            int onGround = ground.Sum(o => Convert.ToInt32(GetField(o, R.Name("SupplyPickup.Amount"))));
            _drops.RemoveAll(d => !ground.Contains(d.Key));
            bool stale = _drops.Any(d => time - d.Value >= UncollectedSeconds);
            bool unreachable = have + onGround < Goal;
            if (!unreachable) _unreachableSince = -1f;
            else if (_unreachableSince < 0f) _unreachableSince = time;
            unreachable = unreachable && time - _unreachableSince >= 3f;   // must hold for 3 s
            if (!stale && !unreachable) return;
            _unreachableSince = -1f;
            int missing = Goal - have;
            _drops.Clear();   // the old drop is written off; the truck drop is not tracked
            DropPickups(new[] { TruckAt[0] + 20f, TruckAt[1] }, missing, false);
            _log("scout: " + (stale ? "dropped supplies uncollected for " + UncollectedSeconds + " s" : "the goal can't be reached (" + have + " carried and delivered, " + onGround + " on the ground)") +
                 "; dropped the missing " + missing + " by the truck (stand-in safety net)");
        }

        private readonly List<KeyValuePair<object, float>> _drops = new List<KeyValuePair<object, float>>();
        private float _unreachableSince = -1f;

        private object Supplies() { return _g.GameMode.GetType().GetProperty("SupplyData", All).GetValue(_g.GameMode, null); }

        private void RegisterSupplies()
        {
            object supplies = Supplies();
            supplies.GetType().GetMethod(R.Name("Supplies.Register"), All).Invoke(supplies, new[] { _player });
        }

        public int CarriedSupplies()
        {
            ushort me = (ushort)_player.GetType().GetProperty("IndexPlayer", All).GetValue(_player, null);
            return (int)Supplies().GetType().GetMethod(R.Name("Supplies.Carried"), All).Invoke(Supplies(), new object[] { (int)me });
        }

        public int TeamSupplies()
        {
            MethodInfo m = Supplies().GetType().GetMethod(R.Name("Supplies.Delivered"), All);
            return (int)m.Invoke(Supplies(), new[] { _player.GetType().GetProperty("TeamId", All).GetValue(_player, null) });
        }

        /// <summary>Ground pickups (SupplyPickup: position and amount fields) in a ring, as in the tutorial.</summary>
        private void DropPickups(float[] at, int total, bool track = true)
        {
            Type t = R.Type("SupplyPickup");
            int count = Math.Max(1, (total + SupplyPerPickup - 1) / SupplyPerPickup);
            for (int i = 0; i < count; i++)
            {
                object pickup = _g.TakeFromPool(t);
                double angle = 2 * Math.PI * i / count;
                float r = count == 1 ? 0f : DropRadius;
                ServerHooks.SetField(pickup, R.Name("Pickup.Position"), _g.Vector2(at[0] + (float)Math.Cos(angle) * r, at[1] + (float)Math.Sin(angle) * r));
                ServerHooks.SetField(pickup, R.Name("SupplyPickup.Amount"), (byte)Math.Min(SupplyPerPickup, total - i * SupplyPerPickup));
                _g.GameManagerType.GetMethod("SpawnGameObject", All).Invoke(_g.GameManager, new object[] { pickup, null, null });
                if (track) _drops.Add(new KeyValuePair<object, float>(pickup, _time));
            }
        }

        /// <summary>A kill: the game's own drop on Looter, special and elite kills (Supplies.DropFromKill; it needs the
        /// blow's object, whose Owner is the killer, as the game passes it).</summary>
        public void OnKill(object victim, object killer)
        {
            if (killer != null && killer.GetType().GetProperty("Owner", All) != null && killer.GetType().GetProperty("Owner", All).GetValue(killer, null) == null && victim == Looter)
                _log("scout: the Looter's killing blow came from " + ServerHooks.Describe(killer) + " with no owner; the game's drop needs one");
            if (victim == _player) return;
            try { Supplies().GetType().GetMethod(R.Name("Supplies.DropFromKill"), All).Invoke(Supplies(), new[] { victim, killer }); }
            catch (Exception e) { _log("scout: supply drop for " + ServerHooks.Describe(victim) + " failed: " + GameRuntime.Unwrap(e).Message); }
            if (victim == Looter) _log("scout: the Looter died (killer " + ServerHooks.Describe(killer) + ")");
        }

        // ---- events ----

        private void Ambush(float time, float[] a)
        {
            if (_ambushAt == null || _ambushDone) return;
            if (_ambushUntil < 0f)
            {
                if (Distance(a, _ambushAt) > AmbushRange) return;
                _ambushUntil = time + AmbushSeconds;
                _log("scout: ambush at " + Fmt(_ambushAt) + " for " + AmbushSeconds + " s (stand-in), up to " + _ambushMax + " walkers");
            }
            if (time > _ambushUntil) { _ambushDone = true; _log("scout: ambush over"); return; }
            if (time < _nextAmbush) return;
            _nextAmbush = time + 0.7f;
            List<float[]> near = _eventSpots.Where(s => Distance(s, _ambushAt) <= 250f).ToList();
            if (near.Count == 0 || Zombies.Count(z => !Destroyed(z) && Distance(ServerHooks.Position(z), _ambushAt) <= 300f) >= _ambushMax) return;
            float[] s0 = near[_random.Next(near.Count)];
            object vet = ++_ambushSpawns % AmbushVeteranEvery == 0 ? SpawnZombie(VeteranType, s0, new[] { s0[2], s0[3] }, (int)s0[4]) : null;
            if (vet == null) SpawnZombie(GameRuntime.PlainZombieType, s0, new[] { s0[2], s0[3] }, (int)s0[4]);
        }

        /// <summary>
        /// The map's Event_Assault, once: it starts when the player comes within its trigger radius (at least 50)
        /// of the assault point or of the nearest Event_TriggerPosition (within AssaultTriggerPairRange),
        /// and spawns its whole roster near the assault point (Club: "that's a big one" is its Floater).
        /// </summary>
        private void Assault(float[] a)
        {
            if (_assaultAt == null || _assaultDone) return;
            if (_assaultTrigger == null && _triggerPositions.Count > 0)
            {
                float[] nearest = _triggerPositions.OrderBy(t => Distance(t, _assaultAt)).First();
                _assaultTrigger = Distance(nearest, _assaultAt) <= AssaultTriggerPairRange ? nearest : _assaultAt;
            }
            float range = Math.Max(_assaultRadius, 50f);
            bool near = Distance(a, _assaultAt) <= range || (_assaultTrigger != null && Distance(a, _assaultTrigger) <= range);
            if (!near) return;
            _assaultDone = true;
            var spawned = new List<string>();
            foreach (string type in _assaultRoster)
            {
                double ang = _random.NextDouble() * Math.PI * 2;
                object z = SpawnZombie(type, new[] { _assaultAt[0] + (float)Math.Cos(ang) * 30f, _assaultAt[1] + (float)Math.Sin(ang) * 30f }, new[] { 0f, 1f }, 0);
                if (z == null) { spawned.Add(type + " (none left in the pool)"); continue; }
                ServerHooks.AggroRange[z] = 400f;   // an assault comes for the player
                spawned.Add(ServerHooks.Describe(z) + " Health " + ServerHooks.GetStat(z, "Health"));
            }
            _log("scout: assault at " + Fmt(_assaultAt) + " (trigger " + Fmt(_assaultTrigger) + "): " + string.Join(", ", spawned.ToArray()));
        }

        // ---- respawn ----

        /// <summary>the player drops what she carries when she dies (Supplies.DropOnDeath) and comes back at the nearest raid respawn point after a few seconds.</summary>
        private void Respawn(float time)
        {
            bool dead = (bool)_player.GetType().GetProperty("IsDead", All).GetValue(_player, null);
            if (!dead) { _diedAt = -1f; return; }
            if (_diedAt < 0f)
            {
                _diedAt = time;
                _diedPos = ServerHooks.Position(_player);
                int carried = CarriedSupplies();
                try { Supplies().GetType().GetMethod(R.Name("Supplies.DropOnDeath"), All).Invoke(Supplies(), new[] { _player }); }
                catch (Exception e) { _log("scout: dropping carried supplies failed: " + GameRuntime.Unwrap(e).Message); }
                _log("scout: the player died at " + Fmt(_diedPos) + " carrying " + carried + " (dropped); respawning in " + RespawnSeconds + " s");
                return;
            }
            if (time - _diedAt < RespawnSeconds) return;
            float[] at = _respawns.OrderBy(r => Distance(r, _diedPos)).FirstOrDefault() ?? new[] { _spawn.X, _spawn.Y };
            object v = _g.Vector2(at[0], at[1]);
            _player.GetType().GetField("SpawnPosition", All).SetValue(_player, v);
            _g.Game("ConductorGameLogic.Entities.Entity").GetMethod("Teleport", All).Invoke(_player, new object[] { v, true });
            _player.GetType().GetMethod("Respawn", All, null, Type.EmptyTypes, null).Invoke(_player, null);
            _diedAt = -1f;
            _log("scout: the player respawned at " + Fmt(at) + ", Health " + ServerHooks.GetStat(_player, "Health"));
        }

        // ---- win ----

        /// <summary>Delivered >= goal: GameMode.IsCompleted, then TeamFinished (GameMessage 4: team 3 bits, placement 3 bits, finished now).</summary>
        private void Win()
        {
            _won = true;
            Step = "won";
            _g.GameMode.GetType().GetProperty("IsCompleted", All).SetValue(_g.GameMode, true, null);
            ServerHooks.SyncNow = true;   // IsCompleted in the same frame as TeamFinished
            int team = Convert.ToInt32(_player.GetType().GetProperty("TeamId", All).GetValue(_player, null));
            ServerHooks.SendGameMessage(TeamFinishedMessage, m => { m.WriteBits((uint)team, 3); m.WriteBits(0u, 3); m.Write(true); });
            _log("scout: mission complete, team delivered " + TeamSupplies() + " of " + Goal + "; IsCompleted, sent TeamFinished(team " + team + ", First)");
            if (OnWon != null) OnWon();
        }

        // ---- helpers ----

        private static string EventName(int e)
        {
            switch (e)
            {
                case 17: return "ScoutCaptureFlag";
                case 18: return "ScoutKillLooter";
                case 19: return "ScoutStealSupplies";
                case 20: return "ScoutReturnToTruck";
                default: return e.ToString();
            }
        }

        private static void SetEnum(object o, string name, int value)
        {
            FieldInfo f = o.GetType().GetField(name, All);
            f.SetValue(o, Enum.ToObject(f.FieldType, value));
        }

        private static Type OptionalArg(Type owner, string name) { return owner.GetField(name, All).FieldType.GetGenericArguments()[0]; }

        private static void SetOptional(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name, All);
            f.SetValue(o, Activator.CreateInstance(f.FieldType, value));
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

        private static float Distance(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Fmt(float[] p) { return p == null ? "none" : "(" + p[0].ToString("0") + ", " + p[1].ToString("0") + ")"; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Horde (game mode type 4, maps 5 Outpost, 6 Lab, 7 Club; queue resistance normal 2). The client's
    /// WaveRules server hooks are empty, so the mission script is ours (docs/game-modes.md).
    /// What the client does by itself, from its own code:
    /// - load game mode spawns a Checkpoint per MapKind.Checkpoint and a supply-point static object
    ///   (HordeSupplyPoint) per MapKind.Wave; checkpoints activate, raise Npc.SpawnTag and respawn/heal on their own.
    /// - The Horde HUD (objectives, "Event Initiated", wave x of y, countdown, kill counter, hoarder,
    ///   boss on the minimap, supplies looted) follows the ActiveMode sync struct (role WaveRules.State).
    /// - Capture: the player channels the capture ability at a neutral WaveSpawner (action slot 26),
    ///   which ends in horde spawner captured -> Room.BeginCapture (Room.State 1).
    /// Everything else (spawners, blockages, waves, hoarder, boss, win) is driven here. Values marked
    /// "stand-in" have no source: the original wave and reward tables lived on the server.
    /// </summary>
    public sealed class HordeDirector
    {
        private const BindingFlags All = GameRuntime.All;
        public const int RespawnMessage = 1, TeamFinishedMessage = 4, ConcedeMessage = 25;
        /// <summary>drop table box ID: the Normal and Heroic Horde boxes.</summary>
        public const int HordeNormalGoalBox = 3, HordeNormalClockBox = 8, NormalGoldBox = 16, NormalSilverBox = 17,
                         HordeHardGoalBox = 2, HordeHardClockBox = 7, HeroicGoldBox = 14, HeroicSilverBox = 15;
        public const int First = 0, Second = 1, Third = 2, Conceded = 3;

        /// <summary>Stand-in: plain waves per supply point; the second point secured adds a hoarder wave.</summary>
        public const int WavesPerPoint = 3;
        /// <summary>Stand-in from event supply spawn's defaults (no map has one): per wave and at once.</summary>
        public const int WalkersPerWave = 12, SpecialsPerWave = 1, WalkerCap = 16, SpecialCap = 2;
        /// <summary>
        /// Stand-in for Heroic (owner's choice, 2026-10-07): the Normal wave x1.5, with Veterans among the walkers
        /// and one elite among the specials. The Heroic cache holds 25 Veterans and about 3 of each elite type,
        /// which Normal never uses (pool probe), so they are the clearest sign of what Heroic waves held.
        /// </summary>
        public const int HeroicWalkersPerWave = 18, HeroicVeteransPerWave = 4, HeroicSpecialsPerWave = 2, HeroicElitesPerWave = 1,
                         HeroicMaxActiveWalkers = 24, HardSpecialCap = 3;
        public static string VeteranType { get { return R.Name("Zombie.Veteran"); } }
        /// <summary>Butcher, Floater, Ram, Puller and Siren elites.</summary>
        public static readonly string[] EliteTypes = { R.Name("Zombie.ButcherElite"), R.Name("Zombie.FloaterElite"), R.Name("Zombie.RamElite"), R.Name("Zombie.PullerElite"), R.Name("Zombie.SirenElite") };
        /// <summary>Stand-in: seconds of countdown before each wave (the HUD field allows 0-60).</summary>
        public const float CountdownSeconds = 10f;
        /// <summary>Stand-in: seconds between the second point secured and the boss stage.</summary>
        public const float BossDelaySeconds = 5f;
        /// <summary>Stand-in supplies looted per kill (by analogy with the balance data's 5 and 50, inferred) and for the hoarder.</summary>
        public const int WalkerSupplies = 5, SpecialSupplies = 50, HoarderSupplies = 100;
        /// <summary>Concede is allowed after this many seconds of match (client rule, unity client).</summary>
        public const float ConcedeAfterSeconds = 300f;
        /// <summary>Stand-in: a dead player not asking to respawn (MatchMessage 1) is respawned after this long.</summary>
        public const float AutoRespawnSeconds = 20f;
        /// <summary>Ours: a player standing in a neutral point's capture range this long captures it, in case the capture channel never reaches the server.</summary>
        public const float FallbackCaptureSeconds = 12f;
        /// <summary>Plain zombies placed around the map at start (stand-in); the rest of the pool is kept for the waves and the boss.</summary>
        public const int PopulationCap = 30;
        public const float StaticAggroRange = 150f, WaveAggroRange = 400f;

        public static string ButcherType { get { return R.Name("Zombie.Butcher"); } }
        public static string FloaterType { get { return R.Name("Zombie.Floater"); } }
        public static string PullerType { get { return R.Name("Zombie.Puller"); } }
        public static string RamType { get { return R.Name("Zombie.Ram"); } }
        public static string SirenType { get { return R.Name("Zombie.Siren"); } }

        private sealed class Point
        {
            public int Id, Slot;           // Slot 1 or 2: the HUD's Supply Point 1 (HordeState.Point1) or 2 (HordeState.Point2)
            public float[] At;
            public float CaptureReach, Radius;
            public object Spawner;
            public readonly List<object> Blockages = new List<object>();   // MapKind.WaveBlock map objects
            public readonly List<float[]> Spots = new List<float[]>();
            public int State;              // 0 neutral, 1 running, 2 secured
            public bool Hoarder;           // has a hoarder wave (the second point secured)
            public float InRangeSince = -1f;
        }

        private readonly GameRuntime _g;
        private readonly object _player;
        private readonly Action<string> _log;
        private readonly Random _random = new Random();
        private readonly object _mode;
        /// <summary>horde mode type Heroic (2), from the queue (resistance hard).</summary>
        public readonly bool Heroic;
        private int _toSpawnVeterans, _toSpawnElites, _eliteTurn;
        private readonly FieldInfo _state;
        public readonly int Map;
        public readonly string HoarderType, BossKind;
        private readonly List<Point> _points = new List<Point>();
        private readonly List<float[]> _eventSpots = new List<float[]>();
        private float[] _bossAt;
        private int _bossMinions;
        private float _bossRespawnMin = 3f, _bossRespawnMax = 4f;
        public readonly List<object> Zombies = new List<object>();

        // wave state of the running point
        private Point _active;
        private int _wave, _waves, _toSpawnWalkers, _toSpawnSpecials, _waveSize;
        private bool _hoarderWave;
        private float _countdownUntil = -1f, _nextSpawn;
        private readonly List<object> _waveZombies = new List<object>();
        private object _hoarder, _boss;
        private readonly List<object> _minions = new List<object>();
        private float _nextMinion;
        private int _secured, _specialTurn;
        private float _bossAfter = -1f;

        private float _time, _diedAt = -1f;
        private bool _started, _won, _respawnAsked;
        public string Step = "start";
        public int Supplies;
        public Action<int, int[]> OnFinished;   // placement, boxes

        public HordeDirector(GameRuntime g, object player, Action<string> log)
        {
            _g = g;
            _player = player;
            _log = log;
            _mode = g.ActiveMode;
            _state = _mode.GetType().GetField(R.Name("HordeRules.State"), All);
            Map = g.MapIndex;
            Heroic = g.Difficulty == 2;
            // Per map, from the pool table (type names from WorldObject.Is*): the hoarder is
            // Outpost floater hoarder, Lab butcher hoarder, Club puller hoarder. The table's real bosses
            // (puller boss, ram boss, SirenBoss) never reach the cache at any difficulty (probe, 2026-10-06:
            // none pooled for Easy, Normal or Heroic), and the client can only show what it cached the same
            // way, so the boss is the one elite the Normal cache holds per map (stand-in): Outpost
            // ButcherElite, Lab FloaterElite, Club RamElite.
            HoarderType = Map == 5 ? R.Name("Zombie.FloaterCarrier") : Map == 6 ? R.Name("Zombie.ButcherCarrier") : R.Name("Zombie.PullerCarrier");
            BossKind = Map == 5 ? R.Name("Zombie.ButcherElite") : Map == 6 ? R.Name("Zombie.FloaterElite") : R.Name("Zombie.RamElite");
        }

        // ---- world ----

        public void PopulateWorld()
        {
            var statics = new List<object[]>();
            foreach (object o in _g.MapThings())
            {
                string kind = o.GetType().Name;
                float[] p = _g.MapPosition(o), d = _g.MapDirection(o);
                if (kind == R.Name("MapKind.Wave"))
                {
                    Point pt = new Point
                    {
                        Id = Convert.ToInt32(GameRuntime.MapValue(o, "ID")),
                        At = p,
                        CaptureReach = Convert.ToSingle(GameRuntime.MapValue(o, R.Name("Map.CaptureReach"))),
                        Radius = Convert.ToSingle(GameRuntime.MapValue(o, R.Name("Map.Leash"))),
                    };
                    _points.Add(pt);
                }
                else if (kind == R.Name("MapKind.EventSpawns"))
                    _eventSpots.Add(new[] { p[0], p[1], d[0], d[1], Convert.ToSingle(Convert.ToInt32(GameRuntime.MapValue(o, R.Name("Npc.Pose")))) });
                else if (kind == R.Name("MapKind.StaticSpawn")) statics.Add(new object[] { p, d, GameRuntime.MapValue(o, R.Name("Npc.Pose")) });
                else if (kind == R.Name("MapKind.Boss"))
                {
                    _bossAt = p;
                    // The map's own boss-event values, with its hard-difficulty set for Heroic.
                    _bossMinions = Convert.ToInt32(GameRuntime.MapValue(o, R.Name(Heroic ? "Map.HardMinionCap" : "Map.MinionCap")));
                    _bossRespawnMin = Convert.ToSingle(GameRuntime.MapValue(o, R.Name(Heroic ? "Map.HardRespawnMin" : "Map.RespawnMin")));
                    _bossRespawnMax = Convert.ToSingle(GameRuntime.MapValue(o, R.Name(Heroic ? "Map.HardRespawnMax" : "Map.RespawnMax")));
                }
            }
            _points.Sort((a, b) => a.Id.CompareTo(b.Id));
            for (int i = 0; i < _points.Count; i++) _points[i].Slot = i + 1;
            foreach (object o in _g.MapThings().Where(o => o.GetType().Name == R.Name("MapKind.WaveBlock")))
            {
                int spawner = Convert.ToInt32(GameRuntime.MapValue(o, R.Name("Wave.Activate")));
                Point pt = _points.FirstOrDefault(x => x.Id == spawner);
                if (pt != null) pt.Blockages.Add(o);
            }
            // Supply points sit where the client's own supply-point objects (HordeSupplyPoint, made by load game mode
            // at get trigger position(ID)) are: the capture channel and range use that spot.
            foreach (object o in (System.Collections.IEnumerable)_mode.GetType().GetProperty(R.Name("Map.Objects"), All).GetValue(_mode, null))
            {
                if (o.GetType().Name != R.Short("HordeSupplyPoint")) continue;
                Point pt = _points.FirstOrDefault(x => x.Id == Convert.ToInt32(ServerHooks.GetFieldValue(o, R.Name("MapPoint.Id"))));
                if (pt != null) pt.At = ServerHooks.Vector(ServerHooks.GetFieldValue(o, R.Name("MapPoint.Position")));
            }
            foreach (Point pt in _points)
            {
                // Wave spawn spots: the map's event spawn points inside the restricted area, away from the
                // middle (ours); a ring at 70% of the radius if the map has too few there.
                foreach (float[] s in _eventSpots)
                {
                    float dist = Distance(s, pt.At);
                    if (dist >= 30f && dist <= pt.Radius) pt.Spots.Add(s);
                }
                if (pt.Spots.Count < 4)
                    for (int i = 0; i < 8; i++)
                    {
                        double a = 2 * Math.PI * i / 8;
                        pt.Spots.Add(new[] { pt.At[0] + (float)Math.Cos(a) * pt.Radius * 0.7f, pt.At[1] + (float)Math.Sin(a) * pt.Radius * 0.7f, 0f, 1f, 0f });
                    }
                pt.Spawner = SpawnSpawner(pt.Id);
            }

            // Ambient population (stand-in): the static spawns, then event points away from the supply points.
            int placed = 0;
            foreach (object[] s in statics)
            {
                if (placed >= PopulationCap) break;
                object z = SpawnZombie(GameRuntime.PlainZombieType, (float[])s[0], (float[])s[1], s[2]);
                if (z != null) { ServerHooks.AggroRange[z] = StaticAggroRange; placed++; }
            }
            foreach (float[] s in _eventSpots.OrderBy(x => _random.Next()))
            {
                if (placed >= PopulationCap) break;
                if (_points.Any(pt => Distance(s, pt.At) <= pt.Radius + 60f) || (_bossAt != null && Distance(s, _bossAt) <= 120f)) continue;
                if (SpawnZombie(GameRuntime.PlainZombieType, s, new[] { s[2], s[3] }, (int)s[4]) != null) placed++;
            }
            ServerHooks.OnKill = OnKill;
            ServerHooks.OnGameMessage = OnGameMessage;
            _log("horde: map " + Map + " (" + _g.MapName + "), difficulty " + ServerHooks.GetFieldValue(_g.World, R.Name("World.Difficulty")) + "; supply points " +
                 string.Join(", ", _points.Select(p => p.Slot + ": ID " + p.Id + " at " + Fmt(p.At) + " range " + p.CaptureReach + " radius " + p.Radius + ", " +
                                                      p.Blockages.Count + " blockages, " + p.Spots.Count + " wave spots").ToArray()) +
                 "; boss " + BossKind + " at " + Fmt(_bossAt) + " (" + _bossMinions + " minions), hoarder " + HoarderType + "; " + placed + " ambient zombies");
        }

        /// <summary>One WaveSpawner per MapKind.Wave with the event's ID (the client's HordeSupplyPoint finds it by ID).</summary>
        private object SpawnSpawner(int id)
        {
            Type t = R.Type("Type.WaveSpawner");
            object s = _g.TakeFromPool(t);
            t.GetField("ID", All).SetValue(s, (ushort)id);
            _g.WorldType.GetMethod(R.Name("World.SpawnObject"), All).Invoke(_g.World, new object[] { s, null, null });
            _log("horde: WaveSpawner " + id + " spawned, synchronizable " + _g.SynchronizableIndex(s));
            return s;
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
                    PropertyInfo ss = z.GetType().GetProperty(R.Name("Npc.PoseNow"), All);
                    ss.SetValue(z, Enum.ToObject(ss.PropertyType, Convert.ToInt32(state)), null);
                }
                Zombies.Add(z);
                return z;
            }
            catch (Exception e)
            {
                if (type == GameRuntime.PlainZombieType)
                {
                    if (!_poolEmptyLogged) _log("horde: no more plain zombies in the pool (" + Zombies.Count + " spawned): " + GameRuntime.Unwrap(e).Message);
                    _poolEmptyLogged = true;
                }
                else _log("horde: spawning " + type + " failed: " + GameRuntime.Unwrap(e).Message);
                return null;
            }
        }

        // ---- the HUD struct (WaveRules.State) ----

        private void Set(string field, object value)
        {
            object s = _state.GetValue(_mode);   // a boxed copy of the struct
            FieldInfo f = s.GetType().GetField(field, All);
            f.SetValue(s, value);
            _state.SetValue(_mode, s);
        }

        private int Get(string field)
        {
            object s = _state.GetValue(_mode);
            return Convert.ToInt32(s.GetType().GetField(field, All).GetValue(s));
        }

        private float MatchLength { get { return (float)R.Type("Type.Mode").GetField("MatchLength", All).GetValue(_mode); } }

        // ---- steps ----

        public void Tick(float time)
        {
            _time = time;
            Respawn(time);
            if (_won) return;
            if (!_started)
            {
                _started = true;
                Set(R.Name("HordeState.Stage"), 1);   // stage 1: objectives horde event1/2 and the first transmission
                Set(R.Name("HordeState.Music"), 1);   // music
                SetStep("supply points", "match start");
                return;
            }
            float[] a = ServerHooks.Position(_player);
            bool dead = IsDead(_player);
            foreach (Point pt in _points)
            {
                if (pt.State != 0) continue;
                ushort capture = Convert.ToUInt16(pt.Spawner.GetType().GetProperty(R.Name("Room.State"), All).GetValue(pt.Spawner, null));
                if (capture == 1) { Captured(pt, "the capture channel finished"); continue; }
                if (_active != null || dead || Distance(a, pt.At) > pt.CaptureReach) { pt.InRangeSince = -1f; continue; }
                if (pt.InRangeSince < 0f) pt.InRangeSince = time;
                else if (time - pt.InRangeSince >= FallbackCaptureSeconds)
                {
                    pt.Spawner.GetType().GetMethod(R.Name("Room.BeginCapture"), All).Invoke(pt.Spawner, null);
                    Captured(pt, "fallback: the player stood in range " + FallbackCaptureSeconds + " s without a capture reaching the server");
                }
            }
            if (_active != null && !dead) Waves(time);
            if (_bossAfter >= 0f && time >= _bossAfter) { _bossAfter = -1f; StartBoss(); }
            if (_boss != null) Boss(time);
        }

        private void SetStep(string step, string why)
        {
            _log("horde step " + Step + " -> " + step + " (" + why + ")");
            Step = step;
        }

        private void Captured(Point pt, string why)
        {
            pt.State = 1;
            pt.InRangeSince = -1f;
            pt.Hoarder = _secured == _points.Count - 1;   // stand-in: the last point secured ends with the hoarder
            _active = pt;
            int spawned = 0;
            MethodInfo block = pt.Spawner.GetType().GetMethod(R.Name("Wave.Block"), All);
            ParameterInfo[] ps = block.GetParameters();
            foreach (object o in pt.Blockages)
            {
                float[] p = _g.MapPosition(o), d = _g.MapDirection(o);
                try
                {
                    block.Invoke(pt.Spawner, new[] { _g.Vector2(p[0], p[1]), _g.Vector2(d[0], d[1]),
                        Enum.Parse(ps[2].ParameterType, GameRuntime.MapValue(o, R.Name("Map.BarricadePieceType")).ToString()),
                        Enum.Parse(ps[3].ParameterType, GameRuntime.MapValue(o, R.Name("Map.BarricadeKind")).ToString()) });
                    spawned++;
                }
                catch (Exception e) { _log("horde: blockage at " + Fmt(p) + " failed: " + GameRuntime.Unwrap(e).Message); }
            }
            Set(pt.Slot == 1 ? R.Name("HordeState.Point1") : R.Name("HordeState.Point2"), 1);
            _wave = 0;
            _waves = WavesPerPoint + (pt.Hoarder ? 1 : 0);
            Set(R.Name("HordeState.Waves"), _waves);
            Set(R.Name("HordeState.WaveIndex"), 0);   // 0-based: the HUD shows HordeState.WaveIndex + 1 ("Wave 1 of 3")
            StartCountdown();
            SetStep("supply point " + pt.Slot, why + "; " + spawned + " blockages up, " + _waves + " waves (stand-in)" + (pt.Hoarder ? " ending with the hoarder" : ""));
        }

        private void StartCountdown()
        {
            _countdownUntil = _time + CountdownSeconds;
            Set(R.Name("HordeState.Countdown"), (int)CountdownSeconds);
            Set(R.Name("HordeState.ZombiesLeft"), 0);
            Set(R.Name("HordeState.WaveSize"), 0);
        }

        private void Waves(float time)
        {
            Point pt = _active;
            if (_countdownUntil >= 0f)
            {
                int left = (int)Math.Ceiling(_countdownUntil - time);
                if (left > 0) { if (Get(R.Name("HordeState.Countdown")) != left) Set(R.Name("HordeState.Countdown"), left); return; }
                _countdownUntil = -1f;
                Set(R.Name("HordeState.Countdown"), 0);
                _wave++;
                _hoarderWave = pt.Hoarder && _wave == _waves;
                _waveZombies.Clear();
                if (_hoarderWave)
                {
                    _toSpawnWalkers = 0;
                    _toSpawnSpecials = 0;
                    _waveSize = 1;
                    float[] s = Spot(pt);
                    _hoarder = SpawnZombie(HoarderType, s, new[] { 0f, 1f }, 0);
                    if (_hoarder != null) { ServerHooks.AggroRange[_hoarder] = WaveAggroRange; _waveZombies.Add(_hoarder); }
                    Set(R.Name("HordeState.Hoarder"), true);
                    _log("horde: wave " + _wave + "/" + _waves + " at supply point " + pt.Slot + ": the hoarder " + ServerHooks.Describe(_hoarder) + " at " + Fmt(s) +
                         (_hoarder == null ? "" : ", Health " + ServerHooks.GetStat(_hoarder, R.Name("Stat.Health"))));
                }
                else
                {
                    _toSpawnWalkers = Heroic ? HeroicWalkersPerWave : WalkersPerWave;
                    _toSpawnSpecials = Heroic ? HeroicSpecialsPerWave : SpecialsPerWave;
                    _toSpawnVeterans = Heroic ? HeroicVeteransPerWave : 0;
                    _toSpawnElites = Heroic ? HeroicElitesPerWave : 0;
                    _waveSize = _toSpawnWalkers + _toSpawnSpecials;
                    _log("horde: wave " + _wave + "/" + _waves + " at supply point " + pt.Slot + ": " + _toSpawnWalkers + " walkers" +
                         (Heroic ? " (" + _toSpawnVeterans + " Veterans)" : "") + " + " + _toSpawnSpecials + " special" +
                         (Heroic ? " (" + _toSpawnElites + " elite), Heroic" : "") + " (stand-in)");
                }
                Set(R.Name("HordeState.WaveIndex"), _wave - 1);
                Set(R.Name("HordeState.WaveSize"), _waveSize);
                Set(R.Name("HordeState.ZombiesLeft"), _waveSize);
                return;
            }
            // Spawn the wave a few at a time, at most WalkerCap / SpecialCap alive.
            if ((_toSpawnWalkers > 0 || _toSpawnSpecials > 0) && time >= _nextSpawn)
            {
                _nextSpawn = time + 0.4f;
                int aliveSpecials = _waveZombies.Count(z => !Destroyed(z) && !IsWalker(z));
                int aliveWalkers = _waveZombies.Count(z => !Destroyed(z)) - aliveSpecials;
                if (_toSpawnSpecials > 0 && aliveSpecials < (Heroic ? HardSpecialCap : SpecialCap)) { SpawnSpecial(pt); _toSpawnSpecials--; }
                else if (_toSpawnWalkers > 0 && aliveWalkers < (Heroic ? HeroicMaxActiveWalkers : WalkerCap))
                {
                    float[] s = Spot(pt);
                    object z = null;
                    if (_toSpawnVeterans > 0) { _toSpawnVeterans--; z = SpawnZombie(VeteranType, s, new[] { s[2], s[3] }, 0); }
                    if (z == null) z = SpawnZombie(GameRuntime.PlainZombieType, s, new[] { s[2], s[3] }, 0);
                    if (z != null) { ServerHooks.AggroRange[z] = WaveAggroRange; _waveZombies.Add(z); }
                    _toSpawnWalkers--;
                    if (z == null) _waveSize--;   // the pool ran dry: the wave is smaller
                }
            }
            Unstick(pt, time);
            int remaining = _toSpawnWalkers + _toSpawnSpecials + _waveZombies.Count(z => !Destroyed(z));
            if (Get(R.Name("HordeState.ZombiesLeft")) != remaining) Set(R.Name("HordeState.ZombiesLeft"), remaining);
            if (remaining > 0) return;
            if (_hoarderWave) Set(R.Name("HordeState.Hoarder"), false);
            if (_wave < _waves)
            {
                _log("horde: wave " + _wave + "/" + _waves + " cleared at supply point " + pt.Slot);
                Set(R.Name("HordeState.WaveIndex"), _wave);   // the next wave, 0-based; the HUD shows "Wave N Completed!" when this changes
                StartCountdown();
                return;
            }
            Secured(pt);
        }

        /// <summary>Ours (playtest 2026-10-07: an Outpost Butcher stood still in the bushes and held the wave open):
        /// a wave zombie that is away from the player and hasn't moved StuckDistance in StuckSeconds is moved to a
        /// spot near the middle of the point, which is walkable.</summary>
        private void Unstick(Point pt, float time)
        {
            float[] a = ServerHooks.Position(_player);
            foreach (object z in _waveZombies)
            {
                if (Destroyed(z)) { _moved.Remove(z); continue; }
                float[] p = ServerHooks.Position(z);
                float[] last;
                if (!_moved.TryGetValue(z, out last) || Distance(p, last) >= StuckDistance || Distance(p, a) <= 20f)
                {
                    _moved[z] = new[] { p[0], p[1], time };
                    continue;
                }
                if (time - last[2] < StuckSeconds) continue;
                double ang = _random.NextDouble() * Math.PI * 2;
                float r = Math.Min(30f, pt.Radius * 0.4f);
                float[] to = { pt.At[0] + (float)Math.Cos(ang) * r, pt.At[1] + (float)Math.Sin(ang) * r };
                R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), All).Invoke(z, new object[] { _g.Vector2(to[0], to[1]), true });
                _moved[z] = new[] { to[0], to[1], time };
                _log("horde: " + ServerHooks.Describe(z) + " stuck at " + Fmt(p) + " for " + StuckSeconds + " s; moved to " + Fmt(to));
            }
        }

        /// <summary>
        /// Boss stage (ours, a stand-in): the boss and its minions chase the player, but the drainage pit on
        /// Outpost is reached by a one-way drop (trigger character push) the navmesh has no route down, so they
        /// piled up at the edge (owner, 2026-10-07). One that hasn't moved StuckDistance in StuckSeconds while
        /// more than 25 units from the player is moved to a spot 45 units from the player with a clear line to
        /// them (the player's own side of any wall); closer if there is no such spot, and as a last resort beside them.
        /// </summary>
        private void ChaseUnstick(IEnumerable<object> chasers, float time)
        {
            float[] a = ServerHooks.Position(_player);
            foreach (object z in chasers)
            {
                if (z == null || Destroyed(z)) { if (z != null) _moved.Remove(z); continue; }
                float[] p = ServerHooks.Position(z);
                float[] last;
                if (!_moved.TryGetValue(z, out last) || Distance(p, last) >= StuckDistance || Distance(p, a) <= 25f)
                {
                    _moved[z] = new[] { p[0], p[1], time };
                    continue;
                }
                if (time - last[2] < StuckSeconds) continue;
                float[] to = null;
                double start = _random.NextDouble() * Math.PI * 2;
                // 45 units first; in a tight spot (the pit) closer rings, then right beside the player, so a boss
                // that can't get down is never left stuck (issue #7).
                foreach (float r in new[] { 45f, 30f, 20f, 12f })
                    for (int k = 0; k < 12 && to == null; k++)
                    {
                        double ang = start + k * Math.PI / 6;
                        float[] c = { a[0] + (float)Math.Cos(ang) * r, a[1] + (float)Math.Sin(ang) * r };
                        try { if (ServerHooks.LineClear(a, c)) to = c; } catch (Exception) { }
                    }
                if (to == null) to = new[] { a[0] + 8f, a[1] };
                R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), All).Invoke(z, new object[] { _g.Vector2(to[0], to[1]), true });
                _moved[z] = new[] { to[0], to[1], time };
                if (_chaseLogged++ < 20) _log("horde: " + ServerHooks.Describe(z) + " could not reach the player from " + Fmt(p) + " for " + StuckSeconds + " s; moved to " + Fmt(to));
            }
        }

        private int _chaseLogged;

        public const float StuckSeconds = 8f, StuckDistance = 3f;
        private readonly Dictionary<object, float[]> _moved = new Dictionary<object, float[]>();

        private void SpawnSpecial(Point pt)
        {
            // Stand-in mix: the Normal pool holds a handful of each special (pool table); take them in turn.
            string[] kinds = { ButcherType, FloaterType, PullerType, RamType, SirenType };
            float[] s = Spot(pt);
            object z = null;
            if (_toSpawnElites > 0)
            {
                // Heroic: an elite, in turn, never the boss's type (the boss needs its cached one).
                _toSpawnElites--;
                string[] elites = EliteTypes.Where(t => t != BossKind).ToArray();
                for (int i = 0; i < elites.Length && z == null; i++)
                    z = SpawnZombie(elites[(_eliteTurn + i) % elites.Length], s, new[] { 0f, 1f }, 0);
                _eliteTurn++;
            }
            for (int i = 0; i < kinds.Length && z == null; i++)
                z = SpawnZombie(kinds[(_specialTurn + i) % kinds.Length], s, new[] { 0f, 1f }, 0);
            _specialTurn++;
            if (z == null) { _waveSize--; return; }
            ServerHooks.AggroRange[z] = WaveAggroRange;
            _waveZombies.Add(z);
        }

        private static bool IsWalker(object z)
        {
            string t = z.GetType().FullName;
            return t == GameRuntime.PlainZombieType || t == VeteranType;
        }

        private float[] Spot(Point pt) { return pt.Spots[_random.Next(pt.Spots.Count)]; }

        private void Secured(Point pt)
        {
            pt.State = 2;
            _active = null;
            _secured++;
            pt.Spawner.GetType().GetMethod(R.Name("Room.Captured"), All).Invoke(pt.Spawner, null);   // state 2, blockages down
            Set(pt.Slot == 1 ? R.Name("HordeState.Point1") : R.Name("HordeState.Point2"), 2);
            Set(R.Name("HordeState.ZombiesLeft"), 0);
            Set(R.Name("HordeState.WaveSize"), 0);
            Set(R.Name("HordeState.Countdown"), 0);
            SetStep(_secured == _points.Count ? "boss" : "supply points", "supply point " + pt.Slot + " secured (" + _secured + " of " + _points.Count + ")");
            if (_secured == _points.Count) _bossAfter = _time + BossDelaySeconds;
        }

        // ---- boss ----

        private void StartBoss()
        {
            float[] at = _bossAt ?? _points.Last().At;
            _boss = SpawnZombie(BossKind, at, new[] { 0f, 1f }, 0);
            Set(R.Name("HordeState.Stage"), 2);   // stage 2: objective horde boss, boss sound
            Set(R.Name("HordeState.Music"), 3);
            if (_boss == null)
            {
                _log("horde: the boss " + BossKind + " could not be spawned; ending the match as a win so it doesn't soft-lock");
                Win();
                return;
            }
            ServerHooks.AggroRange[_boss] = 600f;
            Set(R.Name("HordeState.BossActive"), true);
            Set(R.Name("HordeState.BossMarker"), _g.Vector2(at[0], at[1]));
            _log("horde: boss " + ServerHooks.Describe(_boss) + " at " + Fmt(at) + ", Health " + ServerHooks.GetStat(_boss, R.Name("Stat.Health")) + "/" + ServerHooks.GetStat(_boss, R.Name("Stat.HealthMax")) +
                 "; up to " + _bossMinions + " minions every " + _bossRespawnMin + "-" + _bossRespawnMax + " s (the map's boss event)");
        }

        private float _bossMarkerAt;

        private void Boss(float time)
        {
            if (Destroyed(_boss)) { _log("horde: the boss is dead"); Win(); return; }
            if (time >= _bossMarkerAt)
            {
                _bossMarkerAt = time + 0.5f;
                float[] p = ServerHooks.Position(_boss);
                Set(R.Name("HordeState.BossMarker"), _g.Vector2(p[0], p[1]));
            }
            _minions.RemoveAll(Destroyed);
            ChaseUnstick(new[] { _boss }.Concat(_minions), time);
            if (_minions.Count >= _bossMinions || time < _nextMinion) return;
            _nextMinion = time + _bossRespawnMin + (float)_random.NextDouble() * (_bossRespawnMax - _bossRespawnMin);
            float[] b = ServerHooks.Position(_boss);
            double ang = _random.NextDouble() * Math.PI * 2;
            object z = SpawnZombie(GameRuntime.PlainZombieType, new[] { b[0] + (float)Math.Cos(ang) * 40f, b[1] + (float)Math.Sin(ang) * 40f }, new[] { 0f, 1f }, 0);
            if (z != null) { ServerHooks.AggroRange[z] = WaveAggroRange; _minions.Add(z); }
        }

        // ---- kills, supplies ----

        private void OnKill(object victim, object killer)
        {
            if (victim == _player || _won) return;
            int add = victim == _hoarder ? HoarderSupplies : IsWalker(victim) ? WalkerSupplies : SpecialSupplies;
            Supplies += add;
            Set(R.Name("HordeState.Supplies"), Supplies);
            if (victim == _hoarder)
            {
                _log("horde: the hoarder died; supplies looted " + Supplies + " (+" + HoarderSupplies + ", stand-in)");
                // Issue #6: the HUD state still said "hoarder active" until the wave ended, and the client kept the
                // dead hoarder standing (no death animation, removed late). Clear it as it dies.
                Set(R.Name("HordeState.Hoarder"), false);
            }
        }

        // ---- death and respawn ----

        private bool OnGameMessage(int type, GameBuffer b)
        {
            if (type == RespawnMessage)
            {
                _respawnAsked = true;
                _log("horde: the client asked to respawn (MatchMessage 1)");
                return true;
            }
            if (type == ConcedeMessage)
            {
                float length = MatchLength;
                _log("horde: concede asked at match time " + length.ToString("0") + " s" + (length >= ConcedeAfterSeconds ? "; conceded" : "; refused (allowed after " + ConcedeAfterSeconds + " s)"));
                if (length >= ConcedeAfterSeconds && !_won) Finish(Conceded, "conceded");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Solo, every death is a wipe. Stand-in rule: during a supply point the wave counter starts over
        /// and the player comes back at the point itself (the blockages would keep them out from the
        /// checkpoint); otherwise at the current checkpoint (World.SpawnAt).
        /// </summary>
        private void Respawn(float time)
        {
            if (!IsDead(_player)) { _diedAt = -1f; _respawnAsked = false; return; }
            if (_diedAt < 0f)
            {
                _diedAt = time;
                _respawnAsked = false;
                if (_active != null)
                {
                    foreach (object z in _waveZombies) if (!Destroyed(z)) Despawn(z);
                    _waveZombies.Clear();
                    if (_hoarderWave) { Set(R.Name("HordeState.Hoarder"), false); _hoarder = null; }
                    _wave = 0;
                    _toSpawnWalkers = _toSpawnSpecials = 0;
                    Set(R.Name("HordeState.WaveIndex"), 0);
                    _countdownUntil = -1f;
                    Set(R.Name("HordeState.ZombiesLeft"), 0);
                    Set(R.Name("HordeState.WaveSize"), 0);
                }
                _log("horde: the player died at " + Fmt(ServerHooks.Position(_player)) + (_active != null ? "; supply point " + _active.Slot + " waves start over (stand-in wipe rule)" : ""));
                return;
            }
            if (!_respawnAsked && time - _diedAt < AutoRespawnSeconds) return;
            float[] at;
            if (_active != null) at = _active.At;
            else
            {
                at = ServerHooks.Vector(_mode.GetType().GetMethod(R.Name("World.SpawnAt"), All).Invoke(_mode, new[] { _player.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(_player, null) }));
                if (at[0] == 0f && at[1] == 0f) at = ServerHooks.Vector(_player.GetType().GetField(R.Name("Map.Position"), All).GetValue(_player));
            }
            object v = _g.Vector2(at[0], at[1]);
            _player.GetType().GetField(R.Name("Map.Position"), All).SetValue(_player, v);
            R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), All).Invoke(_player, new object[] { v, true });
            _player.GetType().GetMethod(R.Name("Entity.Revive"), All, null, Type.EmptyTypes, null).Invoke(_player, null);
            _log("horde: the player respawned at " + Fmt(at) + (_active != null ? " (supply point " + _active.Slot + ")" : " (checkpoint " + _mode.GetType().GetProperty(R.Name("Npc.SpawnTag"), All).GetValue(_mode, null) + ")") +
                 (_respawnAsked ? "" : " after " + AutoRespawnSeconds + " s without a respawn request") + ", Health " + ServerHooks.GetStat(_player, R.Name("Stat.Health")));
            _diedAt = -1f;
            _respawnAsked = false;
            if (_active != null) StartCountdown();
        }

        private void Despawn(object z)
        {
            try { z.GetType().GetMethod("Destroy", All, null, Type.EmptyTypes, null).Invoke(z, null); }
            catch (Exception e) { _log("horde: removing " + ServerHooks.Describe(z) + " failed: " + GameRuntime.Unwrap(e).Message); }
        }

        // ---- the end ----

        /// <summary>Medal by match length, as the client's Horde scoreboard reads it: Gold within WaveRules gold time (720 s), Silver within silver time (1200 s), else Bronze.</summary>
        private void Win()
        {
            float length = MatchLength;
            float gold = (float)_mode.GetType().GetField(R.Name("HordeRules.GoldLimit"), All).GetValue(_mode), silver = (float)_mode.GetType().GetField(R.Name("HordeRules.SilverLimit"), All).GetValue(_mode);
            int placement = length <= gold ? First : length <= silver ? Second : Third;
            Finish(placement, "the boss died at match time " + length.ToString("0") + " s (gold within " + gold + ", silver within " + silver + ")");
        }

        private void Finish(int placement, string why)
        {
            _won = true;
            Set(R.Name("HordeState.BossActive"), false);
            _mode.GetType().GetProperty("IsCompleted", All).SetValue(_mode, true, null);
            ServerHooks.SyncNow = true;   // IsCompleted in the same frame as team finished
            int team = Convert.ToInt32(_player.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(_player, null));
            ServerHooks.SendMatchMessage(TeamFinishedMessage, m => { m.WriteBits((uint)team, 3); m.WriteBits((uint)placement, 3); m.Write(true); });
            // Boxes (CC/RewardTables): mission + time box, plus the Gold or Silver medal box; conceded gets the time box only.
            int mission = Heroic ? HordeHardGoalBox : HordeNormalGoalBox, timeBox = Heroic ? HordeHardClockBox : HordeNormalClockBox;
            int[] boxes = placement == Conceded ? new[] { timeBox }
                        : placement == First ? new[] { mission, timeBox, Heroic ? HeroicGoldBox : NormalGoldBox }
                        : placement == Second ? new[] { mission, timeBox, Heroic ? HeroicSilverBox : NormalSilverBox }
                        : new[] { mission, timeBox };
            string medal = placement == First ? R.Name("Medal.First") : placement == Second ? R.Name("Medal.Second") : placement == Third ? R.Name("Medal.Third") : R.Name("Medal.None");
            SetStep("finished", why + "; IsCompleted, sent team finished(team " + team + ", " + medal + "), supplies looted " + Supplies);
            if (OnFinished != null) OnFinished(placement, boxes);
        }

        // ---- helpers ----

        private static bool IsDead(object o) { return (bool)o.GetType().GetProperty(R.Name("Entity.Dead"), All).GetValue(o, null); }

        private static bool Destroyed(object o)
        {
            return o == null || IsDead(o) || !(bool)o.GetType().GetProperty("IsActive", All).GetValue(o, null);
        }

        private static float Distance(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private static string Fmt(float[] p) { return p == null ? "none" : "(" + p[0].ToString("0") + ", " + p[1].ToString("0") + ")"; }
    }
}

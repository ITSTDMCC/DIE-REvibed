using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Scavenger Hunt (game mode type 6, maps 1 Resort, 2 Jungle, 3 Expedition; queue 1), 3 teams x 4 heroes:
    /// the human plus 3 bot teammates on Team1, 4 bots on Team2 and on Team3 (owner, 2026-10-07: bots from the
    /// start). docs/game-modes.md describes the mode; the client only reacts to state, so every decision
    /// is ours (values marked "stand-in" have no source):
    /// - Team phase and barricade race: per team ScavengerState.Teams[team to index] (ScavengerTeam.Phase phase 0/1/2, ScavengerTeam.Barricade1/ScavengerTeam.Barricade2 barricade
    ///   states 0 none, 1 active, 2 destroyed, ScavengerTeam.BarricadeHealth current barricade health 0..100). A team's barricades are its
    ///   wooden MapKind.Breakable groups (Team field), nearest its start first.
    /// - Storage rooms (supply points): one RoomEntity entity per MapKind.RoomEvent, list entries ScavengerState.RoomProgress progress,
    ///   ScavengerState.RoomSpeed speed x100, ScavengerState.RoomBonus bonus, ScavengerState.RoomOwner owner, ScavengerState.RoomCapturer capturing team, in MapKind.RoomPoint order.
    /// - Supplies: the game's own Supplies (register, drops on kills Supplies.DropFromKill, carry, deliver at the truck with slot 17,
    ///   steal with slot 28, drop on death Supplies.DropOnDeath); income for owned rooms via Supplies.AddDelivered.
    /// - Hoarders and dynamic events in ScavengerState.Events (type 1 hoarder); end game ScavengerState.Night, leader ScavengerState.Leader, game over game over.
    /// - The end: team finished per team, scavenger medal information (26), Rewards (5).
    /// </summary>
    public sealed class ScavengerDirector
    {
        private const BindingFlags All = GameRuntime.All;
        public const int TeamFinishedMessage = 4, MedalMessage = 26;
        public const int DeliverSlot = 17, StealSlot = 28;
        /// <summary>drop table box ID: Scavenger boxes.</summary>
        public const int MissionBox = 1, TimeBox = 6, VictoryBox = 11, FirstBarricadeBox = 12, FirstEndGameBox = 13;

        // ---- stand-ins (no source) ----
        /// <summary>Barricade health per piece group (the original is lost).</summary>
        public const float BarricadeHp = 150f;
        /// <summary>Bots stand this far in front of a barricade's middle to break it (ours).</summary>
        public const float BarricadeStandOff = 26f;
        /// <summary>A room captures in CaptureSeconds with one capturing hero present (x2 with two, ...); contested it pauses.</summary>
        public const float CaptureSeconds = 10f, CaptureReach = 30f;
        /// <summary>Income: every IncomeSeconds, IncomePerRoom delivered for each owned room (tuned offline toward a 20-30 minute match).</summary>
        public const float IncomeSeconds = 60f;
        public const int IncomePerRoom = 25;
        /// <summary>First hoarder after this long, then one every HoarderEvery.</summary>
        public const float FirstCarrier = 240f, HoarderEvery = 240f;
        /// <summary>Looters near storage rooms, at most this many alive, one every LooterEvery.</summary>
        public const int MaxLooters = 6;
        public const float LooterEvery = 12f;
        /// <summary>A team reaching the win target holds the lead this long (as the client does) before it wins.</summary>
        public const float DenySeconds = 15f;
        public const float RespawnSeconds = 8f;
        /// <summary>Bots deliver once carrying this much (or anything once night falls).</summary>
        public const int BotDeliverAt = 20;
        public const int AmbientZombies = 70;
        public const float GiveUpSeconds = 4f, IgnoreSeconds = 20f;
        public static string LooterType { get { return R.Name("Zombie.Looter"); } }
        public static readonly string[] HoarderTypes = { R.Name("Zombie.ButcherCarrier"), R.Name("Zombie.PullerCarrier"), R.Name("Zombie.FloaterCarrier") };

        private sealed class TeamInfo
        {
            public string Name;
            public object Id;
            public int Index;
            public SpawnPoint Start, Supply;
            public float[] Truck;
            public readonly List<List<object>> Barricades = new List<List<object>>();
            /// <summary>Where to stand to hit each barricade: in front of it on the team's side (ours).</summary>
            public readonly List<float[]> Approach = new List<float[]>();
            public int Phase, BarricadeAt;
            public float BarricadeHpTotal;
        }

        private sealed class Room
        {
            public ushort Id;
            public float[] At;
            public object Entity;
            public object Owner, Capturing;
            public float Progress;
            public int Speed;   // ScavengerState.RoomSpeed: capture speed x100, negative while contested
            public string Name;
        }

        private readonly GameRuntime _g;
        private readonly object _human;
        private readonly Action<string> _log;
        private readonly Random _random = new Random();
        private readonly object _mode;
        private readonly FieldInfo _state;
        private readonly List<TeamInfo> _teams = new List<TeamInfo>();
        private readonly List<Room> _rooms = new List<Room>();
        private readonly List<float[]> _hoarderSpots = new List<float[]>();
        private readonly List<float[]> _spots = new List<float[]>();
        public readonly List<Bot> Bots = new List<Bot>();
        private readonly Dictionary<object, float> _diedAt = new Dictionary<object, float>();
        private readonly List<object> _looters = new List<object>();
        private object _hoarder;
        private int _hoarderEventId;
        private float _time, _nextIncome = IncomeSeconds, _nextHoarder = FirstCarrier, _nextLooter = 10f, _leaderSince = -1f;
        private bool _started, _ended, _night;
        private object _barricadeRaceTeam, _fillTruckTeam, _winner;
        public int Target, TruckLoaded;
        public Action<int, int[]> OnFinished;   // placement of the human's team, boxes

        public ScavengerDirector(GameRuntime g, object human, Action<string> log)
        {
            _g = g;
            _human = human;
            _log = log;
            _mode = g.ActiveMode;
            _state = _mode.GetType().GetField(R.Name("ScavengerMode.State"), All);
            Target = (int)_mode.GetType().GetField(R.Name("ScavengerMode.WinTarget"), All).GetValue(_mode);
            TruckLoaded = (int)_mode.GetType().GetField(R.Name("ScavengerMode.TruckLoaded"), All).GetValue(_mode);
        }

        // ---- set-up ----

        public void PopulateWorld(IList<string> botHeroes, int level, Weapon melee, Weapon ranged)
        {
            Type teamType = _human.GetType().GetProperty(R.Name("Entity.Team"), All).PropertyType;
            foreach (string name in new[] { R.Name("Team.One"), R.Name("Team.Two"), R.Name("Team.Three") })
            {
                object id = Enum.Parse(teamType, name);
                // team to index: team - 1.
                _teams.Add(new TeamInfo { Name = name, Id = id, Index = Convert.ToInt32(id) - 1 });
            }
            var groups = new Dictionary<int, List<object>>();
            var groupDir = new Dictionary<int, float[]>();
            var groupTeam = new Dictionary<int, string>();
            foreach (object o in _g.MapThings())
            {
                string kind = o.GetType().Name;
                float[] p = _g.MapPosition(o), d = _g.MapDirection(o);
                object teamField = GameRuntime.MapValue(o, R.Name("Record.Team"));
                TeamInfo team = teamField == null ? null : _teams.FirstOrDefault(t => t.Name == teamField.ToString());
                if (kind == R.Name("MapKind.HeroStart") && team != null)
                {
                    var sp = new SpawnPoint { Kind = kind, Team = team.Name, IsStart = (bool)GameRuntime.MapValue(o, R.Name("Msg.Starting")), X = p[0], Y = p[1] };
                    if (sp.IsStart) team.Start = sp; else team.Supply = sp;
                }
                else if (kind == R.Name("MapKind.DriverStart") && team != null) team.Truck = p;
                else if (kind == R.Name("MapKind.Breakable") && team != null && GameRuntime.MapValue(o, R.Name("Map.BarricadeKind")).ToString() == R.Name("BarricadeKind.Wood") &&
                         !(bool)GameRuntime.MapValue(o, R.Name("Map.Dynamic")))
                {
                    int group = Convert.ToInt32(GameRuntime.MapValue(o, R.Name("Map.Group")));
                    if (!groups.ContainsKey(group)) groups[group] = new List<object>();
                    groups[group].Add(SpawnBarricade(o, p, d));
                    groupDir[group] = d;
                    groupTeam[group] = team.Name;
                }
                else if (kind == R.Name("MapKind.RoomEvent"))
                    _rooms.Add(new Room { Id = Convert.ToUInt16(GameRuntime.MapValue(o, "ID")), At = p, Name = GameRuntime.MapValue(o, "Name").ToString().Replace("PCStrings.GUI.Storage.", "") });
                else if (kind == R.Name("MapKind.Dynamic") && (bool)GameRuntime.MapValue(o, R.Name("Map.WaveAllowed"))) _hoarderSpots.Add(p);
                else if (kind == R.Name("MapKind.EventSpawns") || kind == R.Name("MapKind.StaticSpawn")) _spots.Add(new[] { p[0], p[1], d[0], d[1] });
            }
            Type groupType = R.Type("BarricadeGroup");
            foreach (var kv in groups)
            {
                object group = groupType.GetConstructor(All, null, new[] { typeof(int), typeof(int) }, null).Invoke(new object[] { kv.Key, (int)BarricadeHp });
                foreach (object piece in kv.Value) groupType.GetMethod(R.Name("BarricadeGroup.Add"), All).Invoke(group, new[] { piece });
                TeamInfo t = _teams.First(x => x.Name == groupTeam[kv.Key]);
                t.Barricades.Add(kv.Value);
                // The group's middle, and the spot BarricadeStandOff in front of it along its facing, on the side
                // nearer the team's start (the barricade blocks the route from the start).
                float cx = kv.Value.Average(x => Pos(x)[0]), cy = kv.Value.Average(x => Pos(x)[1]);
                float[] dir = groupDir[kv.Key];
                float[] front = { cx + dir[0] * BarricadeStandOff, cy + dir[1] * BarricadeStandOff }, back = { cx - dir[0] * BarricadeStandOff, cy - dir[1] * BarricadeStandOff };
                t.Approach.Add(t.Start != null && Dist(back, Start(t)) < Dist(front, Start(t)) ? back : front);
            }
            foreach (TeamInfo t in _teams)
                if (t.Start != null)
                {
                    var order = Enumerable.Range(0, t.Barricades.Count).OrderBy(i => Dist(Pos(t.Barricades[i][0]), Start(t))).ToList();
                    var bs = order.Select(i => t.Barricades[i]).ToList();
                    var ap = order.Select(i => t.Approach[i]).ToList();
                    t.Barricades.Clear(); t.Barricades.AddRange(bs);
                    t.Approach.Clear(); t.Approach.AddRange(ap);
                }

            // Storage rooms in the game mode's MapKind.RoomPoint order, which the synced lists follow.
            Type roomType = R.Type("Type.Room");
            var points = ((IEnumerable)_mode.GetType().GetProperty(R.Name("Map.Objects"), All).GetValue(_mode, null)).Cast<object>().Where(o => o.GetType().Name == R.Name("MapKind.RoomPoint")).ToList();
            var ordered = new List<Room>();
            foreach (object pt in points)
            {
                ushort id = Convert.ToUInt16(GetField(pt, R.Name("MapPoint.Id")));
                Room r = _rooms.FirstOrDefault(x => x.Id == id);
                if (r != null) ordered.Add(r);
            }
            if (ordered.Count == _rooms.Count) { _rooms.Clear(); _rooms.AddRange(ordered); }
            foreach (Room r in _rooms)
            {
                object room = _g.TakeFromPool(roomType);
                roomType.GetField("ID", All).SetValue(room, r.Id);
                _g.WorldType.GetMethod(R.Name("World.SpawnObject"), All).Invoke(_g.World, new object[] { room, null, null });
                if (roomType.GetField(R.Name("MapKind.RoomPoint"), All).GetValue(room) == null) roomType.GetMethod(R.Name("Room.Link"), All).Invoke(room, null);
                r.Entity = room;
                r.Owner = r.Capturing = Neutral();
                // The room's entry in the synced lists (ScavengerState.RoomProgress..ScavengerState.RoomCapturer). The client's minimap marker and the in-world
                // capture ring read the lists at this index (GUI-map-marker.Update, storage-room-HUD.Update); only the
                // original server set it, so every room showed entry 0's state (owner, 2026-10-07: capturing one
                // point lit up all of them).
                room.GetType().GetProperty(R.Name("Room.Index"), All).SetValue(room, (byte)_rooms.IndexOf(r), null);
            }

            // Ambient zombies (stand-in count), away from the teams' starts.
            int placed = 0;
            foreach (float[] s in _spots.OrderBy(x => _random.Next()))
            {
                if (placed >= AmbientZombies) break;
                if (_teams.Any(t => t.Start != null && Dist(s, Start(t)) < 250f)) continue;
                try { object z = _g.SpawnNpc(GameRuntime.PlainZombieType, s[0], s[1], GameRuntime.ZombieTeam, new[] { s[2], s[3] }); ServerHooks.AggroRange[z] = 120f; placed++; }
                catch (Exception) { break; }
            }

            // Bots: 3 teammates for the human, 4 on each rival team. Same gear and level as the human (stand-in).
            int slot = 1, hero = 0;
            foreach (TeamInfo t in _teams)
            {
                int count = t.Name == R.Name("Team.One") ? 3 : 4;
                for (int i = 0; i < count && hero < botHeroes.Count; i++, slot++, hero++)
                {
                    Weapon bm = melee == null ? null : new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = melee.SchematicId, UserId = 0x7FFF0000UL + (ulong)slot };
                    Weapon br = ranged == null ? null : new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = ranged.SchematicId, UserId = 0x7FFF0000UL + (ulong)slot };
                    try { Bots.Add(BotDriver.Create(_g, slot, botHeroes[hero], t.Name, Pretty(botHeroes[hero]), t.Start, level, bm, br)); }
                    catch (Exception e) { _log("scavenger: bot " + botHeroes[hero] + " failed: " + GameRuntime.Unwrap(e).Message); }
                }
            }
            // The bots' waypoint network over the map's spawn spots and the places they need (rooms, trucks,
            // spawns, barricade fronts, hoarder spots).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var interest = new List<float[]>();
            foreach (TeamInfo t in _teams)
            {
                if (t.Start != null) interest.Add(Start(t));
                if (t.Supply != null) interest.Add(new[] { t.Supply.X, t.Supply.Y });
                if (t.Truck != null) interest.Add(t.Truck);
                interest.AddRange(t.Approach);
            }
            interest.AddRange(_rooms.Select(r => r.At));
            interest.AddRange(_hoarderSpots);
            BotDriver.Graph = RouteGraph.Build(interest.Concat(_spots));
            BotDriver.Log = _log;
            _log("scavenger: waypoint network " + BotDriver.Graph.Nodes.Count + " nodes, " + BotDriver.Graph.EdgeCount + " edges (" + clock.ElapsedMilliseconds + " ms)");
            object supplies = Supplies();
            foreach (object p in Players()) supplies.GetType().GetMethod(R.Name("Supplies.Register"), All).Invoke(supplies, new[] { p });
            ServerHooks.OnKill = OnKill;
            ServerHooks.BlockControl = BlockStagger;
            ServerHooks.ChooseTarget = NearestHero;
            ServerHooks.DamageFilter = WatchFriendlyFire;
            _log("scavenger: map " + _g.MapName + ", win " + Target + ", truck full " + TruckLoaded + "; teams " +
                 string.Join("; ", _teams.Select(t => t.Name + " (index " + t.Index + ") start " + Fmt(Start(t)) + " truck " + Fmt(t.Truck) + ", " + t.Barricades.Count + " barricades").ToArray()) +
                 "; rooms " + string.Join(", ", _rooms.Select(r => r.Name + " " + r.Id).ToArray()) + "; " + _hoarderSpots.Count + " hoarder spots, " + placed + " ambient zombies; bots " +
                 string.Join(", ", Bots.Select(b => b.Name + " " + b.Team).ToArray()));
        }

        private static string Pretty(string hero) { return hero.Replace("Survivor", "").Replace("Mutated", " (Mutated)").Replace("Armored", " (Armored)"); }

        private object SpawnBarricade(object map, float[] p, float[] d)
        {
            Type t = R.Type("Barrier");
            object b = _g.TakeFromPool(t);
            Type mt = map.GetType();
            b.GetType().GetField("Position", All).SetValue(b, _g.Vector2(p[0], p[1]));
            b.GetType().GetField(R.Name("Map.Position"), All).SetValue(b, _g.Vector2(p[0], p[1]));
            t.GetField(R.Name("Barricade.Facing"), All).SetValue(b, _g.Vector2(d[0], d[1]));
            FieldInfo part = t.GetField(R.Name("Barricade.Part"), All);
            part.SetValue(b, Enum.Parse(part.FieldType, mt.GetField(R.Name("Map.Piece")).GetValue(map).ToString()));
            t.GetField(R.Name("Barricade.Kind"), All).SetValue(b, mt.GetField(R.Name("Map.BarricadeKind")).GetValue(map));
            FieldInfo team = t.GetField(R.Name("Barricade.Team"), All);
            team.SetValue(b, Enum.Parse(team.FieldType, R.Name("Team.None")));
            b.GetType().GetProperty(R.Name("Entity.Team"), All).SetValue(b, Enum.Parse(team.FieldType, R.Name("Team.None")), null);
            _g.WorldType.GetMethod(R.Name("World.SpawnObject"), All).Invoke(_g.World, new object[] { b, null, null });
            float hp = ServerHooks.GetStat(b, R.Name("Stat.HealthMax"));
            if (hp <= 0) hp = BarricadeHp;
            t.GetMethod(R.Name("Barricade.SetHealth"), All).Invoke(b, new object[] { hp });
            return b;
        }

        // ---- the synced state (ScavengerMode.State, a struct) ----

        private object State() { return _state.GetValue(_mode); }
        private void Store(object s) { _state.SetValue(_mode, s); }

        private void SetTeam(TeamInfo t, string field, object value)
        {
            object s = State();
            Array teams = (Array)s.GetType().GetField(R.Name("ScavengerState.Teams"), All).GetValue(s);
            object e = teams.GetValue(t.Index);
            FieldInfo f = e.GetType().GetField(field, All);
            f.SetValue(e, f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, value) : Convert.ChangeType(value, f.FieldType));
            teams.SetValue(e, t.Index);
            Store(s);
        }

        private void SetGame(string field, object value)
        {
            object s = State();
            s.GetType().GetField(field, All).SetValue(s, value);
            Store(s);
        }

        private IList List(string field) { return (IList)State().GetType().GetField(field, All).GetValue(State()); }

        private void SyncRooms()
        {
            object s = State();
            Type st = s.GetType();
            var gtb = (IList)st.GetField(R.Name("ScavengerState.RoomProgress"), All).GetValue(s);
            var htb = (IList)st.GetField(R.Name("ScavengerState.RoomSpeed"), All).GetValue(s);
            var itb = (IList)st.GetField(R.Name("ScavengerState.RoomBonus"), All).GetValue(s);
            var jtb = (IList)st.GetField(R.Name("ScavengerState.RoomOwner"), All).GetValue(s);
            var ktb = (IList)st.GetField(R.Name("ScavengerState.RoomCapturer"), All).GetValue(s);
            gtb.Clear(); htb.Clear(); itb.Clear(); jtb.Clear(); ktb.Clear();
            foreach (Room r in _rooms)
            {
                gtb.Add(r.Progress);
                htb.Add(r.Speed);
                itb.Add(0);
                jtb.Add(r.Owner);
                ktb.Add(r.Capturing);
            }
            st.GetField(R.Name("ScavengerState.RoomCount"), All).SetValue(s, _rooms.Count);
            Store(s);
        }

        // ---- the match ----

        public void Tick(float time)
        {
            float dt = _started ? time - _time : 0f;
            _time = time;
            if (!_started)
            {
                _started = true;
                foreach (TeamInfo t in _teams)
                {
                    SetPhase(t, 1, "match start");
                    if (t.Barricades.Count > 0) SetTeam(t, R.Name("ScavengerTeam.Barricade1"), 1);
                }
                SyncRooms();
            }
            if (_ended) return;
            // Per tick, once: the living zombies (the NPC list holds every pooled NPC, ~1400 on Resort).
            _zombies.Clear();
            foreach (object npc in (Array)_g.WorldType.GetField(R.Name("World.Npcs"), All).GetValue(_g.World))
                if (npc != null && (bool)npc.GetType().GetProperty("IsActive", All).GetValue(npc, null) && !BotDriver.IsDead(npc) &&
                    npc.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(npc, null).ToString() == GameRuntime.ZombieTeam) _zombies.Add(npc);
            Respawns(time);
            Barricades();
            Rooms(dt);
            Income(time);
            Events(time);
            EndGame(time);
            foreach (Bot b in Bots) { try { Brain(b, time); BotDriver.Drive(_g, b, time); } catch (Exception e) { _log("scavenger: bot " + b.Name + " failed: " + GameRuntime.Unwrap(e).Message); } }
        }

        private void SetPhase(TeamInfo t, int phase, string why)
        {
            t.Phase = phase;
            SetTeam(t, R.Name("ScavengerTeam.Phase"), phase);
            _log("scavenger: " + t.Name + " phase " + phase + " (" + why + ")");
        }

        /// <summary>The barricade race: the current group's health %, destroyed -> the next, both -> supply area.</summary>
        private void Barricades()
        {
            foreach (TeamInfo t in _teams)
            {
                if (t.Phase != 1) continue;
                if (t.BarricadeAt >= t.Barricades.Count)
                {
                    SetPhase(t, 2, t.Barricades.Count == 0 ? "no barricades" : "both barricades down; supply area reached");
                    if (_barricadeRaceTeam == null) _barricadeRaceTeam = t.Id;
                    continue;
                }
                List<object> group = t.Barricades[t.BarricadeAt];
                float hp = group.Sum(b => Math.Max(0f, ServerHooks.GetStat(b, R.Name("Stat.Health")))), max = group.Sum(b => Math.Max(1f, ServerHooks.GetStat(b, R.Name("Stat.HealthMax"))));
                int pct = (int)Math.Round(100f * hp / max);
                SetTeam(t, R.Name("ScavengerTeam.BarricadeHealth"), pct);
                if (group.All(Destroyed))
                {
                    SetTeam(t, t.BarricadeAt == 0 ? R.Name("ScavengerTeam.Barricade1") : R.Name("ScavengerTeam.Barricade2"), 2);
                    _log("scavenger: " + t.Name + " barricade " + (t.BarricadeAt + 1) + " destroyed");
                    t.BarricadeAt++;
                    if (t.BarricadeAt < t.Barricades.Count) SetTeam(t, R.Name("ScavengerTeam.Barricade2"), 1);
                }
            }
        }

        /// <summary>
        /// Capture (rules from the client, numbers ours). The RoomEntity entity carries Mode.DenyHolder (the owner),
        /// Mode.DenyTakeover (the team taking it over) and Mode.CaptureFinished (the team whose hero pressed the
        /// capture key, slot 19, set by the game's own Mode.OnCapture). The client shows "press
        /// to capture" only to teams that neither hold the room nor are taking it over, and "press to deny" to the
        /// holder while another team takes it over (MapKind.RoomPoint prompt rule).
        /// - A press by a team that doesn't own the room starts its takeover; bots press when they stand in it.
        /// - A press by the owner during a takeover denies it (the takeover ends, progress lost).
        /// - Progress runs at x(capturing heroes present), pauses while another team is present (contested), and
        ///   ends when no capturing hero is left in range.
        /// </summary>
        private void Rooms(float dt)
        {
            bool changed = false;
            object none = Neutral();
            foreach (Room r in _rooms)
            {
                var inRange = Players().Where(p => !BotDriver.IsDead(p) && Dist(ServerHooks.Position(p), r.At) <= CaptureReach).ToList();
                var teams = inRange.Select(TeamOf).Distinct().ToList();
                // Bots press the capture key, like a player would: after BotPressDelay in the room and at most once
                // per BotPressEvery, to take a room their team doesn't own or to deny a takeover of theirs.
                foreach (Bot b in Bots)
                {
                    string key = b.ClientIndex + ":" + r.Id;
                    if (!inRange.Contains(b.Player)) { _inRoomSince.Remove(key); continue; }
                    float since, next;
                    if (!_inRoomSince.TryGetValue(key, out since)) _inRoomSince[key] = since = _time;
                    _nextPress.TryGetValue(b, out next);
                    if (_time - since < BotPressDelay || _time < next) continue;
                    object team = TeamOf(b.Player);
                    bool takeOver = !team.Equals(r.Owner) && !team.Equals(r.Capturing);
                    bool deny = team.Equals(r.Owner) && !r.Capturing.Equals(none);
                    if ((takeOver || deny) && ServerHooks.GetFieldValue(r.Entity, R.Name("Mode.CaptureFinished")).Equals(none))
                    {
                        ServerHooks.SetField(r.Entity, R.Name("Mode.CaptureFinished"), team);
                        _nextPress[b] = _time + BotPressEvery;
                    }
                }
                object pressed = ServerHooks.GetFieldValue(r.Entity, R.Name("Mode.CaptureFinished"));
                if (!pressed.Equals(none))
                {
                    ServerHooks.SetField(r.Entity, R.Name("Mode.CaptureFinished"), none);
                    if (pressed.Equals(r.Owner) && !r.Capturing.Equals(none))
                    {
                        _log("scavenger: " + r.Name + ": " + pressed + " denied " + r.Capturing + "'s takeover");
                        r.Capturing = none; r.Progress = 0f; changed = true;
                    }
                    else if (!pressed.Equals(r.Owner) && !pressed.Equals(r.Capturing))
                    {
                        if (_logPresses++ < 40) _log("scavenger: " + r.Name + ": " + pressed + " starts capturing (owner " + r.Owner + ")" + (inRange.Contains(_human) ? " [human present]" : ""));
                        r.Capturing = pressed; r.Progress = 0f; changed = true;
                    }
                }
                r.Speed = 0;
                if (!r.Capturing.Equals(none))
                {
                    int capturers = inRange.Count(p => TeamOf(p).Equals(r.Capturing));
                    bool contested = teams.Any(t => !t.Equals(r.Capturing));
                    if (capturers == 0) { r.Capturing = none; r.Progress = 0f; changed = true; }
                    else if (contested) { r.Speed = -100; changed = true; }
                    else
                    {
                        r.Speed = 100 * capturers;
                        r.Progress = Math.Min(1f, r.Progress + dt * capturers / CaptureSeconds);
                        changed = true;
                        if (r.Progress >= 1f)
                        {
                            r.Owner = r.Capturing;
                            r.Capturing = none;
                            r.Progress = 0f;
                            _log("scavenger: " + r.Name + " captured by " + r.Owner);
                        }
                    }
                }
                ServerHooks.SetField(r.Entity, R.Name("Mode.DenyHolder"), r.Owner);
                ServerHooks.SetField(r.Entity, R.Name("Mode.DenyTakeover"), r.Capturing);
            }
            if (changed) SyncRooms();
        }

        private int _logPresses;
        /// <summary>Bots press capture this long after entering a room, and at most once per BotPressEvery (ours).</summary>
        public const float BotPressDelay = 1.5f, BotPressEvery = 5f;
        private readonly Dictionary<string, float> _inRoomSince = new Dictionary<string, float>();
        private readonly Dictionary<Bot, float> _nextPress = new Dictionary<Bot, float>();

        private static object TeamOf(object p) { return p.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(p, null); }

        private void Income(float time)
        {
            if (time < _nextIncome) return;
            _nextIncome = time + IncomeSeconds;
            foreach (TeamInfo t in _teams)
            {
                int owned = _rooms.Count(r => r.Owner.Equals(t.Id));
                if (owned == 0) continue;
                AddDelivered(t, owned * IncomePerRoom);
                _log("scavenger: income " + t.Name + " +" + owned * IncomePerRoom + " (" + owned + " rooms) -> " + Delivered(t));
            }
        }

        /// <summary>Looters around the rooms; a hoarder every few minutes at a hoarder spot (ScavengerState.Events type 1).</summary>
        private void Events(float time)
        {
            // A dead looter is replaced only after LooterEvery: replacing it at once took the same pooled object in
            // the same tick, and the client kept showing the dying looter at its last health (2026-10-07 playtest:
            // looters that "would not die" and hits that did nothing).
            if (_looters.RemoveAll(Destroyed) > 0) _nextLooter = Math.Max(_nextLooter, time + LooterEvery);
            if (time >= _nextLooter && _looters.Count < MaxLooters && _rooms.Count > 0)
            {
                _nextLooter = time + LooterEvery;
                Room r = _rooms[_random.Next(_rooms.Count)];
                double a = _random.NextDouble() * Math.PI * 2;
                try
                {
                    object l = _g.SpawnNpc(LooterType, r.At[0] + (float)Math.Cos(a) * 60f, r.At[1] + (float)Math.Sin(a) * 60f, GameRuntime.ZombieTeam, null);
                    ServerHooks.AggroRange[l] = 80f;
                    _looters.Add(l);
                }
                catch (Exception) { }
            }
            if (_hoarder != null && Destroyed(_hoarder))
            {
                RemoveEvent(_hoarderEventId);
                _log("scavenger: the hoarder is dead");
                _hoarder = null;
            }
            if (_hoarder == null && time >= _nextHoarder && _hoarderSpots.Count > 0)
            {
                _nextHoarder = time + HoarderEvery;
                float[] at = _hoarderSpots[_random.Next(_hoarderSpots.Count)];
                foreach (string type in HoarderTypes.OrderBy(x => _random.Next()))
                {
                    try { _hoarder = _g.SpawnNpc(type, at[0], at[1], GameRuntime.ZombieTeam, null); break; }
                    catch (Exception) { }
                }
                if (_hoarder == null) return;
                ServerHooks.AggroRange[_hoarder] = 120f;
                _hoarderEventId = _random.Next(1, 100000);
                AddEvent(_hoarderEventId, 1, at);
                _log("scavenger: hoarder " + ServerHooks.Describe(_hoarder) + " at " + Fmt(at));
            }
        }

        private void AddEvent(int id, int type, float[] at)
        {
            object s = State();
            IList list = (IList)s.GetType().GetField(R.Name("ScavengerState.Events"), All).GetValue(s);
            Type et = list.GetType().GetGenericArguments()[0];
            object e = Activator.CreateInstance(et);
            et.GetField(R.Name("ScavengerEvent.Id"), All).SetValue(e, id);
            et.GetField(R.Name("ScavengerEvent.Kind"), All).SetValue(e, type);
            et.GetField(R.Name("ScavengerEvent.Position"), All).SetValue(e, _g.Vector2(at[0], at[1]));
            list.Add(e);
            Store(s);
        }

        private void RemoveEvent(int id)
        {
            object s = State();
            IList list = (IList)s.GetType().GetField(R.Name("ScavengerState.Events"), All).GetValue(s);
            for (int i = list.Count - 1; i >= 0; i--)
                if ((int)list[i].GetType().GetField(R.Name("ScavengerEvent.Id"), All).GetValue(list[i]) == id) list.RemoveAt(i);
            Store(s);
        }

        /// <summary>Night at the first truck full; a team at the win target leads (ScavengerState.Leader) and wins after DenySeconds.</summary>
        private void EndGame(float time)
        {
            foreach (TeamInfo t in _teams)
                if (_fillTruckTeam == null && Delivered(t) >= TruckLoaded)
                {
                    _fillTruckTeam = t.Id;
                    _night = true;
                    SetGame(R.Name("ScavengerState.Night"), true);
                    _log("scavenger: " + t.Name + " truck full (" + Delivered(t) + "); night falls (end game)");
                }
            object leaderNow = _teams.Where(t => Delivered(t) >= Target).OrderByDescending(Delivered).Select(t => t.Id).FirstOrDefault();
            object leader = State().GetType().GetField(R.Name("ScavengerState.Leader"), All).GetValue(State());
            if (leaderNow == null)
            {
                if (!leader.Equals(Neutral())) { SetGame(R.Name("ScavengerState.Leader"), Neutral()); _leaderSince = -1f; _log("scavenger: lead denied"); }
                return;
            }
            if (!leader.Equals(leaderNow)) { SetGame(R.Name("ScavengerState.Leader"), leaderNow); _leaderSince = time; _log("scavenger: " + leaderNow + " reached " + Target + " and leads"); }
            else if (time - _leaderSince >= DenySeconds) Finish(leaderNow);
        }

        private void Finish(object winner)
        {
            _ended = true;
            _winner = winner;
            SetGame(R.Name("ScavengerState.Ended"), true);
            _mode.GetType().GetProperty("IsCompleted", All).SetValue(_mode, true, null);
            ServerHooks.SyncNow = true;   // IsCompleted in the same frame as team finished
            var ranking = _teams.OrderByDescending(Delivered).ToList();
            ranking.Remove(ranking.First(t => t.Id.Equals(winner)));
            ranking.Insert(0, _teams.First(t => t.Id.Equals(winner)));
            for (int i = 0; i < ranking.Count; i++)
            {
                int team = Convert.ToInt32(ranking[i].Id), placement = i;
                ServerHooks.SendMatchMessage(TeamFinishedMessage, m => { m.WriteBits((uint)team, 3); m.WriteBits((uint)placement, 3); m.Write(true); });
            }
            byte race = Convert.ToByte(_barricadeRaceTeam ?? Neutral()), fill = Convert.ToByte(_fillTruckTeam ?? Neutral()), win = Convert.ToByte(winner);
            ServerHooks.SendMatchMessage(MedalMessage, m => { m.Write(race); m.Write(fill); m.Write(win); });
            int mine = ranking.FindIndex(t => t.Name == R.Name("Team.One"));
            var boxes = new List<int> { MissionBox, TimeBox };
            if (mine == 0) boxes.Add(VictoryBox);
            if (_barricadeRaceTeam != null && Convert.ToInt32(_barricadeRaceTeam) == 1) boxes.Add(FirstBarricadeBox);
            if (_fillTruckTeam != null && Convert.ToInt32(_fillTruckTeam) == 1) boxes.Add(FirstEndGameBox);
            _log("scavenger: " + winner + " wins; delivered " + string.Join(", ", _teams.Select(t => t.Name + " " + Delivered(t)).ToArray()) +
                 "; medals race " + race + ", truck " + fill + ", win " + win + "; Team1 placement " + mine);
            foreach (Bot b in Bots) { b.Goal = null; b.Target = null; }
            if (OnFinished != null) OnFinished(mine, boxes.ToArray());
        }

        // ---- death and respawn (human and bots) ----

        private void Respawns(float time)
        {
            foreach (object p in Players())
            {
                if (!BotDriver.IsDead(p)) { _diedAt.Remove(p); continue; }
                float at;
                if (!_diedAt.TryGetValue(p, out at))
                {
                    _diedAt[p] = time;
                    try { Supplies().GetType().GetMethod(R.Name("Supplies.DropOnDeath"), All).Invoke(Supplies(), new[] { p }); } catch (Exception) { }
                    continue;
                }
                if (time - at < RespawnSeconds) continue;
                object team = p.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(p, null);
                float[] spawn = ServerHooks.Vector(_mode.GetType().GetMethod(R.Name("World.SpawnAt"), All).Invoke(_mode, new[] { team }));
                Bot respawning = Bots.FirstOrDefault(x => x.Player == p);
                if (respawning != null) spawn = BotRespawnPoint(team, spawn);
                object v = _g.Vector2(spawn[0], spawn[1]);
                p.GetType().GetField(R.Name("Map.Position"), All).SetValue(p, v);
                R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), All).Invoke(p, new object[] { v, true });
                p.GetType().GetMethod(R.Name("Entity.Revive"), All, null, Type.EmptyTypes, null).Invoke(p, null);
                _diedAt.Remove(p);
                Bot bot = Bots.FirstOrDefault(x => x.Player == p);
                if (bot != null) bot.RespawnedAt = time;
            }
        }

        private readonly Dictionary<object, float[]> _botRespawn = new Dictionary<object, float[]>();

        /// <summary>
        /// Where a bot respawns (ours): the waypoint node nearest the team's respawn point that lies in the
        /// network's main part. Respawn points on Resort sit in pockets the navmesh can't route out of; bots
        /// respawned there waited out the unstick timer and then blinked out (offline, 2026-10-07).
        /// </summary>
        private float[] BotRespawnPoint(object team, float[] spawn)
        {
            float[] at;
            if (_botRespawn.TryGetValue(team, out at)) return at;
            at = spawn;
            RouteGraph graph = BotDriver.Graph;
            if (graph != null && graph.Nodes.Count > 0)
            {
                int[] comp = graph.Components();
                int main = comp.GroupBy(c => c).OrderByDescending(gr => gr.Count()).First().Key;
                float[] node = Enumerable.Range(0, graph.Nodes.Count).Where(i => comp[i] == main)
                                         .Select(i => graph.Nodes[i]).OrderBy(n => Dist(n, spawn)).First();
                if (Dist(node, spawn) < 250f) at = node;
                _log("scavenger: " + team + " bots respawn at (" + at[0].ToString("0") + ", " + at[1].ToString("0") + "), the game's point is (" + spawn[0].ToString("0") + ", " + spawn[1].ToString("0") + ")");
            }
            _botRespawn[team] = at;
            return at;
        }

        /// <summary>
        /// No stagger from basic attacks between heroes (owner, 2026-10-07, from footage of the original's
        /// Scavenger: player basic attacks don't stun each other; abilities that stun still do). Every basic
        /// attack hit carries the knockback-stun buff Effect.KnockStun; on our server it staggered heroes hit by heroes,
        /// so bots stun-locked the human. Basic attacks are the weapon primaries, whose hits are the hit objects
        /// Hit.MeleePrimary (Ability.LightFirst, Ability.HeavyFirst, fists primary) and Hit.RangedPrimary (pistols primary, Ability.GunFirst, 142)
        /// (per the game's ability registrations); the buff is dropped when one of those applies it, hero to hero.
        /// Damage still lands; zombies still stagger heroes, and heroes still stagger zombies.
        /// </summary>
        private bool BlockStagger(object source, object target)
        {
            if (source == target || !IsHero(source) || !IsHero(target)) return false;
            string hit = ServerHooks.BasicAttackHitOnStack();
            if (hit == null) return false;
            if (_basicStaggerLogged++ < 3) _log("scavenger: basic-attack stagger dropped (" + hit + ") from " + HeroName(source) + " on " + HeroName(target));
            return true;
        }

        private int _basicStaggerLogged;

        private bool IsHero(object o) { return o == _human || Bots.Any(b => b.Player == o); }

        /// <summary>
        /// Zombies (looters, hoarders, ambient walkers) go for the nearest living hero of any team, bots included.
        /// The AI's default is the human only (owner, 2026-10-07: zombies ignored the bots). Recomputed every
        /// tick from positions, so a zombie switches to whoever comes closer.
        /// </summary>
        private object NearestHero(object npc, object human)
        {
            return ServerHooks.NearestTarget(npc, Players());
        }

        private int _friendlyLogged;

        /// <summary>Logs hero damage between teammates (the game's Entity.Hostile forbids it; watched since the 2026-10-07 playtest).</summary>
        private float WatchFriendlyFire(object victim, object changer, float value)
        {
            if (_friendlyLogged >= 20 || changer == null) return value;
            object root = ServerHooks.RootOwner(changer);
            if (root == null || root == victim) return value;
            var heroes = Players();
            if (heroes.Contains(root) && heroes.Contains(victim) && TeamOf(root).Equals(TeamOf(victim)))
            {
                _friendlyLogged++;
                _log("scavenger: FRIENDLY FIRE " + HeroName(root) + " hit " + HeroName(victim) + " (" + TeamOf(victim) + ") for " + value + " via " + ServerHooks.Describe(changer));
            }
            return value;
        }

        private string HeroName(object p)
        {
            if (p == _human) return "the human";
            Bot b = Bots.FirstOrDefault(x => x.Player == p);
            return b != null ? b.Name : ServerHooks.Describe(p);
        }

        private void OnKill(object victim, object killer)
        {
            if (Players().Contains(victim)) return;
            try { Supplies().GetType().GetMethod(R.Name("Supplies.DropFromKill"), All).Invoke(Supplies(), new[] { victim, killer }); }
            catch (Exception) { }
        }

        // ---- bots ----

        /// <summary>
        /// Our bot brain (no original exists): fight what is close (rival heroes within 45, zombies within 25);
        /// before the supply area, break the team's current barricade; after it, deliver when carrying enough
        /// (or anything at night), else take the nearest room the team doesn't own, else the hoarder, else
        /// hunt looters, else hold a room.
        /// </summary>
        private void Brain(Bot b, float time)
        {
            if (BotDriver.IsDead(b.Player)) return;
            TeamInfo t = _teams.First(x => x.Name == b.Team);
            float[] me = ServerHooks.Position(b.Player);
            object teamId = t.Id;
            object enemy = Players().Where(p => !BotDriver.IsDead(p) && !p.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(p, null).Equals(teamId) && Dist(ServerHooks.Position(p), me) <= 45f)
                                    .OrderBy(p => Dist(ServerHooks.Position(p), me)).FirstOrDefault()
                         ?? Zombies().Where(z => Dist(ServerHooks.Position(z), me) <= 25f).OrderBy(z => Dist(ServerHooks.Position(z), me)).FirstOrDefault();
            // A target the bot can't reach (behind a wall, in a spawn pose in the scenery) is dropped after
            // GiveUpSeconds and ignored for IgnoreSeconds (ours; four bots stood at an unreachable walker).
            if (enemy != null && b.Ignore.ContainsKey(enemy) && time < b.Ignore[enemy]) enemy = null;
            if (enemy != b.Target || enemy == null) { b.TargetSince = time; b.TargetHp = enemy == null ? 0f : ServerHooks.GetStat(enemy, R.Name("Stat.Health")); }
            else
            {
                // Progress means the target lost health; none for GiveUpSeconds (out of reach, or behind a wall
                // or barricade while in "range") and it is ignored for a while.
                float hp = ServerHooks.GetStat(enemy, R.Name("Stat.Health"));
                if (hp < b.TargetHp) { b.TargetHp = hp; b.TargetSince = time; }
                else if (time - b.TargetSince > GiveUpSeconds) { b.Ignore[enemy] = time + IgnoreSeconds; enemy = null; }
            }
            b.Target = enemy;
            if (t.Phase < 2 && t.BarricadeAt < t.Barricades.Count)
            {
                List<object> group = t.Barricades[t.BarricadeAt];
                object piece = group.Where(x => !Destroyed(x)).OrderBy(x => Dist(Pos(x), me)).FirstOrDefault();
                if (piece != null)
                {
                    b.Goal = t.Approach[t.BarricadeAt];
                    if (enemy == null && Dist(me, b.Goal) <= 20f) b.Target = piece;
                }
                return;
            }
            int carried = Carried(b.Player);
            if (t.Truck != null && carried > 0 && (carried >= BotDeliverAt || _night))
            {
                b.Goal = t.Truck;
                bool there = Dist(me, t.Truck) <= 18f;
                BotDriver.Tap(b, DeliverSlot, there && ((int)(time * 2) % 2 == 0));
                return;
            }
            Room target = _rooms.Where(r => !r.Owner.Equals(teamId)).OrderBy(r => Dist(r.At, me)).FirstOrDefault();
            if (target != null && (b.ClientIndex % 2 == 0 || _rooms.Count(r => r.Owner.Equals(teamId)) == 0)) { b.Goal = target.At; return; }
            if (_hoarder != null && !Destroyed(_hoarder)) { b.Goal = Pos(_hoarder); if (enemy == null && Dist(Pos(_hoarder), me) < 60f) b.Target = _hoarder; return; }
            object looter = _looters.Where(l => !Destroyed(l)).OrderBy(l => Dist(Pos(l), me)).FirstOrDefault();
            if (looter != null) { b.Goal = Pos(looter); if (enemy == null && Dist(Pos(looter), me) < 60f) b.Target = looter; return; }
            Room hold = _rooms.Where(r => r.Owner.Equals(teamId)).OrderBy(r => Dist(r.At, me)).FirstOrDefault() ?? target;
            b.Goal = hold == null ? t.Truck : hold.At;
        }

        // ---- helpers ----

        private IEnumerable<object> Players() { return new[] { _human }.Concat(Bots.Select(b => b.Player)); }

        private readonly List<object> _zombies = new List<object>();

        private IEnumerable<object> Zombies() { return _zombies; }

        private object Supplies() { return _mode.GetType().GetProperty(R.Name("Mode.Supplies"), All).GetValue(_mode, null); }

        private int Delivered(TeamInfo t) { return (int)Supplies().GetType().GetMethod(R.Name("Supplies.Delivered"), All).Invoke(Supplies(), new[] { t.Id }); }

        private void AddDelivered(TeamInfo t, int amount)
        {
            MethodInfo add = Supplies().GetType().GetMethod(R.Name("Supplies.AddDelivered"), All);
            add.Invoke(Supplies(), new[] { t.Id, (object)amount, Activator.CreateInstance(add.GetParameters()[2].ParameterType) });
        }

        private int Carried(object p)
        {
            ushort idx = (ushort)p.GetType().GetProperty(R.Name("Entity.HeroIndex"), All).GetValue(p, null);
            return (int)Supplies().GetType().GetMethod(R.Name("Supplies.Carried"), All).Invoke(Supplies(), new object[] { (int)idx });
        }

        public int DeliveredTeam1() { return Delivered(_teams[0]); }

        private object Neutral() { return Enum.Parse(_human.GetType().GetProperty(R.Name("Entity.Team"), All).PropertyType, R.Name("Team.None")); }

        private static float[] Start(TeamInfo t) { return t.Start == null ? new float[] { 0, 0 } : new[] { t.Start.X, t.Start.Y }; }

        private static float[] Pos(object o) { return ServerHooks.Position(o); }

        private static bool Destroyed(object o) { return BotDriver.Gone(o) || ServerHooks.GetStat(o, R.Name("Stat.Health")) <= 1f && o.GetType().Name == R.Short("Barrier"); }

        private static object GetField(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static float Dist(float[] a, float[] b) { return BotDriver.Dist(a, b); }

        private static string Fmt(float[] p) { return p == null ? "none" : "(" + p[0].ToString("0") + ", " + p[1].ToString("0") + ")"; }
    }
}

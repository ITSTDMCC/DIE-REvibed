using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>A virtual game method the emitted server class overrides.</summary>
    public sealed class HookedMethod
    {
        public MethodInfo Method;
        public string Key;
        public bool CallBaseFirst;
    }

    /// <summary>A weapon as the match server hands it to the game.</summary>
    public sealed class Weapon
    {
        public byte[] Guid;
        public ushort SchematicId;
        public ulong UserId;
        public int Durability = 100;
        public int Ep;
    }

    /// <summary>A player spawn point or teleport point found in the loaded map.</summary>
    public sealed class SpawnPoint
    {
        public string Kind;
        public string Team;
        public bool IsStart;
        public float X, Y;

        public override string ToString()
        {
            return Kind + ": Team " + Team + (Kind == R.Name("MapKind.HeroStart") ? ", IsStart " + IsStart : "") + ", Position (" + X + ", " + Y + ")";
        }
    }

    /// <summary>
    /// Runs the match client's own game logic as the match server. The game's
    /// libraries are loaded by reflection from the owner's install at run time
    /// (nothing is copied), and only work under 32-bit Mono. Every call into the
    /// game must happen on one thread: the game keeps its World in a
    /// thread-static field.
    /// </summary>
    public sealed class GameRuntime
    {
        public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        public const int TutorialMap = 4;
        /// <summary>The match client's log prints "ModeKind = 5" for the tutorial.</summary>
        public const int TutorialGameModeType = 5;
        /// <summary>Practice (the hub's solo Scout missions): game mode type 9, maps 15-17 (Maps index).</summary>
        public const int ScoutMissionGameModeType = 9;
        public const int ScoutOutpostMap = 15, ScoutLabMap = 16, ScoutClubMap = 17;
        /// <summary>Horde (game mode type 4, maps 5-7) and Scavenger (ScavengeRules, type 6, maps 1, 2, 3, 8, 14).</summary>
        public const int HordeGameModeType = 4, ScavengerHuntGameModeType = 6;
        public const int MaxPlayers = 12, MaxObservers = 4;
        public const float FpsLimit = 30f;
        /// <summary>
        /// The match's infection (server) level: World.MatchLevel on the server and MatchInfo.MatchLevel in
        /// the hail ("Infection Level N" in the client). Scales NPC stats (get infection level modifiers).
        /// Set by the match host before a match is built; 1 until then.
        /// </summary>
        public int MatchLevel = 1;

        /// <summary>World.InfectionScale(MatchLevel, map): the zombie stat multiplier for the level.</summary>
        public float InfectionModifier()
        {
            MethodInfo m = R.Type("Type.Crafting").GetMethod(R.Name("World.InfectionScale"), All);
            return (float)m.Invoke(null, new[] { (object)MatchLevel, Enum.ToObject(m.GetParameters()[1].ParameterType, MapIndex) });
        }

        private readonly string _install;
        private Assembly _game, _crafting;

        public Type WorldType, NetworkType;
        public object World, Network, Tutorial;
        /// <summary>The map (Maps index) and game mode type to load; set before Start. The tutorial by default.</summary>
        public int MapIndex = TutorialMap, ModeKind = TutorialGameModeType;
        /// <summary>The character cached and played (HeroId name), and the MatchInfo difficulty (horde mode type: 0 Easy).</summary>
        public string Character;

        /// <summary>The hero the tutorial uses, and the default before a loadout is queued: HeroId value 7.</summary>
        public const int DefaultHero = 7;

        /// <summary>The HeroId name of a hero number, read from the installed game.</summary>
        public string HeroName(int hero)
        {
            Type e = WorldType != null
                ? WorldType.GetMethod(R.Name("World.Cache"), All).GetParameters()[1].ParameterType.GetGenericArguments()[0]
                : _crafting.GetType(R.Name("Type.HeroId"), true);
            return Enum.GetName(e, hero);
        }
        /// <summary>
        /// More characters to cache and announce in the hail (MatchInfo.HeroCache): bot heroes in Scavenger. Player
        /// pools come only from this list, and the client computes the same counts from it.
        /// </summary>
        public readonly List<string> ExtraCharacters = new List<string>();
        /// <summary>Client slots driven by server-side bots (Bot), for the client-info update and the controller section.</summary>
        public readonly List<int> BotClients = new List<int>();

        /// <summary>The played character first, then the extra ones, without repeats.</summary>
        public List<string> CachedCharacters()
        {
            var list = new List<string> { Character };
            foreach (string c in ExtraCharacters) if (!list.Contains(c)) list.Add(c);
            return list;
        }
        public int Difficulty;
        /// <summary>The game mode instance added for ModeKind (the tutorial mode is also in Tutorial).</summary>
        public object Mode;
        public string MapName;
        public readonly List<SpawnPoint> SpawnPoints = new List<SpawnPoint>();

        private GameRuntime(string install) { _install = install; }

        public string Install { get { return _install; } }

        /// <summary>How long fingerprinting the game's libraries took at load (for the log).</summary>
        public long ResolveMillis;

        /// <summary>Loads the match client's libraries from the install's Dead Island Epidemic_Data\Managed.</summary>
        public static GameRuntime Load(string install)
        {
            string managed = Path.Combine(install, @"Dead Island Epidemic_Data\Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            GameRuntime r = new GameRuntime(install);
            Assembly core = Assembly.LoadFrom(Path.Combine(managed, "StunCore.dll"));
            r._crafting = Assembly.LoadFrom(Path.Combine(managed, "ConductorCrafting.dll"));
            r._game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
            // The game's obfuscated types and members are found by role (Resolve/RoleTable), not by name.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Resolve.R.Init(new[] { core, r._crafting, r._game });
            List<string> missing = Resolve.R.Check();
            if (missing.Count > 0)
                throw new Exception("this copy of the game doesn't match DIE: Revibed's role table (" + missing.Count + " roles not found, first: " +
                                    missing[0] + "). The server supports the final Steam build, 0.8.5.38860.");
            r.ResolveMillis = clock.ElapsedMilliseconds;
            r.Character = r.HeroName(DefaultHero);
            return r;
        }

        /// <summary>Sends the game's own log (Log) to a callback.</summary>
        public void CaptureGameLog(Action<string> line)
        {
            Type log = R.Type("Type.Log");
            log.GetMethod(R.Name("Log.AddStream"), All).Invoke(null, new object[] { new GameLogStream(line) });
        }

        /// <summary>The game's logger buffers per thread; flush the calling thread's buffer.</summary>
        public void FlushGameLog()
        {
            R.Type("Type.Log").GetMethod(R.Name("Log.Flush"), All).Invoke(null, null);
        }

        /// <summary>A type from the match client's ConductorCrafting.dll (rewards, account levels, items).</summary>
        public Type Crafting(string name)
        {
            Type t = _crafting.GetType(name);
            if (t == null) throw new Exception("type " + name + " not found in the crafting library");
            return t;
        }

        public Type Game(string name)
        {
            Type t = _game.GetType(name);
            if (t == null) throw new Exception("type " + name + " not found in Assembly-CSharp");
            return t;
        }

        /// <summary>
        /// Builds a server World and Network, adds the tutorial game
        /// mode and loads its map. The working directory must be the install
        /// folder: the map loader opens "Maps\" relative to it.
        /// </summary>
        public void Start(Action<string> log)
        {
            SpawnPoints.Clear();
            BotClients.Clear();
            // Crafting data: the client ships it as the Unity text asset
            // "GameCraftingData" in resources.assets; read it into memory only.
            string assets = Path.Combine(_install, @"Dead Island Epidemic_Data\resources.assets");
            byte[] craftingXml = ReadTextAsset(assets, "GameCraftingData");
            if (craftingXml == null) throw new Exception("no GameCraftingData text asset in " + assets);
            Stream craftingData = new MemoryStream(craftingXml, false);
            log("roles: " + R.Count + " resolved by fingerprint in " + ResolveMillis + " ms");
            log("crafting data: " + craftingXml.Length + " bytes from the GameCraftingData text asset (in memory)");
            R.Type("GameDataLoader").GetMethod("Load", All).Invoke(null, new object[] { _install });

            WorldType = R.Type("Type.World");
            object view = Activator.CreateInstance(R.Type("ViewManagerStub"), new object[] { true });
            // The client build's server hooks are empty virtuals; ours forward to ServerHooks.
            ServerHooks.Init(this);
            World = Activator.CreateInstance(Subclass(WorldType, "ServerGameManager", new[] { "get_IsServer" }, ServerHooks.Methods(WorldType)),
                view, Path.Combine(_install, R.Name("Maps.List")), "", craftingData, false, null);
            WorldType.GetField(R.Name("World.ThreadCurrent"), All).SetValue(null, World);

            NetworkType = R.Type("Type.Network");
            Type clientType = R.Type("Type.Peer");
            Delegate createClient = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(int), clientType), World,
                WorldType.GetMethod(R.Name("Network.NewClient"), All));
            Network = Activator.CreateInstance(Subclass(NetworkType, "ServerNetworkBase", new string[0]), MaxPlayers, MaxObservers, FpsLimit, createClient);

            if (ModeKind == TutorialGameModeType) Mode = Tutorial = Activator.CreateInstance(R.Type("TutorialMode"));
            else if (ModeKind == ScoutMissionGameModeType) Mode = Activator.CreateInstance(R.Type("Type.PracticeRules"));
            else if (ModeKind == HordeGameModeType) Mode = Activator.CreateInstance(R.Type("Type.WaveRules"));
            else if (ModeKind == ScavengerHuntGameModeType) Mode = Activator.CreateInstance(R.Type("Type.ScavengeRules"));
            else throw new Exception("game mode type " + ModeKind + " is not supported");
            WorldType.GetMethod("Initialize", All).Invoke(World, new[] { Network, null });
            // NPC stats are scaled by the server level and modifier, which start at 0
            // (zombies would spawn with 0 health). NPC stats use Hero.ServerMod x get enemy modifier
            // (get infection level modifiers); nothing in the client sets Hero.ServerMod past
            // Reset's 1. Inference: the server set it to World.InfectionScale(MatchLevel, map),
            // the infection level increase entry for the level, because the client's own walker-health readout
            // (get walker health) is that same product x get enemy modifier x 100.
            WorldType.GetField(R.Name("World.Level"), All).SetValue(World, MatchLevel);
            WorldType.GetField(R.Name("Hero.ServerMod"), All).SetValue(World, InfectionModifier());
            MethodInfo addGameMode = WorldType.GetMethod(R.Name("World.SetMode"), All);
            addGameMode.Invoke(World, new[] { Enum.ToObject(addGameMode.GetParameters()[0].ParameterType, ModeKind), Mode });

            Type mapManager = R.Type("Type.Maps");
            mapManager.GetMethod("Load", All, null, new[] { typeof(int) }, null).Invoke(null, new object[] { MapIndex });
            MethodInfo cacheGame = WorldType.GetMethod(R.Name("World.Cache"), All);
            Type characterEnum = cacheGame.GetParameters()[1].ParameterType.GetGenericArguments()[0];
            List<string> cached = CachedCharacters();
            Array characters = Array.CreateInstance(characterEnum, cached.Count);
            for (int i = 0; i < cached.Count; i++) characters.SetValue(Enum.Parse(characterEnum, cached[i]), i);
            // The pool sizes depend on the difficulty (World.cs, the Horde NPC table in -z/-y.cs), so it
            // is set before World.Cache, as the hail's MatchInfo.Difficulty tells the client.
            FieldInfo difficulty = WorldType.GetField(R.Name("World.Difficulty"), All);
            difficulty.SetValue(World, Enum.ToObject(difficulty.FieldType, Difficulty));
            cacheGame.Invoke(World, new object[] { MapIndex, characters, false });
            MapName = (string)mapManager.GetMethod(R.Name("Maps.Name"), All).Invoke(null, new object[] { MapIndex });
            object settings = R.Type("Type.Mode").GetMethod(R.Name("Maps.Read"), All)
                .Invoke(null, new object[] { MapName, Path.Combine(_install, R.Name("Maps.List")) });
            WorldType.GetMethod(R.Name("World.Load"), All).Invoke(World, new[] { settings, (object)MapIndex });
            log("loaded map " + MapIndex + " (" + MapName + "), game mode type " + ModeKind + ", SyncCount " + SyncCount);

            // The game mode's action interactions (role ActiveMode.ActionInteractions) start off on the server: ActiveMode.Reset turns
            // them on, but our server never runs it. Synced off, the client never offers an action key
            // prompt, so X (capture, deliver) did nothing while G (a plain ability key) worked.
            // Mode.ActionsEnable is server-only code in the client (no caller there).
            object mode = ActiveMode;
            mode.GetType().GetMethod(R.Name("Mode.ActionsEnable"), All).Invoke(mode, new object[] { true });
            log("action interactions on (" + ServerHooks.GetFieldValue(mode, R.Name("Mode.ActionsOn")) + ")");

            // ability manager (role ability manager) is only set by the client when it
            // connects; buffs such as the supply-carry buff read it and threw on the server. Use the static
            // balance data the rest of the game reads (World.Balance).
            FieldInfo abilityManager = WorldType.GetField(R.Name("World.Skills"), All);
            if (abilityManager.GetValue(World) == null)
            {
                abilityManager.SetValue(World, WorldType.GetField(R.Name("World.Balance"), All).GetValue(null));
                log("ability manager set to the static World.Balance");
            }

            foreach (object o in (IEnumerable)WorldType.GetProperty(R.Name("Map.All"), All).GetValue(World, null))
            {
                Type t = o.GetType();
                // Horde maps have no player spawn point: the players start at the MapKind.Checkpoint marked Activated.
                if (t.Name != R.Name("MapKind.HeroStart") && t.Name != R.Name("MapKind.CutsceneSpot") && t.Name != R.Name("MapKind.Checkpoint")) continue;
                object pos = t.GetProperty("Position").GetValue(o, null);
                SpawnPoint sp = new SpawnPoint();
                sp.Kind = t.Name;
                FieldInfo team = t.GetField(R.Name("Record.Team"));
                sp.Team = team == null ? R.Name("Team.One") : team.GetValue(o).ToString();
                sp.IsStart = t.Name == R.Name("MapKind.HeroStart") ? (bool)t.GetField(R.Name("Msg.Starting")).GetValue(o)
                           : t.Name == R.Name("MapKind.Checkpoint") && Convert.ToBoolean(MapValue(o, "Activated"));
                sp.X = (float)pos.GetType().GetField("X").GetValue(pos);
                sp.Y = (float)pos.GetType().GetField("Y").GetValue(pos);
                SpawnPoints.Add(sp);
            }
        }

        /// <summary>
        /// One line per map object: index, full class name, Position, and every public
        /// field or property of a simple type (number, bool, enum, string, Vector2/3);
        /// links to other map objects are shown as their index.
        /// </summary>
        public List<string> DescribeMapObjects()
        {
            List<object> objects = ((IEnumerable)WorldType.GetProperty(R.Name("Map.All"), All).GetValue(World, null)).Cast<object>().ToList();
            Dictionary<object, int> index = new Dictionary<object, int>();
            for (int i = 0; i < objects.Count; i++) if (!index.ContainsKey(objects[i])) index[objects[i]] = i;
            Type mapObject = R.Type("Type.MapThing");
            List<string> lines = new List<string>();
            for (int i = 0; i < objects.Count; i++)
            {
                object o = objects[i];
                Type t = o.GetType();
                List<string> parts = new List<string> { i.ToString(), t.FullName, "Position " + t.GetProperty("Position").GetValue(o, null) };
                foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance))
                {
                    Type mt;
                    FieldInfo f = m as FieldInfo;
                    PropertyInfo p = m as PropertyInfo;
                    if (f != null) mt = f.FieldType;
                    else if (p != null && p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "Position") mt = p.PropertyType;
                    else continue;
                    bool simple = mt.IsPrimitive || mt.IsEnum || mt == typeof(string) || mt.Name == "Vector2" || mt.Name == "Vector3";
                    bool link = mapObject.IsAssignableFrom(mt);
                    if (!simple && !link) continue;
                    object v;
                    try { v = f != null ? f.GetValue(o) : p.GetValue(o, null); }
                    catch (Exception) { continue; }
                    string text = v == null ? "null"
                                : link ? (index.ContainsKey(v) ? "->" + index[v] : "->?")
                                : mt == typeof(string) ? "'" + v + "'"
                                : v.ToString();
                    parts.Add(m.Name + "=" + text);
                }
                lines.Add(string.Join(" | ", parts.ToArray()));
            }
            return lines;
        }

        public int SyncCount
        {
            get { return (int)NetworkType.GetProperty(R.Name("Network.SyncCount"), All).GetValue(Network, null); }
        }

        /// <summary>Spawn choice: a Team1 spawn point, else one marked IsStart, else the first; with none, the first teleport point.</summary>
        public SpawnPoint PickSpawn()
        {
            List<SpawnPoint> spawns = SpawnPoints.Where(s => s.Kind == R.Name("MapKind.HeroStart")).ToList();
            return spawns.FirstOrDefault(s => s.Team == R.Name("Team.One") && s.IsStart) ?? spawns.FirstOrDefault(s => s.Team == R.Name("Team.One")) ?? spawns.FirstOrDefault(s => s.IsStart) ?? spawns.FirstOrDefault()
                ?? SpawnPoints.FirstOrDefault(s => s.Kind == R.Name("MapKind.CutsceneSpot"))
                ?? SpawnPoints.FirstOrDefault(s => s.Kind == R.Name("MapKind.Checkpoint") && s.IsStart);
        }

        public object GetPlayer(int index) { return WorldType.GetMethod(R.Name("World.HeroAt"), All).Invoke(World, new object[] { index }); }

        public object GetClient(int index)
        {
            return NetworkType.GetMethod(R.Name("Network.ClientAt"), All, null, new[] { typeof(int) }, null).Invoke(Network, new object[] { index });
        }

        public object GetGameObject(int index) { return WorldType.GetMethod(R.Name("World.Object"), All).Invoke(World, new object[] { index }); }

        /// <summary>The synchronizable registered at a network index.</summary>
        public object GetSynchronizable(int index)
        {
            IList list = (IList)NetworkType.GetField(R.Name("Network.SyncList"), All).GetValue(Network);
            object data = index < list.Count ? list[index] : null;
            return data == null ? null : data.GetType().GetField(R.Name("SyncData.Payload"), All).GetValue(data);
        }

        /// <summary>
        /// The client info the server keeps for a player: camera direction (the
        /// player can't move without it), name, level, team and the two weapons.
        /// Fields by role: client-info.Camera, .Name, .Melee and .Ranged (the weapon uniques),
        /// .Level and .Team. Team may be a
        /// Entity.Team name or its number.
        /// </summary>
        public object BuildClientInfoData(float cameraX, float cameraY, string name, string team, int level, Weapon melee, Weapon ranged,
                                          IList<Protocol.OwnedGadget> gadgets = null, ulong userId = 0)
        {
            Type t = R.Type("Type.ClientCard");
            object info = Activator.CreateInstance(t);
            t.GetField(R.Name("Client.Camera"), All).SetValue(info, Vector2(cameraX, cameraY));
            t.GetField(R.Name("Client.Name"), All).SetValue(info, name);
            t.GetField(R.Name("Client.Level"), All).SetValue(info, level);
            FieldInfo teamField = t.GetField(R.Name("Client.Team"), All);
            teamField.SetValue(info, Enum.Parse(teamField.FieldType, team));
            if (melee != null) t.GetField(R.Name("Client.Melee"), All).SetValue(info, Unique(t.GetField(R.Name("Client.Melee"), All).FieldType, melee));
            if (ranged != null) t.GetField(R.Name("Client.Ranged"), All).SetValue(info, Unique(t.GetField(R.Name("Client.Ranged"), All).FieldType, ranged));
            // The two gadget (trinket) slots (the client equips them from these when it connects).
            string[] slots = { R.Name("Client.Gadget1"), R.Name("Client.Gadget2") };
            for (int i = 0; gadgets != null && i < gadgets.Count && i < slots.Length; i++)
            {
                FieldInfo f = t.GetField(slots[i], All);
                object g = Activator.CreateInstance(f.FieldType);
                f.FieldType.GetField("GUID").SetValue(g, gadgets[i].Guid);
                f.FieldType.GetField(R.Name("Craft.GadgetKey")).SetValue(g, gadgets[i].GadgetId);
                f.FieldType.GetField(R.Name("Item.Owner")).SetValue(g, userId);
                f.FieldType.GetField(R.Name("Item.Held")).SetValue(g, true);
                f.FieldType.GetField(R.Name("Entity.StatBlock")).SetValue(g, gadgets[i].Stats);
                f.SetValue(info, g);
            }
            return info;
        }

        private static object Unique(Type uniqueType, Weapon w)
        {
            object u = Activator.CreateInstance(uniqueType);
            uniqueType.GetField("Guid").SetValue(u, w.Guid);
            uniqueType.GetField(R.Name("Craft.Blueprint")).SetValue(u, w.SchematicId);
            uniqueType.GetField(R.Name("Item.Owner")).SetValue(u, w.UserId);
            uniqueType.GetField(R.Name("Item.Held")).SetValue(u, true);
            uniqueType.GetField(R.Name("Item.Wear")).SetValue(u, w.Durability);
            uniqueType.GetField(R.Name("Item.Points")).SetValue(u, w.Ep);
            uniqueType.GetField(R.Name("Item.Sockets")).SetValue(u, new byte[0]);
            return u;
        }

        /// <summary>
        /// Reads the client's connect hail with the game's own readers: a flag,
        /// Auth (version, session ticket), three flags (seen as
        /// false, false, true), then Net.Connection. Returns that last
        /// object, or null if the hail doesn't have this shape.
        /// </summary>
        public object ReadClientConnectionData(byte[] clientHail)
        {
            GameBuffer b = GameBuffer.Wrap(clientHail);
            if (!b.ReadBool()) return null;
            object auth = Activator.CreateInstance(R.Type("Type.Auth"));
            auth.GetType().GetMethod("Deserialize", All).Invoke(auth, new[] { b.Buffer });
            b.ReadBool();
            b.ReadBool();
            if (!b.ReadBool()) return null;
            object data = Activator.CreateInstance(R.Type("Type.Connection"));
            data.GetType().GetMethod("Deserialize", All).Invoke(data, new[] { b.Buffer });
            return data;
        }

        /// <summary>Camera direction of a Net.Connection.</summary>
        public static float[] Camera(object data)
        {
            object v = data.GetType().GetField(R.Name("ClientConnection.Camera"), All).GetValue(data);
            return new[] { (float)v.GetType().GetField("X").GetValue(v), (float)v.GetType().GetField("Y").GetValue(v) };
        }

        public void SetCamera(object clientInfoData, float x, float y)
        {
            clientInfoData.GetType().GetField(R.Name("Client.Camera"), All).SetValue(clientInfoData, Vector2(x, y));
        }

        /// <summary>
        /// World.OnStatChange with the arguments the game's own barricade code uses
        /// (Barricade.SetHealth / MakeVulnerable): operation 3 sets the stat to the value, the entity is its
        /// own changer.
        /// </summary>
        public void SetStat(object entity, string stat, float value) { ChangeStat(entity, stat, 3, value); }

        /// <summary>World.OnStatChange with a given operation (0 adds the value, 3 sets it).</summary>
        public void ChangeStat(object entity, string stat, int operation, float value, object changer = null)
        {
            MethodInfo change = WorldType.GetMethods(All).First(m => m.Name == R.Name("Hook.StatChange") && m.GetParameters().Length == 12);
            ParameterInfo[] ps = change.GetParameters();
            object[] args = new object[12];
            args[0] = entity;
            args[1] = Enum.Parse(ps[1].ParameterType, stat);
            args[2] = Enum.ToObject(ps[2].ParameterType, operation);
            args[3] = value;
            args[4] = Enum.ToObject(ps[4].ParameterType, 0);
            args[5] = changer ?? entity;
            args[6] = Enum.ToObject(ps[6].ParameterType, 1);
            args[7] = false;
            args[8] = true;
            args[9] = ps[9].ParameterType.IsEnum ? Enum.ToObject(ps[9].ParameterType, 0) : (ps[9].ParameterType.IsValueType ? Activator.CreateInstance(ps[9].ParameterType) : null);
            args[10] = true;
            args[11] = true;
            change.Invoke(World, args);
        }

        /// <summary>Sets a bool field of the tutorial game mode itself, by role (e.g. TutorialMode.SwapWeaponsAllowed).</summary>
        public void SetTutorialFlag(string role, bool value)
        {
            R.Type("TutorialMode").GetField(R.Name(role), All).SetValue(Tutorial, value);
        }

        /// <summary>Reads an int field of the tutorial state struct, by role.</summary>
        public int GetTutorialField(string role)
        {
            object state = R.Type("TutorialMode").GetField(R.Name("TutorialMode.State"), All).GetValue(Tutorial);
            return (int)state.GetType().GetField(R.Name(role), All).GetValue(state);
        }

        /// <summary>A map object's field or property by name (null if it has neither).</summary>
        public static object MapValue(object mapObject, string name)
        {
            Type t = mapObject.GetType();
            FieldInfo f = t.GetField(name, All);
            if (f != null) return f.GetValue(mapObject);
            PropertyInfo p = t.GetProperty(name, All);
            return p == null ? null : p.GetValue(mapObject, null);
        }

        /// <summary>Sets an int field of the tutorial state struct, by role (e.g. the hint counters).</summary>
        public void SetTutorialField(string role, int value)
        {
            FieldInfo holder = R.Type("TutorialMode").GetField(R.Name("TutorialMode.State"), All);
            object state = holder.GetValue(Tutorial);
            state.GetType().GetField(R.Name(role), All).SetValue(state, value);
            holder.SetValue(Tutorial, state);
        }

        /// <summary>
        /// The infection level for a loadout, as the live game set it from character strength (patch notes v0.6 and
        /// v0.7, docs/patch-history.md): HeroTuning.Strength(character, account level,
        /// melee schematic, ranged schematic, gadgets), then World.Infection(strength).
        /// Returns the level and the strength.
        /// </summary>
        public int InfectionLevelFor(string character, int accountLevel, ushort melee, ushort ranged, IList<Protocol.OwnedGadget> gadgets, ulong userId, out int strength)
        {
            Type cm = R.Type("Type.Crafting");
            Type scv = R.Type("Type.HeroTuning");
            MethodInfo calc = scv.GetMethod(R.Name("HeroTuning.Strength"), All);
            ParameterInfo[] ps = calc.GetParameters();
            MethodInfo schematic = cm.GetMethod(R.Name("Craft.Weapon"), All);
            Type gadgetType = R.Type("Type.Gadget");
            IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(gadgetType));
            if (gadgets != null)
                foreach (Protocol.OwnedGadget og in gadgets)
                {
                    object g = Activator.CreateInstance(gadgetType);
                    gadgetType.GetField("GUID").SetValue(g, og.Guid);
                    gadgetType.GetField(R.Name("Craft.GadgetKey")).SetValue(g, og.GadgetId);
                    gadgetType.GetField(R.Name("Item.Owner")).SetValue(g, userId);
                    gadgetType.GetField(R.Name("Item.Held")).SetValue(g, true);
                    gadgetType.GetField(R.Name("Entity.StatBlock")).SetValue(g, og.Stats);
                    list.Add(g);
                }
            strength = (int)calc.Invoke(null, new[] { Enum.Parse(ps[0].ParameterType, character), (object)accountLevel,
                melee == 0 ? null : schematic.Invoke(null, new object[] { (int)melee }), ranged == 0 ? null : schematic.Invoke(null, new object[] { (int)ranged }), list });
            return (int)cm.GetMethod(R.Name("World.Infection"), All).Invoke(null, new object[] { strength });
        }

        /// <summary>The game info sent at the end of the hail.</summary>
        public object BuildGameInfo(int matchLevel, int serverId, float maxMatchLength)
        {
            Type t = R.Type("Type.MatchInfo");
            object info = Activator.CreateInstance(t);
            t.GetField(R.Name("World.MapNumber")).SetValue(info, MapIndex);
            t.GetField(R.Name("World.Level")).SetValue(info, matchLevel);
            t.GetField(R.Name("MatchInfo.Server")).SetValue(info, serverId);
            t.GetField(R.Name("MatchInfo.MaxLength")).SetValue(info, maxMatchLength);
            // Scavenger's targets: the new end game (truck full + win, as game mode settings defaults on the server)
            // and no target modifier; other modes ignore both.
            t.GetField(R.Name("MatchInfo.NewEnding")).SetValue(info, true);
            t.GetField(R.Name("MatchInfo.SupplyGoalScale")).SetValue(info, 1f);
            FieldInfo difficulty = t.GetField(R.Name("MatchInfo.Difficulty"));
            difficulty.SetValue(info, Enum.ToObject(difficulty.FieldType, Difficulty));
            FieldInfo cache = t.GetField(R.Name("MatchInfo.HeroCache"));
            List<string> cachedNames = CachedCharacters();
            Array characters = Array.CreateInstance(cache.FieldType.GetElementType(), cachedNames.Count);
            for (int i = 0; i < cachedNames.Count; i++) characters.SetValue(Enum.Parse(cache.FieldType.GetElementType(), cachedNames[i]), i);
            cache.SetValue(info, characters);
            return info;
        }

        public object ActiveMode { get { return WorldType.GetProperty(R.Name("World.Mode"), All).GetValue(World, null); } }

        /// <summary>The game mode's supplies object; null for modes without one (WaveRules has no Mode.Supplies).</summary>
        public object Supplies
        {
            get
            {
                PropertyInfo p = Mode.GetType().GetProperty(R.Name("Mode.Supplies"), All);
                return p == null ? null : p.GetValue(Mode, null);
            }
        }

        public int SynchronizableIndex(object o)
        {
            return (int)NetworkType.GetMethod(R.Name("Network.SyncIndex"), All).Invoke(Network, new[] { o });
        }

        public object Vector2(float x, float y) { return Activator.CreateInstance(Game("FarseerPhysics.Common.Vector2"), x, y); }

        /// <summary>
        /// The tutorial stage (role TutorialState.Stage, an enum: 0 intro, 1 wake up,
        /// 2 "Move" guide, 3 weapon guide, ...) in the tutorial mode's state struct
        /// (TutorialMode.State). It reaches the client in the ActiveMode sync.
        /// </summary>
        public int TutorialStage
        {
            get
            {
                if (Tutorial == null) return 0;   // not the tutorial
                object state = R.Type("TutorialMode").GetField(R.Name("TutorialMode.State"), All).GetValue(Tutorial);
                return Convert.ToInt32(state.GetType().GetField(R.Name("TutorialState.Stage"), All).GetValue(state));
            }
            set
            {
                if (Tutorial == null) return;
                FieldInfo holder = R.Type("TutorialMode").GetField(R.Name("TutorialMode.State"), All);
                object state = holder.GetValue(Tutorial);   // a boxed copy of the struct
                FieldInfo stage = state.GetType().GetField(R.Name("TutorialState.Stage"), All);
                stage.SetValue(state, Enum.ToObject(stage.FieldType, value));
                holder.SetValue(Tutorial, state);
            }
        }

        /// <summary>The plain zombie (role Zombie.Walker): 75 of them sit in the tutorial's pool.</summary>
        public static string PlainZombieType { get { return R.Name("Zombie.Walker"); } }
        public static string ZombieTeam { get { return R.Name("Team.Three"); } }
        /// <summary>
        /// Players are on Team2 in the tutorial (owner's design, inferred): the only
        /// MapKind.HeroStart and the MapKind.DriverStart are Team2, and a later stage
        /// points at Team1 enemies.
        /// </summary>
        public static string PlayerTeam { get { return R.Name("Team.Two"); } }

        /// <summary>A plain zombie at (x, y) on the zombie team (used by the probe and tests).</summary>
        public object SpawnZombie(float x, float y, Action<string> log)
        {
            object zombie = SpawnNpc(PlainZombieType, x, y, ZombieTeam, null);
            log("spawned zombie " + ServerHooks.Describe(zombie) + " at (" + x + ", " + y + "), synchronizable " + SynchronizableIndex(zombie) +
                ", Health " + ServerHooks.GetStat(zombie, R.Name("Stat.Health")) + "/" + ServerHooks.GetStat(zombie, R.Name("Stat.HealthMax")) + ", attacks with " + ServerHooks.StrikeAbility[zombie]);
            return zombie;
        }

        /// <summary>
        /// Takes an NPC of the given type from the pool, puts it at (x, y) on the given
        /// team facing the given direction (if any), spawns it, and registers its
        /// slot-0 ability for the AI.
        /// </summary>
        public object SpawnNpc(string typeName, float x, float y, string team, float[] direction)
        {
            object npc = TakeFromPool(Game(typeName));
            // An empty pool hands out a fresh object that isn't registered for sync (index 65535): the
            // client never cached one, so it could not be shown.
            PropertyInfo index = npc.GetType().GetProperty(R.Name("Entity.GlobalIndex"), All);
            if (index != null && Convert.ToInt32(index.GetValue(npc, null)) == 65535) throw new Exception("no " + typeName + " in the cached pool");
            ServerHooks.ForgetNpc(npc);
            PropertyInfo teamProp = npc.GetType().GetProperty(R.Name("Entity.Team"), All);
            teamProp.SetValue(npc, Enum.Parse(teamProp.PropertyType, team), null);
            object position = Vector2(x, y);
            ServerHooks.SetField(npc, "Position", position);
            try { ServerHooks.SetField(npc, R.Name("Map.Position"), position); } catch (MissingFieldException) { }
            if (direction != null && (direction[0] != 0 || direction[1] != 0)) SetAim(npc, direction[0], direction[1]);
            npc.GetType().GetMethod(R.Name("Entity.Appear"), All, null, Type.EmptyTypes, null).Invoke(npc, null);
            SetMovement(npc);

            object bar = npc.GetType().GetProperty(R.Name("Fighter.Bar"), All).GetValue(npc, null);
            Array slots = bar == null ? null : (Array)bar.GetType().GetProperty(R.Name("Bar.Slots"), All).GetValue(bar, null);
            object primary = null;
            if (slots != null)
                foreach (object ability in slots)
                    if (ability != null) { primary = ability.GetType().GetProperty(R.Name("Ability.Key"), All).GetValue(ability, null); break; }
            if (primary == null) primary = Enum.Parse(R.Type("Type.AbilityKey"), R.Name("Ability.UndeadSwing"));
            ServerHooks.StrikeAbility[npc] = primary;
            return npc;
        }

        /// <summary>
        /// Gives a zombie its movement values. Every zombie class keeps acceleration, friction and speed
        /// factors per movement state (on role Zombie.Base), filled by its own Zombie.SetMovementState(state)
        /// from the balance data. The walker calls it when it is set up; the specials (Butcher, Floater, ...),
        /// hoarders and elites only call it when the client reads their state from the network, so on our server
        /// they had acceleration 0 and never moved (Horde playtest 2026-10-07). Here: SetMovementState(0), then
        /// the base class's walk mode (Zombie.Base.WalkMode), which copies those values into the character.
        /// </summary>
        private void SetMovement(object npc)
        {
            Type zombie = R.Type("Zombie.Base");
            if (!zombie.IsInstanceOfType(npc) || (float)zombie.GetField(R.Name("Zombie.Base.Acceleration"), All).GetValue(npc) != 0f) return;
            MethodInfo state = npc.GetType().GetMethods(All | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == R.Name("Zombie.SetMovementState") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            if (state == null) return;
            state.Invoke(npc, new[] { Enum.ToObject(state.GetParameters()[0].ParameterType, 0) });
            zombie.GetMethod(R.Name("Zombie.Base.WalkMode"), All).Invoke(npc, null);
        }

        /// <summary>Takes an object of the given type from the game's pool (not yet spawned).</summary>
        public object TakeFromPool(Type t)
        {
            int typeId = (int)WorldType.GetMethod(R.Name("World.KindOf"), All).Invoke(null, new object[] { t });
            object o = WorldType.GetMethod(R.Name("World.FromPool"), All).Invoke(World, new object[] { typeId, false });
            if (o == null) throw new Exception("no " + t.FullName + " left in the pool");
            return o;
        }

        /// <summary>Points a character's aim (and the aim it turns toward) in a direction.</summary>
        public void SetAim(object character, float x, float y)
        {
            object dir = Vector2(x, y);
            PropertyInfo aim = character.GetType().GetProperty(R.Name("Fighter.Aim"), All);
            if (aim != null) aim.SetValue(character, dir, null);
            for (Type t = character.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(R.Name("Fighter.AimGoal"), All | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(character, dir); break; }
            }
        }

        /// <summary>The loaded map's objects.</summary>
        public List<object> MapThings()
        {
            return ((IEnumerable)WorldType.GetProperty(R.Name("Map.All"), All).GetValue(World, null)).Cast<object>().ToList();
        }

        public float[] MapPosition(object mapObject) { return ServerHooks.Vector(mapObject.GetType().GetProperty("Position").GetValue(mapObject, null)); }

        public float[] MapDirection(object mapObject)
        {
            PropertyInfo d = mapObject.GetType().GetProperty("Direction", All);
            return d == null ? new float[] { 0, 0 } : ServerHooks.Vector(d.GetValue(mapObject, null));
        }

        /// <summary>
        /// Every synchronizable to send now: active game objects, the ability
        /// bars of active characters, and the objects that aren't game objects
        /// (game mode, supplies, combat text). Index order.
        /// </summary>
        public List<object> ActiveSynchronizables(HashSet<object> alsoInclude)
        {
            Type gob = R.Type("Type.WorldObject");
            Type character = R.Type("Type.Fighter");
            Type abilityBar = R.Type("Type.Bar");
            PropertyInfo isActive = gob.GetProperty("IsActive", All);
            PropertyInfo bar = character.GetProperty(R.Name("Fighter.Bar"), All);
            int n = SyncCount;
            object[] all = new object[n];
            HashSet<object> activeBars = new HashSet<object>();
            for (int i = 0; i < n; i++)
            {
                all[i] = GetSynchronizable(i);
                if (all[i] != null && character.IsInstanceOfType(all[i]) && (bool)isActive.GetValue(all[i], null))
                {
                    object b = bar.GetValue(all[i], null);
                    if (b != null) activeBars.Add(b);
                }
            }
            List<object> result = new List<object>();
            foreach (object o in all)
            {
                if (o == null) continue;
                bool send = gob.IsInstanceOfType(o) ? (bool)isActive.GetValue(o, null)
                          : abilityBar.IsInstanceOfType(o) ? activeBars.Contains(o)
                          : true;
                if (send || alsoInclude.Contains(o)) result.Add(o);
            }
            return result;
        }

        /// <summary>
        /// Gets player 0 ready before the client finishes loading: takes it from
        /// the pool, attaches it to client 0, puts it on Team1 at the spawn point,
        /// spawns it and marks the game mode started (otherwise the game drags
        /// the player towards 0,0). Returns player 0.
        /// </summary>
        public object PrepareLocalPlayer(SpawnPoint spawn, object clientInfoData, ulong steamId, ulong userId, string team = null, int accountLevel = 1)
        {
            return PreparePlayer(0, Character, spawn, clientInfoData, steamId, userId, team ?? PlayerTeam, accountLevel, false);
        }

        /// <summary>
        /// The player object of a cached character: World.Cache makes one Player per character in MatchInfo.HeroCache
        /// (GetPlayer(i), i = 0..count-1, not in list order), so the human's and each bot's hero are found by
        /// HeroId. Returns its player index, or -1.
        /// </summary>
        public int PlayerIndexOf(string character)
        {
            for (int i = 0; i < 32; i++)
            {
                object p;
                try { p = GetPlayer(i); } catch (Exception) { break; }
                if (p != null && p.GetType().GetProperty(R.Name("Type.HeroId"), All).GetValue(p, null).ToString() == character) return i;
            }
            return -1;
        }

        /// <summary>
        /// Gets a hero ready on a client slot before the human's client finishes loading: takes the character's
        /// player from the pool, attaches it to client clientIndex (0 = the human; bots use 1-11 with a server-side
        /// client and IsBot), puts it on its team at the spawn point and spawns it. Returns the player.
        /// </summary>
        public object PreparePlayer(int clientIndex, string character, SpawnPoint spawn, object clientInfoData, ulong steamId, ulong userId, string team, int accountLevel, bool bot)
        {
            int playerIndex = PlayerIndexOf(character);
            if (playerIndex < 0) throw new Exception("no cached player for " + character);
            object player = GetPlayer(playerIndex);
            int typeId = (int)WorldType.GetMethod(R.Name("World.KindOf"), All).Invoke(null, new object[] { player.GetType() });
            WorldType.GetMethod(R.Name("World.FromPool"), All).Invoke(World, new object[] { typeId, false });

            object client = GetClient(clientIndex);
            Type ct = client.GetType();
            ct.GetProperty(R.Name("Client.HeroSlot"), All).SetValue(client, playerIndex, null);
            ct.GetProperty(R.Name("Record.Account"), All).SetValue(client, steamId, null);
            ct.GetProperty(R.Name("Item.Owner"), All).SetValue(client, userId, null);
            if (clientInfoData != null) ct.GetField(R.Name("Net.ClientCard"), All).SetValue(client, clientInfoData);
            ct.GetProperty("IsConnected", All).SetValue(client, true, null);
            // The game's own network layer would list the client as active and connected.
            // GM.Update sends destroy messages to the connected clients, and Spawn()
            // marks objects for the active ones, so put client 0 in both lists.
            foreach (string list in new[] { R.Name("Network.Active"), R.Name("Network.Connected") })
            {
                IList clients = (IList)NetworkType.GetProperty(list, All).GetValue(Network, null);
                if (!clients.Contains(client)) clients.Add(client);
            }
            ct.GetMethod(R.Name("World.Created"), All).Invoke(client, new[] { World });
            ct.GetMethod(R.Name("Client.Control"), All).Invoke(client, new[] { player });
            ct.GetMethod(R.Name("Client.Watch"), All).Invoke(client, new[] { player });
            // Kill XP (role KillXp.Award) goes to Network.Heroes near the kill; the client
            // adds its local player there in World.Created, which ran before the player was set.
            IList connected = (IList)WorldType.GetProperty(R.Name("Network.Heroes"), All).GetValue(World, null);
            if (!connected.Contains(player)) connected.Add(player);
            if (clientIndex == 0) ServerHooks.Client0 = client;

            Type pt = player.GetType();
            if (bot) pt.GetField(R.Name("Hero.Bot"), All).SetValue(player, true);
            PropertyInfo teamProp = pt.GetProperty(R.Name("Entity.Team"), All);
            teamProp.SetValue(player, Enum.Parse(teamProp.PropertyType, team), null);
            object position = Vector2(spawn.X, spawn.Y);
            pt.GetField("Position", All).SetValue(player, position);
            pt.GetField(R.Name("Map.Position"), All).SetValue(player, position);
            // The account level decides which class abilities the player has (owner, leveling step 5):
            // Hero.SetRank before Spawn, as the hail's ClientInfoObject level says.
            pt.GetMethod(R.Name("Hero.SetRank"), All).Invoke(player, new object[] { accountLevel });
            pt.GetMethod(R.Name("Entity.Appear"), All, null, Type.EmptyTypes, null).Invoke(player, null);
            if (!bot) R.Type("Type.Mode").GetField(R.Name("World.Started"), All).SetValue(ActiveMode, true);
            return player;
        }

        /// <summary>
        /// Emits a subclass that forwards every base constructor, implements the
        /// listed getters as "return true", every other abstract method as a
        /// no-op returning the default value, and overrides each hooked virtual
        /// method to call ServerHooks.Dispatch (after the base method if asked).
        /// </summary>
        private static readonly Dictionary<string, Type> Subclasses = new Dictionary<string, Type>();

        public static Type Subclass(Type baseType, string name, string[] returnTrue, IList<HookedMethod> hooks = null)
        {
            // Emit each class once; a fresh match reuses it.
            Type cached;
            if (Subclasses.TryGetValue(name, out cached)) return cached;
            AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run);
            TypeBuilder tb = ab.DefineDynamicModule(name).DefineType(name, TypeAttributes.Public | TypeAttributes.Class, baseType);
            foreach (ConstructorInfo bc in baseType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Type[] ps = bc.GetParameters().Select(p => p.ParameterType).ToArray();
                ILGenerator il = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, ps).GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                for (int i = 0; i < ps.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Call, bc);
                il.Emit(OpCodes.Ret);
            }
            for (Type t = baseType; t != null && t.IsAbstract; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(x => x.IsAbstract))
                {
                    MethodAttributes attrs = (m.Attributes & MethodAttributes.MemberAccessMask) | MethodAttributes.Virtual | MethodAttributes.HideBySig;
                    MethodBuilder mb = tb.DefineMethod(m.Name, attrs, m.ReturnType, m.GetParameters().Select(p => p.ParameterType).ToArray());
                    ILGenerator il = mb.GetILGenerator();
                    if (returnTrue.Contains(m.Name)) il.Emit(OpCodes.Ldc_I4_1);
                    else if (m.ReturnType != typeof(void))
                    {
                        LocalBuilder local = il.DeclareLocal(m.ReturnType);
                        il.Emit(OpCodes.Ldloca, local);
                        il.Emit(OpCodes.Initobj, m.ReturnType);
                        il.Emit(OpCodes.Ldloc, local);
                    }
                    il.Emit(OpCodes.Ret);
                    tb.DefineMethodOverride(mb, m);
                }
            }
            if (hooks != null)
                foreach (HookedMethod h in hooks) EmitHook(tb, h);
            Type created = tb.CreateType();
            Subclasses[name] = created;
            return created;
        }

        /// <summary>
        /// Overrides a virtual method with: optionally call the base method, box
        /// every argument into an object[], call ServerHooks.Dispatch(key, this,
        /// args), copy ref/out arguments back, and return the result.
        /// </summary>
        private static void EmitHook(TypeBuilder tb, HookedMethod h)
        {
            MethodInfo m = h.Method;
            ParameterInfo[] ps = m.GetParameters();
            Type[] types = ps.Select(p => p.ParameterType).ToArray();
            MethodAttributes attrs = (m.Attributes & MethodAttributes.MemberAccessMask) | MethodAttributes.Virtual | MethodAttributes.HideBySig;
            MethodBuilder mb = tb.DefineMethod(m.Name, attrs, m.ReturnType, types);
            ILGenerator il = mb.GetILGenerator();
            if (h.CallBaseFirst)
            {
                il.Emit(OpCodes.Ldarg_0);
                for (int i = 0; i < ps.Length; i++) il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Call, m);
                if (m.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
            }
            LocalBuilder args = il.DeclareLocal(typeof(object[]));
            il.Emit(OpCodes.Ldc_I4, ps.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            il.Emit(OpCodes.Stloc, args);
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = types[i].IsByRef ? types[i].GetElementType() : types[i];
                il.Emit(OpCodes.Ldloc, args);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                if (types[i].IsByRef) il.Emit(OpCodes.Ldobj, t);
                if (t.IsValueType) il.Emit(OpCodes.Box, t);
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Ldstr, h.Key);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, args);
            il.Emit(OpCodes.Call, typeof(ServerHooks).GetMethod("Dispatch"));
            LocalBuilder result = il.DeclareLocal(typeof(object));
            il.Emit(OpCodes.Stloc, result);
            for (int i = 0; i < ps.Length; i++)
            {
                if (!types[i].IsByRef) continue;
                Type t = types[i].GetElementType();
                il.Emit(OpCodes.Ldarg, i + 1);
                il.Emit(OpCodes.Ldloc, args);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, t);
                il.Emit(OpCodes.Stobj, t);
            }
            if (m.ReturnType != typeof(void))
            {
                il.Emit(OpCodes.Ldloc, result);
                il.Emit(OpCodes.Unbox_Any, m.ReturnType);
            }
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(mb, m);
        }

        /// <summary>
        /// Finds a Unity 4 text asset by name in a .assets file and returns its text
        /// bytes. A text asset is stored as its name (int32 length, bytes, padded to
        /// 4) followed by its text (int32 length, bytes); we look for that shape
        /// around the name, with text that starts with '&lt;'.
        /// </summary>
        public static byte[] ReadTextAsset(string assetsFile, string name)
        {
            byte[] data = File.ReadAllBytes(assetsFile);
            byte[] needle = System.Text.Encoding.ASCII.GetBytes(name);
            for (int i = 4; i + needle.Length + 8 <= data.Length; i++)
            {
                if (data[i] != needle[0] || BitConverter.ToInt32(data, i - 4) != needle.Length) continue;
                bool match = true;
                for (int j = 1; j < needle.Length && match; j++) match = data[i + j] == needle[j];
                if (!match) continue;
                int textAt = i + ((needle.Length + 3) & ~3);
                int length = BitConverter.ToInt32(data, textAt);
                if (length <= 0 || textAt + 4 + length > data.Length || data[textAt + 4] != (byte)'<') continue;
                byte[] text = new byte[length];
                Array.Copy(data, textAt + 4, text, 0, length);
                return text;
            }
            return null;
        }

        public static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}

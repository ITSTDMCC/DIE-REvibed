// Scavenger probe: loads a Scavenger map (1 Resort, 2 Jungle, 3 Expedition) with the ScavengeRules game mode and
// the human's hero plus bot heroes cached, through the match server's GameRuntime, and plays it offline with the
// server's ScavengerDirector. Read-only towards the install; output to the console (run it into local\).
// Build and run with tools\run_scavenger_probe.cmd [1|2|3] [mode].
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;
using EpidemicServer.Match;

public static class ScavengerProbe
{
    private const BindingFlags all = GameRuntime.All;

    public static int Main(string[] args)
    {
        Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        string install = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        int map = args.Length > 1 ? int.Parse(args[1]) : 1;
        string mode = args.Length > 2 ? args[2] : "players";
        Environment.CurrentDirectory = install;
        ServerHooks.Log = line => { if (line.Contains("failed")) Console.WriteLine("     [hook] " + line); };
        try
        {
            GameRuntime game = GameRuntime.Load(install);
            GameBuffer.Init(game);
            game.MapIndex = map;
            game.ModeKind = GameRuntime.ScavengerHuntGameModeType;
            game.Character = Environment.GetEnvironmentVariable("HUMAN") ?? R.Name("Hero.Id7");
            // HEROES: the 11 bot heroes in team order (3 Team1, 4 Team2, 4 Team3), e.g. a live match's lineup.
            string heroList = Environment.GetEnvironmentVariable("HEROES");
            game.ExtraCharacters.AddRange(heroList != null ? heroList.Split(',') : new[] { R.Name("Hero.Id5"), R.Name("Hero.Id8"), R.Name("Hero.Id6"), R.Name("Hero.Id13"), R.Name("Hero.Id15"), R.Name("Hero.Id16"), R.Name("Hero.Id17"), R.Name("Hero.Id18"), R.Name("Hero.Id19"), R.Name("Hero.Id20"), R.Name("Hero.Id24") });
            // LEVEL: the infection (server) level, as the match host sets it from the hero's strength.
            if (Environment.GetEnvironmentVariable("LEVEL") != null) game.MatchLevel = int.Parse(Environment.GetEnvironmentVariable("LEVEL"));
            game.Start(line => Console.WriteLine("   " + line));
            int poolErrors = 0;
            game.CaptureGameLog(line => { if (line.Contains("No GameObject in pool") && poolErrors++ < 5) Console.WriteLine("   [game log] " + line); });
            Console.WriteLine("server level " + game.MatchLevel + ", Hero.ServerMod " + game.WorldType.GetField(R.Name("Hero.ServerMod"), all).GetValue(game.World));
            Console.WriteLine("map " + game.MapName + ", synchronizables " + game.SyncCount + ", cached " + string.Join(",", game.CachedCharacters().ToArray()));
            if (mode == "players")
            {
                for (int i = 0; i < 40; i++)
                {
                    object p;
                    try { p = game.GetPlayer(i); } catch (Exception) { break; }
                    if (p == null) { Console.WriteLine("player " + i + ": null"); continue; }
                    Console.WriteLine("player " + i + ": " + p.GetType().FullName + " " + p.GetType().GetProperty(R.Name("Type.HeroId"), all).GetValue(p, null) +
                                      " active " + p.GetType().GetProperty("IsActive", all).GetValue(p, null) + " sync " + game.SynchronizableIndex(p));
                }
            }
            if (mode == "graph")
            {
                var pts = game.MapThings().Where(o => o.GetType().Name == R.Name("MapKind.EventSpawns") || o.GetType().Name == R.Name("MapKind.StaticSpawn")).Select(o => game.MapPosition(o)).ToList();
                var named = new Dictionary<string, float[]> { { "T1start", new[] { 1156f, 3302f } }, { "T1supply", new[] { 1847f, 2680f } }, { "T1truck", new[] { 1729f, 2599f } },
                    { "T2start", new[] { 3553f, 2108f } }, { "T2supply", new[] { 2654f, 1799f } }, { "T3start", new[] { 1291f, 592f } }, { "T3supply", new[] { 1493f, 1533f } }, { "T3truck", new[] { 1626f, 1475f } },
                    { "T3stuck", new[] { 1336f, 1589f } }, { "Resort", new[] { 1567f, 2116f } }, { "Marina", new[] { 2124f, 1556f } }, { "Banana", new[] { 2003f, 2001f } }, { "Water", new[] { 2328f, 2314f } } };
                foreach (var pr in new[] { new[] { "T1supply", "T1truck" }, new[] { "T3supply", "T3truck" }, new[] { "T3stuck", "T3truck" }, new[] { "T1start", "T1supply" } })
                {
                    float[] a = named[pr[0]], b = named[pr[1]];
                    var path = ServerHooks.Path(a, b, 20000, true);
                    float len = 0f; float[] at = a; if (path != null) { foreach (var q in path) { len += BotDriver.Dist(at, q); at = q; } len += BotDriver.Dist(at, b); }
                    Console.WriteLine("  pair " + pr[0] + "-" + pr[1] + " dist " + BotDriver.Dist(a, b).ToString("0") + " clear " + ServerHooks.LineClear(a, b) + " path " + (path == null ? "null" : path.Count + " len " + len.ToString("0")));
                }
                var graph = RouteGraph.Build(named.Values.Concat(pts));
                int[] comp = graph.Components();
                Console.WriteLine("graph " + graph.Nodes.Count + " nodes " + graph.EdgeCount + " edges, components " + comp.Distinct().Count() + " sizes " + string.Join(",", comp.GroupBy(c => c).Select(gr => gr.Count()).OrderByDescending(x => x).Take(8).Select(x => x.ToString()).ToArray()));
                foreach (var kv in named) { int n = graph.NearestNode(kv.Value); Console.WriteLine("  " + kv.Key + " node " + n + " component " + comp[n] + " (" + BotDriver.Dist(graph.Nodes[n], kv.Value).ToString("0") + " away)"); }
                return 0;
            }
            if (mode == "path")
            {
                // Can the navmesh route Team 3's supply area to the Resort room? Try several budgets.
                foreach (float[][] pair in new[] {
                    new[] { new[] { 1336f, 1589f }, new[] { 1626f, 1475f } }, new[] { new[] { 1336f, 1589f }, new[] { 2124f, 1556f } },
                    new[] { new[] { 1336f, 1589f }, new[] { 1729f, 2599f } }, new[] { new[] { 1847f, 2680f }, new[] { 2124f, 1556f } },
                    new[] { new[] { 1847f, 2680f }, new[] { 1567f, 2150f } }, new[] { new[] { 1847f, 2680f }, new[] { 1600f, 2116f } },
                    new[] { new[] { 1626f, 1475f }, new[] { 2124f, 1556f } }, new[] { new[] { 1626f, 1475f }, new[] { 2003f, 2001f } } })
                {
                    float[] from = pair[0], to = pair[1];
                    int it = 100000;
                    var path = ServerHooks.Path(from, to, it, true);
                    Console.Write("from " + from[0] + "," + from[1] + " to " + to[0] + "," + to[1] + " ");
                    Console.WriteLine("budget " + it + ": " + (path == null ? "no path" : path.Count + " points, first " + (path.Count == 0 ? "-" : path[0][0].ToString("0") + "," + path[0][1].ToString("0")) + ", last " + (path.Count == 0 ? "-" : path[path.Count - 1][0].ToString("0") + "," + path[path.Count - 1][1].ToString("0"))));
                }
                return 0;
            }
            if (mode == "match")
            {
                // A whole match offline: the human waits (healed) at its start; 11 bots play it out.
                SpawnPoint start = game.SpawnPoints.First(sp => sp.Kind == R.Name("MapKind.HeroStart") && sp.Team == R.Name("Team.One") && sp.IsStart);
                Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
                Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
                object info = game.BuildClientInfoData(MatchHost.DefaultCameraX, MatchHost.DefaultCameraY, "Player", R.Name("Team.One"), 10, melee, ranged);
                object human = game.PrepareLocalPlayer(start, info, 0, 1, R.Name("Team.One"), 10);
                var dir = new ScavengerDirector(game, human, line => Console.WriteLine("     " + line));
                dir.PopulateWorld(game.ExtraCharacters, 10, melee, ranged);
                ServerHooks.AfterUpdate = dir.Tick;
                new Leveling(game, human, line => { }).Install();
                // Friendly fire watch: hero damage whose root source is a hero on the same team.
                int friendly = 0;
                ServerHooks.DamageFilter = (victim, changer, value) =>
                {
                    object root = changer == null ? null : ServerHooks.RootOwner(changer);
                    PropertyInfo tp = victim.GetType().GetProperty(R.Name("Entity.Team"), all);
                    if (root != null && root != victim && tp != null && root.GetType().GetProperty(R.Name("Entity.Team"), all) != null &&
                        dir.Bots.Any(x => x.Player == root) && (victim == human || dir.Bots.Any(x => x.Player == victim)) &&
                        tp.GetValue(victim, null).Equals(root.GetType().GetProperty(R.Name("Entity.Team"), all).GetValue(root, null)) && friendly++ < 12)
                        Console.WriteLine("   FRIENDLY " + ServerHooks.Describe(root) + " (" + tp.GetValue(victim, null) + ") hit " + ServerHooks.Describe(victim) + " for " + value + " via " + ServerHooks.Describe(changer));
                    return value;
                };
                var messages = new Dictionary<int, int>();
                ServerHooks.QueueReliable = m => { byte[] b = GameBuffer.From(m).ToBytes(); int type = b.Length > 2 ? (b[0] == 0 ? -1 : b[2]) : -2, n; messages.TryGetValue(type, out n); messages[type] = n + 1; };
                bool finished = false;
                dir.OnFinished = (placement, boxes) => { finished = true; Console.WriteLine("   finished: Team1 placement " + placement + ", boxes " + string.Join(",", boxes.Select(x => x.ToString()).ToArray())); };
                MethodInfo update = game.WorldType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
                float t = 0f; int frame = 0;
                object st = game.ActiveMode.GetType().GetField(R.Name("ScavengerMode.State"), all).GetValue(game.ActiveMode);
                int maxSec = args.Length > 3 ? int.Parse(args[3]) : 1800;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int sec = 0; sec <= maxSec && !finished; sec++)
                {
                    if ((sec <= 16) || (sec % 5 == 0 && sec <= 60))
                    {
                        Bot b0 = dir.Bots[0];
                        Console.WriteLine("   s=" + sec + " real " + clock.Elapsed.TotalSeconds.ToString("0.0") + "s; bot0 " + b0.Name + " at " + string.Join(",", ServerHooks.Position(b0.Player).Select(v => v.ToString("0")).ToArray()) +
                            " goal " + (b0.Goal == null ? "-" : string.Join(",", b0.Goal.Select(v => v.ToString("0")).ToArray())) + " target " + ServerHooks.Describe(b0.Target) +
                            (b0.Target == null ? "" : " HP " + ServerHooks.GetStat(b0.Target, R.Name("Stat.Health"))) + " in " + b0.Player.GetType().GetProperty(R.Name("Fighter.Input"), all).GetValue(b0.Player, null) + " dead " + BotDriver.IsDead(b0.Player) + " path " + (b0.Path == null ? "null" : b0.Path.Count + " [" + string.Join(" ", b0.Path.Take(4).Select(q => q[0].ToString("0") + "," + q[1].ToString("0")).ToArray()) + "]"));
                    }
                    for (int k = 0; k < 30; k++) { frame++; t += 1f / 30f; game.SetStat(human, R.Name("Stat.Health"), ServerHooks.GetStat(human, R.Name("Stat.HealthMax"))); update.Invoke(game.World, new object[] { 1f / 30f, t, frame }); }
                    if (sec % (Environment.GetEnvironmentVariable("EVERY") == null ? 60 : int.Parse(Environment.GetEnvironmentVariable("EVERY"))) == 0)
                    {
                        st = game.ActiveMode.GetType().GetField(R.Name("ScavengerMode.State"), all).GetValue(game.ActiveMode);
                        Array teams = (Array)st.GetType().GetField(R.Name("ScavengerState.Teams"), all).GetValue(st);
                        var jtb = (IList)st.GetType().GetField(R.Name("ScavengerState.RoomOwner"), all).GetValue(st);
                        object sup = game.ActiveMode.GetType().GetProperty(R.Name("Mode.Supplies"), all).GetValue(game.ActiveMode, null);
                        Console.WriteLine("t=" + sec + " teams " + string.Join(" ", teams.Cast<object>().Select(x => "[" + string.Join(",", x.GetType().GetFields(all).Where(f => !f.IsStatic).Select(f => f.GetValue(x).ToString()).ToArray()) + "]").ToArray()) +
                            " rooms " + string.Join(",", jtb.Cast<object>().Select(x => x.ToString()).ToArray()) +
                            " delivered " + string.Join("/", new[] { R.Name("Team.One"), R.Name("Team.Two"), R.Name("Team.Three") }.Select(n => sup.GetType().GetMethod(R.Name("Supplies.Delivered"), all).Invoke(sup, new[] { Enum.Parse(human.GetType().GetProperty(R.Name("Entity.Team"), all).PropertyType, n) }).ToString()).ToArray()) +
                            " carried " + string.Join("/", new[] { R.Name("Team.One"), R.Name("Team.Two"), R.Name("Team.Three") }.Select(n => sup.GetType().GetMethod(R.Name("Supplies.CarriedByTeam"), all).Invoke(sup, new[] { Enum.Parse(human.GetType().GetProperty(R.Name("Entity.Team"), all).PropertyType, n) }).ToString()).ToArray()) +
                            " night " + st.GetType().GetField(R.Name("ScavengerState.Night"), all).GetValue(st) + " dead bots " + dir.Bots.Count(b => BotDriver.IsDead(b.Player)));
                        MethodInfo getLevel = human.GetType().GetMethod(R.Name("Hero.LevelOf"), all, null, Type.EmptyTypes, null);
                        var zombies = ((Array)game.WorldType.GetField(R.Name("World.Npcs"), all).GetValue(game.World)).Cast<object>().Where(n => n != null && !BotDriver.Gone(n)).ToList();
                        Console.WriteLine("   levels: human " + getLevel.Invoke(human, null) + ", bots " + string.Join(",", dir.Bots.Select(b => getLevel.Invoke(b.Player, null).ToString()).ToArray()) +
                            ", average " + game.WorldType.GetMethod(R.Name("World.AverageLevel"), all).Invoke(game.World, null) +
                            "; zombies " + string.Join(" ", zombies.Take(5).Select(n => ServerHooks.Describe(n) + " LV " + getLevel.Invoke(n, null) + " HP " + ServerHooks.GetStat(n, R.Name("Stat.HealthMax"))).ToArray()));
                        // Hero-owned NPCs (pets and summons): who owns them and where they are.
                        foreach (object n in zombies.Where(z => { object o = ServerHooks.RootOwner(z); return o != null && o != z && (o == human || dir.Bots.Any(x => x.Player == o)); }))
                        {
                            object o = ServerHooks.RootOwner(n);
                            Bot ob = dir.Bots.FirstOrDefault(x => x.Player == o);
                            Console.WriteLine("   pet " + ServerHooks.Describe(n) + " of " + (ob == null ? "human" : ob.Name) + " at " + string.Join(",", ServerHooks.Position(n).Select(v => v.ToString("0")).ToArray()) +
                                              " owner at " + string.Join(",", ServerHooks.Position(o).Select(v => v.ToString("0")).ToArray()) + " HP " + ServerHooks.GetStat(n, R.Name("Stat.Health")));
                        }
                        foreach (Bot b in dir.Bots.Where(x => x.Team == (Environment.GetEnvironmentVariable("WATCH_TEAM") ?? R.Name("Team.Three"))))
                            Console.WriteLine("     " + b.Name + " at " + string.Join(",", ServerHooks.Position(b.Player).Select(v => v.ToString("0")).ToArray()) + " goal " + (b.Goal == null ? "-" : string.Join(",", b.Goal.Select(v => v.ToString("0")).ToArray())) +
                                              " target " + ServerHooks.Describe(b.Target) + " path " + (b.Path == null ? "null" : b.Path.Count.ToString()) + " dead " + BotDriver.IsDead(b.Player) + " progressAt " + b.ProgressAt.ToString("0") + " in " + b.Player.GetType().GetProperty(R.Name("Fighter.Input"), all).GetValue(b.Player, null) + " wp " + (b.Waypoint == null ? "-" : b.Waypoint[0].ToString("0") + "," + b.Waypoint[1].ToString("0")));
                    }
                }
                Console.WriteLine("messages " + string.Join(", ", messages.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value).ToArray()));
                return 0;
            }
            if (mode == "pet")
            {
                // A hero's pet (HUMAN=a hero that summons a minion) next to its owner, a zombie 70 units away, then the
                // owner walks off: the pet should fight the zombie, then follow.
                SpawnPoint start = game.SpawnPoints.First(sp => sp.Kind == R.Name("MapKind.HeroStart") && sp.Team == R.Name("Team.One") && sp.IsStart);
                Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
                Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
                object info = game.BuildClientInfoData(MatchHost.DefaultCameraX, MatchHost.DefaultCameraY, "Player", R.Name("Team.One"), 10, melee, ranged);
                object human = game.PrepareLocalPlayer(start, info, 0, 1, R.Name("Team.One"), 10);
                Type petType = R.Type("Type.Minion");
                object pet = game.TakeFromPool(petType);
                pet.GetType().GetProperty(R.Name("Entity.Team"), all).SetValue(pet, human.GetType().GetProperty(R.Name("Entity.Team"), all).GetValue(human, null), null);
                ServerHooks.SetField(pet, "Position", game.Vector2(start.X + 10f, start.Y));
                pet.GetType().GetProperty(R.Name("Fighter.Master"), all).SetValue(pet, human, null);
                pet.GetType().GetMethod(R.Name("Entity.Appear"), all, null, Type.EmptyTypes, null).Invoke(pet, null);
                object z = game.SpawnNpc(GameRuntime.PlainZombieType, start.X + 70f, start.Y + 20f, GameRuntime.ZombieTeam, null);
                // PETAGGRO=1: the zombie is active and the owner stands far off, so the zombie should go for the pet.
                bool petAggro = Environment.GetEnvironmentVariable("PETAGGRO") != null;
                if (!petAggro) ServerHooks.Passive.Add(z);
                else ServerHooks.Log = line => { if (line.StartsWith("AI: ") && line.Contains("attacks")) Console.WriteLine("     [hook] " + line); };
                if (!petAggro) ServerHooks.Log = line => { if (line.Contains("pet") || line.Contains("failed")) Console.WriteLine("     [hook] " + line); };
                MethodInfo update = game.WorldType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
                float t = 0f; int frame = 0;
                for (int sec = 0; sec <= 20; sec++)
                {
                    Console.WriteLine("   s=" + sec + " pet at " + string.Join(",", ServerHooks.Position(pet).Select(v => v.ToString("0")).ToArray()) + " HP " + ServerHooks.GetStat(pet, R.Name("Stat.Health")) +
                                      " | zombie HP " + (BotDriver.Gone(z) ? "dead" : ServerHooks.GetStat(z, R.Name("Stat.Health")).ToString()) + " | owner at " + string.Join(",", ServerHooks.Position(human).Select(v => v.ToString("0")).ToArray()));
                    if (sec == 0 && petAggro) R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), all).Invoke(human, new object[] { game.Vector2(start.X - 400f, start.Y), true });
                    if (sec == 1 && petAggro) Console.WriteLine("   zombie at " + string.Join(",", ServerHooks.Position(z).Select(v => v.ToString("0")).ToArray()));
                    if (sec == 10 && !petAggro) R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), all).Invoke(human, new object[] { game.Vector2(start.X - 150f, start.Y + 60f), true });
                    for (int k = 0; k < 30; k++) { frame++; t += 1f / 30f; game.SetStat(human, R.Name("Stat.Health"), ServerHooks.GetStat(human, R.Name("Stat.HealthMax"))); update.Invoke(game.World, new object[] { 1f / 30f, t, frame }); }
                }
                return 0;
            }
            if (mode == "stun")
            {
                // Two heroes on rival teams fight each other with basic attacks; trace every buff and control
                // effect they put on each other (what staggers a hero in PvP).
                SpawnPoint start = game.SpawnPoints.First(sp => sp.Kind == R.Name("MapKind.HeroStart") && sp.Team == R.Name("Team.One") && sp.IsStart);
                Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
                Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
                object info = game.BuildClientInfoData(MatchHost.DefaultCameraX, MatchHost.DefaultCameraY, "Player", R.Name("Team.One"), 10, melee, ranged);
                object human = game.PrepareLocalPlayer(start, info, 0, 1, R.Name("Team.One"), 10);
                string[] pair = (Environment.GetEnvironmentVariable("PAIR") ?? R.Name("Hero.Id5") + "," + R.Name("Hero.Id6")).Split(',');
                Bot a = BotDriver.Create(game, 1, pair[0], R.Name("Team.Two"), "A " + pair[0], start, 10, melee, ranged);
                Bot b2 = BotDriver.Create(game, 2, pair[1], R.Name("Team.Three"), "B " + pair[1], start, 10, melee, ranged);
                a.Target = b2.Player; b2.Target = a.Player;
                ServerHooks.TraceControl = true;
                // NOSTAGGER=1: the Scavenger rule, no basic-attack stagger between heroes.
                if (Environment.GetEnvironmentVariable("NOSTAGGER") != null)
                    ServerHooks.BlockControl = (src, tgt) => src != tgt && ServerHooks.BasicAttackHitOnStack() != null;
                var seen = new Dictionary<string, int>();
                ServerHooks.Log = line =>
                {
                    if (!line.StartsWith("trace")) return;
                    string key = System.Text.RegularExpressions.Regex.Replace(line, "#[0-9]+", "");
                    int c; seen.TryGetValue(key, out c); seen[key] = c + 1;
                    if (c == 0) Console.WriteLine("   " + line);
                };
                MethodInfo update = game.WorldType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
                float t = 0f; int frame = 0;
                for (int k = 0; k < 30 * 8; k++)
                {
                    frame++; t += 1f / 30f;
                    BotDriver.Drive(game, a, t); BotDriver.Drive(game, b2, t);
                    update.Invoke(game.World, new object[] { 1f / 30f, t, frame });
                    game.SetStat(a.Player, R.Name("Stat.Health"), ServerHooks.GetStat(a.Player, R.Name("Stat.HealthMax")));
                    game.SetStat(b2.Player, R.Name("Stat.Health"), ServerHooks.GetStat(b2.Player, R.Name("Stat.HealthMax")));
                }
                Console.WriteLine("A " + ServerHooks.Describe(a.Player) + ", B " + ServerHooks.Describe(b2.Player));
                foreach (var kv in seen) Console.WriteLine(kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
                return 0;
            }
            if (mode == "bots")
            {
                // Human (client 0) and three bots on Team1 at the team's start; bots walk to a storage room 300 units
                // away and fight zombies on the way.
                SpawnPoint start = game.SpawnPoints.First(sp => sp.Kind == R.Name("MapKind.HeroStart") && sp.Team == R.Name("Team.One") && sp.IsStart);
                Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
                Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
                object info = game.BuildClientInfoData(MatchHost.DefaultCameraX, MatchHost.DefaultCameraY, "Player", R.Name("Team.One"), 10, melee, ranged);
                object human = game.PrepareLocalPlayer(start, info, 0, 1, R.Name("Team.One"), 10);
                var bots = new List<Bot>();
                string[] heroes = { R.Name("Hero.Id5"), R.Name("Hero.Id8"), R.Name("Hero.Id6") };
                for (int i = 0; i < 3; i++)
                {
                    Weapon bm = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 2 };
                    Weapon br = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 2 };
                    bots.Add(BotDriver.Create(game, 1 + i, heroes[i], R.Name("Team.One"), "Bot " + heroes[i], start, 10, bm, br));
                }
                Console.WriteLine("human " + ServerHooks.Describe(human) + " player index " + game.PlayerIndexOf(R.Name("Hero.Id7")) + "; bots: " +
                    string.Join(", ", bots.Select(b => b.Name + " client " + b.ClientIndex + " " + ServerHooks.Describe(b.Player) + " W1 " + ServerHooks.Describe(ServerHooks.GetFieldValue(b.Player, R.Name("Fighter.WeaponA"))) + " at " + string.Join(",", ServerHooks.Position(b.Player).Select(v => v.ToString("0")).ToArray())).ToArray()));
                object bar0 = bots[0].Player.GetType().GetProperty(R.Name("Fighter.Bar"), all).GetValue(bots[0].Player, null);
                Array slots0 = (Array)bar0.GetType().GetProperty(R.Name("Bar.Slots"), all).GetValue(bar0, null);
                for (int i = 0; i < slots0.Length; i++) { object ab = slots0.GetValue(i); if (ab != null) Console.WriteLine("   slot " + i + ": " + ab.GetType().GetProperty(R.Name("Ability.Key"), all).GetValue(ab, null) + " range " + (ab.GetType().GetProperty("Range", all) == null ? "?" : ab.GetType().GetProperty("Range", all).GetValue(ab, null))); }
                Console.WriteLine("   NPC list length " + ((Array)game.WorldType.GetField(R.Name("World.Npcs"), all).GetValue(game.World)).Length);
                var rooms = game.MapThings().Where(o => o.GetType().Name == R.Name("MapKind.RoomEvent")).Select(o => game.MapPosition(o)).ToList();
                float[] goal = rooms.OrderBy(r => BotDriver.Dist(r, new[] { start.X, start.Y })).First();
                foreach (Bot b in bots) b.Goal = goal;
                Console.WriteLine("start " + start.X.ToString("0") + "," + start.Y.ToString("0") + " goal " + goal[0].ToString("0") + "," + goal[1].ToString("0"));
                // A zombie in front of bot 0 to test the primary attack.
                object z = game.SpawnNpc(GameRuntime.PlainZombieType, start.X + 25f, start.Y, GameRuntime.ZombieTeam, null);
                ServerHooks.Passive.Add(z);
                bots[0].Target = z;
                MethodInfo update = game.WorldType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
                float t = 0f; int frame = 0;
                for (int sec = 0; sec <= 30; sec++)
                {
                    Console.WriteLine("   s=" + sec + " bot1 at " + string.Join(",", ServerHooks.Position(bots[1].Player).Select(v => v.ToString("0")).ToArray()) + " wp " + (bots[1].Waypoint == null ? "direct" : string.Join(",", bots[1].Waypoint.Select(v => v.ToString("0")).ToArray())) +
                                      " speed stat " + ServerHooks.GetStat(bots[1].Player, "Speed") + " move " + ServerHooks.GetFieldValue(bots[1].Player, R.Name("Fighter.Move")) + " vel " + ServerHooks.GetFieldValue(bots[1].Player, "Velocity"));
                    if (sec % 3 == 0)
                        Console.WriteLine("t=" + sec + " zombie HP " + ServerHooks.GetStat(z, R.Name("Stat.Health")) + " | " + string.Join(" | ", bots.Select(b => b.Name.Substring(4) + " d=" + BotDriver.Dist(ServerHooks.Position(b.Player), goal).ToString("0") +
                            " in " + b.Player.GetType().GetProperty(R.Name("Fighter.Input"), all).GetValue(b.Player, null)).ToArray()));
                    for (int k = 0; k < 30; k++) { frame++; t += 1f / 30f; foreach (Bot b in bots) BotDriver.Drive(game, b, t); update.Invoke(game.World, new object[] { 1f / 30f, t, frame }); }
                    if (BotDriver.Gone(z) && bots[0].Target != null) { Console.WriteLine("   zombie dead at t=" + sec); bots[0].Target = null; }
                }
            }
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + GameRuntime.Unwrap(e)); return 1; }
    }
}

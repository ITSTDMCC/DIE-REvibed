// Horde probe: loads a Horde map (5 Outpost, 6 Lab, 7 Club) with the HordeMode game mode at Normal
// difficulty in the match server's GameRuntime and plays it offline with the server's HordeDirector
// and Leveling: capture (slot 26 channel, else the fallback), waves, a wipe and respawn, the hoarder,
// checkpoints, the boss, medal, rewards. Read-only towards the install.
// Build and run with tools\run_horde_probe.cmd [5|6|7].
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;
using EpidemicServer.Match;
using EpidemicServer.Protocol;

public static class HordeProbe
{
    private const float Tick = 1f / 30f;
    private const BindingFlags all = GameRuntime.All;

    public static int Main(string[] args)
    {
        string install = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        int map = args.Length > 1 ? int.Parse(args[1]) : 5;
        Environment.CurrentDirectory = install;
        ServerHooks.Log = line => { if (line.StartsWith("hook ") || line.Contains("failed")) Console.WriteLine("     [hook] " + line); };
        try
        {
            GameRuntime game = GameRuntime.Load(install);
            GameBuffer.Init(game);
            game.MapIndex = map;
            game.GameModeType = map >= 15 ? GameRuntime.ScoutMissionGameModeType : GameRuntime.HordeGameModeType;   // pools: Scout maps too
            game.Difficulty = args.Length > 3 ? int.Parse(args[3]) : 1;
            game.Start(line => Console.WriteLine("   " + line));
            if (args.Length > 2 && args[2] == "pools")
            {
                // Which boss, hoarder and special types the Normal pool holds (takes one of each; probe only).
                foreach (string ty in new[] { R.Name("Probe.PoolType1"), R.Name("Probe.PoolType2"), R.Name("Probe.PoolType3"), R.Name("Probe.PoolType4"), R.Name("Probe.PoolType5"), R.Name("Probe.PoolType6"), R.Name("Probe.PoolType7"), R.Name("Zombie.FloaterHoarder"), R.Name("Zombie.ButcherHoarder"), R.Name("Zombie.PullerHoarder"),
                                              R.Name("Zombie.Butcher"), R.Name("Zombie.Floater"), R.Name("Zombie.Puller"), R.Name("Zombie.Ram"), R.Name("Zombie.Siren"), R.Name("Zombie.Veteran"), R.Name("Zombie.PullerElite"), R.Name("Zombie.RamElite"), R.Name("Zombie.ButcherElite"), R.Name("Zombie.FloaterElite"), R.Name("Zombie.SirenElite") })
                {
                    int n = 0; string first = "";
                    for (int i = 0; i < 40; i++)
                    {
                        object o;
                        try { o = game.TakeFromPool(game.Game(ty)); } catch (Exception) { break; }
                        object idx = o.GetType().GetProperty("IndexGlobal", all).GetValue(o, null);
                        if (i == 0) first = idx.ToString();
                        if (Convert.ToInt32(idx) == 65535) break;
                        n++;
                    }
                    Console.WriteLine("pool " + ty + ": " + n + " (first index " + first + ")");
                }
                return 0;
            }
            SpawnPoint sp = game.PickSpawn();
            int level = Leveling.AccountLevel(game, 100);
            Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
            Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
            object info = game.BuildClientInfoData(-67.175f, -67.175f, "Player", "Team1", level, melee, ranged);
            object player = game.PrepareLocalPlayer(sp, info, 0, 1, "Team1", level);
            Console.WriteLine("Map " + game.MapName + ", spawn " + sp + ", level " + level + ", synchronizables " + game.NumOfSynchronizables);
            var dir = new HordeDirector(game, player, line => Console.WriteLine("     " + line));
            dir.PopulateWorld();
            ServerHooks.AfterUpdate = dir.Tick;
            var leveling = new Leveling(game, player, line => Console.WriteLine("     " + line));
            leveling.Install();
            var messages = new Dictionary<int, int>();
            ServerHooks.QueueReliable = m =>
            {
                byte[] b = GameBuffer.From(m).ToBytes();
                int type = b.Length > 2 ? b[2] : -1, n;
                messages.TryGetValue(type, out n);
                messages[type] = n + 1;
            };
            Account account = new Account();
            bool finished = false;
            dir.OnFinished = (placement, boxes) =>
            {
                finished = true;
                int xp = leveling.EndMatch(2, map, account, boxes, dir.Supplies, placement);
                Console.WriteLine("     account: " + Leveling.ApplyToAccount(game, account, xp));
            };
            object mode = game.GameMode;
            Console.WriteLine("Game mode " + mode.GetType().Name + " sync " + game.SynchronizableIndex(mode) + ", static map objects " +
                              string.Join(", ", ((System.Collections.IEnumerable)mode.GetType().GetProperty("StaticMapObjects", all).GetValue(mode, null)).Cast<object>().Select(o => o.GetType().Name).GroupBy(n => n).Select(g => g.Key + " x" + g.Count()).ToArray()) +
                              ", checkpoints " + Checkpoints(game) + ", pool difficulty " + ServerHooks.GetFieldValue(game.GameManager, "Difficulty"));

            MethodInfo update = game.GameManagerType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
            float t = 0f;
            int frame = 0;
            Action<int> run = n => { for (int i = 0; i < n; i++) { frame++; t += Tick; update.Invoke(game.GameManager, new object[] { Tick, t, frame }); } };
            MethodInfo teleport = game.Game("ConductorGameLogic.Entities.Entity").GetMethod("Teleport", all);
            Action<float[]> put = p => teleport.Invoke(player, new object[] { game.Vector2(p[0], p[1]), true });
            Action heal = () => { if (!IsDead(player)) game.SetStat(player, "Health", ServerHooks.GetStat(player, "MaxHealth")); };
            object bar = player.GetType().GetProperty("AbilityBar", all).GetValue(player, null);
            MethodInfo press = bar.GetType().GetMethod("SetAbilityPressed", all);

            if (args.Length > 2 && args[2] == "attrs")
            {
                // Zombie attributes: AttributeBuffs.AttributeBuffs.For(AttributeType) names each attribute's buff class; apply each to a
                // walker with the game's GetAndApplyBuffFromPool and see whether the pool holds it.
                Type attr = game.Game("ConductorGameLogic.Gameplay.AttributeType");
                MethodInfo buffFor = R.Type("AttributeBuffs").GetMethod(R.Name("AttributeBuffs.For"), all);
                MethodInfo apply = game.GameManagerType.GetMethods(all).First(m => m.Name == "GetAndApplyBuffFromPool");
                float[] me = ServerHooks.Position(player);
                foreach (object v in Enum.GetValues(attr))
                {
                    if (Convert.ToInt32(v) == 0) continue;
                    object z = game.SpawnNpc(GameRuntime.PlainZombieType, me[0] + 80f, me[1], GameRuntime.ZombieTeam, null);
                    Type bt = (Type)buffFor.Invoke(null, new[] { v });
                    float hp0 = ServerHooks.GetStat(z, "MaxHealth");
                    object b = null; string err = "";
                    try { b = apply.MakeGenericMethod(bt).Invoke(game.GameManager, new object[] { z, z, null }); }
                    catch (Exception e) { err = GameRuntime.Unwrap(e).Message; }
                    run(2);
                    Console.WriteLine("attribute " + v + " -> " + bt.FullName + ": " + (b == null ? "none from pool " + err : ServerHooks.Describe(b) + " index " + game.SynchronizableIndex(b)) +
                                      ", MaxHealth " + hp0 + " -> " + ServerHooks.GetStat(z, "MaxHealth") + ", Speed " + ServerHooks.GetStat(z, "Speed"));
                }
                return 0;
            }
            if (args.Length > 2 && args[2] == "move")
            {
                // Do specials walk to the player like walkers? Spawn one of each 60 units away and watch.
                float[] me = ServerHooks.Position(player);
                var test = new List<object>();
                string[] kinds = { GameRuntime.PlainZombieType, HordeDirector.ButcherType, HordeDirector.FloaterType, HordeDirector.PullerType, HordeDirector.RamType, HordeDirector.SirenType, dir.HoarderType, dir.BossType };
                for (int k = 0; k < kinds.Length; k++)
                {
                    double ang = k * Math.PI * 2 / kinds.Length;
                    object z = game.SpawnNpc(kinds[k], me[0] + (float)Math.Cos(ang) * 60f, me[1] + (float)Math.Sin(ang) * 60f, GameRuntime.ZombieTeam, new[] { 0f, 1f });
                    ServerHooks.AggroRange[z] = 400f;
                    test.Add(z);
                }
                ServerHooks.Log = line => Console.WriteLine("     [hook] " + line);
                for (int sec = 0; sec <= 6; sec++)
                {
                    Console.WriteLine("t=" + sec + ": " + string.Join(" | ", test.Select(z => ServerHooks.Describe(z) + " d=" + Dist(ServerHooks.Position(z), me).ToString("0") +
                        " spd=" + ServerHooks.GetStat(z, "Speed").ToString("0.0") + " state=" + z.GetType().GetProperty("SpawnState_Current", all).GetValue(z, null) +
                        " goal=" + z.GetType().GetProperty("UseGoalTarget", all).GetValue(z, null) + "/" + ServerHooks.GetFieldValue(z, R.Name("Npc.UseGoalPosition"))).ToArray()));
                    heal();
                    run(30);
                }
                // Which simple fields differ between the walker and the Butcher (both after 6 s)?
                Type stop = game.Game("ConductorGameLogic.Entities.GameObjectBase").BaseType;
                Func<object, Dictionary<string, string>> dump = o =>
                {
                    var d = new Dictionary<string, string>();
                    for (Type ty = R.Type("Zombie.Base"); ty != stop; ty = ty.BaseType)
                        foreach (FieldInfo f in ty.GetFields(all | BindingFlags.DeclaredOnly))
                        {
                            if (f.IsStatic) continue;
                            Type ft = f.FieldType;
                            if (!(ft.IsPrimitive || ft.IsEnum || ft.Name == "Vector2")) continue;
                            try { d[ty.Name + "." + f.Name] = Convert.ToString(f.GetValue(o)); } catch { }
                        }
                    foreach (PropertyInfo pr in game.Game("ConductorGameLogic.Entities.Character").GetProperties(all))
                    {
                        if (pr.PropertyType != typeof(bool) || pr.GetIndexParameters().Length > 0) continue;
                        try { d["P." + pr.Name] = Convert.ToString(pr.GetValue(o, null)); } catch { }
                    }
                    return d;
                };
                var w = dump(test[0]); var b = dump(test[1]);
                foreach (var kv in w) if (b.ContainsKey(kv.Key) && b[kv.Key] != kv.Value && !kv.Key.Contains("Position")) Console.WriteLine("diff " + kv.Key + ": walker " + kv.Value + " / butcher " + b[kv.Key]);
                return 0;
            }
            run(10);
            Console.WriteLine("1. start: " + Hud(game) + ", step " + dir.Step);
            ServerHooks.OnGameMessage(HordeDirector.ConcedeMessage, GameBuffer.Wrap(new byte[0]));

            var points = ((System.Collections.IEnumerable)mode.GetType().GetProperty("StaticMapObjects", all).GetValue(mode, null)).Cast<object>().Where(o => o.GetType().Name == R.Short("HordeSupplyPoint"))
                             .OrderBy(o => Convert.ToInt32(ServerHooks.GetFieldValue(o, R.Name("MapPoint.Id")))).Select(o => ServerHooks.Vector(ServerHooks.GetFieldValue(o, R.Name("MapPoint.Position")))).ToList();
            var cps = game.MapObjects().Where(o => o.GetType().Name == "Event_TriggerPosition" && (bool)GameRuntime.MapValue(o, "IsCheckpointTrigger"))
                          .OrderBy(o => (int)GameRuntime.MapValue(o, "ID")).ToList();
            // A checkpoint on the way: walk into the first non-start checkpoint's trigger.
            if (cps.Count > 1)
            {
                put(game.MapPosition(cps[1]));
                run(5);
                Console.WriteLine("2. at checkpoint trigger " + GameRuntime.MapValue(cps[1], "ID") + ": SpawnId " + mode.GetType().GetProperty("SpawnId", all).GetValue(mode, null) + ", checkpoints " + Checkpoints(game));
            }
            for (int pi = 0; pi < points.Count; pi++)
            {
                float[] at = points[pi];
                put(at);
                heal();
                // The capture channel as the client starts it: action slot 26 held for a moment.
                for (int i = 0; i < 8 * 30 && dir.Step == "supply points"; i++) { press.Invoke(bar, new object[] { 26, i < 3 }); heal(); run(1); }
                Console.WriteLine((3 + pi) + ". supply point " + (pi + 1) + " after 8 s: step " + dir.Step + ", " + Hud(game) + ", casting " + player.GetType().GetProperty("IsChanneling", all).GetValue(player, null));
                for (int i = 0; i < 6 * 30 && dir.Step == "supply points"; i++) { heal(); run(1); }
                if (dir.Step == "supply points") { Console.WriteLine("   no capture; giving up"); return 1; }
                bool wiped = false;
                for (int i = 0; i < 600 * 30 && dir.Step.StartsWith("supply point "); i++)
                {
                    put(at);
                    heal();
                    if (i % 15 == 0)
                    {
                        // A wipe in the second wave of the last point: die, ask to respawn, keep going.
                        if (!wiped && pi == points.Count - 1 && Field(game, R.Name("HordeState.WaveIndex")) == 2 && Field(game, R.Name("HordeState.ZombiesLeft")) > 0 && Field(game, R.Name("HordeState.Countdown")) == 0)
                        {
                            wiped = true;
                            game.ChangeStat(player, "Health", 0, -1000000f);
                            run(30);
                            Console.WriteLine("   wipe: dead " + IsDead(player) + ", " + Hud(game));
                            ServerHooks.OnGameMessage(HordeDirector.RespawnMessage, GameBuffer.Wrap(new byte[0]));
                            run(3);
                            Console.WriteLine("   respawned: dead " + IsDead(player) + " at " + Pos(player) + ", " + Hud(game));
                            continue;
                        }
                        foreach (object z in dir.Zombies.Where(z => !Gone(z) && Dist(ServerHooks.Position(z), at) <= 200f).ToList())
                            game.ChangeStat(z, "Health", 0, -1000000f, player);
                    }
                    if (i % (30 * 10) == 0) Console.WriteLine("   t=" + (int)t + " " + Hud(game));
                    run(1);
                }
                Console.WriteLine("   point done: step " + dir.Step + ", " + Hud(game));
            }
            for (int i = 0; i < 10 * 30 && Field(game, R.Name("HordeState.Stage")) != 2; i++) run(1);
            Console.WriteLine((3 + points.Count) + ". boss stage: " + Hud(game) + ", step " + dir.Step);
            object boss = dir.Zombies.LastOrDefault(z => z.GetType().FullName == dir.BossType);
            if (boss != null)
            {
                float[] b = ServerHooks.Position(boss);
                put(new[] { b[0] + 30f, b[1] });
                heal();
                run(60);
                Console.WriteLine("   boss " + ServerHooks.Describe(boss) + " Health " + ServerHooks.GetStat(boss, "Health") + ", alive zombies near it " +
                                  dir.Zombies.Count(z => !Gone(z) && Dist(ServerHooks.Position(z), b) <= 120f) + ", player Health " + ServerHooks.GetStat(player, "Health"));
                game.ChangeStat(boss, "Health", 0, -100000000f, player);
            }
            for (int i = 0; i < 60 && !finished; i++) run(1);
            Console.WriteLine((4 + points.Count) + ". finished " + finished + ": step " + dir.Step + ", IsCompleted " + mode.GetType().GetProperty("IsCompleted", all).GetValue(mode, null) +
                              ", MatchLength " + ServerHooks.GetFieldValue(mode, "MatchLength") + ", " + Hud(game));
            object score = player.GetType().GetField("PlayerScoreStats", all).GetValue(player);
            Console.WriteLine("Score stats on the server: zombies killed " + ServerHooks.GetFieldValue(score, R.Name("ScoreStats.ZombiesKilled")) + ", kills " + ServerHooks.GetFieldValue(score, R.Name("ScoreStats.Kills")) + ", deaths " + ServerHooks.GetFieldValue(score, R.Name("ScoreStats.Deaths")));
            Console.WriteLine("Messages sent by type: " + string.Join(", ", messages.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value).ToArray()) +
                              " (4 TeamFinished, 5 Rewards, 8 score stats, 21 destroy)");
            return finished ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + GameRuntime.Unwrap(e)); return 1; }
    }

    private static int Field(GameRuntime game, string name)
    {
        object s = ServerHooks.GetFieldValue(game.GameMode, R.Name("HordeMode.State"));
        return Convert.ToInt32(s.GetType().GetField(name, all).GetValue(s));
    }

    private static string Hud(GameRuntime game)
    {
        object s = ServerHooks.GetFieldValue(game.GameMode, R.Name("HordeMode.State"));
        Func<string, object> f = n => s.GetType().GetField(n, all).GetValue(s);
        return "stage " + f(R.Name("HordeState.Stage")) + " SP1 " + f(R.Name("HordeState.Point1")) + " SP2 " + f(R.Name("HordeState.Point2")) + " wave " + f(R.Name("HordeState.WaveIndex")) + "/" + f(R.Name("HordeState.Waves")) + " countdown " + f(R.Name("HordeState.Countdown")) +
               " left " + f(R.Name("HordeState.ZombiesLeft")) + "/" + f(R.Name("HordeState.WaveSize")) + " hoarder " + f(R.Name("HordeState.Hoarder")) + " supplies " + f(R.Name("HordeState.Supplies")) + " boss " + f(R.Name("HordeState.BossActive")) + "@" + f(R.Name("HordeState.BossMarker")) + " music " + f(R.Name("HordeState.Music"));
    }

    private static string Checkpoints(GameRuntime game)
    {
        Type cp = game.Game("ConductorGameLogic.Checkpoint");
        var list = game.ActiveSynchronizables(new HashSet<object>()).Where(o => cp.IsInstanceOfType(o)).ToList();
        return list.Count + " [" + string.Join(" ", list.Select(o => ServerHooks.GetFieldValue(o, "TriggerID") + (Convert.ToBoolean(ServerHooks.GetFieldValue(o, "IsCheckpointActivated")) ? "*" : "")).ToArray()) + "]";
    }

    private static bool IsDead(object o) { return (bool)o.GetType().GetProperty("IsDead", all).GetValue(o, null); }

    private static bool Gone(object o) { return IsDead(o) || !(bool)o.GetType().GetProperty("IsActive", all).GetValue(o, null); }

    private static float Dist(float[] a, float[] b) { float dx = a[0] - b[0], dy = a[1] - b[1]; return (float)Math.Sqrt(dx * dx + dy * dy); }

    private static string Pos(object o) { return string.Join(",", ServerHooks.Position(o).Select(v => v.ToString("0")).ToArray()); }
}

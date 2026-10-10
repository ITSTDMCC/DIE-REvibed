// Practice (Scout mission) probe: loads a Scout map with the PracticeRules game mode in the
// match server's GameRuntime and plays the mission offline with the server's ScoutDirector
// and Leveling: objectives, interaction, Looter, delivery, win, kill XP, respawn, rewards.
// Read-only towards the install. Build and run with tools\run_scout_probe.cmd [15|16|17].
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;
using EpidemicServer.Match;
using EpidemicServer.Protocol;

public static class ScoutProbe
{
    private const float Tick = 1f / 30f;

    public static int Main(string[] args)
    {
        string install = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        int map = args.Length > 1 ? int.Parse(args[1]) : GameRuntime.ScoutOutpostMap;
        Environment.CurrentDirectory = install;
        ServerHooks.Log = line => { if (line.StartsWith("hook ") || line.Contains("failed") || line.StartsWith(R.Name("Hook.Buff"))) Console.WriteLine("     [hook] " + line); };
        const BindingFlags all = GameRuntime.All;
        try
        {
            GameRuntime game = GameRuntime.Load(install);
            GameBuffer.Init(game);
            game.MapIndex = map;
            game.ModeKind = GameRuntime.ScoutMissionGameModeType;
            game.Start(line => Console.WriteLine("   " + line));
            SpawnPoint sp = game.PickSpawn();
            int level = Leveling.AccountLevel(game, 0);
            Weapon melee = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1005, UserId = 1 };
            Weapon ranged = new Weapon { Guid = Guid.NewGuid().ToByteArray(), SchematicId = 1009, UserId = 1 };
            object info = game.BuildClientInfoData(-67.175f, -67.175f, "Player", R.Name("Team.One"), level, melee, ranged);
            object player = game.PrepareLocalPlayer(sp, info, 0, 1, R.Name("Team.One"), level);
            Console.WriteLine("Map " + game.MapName + ", spawn " + sp + ", account level " + level + ", the player team " + player.GetType().GetProperty(R.Name("Entity.Team"), all).GetValue(player, null) +
                              ", Weapon1 " + ServerHooks.Describe(ServerHooks.GetFieldValue(player, R.Name("Fighter.WeaponA"))) + ", Weapon2 " + ServerHooks.Describe(ServerHooks.GetFieldValue(player, R.Name("Fighter.WeaponB"))) +
                              ", account level field " + ServerHooks.GetFieldValue(player, R.Name("Hero.Rank")));
            ScoutDirector.MinStepSeconds = 0f;
            var dir = new ScoutDirector(game, player, sp, line => Console.WriteLine("     " + line));
            dir.PopulateWorld();
            ServerHooks.OnKill = dir.OnKill;
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
            dir.OnWon = () =>
            {
                int xp = leveling.EndMatch(6, map, account, new[] { 4, 9 }, dir.SuppliesBanked());
                Console.WriteLine("     account: " + Leveling.ApplyToAccount(game, account, xp) + "; stackables " +
                                  string.Join(", ", account.Stackables.Select(s => s.Id + ":" + s.Type + "x" + s.Amount).ToArray()));
            };
            Console.WriteLine("Synchronizables active: " + game.ActiveSynchronizables(new HashSet<object>()).Count + "; game mode " + game.SynchronizableIndex(game.ActiveMode) +
                              ", supplies " + game.SynchronizableIndex(game.Supplies) + ", objectives object " + game.SynchronizableIndex(ServerHooks.GetFieldValue(game.ActiveMode, R.Name("ScoutMode.Objectives"))));

            MethodInfo update = game.WorldType.GetMethod("Update", all, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
            float t = 0f;
            int frame = 0;
            Action<int> run = n => { for (int i = 0; i < n; i++) { frame++; t += Tick; update.Invoke(game.World, new object[] { Tick, t, frame }); } };
            MethodInfo teleport = R.Type("Type.Entity").GetMethod(R.Name("Entity.MoveTo"), all);
            Action<float[]> put = p => teleport.Invoke(player, new object[] { game.Vector2(p[0], p[1]), true });
            Func<string> where = () => string.Join(",", ServerHooks.Position(player).Select(v => v.ToString("0")).ToArray());
            Action heal = () => game.SetStat(player, R.Name("Stat.Health"), ServerHooks.GetStat(player, R.Name("Stat.HealthMax")));

            run(10);
            Console.WriteLine("1. start: step " + dir.Step + ", objective " + ObjectiveText(game) + ", markers " + Count(game, R.Name("ScoutLists.Markers")));
            // Kill XP: the player kills three zombies.
            foreach (object z in dir.Zombies.Where(z => !(bool)z.GetType().GetProperty(R.Name("Entity.Dead"), all).GetValue(z, null)).Take(3).ToList())
            {
                float[] zp = ServerHooks.Position(z);
                put(new[] { zp[0] + 30f, zp[1] });
                game.ChangeStat(z, R.Name("Stat.Health"), 0, -100000f, player);
                run(1);
            }
            run(2);
            if (dir.AssaultAt != null)
            {
                put(new[] { dir.AssaultAt[0] + 20f, dir.AssaultAt[1] });
                heal();
                run(5);
                Console.WriteLine("   walked to the assault at " + string.Join(",", dir.AssaultAt.Select(v => v.ToString("0")).ToArray()));
            }
            Console.WriteLine("2. after 3 kills: XP " + player.GetType().GetProperty(R.Name("Hero.Xp"), all).GetValue(player, null) + ", level " +
                              player.GetType().GetMethod(R.Name("Hero.LevelOf"), all, null, Type.EmptyTypes, null).Invoke(player, null));
            put(new[] { dir.EventAt[0] + 20f, dir.EventAt[1] });
            heal();
            run(5);
            Console.WriteLine("3. at the event: step " + dir.Step + ", objective " + ObjectiveText(game) + ", interactions " + Count(game, R.Name("ScoutLists.Interactions")));
            if (dir.Event == ScoutDirector.LooterHunt)
            {
                object balance = game.WorldType.GetField(R.Name("World.Balance"), all).GetValue(null);
                Console.WriteLine("   Looter drop setting " + ServerHooks.GetFieldValue(ServerHooks.GetFieldValue(balance, R.Name("Skills.Balance")), R.Name("Balance.LooterDrop")));
                // The killing blow as the game deals it: an object owned by the player (here a pooled pickup stands in for her spell).
                object blow = game.TakeFromPool(R.Type("SupplyPickup"));
                blow.GetType().GetMethod("SetOwner", all).Invoke(blow, new[] { player });
                if (dir.Looter != null) game.ChangeStat(dir.Looter, R.Name("Stat.Health"), 0, -100000f, blow);
                run(10);
                Console.WriteLine("4. Looter killed: step " + dir.Step + ", pickups: " + Pickups(game));
                // Safety net: leave the drop for 21 s; the missing amount should appear by the truck.
                put(dir.TruckAt);
                for (int i = 0; i < 21 * 30; i++) { heal(); run(1); }
                Console.WriteLine("   after 21 s without collecting: pickups: " + Pickups(game));
                WalkAround(dir.EventAt, put, heal, run);
                Console.WriteLine("   walked over the drop: carried " + dir.SuppliesCarried());
            }
            else
            {
                GameBuffer msg = GameBuffer.Create();
                msg.Write((ushort)1);
                msg.Write((ushort)0);
                ServerHooks.OnGameMessage(ScoutDirector.StartInteractionMessage, GameBuffer.Wrap(msg.ToBytes()));
                put(dir.EventAt);
                for (int i = 0; i < 7 * 30 && dir.Step == "event"; i++) { heal(); run(1); }
                Console.WriteLine("4. interaction done: step " + dir.Step + ", carried " + dir.SuppliesCarried() + ", pickups: " + Pickups(game));
                if (dir.SuppliesCarried() == 0)
                {
                    WalkAround(dir.EventAt, put, heal, run);
                    Console.WriteLine("   walked over the drop: carried " + dir.SuppliesCarried());
                }
            }
            // Death drops what she carries, then respawn at a raid respawn point.
            int carriedBefore = dir.SuppliesCarried();
            float[] diedAt = ServerHooks.Position(player);
            game.ChangeStat(player, R.Name("Stat.Health"), 0, -100000f);
            run(6 * 30 + 5);
            Console.WriteLine("5. died carrying " + carriedBefore + ": now at " + where() + ", carried " + dir.SuppliesCarried() + ", dead " +
                              player.GetType().GetProperty(R.Name("Entity.Dead"), all).GetValue(player, null) + ", pickups: " + Pickups(game));
            if (dir.SuppliesCarried() < carriedBefore)
            {
                WalkAround(diedAt, put, heal, run);
                Console.WriteLine("   walked back over the drop: carried " + dir.SuppliesCarried());
            }
            put(dir.TruckAt);
            heal();
            run(5);
            Console.WriteLine("6. at the truck: step " + dir.Step + ", objective " + ObjectiveText(game));
            Console.WriteLine("   objectives as the client would read them: " + ClientSideObjectives(game));
            object bar = player.GetType().GetProperty(R.Name("Fighter.Bar"), all).GetValue(player, null);
            MethodInfo press = bar.GetType().GetMethod(R.Name("Fighter.Press"), all);
            for (int i = 0; i < 300 && dir.Step == "return"; i++) { press.Invoke(bar, new object[] { 17, i < 3 }); heal(); run(1); }
            Console.WriteLine("7. after Deliver (slot 17): step " + dir.Step + ", team " + dir.SuppliesBanked() + " of " + dir.Goal + ", carried " + dir.SuppliesCarried() +
                              ", IsCompleted " + game.ActiveMode.GetType().GetProperty("IsCompleted", all).GetValue(game.ActiveMode, null));
            if (dir.Step == "return")
            {
                object supplies = game.Supplies;
                MethodInfo give = supplies.GetType().GetMethod(R.Name("Supplies.Deliver"), all);
                give.Invoke(supplies, new object[] { player, dir.SuppliesCarried(), Activator.CreateInstance(give.GetParameters()[2].ParameterType) });
                run(3);
                Console.WriteLine("   delivered directly: step " + dir.Step + ", team " + dir.SuppliesBanked());
            }
            // Ability upgrade (MatchMessage 3: int, int) as the client sends it.
            GameBuffer up = GameBuffer.Create();
            up.Write(0);
            up.Write(1);
            ServerHooks.OnGameMessage(Leveling.UpgradeAbilityMessage, GameBuffer.Wrap(up.ToBytes()));
            // Level-up items for a big XP jump (levels 2..10).
            Account rich = new Account();
            uint toTen = (uint)R.Type("Type.Levels").GetMethod(R.Name("Levels.XpFor"), all).Invoke(null, new object[] { 10 });
            Console.WriteLine("8. level-up grants up to level 10 (" + toTen + " XP): " + Leveling.ApplyToAccount(game, rich, (int)toTen) +
                              "; stackables " + string.Join(", ", rich.Stackables.Select(s => s.Id + ":" + s.Type + "x" + s.Amount).ToArray()) +
                              ", character points " + rich.HeroPoints + ", research points " + rich.LabPoints);
            Console.WriteLine("Messages sent by type: " + string.Join(", ", messages.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value).ToArray()) +
                              " (4 team finished, 5 rewards, 21 destroy, 30 objective update, 36 upgrade done)");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + GameRuntime.Unwrap(e)); return 1; }
    }

    private static void WalkAround(float[] at, Action<float[]> put, Action heal, Action<int> run)
    {
        for (int i = 0; i < 60; i++)
        {
            double ang = i * 0.55, r = 3 + i * 0.7;
            put(new[] { at[0] + (float)(Math.Cos(ang) * r), at[1] + (float)(Math.Sin(ang) * r) });
            heal();
            run(3);
        }
    }

    /// <summary>Serialises the objectives object (ScoutMode.Objectives) for client 0 and reads it back with the game's own ScoutLists Deserialize.</summary>
    private static string ClientSideObjectives(GameRuntime game)
    {
        const BindingFlags all = GameRuntime.All;
        object jmd = ServerHooks.GetFieldValue(game.ActiveMode, R.Name("ScoutMode.Objectives"));
        GameBuffer b = GameBuffer.Create();
        jmd.GetType().GetMethod("Serialize", all).Invoke(jmd, new[] { b.Buffer, ServerHooks.Client0 });
        byte[] bytes = b.ToBytes();
        Type nmd = R.Type("ScoutLists");
        object fresh = Activator.CreateInstance(nmd);
        foreach (FieldInfo f in nmd.GetFields(all)) f.SetValue(fresh, Activator.CreateInstance(f.FieldType));
        object[] args = { GameBuffer.Wrap(bytes).Buffer };
        MethodInfo des = nmd.GetMethod("Deserialize", all);
        object boxed = fresh;
        des.Invoke(boxed, args);
        var states = (System.Collections.IList)nmd.GetField(R.Name("ScoutLists.States"), all).GetValue(boxed);
        string s = bytes.Length + " bytes " + BitConverter.ToString(bytes) + " -> " + states.Count + " state(s)";
        foreach (object st in states) s += "; " + ServerHooks.GetFieldValue(st, "EventType") + " visual " + ServerHooks.GetFieldValue(st, R.Name("Map.Visual")) + " " + ServerHooks.GetFieldValue(st, R.Name("Ui.ObjectiveField"));
        return s;
    }

    private static int Count(GameRuntime game, string list)
    {
        object lists = ServerHooks.GetFieldValue(game.ActiveMode, R.Name("ScoutMode.Lists"));
        return ((System.Collections.IList)ServerHooks.GetFieldValue(lists, list)).Count;
    }

    private static string ObjectiveText(GameRuntime game)
    {
        object lists = ServerHooks.GetFieldValue(game.ActiveMode, R.Name("ScoutMode.Lists"));
        var states = (System.Collections.IList)ServerHooks.GetFieldValue(lists, R.Name("ScoutLists.States"));
        if (states.Count == 0) return "none";
        object s = states[0];
        return ServerHooks.GetFieldValue(s, "EventType") + " visual " + ServerHooks.GetFieldValue(s, R.Name("Map.Visual")) + " " + ServerHooks.GetFieldValue(s, R.Name("Ui.ObjectiveField"));
    }

    private static string Pickups(GameRuntime game)
    {
        var list = game.ActiveSynchronizables(new HashSet<object>()).Where(o => o.GetType().Name == R.Short("SupplyPickup")).ToList();
        return list.Count + " (" + string.Join(" ", list.Take(8).Select(o => ServerHooks.GetFieldValue(o, R.Name("SupplyPickup.Amount")) + "@" +
               string.Join(",", ServerHooks.Vector(ServerHooks.GetFieldValue(o, R.Name("Pickup.Position"))).Select(v => v.ToString("0")).ToArray())).ToArray()) + ")";
    }
}

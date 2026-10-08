// Map probe: loads any map with a given game mode in the match server's GameRuntime and lists
// what the map holds and the game mode's own fields, read-only. Used to study Horde and Scavenger.
// Build and run with tools\run_map_probe.cmd <map index> <game mode type>.
using System;
using System.Linq;
using System.Reflection;
using EpidemicServer.Match;

public static class MapProbe
{
    public static int Main(string[] args)
    {
        string install = args[0];
        int map = int.Parse(args[1]), mode = int.Parse(args[2]);
        Environment.CurrentDirectory = install;
        ServerHooks.Log = line => { };
        try
        {
            GameRuntime game = GameRuntime.Load(install);
            game.MapIndex = map;
            game.GameModeType = mode;
            game.Start(line => Console.WriteLine("   " + line));
            Console.WriteLine("Game mode: " + game.GameMode.GetType().FullName + ", map " + game.MapName);
            var lines = game.DescribeMapObjects();
            Console.WriteLine(lines.Count + " map objects by type:");
            foreach (var g in lines.Select(l => l.Split('|')[1].Trim()).GroupBy(n => n).OrderByDescending(g => g.Count()))
                Console.WriteLine("  " + g.Count() + "  " + g.Key);
            Console.WriteLine("Map objects (no Spawn_Static / Spawn_EventEntities):");
            foreach (string l in lines.Where(l => !l.Contains("Spawn_Static") && !l.Contains("Spawn_EventEntities"))) Console.WriteLine("  " + l);
            Console.WriteLine("Spawn points:");
            foreach (SpawnPoint s in game.SpawnPoints) Console.WriteLine("  " + s);
            object gm = game.GameMode;
            Console.WriteLine(gm.GetType().Name + " fields:");
            foreach (var f in gm.GetType().GetFields(GameRuntime.All | BindingFlags.DeclaredOnly))
            {
                object v; try { v = f.GetValue(gm); } catch { continue; }
                Console.WriteLine("  " + f.Name + " (" + f.FieldType.Name + ") = " + v);
            }
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + GameRuntime.Unwrap(e)); return 1; }
    }
}

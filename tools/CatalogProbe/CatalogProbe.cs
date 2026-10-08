// Catalog probe: loads the match client's crafting data through the server's GameRuntime and lists what an
// account could own (weapons, gadgets, parts, consumables, designs), with counts. Read-only towards the
// install; output goes to the console (run it into local\, which is git-ignored).
// Build and run with tools\run_catalog_probe.cmd.
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EpidemicServer.Match;

public static class CatalogProbe
{
    public static int Main(string[] args)
    {
        string install = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        Environment.CurrentDirectory = install;
        ServerHooks.Log = line => { };
        const BindingFlags all = GameRuntime.All;
        try
        {
            GameRuntime game = GameRuntime.Load(install);
            game.Start(line => { });
            Type cm = game.Crafting("ConductorCrafting.CraftingManager");
            Console.WriteLine("crafting data loaded: " + cm.GetProperty("HasLoadedData", all).GetValue(null, null) + ", max weapon tier " + cm.GetProperty("MaxWeaponTier", all).GetValue(null, null));
            foreach (string name in new[] { "WeaponSchematics", "Trinkets", "Parts", "Consumables", "Designs", "ItemPerks" })
            {
                Array a = (Array)cm.GetField(name, all).GetValue(null);
                Console.WriteLine("== " + name + ": " + (a == null ? "null" : a.Length.ToString()));
                if (a == null) continue;
                foreach (object o in a)
                {
                    if (o == null) { Console.WriteLine("  (null)"); continue; }
                    Type t = o.GetType();
                    var parts = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string))
                        .Select(f => f.Name + "=" + f.GetValue(o));
                    var tags = t.GetField("Tags");
                    string tagText = tags == null || tags.GetValue(o) == null ? "" : " Tags=" + string.Join("/", ((Array)tags.GetValue(o)).Cast<object>().Select(x => x.ToString()).ToArray());
                    Console.WriteLine("  " + string.Join(" ", parts.ToArray()) + tagText);
                }
            }
            Type alh = game.Crafting("ConductorCrafting.AccountLevelHelpers");
            foreach (MethodInfo m in alh.GetMethods(all).Where(x => x.Name == "LevelRequiredForCraftingTier"))
            {
                Type pt = m.GetParameters()[0].ParameterType;
                var vals = pt.IsEnum ? Enum.GetValues(pt).Cast<object>().ToArray() : Enumerable.Range(0, 20).Cast<object>().ToArray();
                Console.WriteLine("LevelRequiredForCraftingTier(" + pt.Name + "): " + string.Join(" ", vals.Select(x => { try { return x + "=" + m.Invoke(null, new[] { x }); } catch (Exception e) { return x + "=!" + GameRuntime.Unwrap(e).GetType().Name; } }).ToArray()));
            }
            var lvRewards = (Array)alh.GetField("Rewards", all).GetValue(null);
            Console.WriteLine("reward table levels: " + string.Join(",", lvRewards.Cast<object>().Select(r => r.GetType().GetField("LevelRequirement").GetValue(r).ToString()).ToArray()));
            Console.WriteLine("unlock catalog: " + UnlockCatalogBuilder.Build(game, 1));
            EpidemicServer.Protocol.Account acct = new EpidemicServer.Protocol.Account { UnlockAll = true, MaxLevel = true, UnlimitedCurrency = true };
            EpidemicServer.Protocol.Inventory.EnsureStartingItems(acct);
            EpidemicServer.Protocol.Account v = EpidemicServer.Protocol.AccountView.For(acct);
            Console.WriteLine("view: " + v.Characters.Count + " characters, " + v.Uniques.Count + " weapons, " + v.Gadgets.Count + " gadgets, " + v.Stackables.Count +
                              " stacks, XP " + v.StoryMapXp + ", gold " + v.Gold + "; login data " + EpidemicServer.Protocol.RequestServerEncoders.LoginData(acct, new byte[16], new byte[16], 0).Length +
                              " bytes, features " + string.Join(",", EpidemicServer.Protocol.RequestServerEncoders.CurrentDisabledFeatures().Select(x => x.ToString()).ToArray()));
            // Decode our replies with the game's own deserializers (GameProtocol.MessageSerialization).
            Decode(game, "LoginDataMessage", EpidemicServer.Protocol.RequestServerEncoders.LoginData(acct, new byte[16], new byte[16], 0));
            Decode(game, "GetInventoryResponse", EpidemicServer.Protocol.RequestServerEncoders.InventoryResponse(acct));
            Decode(game, "GetCurrencyResult", EpidemicServer.Protocol.RequestServerEncoders.CurrencyResponse(acct));
            Decode(game, "GetStoryMapResponse", EpidemicServer.Protocol.CatalogueEncoders.StoryMapResponse(EpidemicServer.Protocol.AccountView.For(acct).StoryMapData));
            foreach (string m in new[] { "GetDroppableWeapons", "GetDroppableGadgets", "GetDroppableDesigns" })
                Console.WriteLine(m + ": " + ((ICollection)cm.GetMethod(m, all).Invoke(null, null)).Count);
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + GameRuntime.Unwrap(e)); return 1; }
    }

    private static void Decode(GameRuntime game, string typeName, byte[] bytes)
    {
        const BindingFlags all = GameRuntime.All;
        Type ser = game.Game("GameProtocol.MessageSerialization");
        MethodInfo de = ser.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Deserialize" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.GetElementType().Name == typeName);
        if (de == null) { Console.WriteLine("decode " + typeName + ": the game has no deserializer by that name"); return; }
        Type t = de.GetParameters()[0].ParameterType.GetElementType();
        Type mt = de.GetParameters()[1].ParameterType.GetElementType();
        Func<ArraySegment<byte>> chunks = () => new ArraySegment<byte>(new byte[4096]);
        object msg = Activator.CreateInstance(mt, new System.Collections.Generic.List<ArraySegment<byte>>(), chunks);
        mt.GetMethod("Write", new[] { typeof(byte[]), typeof(int), typeof(int) }).Invoke(msg, new object[] { bytes, 0, bytes.Length });
        PropertyInfo pos = mt.GetProperty("Position"), len = mt.GetProperty("Length");
        pos.SetValue(msg, Convert.ChangeType(0, pos.PropertyType), null);
        object[] args = { Activator.CreateInstance(t), msg };
        bool ok = (bool)de.Invoke(null, args);
        long left = Convert.ToInt64(len.GetValue(args[1], null)) - Convert.ToInt64(pos.GetValue(args[1], null));
        var counts = t.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => typeof(IEnumerable).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(string) && f.FieldType != typeof(byte[]))
            .Select(f => { object v = f.GetValue(args[0]); return f.Name + "=" + (v == null ? "null" : ((IEnumerable)v).Cast<object>().Count().ToString()); });
        Console.WriteLine("decode " + typeName + " (" + bytes.Length + " bytes): " + (ok ? "ok" : "FAILED") + ", " + left + " bytes left; " + string.Join(" ", counts.ToArray()));
    }
}

// Checks our story map blob byte-for-byte against the game's own account-data
// serializer, then decodes it with the game's Deserialize. Loads the game's
// libraries from the owner's install at run time (all game names through roles);
// they are 32-bit only, so this builds as x86. Also runs InventoryCheck.
// Build and run with tests/run_windows_checks.cmd.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;
using EpidemicServer.Protocol;
using System.IO;

public static class StoryMapCheck
{
    private static int _failures;

    public static int Main(string[] args)
    {
        string managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly core = Assembly.LoadFrom(Path.Combine(managed, "StunCore.dll"));
        _crafting = Assembly.LoadFrom(Path.Combine(managed, "ConductorCrafting.dll"));
        Assembly game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        R.Init(new[] { core, _crafting, game });
        PacketType = R.Type("Type.Packet");
        AccountsType = R.Type("Type.AccountBlob");
        Account starting = new Account();
        Inventory.EnsureStartingItems(starting);
        Case("starting rewards node only", starting);
        Account startingHero = Claimed(0);
        Inventory.EnsureStartingItems(startingHero);
        Case("starting rewards node and hero 0", startingHero);
        Case("first character claimed (hero 0)", Claimed(0));
        Case("first character claimed (hero 3)", Claimed(3));
        Case("character points chosen", Claimed(4));
        Case("several nodes and characters", Several());
        Case("empty lists", new Account());
        _failures += InventoryCheck.Run(managed);
        Console.WriteLine((_failures == 0 ? "no failures" : _failures + " checks failed") +
                          (InventoryCheck.Skipped > 0 ? ", " + InventoryCheck.Skipped + " skipped (not verified)" : ""));
        return _failures == 0 ? 0 : 1;
    }

    private static Account Claimed(byte choice)
    {
        Account a = new Account();
        StoryMapRules.Unlock(a, StoryMapRules.FirstCharacterNode, new byte[] { choice });
        return a;
    }

    private static Account Several()
    {
        Account a = Claimed(2);
        StoryMapNode n = new StoryMapNode();
        n.Id = 300;
        n.TimesUnlocked = 3;
        n.Choices.AddRange(new byte[] { 0, 1, 2 });
        a.Nodes.Add(n);
        OwnedCharacter c = new OwnedCharacter();
        c.Id = 6;
        c.Owned = false;
        c.Xp = 123456;
        a.Characters.Add(c);
        return a;
    }

    private static void Case(string name, Account a)
    {
        try
        {
            byte[] ours = StoryMapRules.EncodeLists(a.Nodes, a.Characters);
            byte[] theirs = GameSerialize(a);
            if (!ours.SequenceEqual(theirs))
                throw new Exception("bytes differ:\n ours   " + BitConverter.ToString(ours) + "\n theirs " + BitConverter.ToString(theirs));
            GameDeserialize(ours, a);
            Console.WriteLine("PASS " + name + "  " + BitConverter.ToString(ours));
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine("FAIL " + name + ": " + (e is TargetInvocationException ? e.InnerException : e));
        }
    }

    private static Assembly _crafting;
    private static Type PacketType, AccountsType;
    private static Type NodeType { get { return _crafting.GetType(R.Name("Type.UnlockRecord"), true); } }
    private static Type CharacterType { get { return _crafting.GetType(R.Name("Type.HeroRecord"), true); } }
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>A new game message buffer (its constructor takes a segment list and a segment factory).</summary>
    private static object NewMessage()
    {
        ConstructorInfo c = PacketType.GetConstructors(All).First(x => x.GetParameters().Length == 2);
        Delegate factory = Delegate.CreateDelegate(c.GetParameters()[1].ParameterType, typeof(StoryMapCheck).GetMethod("NewSegment", All));
        return c.Invoke(new object[] { new List<ArraySegment<byte>>(), factory });
    }

    private static ArraySegment<byte> NewSegment() { return new ArraySegment<byte>(new byte[4096]); }

    private static object Prop(object o, string name) { return o.GetType().GetProperty(name, All).GetValue(o, null); }
    private static void SetPosition(object m, long v) { PropertyInfo p = m.GetType().GetProperty("Position", All); p.SetValue(m, Convert.ChangeType(v, p.PropertyType), null); }
    private static long Num(object o, string name) { return Convert.ToInt64(Prop(o, name)); }

    private static MethodInfo AccountsMethod(string name)
    {
        foreach (MethodInfo m in AccountsType.GetMethods(All))
            if (m.Name == name && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == PacketType.MakeByRefType()) return m;
        throw new Exception("account data " + name + " not found");
    }

    private static object Invoke(MethodInfo m, object[] args)
    {
        object target = m.IsStatic ? null : (AccountsType.IsValueType ? Activator.CreateInstance(AccountsType) : null);
        return m.Invoke(target, args);
    }

    private static byte[] GameSerialize(Account a)
    {
        IList nodes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(NodeType));
        foreach (StoryMapNode n in a.Nodes)
        {
            object g = Activator.CreateInstance(NodeType);
            NodeType.GetField(R.Name("UnlockRecord.Node")).SetValue(g, n.Id);
            NodeType.GetField(R.Name("UnlockRecord.Times")).SetValue(g, n.TimesUnlocked);
            NodeType.GetField(R.Name("UnlockRecord.Choices")).SetValue(g, n.Choices.ToArray());
            nodes.Add(g);
        }
        IList characters = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(CharacterType));
        foreach (OwnedCharacter c in a.Characters)
        {
            object g = Activator.CreateInstance(CharacterType);
            FieldInfo id = CharacterType.GetField(R.Name("HeroRecord.Id"));
            id.SetValue(g, Enum.ToObject(id.FieldType, (int)c.Id));
            CharacterType.GetField(R.Name("HeroRecord.Owned")).SetValue(g, c.Owned);
            CharacterType.GetField(R.Name("HeroRecord.Xp")).SetValue(g, c.Xp);
            characters.Add(g);
        }
        object[] args = { NewMessage(), nodes, characters };
        Invoke(AccountsMethod("Serialize"), args);
        object m = args[0];
        SetPosition(m, 0);
        return (byte[])m.GetType().GetMethod(R.Name("Net.Bytes"), All, null, Type.EmptyTypes, null).Invoke(m, null);
    }

    private static void GameDeserialize(byte[] ours, Account a)
    {
        object m = NewMessage();
        m.GetType().GetMethod("Write", All, null, new[] { typeof(byte[]), typeof(int), typeof(int) }, null).Invoke(m, new object[] { ours, 0, ours.Length });
        SetPosition(m, 0);
        object[] args = { m, null, null };
        if (!(bool)Invoke(AccountsMethod("Deserialize"), args)) throw new Exception("game's Deserialize rejected our blob");
        m = args[0];
        if (Num(m, "Position") != Num(m, "Length")) throw new Exception("game's Deserialize left " + (Num(m, "Length") - Num(m, "Position")) + " bytes unread");
        List<object> nodes = ((IEnumerable)args[1]).Cast<object>().ToList();
        List<object> characters = ((IEnumerable)args[2]).Cast<object>().ToList();
        if (nodes.Count != a.Nodes.Count) throw new Exception("game decoded " + nodes.Count + " nodes, expected " + a.Nodes.Count);
        if (characters.Count != a.Characters.Count) throw new Exception("game decoded " + characters.Count + " characters, expected " + a.Characters.Count);
        for (int i = 0; i < characters.Count; i++)
        {
            int id = Convert.ToInt32(CharacterType.GetField(R.Name("HeroRecord.Id")).GetValue(characters[i]));
            bool owned = (bool)CharacterType.GetField(R.Name("HeroRecord.Owned")).GetValue(characters[i]);
            if (id != a.Characters[i].Id || owned != a.Characters[i].Owned)
                throw new Exception("game decoded character " + id + "/" + owned + ", expected " + a.Characters[i].Id + "/" + a.Characters[i].Owned);
        }
    }
}

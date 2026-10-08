// Checks our story map blob byte-for-byte against the game's own
// ConductorCrafting.AccountsData.Serialize, then decodes it with the game's
// Deserialize. Loads the game's libraries from the owner's install at run time;
// they are 32-bit only, so this builds as x86. Also runs InventoryCheck when
// given the game's Managed folder. Build and run with tests\run_windows_checks.cmd.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Protocol;
using StunMessage;

public static class StoryMapCheck
{
    private static int _failures;

    public static int Main(string[] args)
    {
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
        if (args.Length > 0) _failures += InventoryCheck.Run(args[0]);
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
        n.UnlockedTimes = 3;
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

    private static Assembly Crafting { get { return typeof(ConductorCrafting.AccountsData).Assembly; } }
    private static Type NodeType { get { return Crafting.GetType("StoryMapUnlockData", true); } }
    private static Type CharacterType { get { return Crafting.GetType("ConductorCrafting.CharacterData", true); } }

    private static Message NewMessage()
    {
        return new Message(new List<ArraySegment<byte>>(), () => new ArraySegment<byte>(new byte[4096]));
    }

    private static MethodInfo AccountsMethod(string name)
    {
        foreach (MethodInfo m in typeof(ConductorCrafting.AccountsData).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(Message).MakeByRefType()) return m;
        throw new Exception("AccountsData." + name + " not found");
    }

    private static object Invoke(MethodInfo m, object[] args)
    {
        object target = m.IsStatic ? null : (object)default(ConductorCrafting.AccountsData);
        return m.Invoke(target, args);
    }

    private static byte[] GameSerialize(Account a)
    {
        IList nodes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(NodeType));
        foreach (StoryMapNode n in a.Nodes)
        {
            object g = Activator.CreateInstance(NodeType);
            NodeType.GetField("NodeID").SetValue(g, n.Id);
            NodeType.GetField("UnlockedTimes").SetValue(g, n.UnlockedTimes);
            NodeType.GetField("RewardUnlockChoices").SetValue(g, n.Choices.ToArray());
            nodes.Add(g);
        }
        IList characters = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(CharacterType));
        foreach (OwnedCharacter c in a.Characters)
        {
            object g = Activator.CreateInstance(CharacterType);
            FieldInfo id = CharacterType.GetField("Character");
            id.SetValue(g, Enum.ToObject(id.FieldType, (int)c.Id));
            CharacterType.GetField("Owned").SetValue(g, c.Owned);
            CharacterType.GetField("XP").SetValue(g, c.Xp);
            characters.Add(g);
        }
        object[] args = { NewMessage(), nodes, characters };
        Invoke(AccountsMethod("Serialize"), args);
        Message m = (Message)args[0];
        m.Position = 0;
        return m.ToBytes();
    }

    private static void GameDeserialize(byte[] ours, Account a)
    {
        Message m = NewMessage();
        m.Write(ours, 0, ours.Length);
        m.Position = 0;
        object[] args = { m, null, null };
        if (!(bool)Invoke(AccountsMethod("Deserialize"), args)) throw new Exception("game's Deserialize rejected our blob");
        m = (Message)args[0];
        if (m.Position != m.Length) throw new Exception("game's Deserialize left " + (m.Length - m.Position) + " bytes unread");
        List<object> nodes = ((IEnumerable)args[1]).Cast<object>().ToList();
        List<object> characters = ((IEnumerable)args[2]).Cast<object>().ToList();
        if (nodes.Count != a.Nodes.Count) throw new Exception("game decoded " + nodes.Count + " nodes, expected " + a.Nodes.Count);
        if (characters.Count != a.Characters.Count) throw new Exception("game decoded " + characters.Count + " characters, expected " + a.Characters.Count);
        for (int i = 0; i < characters.Count; i++)
        {
            int id = Convert.ToInt32(CharacterType.GetField("Character").GetValue(characters[i]));
            bool owned = (bool)CharacterType.GetField("Owned").GetValue(characters[i]);
            if (id != a.Characters[i].Id || owned != a.Characters[i].Owned)
                throw new Exception("game decoded character " + id + "/" + owned + ", expected " + a.Characters[i].Id + "/" + a.Characters[i].Owned);
        }
    }
}

// Checks our Unique (weapon) encoding byte-for-byte against the game's own
// protocol.Serializer.Serialize(ref Unique, ref Message), loaded
// by reflection from Assembly-CSharp.dll in the owner's install.
// Run from tests\run_windows_checks.cmd.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;
using EpidemicServer.Protocol;
using EpidemicServer.Wire;

public static class InventoryCheck
{
    /// <summary>Checks that could not run on this runtime (not verified).</summary>
    public static int Skipped;

    /// <summary>Runs the checks and returns the number of failures.</summary>
    public static int Run(string managedDir)
    {
        Assembly hub = Assembly.LoadFrom(Path.Combine(managedDir, "Assembly-CSharp.dll"));
        int failures = 0;

        OwnedUnique melee = new OwnedUnique();
        melee.Guid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        melee.SchematicId = Inventory.DefaultMeleeSchematic;
        failures += Case(hub, "default melee weapon (unique)", melee, 1234567890123UL);

        OwnedUnique ranged = new OwnedUnique();
        ranged.Guid = Guid.NewGuid().ToByteArray();
        ranged.SchematicId = Inventory.DefaultRangedSchematic;
        failures += Case(hub, "default ranged weapon (unique)", ranged, 1UL);

        OwnedUnique worn = new OwnedUnique();
        worn.Guid = Guid.NewGuid().ToByteArray();
        worn.SchematicId = 60000;
        worn.InBag = false;
        worn.Durability = 0;
        worn.Ep = 300;
        failures += Case(hub, "broken, stored weapon (unique)", worn, ulong.MaxValue);
        return failures;
    }

    private static int Case(Assembly hub, string name, OwnedUnique u, ulong userId)
    {
        try
        {
            WireWriter w = new WireWriter();
            Inventory.WriteUnique(w, u, userId);
            byte[] ours = w.ToArray();
            byte[] theirs = GameSerialize(hub, u, userId);
            if (!ours.SequenceEqual(theirs))
                throw new Exception("bytes differ:\n ours   " + BitConverter.ToString(ours) + "\n theirs " + BitConverter.ToString(theirs));
            Console.WriteLine("PASS " + name + "  " + BitConverter.ToString(ours));
            return 0;
        }
        catch (Exception e)
        {
            Exception inner = e is TargetInvocationException ? e.InnerException : e;
            if (inner is TypeInitializationException && inner.InnerException is InvalidProgramException)
            {
                // The library's obfuscated start-up code only runs on Mono (see docs/devlog.md).
                Skipped++;
                Console.WriteLine("SKIP " + name + ": NOT VERIFIED, Assembly-CSharp.dll only runs under Mono");
                return 0;
            }
            Console.WriteLine("FAIL " + name + ": " + inner);
            return 1;
        }
    }

    private static byte[] GameSerialize(Assembly hub, OwnedUnique u, ulong userId)
    {
        Type serialization = hub.GetType(R.Name("Type.Serializer"), true);
        MethodInfo serialize = serialization.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m =>
            m.Name == "Serialize" && m.GetParameters().Length == 2 &&
            m.GetParameters()[0].ParameterType.GetElementType().FullName == R.Name("Type.ItemInstance"));
        // Take both types from the signature so they are exactly the ones the serializer binds to.
        Type uniqueType = serialize.GetParameters()[0].ParameterType.GetElementType();
        Type messageType = serialize.GetParameters()[1].ParameterType.GetElementType();

        object unique = Activator.CreateInstance(uniqueType);
        uniqueType.GetField("Guid").SetValue(unique, u.Guid);
        uniqueType.GetField(R.Name("Craft.Blueprint")).SetValue(unique, u.SchematicId);
        uniqueType.GetField(R.Name("Item.Owner")).SetValue(unique, userId);
        uniqueType.GetField(R.Name("Item.Held")).SetValue(unique, u.InBag);
        uniqueType.GetField(R.Name("Item.Wear")).SetValue(unique, u.Durability);
        uniqueType.GetField(R.Name("Item.Points")).SetValue(unique, u.Ep);
        uniqueType.GetField(R.Name("Item.Sockets")).SetValue(unique, new byte[0]);

        Func<ArraySegment<byte>> chunks = () => new ArraySegment<byte>(new byte[4096]);
        object message = Activator.CreateInstance(messageType, new List<ArraySegment<byte>>(), chunks);
        object[] args = { unique, message };
        serialize.Invoke(null, args);
        message = args[1];
        PropertyInfo position = messageType.GetProperty("Position");
        if (position != null) position.SetValue(message, Convert.ChangeType(0, position.PropertyType), null);
        else { FieldInfo f = messageType.GetField("Position"); f.SetValue(message, Convert.ChangeType(0, f.FieldType)); }
        return (byte[])messageType.GetMethod(R.Name("Net.Bytes"), Type.EmptyTypes).Invoke(message, null);
    }
}

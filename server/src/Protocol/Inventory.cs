using System;
using System.Collections.Generic;
using EpidemicServer.Wire;

namespace EpidemicServer.Protocol
{
    /// <summary>A unique inventory item, such as a weapon. Its GUID is made once and kept.</summary>
    public sealed class OwnedUnique
    {
        public byte[] Guid = new byte[16];
        public ushort SchematicId;
        public bool IsInInventory = true;
        public int Durability = FullDurability;
        public int Ep;

        public const int FullDurability = 100;
    }

    /// <summary>A stack of an item (ConductorCrafting.Stackable): id, StackableType, amount.</summary>
    public sealed class OwnedStackable
    {
        public ushort Id;
        public int Type;
        public int Amount;
    }

    /// <summary>Inventory encoding and the items every account starts with.</summary>
    public static class Inventory
    {
        /// <summary>Default melee and ranged weapons, the account's starting rewards (story map node 0).</summary>
        public const ushort DefaultMeleeSchematic = 1005;
        public const ushort DefaultRangedSchematic = 1009;
        public const int StartingRewardsNode = 0;

        /// <summary>
        /// Gives the account any default weapon it lacks and marks the starting
        /// rewards node unlocked. Returns true if anything changed.
        /// </summary>
        public static bool EnsureStartingItems(Account a)
        {
            bool changed = false;
            foreach (ushort schematic in new ushort[] { DefaultMeleeSchematic, DefaultRangedSchematic })
            {
                if (a.Uniques.Exists(u => u.SchematicId == schematic)) continue;
                OwnedUnique item = new OwnedUnique();
                item.Guid = System.Guid.NewGuid().ToByteArray();
                item.SchematicId = schematic;
                a.Uniques.Add(item);
                changed = true;
            }
            if (!a.Nodes.Exists(n => n.Id == StartingRewardsNode))
            {
                StoryMapNode node = new StoryMapNode();
                node.Id = StartingRewardsNode;
                a.Nodes.Insert(0, node);
                changed = true;
            }
            return changed;
        }

        public static void WriteUnique(WireWriter w, OwnedUnique u, ulong userId)
        {
            w.WriteBytes(u.Guid);
            w.WriteUInt16(u.SchematicId);
            w.WriteVarUInt64(userId);
            w.WriteBool(u.IsInInventory);
            w.WriteVarInt32(u.Durability);
            w.WriteVarInt32(u.Ep);
            w.WriteVarUInt32(0);  // no slots
        }

        /// <summary>
        /// One stackable as MessageSerialization.Serialize(ref Stackable) writes it (checked against
        /// the game: id 1256, type 6, user 1, amount 3 is E8-04-06-01-03-00): ushort id, varint
        /// type, varint user id, ushort amount.
        /// </summary>
        public static void WriteStackable(WireWriter w, OwnedStackable s, ulong userId)
        {
            w.WriteUInt16(s.Id);
            w.WriteVarInt32(s.Type);
            w.WriteVarUInt64(userId);
            w.WriteUInt16((ushort)Math.Min(s.Amount, ushort.MaxValue));
        }

        public static void WriteStackables(WireWriter w, Account a)
        {
            w.WriteVarUInt32((uint)a.Stackables.Count);
            foreach (OwnedStackable s in a.Stackables) WriteStackable(w, s, a.UserId);
        }

        /// <summary>Adds to a stack of the same id and type, or starts one.</summary>
        public static void AddStackable(Account a, ushort id, int type, int amount)
        {
            OwnedStackable s = a.Stackables.Find(x => x.Id == id && x.Type == type);
            if (s == null) a.Stackables.Add(new OwnedStackable { Id = id, Type = type, Amount = amount });
            else s.Amount += amount;
        }

        /// <summary>
        /// Gadgets as MessageSerialization.Serialize(ref Gadget) writes them (checked against the game: GUID 01..10,
        /// id 0x1234, user 1, in inventory, stats 09 08 gives 10-01..10-34-12-01-01-02-09-08): bytes GUID, ushort id,
        /// varint user id, bool in inventory, bytes stats.
        /// </summary>
        public static void WriteGadgets(WireWriter w, Account a)
        {
            w.WriteVarUInt32((uint)a.Gadgets.Count);
            foreach (OwnedGadget g in a.Gadgets)
            {
                w.WriteBytes(g.Guid);
                w.WriteUInt16(g.GadgetId);
                w.WriteVarUInt64(a.UserId);
                w.WriteBool(g.IsInInventory);
                w.WriteBytes(g.Stats);
            }
        }

        public static void WriteUniques(WireWriter w, Account a)
        {
            w.WriteVarUInt32((uint)a.Uniques.Count);
            foreach (OwnedUnique u in a.Uniques) WriteUnique(w, u, a.UserId);
        }
    }
}

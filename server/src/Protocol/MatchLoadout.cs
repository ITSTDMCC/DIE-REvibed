using System;
using System.Collections.Generic;
using EpidemicServer.Wire;

namespace EpidemicServer.Protocol
{
    /// <summary>
    /// The character and gear the hub queued with (GetMatchmakingTicketRequest, sent before every queue and
    /// solo match). The request is a list of ConductorCrafting.PlayerQueueData; read field by field, checked
    /// against a logged request (the player, paddle 1005 + pistol 1009, two empty gadget slots):
    /// varint count; per player: varint user id, varint CharacterEnum, varint count + bytes per equipped weapon
    /// GUID, varint count + varint per consumable id, varint count + bytes per gadget GUID, ushort vanity title,
    /// ushort vanity icon, varint count + varint per queue flag, varint starter-quest node, bytes login session,
    /// ushort queued Crossroads difficulty.
    /// The match host plays the next match with it (owner's design, 2026-10-07; before this every match used
    /// the player with the default weapons).
    /// </summary>
    public sealed class MatchLoadout
    {
        public byte Character;
        public OwnedUnique Melee, Ranged;
        public readonly List<OwnedGadget> Gadgets = new List<OwnedGadget>();
        public readonly List<int> Consumables = new List<int>();

        /// <summary>The last loadout the hub queued with (null until one arrives).</summary>
        public static volatile MatchLoadout Current;

        /// <summary>WeaponType values (ConductorCrafting.WeaponType): 1 Fists, 2 Light, 3 Heavy are melee; 4-6 ranged.</summary>
        public static bool IsMelee(int weaponType) { return weaponType >= 1 && weaponType <= 3; }

        /// <summary>
        /// Reads the first player of a ticket request and resolves its GUIDs against the account as the hub sees
        /// it (AccountView, so unlock-all items count). weaponType maps a weapon schematic id to its WeaponType.
        /// </summary>
        public static MatchLoadout Read(byte[] body, Account view, Func<ushort, int> weaponType, out string text)
        {
            WireReader r = new WireReader(body);
            uint players = r.ReadVarUInt32();
            if (players == 0) { text = "no players"; return null; }
            MatchLoadout l = new MatchLoadout();
            r.ReadVarUInt64();   // user id
            l.Character = (byte)r.ReadVarInt32();
            var unknown = new List<string>();
            uint weapons = r.ReadVarUInt32();
            for (uint i = 0; i < weapons; i++)
            {
                byte[] guid = r.ReadBytes();
                if (guid.Length == 0) continue;
                OwnedUnique u = view.Uniques.Find(x => Same(x.Guid, guid));
                if (u == null) { unknown.Add("weapon " + BitConverter.ToString(guid)); continue; }
                if (IsMelee(weaponType(u.SchematicId))) { if (l.Melee == null) l.Melee = u; }
                else if (l.Ranged == null) l.Ranged = u;
            }
            uint consumables = r.ReadVarUInt32();
            for (uint i = 0; i < consumables; i++) { int id = r.ReadVarInt32(); if (id != 0) l.Consumables.Add(id); }
            uint gadgets = r.ReadVarUInt32();
            for (uint i = 0; i < gadgets; i++)
            {
                byte[] guid = r.ReadBytes();
                if (guid.Length == 0) continue;
                OwnedGadget g = view.Gadgets.Find(x => Same(x.Guid, guid));
                if (g == null) unknown.Add("gadget " + BitConverter.ToString(guid));
                else l.Gadgets.Add(g);
            }
            text = "character " + l.Character + ", melee " + (l.Melee == null ? "none" : l.Melee.SchematicId.ToString()) + ", ranged " +
                   (l.Ranged == null ? "none" : l.Ranged.SchematicId.ToString()) + ", gadgets [" + string.Join(",", l.Gadgets.ConvertAll(g => g.GadgetId.ToString()).ToArray()) + "]" +
                   (unknown.Count == 0 ? "" : ", not on the account: " + string.Join(", ", unknown.ToArray()));
            return l;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}

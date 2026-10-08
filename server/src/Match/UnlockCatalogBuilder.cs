using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Protocol;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Fills UnlockCatalog from the game's own data once its libraries are loaded (game thread):
    /// - characters: every hub CharacterEnum value except None and those marked [Hide] (the hub's own rule for
    ///   its character list, CharacterData.GetAllCharacters) that has a hub model (CharacterLoader prefab),
    ///   read in the hub-rewards AppDomain;
    /// - CraftingManager.WeaponSchematics / Trinkets / Parts / Consumables / Designs, without the entries
    ///   tagged Debug (test items) or InGameShop (bought inside a Scavenger match, not account items);
    /// - one rolled gadget per gadget id (Gadget.GenerateTrinket, the game's own roll);
    /// - stack caps (CraftingManager.GetMaxStackableForType) and the XP of the highest level any reward or gear requires.
    /// </summary>
    public static class UnlockCatalogBuilder
    {
        private const BindingFlags All = GameRuntime.All;
        /// <summary>The maxLevel switch's level is at least this (owner's choice).</summary>
        public const int MaxLevelFloor = 50;
        /// <summary>The highest level any reward, gear tier, design or perk requires (49 in this client).</summary>
        public static int RequiredTop;

        public static string Build(GameRuntime g, ulong userId)
        {
            Type cm = g.Crafting("ConductorCrafting.CraftingManager");

            // The match client's CharacterEnum has no [Hide] marks; the hub's does, so ask the hub (its own rule).
            byte[] characters = HubRewards.ListedCharacters(g.Install);
            UnlockCatalog.Characters = characters;

            UnlockCatalog.Weapons = Ids(cm, "WeaponSchematics", "Id");
            UnlockCatalog.WeaponTypes.Clear();
            foreach (object w in (Array)cm.GetField("WeaponSchematics", All).GetValue(null))
                if (w != null) UnlockCatalog.WeaponTypes[Convert.ToUInt16(w.GetType().GetField("Id").GetValue(w))] = Convert.ToInt32(w.GetType().GetField("WeaponType").GetValue(w));
            UnlockCatalog.Parts = Ids(cm, "Parts", "ID");
            UnlockCatalog.Consumables = Ids(cm, "Consumables", "ID");
            UnlockCatalog.ConsumableStacks.Clear();
            foreach (object c in Listed(cm, "Consumables"))
                UnlockCatalog.ConsumableStacks[Convert.ToUInt16(c.GetType().GetField("ID").GetValue(c))] = Convert.ToInt32(c.GetType().GetField("Stacks").GetValue(c));
            UnlockCatalog.Designs = Ids(cm, "Designs", "ID");
            UnlockCatalog.GadgetBlueprints = Ids(cm, "Trinkets", "ID");

            UnlockCatalog.GadgetStats.Clear();
            Type gadget = g.Crafting("ConductorCrafting.Gadget");
            MethodInfo roll = gadget.GetMethod("GenerateTrinket", All);
            foreach (object t in Listed(cm, "Trinkets"))
            {
                object rolled = roll.Invoke(null, new[] { (object)userId, t });
                ushort id = Convert.ToUInt16(t.GetType().GetField("ID").GetValue(t));
                UnlockCatalog.GadgetStats[id] = (byte[])gadget.GetField("Stats").GetValue(rolled) ?? new byte[0];
            }

            UnlockCatalog.MaxStack.Clear();
            MethodInfo maxStack = cm.GetMethod("GetMaxStackableForType", All);
            Type stackType = maxStack.GetParameters()[0].ParameterType;
            foreach (object type in Enum.GetValues(stackType))
            {
                object optional = maxStack.Invoke(null, new[] { type });
                int? cap = OptionalInt(optional);
                if (cap.HasValue) UnlockCatalog.MaxStack[Convert.ToInt32(type)] = cap.Value;
            }

            Type levels = g.Crafting("ConductorCrafting.AccountLevelHelpers");
            // The account level has no cap (the hub's experience bar is given max level 0), so "max level" is the
            // highest level anything asks for: the static reward table (AccountLevelHelpers.Rewards,
            // AccountReward.LevelRequirement) and the gear requirements below.
            Array table = (Array)levels.GetField("Rewards", All).GetValue(null);
            int top = 20;   // at least Heroic Horde's level
            foreach (object r in table) top = Math.Max(top, Convert.ToInt32(r.GetType().GetField("LevelRequirement").GetValue(r)));
            // Gear needs levels too (owner's playtest: items asked for level 38): the level to craft/equip each weapon
            // tier and each design rarity (AccountLevelHelpers.LevelRequiredForCraftingTier; 99 = not craftable).
            // The top weapon tier (16) and legendary designs need 49.
            foreach (MethodInfo m in levels.GetMethods(All).Where(x => x.Name == "LevelRequiredForCraftingTier"))
            {
                Type pt = m.GetParameters()[0].ParameterType;
                IEnumerable<object> args = pt.IsEnum ? Enum.GetValues(pt).Cast<object>()
                                         : Enumerable.Range(1, (int)cm.GetProperty("MaxWeaponTier", All).GetValue(null, null)).Cast<object>();
                foreach (object a in args)
                {
                    int need = Convert.ToInt32(m.Invoke(null, new[] { a }));
                    if (need < 99) top = Math.Max(top, need);
                }
            }
            // Designs and perks carry their own RequiredLevel (ItemSchematicUpgrade.RequiredLevel).
            foreach (string field in new[] { "Designs", "ItemPerks" })
                foreach (object d in (Array)cm.GetField(field, All).GetValue(null) ?? new object[0])
                {
                    if (d == null) continue;
                    int need = Convert.ToInt32(d.GetType().GetProperty("RequiredLevel", All).GetValue(d, null));
                    if (need < 99) top = Math.Max(top, need);
                }
            RequiredTop = top;
            // Owner, 2026-10-07: max level is 50 (one above the highest requirement, 49), or higher if anything needs more.
            top = Math.Max(top, MaxLevelFloor);
            UnlockCatalog.MaxLevel = top;
            UnlockCatalog.MaxLevelXp = (uint)levels.GetMethod("GetRequiredExperience", All).Invoke(null, new object[] { top });
            UnlockCatalog.Ready = true;
            return characters.Length + " characters, " + UnlockCatalog.Weapons.Length + " weapons, " + UnlockCatalog.GadgetBlueprints.Length + " gadgets (" +
                   UnlockCatalog.GadgetStats.Count + " rolled), " + UnlockCatalog.Designs.Length + " designs, " + UnlockCatalog.Parts.Length + " parts, " +
                   UnlockCatalog.Consumables.Length + " consumables; stack caps " +
                   string.Join(", ", UnlockCatalog.MaxStack.Select(kv => kv.Key + "=" + kv.Value).ToArray()) + "; highest level required by anything " + RequiredTop + ", max level " + top + " at story map XP " + UnlockCatalog.MaxLevelXp;
        }

        /// <summary>The entries of a CraftingManager array that an account can own (not Debug, not InGameShop).</summary>
        private static IEnumerable<object> Listed(Type cm, string field)
        {
            Array a = (Array)cm.GetField(field, All).GetValue(null);
            if (a == null) yield break;
            foreach (object o in a)
            {
                if (o == null) continue;
                FieldInfo tags = o.GetType().GetField("Tags");
                Array t = tags == null ? null : (Array)tags.GetValue(o);
                if (t != null && t.Cast<object>().Any(x => x.ToString() == "Debug" || x.ToString() == "InGameShop")) continue;
                yield return o;
            }
        }

        private static ushort[] Ids(Type cm, string field, string idField)
        {
            return Listed(cm, field).Select(o => Convert.ToUInt16(o.GetType().GetField(idField).GetValue(o))).Distinct().ToArray();
        }

        /// <summary>Reads an Optional&lt;int&gt;-like value by its HasValue / Value members (null if empty).</summary>
        private static int? OptionalInt(object optional)
        {
            if (optional == null) return null;
            Type t = optional.GetType();
            MemberInfo has = (MemberInfo)t.GetProperty("HasValue", All) ?? t.GetField("HasValue", All);
            MemberInfo val = (MemberInfo)t.GetProperty("Value", All) ?? t.GetField("Value", All);
            if (has == null || val == null) return null;
            object h = has is PropertyInfo ? ((PropertyInfo)has).GetValue(optional, null) : ((FieldInfo)has).GetValue(optional);
            if (!(bool)h) return null;
            object v = val is PropertyInfo ? ((PropertyInfo)val).GetValue(optional, null) : ((FieldInfo)val).GetValue(optional);
            return Convert.ToInt32(v);
        }
    }
}

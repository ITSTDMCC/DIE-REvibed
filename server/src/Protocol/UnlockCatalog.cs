using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace EpidemicServer.Protocol
{
    /// <summary>A gadget (trinket) item: GUID, gadget id, rolled stats (ConductorCrafting.Gadget).</summary>
    public sealed class OwnedGadget
    {
        public byte[] Guid = new byte[16];
        public ushort GadgetId;
        public bool IsInInventory = true;
        public byte[] Stats = new byte[0];
    }

    /// <summary>
    /// Everything an account could own, read from the game's own data when the match host loads it
    /// (UnlockCatalogBuilder): the characters the hub lists, every weapon, gadget, part, consumable and
    /// design except the Debug and in-match shop entries, the stack caps, and the XP for the top account
    /// level. Plain data only, so the request server can use it without the game's libraries.
    /// </summary>
    public static class UnlockCatalog
    {
        public static bool Ready;
        public static byte[] Characters = new byte[0];
        public static ushort[] Weapons = new ushort[0];
        public static ushort[] Parts = new ushort[0];
        public static ushort[] Consumables = new ushort[0];
        public static ushort[] Designs = new ushort[0];
        public static ushort[] GadgetBlueprints = new ushort[0];
        /// <summary>One rolled gadget per gadget id (stats from the game's own Gadget.GenerateTrinket).</summary>
        public static readonly Dictionary<ushort, byte[]> GadgetStats = new Dictionary<ushort, byte[]>();
        /// <summary>Every weapon schematic's WeaponType (ItemSchematic.WeaponType), Debug ones included.</summary>
        public static readonly Dictionary<ushort, int> WeaponTypes = new Dictionary<ushort, int>();
        /// <summary>Each consumable's own stack size (StructuredConsumable.Stacks).</summary>
        public static readonly Dictionary<ushort, int> ConsumableStacks = new Dictionary<ushort, int>();
        /// <summary>Stack caps per StackableType (CraftingManager.GetMaxStackableForType); missing = no cap known.</summary>
        public static readonly Dictionary<int, int> MaxStack = new Dictionary<int, int>();
        public static int MaxLevel;
        public static uint MaxLevelXp;

        /// <summary>A GUID that is the same on every login, so the hub's equipment keeps pointing at the item.</summary>
        public static byte[] StableGuid(string kind, int id)
        {
            using (MD5 md5 = MD5.Create())
                return md5.ComputeHash(Encoding.UTF8.GetBytes("EpidemicServer.unlockAll:" + kind + ":" + id));
        }

        public static int Cap(int type, int fallback)
        {
            int cap;
            return MaxStack.TryGetValue(type, out cap) && cap > 0 ? cap : fallback;
        }
    }

    /// <summary>
    /// The account as the hub sees it (owner's design, 2026-10-07: a preservation build lets the owner
    /// see everything). The real account is never changed; the switches only add to what is sent:
    /// - unlockAll: every listed character, weapon (blueprint + one crafted copy), gadget (blueprint + one
    ///   copy), design, part and consumable (to the stack cap), and every game mode (Heroic too).
    /// - maxLevel: the account level shown and used in matches is the top one.
    /// - unlimitedCurrency: gold, silver, character and research points shown as at least UnlimitedAmount.
    /// Turning a switch off returns to the real account.
    /// </summary>
    public static class AccountView
    {
        public const int UnlimitedAmount = 9999999;
        /// <summary>Parts (and consumables without a stack size) are topped up to this when the game sets no cap (ours).</summary>
        public const int DefaultStack = 999;

        public static uint EffectiveXp(Account a)
        {
            return a.MaxLevel && UnlockCatalog.Ready ? Math.Max(a.StoryMapXp, UnlockCatalog.MaxLevelXp) : a.StoryMapXp;
        }

        public static Account For(Account real)
        {
            if (!real.UnlockAll && !real.MaxLevel && !real.UnlimitedCurrency) return real;
            Account v = new Account
            {
                UserId = real.UserId, SteamId = real.SteamId, Name = real.Name, Language = real.Language,
                Gold = real.Gold, Silver = real.Silver, CharacterPoints = real.CharacterPoints, ResearchPoints = real.ResearchPoints,
                StoryMapXp = EffectiveXp(real), StoryMapVersion = real.StoryMapVersion, RegularBoost = real.RegularBoost,
                PremiumBoost = real.PremiumBoost, UnboundXp = real.UnboundXp, CreateTime = real.CreateTime, VanityIcon = real.VanityIcon,
                UnlockedDlc = real.UnlockedDlc, LastPremiumGain = real.LastPremiumGain, LastScavengerWinBonusTime = real.LastScavengerWinBonusTime,
                LastHordeWinBonusTime = real.LastHordeWinBonusTime, CounterData = real.CounterData, CrossroadProgress = real.CrossroadProgress,
                FirstLogin = real.FirstLogin, TutorialCompleted = real.TutorialCompleted,
                UnlockAll = real.UnlockAll, MaxLevel = real.MaxLevel, UnlimitedCurrency = real.UnlimitedCurrency,
            };
            v.Nodes.AddRange(real.Nodes);
            foreach (OwnedCharacter c in real.Characters) v.Characters.Add(new OwnedCharacter { Id = c.Id, Owned = c.Owned, Xp = c.Xp });
            v.Uniques.AddRange(real.Uniques);
            v.Gadgets.AddRange(real.Gadgets);
            foreach (OwnedStackable s in real.Stackables) v.Stackables.Add(new OwnedStackable { Id = s.Id, Type = s.Type, Amount = s.Amount });

            if (real.UnlimitedCurrency)
            {
                v.Gold = Math.Max(v.Gold, UnlimitedAmount);
                v.Silver = Math.Max(v.Silver, UnlimitedAmount);
                v.CharacterPoints = Math.Max(v.CharacterPoints, UnlimitedAmount);
                v.ResearchPoints = Math.Max(v.ResearchPoints, UnlimitedAmount);
            }
            if (real.UnlockAll && UnlockCatalog.Ready)
            {
                foreach (byte id in UnlockCatalog.Characters)
                {
                    OwnedCharacter c = v.Characters.Find(x => x.Id == id);
                    if (c == null) v.Characters.Add(new OwnedCharacter { Id = id });
                    else c.Owned = true;
                }
                foreach (ushort id in UnlockCatalog.Weapons)
                {
                    AtLeast(v, id, StackSchematic, 1);
                    if (!v.Uniques.Exists(u => u.SchematicId == id))
                        v.Uniques.Add(new OwnedUnique { Guid = UnlockCatalog.StableGuid("weapon", id), SchematicId = id });
                }
                foreach (ushort id in UnlockCatalog.GadgetBlueprints)
                {
                    AtLeast(v, id, StackGadgetBlueprint, 1);
                    byte[] stats;
                    if (!v.Gadgets.Exists(g => g.GadgetId == id) && UnlockCatalog.GadgetStats.TryGetValue(id, out stats))
                        v.Gadgets.Add(new OwnedGadget { Guid = UnlockCatalog.StableGuid("gadget", id), GadgetId = id, Stats = stats });
                }
                foreach (ushort id in UnlockCatalog.Designs) AtLeast(v, id, StackDesign, 1);
                foreach (ushort id in UnlockCatalog.Parts) AtLeast(v, id, StackPart, UnlockCatalog.Cap(StackPart, DefaultStack));
                foreach (ushort id in UnlockCatalog.Consumables)
                {
                    int stack;
                    AtLeast(v, id, StackConsumable, UnlockCatalog.ConsumableStacks.TryGetValue(id, out stack) && stack > 0 ? stack : UnlockCatalog.Cap(StackConsumable, DefaultStack));
                }
            }
            return v;
        }

        /// <summary>StackableType values (ConductorCrafting.StackableType).</summary>
        public const int StackConsumable = 1, StackSchematic = 2, StackPart = 3, StackGadgetBlueprint = 6, StackDesign = 7;

        private static void AtLeast(Account a, ushort id, int type, int amount)
        {
            OwnedStackable s = a.Stackables.Find(x => x.Id == id && x.Type == type);
            if (s == null) a.Stackables.Add(new OwnedStackable { Id = id, Type = type, Amount = amount });
            else s.Amount = Math.Max(s.Amount, amount);
        }
    }
}

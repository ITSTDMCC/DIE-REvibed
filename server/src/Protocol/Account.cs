using System.Collections.Generic;

namespace EpidemicServer.Protocol
{
    /// <summary>The one local player's account. Field meanings are inferred from their names.</summary>
    public sealed class Account
    {
        public ulong UserId = 1;
        /// <summary>The owner's Steam id; only in the local account file, 0 if unset.</summary>
        public ulong SteamId;
        public string Name = "Player";
        public int Language;

        // Currencies
        public int Gold;
        public int Silver = 1000;
        public int HeroPoints;
        public int LabPoints;

        // Account progress
        public uint StoryMapXp;
        public readonly List<StoryMapNode> Nodes = new List<StoryMapNode>();
        public readonly List<OwnedCharacter> Characters = new List<OwnedCharacter>();
        public readonly List<OwnedUnique> Uniques = new List<OwnedUnique>();
        public readonly List<OwnedStackable> Stackables = new List<OwnedStackable>();
        public readonly List<OwnedGadget> Gadgets = new List<OwnedGadget>();
        public byte[] UnlockTreeData { get { return StoryMapRules.Encode(Nodes, Characters); } }
        public byte UnlockTreeVersion;
        public long BoostNormal;
        public long BoostPaid;
        public uint UnboundXp;
        public long CreateTime;
        public byte ProfileIcon;
        public uint UnlockedDlc;
        public long LastPaidGain;
        public long LastScavengerBonus;
        public long LastHordeBonus;
        public byte[] Counters = new byte[0];
        public ushort CrossroadsState;

        public bool FirstSignIn = true;
        /// <summary>Ours: set when the local match server's tutorial reaches its last stage (24).</summary>
        public bool TutorialCompleted;

        /// <summary>
        /// Preservation switches (owner, 2026-10-07), set in the account file (unlockAll=true, maxLevel=true,
        /// unlimitedCurrency=true) or with toolsccount_switch.cmd. See AccountView: they change what the hub
        /// is sent, never the real progress above.
        /// </summary>
        public bool UnlockAll, MaxLevel, UnlimitedCurrency;
    }
}

using System.Collections.Generic;
using EpidemicServer.Wire;

namespace EpidemicServer.Protocol
{
    /// <summary>One unlocked story map node and the reward choices made on it.</summary>
    public sealed class StoryMapNode
    {
        public int Id;
        public byte UnlockedTimes = 1;
        public List<byte> Choices = new List<byte>();
    }

    /// <summary>A character on the account (ids are the client's character numbers).</summary>
    public sealed class OwnedCharacter
    {
        public byte Id;
        public bool Owned = true;
        public int Xp;
    }

    /// <summary>Outcomes of a story map node unlock, as the client numbers them.</summary>
    public enum StoryMapUnlockResult
    {
        UnknownError = 0,
        Success = 1,
        CannotUnlock = 2,
        RevisionOutdated = 3,
        WrongAmountOfChoices = 4,
        NotEnoughPoints = 5,
        AlreadyUnlocked = 6,
    }

    /// <summary>
    /// Story map unlocks and owned characters. The client keeps both in the
    /// account's story map blob, and the first character is claimed by
    /// unlocking story map node 2.
    /// </summary>
    public static class StoryMapRules
    {
        public const int FirstCharacterNode = 2;
        public const int FirstCharacterPointsChoice = 4;
        public const int FirstCharacterPoints = 7200;

        /// <summary>Node 2 choices 0-3: heroes 0-3 (survivor versions).</summary>
        private static readonly byte[] FirstCharacterChoices = { 7, 5, 8, 6 };

        private const byte BlobVersion = 1;
        private const byte NodeListVersion = 5;
        private const byte CharacterListVersion = 1;

        /// <summary>
        /// Encodes the story map blob. An account with nothing unlocked sends an
        /// empty blob, as before this was implemented.
        /// </summary>
        public static byte[] Encode(List<StoryMapNode> nodes, List<OwnedCharacter> characters)
        {
            if (nodes.Count == 0 && characters.Count == 0) return new byte[0];
            return EncodeLists(nodes, characters);
        }

        /// <summary>The blob layout with both lists always written (also used by the reference check).</summary>
        public static byte[] EncodeLists(List<StoryMapNode> nodes, List<OwnedCharacter> characters)
        {
            WireWriter w = new WireWriter();
            w.WriteByte(BlobVersion);
            w.WriteByte(NodeListVersion);
            w.WriteVarUInt32((uint)nodes.Count);
            foreach (StoryMapNode n in nodes)
            {
                w.WriteVarInt32(n.Id);
                w.WriteByte(n.UnlockedTimes);
                w.WriteByte((byte)n.Choices.Count);
                foreach (byte c in n.Choices) w.WriteByte(c);
            }
            w.WriteByte(CharacterListVersion);
            w.WriteVarUInt32((uint)characters.Count);
            foreach (OwnedCharacter c in characters)
            {
                w.WriteByte(c.Id);
                w.WriteBool(c.Owned);
                w.WriteVarInt32(c.Xp);
            }
            return w.ToArray();
        }

        /// <summary>Applies an unlock to the account. Only the first-character node grants anything so far.</summary>
        public static StoryMapUnlockResult Unlock(Account a, int nodeId, byte[] choices)
        {
            foreach (StoryMapNode n in a.Nodes)
                if (n.Id == nodeId) return StoryMapUnlockResult.AlreadyUnlocked;

            if (nodeId == FirstCharacterNode)
            {
                if (choices.Length != 1) return StoryMapUnlockResult.WrongAmountOfChoices;
                byte choice = choices[0];
                if (choice < FirstCharacterChoices.Length)
                    AddCharacter(a, FirstCharacterChoices[choice]);
                else if (choice == FirstCharacterPointsChoice)
                    a.CharacterPoints += FirstCharacterPoints;
                else
                    return StoryMapUnlockResult.CannotUnlock;
            }

            StoryMapNode node = new StoryMapNode();
            node.Id = nodeId;
            node.Choices.AddRange(choices);
            a.Nodes.Add(node);
            return StoryMapUnlockResult.Success;
        }

        private static void AddCharacter(Account a, byte id)
        {
            foreach (OwnedCharacter c in a.Characters)
                if (c.Id == id) { c.Owned = true; return; }
            OwnedCharacter added = new OwnedCharacter();
            added.Id = id;
            a.Characters.Add(added);
        }

        public static byte[] UnlockResponse(StoryMapUnlockResult result, int revision)
        {
            WireWriter w = new WireWriter();
            w.WriteVarInt32((int)result);
            w.WriteVarUInt32(0);   // unique rewards
            w.WriteVarInt32(revision);
            return w.ToArray();
        }
    }
}

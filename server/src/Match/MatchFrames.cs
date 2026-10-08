using System.Collections.Generic;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Sections of a server-to-client GameplayData frame, per the owner's
    /// design notes (2026-10-06), written with the game's bit buffer.
    /// </summary>
    public static class MatchFrames
    {
        public const int ControllerSlots = 16;

        /// <summary>
        /// Controllers: flag 1, then 16 slots. Slot 0 is the local client: a 1
        /// followed by the game's own Client.ControllerServerToClientSerialize
        /// (local bits, look-at, server frame). Slots 1-15 are empty.
        /// </summary>
        public static void WriteControllers(GameBuffer b, object client0, GameRuntime game = null)
        {
            b.Write(true);
            b.Write(true);
            client0.GetType().GetMethod("ControllerServerToClientSerialize", GameRuntime.All).Invoke(client0, new[] { b.Buffer, (object)true });
            for (int slot = 1; slot < ControllerSlots; slot++)
            {
                // Bot slots: their controller as a remote client's (not local), so the human sees their aim and moves.
                if (game != null && game.BotClients.Contains(slot))
                {
                    object c = game.GetClient(slot);
                    b.Write(true);
                    c.GetType().GetMethod("ControllerServerToClientSerialize", GameRuntime.All).Invoke(c, new[] { b.Buffer, (object)false });
                }
                else b.Write(false);
            }
        }

        /// <summary>
        /// Syncs: flag 1, item count as a ranged 0..N, a server-count check (1
        /// and the ushort N), then each item as its ranged index 0..N followed by
        /// the object's own Serialize(buffer, viewer).
        /// </summary>
        public static void WriteSyncs(GameRuntime game, GameBuffer b, IList<object> items, object viewer)
        {
            if (items.Count == 0)
            {
                b.Write(false);
                return;
            }
            int n = game.NumOfSynchronizables;
            b.Write(true);
            b.WriteRanged(0, n, items.Count);
            b.Write(true);
            b.Write((ushort)n);
            foreach (object item in items)
            {
                b.WriteRanged(0, n, game.SynchronizableIndex(item));
                item.GetType().GetMethod("Serialize", GameRuntime.All, null,
                    new[] { game.Game("StunGameNetwork.INetBuffer"), game.Game("StunGameNetwork.IClient") }, null)
                    .Invoke(item, new[] { b.Buffer, viewer });
            }
        }
    }
}

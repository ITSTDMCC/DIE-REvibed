using System;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// The server hail sent in reply to MatchSignIn, per the owner's
    /// design (2026-10-06): a net-debug flag, the client index, the client info,
    /// then the game info last.
    /// </summary>
    public static class MatchHail
    {
        /// <summary>
        /// Writes the client info field by field, as the owner's design lists
        /// it. Used to check our layout against the owner's known-good hail.
        /// </summary>
        public static byte[] BuildFieldByField(GameRuntime game, byte clientIndex, ulong steamId, ulong userId, int playerIndex,
                                               object clientInfoData, object gameInfo)
        {
            GameBuffer b = GameBuffer.Create();
            b.Write(false);              // net debug
            b.Write(clientIndex);
            b.Write(steamId);
            b.Write(userId);
            b.Write(false);              // is observer
            b.Write(true);               // is connected
            b.Write(false);              // is reconnected
            b.Write((uint)0);            // debug flags
            b.Write((byte)0);            // early access
            b.Write((ushort)0);          // vanity title
            b.Write((ushort)0);          // vanity icon
            b.Write(false);              // premium boost active
            b.Write(true);               // has player
            b.Write((ushort)playerIndex);
            clientInfoData.GetType().GetMethod("Serialize", GameRuntime.All).Invoke(clientInfoData, new[] { b.Buffer, (object)true });
            WriteGameInfo(b, gameInfo);
            return b.ToBytes();
        }

        /// <summary>
        /// The same hail with the client info written by the game's own
        /// Net.WriteClientInfo, so the server's client
        /// object and what the client is told stay in step. The client must
        /// already have its player (PrepareLocalPlayer).
        /// </summary>
        public static byte[] Build(byte clientIndex, object client, object gameInfo)
        {
            GameBuffer b = GameBuffer.Create();
            b.Write(false);              // net debug
            b.Write(clientIndex);
            client.GetType().GetMethod(R.Name("Net.WriteClientInfo"), GameRuntime.All).Invoke(client, new[] { b.Buffer, (object)true });
            WriteGameInfo(b, gameInfo);
            return b.ToBytes();
        }

        private static void WriteGameInfo(GameBuffer b, object gameInfo)
        {
            b.Write(true);               // has game info
            gameInfo.GetType().GetMethod("Serialize", GameRuntime.All).Invoke(gameInfo, new[] { b.Buffer });
        }
    }
}

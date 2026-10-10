using System;
using System.Net;
using System.Threading;
using EpidemicServer.Net;
using EpidemicServer.Wire;

namespace EpidemicServer.Services
{
    /// <summary>
    /// The matchmaking server (default port 2555).
    /// - Solo matches (tutorial, practice): SoloServer, answered with our match server's address.
    /// - Queued matches (Horde): the hub's normal queue flow (matchmaking protocol, layouts checked against the
    ///   hub's own serializer), for a one-player party:
    ///   JoinQueue -> join queue response; MatchMade; the hub's ConfirmReady (sent by the
    ///   lobby owner after its "ready" popup); ReadyTally; FindingServer; ServerReady (a
    ///   request from us, answered by an empty server found affirmative message), after which the hub starts the
    ///   match client against our match server.
    /// </summary>
    public sealed class MatchmakingService : ILinkHandler
    {
        public const ushort JoinQueue = 1, ServerReady = 2, ConfirmReady = 3, MatchMade = 4,
                            FindingServer = 6, QuitQueue = 7, ActiveMatch = 9, SoloServer = 10,
                            ReadyTally = 12;

        private const int AuthSuccess = 1;
        private const int JoinQueueSuccess = 1;
        /// <summary>
        /// MatchupFound: the number of players who must ready up. The hub draws one ready icon per player, centred
        /// on this count (GUI-game-found-popup.Show(int); on matchup found message). It was sent as 30 (read as a
        /// timeout), so the hub's 12 icons were laid out for 30 and sat off to the left (owner, 2026-10-07). Only the
        /// owner readies (bots join on the server), and ReadyTally then reports 1 of 1.
        /// </summary>
        private const int PlayersToReady = 1;
        /// <summary>Horde Normal maps in turn (stand-in: the original matchmaker's map choice is unknown): Outpost, Lab, Club.</summary>
        private static readonly int[] HordeMaps = { 5, 6, 7 };
        private static int _nextHordeMap;
        /// <summary>Scavenger maps in turn (stand-in, as Horde): Resort, Jungle, Expedition (the three in matchmaking).</summary>
        private static readonly int[] ScavengerMaps = { 1, 2, 3 };
        private static int _nextScavengerMap;

        private readonly byte[] _matchAddress;
        private readonly ushort _matchPort;
        private readonly Match.MatchHost _host;

        private sealed class QueueState
        {
            public int QueueType;
            public byte[] MatchGuid;
            public Timer Timer;
            public bool Matched;
        }

        public MatchmakingService(IPAddress matchAddress, ushort matchPort, Match.MatchHost host = null)
        {
            _matchAddress = matchAddress.GetAddressBytes();
            _matchPort = matchPort;
            _host = host;
        }

        public string Name { get { return "matchmaking"; } }

        public void OnConnected(Connection connection) { }

        public void OnDisconnected(Connection connection)
        {
            QueueState q = connection.State as QueueState;
            if (q != null && q.Timer != null) q.Timer.Dispose();
        }

        public void OnPacket(Connection c, Packet p)
        {
            if (p.Kind == PacketKind.Request && p.Type == SoloServer)
            {
                Log.Info("matchmaking: type " + p.Type);
                c.Respond(p.RequestId, ResultCode.OK, HandleSoloServerCreate(p));
                return;
            }
            if (p.Kind == PacketKind.Request && p.Type == ActiveMatch)
            {
                // No running match to rejoin: SignInResult Success, has match false, AFK false, empty address, port 0, empty GUID.
                WireWriter w = new WireWriter();
                w.WriteVarInt32(AuthSuccess);
                w.WriteBool(false);
                w.WriteBool(false);
                w.WriteBytes(new byte[0]);
                w.WriteUInt16(0);
                w.WriteBytes(new byte[0]);
                c.Respond(p.RequestId, ResultCode.OK, w.ToArray());
                Log.Info("matchmaking: get active match: none");
                return;
            }
            if (p.Kind == PacketKind.Request && p.Type == JoinQueue)
            {
                HandleJoinQueue(c, p);
                return;
            }
            if (p.Kind == PacketKind.Message && p.Type == ConfirmReady)
            {
                HandleReady(c, p);
                return;
            }
            if (p.Kind == PacketKind.Message && p.Type == QuitQueue)
            {
                QueueState q = c.State as QueueState;
                if (q != null && q.Timer != null) q.Timer.Dispose();
                c.State = null;
                Log.Info("matchmaking: left the queue");
                return;
            }
            if (p.Kind == PacketKind.Response)
            {
                Log.Info("matchmaking: response " + p.Result + " body " + BitConverter.ToString(p.Body) + " (ServerFoundAffirmative)");
                return;
            }
            Log.Warn("matchmaking: unimplemented " + p.Kind + " type " + p.Type + " body " + BitConverter.ToString(p.Body));
            if (p.Kind == PacketKind.Request)
                c.Respond(p.RequestId, ResultCode.Unrecognised, null);
        }

        /// <summary>auth data as the hub writes it: session ticket, four version numbers, branch, auth value.</summary>
        private static string ReadAuth(WireReader r)
        {
            r.ReadBytes();
            int major = r.ReadVarInt32(), minor = r.ReadVarInt32(), build = r.ReadVarInt32(), revision = r.ReadVarInt32();
            string branch = r.ReadString();
            r.ReadVarInt32();
            return major + "." + minor + "." + build + "." + revision + " '" + branch + "'";
        }

        /// <summary>JoinQueue: auth data, matchmaking ticket, skill rating data[] (four varints each), QueueType.</summary>
        private void HandleJoinQueue(Connection c, Packet p)
        {
            WireReader r = new WireReader(p.Body);
            string version = ReadAuth(r);
            byte[] ticket = r.ReadBytes();
            uint skills = r.ReadVarUInt32();
            for (uint i = 0; i < skills * 4; i++) r.ReadVarInt32();
            int queueType = r.ReadVarInt32();
            Log.Info("matchmaking: join queue " + queueType + " (version " + version + ", ticket " + ticket.Length + " bytes, " + skills + " skill entries)");

            WireWriter w = new WireWriter();
            w.WriteVarInt32(AuthSuccess);
            w.WriteVarInt32(JoinQueueSuccess);
            w.WriteVarUInt32(0);   // no time-out data
            c.Respond(p.RequestId, ResultCode.OK, w.ToArray());

            QueueState q = new QueueState { QueueType = queueType, MatchGuid = Guid.NewGuid().ToByteArray() };
            c.State = q;
            // One-player party: matched straight away (after a short beat, so the hub shows its queue state).
            q.Timer = new Timer(delegate (object _)
            {
                try
                {
                    WireWriter m = new WireWriter();
                    m.WriteBytes(q.MatchGuid);
                    m.WriteVarInt32(q.QueueType);
                    m.WriteVarInt32(PlayersToReady);
                    c.SendMessage(MatchMade, m.ToArray());
                    Log.Info("matchmaking: matchup found for queue " + q.QueueType + " (match " + BitConverter.ToString(q.MatchGuid) + ")");
                }
                catch (Exception e) { Log.Warn("matchmaking: could not send the matchup: " + e.Message); }
            }, null, 1500, Timeout.Infinite);
        }

        /// <summary>ConfirmReady from the lobby owner (ready count, match GUID): ready count, finding server, server found.</summary>
        private void HandleReady(Connection c, Packet p)
        {
            QueueState q = c.State as QueueState;
            WireReader r = new WireReader(p.Body);
            int count = 0;
            byte[] guid = null;
            try { count = r.ReadVarInt32(); guid = r.ReadBytes(); }
            catch (Exception) { }
            Log.Info("matchmaking: ready for match (" + count + " ready, body " + BitConverter.ToString(p.Body) + ")");
            if (q == null || q.Matched) return;
            q.Matched = true;

            WireWriter ready = new WireWriter();
            ready.WriteVarInt32(1);
            ready.WriteBytes(q.MatchGuid);
            c.SendMessage(ReadyTally, ready.ToArray());

            WireWriter finding = new WireWriter();
            finding.WriteBytes(q.MatchGuid);
            c.SendMessage(FindingServer, finding.ToArray());

            int map = PickMap(q.QueueType);
            if (_host != null) _host.RequestMatch(q.QueueType, map);

            WireWriter found = new WireWriter();
            found.WriteBytes(q.MatchGuid);
            found.WriteBytes(_matchAddress);
            found.WriteUInt16(_matchPort);
            found.WriteVarInt32(q.QueueType);
            found.WriteVarInt32(0);   // matchmaking region
            c.Send(Packet.EncodeRequest(Guid.NewGuid().ToByteArray(), ServerReady, found.ToArray()));
            Log.Info("matchmaking: server found for queue " + q.QueueType + ", map " + map + ": " + new IPAddress(_matchAddress) + ":" + _matchPort);
        }

        private static int PickMap(int queueType)
        {
            if (queueType == 1)
            {
                int map = ScavengerMaps[_nextScavengerMap % ScavengerMaps.Length];
                _nextScavengerMap++;
                return map;
            }
            if (queueType == 2 || queueType == 3)
            {
                int map = HordeMaps[_nextHordeMap % HordeMaps.Length];
                _nextHordeMap++;
                return map;
            }
            return -1;
        }

        /// <summary>Logs the solo match request and answers with our match server's address. Any ticket is accepted.</summary>
        private byte[] HandleSoloServerCreate(Packet p)
        {
            WireReader r = new WireReader(p.Body);
            byte[] sessionTicket = r.ReadBytes();
            int major = r.ReadVarInt32(), minor = r.ReadVarInt32(), build = r.ReadVarInt32(), revision = r.ReadVarInt32();
            string branch = r.ReadString();
            int authValue = r.ReadVarInt32();
            byte[] matchmakingTicket = r.ReadBytes();
            int queueType = r.ReadVarInt32();
            int mapIndex = r.ReadVarInt32();
            Log.Info("matchmaking: solo server create: session ticket " + BitConverter.ToString(sessionTicket) +
                     ", version " + major + "." + minor + "." + build + "." + revision + ", branch '" + branch +
                     "', auth value " + authValue + ", matchmaking ticket " + BitConverter.ToString(matchmakingTicket) +
                     ", queue type " + queueType + ", map index " + mapIndex);

            // The match server builds the asked-for map for the next match client (practice:
            // queue 6, map 15/16/17; the tutorial: queue 5, map 4).
            if (_host != null) _host.RequestMatch(queueType, mapIndex);

            WireWriter w = new WireWriter();
            w.WriteVarInt32(AuthSuccess);
            w.WriteVarInt32(JoinQueueSuccess);
            w.WriteBytes(_matchAddress);
            w.WriteUInt16(_matchPort);
            Log.Info("matchmaking: sent match server " + new IPAddress(_matchAddress) + ":" + _matchPort);
            return w.ToArray();
        }
    }
}

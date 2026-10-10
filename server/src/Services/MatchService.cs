using System;
using System.Collections.Generic;
using System.Threading;
using EpidemicServer.Match;
using EpidemicServer.Net;
using EpidemicServer.Wire;

namespace EpidemicServer.Services
{
    /// <summary>
    /// First step of the match server (TCP 3555, same framing as 1555). The
    /// match client falls back to TCP after its UDP attempts. We answer its
    /// gameplay auth with a fixed server hail, then send empty game frames
    /// about ten times a second, and log the frames the client sends back.
    /// </summary>
    public sealed class MatchService : ILinkHandler
    {
        public const ushort MatchSignIn = 5;
        public const ushort MatchFrame = 38;

        private const int FrameIntervalMs = 100;
        /// <summary>With the game logic running, client frames vary with input; log only the first few distinct ones.</summary>
        private const int MaxDistinctLogged = 40;
        private const int GameplayAuthSuccess = 1;

        /// <summary>An empty game frame: varint 1, then one zero byte.</summary>
        private static readonly byte[] EmptyFrame = { 1, 0 };

        private sealed class Session
        {
            public Timer Frames;
            public int FramesSent;
            public bool Attached;
            public int FramesReceived;
            public readonly Dictionary<string, int> DistinctReceived = new Dictionary<string, int>();
        }

        private readonly MatchHost _host;

        /// <summary>Without a game logic host the fixed hail is sent.</summary>
        public MatchService(MatchHost host) { _host = host; }

        public string Name { get { return "match"; } }

        public void OnConnected(Connection c)
        {
            c.State = new Session();
        }

        public void OnDisconnected(Connection c)
        {
            Session s = (Session)c.State;
            lock (s)
            {
                if (s.Frames != null) s.Frames.Dispose();
                if (s.Attached) _host.Detach();
                Log.Info("match: " + c.Remote + " sent " + s.FramesReceived + " gameplay frames (" + s.DistinctReceived.Count +
                         " distinct), we sent " + s.FramesSent);
                int listed = 0;
                foreach (KeyValuePair<string, int> kv in s.DistinctReceived)
                    if (listed++ < MaxDistinctLogged) Log.Info("match:   x" + kv.Value + "  " + kv.Key);
            }
        }

        public void OnPacket(Connection c, Packet p)
        {
            Session s = (Session)c.State;
            if (p.Kind == PacketKind.Request && p.Type == MatchSignIn)
            {
                HandleGameplayAuth(c, p, s);
                return;
            }
            if (p.Type == MatchFrame && p.Kind != PacketKind.Request)
            {
                string hex = p.Body.Length == 0 ? "(empty)" : BitConverter.ToString(p.Body);
                lock (s)
                {
                    s.FramesReceived++;
                    int seen;
                    s.DistinctReceived.TryGetValue(hex, out seen);
                    s.DistinctReceived[hex] = seen + 1;
                    if (seen == 0 && s.DistinctReceived.Count <= MaxDistinctLogged)
                        Log.Info("match: new gameplay frame #" + s.DistinctReceived.Count + " (frame " + s.FramesReceived + "): " + hex);
                    if (s.FramesReceived % 100 == 0)
                        Log.Info("match: " + s.FramesReceived + " gameplay frames received, " + s.DistinctReceived.Count + " distinct");
                }
                if (s.Attached) _host.OnClientFrame(new WireReader(p.Body).ReadBytes());
                return;
            }
            Log.Warn("match: unimplemented " + p.Kind + " type " + p.Type + " body " + BitConverter.ToString(p.Body));
            if (p.Kind == PacketKind.Request)
                c.Respond(p.RequestId, ResultCode.Unrecognised, null);
        }

        private void HandleGameplayAuth(Connection c, Packet p, Session s)
        {
            WireReader r = new WireReader(p.Body);
            int serverPort = r.ReadVarInt32();
            byte[] hail = r.ReadBytes();
            Log.Info("match: gameplay auth, server port " + serverPort + ", " + hail.Length + "-byte hail " + BitConverter.ToString(hail));

            // The hail is built at run time with the game's own serializers, so a match needs the game logic
            // (tools/run_server_mono.cmd, which passes --game).
            byte[] serverHail = null;
            bool ready = _host != null && _host.WaitReady(30000);
            if (ready)
            {
                try
                {
                    serverHail = _host.BuildHail(hail);
                    Log.Info("match: built the server hail with the game's serializers: " + BitConverter.ToString(serverHail));
                }
                catch (Exception e) { Log.Error("match: could not build the server hail: " + e.Message); }
            }
            if (serverHail == null)
            {
                Log.Error(ready ? "match: refusing this match (see the error above); the next one is built afresh"
                                : "match: no game logic loaded (start the server with tools/run_server_mono.cmd); refusing the match");
                c.Close();
                return;
            }
            WireWriter w = new WireWriter();
            w.WriteVarInt32(GameplayAuthSuccess);
            w.WriteBytes(serverHail);
            c.Respond(p.RequestId, ResultCode.OK, w.ToArray());
            if (_host != null)
            {
                // The game logic sends the frames from now on.
                Log.Info("match: sent server hail (" + serverHail.Length + " bytes); the game logic takes over the frames");
                _host.Attach(frame =>
                {
                    WireWriter fw = new WireWriter();
                    fw.WriteBytes(frame);
                    try { c.SendMessage(MatchFrame, fw.ToArray()); } catch (Exception) { }  // the receive loop notices
                });
                s.Attached = true;
                return;
            }
        }

        private static byte[] Hex(string dashed)
        {
            string[] parts = dashed.Split('-');
            byte[] bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }
    }
}

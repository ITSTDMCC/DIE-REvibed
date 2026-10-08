using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EpidemicServer.Wire;

namespace EpidemicServer.Net
{
    /// <summary>Handles the packets of one service (request, matchmaking or stats).</summary>
    public interface ILinkHandler
    {
        string Name { get; }
        void OnConnected(Connection connection);
        void OnPacket(Connection connection, Packet packet);
        void OnDisconnected(Connection connection);
    }

    /// <summary>A connected client on one of the TCP services.</summary>
    public sealed class Connection
    {
        private readonly Socket _socket;
        private readonly object _sendGate = new object();
        public readonly string Remote;
        public object State;

        public Connection(Socket socket)
        {
            _socket = socket;
            Remote = socket.RemoteEndPoint.ToString();
        }

        public void Send(byte[] frame)
        {
            lock (_sendGate)
            {
                _socket.Send(frame);
            }
        }

        public void SendMessage(ushort type, byte[] body) { Send(Packet.EncodeMessage(type, body)); }

        public void Respond(byte[] requestId, RequestResult result, byte[] body)
        {
            Send(Packet.EncodeResponse(requestId, result, body));
        }

        public void Close()
        {
            try { _socket.Close(); } catch (Exception) { }
        }

        internal int Receive(byte[] buffer)
        {
            return _socket.Receive(buffer);
        }
    }

    /// <summary>Accepts TCP clients on one port and feeds their frames to a handler.</summary>
    public sealed class LinkServer
    {
        private readonly ILinkHandler _handler;
        private readonly TcpListener _listener;

        public LinkServer(IPAddress address, int port, ILinkHandler handler)
        {
            _handler = handler;
            _listener = new TcpListener(address, port);
        }

        public int Port { get { return ((IPEndPoint)_listener.LocalEndpoint).Port; } }

        public void Start()
        {
            _listener.Start();
            Log.Info(_handler.Name + " listening on " + _listener.LocalEndpoint);
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = _handler.Name + " accept";
            t.Start();
        }

        public void Stop()
        {
            _listener.Stop();
        }

        private void AcceptLoop()
        {
            while (true)
            {
                Socket socket;
                try { socket = _listener.AcceptSocket(); }
                catch (Exception) { return; }
                socket.NoDelay = true;
                Connection c = new Connection(socket);
                Thread t = new Thread(delegate () { ReceiveLoop(c); });
                t.IsBackground = true;
                t.Name = _handler.Name + " " + c.Remote;
                t.Start();
            }
        }

        private void ReceiveLoop(Connection c)
        {
            Log.Info(_handler.Name + ": client connected from " + c.Remote);
            FrameSplitter splitter = new FrameSplitter();
            byte[] buffer = new byte[64 * 1024];
            try
            {
                _handler.OnConnected(c);
                while (true)
                {
                    int count = c.Receive(buffer);
                    if (count <= 0) break;
                    foreach (byte[] frame in splitter.Push(buffer, count))
                    {
                        Packet p = Packet.Decode(frame);
                        try { _handler.OnPacket(c, p); }
                        catch (Exception e)
                        {
                            Log.Error(_handler.Name + ": failed handling packet type " + p.Type + ": " + e);
                            if (p.Kind == PacketKind.Request)
                                c.Respond(p.RequestId, RequestResult.UnrecognizedError, null);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(_handler.Name + ": connection " + c.Remote + " ended: " + e.Message);
            }
            c.Close();
            try { _handler.OnDisconnected(c); }
            catch (Exception e) { Log.Error(_handler.Name + ": disconnect handling failed: " + e); }
            Log.Info(_handler.Name + ": client " + c.Remote + " disconnected");
        }
    }
}

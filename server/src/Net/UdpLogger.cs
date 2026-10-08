using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EpidemicServer.Net
{
    /// <summary>
    /// Listens on a UDP port and logs every datagram with its sender. It never
    /// replies; it exists to capture the match client's first handshake.
    /// </summary>
    public sealed class UdpLogger
    {
        private readonly string _name;
        private readonly UdpClient _socket;

        public UdpLogger(string name, IPAddress address, int port)
        {
            _name = name;
            _socket = new UdpClient(new IPEndPoint(address, port));
        }

        public void Start()
        {
            Log.Info(_name + " listening on udp " + _socket.Client.LocalEndPoint + " (log only, no replies)");
            Thread t = new Thread(ReceiveLoop);
            t.IsBackground = true;
            t.Name = _name + " udp";
            t.Start();
        }

        private void ReceiveLoop()
        {
            while (true)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = _socket.Receive(ref from); }
                catch (SocketException e)
                {
                    // Windows reports an earlier send's ICMP "port unreachable" here; keep listening.
                    Log.Warn(_name + ": udp receive error " + e.SocketErrorCode);
                    continue;
                }
                catch (ObjectDisposedException) { return; }
                Log.Info(_name + ": udp " + data.Length + " bytes from " + from + ": " + BitConverter.ToString(data));
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace EpidemicServer.Wire
{
    public enum PacketKind : byte
    {
        Message = 1,
        Request = 2,
        Response = 3
    }

    /// <summary>Outcome code carried by every response, ahead of its body.</summary>
    public enum RequestResult
    {
        OK = 0,
        Disconnected = 1,
        Timeout = 2,
        DeserializeFail = 3,
        UnrecognizedError = 4,
        PermissionDenied = 5
    }

    /// <summary>
    /// One decoded frame. Frames are: uint32 total length (including these 4
    /// bytes), a packet kind byte, then
    ///   Message:  uint16 type, body
    ///   Request:  16-byte request id, uint16 type, body
    ///   Response: 16-byte request id, varint result, body
    /// A frame whose length field is 0 is a keep-alive and carries nothing else.
    /// </summary>
    public sealed class Packet
    {
        public PacketKind Kind;
        public byte[] RequestId;
        public ushort Type;
        public int Result;
        public byte[] Body;

        public static byte[] EncodeMessage(ushort type, byte[] body)
        {
            WireWriter w = new WireWriter();
            w.WriteByte((byte)PacketKind.Message);
            w.WriteUInt16(type);
            w.WriteRaw(body);
            return Frame(w.ToArray());
        }

        public static byte[] EncodeRequest(byte[] requestId, ushort type, byte[] body)
        {
            WireWriter w = new WireWriter();
            w.WriteByte((byte)PacketKind.Request);
            w.WriteRaw(requestId);
            w.WriteUInt16(type);
            w.WriteRaw(body);
            return Frame(w.ToArray());
        }

        public static byte[] EncodeResponse(byte[] requestId, RequestResult result, byte[] body)
        {
            WireWriter w = new WireWriter();
            w.WriteByte((byte)PacketKind.Response);
            w.WriteRaw(requestId);
            w.WriteVarInt32((int)result);
            if (result == RequestResult.OK && body != null) w.WriteRaw(body);
            return Frame(w.ToArray());
        }

        private static byte[] Frame(byte[] payload)
        {
            byte[] frame = new byte[payload.Length + 4];
            uint total = (uint)frame.Length;
            for (int i = 0; i < 4; i++) frame[i] = (byte)(total >> (8 * i));
            Array.Copy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        public static Packet Decode(byte[] frame)
        {
            WireReader r = new WireReader(frame, 4, frame.Length - 4);
            Packet p = new Packet();
            p.Kind = (PacketKind)r.ReadByte();
            switch (p.Kind)
            {
                case PacketKind.Message:
                    p.Type = r.ReadUInt16();
                    break;
                case PacketKind.Request:
                    p.RequestId = r.ReadRaw(16);
                    p.Type = r.ReadUInt16();
                    break;
                case PacketKind.Response:
                    p.RequestId = r.ReadRaw(16);
                    p.Result = r.ReadVarInt32();
                    break;
                default:
                    throw new FormatException("Unknown packet kind " + (byte)p.Kind);
            }
            p.Body = r.ReadRaw(r.Remaining);
            return p;
        }
    }

    /// <summary>Splits a TCP byte stream into frames.</summary>
    public sealed class FrameSplitter
    {
        private byte[] _pending = new byte[0];

        /// <summary>Adds received bytes and returns every complete frame (keep-alives are dropped).</summary>
        public List<byte[]> Push(byte[] data, int count)
        {
            byte[] merged = new byte[_pending.Length + count];
            Array.Copy(_pending, merged, _pending.Length);
            Array.Copy(data, 0, merged, _pending.Length, count);

            List<byte[]> frames = new List<byte[]>();
            int offset = 0;
            while (merged.Length - offset >= 4)
            {
                uint length = (uint)(merged[offset] | merged[offset + 1] << 8 | merged[offset + 2] << 16 | merged[offset + 3] << 24);
                if (length == 0) { offset += 4; continue; }
                if (length < 5 || length > 16 * 1024 * 1024) throw new FormatException("Bad frame length " + length);
                if (merged.Length - offset < length) break;
                byte[] frame = new byte[length];
                Array.Copy(merged, offset, frame, 0, (int)length);
                frames.Add(frame);
                offset += (int)length;
            }
            _pending = new byte[merged.Length - offset];
            Array.Copy(merged, offset, _pending, 0, _pending.Length);
            return frames;
        }
    }
}

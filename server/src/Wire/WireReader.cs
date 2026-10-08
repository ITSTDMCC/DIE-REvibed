using System;
using System.Text;

namespace EpidemicServer.Wire
{
    /// <summary>Reads values written in the game's wire format. Throws on truncated input.</summary>
    public sealed class WireReader
    {
        private readonly byte[] _buffer;
        private int _position;
        private readonly int _end;

        public WireReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

        public WireReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer;
            _position = offset;
            _end = offset + count;
        }

        public int Remaining { get { return _end - _position; } }

        private void Need(int count)
        {
            if (_end - _position < count) throw new FormatException("Message ended early");
        }

        public byte ReadByte() { Need(1); return _buffer[_position++]; }

        public bool ReadBool() { return ReadByte() != 0; }

        public ushort ReadUInt16()
        {
            Need(2);
            ushort value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
            _position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            Need(4);
            uint value = 0;
            for (int i = 0; i < 4; i++) value |= (uint)_buffer[_position + i] << (8 * i);
            _position += 4;
            return value;
        }

        public float ReadSingle()
        {
            Need(4);
            byte[] bytes = new byte[4];
            Array.Copy(_buffer, _position, bytes, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            _position += 4;
            return BitConverter.ToSingle(bytes, 0);
        }

        public byte[] ReadRaw(int count)
        {
            Need(count);
            byte[] bytes = new byte[count];
            Array.Copy(_buffer, _position, bytes, 0, count);
            _position += count;
            return bytes;
        }

        public ulong ReadVarUInt64()
        {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                byte b = ReadByte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            return value;
        }

        public uint ReadVarUInt32()
        {
            uint value = 0;
            for (int shift = 0; shift < 32; shift += 7)
            {
                byte b = ReadByte();
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            return value;
        }

        public int ReadVarInt32() { return (int)ReadVarUInt32(); }

        public long ReadVarInt64() { return (long)ReadVarUInt64(); }

        public string ReadString()
        {
            int length = (int)ReadVarUInt32();
            return Encoding.UTF8.GetString(ReadRaw(length));
        }

        public byte[] ReadBytes()
        {
            return ReadRaw((int)ReadVarUInt32());
        }
    }
}

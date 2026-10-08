using System;
using System.IO;
using System.Text;

namespace EpidemicServer.Wire
{
    /// <summary>
    /// Writes values in the game's wire format: little-endian fixed-size
    /// integers, LEB128 variable-length integers, and strings as a varint
    /// byte length followed by UTF-8 bytes. See docs/protocol.md.
    /// </summary>
    public sealed class WireWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();

        public long Length { get { return _stream.Length; } }

        public byte[] ToArray() { return _stream.ToArray(); }

        public void WriteByte(byte value) { _stream.WriteByte(value); }

        public void WriteBool(bool value) { _stream.WriteByte(value ? (byte)1 : (byte)0); }

        public void WriteUInt16(ushort value)
        {
            _stream.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
        }

        public void WriteUInt32(uint value)
        {
            for (int i = 0; i < 4; i++) _stream.WriteByte((byte)(value >> (8 * i)));
        }

        public void WriteSingle(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteRaw(byte[] bytes)
        {
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteVarUInt64(ulong value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                _stream.WriteByte(b);
            } while (value != 0);
        }

        public void WriteVarUInt32(uint value) { WriteVarUInt64(value); }

        /// <summary>Signed 32-bit values are sent as their unsigned bit pattern.</summary>
        public void WriteVarInt32(int value) { WriteVarUInt64((uint)value); }

        /// <summary>Signed 64-bit values are sent as their unsigned bit pattern.</summary>
        public void WriteVarInt64(long value) { WriteVarUInt64((ulong)value); }

        public void WriteString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            WriteVarUInt32((uint)bytes.Length);
            WriteRaw(bytes);
        }

        /// <summary>A byte array is a varint count followed by the bytes; null is sent as empty.</summary>
        public void WriteBytes(byte[] value)
        {
            if (value == null) { WriteVarUInt32(0); return; }
            WriteVarUInt32((uint)value.Length);
            WriteRaw(value);
        }
    }
}

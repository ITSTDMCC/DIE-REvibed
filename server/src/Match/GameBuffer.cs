using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// The game's own bit buffers, used through reflection. Output buffers come
    /// from the game's buffer factory (role NetBufferFactory, created with
    /// (false, 4), then Create(0)); input buffers from the network buffer's input
    /// creator, filled with WriteBytes and WriteInt (length). Bits are packed LSB
    /// first. The buffer methods are found by role (NetBuffer.*, see RoleTable).
    /// </summary>
    public sealed class GameBuffer
    {
        private static GameRuntime _game;
        private static object _factory;
        private static MethodInfo _create;
        private static Delegate _inputCreator;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        /// <summary>The game's buffer object, to pass to game methods.</summary>
        public readonly object Buffer;

        private GameBuffer(object buffer) { Buffer = buffer; }

        public static void Init(GameRuntime game)
        {
            if (_game != null) return;
            _game = game;
            Type factoryType = game.Game(R.Name("NetBufferFactory"));
            _factory = factoryType.GetConstructor(GameRuntime.All, null, new[] { typeof(bool), typeof(int) }, null)
                .Invoke(new object[] { false, 4 });
            _create = factoryType.GetMethod(R.Name("NetBufferFactory.Create"), GameRuntime.All);
            _inputCreator = (Delegate)game.Game("StunGameNetwork.LidgrenNetBuffer").GetField(R.Name("NetBuffer.InputCreator"), GameRuntime.All).GetValue(null);
        }

        /// <summary>An empty buffer for writing (the game's outgoing message type).</summary>
        public static GameBuffer Create() { return new GameBuffer(_create.Invoke(_factory, new object[] { 0 })); }

        /// <summary>Wraps a buffer object the game handed us.</summary>
        public static GameBuffer From(object buffer) { return new GameBuffer(buffer); }

        /// <summary>A buffer for reading the given bytes.</summary>
        public static GameBuffer Wrap(byte[] data)
        {
            GameBuffer b = new GameBuffer(_inputCreator.DynamicInvoke());
            b.Call(R.Name("NetBuffer.WriteBytes"), data);
            b.Call(R.Name("NetBuffer.WriteInt"), data.Length);
            return b;
        }

        public int LengthBits { get { return (int)Call(R.Name("NetBuffer.LengthBits")); } }

        public byte[] ToBytes()
        {
            int length = (int)Call(R.Name("NetBuffer.LengthBytes"));
            byte[] data = (byte[])Call(R.Name("NetBuffer.ReadBytes"));
            byte[] bytes = new byte[length];
            if (length > 0) Array.Copy(data, bytes, length);
            return bytes;
        }

        public void Write(bool value) { Call("Write", value); }
        public void Write(byte value) { Call("Write", value); }
        public void Write(ushort value) { Call("Write", value); }
        public void Write(uint value) { Call("Write", value); }
        public void Write(ulong value) { Call("Write", value); }
        public void WriteRanged(int min, int max, int value) { Call(R.Name("NetBuffer.WriteRanged"), min, max, value); }
        public void Write(int value) { Call("Write", value); }
        public void Write(float value) { Call("Write", value); }
        public void Write(byte[] value) { Call("Write", value); }
        /// <summary>The low <paramref name="bits"/> bits of a value (read back with ReadByte(bits) on the game's buffer).</summary>
        public void WriteBits(uint value, int bits) { Call("Write", value, bits); }
        public int ReadInt() { return (int)Call(R.Name("NetBuffer.ReadInt")); }

        public bool ReadBool() { return (bool)Call(R.Name("NetBuffer.ReadBool")); }
        public byte ReadByte() { return (byte)Call(R.Name("NetBuffer.ReadByte")); }
        public ushort ReadUShort() { return (ushort)Call(R.Name("NetBuffer.ReadUShort")); }
        public int ReadRanged(int min, int max) { return (int)Call(R.Name("NetBuffer.ReadRanged"), min, max); }

        /// <summary>Calls a method by name and argument types on the buffer's class or its interfaces.</summary>
        private object Call(string name, params object[] args)
        {
            Type[] types = args.Select(a => a.GetType()).ToArray();
            Type t = Buffer.GetType();
            string key = t.FullName + "::" + name + "(" + string.Join(",", types.Select(x => x.Name).ToArray()) + ")";
            MethodInfo m;
            if (!Methods.TryGetValue(key, out m))
            {
                m = new[] { t }.Concat(t.GetInterfaces())
                    .Select(x => x.GetMethod(name, GameRuntime.All, null, types, null))
                    .FirstOrDefault(x => x != null);
                if (m == null) throw new MissingMethodException(t.FullName, key);
                Methods[key] = m;
            }
            return m.Invoke(Buffer, args);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;

namespace EpidemicServer
{
    /// <summary>
    /// Cached Type.GetProperty / GetField / GetMethod. The match code looks the game's members up
    /// by name on every tick for every NPC; under Mono those lookups dominated a Scavenger tick
    /// (about 110 ms against a 33 ms budget). A member found once never changes, so each lookup is
    /// remembered by type, name, flags and parameter types. Lookups that throw (an ambiguous
    /// match) are not cached and throw again as before. The key holds the Type object itself:
    /// building a string from the type's name on each call costs more than the lookup.
    /// </summary>
    public static class ReflectionCache
    {
        private const BindingFlags Default = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        private sealed class Key : IEquatable<Key>
        {
            private readonly Type _type;
            private readonly string _name;
            private readonly BindingFlags _flags;
            private readonly Type[] _types;
            private readonly int _hash;

            public Key(Type type, string name, BindingFlags flags, Type[] types)
            {
                _type = type;
                _name = name;
                _flags = flags;
                _types = types;
                int h = type.GetHashCode() * 31 + name.GetHashCode();
                h = h * 31 + (int)flags;
                if (types != null)
                {
                    h = h * 31 + types.Length + 1;
                    foreach (Type p in types) h = h * 31 + (p == null ? 0 : p.GetHashCode());
                }
                _hash = h;
            }

            public bool Equals(Key o)
            {
                if (o == null || o._hash != _hash || o._type != _type || o._flags != _flags || o._name != _name) return false;
                if ((_types == null) != (o._types == null)) return false;
                if (_types == null) return true;
                if (_types.Length != o._types.Length) return false;
                for (int i = 0; i < _types.Length; i++) if (_types[i] != o._types[i]) return false;
                return true;
            }

            public override bool Equals(object o) { return Equals(o as Key); }

            public override int GetHashCode() { return _hash; }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<Key, PropertyInfo> Properties = new Dictionary<Key, PropertyInfo>();
        private static readonly Dictionary<Key, FieldInfo> Fields = new Dictionary<Key, FieldInfo>();
        private static readonly Dictionary<Key, MethodInfo> Methods = new Dictionary<Key, MethodInfo>();

        private static T Lookup<T>(Dictionary<Key, T> cache, Key k, Func<T> find) where T : class
        {
            T v;
            lock (Gate) { if (cache.TryGetValue(k, out v)) return v; }
            v = find();
            lock (Gate) { cache[k] = v; }
            return v;
        }

        public static PropertyInfo CProp(this Type t, string name) { return CProp(t, name, Default); }

        public static PropertyInfo CProp(this Type t, string name, BindingFlags flags)
        {
            return Lookup(Properties, new Key(t, name, flags, null), () => t.GetProperty(name, flags));
        }

        public static FieldInfo CField(this Type t, string name) { return CField(t, name, Default); }

        public static FieldInfo CField(this Type t, string name, BindingFlags flags)
        {
            return Lookup(Fields, new Key(t, name, flags, null), () => t.GetField(name, flags));
        }

        public static MethodInfo CMethod(this Type t, string name) { return CMethod(t, name, Default); }

        public static MethodInfo CMethod(this Type t, string name, BindingFlags flags)
        {
            return Lookup(Methods, new Key(t, name, flags, null), () => t.GetMethod(name, flags));
        }

        public static MethodInfo CMethod(this Type t, string name, Type[] types) { return CMethod(t, name, Default, null, types, null); }

        public static MethodInfo CMethod(this Type t, string name, BindingFlags flags, Binder binder, Type[] types, ParameterModifier[] modifiers)
        {
            // Only the default binder and no modifiers are cached; anything else is looked up as before.
            if (binder != null || modifiers != null) return t.GetMethod(name, flags, binder, types, modifiers);
            return Lookup(Methods, new Key(t, name, flags, types ?? Type.EmptyTypes), () => t.GetMethod(name, flags, null, types, null));
        }
    }
}

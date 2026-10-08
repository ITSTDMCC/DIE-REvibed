using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EpidemicServer.Resolve
{
    /// <summary>
    /// Run-time lookup of the game's obfuscated types and members by role (see Fingerprint and RoleTable).
    /// Init fingerprints the player's installed libraries once; after that a role resolves to the real type
    /// or member, or the server stops with a clear error naming the role.
    /// </summary>
    public static class R
    {
        private static FingerprintIndex _index;
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>();
        private static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>();
        private static readonly object Gate = new object();

        /// <summary>The number of roles in the table.</summary>
        public static int Count { get { return RoleTable.Prints.Count; } }

        public static bool Ready { get { return _index != null; } }

        /// <summary>Fingerprints the given libraries (the match client's, loaded from the player's install).</summary>
        public static void Init(IEnumerable<Assembly> assemblies)
        {
            lock (Gate)
            {
                _index = new FingerprintIndex(assemblies);
                Types.Clear();
                Members.Clear();
            }
        }

        /// <summary>Resolves every role in RoleTable now, so a mismatch shows at start-up rather than mid-match.</summary>
        public static List<string> Check()
        {
            var missing = new List<string>();
            foreach (KeyValuePair<string, string> kv in RoleTable.Prints)
            {
                try { if (kv.Value.StartsWith("T|")) Type(kv.Key); else Member(kv.Key); }
                catch (Exception) { missing.Add(kv.Key); }
            }
            return missing;
        }

        /// <summary>The game type that plays this role.</summary>
        public static Type Type(string role)
        {
            lock (Gate)
            {
                Type t;
                if (Types.TryGetValue(role, out t)) return t;
                t = Index().Type(Print(role));
                if (t == null) throw new Exception("game type for role '" + role + "' not found in the installed game");
                Types[role] = t;
                return t;
            }
        }

        /// <summary>The game field or method that plays this role.</summary>
        public static MemberInfo Member(string role)
        {
            lock (Gate)
            {
                MemberInfo m;
                if (Members.TryGetValue(role, out m)) return m;
                m = Index().Member(Print(role));
                if (m == null) throw new Exception("game member for role '" + role + "' not found in the installed game");
                Members[role] = m;
                return m;
            }
        }

        /// <summary>The name to use with reflection: a type's full name, or a member's name.</summary>
        public static string Name(string role)
        {
            return Print(role).StartsWith("T|") ? Type(role).FullName : Member(role).Name;
        }

        /// <summary>A type role's short name (Type.Name), for comparing against an object's runtime type.</summary>
        public static string Short(string role) { return Type(role).Name; }

        /// <summary>True when the object is exactly of the role's type.</summary>
        public static bool Is(object o, string role) { return o != null && o.GetType() == Type(role); }

        private static string Print(string role)
        {
            string fp;
            if (!RoleTable.Prints.TryGetValue(role, out fp)) throw new Exception("unknown role '" + role + "'");
            return fp;
        }

        private static FingerprintIndex Index()
        {
            if (_index == null) throw new InvalidOperationException("R.Init has not run: the game libraries aren't loaded");
            return _index;
        }
    }
}

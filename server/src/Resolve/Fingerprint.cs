using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EpidemicServer.Resolve
{
    /// <summary>
    /// Structural fingerprints for the types and members of the player's installed game libraries.
    ///
    /// The game's libraries are obfuscated: most classes and members have meaningless generated names. Instead
    /// of naming them in our code, DIE: Revibed refers to each one by a role (see RoleTable) whose fingerprint
    /// describes its shape: what it derives from, what kinds of fields and methods it has. At start-up the
    /// server fingerprints the installed libraries and looks each role up. A fingerprint is a one-way digest of
    /// that shape plus an ordinal to break ties, so it contains no names from the game.
    ///
    /// Format:
    ///   type:   T|&lt;assembly&gt;|&lt;shape digest&gt;|&lt;ordinal among types with that digest&gt;
    ///   field:  F|&lt;declaring type fingerprint&gt;|&lt;signature digest&gt;|&lt;ordinal&gt;
    ///   method: M|&lt;declaring type fingerprint&gt;|&lt;signature digest&gt;|&lt;ordinal&gt;
    /// Ordinals count in the library's own declaration order, which is fixed for a given build of the game.
    /// </summary>
    public static class Fingerprint
    {
        public const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                             BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>True for the obfuscator's generated names (they contain '#').</summary>
        public static bool Generated(string name) { return name != null && name.IndexOf('#') >= 0; }

        /// <summary>A type as it appears in a shape: generated names become "?", everything else stays as declared.</summary>
        public static string Norm(Type t)
        {
            if (t == null) return "-";
            if (t.IsByRef) return Norm(t.GetElementType()) + "&";
            if (t.IsPointer) return Norm(t.GetElementType()) + "*";
            if (t.IsArray) return Norm(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return "!" + t.GenericParameterPosition;
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
                return Norm(t.GetGenericTypeDefinition()) + "<" + string.Join(",", t.GetGenericArguments().Select(Norm).ToArray()) + ">";
            string name = t.FullName ?? t.Name;
            if (Generated(name)) return "?" + (t.IsGenericTypeDefinition ? "`" + t.GetGenericArguments().Length : "");
            return name;
        }

        /// <summary>The shape of a type: kind, base, interfaces, its own fields and methods in order, nested types.</summary>
        public static string Shape(Type t)
        {
            var s = new StringBuilder();
            s.Append(t.IsInterface ? "I" : t.IsEnum ? "E" : t.IsValueType ? "S" : "C");
            s.Append(t.IsAbstract ? "a" : "").Append(t.IsSealed ? "s" : "").Append(t.IsNested ? "n" : "");
            s.Append("|b:").Append(Norm(t.BaseType));
            s.Append("|i:").Append(string.Join(",", t.GetInterfaces().Select(Norm).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            s.Append("|f:");
            foreach (FieldInfo f in Fields(t)) s.Append(FieldSig(f)).Append(';');
            s.Append("|m:");
            foreach (MethodBase m in Methods(t)) s.Append(MethodSig(m)).Append(';');
            s.Append("|p:").Append(Safe(() => t.GetProperties(Declared).Length));
            s.Append("|n:").Append(Safe(() => t.GetNestedTypes(Declared).Length));
            if (t.IsEnum) s.Append("|v:").Append(string.Join(",", Enum.GetNames(t).Select(n => Generated(n) ? "?" : n).ToArray()));
            return s.ToString();
        }

        public static string FieldSig(FieldInfo f)
        {
            return (f.IsStatic ? "s" : "i") + (f.IsLiteral ? "c" : "") + Norm(f.FieldType);
        }

        public static string MethodSig(MethodBase m)
        {
            var mi = m as MethodInfo;
            string ret = mi == null ? "ctor" : Norm(mi.ReturnType);
            string name = m.IsSpecialName || !Generated(m.Name) ? m.Name : "?";
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); } catch { ps = new ParameterInfo[0]; }
            return (m.IsStatic ? "s" : "i") + (m.IsVirtual ? "v" : "") + name + ":" + ret + "(" +
                   string.Join(",", ps.Select(p => Norm(p.ParameterType)).ToArray()) + ")" +
                   (m.IsGenericMethodDefinition ? "`" + m.GetGenericArguments().Length : "");
        }

        public static FieldInfo[] Fields(Type t) { return Safe(() => t.GetFields(Declared)) ?? new FieldInfo[0]; }

        public static MethodBase[] Methods(Type t)
        {
            MethodBase[] ms = Safe(() => t.GetMethods(Declared)) ?? new MethodInfo[0];
            MethodBase[] cs = Safe(() => t.GetConstructors(Declared)) ?? new ConstructorInfo[0];
            return cs.Concat(ms).ToArray();
        }

        public static string Digest(string s)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default(T); } }

        public static Type[] Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null).ToArray(); }
        }
    }

    /// <summary>
    /// Fingerprints of every type in a set of assemblies, built once, so roles can be looked up both ways
    /// (fingerprint to type at run time, type to fingerprint in the generator).
    /// </summary>
    public sealed class FingerprintIndex
    {
        private readonly Dictionary<string, Type> _byPrint = new Dictionary<string, Type>();
        private readonly Dictionary<Type, string> _print = new Dictionary<Type, string>();

        public FingerprintIndex(IEnumerable<Assembly> assemblies)
        {
            foreach (Assembly a in assemblies)
            {
                string asm = a.GetName().Name;
                var seen = new Dictionary<string, int>();
                foreach (Type t in Fingerprint.Types(a))
                {
                    string d = Fingerprint.Digest(Fingerprint.Shape(t));
                    int n;
                    seen.TryGetValue(d, out n);
                    seen[d] = n + 1;
                    string fp = "T|" + asm + "|" + d + "|" + n;
                    _byPrint[fp] = t;
                    _print[t] = fp;
                }
            }
        }

        public Type Type(string fp)
        {
            Type t;
            return _byPrint.TryGetValue(fp, out t) ? t : null;
        }

        public string Of(Type t)
        {
            string fp;
            return _print.TryGetValue(t, out fp) ? fp : null;
        }

        /// <summary>The fingerprint of a field or method (declared on its type), or null.</summary>
        public string Of(MemberInfo m)
        {
            string owner = Of(m.DeclaringType);
            if (owner == null) return null;
            if (m is FieldInfo)
            {
                string sig = Fingerprint.FieldSig((FieldInfo)m);
                int ord = Fingerprint.Fields(m.DeclaringType).TakeWhile(f => f.MetadataToken != m.MetadataToken).Count(f => Fingerprint.FieldSig(f) == sig);
                return "F|" + owner + "|" + Fingerprint.Digest(sig) + "|" + ord;
            }
            var mb = m as MethodBase;
            if (mb == null) return null;
            string msig = Fingerprint.MethodSig(mb);
            int mord = Fingerprint.Methods(m.DeclaringType).TakeWhile(x => x.MetadataToken != mb.MetadataToken).Count(x => Fingerprint.MethodSig(x) == msig);
            return "M|" + owner + "|" + Fingerprint.Digest(msig) + "|" + mord;
        }

        /// <summary>The field or method a member fingerprint names, or null.</summary>
        public MemberInfo Member(string fp)
        {
            // F|T|asm|digest|n|sigdigest|ord  (the type fingerprint is embedded)
            string[] p = fp.Split('|');
            if (p.Length != 7) return null;
            Type owner = Type(string.Join("|", p, 1, 4));
            if (owner == null) return null;
            int ord = int.Parse(p[6]);
            if (p[0] == "F")
                return Fingerprint.Fields(owner).Where(f => Fingerprint.Digest(Fingerprint.FieldSig(f)) == p[5]).Skip(ord).FirstOrDefault();
            if (p[0] == "M")
                return Fingerprint.Methods(owner).Where(m => Fingerprint.Digest(Fingerprint.MethodSig(m)) == p[5]).Skip(ord).FirstOrDefault();
            return null;
        }
    }
}

// RoleGen: builds server/src/Resolve/RoleTable.cs from a LOCAL role map and the player's installed game.
//
// The role map (one line per role: "Role | T|F|M | type | member | filter", or "Role | N | readable name")
// names the game's current
// identifiers, so it stays on the developer's machine (local/, git-ignored) and is never committed. RoleGen
// fingerprints each role's type or member (see Fingerprint.cs), checks that every fingerprint points back to
// exactly that type or member, and writes RoleTable.cs, which contains only role names and fingerprints.
//
// Usage: tools\run_rolegen.cmd <role map> ["<game install dir>"]           write RoleTable.cs
//        tools\run_rolegen.cmd <role map> ["<game install dir>"] check     compare RoleTable.cs with the map
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using EpidemicServer.Resolve;

public static class RoleGen
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    public static int Main(string[] args)
    {
        string map = args[0];
        bool check = args.Skip(1).Contains("check");
        string install = args.Skip(1).FirstOrDefault(a => a != "" && a != "check") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Dead Island Epidemic";
        string managed = Path.Combine(install, @"Dead Island Epidemic_Data\Managed");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string p = Path.Combine(managed, e.Name.Split(',')[0] + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly[] asms = { Assembly.LoadFrom(Path.Combine(managed, "StunCore.dll")),
                            Assembly.LoadFrom(Path.Combine(managed, "ConductorCrafting.dll")),
                            Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll")) };
        var index = new FingerprintIndex(asms);
        var names = new NameIndex(asms);
        // The hub ("Crib") has its own copy of Assembly-CSharp; LoadFile keeps it apart from the match client's.
        string crib = Path.Combine(install, @"Dead Island Epidemic - Crib_Data\Managed");
        var cribNames = new NameIndex(new[] { Assembly.LoadFile(Path.Combine(crib, "Assembly-CSharp.dll")) });
        var cribOnly = new List<string>();

        var rows = new List<KeyValuePair<string, string>>();
        int errors = 0;
        foreach (string raw in File.ReadAllLines(map))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] p = line.Split('|').Select(x => x.Trim()).ToArray();
            string role = p[0], kind = p[1], typeName = p[2];
            if (kind == "N")
            {
                string fpn = NameIndex.Print(typeName);
                if (names.Name(fpn) == typeName) { rows.Add(new KeyValuePair<string, string>(role, fpn)); continue; }
                if (cribNames.Name(fpn) == typeName) { rows.Add(new KeyValuePair<string, string>(role, fpn)); cribOnly.Add(role); continue; }
                Console.WriteLine("ERROR " + role + ": name not found in the game's libraries"); errors++; continue;
            }
            Type t = asms.Select(a => a.GetType(typeName)).FirstOrDefault(x => x != null);
            if (t == null) { Console.WriteLine("ERROR " + role + ": type not found"); errors++; continue; }
            string fp;
            if (kind == "T")
            {
                fp = index.Of(t);
                if (index.Type(fp) != t) { Console.WriteLine("ERROR " + role + ": type fingerprint does not round-trip"); errors++; continue; }
            }
            else
            {
                string member = p[3];
                string filter = p.Length > 4 ? p[4] : "";
                MemberInfo m = Find(t, kind, member, filter);
                if (m == null) { Console.WriteLine("ERROR " + role + ": member not found on " + typeName); errors++; continue; }
                fp = index.Of(m);
                MemberInfo back = fp == null ? null : index.Member(fp);
                if (back == null || back.MetadataToken != m.MetadataToken || back.Module != m.Module)
                { Console.WriteLine("ERROR " + role + ": member fingerprint does not round-trip"); errors++; continue; }
                if (back.Name != member) { Console.WriteLine("ERROR " + role + ": resolves to a different name"); errors++; continue; }
            }
            rows.Add(new KeyValuePair<string, string>(role, fp));
        }
        Console.WriteLine(rows.Count + " roles fingerprinted, " + errors + " errors");
        if (cribOnly.Count > 0) Console.WriteLine("hub-only names (resolve only where R.Init gets the hub's libraries): " + string.Join(", ", cribOnly.ToArray()));
        var dup = rows.GroupBy(r => r.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) { Console.WriteLine("ERROR duplicate roles: " + string.Join(", ", dup.ToArray())); return 1; }
        if (errors > 0) return 1;

        string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(typeof(RoleGen).Assembly.Location)), @"..\..\server\src\Resolve\RoleTable.cs");
        outPath = Path.GetFullPath(outPath);
        if (check)
        {
            // Compare with the committed table: every role must be present with the same fingerprint.
            string text = File.ReadAllText(outPath);
            int diff = rows.Count(r => !text.Contains("{ \"" + r.Key + "\", \"" + r.Value + "\" }"));
            Console.WriteLine(diff == 0 ? "RoleTable.cs matches the map and the installed game" : diff + " roles differ from RoleTable.cs");
            return diff == 0 ? 0 : 1;
        }
        var s = new StringBuilder();
        s.AppendLine("// Generated by tools/RoleGen from the installed game. Do not edit by hand.");
        s.AppendLine("// Each value is a structural fingerprint (see Fingerprint.cs); no names from the game appear here.");
        s.AppendLine("using System.Collections.Generic;");
        s.AppendLine();
        s.AppendLine("namespace EpidemicServer.Resolve");
        s.AppendLine("{");
        s.AppendLine("    public static class RoleTable");
        s.AppendLine("    {");
        s.AppendLine("        public static readonly Dictionary<string, string> Prints = new Dictionary<string, string>");
        s.AppendLine("        {");
        foreach (var r in rows) s.AppendLine("            { \"" + r.Key + "\", \"" + r.Value + "\" },");
        s.AppendLine("        };");
        s.AppendLine();
        s.AppendLine("        /// <summary>Roles found only in the hub's library; the match side doesn't check them.</summary>");
        s.AppendLine("        public static readonly HashSet<string> HubOnly = new HashSet<string> { " + string.Join(", ", cribOnly.Select(r => "\"" + r + "\"").ToArray()) + " };");
        s.AppendLine("    }");
        s.AppendLine("}");
        File.WriteAllText(outPath, s.ToString());
        Console.WriteLine("wrote " + outPath);
        return 0;
    }

    private static MemberInfo Find(Type t, string kind, string name, string filter)
    {
        for (Type x = t; x != null; x = x.BaseType)
        {
            const BindingFlags Declared = All | BindingFlags.DeclaredOnly;
            if (kind == "F")
            {
                FieldInfo f = x.GetField(name, Declared);
                if (f != null) return f;
            }
            else
            {
                IEnumerable<MethodInfo> ms = x.GetMethods(Declared).Where(m => m.Name == name);
                if (filter.StartsWith("params=")) { int n = int.Parse(filter.Substring(7)); ms = ms.Where(m => m.GetParameters().Length == n); }
                if (filter == "enumparam") ms = ms.Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
                MethodInfo m0 = ms.FirstOrDefault();
                if (m0 != null) return m0;
            }
        }
        return null;
    }
}

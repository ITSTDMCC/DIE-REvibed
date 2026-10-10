using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EpidemicServer.Protocol
{
    /// <summary>
    /// Saves the local account as a small text file (one "key=value" per line)
    /// so unlocks and characters survive a server restart.
    /// </summary>
    public sealed class AccountStore
    {
        private readonly string _path;

        public AccountStore(string path) { _path = path; }

        public string Path { get { return _path; } }

        public Account Load()
        {
            Account a = new Account();
            if (!File.Exists(_path)) return a;
            foreach (string raw in File.ReadAllLines(_path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq < 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "name": a.Name = value; break;
                    case "steamId": a.SteamId = ulong.Parse(value, CultureInfo.InvariantCulture); break;
                    case "gold": a.Gold = Int(value); break;
                    case "silver": a.Silver = Int(value); break;
                    case "characterPoints": a.HeroPoints = Int(value); break;
                    case "researchPoints": a.LabPoints = Int(value); break;
                    case "storyMapXp": a.StoryMapXp = (uint)Int(value); break;
                    case "createTime": a.CreateTime = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case "firstLogin": a.FirstSignIn = value == "true"; break;
                    case "tutorialCompleted": a.TutorialCompleted = value == "true"; break;
                    case "unlockAll": case "maxLevel": case "unlimitedCurrency": SetSwitch(a, key, value); break;
                    case "node": a.Nodes.Add(ParseNode(value)); break;
                    case "character": a.Characters.Add(ParseCharacter(value)); break;
                    case "unique": a.Uniques.Add(ParseUnique(value)); break;
                    case "stackable":
                        {
                            string[] p = value.Split(':');
                            a.Stackables.Add(new OwnedStackable { Id = ushort.Parse(p[0], CultureInfo.InvariantCulture), Type = Int(p[1]), Amount = Int(p[2]) });
                            break;
                        }
                    default: Log.Warn("account: ignoring unknown key '" + key + "' in " + _path); break;
                }
            }
            return a;
        }

        /// <summary>
        /// Re-reads only the preservation switches from the file, so a switch flipped by hand (or with
        /// toolsccount_switch.cmd) while the server runs is kept and takes effect at the next hub login.
        /// </summary>
        public void ReloadSwitches(Account a)
        {
            if (!File.Exists(_path)) return;
            foreach (string raw in File.ReadAllLines(_path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq);
                if (key == "unlockAll" || key == "maxLevel" || key == "unlimitedCurrency") SetSwitch(a, key, line.Substring(eq + 1));
            }
        }

        private static void SetSwitch(Account a, string key, string value)
        {
            bool on = value.Trim().ToLowerInvariant() == "true";
            if (key == "unlockAll") a.UnlockAll = on;
            else if (key == "maxLevel") a.MaxLevel = on;
            else a.UnlimitedCurrency = on;
        }

        public void Save(Account a)
        {
            ReloadSwitches(a);   // the file's switches win over the copy in memory
            StringBuilder s = new StringBuilder();
            s.AppendLine("# Local Dead Island: Epidemic account, written by EpidemicServer.");
            s.AppendLine("name=" + a.Name);
            if (a.SteamId != 0) s.AppendLine("steamId=" + a.SteamId.ToString(CultureInfo.InvariantCulture));
            s.AppendLine("gold=" + Str(a.Gold));
            s.AppendLine("silver=" + Str(a.Silver));
            s.AppendLine("characterPoints=" + Str(a.HeroPoints));
            s.AppendLine("researchPoints=" + Str(a.LabPoints));
            s.AppendLine("storyMapXp=" + a.StoryMapXp.ToString(CultureInfo.InvariantCulture));
            s.AppendLine("createTime=" + a.CreateTime.ToString(CultureInfo.InvariantCulture));
            s.AppendLine("firstLogin=" + (a.FirstSignIn ? "true" : "false"));
            s.AppendLine("tutorialCompleted=" + (a.TutorialCompleted ? "true" : "false"));
            s.AppendLine("# Preservation switches (true/false); they take effect at the next hub login.");
            s.AppendLine("unlockAll=" + (a.UnlockAll ? "true" : "false"));
            s.AppendLine("maxLevel=" + (a.MaxLevel ? "true" : "false"));
            s.AppendLine("unlimitedCurrency=" + (a.UnlimitedCurrency ? "true" : "false"));
            // node=<id>:<times unlocked>:<choice>,<choice>...
            foreach (StoryMapNode n in a.Nodes)
            {
                List<string> choices = new List<string>();
                foreach (byte c in n.Choices) choices.Add(c.ToString(CultureInfo.InvariantCulture));
                s.AppendLine("node=" + Str(n.Id) + ":" + n.TimesUnlocked.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", choices.ToArray()));
            }
            // character=<id>:<owned>:<xp>
            foreach (OwnedCharacter c in a.Characters)
                s.AppendLine("character=" + c.Id.ToString(CultureInfo.InvariantCulture) + ":" + (c.Owned ? "true" : "false") + ":" + Str(c.Xp));
            // unique=<guid hex>:<schematic id>:<in inventory>:<durability>:<ep>
            foreach (OwnedUnique u in a.Uniques)
                s.AppendLine("unique=" + BitConverter.ToString(u.Guid).Replace("-", "") + ":" + u.SchematicId.ToString(CultureInfo.InvariantCulture) + ":" +
                             (u.InBag ? "true" : "false") + ":" + Str(u.Durability) + ":" + Str(u.Ep));

            // stackable=<id>:<stackable type>:<amount>
            foreach (OwnedStackable st in a.Stackables)
                s.AppendLine("stackable=" + st.Id.ToString(CultureInfo.InvariantCulture) + ":" + Str(st.Type) + ":" + Str(st.Amount));

            string temp = _path + ".tmp";
            File.WriteAllText(temp, s.ToString());
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }

        private static StoryMapNode ParseNode(string value)
        {
            string[] parts = value.Split(':');
            StoryMapNode n = new StoryMapNode();
            n.Id = Int(parts[0]);
            n.TimesUnlocked = byte.Parse(parts[1], CultureInfo.InvariantCulture);
            if (parts.Length > 2 && parts[2].Length > 0)
                foreach (string c in parts[2].Split(','))
                    n.Choices.Add(byte.Parse(c, CultureInfo.InvariantCulture));
            return n;
        }

        private static OwnedCharacter ParseCharacter(string value)
        {
            string[] parts = value.Split(':');
            OwnedCharacter c = new OwnedCharacter();
            c.Id = byte.Parse(parts[0], CultureInfo.InvariantCulture);
            c.Owned = parts[1] == "true";
            c.Xp = Int(parts[2]);
            return c;
        }

        private static OwnedUnique ParseUnique(string value)
        {
            string[] parts = value.Split(':');
            OwnedUnique u = new OwnedUnique();
            u.Guid = new byte[parts[0].Length / 2];
            for (int i = 0; i < u.Guid.Length; i++)
                u.Guid[i] = byte.Parse(parts[0].Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            u.SchematicId = ushort.Parse(parts[1], CultureInfo.InvariantCulture);
            u.InBag = parts[2] == "true";
            u.Durability = Int(parts[3]);
            u.Ep = Int(parts[4]);
            return u;
        }

        private static int Int(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }
        private static string Str(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}

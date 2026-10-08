using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace EpidemicServer.Match
{
    /// <summary>What goes into one player's end-of-match rewards (PlayerRewardData fields we fill).</summary>
    [Serializable]
    public sealed class RewardsInput
    {
        public int ZombieKills, Deaths, Supplies, TeamSupplies, Damage, MapIndex, LevelReached, AccountExperience;
        public long StartTimeTicks;
        public uint MatchTimeSeconds;
        public byte Team, Placement, Character, QueueType;
        public ulong SteamId;
        public int[] BoxIds = new int[0], BoxScores = new int[0];
        public byte[] Weapon1, Weapon2;
    }

    /// <summary>
    /// Builds the end-of-match rewards blob (GameMessage 5, written by the match client to
    /// lastRewards.data and read by the hub) with the hub's own ConductorGameLogic.PlayerRewards
    /// .Serialize (version 9). The hub's Assembly-CSharp has the same name as the match client's,
    /// so it is loaded in a separate AppDomain.
    /// </summary>
    public static class HubRewards
    {
        private static AppDomain _domain;
        private static HubRewardsWriter _writer;

        public static byte[] Build(string install, RewardsInput input)
        {
            return Writer(install).Build(input);
        }

        /// <summary>The characters the hub lists (CharacterData.GetAllCharacters: not None, not [Hide]).</summary>
        public static byte[] ListedCharacters(string install)
        {
            return Writer(install).ListedCharacters();
        }

        private static HubRewardsWriter Writer(string install)
        {
            if (_writer == null)
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                AppDomainSetup setup = new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(exe) };
                _domain = AppDomain.CreateDomain("hub-rewards", null, setup);
                _writer = (HubRewardsWriter)_domain.CreateInstanceFromAndUnwrap(exe, typeof(HubRewardsWriter).FullName);
                _writer.Load(Path.Combine(install, @"Dead Island Epidemic - Crib_Data\Managed"));
            }
            return _writer;
        }
    }

    /// <summary>Runs inside the hub-rewards AppDomain.</summary>
    public sealed class HubRewardsWriter : MarshalByRefObject
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private Assembly _hub;

        public void Load(string managed)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            _hub = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        }

        /// <summary>
        /// Characters the hub lists (not None, not [Hide]) that also have a hub model: the hub loads one prefab per
        /// character from CharacterLoader.prefabMapping (filled by its Initialize). Logan is listed but has no
        /// prefab, so he is left out (owner, 2026-10-07: only characters with a hub model).
        /// </summary>
        public byte[] ListedCharacters()
        {
            Type e = _hub.GetType("CharacterEnum", true);
            Type loader = _hub.GetType("CharacterLoader", true);
            loader.GetMethod("Initialize", All).Invoke(null, null);
            IDictionary prefabs = (IDictionary)loader.GetField("prefabMapping", All).GetValue(null);
            return e.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => Convert.ToInt32(f.GetValue(null)) > 0 && !f.GetCustomAttributes(false).Any(a => a.GetType().Name == "HideAttribute") &&
                            prefabs != null && prefabs.Contains(f.GetValue(null)))
                .Select(f => (byte)Convert.ToInt32(f.GetValue(null))).ToArray();
        }

        public byte[] Build(RewardsInput input)
        {
            Type rewardsType = _hub.GetType("ConductorGameLogic.PlayerRewards", true);
            FieldInfo dataField = rewardsType.GetField("Data");
            Type dataType = dataField.FieldType;
            object data = Activator.CreateInstance(dataType);
            Set(data, "ZombieKills", input.ZombieKills);
            Set(data, "Deaths", input.Deaths);
            Set(data, "Supplies", input.Supplies);
            Set(data, "TeamSupplies", input.TeamSupplies);
            Set(data, "Damage", input.Damage);
            Set(data, "StartTimeTicks", input.StartTimeTicks);
            Set(data, "MatchTimeSeconds", input.MatchTimeSeconds);
            Set(data, "Team", input.Team);
            Set(data, "MapIndex", input.MapIndex);
            Set(data, "LevelReached", input.LevelReached);
            SetEnum(data, "Placement", input.Placement);
            SetEnum(data, "Character", input.Character);
            SetEnum(data, "GameMode", input.QueueType);
            Set(data, "Weapon1", input.Weapon1 ?? new byte[0]);
            Set(data, "Weapon2", input.Weapon2 ?? new byte[0]);
            Set(data, "NewUnlockedNodes", new int[0]);
            Set(data, "CrossroadCompleteScores", new byte[0]);
            Set(data, "CrossroadMissionTypes", new byte[0]);

            FieldInfo boxesField = dataType.GetField("BoxScores");
            Type boxType = boxesField.FieldType.GetElementType();
            Array boxes = Array.CreateInstance(boxType, input.BoxIds.Length);
            for (int i = 0; i < boxes.Length; i++)
            {
                object box = Activator.CreateInstance(boxType);
                FieldInfo id = boxType.GetField("ID");
                id.SetValue(box, Enum.ToObject(id.FieldType, input.BoxIds[i]));
                boxType.GetField("Score").SetValue(box, input.BoxScores[i]);
                boxes.SetValue(box, i);
            }
            boxesField.SetValue(data, boxes);

            foreach (string name in new[] { "Rewards", "FirstWinRewards" })
            {
                FieldInfo f = dataType.GetField(name);
                object collection = Activator.CreateInstance(f.FieldType);
                FillEmpty(collection);
                if (name == "Rewards") Set(collection, "AccountExperience", input.AccountExperience);
                f.SetValue(data, collection);
            }

            object rewards = Activator.CreateInstance(rewardsType);
            dataField.SetValue(rewards, data);
            FieldInfo players = rewardsType.GetField("PlayersInMatch");
            Type playerType = players.FieldType.GetElementType();
            object player = Activator.CreateInstance(playerType);
            playerType.GetField("SteamID").SetValue(player, input.SteamId);
            FieldInfo team = playerType.GetField("Team");
            team.SetValue(player, Enum.ToObject(team.FieldType, (int)input.Team));
            Array list = Array.CreateInstance(playerType, 1);
            list.SetValue(player, 0);
            players.SetValue(rewards, list);

            // As RewardSerialization.ReadRewardFile, but writing: a LidgrenNetBuffer over a new inner buffer.
            Type bufferType = _hub.GetType("StunGameNetwork.LidgrenNetBuffer", true);
            ConstructorInfo ctor = bufferType.GetConstructors(All).First(c => c.GetParameters().Length == 1);
            object buffer = ctor.Invoke(new[] { Activator.CreateInstance(ctor.GetParameters()[0].ParameterType, true) });
            MethodInfo serialize = rewardsType.GetMethods(All).First(m => m.Name == "Serialize" && m.GetParameters().Length == 1);
            object[] args = { buffer };
            serialize.Invoke(rewards, args);
            MethodInfo lengthBytes = LengthInBytes(bufferType, ctor);
            MethodInfo getData = bufferType.GetMethods(All).FirstOrDefault(m => m.Name == "get_Data");
            if (lengthBytes == null || getData == null) throw new MissingMethodException("LidgrenNetBuffer length/get_Data");
            int length = (int)lengthBytes.Invoke(buffer, null);
            byte[] all = (byte[])getData.Invoke(buffer, null);
            byte[] bytes = new byte[length];
            Array.Copy(all, bytes, length);
            return bytes;
        }

        /// <summary>Gives every null list or array field of a struct (and of its struct fields) an empty one.</summary>
        private static void FillEmpty(object o)
        {
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = f.GetValue(o);
                if (f.FieldType.IsArray && v == null) f.SetValue(o, Array.CreateInstance(f.FieldType.GetElementType(), 0));
                else if (typeof(IList).IsAssignableFrom(f.FieldType) && v == null && !f.FieldType.IsAbstract) f.SetValue(o, Activator.CreateInstance(f.FieldType));
                else if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && f.FieldType.Assembly == o.GetType().Assembly)
                {
                    object inner = v ?? Activator.CreateInstance(f.FieldType);
                    FillEmpty(inner);
                    f.SetValue(o, inner);
                }
            }
        }

        private static void Set(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name);
            f.SetValue(o, Convert.ChangeType(value, f.FieldType));
        }

        private static void SetEnum(object o, string name, int value)
        {
            FieldInfo f = o.GetType().GetField(name);
            f.SetValue(o, Enum.ToObject(f.FieldType, value));
        }

        /// <summary>
        /// The hub buffer's "written length in bytes" method, found by behaviour rather than by its obfuscated
        /// name: the parameterless int method (a property getter) that returns 3 after three bytes are written to a fresh buffer and
        /// 11 after eleven (each candidate is tried on its own fresh buffers).
        /// </summary>
        private static MethodInfo LengthInBytes(Type bufferType, ConstructorInfo ctor)
        {
            MethodInfo writeByte = bufferType.GetMethods().First(m => m.Name == "Write" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(byte));
            Func<int, object> filled = n =>
            {
                object b = ctor.Invoke(new[] { Activator.CreateInstance(ctor.GetParameters()[0].ParameterType, true) });
                for (int i = 0; i < n; i++) writeByte.Invoke(b, new object[] { (byte)0xFF });
                return b;
            };
            var found = new List<MethodInfo>();
            foreach (MethodInfo m in bufferType.GetMethods(All))
            {
                if (m.IsStatic || m.ReturnType != typeof(int) || m.GetParameters().Length != 0) continue;
                try
                {
                    if ((int)m.Invoke(filled(3), null) == 3 && (int)m.Invoke(filled(11), null) == 11) found.Add(m);
                }
                catch (Exception) { }
            }
            return found.Count == 1 ? found[0] : null;
        }
    }
}

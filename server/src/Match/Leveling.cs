using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Protocol;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Leveling for one match (owner's design, 2026-10-06): the account level at match start,
    /// in-match XP on kills, ability upgrades, and the end-of-match rewards (MatchMessage 5)
    /// with the account update that goes with them.
    /// </summary>
    public sealed class Leveling
    {
        private const BindingFlags All = GameRuntime.All;
        public const int UpgradeAbilityMessage = 3, RewardsMessage = 5, UpgradeAbilityDoneMessage = 36, ScoreStatsMessage = 8;
        /// <summary>Stand-in rate (the original reward tables are lost): account XP per match minute.</summary>
        public const int StandInXpPerMinute = 10;

        private readonly GameRuntime _g;
        private readonly object _player;
        private readonly Action<string> _log;
        private readonly DateTime _start = DateTime.UtcNow;
        public int UndeadKills, Deaths, SuppliesHandedIn;
        private bool _ended;

        public Leveling(GameRuntime g, object player, Action<string> log)
        {
            _g = g;
            _player = player;
            _log = log;
        }

        /// <summary>Levels.ForLevel: the account level for a story map XP total.</summary>
        public static int AccountLevel(GameRuntime g, uint storyMapXp)
        {
            return (int)R.Type("Type.Levels").GetMethod(R.Name("Levels.ForLevel"), All).Invoke(null, new object[] { storyMapXp });
        }

        /// <summary>Installs the kill and ability-upgrade hooks.</summary>
        public void Install()
        {
            Action<float> previousUpdate = ServerHooks.AfterUpdate;
            ServerHooks.AfterUpdate = time => { if (previousUpdate != null) previousUpdate(time); SendScoreStats(time); };
            Action<object, object> previousKill = ServerHooks.OnKill;
            ServerHooks.OnKill = (victim, killer) => { OnKill(victim, killer); if (previousKill != null) previousKill(victim, killer); };
            Func<int, GameBuffer, bool> previous = ServerHooks.OnGameMessage;
            ServerHooks.OnGameMessage = (type, b) => (type == UpgradeAbilityMessage && OnUpgradeAbility(b)) || (previous != null && previous(type, b));
        }

        private int _xpLogged;

        /// <summary>
        /// A kill: the game's own XP award (role KillXp.Award: victim, killer, gm, multiplier 1), which the
        /// client build no longer calls (the server did).
        /// </summary>
        private void OnKill(object victim, object changer)
        {
            object killer = changer == null ? null : ServerHooks.RootOwner(changer);
            _scoreDirty = true;
            if (victim == _player) { Deaths++; return; }
            // A teammate's pet expiring (for example a hero's minion) is no kill (it counted as one, 2026-10-07).
            PropertyInfo team = victim == null ? null : victim.GetType().GetProperty(R.Name("Entity.Team"), All);
            if (team != null && team.GetValue(victim, null).Equals(_player.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(_player, null))) return;
            // Bots earn kill XP the same way (the award shares it with teammates in range). Zombie LV is
            // World.AverageLevel, the mean in-match level of every hero, so bots stuck at
            // level 1 held Scavenger zombies at LV 1 (owner, 2026-10-07).
            if (killer != _player)
            {
                if (killer != null && IsBot(killer))
                    ((MethodInfo)R.Member("KillXp.Award")).Invoke(null, new object[] { victim, killer, _g.World, 1f });
                return;
            }
            UndeadKills++;
            float before = Experience(_player);
            ((MethodInfo)R.Member("KillXp.Award")).Invoke(null, new object[] { victim, killer, _g.World, 1f });
            if (_xpLogged++ < 15 || UndeadKills % 25 == 0)
                _log("leveling: " + ServerHooks.Describe(victim) + " killed; the player's XP " + before + " -> " + Experience(_player) + " (level " + Level(_player) + ", " + UndeadKills + " kills)");
        }

        private bool _scoreDirty = true;
        private float _nextScore;

        /// <summary>
        /// MatchMessage 8: the player's Hero.Score as the client reads them (message 8:
        /// bool, ushort player index, then the stats in the game's own format). The scoreboards read zombies
        /// killed, kills and deaths from there; Character.Die counts them on our server, nothing sent them (Horde
        /// scoreboard showed 0 kills, 2026-10-07). Sent at most every 2 s while they change, and at the end.
        /// </summary>
        public void SendScoreStats(float time, bool force = false)
        {
            if (!force && (!_scoreDirty || time < _nextScore)) return;
            _scoreDirty = false;
            _nextScore = time + 2f;
            ushort index = (ushort)_player.GetType().GetProperty(R.Name("Entity.HeroIndex"), All).GetValue(_player, null);
            object stats = _player.GetType().GetField(R.Name("Hero.Score"), All).GetValue(_player);
            MethodInfo serialize = stats.GetType().GetMethod("Serialize", All);
            ServerHooks.SendMatchMessage(ScoreStatsMessage, m => { m.Write(true); m.Write(index); serialize.Invoke(stats, new[] { m.Buffer }); });
        }

        private static bool IsBot(object c)
        {
            FieldInfo f = c.GetType().GetField(R.Name("Hero.Bot"), All);
            return f != null && (bool)f.GetValue(c);
        }

        private static float Experience(object c) { return (float)c.GetType().GetProperty(R.Name("Hero.Xp"), All).GetValue(c, null); }

        private static int Level(object c) { return (int)c.GetType().GetMethod(R.Name("Hero.LevelOf"), All, null, Type.EmptyTypes, null).Invoke(c, null); }

        /// <summary>MatchMessage 3 (upgrade ability: int, int): Hero.Upgrade, then MatchMessage 36 (upgrade ability done).</summary>
        private bool OnUpgradeAbility(GameBuffer b)
        {
            int a = b.ReadInt(), c = b.ReadInt();
            _player.GetType().GetMethod(R.Name("Hero.Upgrade"), All).Invoke(_player, new object[] { a, c });
            ServerHooks.SendMatchMessage(UpgradeAbilityDoneMessage, null);
            _log("leveling: upgrade ability (" + a + ", " + c + ") at level " + Level(_player) + "; sent upgrade ability done");
            return true;
        }

        /// <summary>
        /// The end of the match: the stand-in reward (StandInXpPerMinute per match minute, the
        /// minutes clamped to World.MinLength/World.MaxLength for the queue), split
        /// over a mission box and a time box, sent as MatchMessage 5 with the hub's own Payout
        /// (v9). Returns the account XP to add, or -1 if the match already ended.
        /// </summary>
        public int EndMatch(int queueType, int mapIndex, Account account, int[] boxIds, int teamSupplies, int placement = 0)
        {
            if (_ended) return -1;
            _ended = true;
            Type rm = R.Type("Type.RewardTables");
            object queue = Enum.ToObject(rm.GetMethod(R.Name("World.MinLength"), All).GetParameters()[0].ParameterType, queueType);
            float min = (float)rm.GetMethod(R.Name("World.MinLength"), All).Invoke(null, new[] { queue });
            float max = (float)rm.GetMethod(R.Name("World.MaxLength"), All).Invoke(null, new[] { queue });
            double seconds = (DateTime.UtcNow - _start).TotalSeconds;
            double counted = Math.Max(min, Math.Min(max, seconds));
            int xp = (int)Math.Round(StandInXpPerMinute * counted / 60.0);
            // The XP is split evenly over the boxes (stand-in: the box contents lived in the back end's drop table).
            int[] boxScores = new int[boxIds.Length];
            for (int i = 0; i < boxScores.Length; i++) boxScores[i] = xp / boxScores.Length + (i < xp % boxScores.Length ? 1 : 0);
            uint xpBefore;
            byte character;
            lock (account) { xpBefore = account.StoryMapXp; character = account.Characters.Count > 0 ? account.Characters[0].Id : (byte)7; }
            // The hero actually played (HeroId), so the hub's match history shows its portrait;
            // the account's first character was used before (owner, 2026-10-07: a hero variant listed as the player).
            try { character = Convert.ToByte(_player.GetType().GetProperty(R.Name("Type.HeroId"), All).GetValue(_player, null)); }
            catch (Exception) { }
            RewardsInput input = new RewardsInput
            {
                UndeadKills = UndeadKills,
                Deaths = Deaths,
                Supplies = SuppliesHandedIn,
                SuppliesBanked = teamSupplies,
                StartTicks = _start.Ticks,
                MatchSeconds = (uint)seconds,
                Team = Convert.ToByte(_player.GetType().GetProperty(R.Name("Entity.Team"), All).GetValue(_player, null)),
                Placement = (byte)placement,
                Character = character,
                QueueType = (byte)queueType,
                MapIndex = mapIndex,
                LevelHit = Level(_player),
                AccountXp = xp,
                SteamId = account.SteamId,
                BoxIds = boxIds,
                BoxPoints = boxScores,
            };
            SendScoreStats(0f, true);   // the end screen's scoreboard reads these
            byte[] blob = HubRewards.Build(_g.Install, input);
            ServerHooks.SendMatchMessage(RewardsMessage, m => { m.Write(blob.Length); m.Write(blob); });
            _log("leveling: STAND-IN reward (original rates lost): " + xp + " account XP for " + (int)seconds + " s (counted " + (int)counted + " s, limits " + min + "-" + max +
                 " s), placement " + placement + ", boxes " + string.Join(", ", boxIds.Select((b, i) => b + "=" + boxScores[i]).ToArray()) + ", " + UndeadKills + " kills, " + Deaths + " deaths; sent Rewards (" + blob.Length + "-byte rewards blob v9)");
            return xp;
        }

        /// <summary>
        /// Applies account XP to the account: story map XP, then for every level crossed the level-up
        /// items from Levels.Rewards. Returns a log line. Caller saves the account.
        /// </summary>
        public static string ApplyToAccount(GameRuntime g, Account a, int xp)
        {
            int before = AccountLevel(g, a.StoryMapXp);
            a.StoryMapXp += (uint)Math.Max(0, xp);
            int after = AccountLevel(g, a.StoryMapXp);
            List<string> items = new List<string>();
            MethodInfo levelUp = R.Type("Type.Levels").GetMethod(R.Name("Levels.Rewards"), All);
            for (int level = before + 1; level <= after; level++)
            {
                IEnumerable rewards = (IEnumerable)levelUp.Invoke(null, new object[] { level });
                if (rewards == null) continue;
                foreach (object r in rewards) items.Add("level " + level + ": " + Grant(a, r));
            }
            return "story map XP +" + xp + " -> " + a.StoryMapXp + ", account level " + before + " -> " + after + (items.Count == 0 ? "" : "; " + string.Join("; ", items.ToArray()));
        }

        /// <summary>One crafting.Reward (Type, Craft.BlueprintField, Craft.Amount) into the account.</summary>
        private static string Grant(Account a, object reward)
        {
            Type t = reward.GetType();
            int type = Convert.ToInt32(t.GetField("Type", All).GetValue(reward));
            ushort id = Convert.ToUInt16(t.GetField(R.Name("Craft.BlueprintField"), All).GetValue(reward));
            int quantity = Convert.ToInt32(t.GetField(R.Name("Craft.Amount"), All).GetValue(reward));
            string name = Enum.GetName(t.GetField("Type", All).FieldType, type) ?? type.ToString();
            // reward type (the global enum Reward.Type uses) -> stackable type for the item rewards;
            // character and research points go to the account.
            int stackable;
            switch (type)
            {
                case 3: stackable = 3; break;   // Part
                case 8: stackable = 2; break;   // blue print -> Schematic
                case 9: stackable = 1; break;   // Consumable
                case 12: stackable = 4; break;  // vanity title
                case 13: stackable = 5; break;  // ProfileIcon
                case 15: stackable = 6; break;  // gadget blueprint
                case 17: stackable = 7; break;  // Design
                case 20: stackable = 8; break;  // Keycode
                case 21: stackable = 9; break;  // Lockbox
                case 10: a.HeroPoints += quantity; return name + " +" + quantity;
                case 19: a.LabPoints += quantity; return name + " +" + quantity;
                default: return name + " " + id + " x" + quantity + " (not applied: no account field for it yet)";
            }
            Inventory.AddStackable(a, id, stackable, quantity);
            return name + " " + id + " x" + quantity;
        }
    }
}

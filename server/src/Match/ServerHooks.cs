using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// The server side of the game's World. The client build ships its
    /// server hooks as empty virtuals that return dummies, so nothing takes
    /// damage; the emitted server World overrides them and forwards here
    /// (owner's design, step 3a, 2026-10-06). Everything runs on the game thread.
    /// </summary>
    public static class ServerHooks
    {
        private const BindingFlags All = GameRuntime.All;
        public const float DefaultAggroRange = 600f;
        /// <summary>Zombie strikes reach 16-20 units (owner's notes), so close in to about 14.</summary>
        public const float AttackRange = 14f;
        /// <summary>Strike once the aim is within this many degrees of the target.</summary>
        public const float AttackAimDegrees = 30f;
        public const float AttackInterval = 1f;

        private static GameRuntime _g;
        private static uint _statChangeId;
        private static bool _knockbackLogged, _sctLogged;
        private static readonly Dictionary<string, Func<object, object[], object>> Handlers = new Dictionary<string, Func<object, object[], object>>();
        public static readonly Dictionary<object, float> LastAttack = new Dictionary<object, float>();
        private static float _time;

        /// <summary>Called for every game log line we write (the server's Log, or the probe's console).</summary>
        public static Action<string> Log = s => { };

        /// <summary>The client whose combat text and view we serve (client 0).</summary>
        public static object Client0;

        /// <summary>Ability the AI attacks with; read from the NPC's ability bar slot 0 when spawned.</summary>
        public static readonly Dictionary<object, object> StrikeAbility = new Dictionary<object, object>();

        /// <summary>Per-NPC aggro range (static zombies idle until a player is close); default AggroRange.</summary>
        public static readonly Dictionary<object, float> AggroRange = new Dictionary<object, float>();

        /// <summary>Per-NPC time between strikes (default AttackInterval); hero NPCs stagger the player on every hit.</summary>
        public static readonly Dictionary<object, float> StrikeInterval = new Dictionary<object, float>();

        /// <summary>Per-NPC strike range (default AttackRange).</summary>
        public static readonly Dictionary<object, float> StrikeRange = new Dictionary<object, float>();

        /// <summary>Per-NPC path state: when to recompute, and the waypoint to walk to (null = straight at the target).</summary>
        private static readonly Dictionary<object, float> NavNext = new Dictionary<object, float>();
        private static readonly Dictionary<object, float[]> NavWaypoint = new Dictionary<object, float[]>();
        /// <summary>Where each steering NPC was at its last recompute, and until when it uses the short lookahead after getting stuck.</summary>
        private static readonly Dictionary<object, float[]> NavLastPos = new Dictionary<object, float[]>();
        private static readonly Dictionary<object, float> NavShortUntil = new Dictionary<object, float>();
        public const float NavRecompute = 0.5f;
        private static bool _pathLogged;

        /// <summary>NPCs the AI leaves alone (scripted by the tutorial director).</summary>
        public static readonly HashSet<object> Passive = new HashSet<object>();

        /// <summary>NPCs that turn and strike but never walk (the hooking Puller).</summary>
        public static readonly HashSet<object> HoldPosition = new HashSet<object>();

        /// <summary>When each NPC finishes getting up from its spawn pose (0 = awake from the start).</summary>
        public static readonly Dictionary<object, float> WakeUntil = new Dictionary<object, float>();

        /// <summary>Runs after the AI in each World update (the tutorial director), with the game time.</summary>
        public static Action<float> AfterUpdate;

        public static void Init(GameRuntime g)
        {
            _g = g;
            Handlers[R.Name("Hook.StatChange")] = OnStatChange;
            Handlers[R.Name("Hook.Effect")] = OnEffect;
            Handlers[R.Name("Hook.Buff")] = OnBuff;
            Handlers[R.Name("World.SpawnObject")] = HandleSpawn;
            Handlers[R.Name("Hook.Push")] = OnPush;
            Handlers[R.Name("Hook.FloatText") + "(" + R.Short("Type.Hero") + ")"] = SendSctToPlayer;
            Handlers[R.Name("Hook.FloatText") + "(" + R.Short("Type.Peer") + ")"] = SendSctToClient;
            Handlers[R.Name("Hook.Tick")] = OnTick;
            Handlers[R.Name("Hook.Input")] = HandleInput;
            Handlers[R.Name("Hook.Destroyed")] = OnDestroyed;
        }

        /// <summary>The World virtuals the server overrides.</summary>
        /// <summary>Clears what belongs to one match (the session's queue, client, AI state).</summary>
        public static void ResetMatchState()
        {
            QueueReliable = null;
            Client0 = null;
            OnKill = null;
            OnGameMessage = null;
            StrikeAbility.Clear();
            SpecialMoves.Clear();
            LastSpecial.Clear();
            SpecialBusyUntil.Clear();
            StaggeredAt.Clear();
            AggroRange.Clear();
            WakeUntil.Clear();
            NavNext.Clear();
            NavWaypoint.Clear();
            NavLastPos.Clear();
            NavShortUntil.Clear();
            _pathLogged = false;
            Passive.Clear();
            StrikeRange.Clear();
            StrikeInterval.Clear();
            HoldPosition.Clear();
            AfterUpdate = null;
            PressNote = null;
            ChooseTarget = null;
            SelfCastPressed = null;
            DamageFilter = null;
            BlockControl = null;
            _blockedLogged.Clear();
            _lastSelfCast = false;
            _lastPressCount = -1;
            LastAttack.Clear();
            RecentSpawns.Clear();
            _lastControls = null;
        }

        public static IList<HookedMethod> Methods(Type gm)
        {
            var hooks = new List<HookedMethod>();
            foreach (MethodInfo m in gm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!m.IsVirtual || m.IsFinal) continue;
                string key = m.Name == R.Name("Hook.FloatText") ? m.Name + "(" + m.GetParameters()[0].ParameterType.Name + ")" : m.Name;
                if (!Handlers.ContainsKey(key)) continue;
                // OnTick keeps the game's own update and adds our AI after it.
                hooks.Add(new HookedMethod { Method = m, Key = key, CallBaseFirst = m.Name == R.Name("Hook.Tick") });
            }
            Log("server World overrides: " + string.Join(", ", hooks.Select(h => h.Key).ToArray()));
            return hooks;
        }

        private static readonly HashSet<string> Called = new HashSet<string>();

        /// <summary>The first call of each hook is logged, to show which ones the game uses.</summary>
        public static object Dispatch(string key, object gm, object[] args)
        {
            if (Called.Add(key) && key != R.Name("Hook.Tick")) Log("hook " + key + ": first call");
            try { return Handlers[key](gm, args); }
            catch (Exception e)
            {
                Log("hook " + key + " failed: " + GameRuntime.Unwrap(e));
                throw;
            }
        }

        // ---- stats ----

        private static object OnStatChange(object gm, object[] a)
        {
            object entity = a[0], statType = a[1], stackType = a[2], reason = a[4], changer = a[5], changeType = a[6], order = a[9];
            float value = (float)a[3];
            if (DamageFilter != null && value < 0 && statType.ToString() == R.Name("Stat.Health")) value = DamageFilter(entity, changer, value);
            bool pure = (bool)a[7], isPermanent = (bool)a[8], sendSct = (bool)a[10];
            object stats = Field(entity, R.Name("Entity.StatBlock"));
            uint id = ++_statChangeId;
            float before = GetStat(entity, statType);
            // stat-container.Apply(stat, id, stack, reason, changeType, order, pure, isPermanent, value, changer, out tranced, out crit, out punctured, out blocked, out _)
            object[] call = { statType, id, stackType, reason, changeType, Convert.ToInt32(order), pure, isPermanent, value, changer, false, false, false, false, false };
            float applied = (float)Method(stats, R.Name("Stats.Apply")).Invoke(stats, call);
            bool tranced = (bool)call[10], crit = (bool)call[11], punctured = (bool)call[12], blocked = (bool)call[13];
            int changerId = changer == null ? -1 : (ushort)Prop(changer, R.Name("Entity.GlobalIndex"));
            object result = R.Type("StatChangeRecord").GetConstructors(All).First(c => c.GetParameters().Length == 10).Invoke(new object[]
                { statType, (ushort)Prop(entity, R.Name("Entity.LocalIndex")), id, changerId, isPermanent, applied, tranced, crit, punctured, blocked });
            if (_statChangeId <= 40)
                Log("OnStatChange #" + id + " " + Describe(entity) + " " + statType + " " + value + " -> applied " + applied + " (now " + GetStat(entity, statType) +
                    ", pure " + pure + ", permanent " + isPermanent + ", reason " + reason + ", type " + changeType + ")");
            else if (statType.ToString() == R.Name("Stat.Health") && value != 0)
                Log("OnStatChange " + Describe(entity) + " Health " + before + " -> " + GetStat(entity, statType) + " (" + value + (changer != null ? " from " + Describe(changer) : "") + ")");
            // Combat text only for health and shield, and not for an entity's own spawn and
            // setup changes (they showed as a green "60" over zombies: rotation speed +60).
            string statName = statType.ToString();
            bool showText = (statName == R.Name("Stat.Health") || statName == R.Name("Stat.Shield")) && changer != entity;
            if (sendSct && showText && value != 0) SendDamageText(entity, changer, statType, before, applied, crit, punctured, blocked, tranced, reason, changeType);
            // A kill: a character's health crosses to zero. The killer is the character behind the changer.
            if (OnKill != null && statName == R.Name("Stat.Health") && before > 0f && GetStat(entity, statType) <= 0f &&
                R.Type("Type.Fighter").IsInstanceOfType(entity))
            {
                try { OnKill(entity, changer); }
                catch (Exception e) { Log("kill handling failed: " + GameRuntime.Unwrap(e)); }
            }
            return result;
        }

        /// <summary>Called when a character dies: (victim, the changer that dealt the blow: a spell or hit object, or a character; may be null).</summary>
        public static Action<object, object> OnKill;

        /// <summary>A reliable game message from the client: (type, buffer positioned after the type) -> true if handled.</summary>
        public static Func<int, GameBuffer, bool> OnGameMessage;

        /// <summary>
        /// Queues a reliable game message to the client: 1, conductor-game-message-type.MatchMessage (0),
        /// the game message type, then the body (the layout the server's other messages use).
        /// </summary>
        public static bool SendMatchMessage(int type, Action<GameBuffer> body)
        {
            if (QueueReliable == null) return false;
            GameBuffer m = GameBuffer.Create();
            m.Write((byte)1);
            m.Write((byte)0);
            m.Write((byte)type);
            if (body != null) body(m);
            QueueReliable(m.Buffer);
            return true;
        }

        private static object OnEffect(object gm, object[] a)
        {
            object changer = a[0], entity = a[1], type = a[2];
            uint id = ++_statChangeId;
            int changerId = changer != null && R.Type("Type.WorldObject").IsInstanceOfType(changer) ? (ushort)Prop(changer, R.Name("Entity.GlobalIndex")) : -1;
            object change = R.Type("Type.EffectChange").GetConstructors(All).First(c => c.GetParameters().Length == 4)
                .Invoke(new object[] { type, (ushort)Prop(entity, R.Name("Entity.LocalIndex")), id, changerId });
            a[3] = change;
            if (TraceControl) Log("trace stat effect " + type + " changer " + Describe(changer) + " on " + Describe(entity));
            if (ControlEffects.Contains(type.ToString()) && Blocked(changer, entity, "stat effect " + type)) return null;
            string t = type.ToString();
            if ((t == R.Name("Effect.KnockStun") || t == R.Name("Effect.LightKnockStun")) && GuardStagger(entity))
            {
                if (_blockedLogged.Add("stagger guard " + entity.GetType().Name))
                    Log("stagger guard: " + Describe(entity) + " was staggered less than " + SpecialStaggerImmunity + " s ago; this basic-attack stagger is dropped (logged once per kind)");
                return null;
            }
            object container = Field(entity, R.Name("Entity.Effects"));
            object[] call = { change };
            Method(container, R.Name("Effects.Add")).Invoke(container, call);
            a[3] = call[0];
            return null;
        }

        private static object OnBuff(object gm, object[] a)
        {
            object buff = a[0], target = a[1], owner = a[2];
            if (buff == null || target == null)
            {
                Log("OnBuff: skipped, " + (buff == null ? "no buff (pool empty?)" : "buff " + Describe(buff)) + ", target " + Describe(target) + ", owner " + Describe(owner));
                return Enum.ToObject(R.Type("BuffResult"), 0);
            }
            if (TraceControl) Log("trace buff " + Describe(buff) + " owner " + Describe(owner) + " target " + Describe(target) + " ability " + AbilityName(buff) + " / owner's " + AbilityName(owner) + " via " + GameFrames());
            bool stagger = buff.GetType().Name == R.Short("Buff.Stagger");
            if ((BlockControl != null && owner != null && BlockControl(RootOwner(owner), target)) || (stagger && GuardStagger(target)))
            {
                // The melee knockback-stun buff (role Effect.KnockStun): Effect.LightKnockStun stat effect, knockback
                // vector and the stagger animation, synced to the client. Drop it whole (the hit's
                // damage is a separate OnStatChange). Other buffs are kept and logged.
                bool control = buff.GetType().Name == R.Short("Buff.Stagger");
                if (_blockedLogged.Add("buff " + buff.GetType().Name))
                    Log("OnBuff: " + Describe(buff) + " from " + Describe(owner) + " on " + Describe(target) + (control ? " dropped (knockback-stun buff)" : " kept") + " (logged once per type)");
                if (control)
                {
                    // The buff came out of the game's pool (World.PooledEffect); hand it back, or the pool runs
                    // dry and every later knockback hit throws inside the game's hit code (2026-10-07 playtest: from
                    // about 6 minutes in, "No GameObject in pool" for that buff on each hit, zombies dying seconds late).
                    try { _g.WorldType.GetMethod(R.Name("World.Recycle"), All).Invoke(_g.World, new[] { buff }); }
                    catch (Exception e) { if (_poolReturnLogged++ < 3) Log("OnBuff: could not return " + Describe(buff) + " to the pool: " + GameRuntime.Unwrap(e).Message); }
                    return Enum.ToObject(R.Type("BuffResult"), 0);
                }
            }
            buff.GetType().GetProperty("Target", All).SetValue(buff, target, null);
            if (owner != null) Method(buff, "SetOwner").Invoke(buff, new[] { owner });
            Method(target, R.Name("Entity.AddEffect")).Invoke(target, new[] { buff });
            try { Method(buff, R.Name("Entity.Appear"), Type.EmptyTypes).Invoke(buff, null); }
            catch (TargetInvocationException e)
            {
                // e.g. the supply-carry buff touches a view effect that the server doesn't have.
                if (_buffSetupLogged.Add(buff.GetType().Name))
                    Log("OnBuff: " + Describe(buff) + "'s own setup threw " + GameRuntime.Unwrap(e).GetType().Name + " (kept the buff; logged once per type)");
            }
            // Return the success value of the buff result enum (role BuffResult; taken as 0 until confirmed).
            return Enum.ToObject(R.Type("BuffResult"), 0);
        }

        private static int _spawnsLogged;
        private static readonly HashSet<string> _buffSetupLogged = new HashSet<string>();

        /// <summary>The last objects spawned through HandleSpawn (for diagnostics).</summary>
        public static readonly List<object> RecentSpawns = new List<object>();

        private static object HandleSpawn(object gm, object[] a)
        {
            object obj = a[0], owner = a[1];
            RecentSpawns.Add(obj);
            if (RecentSpawns.Count > 50) RecentSpawns.RemoveAt(0);
            if (owner != null)
            {
                // Spells spawn Neutral, and the hit test compares the spell's own team
                // with the target's: give the spell its owner's team first.
                PropertyInfo team = obj.GetType().GetProperty(R.Name("Entity.Team"), All);
                team.SetValue(obj, team.GetValue(owner, null), null);
                Method(obj, "SetOwner").Invoke(obj, new[] { owner });
            }
            // get and spawn game object from pool only fetches the object and calls this hook;
            // Spawn() itself calls the object's own HandleSpawn(), never this one.
            // So the server spawns it here, or spells stay inactive and never hit.
            bool spawned = false;
            if (!(bool)Prop(obj, "IsActive"))
            {
                Method(obj, R.Name("Entity.Appear"), Type.EmptyTypes).Invoke(obj, null);
                spawned = true;
            }
            if (_spawnsLogged++ < 10) Log("spawn " + Describe(obj) + " owner " + Describe(owner) + " team " + Prop(obj, R.Name("Entity.Team")) + (spawned ? "" : " (already active)"));
            if (owner != null && R.Type("Type.Hero").IsInstanceOfType(owner) && _playerSpellsLogged++ < 20)
                LogAim(obj, owner);
            return null;
        }

        /// <summary>Knockback: apply the direction to the owner's prioritized vector.</summary>
        private static object OnPush(object gm, object[] a)
        {
            object vector = a[0], direction = a[1], owner = a[2], priority = a[5];
            Type changeType = R.Type("StatEffectChangeList");
            object change = changeType.GetConstructors(All)[0].Invoke(new object[] { null });
            Method(change, "Initialize").Invoke(change, new[] { vector, direction, priority });
            if (!Blocked(a[2], a[3], "knockback") && !Blocked(a[3], a[2], "knockback"))
                Method(vector, "Apply").Invoke(vector, new[] { change });
            if (!_knockbackLogged)
            {
                _knockbackLogged = true;
                Log("knockback: direction " + direction + ", priority " + priority + ", owner " + Describe(owner) +
                    ", vector now " + vector.GetType().GetProperty("Value", All).GetValue(vector, null));
            }
            return R.Type("StatEffectChangeWrap").GetConstructors(All).First(c => c.GetParameters().Length == 1).Invoke(new[] { change });
        }

        // ---- combat text ----

        private static void SendDamageText(object entity, object changer, object statType, float before, float applied,
                                           bool crit, bool punctured, bool blocked, bool tranced, object reason, object changeType)
        {
            if (Client0 == null) return;
            Type t = R.Type("Type.FloatingText");
            object data = Activator.CreateInstance(t);
            Set(data, R.Name("Stat.Kind"), statType);
            Set(data, R.Name("Stat.Start"), before);
            Set(data, "Value", applied);
            Set(data, R.Name("Text.Crit"), crit);
            Set(data, R.Name("Text.Pierce"), punctured);
            Set(data, R.Name("Text.Block"), blocked);
            Set(data, R.Name("Text.Trance"), tranced);
            Set(data, R.Name("Text.Kill"), (bool)Prop(entity, R.Name("Entity.Dead")));
            Set(data, R.Name("Text.HitFx"), true);
            Set(data, R.Name("Stat.Reason"), reason);
            Set(data, R.Name("Stat.ChangeKind"), changeType);
            object owner = changer == null ? null : R.Type("Type.Entity").IsInstanceOfType(changer) ? changer : Prop(changer, "Owner");
            SendSctToClient(_g.World, new[] { Client0, entity, owner, changer, data });
        }

        private static object SendSctToPlayer(object gm, object[] a)
        {
            return Client0 == null ? null : SendSctToClient(gm, new[] { Client0, a[1], a[2], a[3], a[4] });
        }

        private static object SendSctToClient(object gm, object[] a)
        {
            object sct = _g.WorldType.GetField(R.Name("Text.Sync"), All).GetValue(gm);
            if (sct == null) return null;
            Method(sct, R.Name("Hook.FloatText")).Invoke(sct, a);
            if (!_sctLogged)
            {
                _sctLogged = true;
                Log("combat text: first floating text to client 0 for " + Describe(a[1]));
            }
            return null;
        }

        private static int _playerSpellsLogged;

        /// <summary>For a player's spell: where the player is aiming, versus where the nearest live NPC is.</summary>
        private static void LogAim(object spell, object player)
        {
            float[] p = Position(player);
            float[] aim = Vector(Prop(player, R.Name("Fighter.Aim")));
            float[] target = Vector(Field(player, R.Name("Fighter.AimGoal")));
            string mouse = "";
            try { mouse = ", mouse " + Prop(player, "MousePosition"); } catch (Exception) { }
            string nearest = "no NPC";
            float best = float.MaxValue;
            foreach (object npc in (Array)_g.WorldType.GetField(R.Name("World.Npcs"), All).GetValue(_g.World))
            {
                if (npc == null || !(bool)Prop(npc, "IsActive") || (bool)Prop(npc, R.Name("Entity.Dead"))) continue;
                float[] n = Position(npc);
                float dx = n[0] - p[0], dy = n[1] - p[1], d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d >= best) continue;
                best = d;
                double angle = Math.Atan2(aim[0] * dy - aim[1] * dx, aim[0] * dx + aim[1] * dy) * 180.0 / Math.PI;
                nearest = Describe(npc) + " at " + d.ToString("0.0") + " units, direction (" + (dx / d).ToString("0.00") + ", " + (dy / d).ToString("0.00") +
                          "), " + Math.Abs(angle).ToString("0") + " degrees off the aim";
            }
            Log("player spell " + Describe(spell) + ": player at (" + p[0].ToString("0.0") + ", " + p[1].ToString("0.0") + "), aim (" + aim[0].ToString("0.00") + ", " +
                aim[1].ToString("0.00") + "), target aim (" + target[0].ToString("0.00") + ", " + target[1].ToString("0.00") + ")" + mouse + "; nearest " + nearest);
        }

        // ---- destroy ----

        /// <summary>Queues a reliable, in-order game message for client 0 (set by the match session).</summary>
        public static Action<object> QueueReliable;

        /// <summary>
        /// Set by a director to put every synchronizable in this tick's frame (MatchSession clears it). Used when a
        /// reliable message depends on synced state: the client's HUD drops the victory screen that team finished
        /// opened on any frame where ActiveMode.IsCompleted isn't true yet (UI-HUD-binding.Update), so the end
        /// messages must arrive in the same frame as the IsCompleted sync (2026-10-07: the Scavenger end screen
        /// flashed and vanished).
        /// </summary>
        public static bool SyncNow;

        /// <summary>Destroy messages sent so far (checked by the probe).</summary>
        public static int DestroyMessages;

        /// <summary>
        /// GM.Update passes every destroyed object to this hook for each client that
        /// has seen it (owner's design, step 3c). The message: byte 1 (game
        /// message), byte 0 (MatchMessage), byte 21 (game object destroyed), the spawn
        /// id as this client knows it (ranged 0..3), the object's global index
        /// (ranged 0..World.ObjectCount), then the object's own World.WriteDestroy
        /// (for an NPC: IsDead and its killer), so the client plays the death.
        /// </summary>
        private static object OnDestroyed(object gm, object[] a)
        {
            object client = a[0], obj = a[1];
            if (QueueReliable == null) return null;
            int clientIndex = (int)Prop(client, "Index");
            ushort global = (ushort)Prop(obj, R.Name("Entity.GlobalIndex"));
            object spawnDatas = gm.GetType().GetProperty(R.Name("World.SpawnTable"), All).GetValue(gm, null);
            int spawnId = (int)spawnDatas.GetType().GetMethod(R.Name("SpawnData.NextSpawnId"), All, null, new[] { typeof(int), typeof(int) }, null)
                .Invoke(spawnDatas, new object[] { (int)global, clientIndex });
            int objects = (int)gm.GetType().GetProperty(R.Name("World.ObjectCount"), All).GetValue(gm, null);
            GameBuffer m = GameBuffer.Create();
            m.Write((byte)1);
            m.Write((byte)0);
            m.Write((byte)21);
            m.WriteRanged(0, 3, spawnId);
            m.WriteRanged(0, objects, global);
            Method(obj, R.Name("World.WriteDestroy")).Invoke(obj, new[] { m.Buffer, client });
            QueueReliable(m.Buffer);
            DestroyMessages++;
            Log("destroy message for " + Describe(obj) + " (spawn id " + spawnId + ") to client " + clientIndex + ": " + BitConverter.ToString(m.ToBytes()));
            return null;
        }

        // ---- input ----

        private static string _lastControls;
        private static int _lastPressCount = -1;
        private static bool _lastSelfCast;

        /// <summary>Adjusts a health loss (entity, changer, value) -> value; the tutorial uses it for the wooden barricade.</summary>
        public static Func<object, object, float, float> DamageFilter;

        /// <summary>
        /// (source character, target) -> true to drop a control effect: a stun-type stat effect or a
        /// knockback. The source is the root owner of the buff/spell that applies it. The tutorial
        /// uses it so the rival NPCs' hits don't stun the player (owner, run 4).
        /// </summary>
        public static Func<object, object, bool> BlockControl;

        /// <summary>stat effect type names that take control away from the target.</summary>
        private static HashSet<string> _controlEffects;
        private static HashSet<string> ControlEffects { get { return _controlEffects ?? (_controlEffects = new HashSet<string> {
            R.Name("Effect.Stun"), R.Name("Effect.Disarm"), R.Name("Effect.Silence"), "Disabled", R.Name("Effect.Locked"), R.Name("Effect.Slow"), "Control", R.Name("Effect.Blind"),
            R.Name("Effect.KnockStun"), R.Name("Effect.Taunt"), R.Name("Effect.BigStun"), R.Name("Effect.LightKnockStun"), R.Name("Effect.Bind"), R.Name("Effect.FlipX"), R.Name("Effect.FlipY") }); } }
        private static readonly HashSet<string> _blockedLogged = new HashSet<string>();
        private static int _poolReturnLogged;

        /// <summary>Follows Owner from a buff or spell up to the character behind it.</summary>
        public static object RootOwner(object o)
        {
            Type character = R.Type("Type.Fighter");
            for (int i = 0; i < 6 && o != null; i++)
            {
                if (character.IsInstanceOfType(o)) return o;
                PropertyInfo owner = null;
                for (Type t = o.GetType(); t != null && owner == null; t = t.BaseType)
                    owner = t.GetProperty("Owner", All | BindingFlags.DeclaredOnly);
                object next = owner == null ? null : owner.GetValue(o, null);
                if (next == null || next == o) return o;
                o = next;
            }
            return o;
        }

        /// <summary>Probe only: log every stat effect, knockback and buff (diagnostics).</summary>
        public static bool TraceControl;

        /// <summary>
        /// The weapon-primary hit object on the current stack, or null: role Hit.MeleePrimary (Ability.LightFirst,
        /// Ability.HeavyFirst, fists primary) or Hit.RangedPrimary (pistols primary, Ability.GunFirst, 142), per the
        /// game's ability registrations. Used to tell a basic attack's knockback-stun from an ability's.
        /// </summary>
        public static string BasicAttackHitOnStack()
        {
            var frames = new System.Diagnostics.StackTrace(false).GetFrames();
            if (frames == null) return null;
            foreach (var f in frames)
            {
                MethodBase m = f.GetMethod();
                if (m != null && m.DeclaringType != null && BasicAttackHits.Contains(m.DeclaringType.FullName)) return m.DeclaringType.FullName;
            }
            return null;
        }

        private static HashSet<string> _basicAttackHits;
        private static HashSet<string> BasicAttackHits
        {
            get { return _basicAttackHits ?? (_basicAttackHits = new HashSet<string> { R.Name("Hit.MeleePrimary"), R.Name("Hit.RangedPrimary") }); }
        }

        /// <summary>The game's own frames on the current stack (diagnostics): declaring type::method, innermost first.</summary>
        public static string GameFrames()
        {
            var frames = new System.Diagnostics.StackTrace(false).GetFrames() ?? new System.Diagnostics.StackFrame[0];
            return string.Join(" < ", frames.Select(f => f.GetMethod()).Where(m => m != null && m.DeclaringType != null && m.DeclaringType.Assembly != typeof(ServerHooks).Assembly && m.DeclaringType.Assembly.GetName().Name == "Assembly-CSharp")
                                           .Take(6).Select(m => m.DeclaringType.FullName + "::" + m.Name).ToArray());
        }

        /// <summary>The Ability.Key of the ability an object came from (Spell.Ability), or "-".</summary>
        public static string AbilityName(object o)
        {
            if (o == null) return "-";
            PropertyInfo p = o.GetType().GetProperty(R.Name("Spell.Ability"), All);
            object a = p == null ? null : p.GetValue(o, null);
            if (a == null) return "-";
            PropertyInfo id = a.GetType().GetProperty(R.Name("Ability.Key"), All);
            return id == null ? a.GetType().Name : id.GetValue(a, null).ToString();
        }

        private static bool Blocked(object source, object target, string what)
        {
            if (TraceControl) Log("trace " + what + ": source " + Describe(source) + " root " + Describe(source == null ? null : RootOwner(source)) + " target " + Describe(target));
            if (BlockControl == null || source == null || target == null) return false;
            object root = RootOwner(source);
            if (!BlockControl(root, target)) return false;
            if (_blockedLogged.Add(what + " " + Describe(root)))
                Log("control blocked: " + what + " from " + Describe(root) + " (via " + Describe(source) + ") on " + Describe(target) + " (logged once per kind and source)");
            return true;
        }

        /// <summary>Called on a self-cast press from the client (the tutorial director uses it at the supply point).</summary>
        public static Action SelfCastPressed;

        /// <summary>Picks an NPC's target given the player (the tutorial director lets ambush walkers go for the companion too); null = the player.</summary>
        public static Func<object, object, object> ChooseTarget;

        /// <summary>Extra context for the ability-press log line (the tutorial director adds the distance to the storage room).</summary>
        public static Func<string> PressNote;
        /// <summary>Controller fields that count frames; left out so only real input changes are logged.</summary>
        private static HashSet<string> _frameCounters;
        private static HashSet<string> FrameCounters
        {
            get { return _frameCounters ?? (_frameCounters = new HashSet<string> { R.Name("ControllerData.FrameA"), R.Name("ControllerData.FrameB") }); }
        }

        /// <summary>
        /// Called with each client frame's controller data, just before the game's
        /// own server set controller data. Logs the int and bool fields
        /// (ability id, use id, ...) whenever they change, to see what the
        /// client asks for.
        /// </summary>
        private static object HandleInput(object gm, object[] a)
        {
            object data = a[1];
            if (data == null) return null;
            var parts = new List<string>();
            foreach (FieldInfo f in data.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if ((f.FieldType == typeof(int) || f.FieldType == typeof(bool) || f.FieldType.IsEnum) && !FrameCounters.Contains(f.Name))
                    parts.Add(f.Name + "=" + f.GetValue(data));
            // A self-cast press (rising edge): what the player's X presses at the supply point arrive as.
            FieldInfo selfCast = data.GetType().GetField(R.Name("ControllerData.CastOnSelf"), All);
            bool sc = selfCast != null && (bool)selfCast.GetValue(data);
            if (sc && !_lastSelfCast && SelfCastPressed != null) SelfCastPressed();
            _lastSelfCast = sc;
            // Name each new ability press (the pressed value is an index into the bar's cached abilities).
            FieldInfo pressed = data.GetType().GetField(R.Name("ControllerData.Pressed"), All), counter = data.GetType().GetField(R.Name("ControllerData.PressCounter"), All);
            if (pressed != null && counter != null)
            {
                int index = (int)pressed.GetValue(data), count = (int)counter.GetValue(data);
                if (index >= 0 && count != _lastPressCount)
                {
                    _lastPressCount = count;
                    try
                    {
                        object player = _g.GetPlayer(0);
                        object bar = Prop(player, R.Name("Fighter.Bar"));
                        object ability = bar.GetType().GetMethods(All).First(m => m.Name == R.Name("Bar.Cached") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int)).Invoke(bar, new object[] { index });
                        if (ability == null || !new[] { R.Name("Ability.HeavyFirst"), R.Name("Ability.HeavySecond"), R.Name("Ability.LightFirst"), R.Name("Ability.GunFirst"), "Dash" }.Contains(Prop(ability, R.Name("Ability.Key")).ToString()))
                            Log("ability press: cached " + index + " = " + (ability == null ? "(none)" : Prop(ability, R.Name("Ability.Key")) + " in slot " + ability.GetType().GetField(R.Name("Bar.Slot"), All).GetValue(ability)) +
                                ", player at (" + string.Join(", ", Position(player).Select(v => v.ToString("0")).ToArray()) + ")" + (PressNote == null ? "" : ", " + PressNote()));
                    }
                    catch (Exception e) { Log("ability press: cached " + index + " (" + GameRuntime.Unwrap(e).Message + ")"); }
                }
            }
            string controls = string.Join(" ", parts.ToArray());
            if (controls != _lastControls)
            {
                _lastControls = controls;
                Log("controls from client " + a[0] + ": " + controls);
            }
            return null;
        }

        // ---- AI ----

        /// <summary>Runs after the game's own OnTick: chase and hit the player.</summary>
        private static object OnTick(object gm, object[] a)
        {
            _time = (float)a[1];
            try { RunAi(gm); RunPets(gm); }
            finally { if (AfterUpdate != null) AfterUpdate(_time); }
            return null;
        }

        /// <summary>
        /// A zombie in a spawn pose (sitting, lying, ...) can't move until its spawn state
        /// is 0 (update entity and change direction check it), and only the
        /// server clears it. Clearing it makes the NPC play its get-up animation and set
        /// Npc.RiseTime; it may act once that has passed. False while getting up.
        /// </summary>
        private static bool WakeUp(object npc)
        {
            float until;
            if (WakeUntil.TryGetValue(npc, out until)) return _time >= until;
            PropertyInfo state = npc.GetType().GetProperty(R.Name("Npc.PoseNow"), All);
            if (state == null || Convert.ToInt32(state.GetValue(npc, null)) == 0) { WakeUntil[npc] = 0f; return true; }
            object from = state.GetValue(npc, null);
            state.SetValue(npc, Enum.ToObject(state.PropertyType, 0), null);
            FieldInfo d = npc.GetType().GetField(R.Name("Npc.RiseTime"), All);
            float duration = d == null ? 0f : (float)d.GetValue(npc);
            WakeUntil[npc] = _time + duration;
            Log("AI: " + Describe(npc) + " wakes from spawn state " + from + ", getting up for " + duration + " s");
            return duration <= 0f;
        }

        /// <summary>
        /// Moves an NPC toward a target entity the way the client's tutorial arrow finds its
        /// way: if the collision tree says the straight line is blocked
        /// (CollisionTree.LineBlocked with flags 37), ask the navmesh pathfinder (PathFinder.Find, 2000
        /// iterations) and head for the edge midpoint about 100 units along the path; else
        /// go straight. The NPC's own goal-position mode (Npc.UseGoalPosition, goal position) does the walking.
        /// Recomputed every NavRecompute s.
        /// </summary>
        public static void Steer(object npc, object target)
        {
            Type t = npc.GetType();
            t.GetProperty(R.Name("Npc.Target"), All).SetValue(npc, target, null);
            float next;
            if (!NavNext.TryGetValue(npc, out next) || _time >= next)
            {
                NavNext[npc] = _time + NavRecompute;
                float[] here = Position(npc), last;
                float shortUntil;
                // Stuck (moved under 2 units since the last recompute while heading for a waypoint):
                // aim at the next edge on the path for 2 s instead of the one ~100 units ahead.
                if (NavWaypoint.ContainsKey(npc) && NavWaypoint[npc] != null && NavLastPos.TryGetValue(npc, out last) &&
                    Math.Abs(here[0] - last[0]) + Math.Abs(here[1] - last[1]) < 2f)
                    NavShortUntil[npc] = _time + 2f;
                NavLastPos[npc] = here;
                bool shortLook = NavShortUntil.TryGetValue(npc, out shortUntil) && _time < shortUntil;
                float[] wp = null;
                try { wp = Waypoint(here, Position(target), shortLook ? 0f : 100f); }
                catch (Exception e) { if (!_pathLogged) { _pathLogged = true; Log("path: failed: " + GameRuntime.Unwrap(e).Message); } }
                NavWaypoint[npc] = wp;
            }
            float[] w = NavWaypoint[npc];
            if (w == null)
            {
                SetField(npc, R.Name("Npc.UseGoalPosition"), false);
                t.GetProperty(R.Name("Npc.UseTarget"), All).SetValue(npc, true, null);
            }
            else
            {
                t.GetProperty(R.Name("Npc.UseTarget"), All).SetValue(npc, false, null);
                SetField(npc, R.Name("Npc.GoalPoint"), _g.Vector2(w[0], w[1]));
                SetField(npc, R.Name("Npc.UseGoalPosition"), true);
            }
        }

        /// <summary>
        /// Forgets everything the AI remembered about an object: NPCs come back out of the game's
        /// pool, and a reused zombie must not inherit "already awake" (its new spawn pose would
        /// never be cleared and it could not move), its old path or its attack timing.
        /// </summary>
        public static void ForgetNpc(object npc)
        {
            WakeUntil.Remove(npc);
            NavNext.Remove(npc);
            NavWaypoint.Remove(npc);
            NavLastPos.Remove(npc);
            NavShortUntil.Remove(npc);
            LastAttack.Remove(npc);
            SpecialMoves.Remove(npc);
            LastSpecial.Remove(npc);
            SpecialBusyUntil.Remove(npc);
            StaggeredAt.Remove(npc);
            AggroRange.Remove(npc);
            StrikeRange.Remove(npc);
            StrikeInterval.Remove(npc);
            HoldPosition.Remove(npc);
            Passive.Remove(npc);
            PetAttack.Remove(npc);
            ActivePets.Remove(npc);
        }

        /// <summary>Stops an NPC that is steering (no goal target, no goal position).</summary>
        public static void StopSteering(object npc)
        {
            npc.GetType().GetProperty(R.Name("Npc.UseTarget"), All).SetValue(npc, false, null);
            npc.GetType().GetProperty(R.Name("Npc.Target"), All).SetValue(npc, null, null);
            SetField(npc, R.Name("Npc.UseGoalPosition"), false);
            NavWaypoint.Remove(npc);
            NavNext.Remove(npc);
        }

        /// <summary>The next waypoint from one point toward another, or null when the straight line is clear (or no path).</summary>
        public static float[] Waypoint(float[] from, float[] to) { return Waypoint(from, to, 100f); }

        public static float[] Waypoint(float[] from, float[] to, float lookahead) { return Waypoint(from, to, lookahead, 2000); }

        /// <summary>True when the collision tree says the straight line between two points is clear (same check as Waypoint).</summary>
        public static bool LineClear(float[] from, float[] to)
        {
            object gm = _g.World;
            object collision = _g.WorldType.GetField(R.Name("World.Collision"), All).GetValue(gm);
            object tree = collision.GetType().GetField(R.Name("Collision.Tree"), All).GetValue(collision);
            if (_lineCheck == null) _lineCheck = tree.GetType().GetMethods(All).First(m => m.Name == R.Name("CollisionTree.LineBlocked") && m.GetParameters().Length == 6);
            object a = _g.Vector2(from[0], from[1]), b = _g.Vector2(to[0], to[1]);
            return !(bool)_lineCheck.Invoke(tree, new[] { a, b, Enum.ToObject(_lineCheck.GetParameters()[2].ParameterType, 37), (object)false, true, false });
        }

        private static MethodInfo _lineCheck;

        /// <summary>
        /// The whole navmesh path from one point to another as edge midpoints, nearest first. Empty when the straight line is
        /// clear, null when no path was found within the iteration budget.
        /// </summary>
        public static List<float[]> Path(float[] from, float[] to, int iterations, bool force = false)
        {
            object gm = _g.World;
            object collision = _g.WorldType.GetField(R.Name("World.Collision"), All).GetValue(gm);
            object tree = collision.GetType().GetField(R.Name("Collision.Tree"), All).GetValue(collision);
            MethodInfo blockedM = tree.GetType().GetMethods(All).First(m => m.Name == R.Name("CollisionTree.LineBlocked") && m.GetParameters().Length == 6);
            object a = _g.Vector2(from[0], from[1]), b = _g.Vector2(to[0], to[1]);
            bool blocked = (bool)blockedM.Invoke(tree, new[] { a, b, Enum.ToObject(blockedM.GetParameters()[2].ParameterType, 37), (object)false, true, false });
            if (!blocked && !force) return new List<float[]>();
            object nav = _g.WorldType.GetProperty("NavMesh", All).GetValue(gm, null);
            Type finder = R.Type("PathFinder");
            MethodInfo find = finder.GetMethods(All).First(m => m.Name == R.Name("PathFinder.Find") && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == a.GetType());
            object node = find.Invoke(null, new[] { a, b, nav, null, (object)iterations });
            if (node == null) return null;
            PropertyInfo prev = node.GetType().GetProperty("Previous", All);
            PropertyInfo edge = node.GetType().GetProperty("Edge", All);
            var points = new List<float[]>();
            for (object n = node; n != null && points.Count < 400; n = prev.GetValue(n, null))
            {
                object e = edge.GetValue(n, null);
                if (e == null) continue;
                float[] mid = Vector(e.GetType().GetField(R.Name("NavEdge.Mid"), All).GetValue(e));
                if (mid[0] != 0 || mid[1] != 0) points.Add(mid);
            }
            // The returned node's chain runs from the start end toward the goal (probe: first midpoints near
            // the bot), which is the order the walker wants.
            return points;
        }

        /// <summary>As Waypoint, with the pathfinder's iteration budget (bots crossing a whole Scavenger map need more).</summary>
        public static float[] Waypoint(float[] from, float[] to, float lookahead, int iterations)
        {
            object gm = _g.World;
            object collision = _g.WorldType.GetField(R.Name("World.Collision"), All).GetValue(gm);
            object tree = collision.GetType().GetField(R.Name("Collision.Tree"), All).GetValue(collision);
            MethodInfo blockedM = tree.GetType().GetMethods(All).First(m => m.Name == R.Name("CollisionTree.LineBlocked") && m.GetParameters().Length == 6);
            object a = _g.Vector2(from[0], from[1]), b = _g.Vector2(to[0], to[1]);
            bool blocked = (bool)blockedM.Invoke(tree, new[] { a, b, Enum.ToObject(blockedM.GetParameters()[2].ParameterType, 37), (object)false, true, false });
            if (!blocked) return null;
            object nav = _g.WorldType.GetProperty("NavMesh", All).GetValue(gm, null);
            Type finder = R.Type("PathFinder");
            MethodInfo find = finder.GetMethods(All).First(m => m.Name == R.Name("PathFinder.Find") && m.GetParameters().Length == 5 && m.GetParameters()[0].ParameterType == a.GetType());
            object node = find.Invoke(null, new[] { a, b, nav, null, (object)iterations });
            if (node == null) return null;
            PropertyInfo prev = node.GetType().GetProperty("Previous", All);
            FieldInfo dist = node.GetType().GetField(R.Name("PathNode.Remaining"), All);
            PropertyInfo edge = node.GetType().GetProperty("Edge", All);
            if (!_pathLogged)
            {
                _pathLogged = true;
                var chain = new List<string>();
                for (object n = node; n != null && chain.Count < 40; n = prev.GetValue(n, null))
                {
                    float[] mid = Vector(edge.GetValue(n, null).GetType().GetField(R.Name("NavEdge.Mid"), All).GetValue(edge.GetValue(n, null)));
                    chain.Add(((float)dist.GetValue(n)).ToString("0") + "@(" + mid[0].ToString("0") + "," + mid[1].ToString("0") + ")");
                }
                Log("path: from (" + from[0].ToString("0") + "," + from[1].ToString("0") + ") to (" + to[0].ToString("0") + "," + to[1].ToString("0") + "), chain (remaining@edge midpoint, returned node first): " + string.Join(" ", chain.ToArray()));
            }
            object first = prev.GetValue(node, null);
            if (first == null || (float)dist.GetValue(first) <= 50f) return null;
            float firstDist = (float)dist.GetValue(first);
            object pick = first;
            while (pick != null && (float)dist.GetValue(pick) > firstDist - lookahead) pick = prev.GetValue(pick, null);
            if (pick == null) return null;
            float[] m2 = Vector(edge.GetValue(pick, null).GetType().GetField(R.Name("NavEdge.Mid"), All).GetValue(edge.GetValue(pick, null)));
            if (m2[0] == 0 && m2[1] == 0) return null;
            return m2;
        }

        private static void RunAi(object gm)
        {
            object player = _g.GetPlayer(0);
            if (player == null) return;
            // With a ChooseTarget (Scavenger: the nearest living hero), zombies keep fighting the bots while the
            // human is dead; without one they only ever chase the human.
            bool playerUp = (bool)Prop(player, "IsActive") && !(bool)Prop(player, R.Name("Entity.Dead"));
            if (!playerUp && ChooseTarget == null) return;
            foreach (object npc in (Array)_g.WorldType.GetField(R.Name("World.Npcs"), All).GetValue(gm))
            {
                if (npc == null || !(bool)Prop(npc, "IsActive") || (bool)Prop(npc, R.Name("Entity.Dead")) || Passive.Contains(npc)) continue;
                if (Prop(npc, R.Name("Entity.Team")).Equals(Prop(player, R.Name("Entity.Team")))) continue;   // allies (the companion, truckers) have their own AI
                PropertyInfo petOwner = npc.GetType().GetProperty(R.Name("Fighter.Master"), All);
                if (petOwner != null && petOwner.GetValue(npc, null) != null) continue;   // pets: RunPets
                object target = ChooseTarget != null ? ChooseTarget(npc, player)
                              : NearestTarget(npc, playerUp ? new[] { player } : new object[0]);
                if (target == null && ChooseTarget != null && playerUp) target = player;
                if (target == null) continue;
                float[] p = Position(target);
                float[] n = Position(npc);
                float dx = p[0] - n[0], dy = p[1] - n[1], dist = (float)Math.Sqrt(dx * dx + dy * dy);
                float aggro;
                if (!AggroRange.TryGetValue(npc, out aggro)) aggro = DefaultAggroRange;
                if (dist > aggro) continue;
                if (!WakeUp(npc)) continue;
                // While a special move plays out (a charge, the hook, a spit) the game moves and turns the NPC itself.
                float busy;
                if (SpecialBusyUntil.TryGetValue(npc, out busy) && _time < busy) continue;
                if (!HoldPosition.Contains(npc)) Steer(npc, target);
                // Turn toward the player; the character update eases Fighter.Aim toward this.
                object dir = _g.Vector2(dx / Math.Max(dist, 0.001f), dy / Math.Max(dist, 0.001f));
                SetField(npc, R.Name("Fighter.AimGoal"), dir);
                if (TrySpecialMove(npc, target, dir, dist)) continue;
                float last;
                float reach;
                if (!StrikeRange.TryGetValue(npc, out reach)) reach = AttackRange;
                float interval;
                if (!StrikeInterval.TryGetValue(npc, out interval)) interval = AttackInterval;
                if (dist > reach || (LastAttack.TryGetValue(npc, out last) && _time - last < interval)) continue;
                object ability;
                if (!StrikeAbility.TryGetValue(npc, out ability)) continue;
                float[] aim = Vector(Prop(npc, R.Name("Fighter.Aim")));
                double aimDegrees = Math.Abs(Math.Atan2(aim[0] * dy / dist - aim[1] * dx / dist, aim[0] * dx / dist + aim[1] * dy / dist)) * 180.0 / Math.PI;
                if (aimDegrees > AttackAimDegrees) continue;
                LastAttack[npc] = _time;
                bool attacked = (bool)npc.GetType().GetMethod(R.Name("Fighter.Strike"), All, null, new[] { dir.GetType(), ability.GetType() }, null).Invoke(npc, new[] { dir, ability });
                Log("AI: " + Describe(npc) + " attacks " + Describe(target) + " at " + dist.ToString("0") + " units with " + ability + (attacked ? "" : " (refused)"));
            }
        }

        // ---- special zombies' own moves (issue #4) and their stagger guard (issue #5) ----

        /// <summary>
        /// The special moves a zombie may use, by role, with the distances (units) it uses them at. The game keeps
        /// each move's cooldown and "can use" rule; the original server's choice of when to use which is lost, so
        /// these distances are stand-ins (ours, 2026-10-10). Walkers, looters and veterans only have strike
        /// variants and keep the basic attack.
        /// </summary>
        private static readonly string[][] SpecialMoveTable =
        {
            new[] { "Special.BigCharge", "40", "140" },
            new[] { "Special.CarrierCharge", "40", "140" },
            new[] { "Special.Ram", "40", "140" },
            new[] { "Special.FloatDash", "40", "140" },
            new[] { "Special.Crawl", "40", "140" },
            new[] { "Ability.PullerGrab", "30", "120" },
            new[] { "Special.Spit", "30", "160" },
            new[] { "Special.CarrierSpit", "30", "160" },
            new[] { "Special.CarrierBomb", "30", "160" },
            new[] { "Special.BigRavage", "0", "25" },
            new[] { "Special.Stomp", "0", "25" },
            new[] { "Special.Fear", "0", "40" },
            new[] { "Special.Summon", "0", "200" },
        };

        /// <summary>Seconds between two special moves of one zombie, and how long the AI leaves it alone after one (ours).</summary>
        public const float SpecialSpacing = 3f, SpecialBusySeconds = 1.5f;

        /// <summary>A special zombie can be staggered by heroes' basic attacks at most once in this many seconds (ours, issue #5).</summary>
        public const float SpecialStaggerImmunity = 2f;

        /// <summary>Counts for probes: basic-attack staggers on specials let through and dropped by the guard.</summary>
        public static int StaggersAllowed, StaggersDropped;

        private sealed class SpecialMove { public object Ability, Key; public float Min, Max; }
        private static readonly Dictionary<object, List<SpecialMove>> SpecialMoves = new Dictionary<object, List<SpecialMove>>();
        private static readonly Dictionary<object, float> LastSpecial = new Dictionary<object, float>();
        private static readonly Dictionary<object, float> SpecialBusyUntil = new Dictionary<object, float>();
        private static readonly Dictionary<object, float> StaggeredAt = new Dictionary<object, float>();
        private static readonly HashSet<string> _specialLogged = new HashSet<string>();

        /// <summary>The NPC's special moves from its ability bar (cached; empty for walkers and the like).</summary>
        private static List<SpecialMove> MovesOf(object npc)
        {
            List<SpecialMove> moves;
            if (SpecialMoves.TryGetValue(npc, out moves)) return moves;
            moves = new List<SpecialMove>();
            object bar = Prop(npc, R.Name("Fighter.Bar"));
            Array slots = bar == null ? null : (Array)Prop(bar, R.Name("Bar.Slots"));
            if (slots != null)
                foreach (object ability in slots)
                {
                    if (ability == null) continue;
                    object key = Prop(ability, R.Name("Ability.Key"));
                    foreach (string[] row in SpecialMoveTable)
                        if (key.ToString() == R.Name(row[0]))
                            moves.Add(new SpecialMove { Ability = ability, Key = key, Min = float.Parse(row[1]), Max = float.Parse(row[2]) });
                }
            SpecialMoves[npc] = moves;
            return moves;
        }

        /// <summary>Uses one special move if one is ready and the target is at its distance; true if one started.</summary>
        private static bool TrySpecialMove(object npc, object target, object dir, float dist)
        {
            List<SpecialMove> moves = MovesOf(npc);
            if (moves.Count == 0) return false;
            float last;
            if (LastSpecial.TryGetValue(npc, out last) && _time - last < SpecialSpacing) return false;
            foreach (SpecialMove m in moves)
            {
                if (dist < m.Min || dist > m.Max) continue;
                MethodInfo ready = m.Ability.GetType().GetMethod(R.Name("Ability.Ready"), All, null, new[] { typeof(bool) }, null);
                if (ready != null && !(bool)ready.Invoke(m.Ability, new object[] { false })) continue;
                // Moves go where the NPC faces (a charge launched while it still faced away ran off: specials test,
                // 2026-10-10), so face the target first.
                float[] d = Vector(dir);
                _g.SetAim(npc, d[0], d[1]);
                bool started = (bool)npc.GetType().GetMethod(R.Name("Fighter.Strike"), All, null, new[] { dir.GetType(), m.Key.GetType() }, null).Invoke(npc, new[] { dir, m.Key });
                if (!started) continue;
                LastSpecial[npc] = _time;
                LastAttack[npc] = _time;
                SpecialBusyUntil[npc] = _time + SpecialBusySeconds;
                if (_specialLogged.Add(npc.GetType().Name + " " + m.Key))
                    Log("AI: " + Describe(npc) + " uses " + m.Key + " on " + Describe(target) + " at " + dist.ToString("0") + " units (first use of this move by this kind)");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Issue #5: heroes' basic attacks staggered special zombies on every hit, so they could be stun-locked. A special
        /// (a zombie with special moves) now takes at most one basic-attack stagger every SpecialStaggerImmunity seconds;
        /// the damage always lands, and abilities meant to stun are not affected. True when this stagger is dropped.
        /// </summary>
        private static bool GuardStagger(object target)
        {
            if (target == null || !R.Type("Type.Fighter").IsInstanceOfType(target) || MovesOf(target).Count == 0) return false;
            if (BasicAttackHitOnStack() == null) return false;
            float last;
            if (StaggeredAt.TryGetValue(target, out last))
            {
                if (_time == last) return false;   // the same hit's buff and stat effect
                if (_time - last < SpecialStaggerImmunity) { StaggersDropped++; return true; }
            }
            StaggeredAt[target] = _time;
            StaggersAllowed++;
            return false;
        }

        /// <summary>Pets this close to their owner stop following; enemies this close to the pet (or to its owner) are fought.</summary>
        public const float PetFollowDistance = 45f, PetEngageRange = 90f, PetLeashRange = 200f;
        private static readonly Dictionary<object, object> PetAttack = new Dictionary<object, object>();
        private static int _petsLogged;

        /// <summary>
        /// Pets (ours, a stand-in: the original server's pet AI is not in the client). An NPC with a Fighter.Master (a hero's
        /// summoned minion, ...) only plays effects in its own update NPC; movement and attacks were the server's.
        /// A pet fights the target its owner gave it (World.TargetSlot) or the nearest enemy within
        /// PetEngageRange of it or of its owner, never straying past PetLeashRange from the owner; otherwise it
        /// follows the owner and stops within PetFollowDistance. Owner's 2026-10-07 playtest: the minion never moved.
        /// </summary>
        /// <summary>Hero-owned pets alive this tick (filled by RunPets); zombies may target them too.</summary>
        public static readonly List<object> ActivePets = new List<object>();

        /// <summary>
        /// The nearest of the candidates and the living pets the NPC counts as enemies (Entity.Hostile).
        /// Pets drew no aggro before (owner, 2026-10-07: "the minion is not being targeted by any enemies").
        /// </summary>
        public static object NearestTarget(object npc, IEnumerable<object> candidates)
        {
            float[] at = Position(npc);
            object best = null;
            float bestDist = float.MaxValue;
            MethodInfo isEnemy = R.Type("Type.WorldObject").GetMethod(R.Name("Entity.Hostile"), All);
            foreach (object c in candidates.Concat(ActivePets.Where(p => (bool)isEnemy.Invoke(npc, new[] { p }))))
            {
                if (c == null || !(bool)Prop(c, "IsActive") || (bool)Prop(c, R.Name("Entity.Dead"))) continue;
                float d = Dist(Position(c), at);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        private static void RunPets(object gm)
        {
            Array npcs = (Array)_g.WorldType.GetField(R.Name("World.Npcs"), All).GetValue(gm);
            ActivePets.Clear();
            IList heroes = (IList)_g.WorldType.GetProperty(R.Name("World.Heroes"), All).GetValue(gm, null);
            MethodInfo isEnemy = null;
            foreach (object pet in npcs)
            {
                if (pet == null || !(bool)Prop(pet, "IsActive") || (bool)Prop(pet, R.Name("Entity.Dead"))) continue;
                PropertyInfo ownerProp = pet.GetType().GetProperty(R.Name("Fighter.Master"), All);
                object owner = ownerProp == null ? null : ownerProp.GetValue(pet, null);
                if (owner == null) continue;
                ActivePets.Add(pet);
                if (!(bool)Prop(owner, "IsActive") || (bool)Prop(owner, R.Name("Entity.Dead"))) { StopSteering(pet); continue; }
                if (isEnemy == null) isEnemy = R.Type("Type.WorldObject").GetMethod(R.Name("Entity.Hostile"), All);
                WakeUp(pet);
                float[] here = Position(pet), home = Position(owner);
                // The owner's order first, then the nearest enemy near the pet or the owner.
                object target = null;
                FieldInfo order = pet.GetType().GetField(R.Name("World.TargetSlot"), All);
                if (order != null && (int)order.GetValue(pet) >= 0)
                {
                    object o = _g.WorldType.GetMethod(R.Name("World.Object"), All, null, new[] { typeof(int) }, null).Invoke(gm, new object[] { (int)order.GetValue(pet) });
                    if (o != null && (bool)Prop(o, R.Name("Entity.IsLiving")) && (bool)Prop(o, "IsActive") && !(bool)Prop(o, R.Name("Entity.Dead"))) target = o;
                }
                if (target == null)
                {
                    float best = float.MaxValue;
                    foreach (object c in npcs.Cast<object>().Concat(heroes.Cast<object>()))
                    {
                        if (c == null || c == pet || c == owner || !(bool)Prop(c, "IsActive") || (bool)Prop(c, R.Name("Entity.Dead"))) continue;
                        if (!(bool)isEnemy.Invoke(pet, new[] { c })) continue;
                        float[] cp = Position(c);
                        float dPet = Dist(cp, here), dOwner = Dist(cp, home);
                        if ((dPet > PetEngageRange && dOwner > PetEngageRange) || dOwner > PetLeashRange) continue;
                        if (dPet < best) { best = dPet; target = c; }
                    }
                }
                if (target == null)
                {
                    if (Dist(here, home) > PetFollowDistance) Steer(pet, owner); else StopSteering(pet);
                    continue;
                }
                Steer(pet, target);
                float[] tp = Position(target);
                float dx = tp[0] - here[0], dy = tp[1] - here[1], dist = (float)Math.Sqrt(dx * dx + dy * dy);
                object dir = _g.Vector2(dx / Math.Max(dist, 0.001f), dy / Math.Max(dist, 0.001f));
                SetField(pet, R.Name("Fighter.AimGoal"), dir);
                object ability;
                if (!PetAttack.TryGetValue(pet, out ability))
                {
                    object bar = pet.GetType().GetProperty(R.Name("Fighter.Bar"), All).GetValue(pet, null);
                    Array slots = bar == null ? null : (Array)bar.GetType().GetProperty(R.Name("Bar.Slots"), All).GetValue(bar, null);
                    if (slots != null) foreach (object a in slots) if (a != null) { ability = a.GetType().GetProperty(R.Name("Ability.Key"), All).GetValue(a, null); break; }
                    PetAttack[pet] = ability;
                    if (_petsLogged++ < 5) Log("AI: pet " + Describe(pet) + " of " + Describe(owner) + " attacks with " + (ability ?? "(no ability)"));
                }
                float last;
                if (ability == null || dist > AttackRange || (LastAttack.TryGetValue(pet, out last) && _time - last < AttackInterval)) continue;
                LastAttack[pet] = _time;
                pet.GetType().GetMethod(R.Name("Fighter.Strike"), All, null, new[] { dir.GetType(), ability.GetType() }, null).Invoke(pet, new[] { dir, ability });
            }
        }

        private static float Dist(float[] a, float[] b) { float dx = a[0] - b[0], dy = a[1] - b[1]; return (float)Math.Sqrt(dx * dx + dy * dy); }

        // ---- helpers ----

        public static float GetStat(object entity, object statType)
        {
            object stats = Field(entity, R.Name("Entity.StatBlock"));
            return (float)Method(stats, "GetValue").Invoke(stats, new[] { statType });
        }

        public static float GetStat(object entity, string statName)
        {
            return GetStat(entity, Enum.Parse(R.Type("Type.Stat"), statName));
        }

        public static float[] Position(object entity) { return Vector(Field(entity, "Position")); }

        public static float[] Vector(object v)
        {
            return new[] { (float)v.GetType().GetField("X").GetValue(v), (float)v.GetType().GetField("Y").GetValue(v) };
        }

        public static object GetFieldValue(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            throw new MissingFieldException(o.GetType().FullName, name);
        }

        public static void SetField(object o, string name, object value)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f == null) continue;
                f.SetValue(o, value);
                return;
            }
            throw new MissingFieldException(o.GetType().FullName, name);
        }

        public static string Describe(object o)
        {
            if (o == null) return "(none)";
            try { return o.GetType().Name + "#" + Prop(o, R.Name("Entity.GlobalIndex")); }
            catch (Exception) { return o.GetType().Name; }
        }

        private static readonly Dictionary<string, MemberInfo> Cache = new Dictionary<string, MemberInfo>();

        private static MethodInfo Method(object o, string name, Type[] types = null)
        {
            string key = o.GetType().FullName + "::" + name + (types == null ? "" : "/" + types.Length);
            MemberInfo m;
            if (!Cache.TryGetValue(key, out m))
            {
                for (Type t = o.GetType(); t != null && m == null; t = t.BaseType)
                    m = types == null
                        ? t.GetMethods(All | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name)
                        : t.GetMethod(name, All | BindingFlags.DeclaredOnly, null, types, null);
                if (m == null) throw new MissingMethodException(o.GetType().FullName, name);
                Cache[key] = m;
            }
            return (MethodInfo)m;
        }

        private static object Field(object o, string name)
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            throw new MissingFieldException(o.GetType().FullName, name);
        }

        private static object Prop(object o, string name)
        {
            return o.GetType().GetProperty(name, All).GetValue(o, null);
        }

        private static void Set(object boxedStruct, string field, object value)
        {
            boxedStruct.GetType().GetField(field, All).SetValue(boxedStruct, value);
        }
    }
}

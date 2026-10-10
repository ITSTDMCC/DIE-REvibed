using System;
using EpidemicServer.Resolve;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EpidemicServer.Protocol;

namespace EpidemicServer.Match
{
    /// <summary>
    /// Owns the game logic runtime and the one thread every call into it runs
    /// on. Started with --game=&lt;install&gt; under 32-bit Mono.
    /// </summary>
    public sealed class MatchHost
    {
        /// <summary>Camera direction from the match client's own connect hail (Net.Connection).</summary>
        public const float DefaultCameraX = -67.175f, DefaultCameraY = -67.175f;

        private readonly string _install;
        private readonly Account _account;
        private readonly AccountStore _store;
        private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);

        public GameRuntime Game;
        public object Player;
        public object ClientInfoObject;
        public SpawnPoint Spawn;
        public TutorialDirector Tutorial;
        public ScoutDirector Scout;
        public HordeDirector Horde;
        public ScavengerDirector Scavenger;
        public Leveling Leveling;
        /// <summary>The queue type and map the hub asked matchmaking for (SoloServerCreate); the next match client gets that match.</summary>
        private int _wantedQueue = 5, _wantedMap = GameRuntime.TutorialMap;
        public bool Failed;

        public MatchHost(string install, Account account, AccountStore store)
        {
            _install = install;
            _account = account;
            _store = store;
        }

        public void Start()
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Name = "game";
            t.Start();
        }

        /// <summary>Waits until start-up has finished (or failed).</summary>
        public bool WaitReady(int timeoutMs) { return _ready.WaitOne(timeoutMs) && !Failed; }

        /// <summary>Runs an action on the game thread.</summary>
        public void Post(Action action) { _work.Add(action); }

        private void Run()
        {
            try
            {
                StartGame();
            }
            catch (Exception e)
            {
                Failed = true;
                Log.Error("match: game logic failed to start: " + GameRuntime.Unwrap(e));
            }
            _ready.Set();
            // Run posted work as it arrives and tick the session at 30 Hz.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            double tickMs = MatchSession.TickSeconds * 1000.0, nextTick = tickMs;
            while (true)
            {
                int wait = (int)Math.Max(0, nextTick - clock.Elapsed.TotalMilliseconds);
                Action action;
                if (_work.TryTake(out action, wait))
                {
                    try { action(); }
                    catch (Exception e) { Log.Error("match: game thread: " + GameRuntime.Unwrap(e)); }
                    continue;
                }
                nextTick += tickMs;
                if (nextTick < clock.Elapsed.TotalMilliseconds) nextTick = clock.Elapsed.TotalMilliseconds + tickMs;
                if (Failed) continue;
                try { Game.FlushGameLog(); } catch (Exception) { }
                if (_session == null) continue;
                try { _session.Tick(); }
                catch (Exception e)
                {
                    Log.Error("match: tick failed, stopping the session: " + GameRuntime.Unwrap(e));
                    _session = null;
                }
            }
        }

        private MatchSession _session;

        /// <summary>Called by matchmaking for each SoloServerCreate: practice is queue 6 (resistance easy) on map 15/16/17.</summary>
        public void RequestMatch(int queueType, int mapIndex)
        {
            Post(() =>
            {
                _wantedQueue = queueType;
                _wantedMap = mapIndex;
                Log.Info("match: the hub asked for queue type " + queueType + ", map " + mapIndex);
            });
        }

        private Weapon ToWeapon(OwnedUnique u)
        {
            return new Weapon { Guid = u.Guid, SchematicId = u.SchematicId, UserId = _account.UserId, Durability = u.Durability, Ep = u.Ep };
        }

        /// <summary>
        /// The character and infection level for the next match. The tutorial is the player at infection 1, as the
        /// original. Otherwise the character the hub queued with, and the infection level from that loadout's
        /// strength (GameRuntime.InfectionLevelFor; owner's choice 2026-10-07, matching the live game after v0.6).
        /// </summary>
        private void ChooseCharacterAndInfection(bool tutorial)
        {
            MatchLoadout l = MatchLoadout.Current;
            Type characterEnum = Game.WorldType.GetMethod(R.Name("World.Cache"), GameRuntime.All).GetParameters()[1].ParameterType.GetGenericArguments()[0];
            string character = Enum.GetName(characterEnum, GameRuntime.DefaultHero);
            if (!tutorial && l != null && Enum.IsDefined(characterEnum, (int)l.Character)) character = Enum.GetName(characterEnum, (int)l.Character);
            Game.Character = character;
            Game.MatchLevel = 1;
            if (tutorial) return;
            try
            {
                int level, strength;
                lock (_account) level = Leveling.AccountLevel(Game, AccountView.EffectiveXp(_account));
                ushort melee = l != null && l.Melee != null ? l.Melee.SchematicId : Protocol.Inventory.DefaultMeleeSchematic;
                ushort ranged = l != null && l.Ranged != null ? l.Ranged.SchematicId : Protocol.Inventory.DefaultRangedSchematic;
                Game.MatchLevel = Math.Max(1, Game.InfectionLevelFor(character, level, melee, ranged, l == null ? null : l.Gadgets, _account.UserId, out strength));
                Log.Info("match: " + character + " at account level " + level + " with " + melee + "/" + ranged + ": strength " + strength + " -> infection level " + Game.MatchLevel);
            }
            catch (Exception e) { Log.Warn("match: infection level from strength failed, using 1: " + GameRuntime.Unwrap(e).Message); }
        }

        /// <summary>
        /// Scavenger's 11 bot heroes (3 teammates, 4 per rival team): random characters the hub lists with a model
        /// (UnlockCatalog), never the human's. They are cached with the human's (MatchInfo.HeroCache). Other modes
        /// cache only the human's hero.
        /// </summary>
        private void ChooseBotHeroes(bool scavenger)
        {
            Game.ExtraCharacters.Clear();
            if (!scavenger) return;
            Type characterEnum = Game.WorldType.GetMethod(R.Name("World.Cache"), GameRuntime.All).GetParameters()[1].ParameterType.GetGenericArguments()[0];
            var pool = (UnlockCatalog.Ready && UnlockCatalog.Characters.Length > 0 ? UnlockCatalog.Characters.Select(id => (int)id) : Enumerable.Range(1, 13))
                .Where(id => Enum.IsDefined(characterEnum, id)).Select(id => Enum.GetName(characterEnum, id)).Where(n => n != Game.Character).ToList();
            Random r = new Random();
            Game.ExtraCharacters.AddRange(pool.OrderBy(x => r.Next()).Take(ScavengerBots));
            Log.Info("match: Scavenger bot heroes: " + string.Join(", ", Game.ExtraCharacters.ToArray()));
        }

        public const int ScavengerBots = 11;

        private Weapon AccountWeapon(ushort schematic)
        {
            Protocol.OwnedUnique u = _account.Uniques.Find(x => x.SchematicId == schematic);
            if (u == null) return null;
            return new Weapon { Guid = u.Guid, SchematicId = u.SchematicId, UserId = _account.UserId, Durability = u.Durability, Ep = u.Ep };
        }

        /// <summary>The end of a match: rewards message, then the same reward applied to the account file.</summary>
        private void EndMatch(int[] boxes, int placement = 0)
        {
            try
            {
                int team = Scout != null ? Scout.SuppliesBanked() : Horde != null ? Horde.Supplies : Scavenger != null ? Scavenger.DeliveredTeam1() : 0;
                int xp = Leveling.EndMatch(_wantedQueue, Game.MapIndex, _account, boxes, team, placement);
                if (xp < 0) return;
                lock (_account)
                {
                    string line = Leveling.ApplyToAccount(Game, _account, xp);
                    _store.Save(_account);
                    Log.Info("match: account updated: " + line);
                }
            }
            catch (Exception e) { Log.Error("match: end-of-match rewards failed: " + GameRuntime.Unwrap(e)); }
        }

        /// <summary>Stage 24: marks the tutorial completed on the local account and saves it.</summary>
        private void RecordTutorialCompleted()
        {
            try
            {
                lock (_account)
                {
                    bool already = _account.TutorialCompleted;
                    _account.TutorialCompleted = true;
                    if (!already) _store.Save(_account);
                    Log.Info("match: tutorial completed" + (already ? " (already recorded on the account)" : "; recorded on the account in " + _store.Path));
                }
            }
            catch (Exception e) { Log.Error("match: could not record tutorial completion: " + e.Message); }
        }

        /// <summary>A match client finished the gameplay auth; frames go out through send.</summary>
        public void Attach(Action<byte[]> send)
        {
            Post(() =>
            {
                _session = new MatchSession(Game, Player, send);
                _matchUsed = true;
                Log.Info("match: client attached; ticking at 30 Hz");
            });
        }

        /// <summary>A MatchFrame frame from the attached client.</summary>
        public void OnClientFrame(byte[] frame)
        {
            Post(() => { if (_session != null) _session.OnClientFrame(frame); });
        }

        public void Detach()
        {
            Post(() =>
            {
                if (_session == null) return;
                Log.Info("match: client detached after " + _session.FramesIn + " frames in, " + _session.FramesOut + " out, " +
                         _session.MessagesIn + " reliable messages");
                _session = null;
            });
        }

        private void StartGame()
        {
            Log.Info("match: loading the match client's game logic from " + _install);
            Game = GameRuntime.Load(_install);
            // Heroic Horde unlock (see RequestServerEncoders.HeroicUnlockXp).
            RequestServerEncoders.HeroicUnlockXp = (uint)R.Type("Type.Levels").GetMethod(R.Name("Levels.XpFor"), GameRuntime.All)
                .Invoke(null, new object[] { RequestServerEncoders.HeroicUnlockLevel });
            RequestServerEncoders.FeatureAccount = _account;
            Log.Info("match: Heroic Horde unlocks at account level " + RequestServerEncoders.HeroicUnlockLevel + " (story map XP " + RequestServerEncoders.HeroicUnlockXp +
                     "; account has " + _account.StoryMapXp + ")");
            GameBuffer.Init(Game);
            ServerHooks.Log = line => Log.Info("match: " + line);
            Game.CaptureGameLog(line => Log.Info("game log: " + line));
            BuildMatch();
            // The preservation switches' catalog (UnlockCatalog), from the crafting data BuildMatch loaded.
            try { Log.Info("match: unlock catalog: " + UnlockCatalogBuilder.Build(Game, _account.UserId)); }
            catch (Exception e) { Log.Error("match: building the unlock catalog failed: " + GameRuntime.Unwrap(e)); }
        }

        /// <summary>True once a client has played the current match; the next one gets a fresh match.</summary>
        private bool _matchUsed;

        /// <summary>Builds a fresh match: World, network base, tutorial map and player 0.</summary>
        private void BuildMatch()
        {
            Game.Start(line => Log.Info("match: " + line));
            Log.Info("match: SyncCount " + Game.SyncCount + " (the match client logged 896 for the tutorial)");
            DumpMapObjects();

            Log.Info("match: " + Game.SpawnPoints.Count + " spawn/teleport points in " + Game.MapName + ":");
            foreach (SpawnPoint s in Game.SpawnPoints) Log.Info("match:   " + s);
            Spawn = Game.PickSpawn();
            if (Spawn == null) throw new Exception("no MapKind.HeroStart or MapKind.CutsceneSpot in the map");
            Log.Info("match: spawning at " + Spawn);

            bool tutorial = Game.ModeKind == GameRuntime.TutorialGameModeType;
            int level;
            Weapon melee = null, ranged = null;
            List<OwnedGadget> gadgets = new List<OwnedGadget>();
            lock (_account)
            {
                level = Leveling.AccountLevel(Game, AccountView.EffectiveXp(_account));   // maxLevel switch: the top level
                if (!tutorial)
                {
                    // Outside the tutorial the client equips Weapon1/Weapon2 and the trinkets from the
                    // hail's ClientInfoObject (World.Created, also run here on the server):
                    // what the hub queued with (MatchLoadout), else the account's default melee and ranged weapons.
                    MatchLoadout l = MatchLoadout.Current;
                    melee = l != null && l.Melee != null ? ToWeapon(l.Melee) : AccountWeapon(Protocol.Inventory.DefaultMeleeSchematic);
                    ranged = l != null && l.Ranged != null ? ToWeapon(l.Ranged) : AccountWeapon(Protocol.Inventory.DefaultRangedSchematic);
                    if (l != null) gadgets.AddRange(l.Gadgets);
                }
            }
            // The tutorial starts the player with her fists, as the original game does (the
            // owner's known-good hail had no weapons, and the client shows fists), so
            // no weapons go into her match client info there.
            string team = tutorial ? GameRuntime.PlayerTeam : R.Name("Team.One");
            ClientInfoObject = Game.BuildClientInfoData(DefaultCameraX, DefaultCameraY, _account.Name, team, level, melee, ranged, gadgets, _account.UserId);
            if (_account.SteamId == 0) Log.Warn("match: no steamId in the account file; the hail will carry Steam id 0");
            Player = Game.PrepareLocalPlayer(Spawn, ClientInfoObject, _account.SteamId, _account.UserId, team, level);
            Log.Info("match: " + Game.Character + ", account level " + level + " (story map XP " + _account.StoryMapXp + "), infection level " + Game.MatchLevel + ", team " + team +
                     (gadgets.Count == 0 ? "" : ", gadgets " + string.Join(",", gadgets.Select(g => g.GadgetId.ToString()).ToArray())) +
                     (melee == null ? ", no weapons in the hail" : ", weapons " + melee.SchematicId + "/" + ranged.SchematicId + " in the hail") +
                     ", Weapon1 " + ServerHooks.Describe(ServerHooks.GetFieldValue(Player, R.Name("Fighter.WeaponA"))) + ", Weapon2 " + ServerHooks.Describe(ServerHooks.GetFieldValue(Player, R.Name("Fighter.WeaponB"))));
            Leveling = new Leveling(Game, Player, line => Log.Info("match: " + line));
            Tutorial = null;
            Scout = null;
            Horde = null;
            Scavenger = null;
            if (tutorial)
            {
                Tutorial = new TutorialDirector(Game, Player, Spawn, line => Log.Info("match: " + line));
                Tutorial.PopulateWorld();
                ServerHooks.AfterUpdate = Tutorial.Tick;
                Tutorial.OnCompleted = () => { RecordTutorialCompleted(); EndMatch(new[] { 5, 10 }); };
            }
            else if (Game.ModeKind == GameRuntime.ScavengerHuntGameModeType)
            {
                Scavenger = new ScavengerDirector(Game, Player, line => Log.Info("match: " + line));
                Scavenger.PopulateWorld(Game.ExtraCharacters, level, melee, ranged);
                ServerHooks.AfterUpdate = Scavenger.Tick;
                Scavenger.OnFinished = (placement, boxes) => EndMatch(boxes, placement);
            }
            else if (Game.ModeKind == GameRuntime.HordeGameModeType)
            {
                Horde = new HordeDirector(Game, Player, line => Log.Info("match: " + line));
                Horde.PopulateWorld();
                ServerHooks.AfterUpdate = Horde.Tick;
                Horde.OnFinished = (placement, boxes) => EndMatch(boxes, placement);
            }
            else
            {
                Scout = new ScoutDirector(Game, Player, Spawn, line => Log.Info("match: " + line));
                Scout.PopulateWorld();
                ServerHooks.OnKill = Scout.OnKill;
                ServerHooks.AfterUpdate = Scout.Tick;
                Scout.OnWon = () => EndMatch(new[] { 4, 9 });
            }
            Leveling.Install();
            Log.Info("match: player 0 ready: synchronizable " + Game.SynchronizableIndex(Player) +
                     ", ability bar " + Game.SynchronizableIndex(Game.GetSynchronizable(Game.SynchronizableIndex(Player) + 1)) +
                     ", game mode " + Game.SynchronizableIndex(Game.ActiveMode) + ", supplies " + (Game.Supplies == null ? "none" : Game.SynchronizableIndex(Game.Supplies).ToString()) +
                     ", Health " + ServerHooks.GetStat(Player, R.Name("Stat.Health")) + "/" + ServerHooks.GetStat(Player, R.Name("Stat.HealthMax")) +
                     ", tutorial stage " + Game.TutorialStage);
        }

        /// <summary>Ends the current match: drops the session and shuts the World down (on a server that resets it).</summary>
        private void TeardownMatch()
        {
            _session = null;
            ServerHooks.ResetMatchState();
            Game.WorldType.GetMethod("Shutdown", GameRuntime.All, null, Type.EmptyTypes, null).Invoke(Game.World, null);
            Log.Info("match: previous match shut down");
        }

        /// <summary>
        /// Writes every map object of the loaded map (class, position, simple public
        /// fields and properties) to local\tutorial_map_objects.txt in the repo. That
        /// folder is git-ignored: the dump is derived from game data and stays local.
        /// </summary>
        private void DumpMapObjects()
        {
            try
            {
                string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
                string dir = Path.Combine(repo, "local");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "tutorial_map_objects.txt");
                File.WriteAllLines(file, Game.DescribeMapObjects().ToArray());
                Log.Info("match: wrote the map-object dump to " + file);
            }
            catch (Exception e) { Log.Warn("match: map-object dump failed: " + GameRuntime.Unwrap(e).Message); }
        }

        /// <summary>
        /// Builds the server hail for client 0 on the game thread: takes the
        /// camera direction from the client's own connect hail (without it the
        /// player can't move), then writes the hail with the game's serializers.
        /// </summary>
        public byte[] BuildHail(byte[] clientHail)
        {
            byte[] result = null;
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Post(() =>
            {
                try
                {
                    int wantedMode = _wantedMap == GameRuntime.TutorialMap ? GameRuntime.TutorialGameModeType
                                   : _wantedMap >= 5 && _wantedMap <= 7 ? GameRuntime.HordeGameModeType
                                   : _wantedMap == 1 || _wantedMap == 2 || _wantedMap == 3 || _wantedMap == 8 || _wantedMap == 14 ? GameRuntime.ScavengerHuntGameModeType
                                   : GameRuntime.ScoutMissionGameModeType;
                    int wantedDifficulty = _wantedQueue == 2 ? 1 : _wantedQueue == 3 ? 2 : 0;
                    string oldCharacter = Game.Character;
                    int oldLevel = Game.MatchLevel;
                    ChooseCharacterAndInfection(wantedMode == GameRuntime.TutorialGameModeType);
                    ChooseBotHeroes(wantedMode == GameRuntime.ScavengerHuntGameModeType);
                    if (_matchUsed || wantedMode == GameRuntime.ScavengerHuntGameModeType || _wantedMap != Game.MapIndex || wantedMode != Game.ModeKind || wantedDifficulty != Game.Difficulty ||
                        oldCharacter != Game.Character || oldLevel != Game.MatchLevel)
                    {
                        Game.MapIndex = _wantedMap;
                        Game.ModeKind = wantedMode;
                        // horde mode type in the hail and on the server: Normal (1) for resistance normal, Heroic (2)
                        // for resistance hard (the hub keeps Heroic locked below account level 20), else Easy (0).
                        Game.Difficulty = wantedDifficulty;
                        // A new match client: start from a fresh match.
                        Log.Info("match: new match client; building a fresh match");
                        TeardownMatch();
                        try { BuildMatch(); }
                        catch (Exception e)
                        {
                            Failed = true;
                            throw new Exception("rebuilding the match failed: " + GameRuntime.Unwrap(e), e);
                        }
                        _matchUsed = false;
                    }
                    object connection = null;
                    try { connection = Game.ReadClientConnectionData(clientHail); }
                    catch (Exception e) { Log.Warn("match: could not read the client's connect hail: " + GameRuntime.Unwrap(e).Message); }
                    object client = Game.GetClient(0);
                    if (connection != null)
                    {
                        float[] camera = GameRuntime.Camera(connection);
                        Game.SetCamera(ClientInfoObject, camera[0], camera[1]);
                        client.GetType().GetField(R.Name("Net.Connection"), GameRuntime.All).SetValue(client, connection);
                        Log.Info("match: client camera direction (" + camera[0] + ", " + camera[1] + ") from its connect hail");
                    }
                    else Log.Warn("match: keeping the default camera direction (" + DefaultCameraX + ", " + DefaultCameraY + ")");
                    result = MatchHail.Build(0, client, Game.BuildGameInfo(Game.MatchLevel, 1, 1800f));
                }
                catch (Exception e) { error = GameRuntime.Unwrap(e); }
                done.Set();
            });
            done.WaitOne();
            if (error != null) throw new Exception("building the hail failed: " + error.Message, error);
            return result;
        }
    }
}

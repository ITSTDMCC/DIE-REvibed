using System;
using System.Collections.Generic;
using System.Reflection;
using EpidemicServer.Resolve;

namespace EpidemicServer.Match
{
    /// <summary>
    /// One connected match client, driven on the game thread (owner's design,
    /// 2026-10-06). Client frames go through the game's own reliable layers
    /// (roles ReliableOrdered and ReliableUnordered), which record what to ack.
    /// Until the client reports Loaded we send empty frames, as before;
    /// after it, every tick runs the game logic and sends acks, controllers and,
    /// when due, synchronizables.
    /// </summary>
    public sealed class MatchSession
    {
        public const float TickSeconds = 1f / 30f;
        /// <summary>Synchronizables go out every this many ticks.</summary>
        private const int SyncEveryTicks = 3;
        private const byte MatchMessage = 0;
        private const byte Loaded = 20;
        private const byte IntroCameraDone = 2;
        private const byte IntroSkipped = 14;
        /// <summary>The "Move" guide follows the wake-up stage after this long.</summary>
        private const float WakeUpSeconds = 4f;
        private static Dictionary<byte, string> _messageNames;
        private static Dictionary<byte, string> MessageNames
        {
            get
            {
                return _messageNames ?? (_messageNames = new Dictionary<byte, string>
                {
                    { 2, R.Name("Msg.CameraDone") }, { 14, R.Name("Msg.IntroSkipped") }, { 20, R.Name("Msg.Loaded") }, { 28, R.Name("Msg.Stats") },
                });
            }
        }

        private readonly GameRuntime _game;
        private readonly object _player, _client;
        private readonly Action<byte[]> _send;
        private readonly object _ordered, _unordered;
        private readonly Delegate _onMessage;
        private readonly DateTime _start = DateTime.UtcNow;
        private readonly MethodInfo _update, _readOrdered, _readUnordered, _sendOrdered, _sendUnordered, _controllerIn, _queueOrdered;
        private readonly Delegate _ackIgnored;

        private int _frame, _ticksSinceSync;
        private bool _loaded, _syncDue;
        private readonly HashSet<Type> _typesSent = new HashSet<Type>();
        private static readonly HashSet<object> NoExtras = new HashSet<object>();
        public int FramesIn, FramesOut, MessagesIn;
        private float _wakeUpAt = -1f;
        private int FirstLoadedFrame;

        public MatchSession(GameRuntime game, object player, Action<byte[]> send)
        {
            _game = game;
            _player = player;
            _client = game.GetClient(0);
            _send = send;
            _ordered = NewLayer("ReliableOrdered");
            _unordered = NewLayer("ReliableUnordered");
            _readOrdered = _ordered.GetType().GetMethod(R.Name("ReliableOrdered.Read"), GameRuntime.All);
            _readUnordered = _unordered.GetType().GetMethod(R.Name("ReliableUnordered.Read"), GameRuntime.All);
            _sendOrdered = _ordered.GetType().GetMethod("Send", GameRuntime.All);
            _sendUnordered = _unordered.GetType().GetMethod("Send", GameRuntime.All);
            _onMessage = Delegate.CreateDelegate(_readOrdered.GetParameters()[1].ParameterType, this,
                typeof(MatchSession).GetMethod("OnMessage", BindingFlags.NonPublic | BindingFlags.Instance));
            _update = game.WorldType.GetMethod("Update", GameRuntime.All, null, new[] { typeof(float), typeof(float), typeof(int) }, null);
            _controllerIn = _client.GetType().GetMethod(R.Name("Net.ReadInput"), GameRuntime.All);
            // Reliable game messages from the server hooks (e.g. destroy) go on the in-order layer, channel 0.
            _queueOrdered = _ordered.GetType().GetMethod(R.Name("ReliableOrdered.Queue"), GameRuntime.All);
            _ackIgnored = Delegate.CreateDelegate(_queueOrdered.GetParameters()[3].ParameterType,
                typeof(MatchSession).GetMethod("IgnoreAck", BindingFlags.NonPublic | BindingFlags.Static));
            ServerHooks.QueueReliable = message => _queueOrdered.Invoke(_ordered, new[] { (object)Time, message, 0, _ackIgnored, 0.1f });
            // Bot heroes (Scavenger): tell the client who they are before their players sync.
            BotDriver.AnnounceBots(game);
        }

        /// <summary>A reliable layer (by role) whose callback is a no-op (it throws without one).</summary>
        private object NewLayer(string role)
        {
            Type t = R.Type(role);
            object layer = Activator.CreateInstance(t, true);
            FieldInfo callback = t.GetField(R.Name(role + ".Callback"), GameRuntime.All);
            callback.SetValue(layer, Delegate.CreateDelegate(callback.FieldType, typeof(MatchSession).GetMethod("Ignore", BindingFlags.NonPublic | BindingFlags.Static)));
            return layer;
        }

        private static void Ignore(object buffer, int channel) { }

        private static void IgnoreAck(object message) { }

        private float Time { get { return (float)(DateTime.UtcNow - _start).TotalSeconds; } }

        /// <summary>A MatchFrame frame from the client.</summary>
        public void OnClientFrame(byte[] frame)
        {
            FramesIn++;
            GameBuffer b = GameBuffer.Wrap(frame);
            _controllerIn.Invoke(_client, new[] { b.Buffer });
            _readOrdered.Invoke(_ordered, new[] { b.Buffer, _onMessage });
            _readUnordered.Invoke(_unordered, new[] { b.Buffer, _onMessage });
        }

        /// <summary>A reliable message payload: byte message kind, byte message type, then fields.</summary>
        private void OnMessage(object buffer)
        {
            MessagesIn++;
            GameBuffer b = GameBuffer.From(buffer);
            byte kind = b.ReadByte();
            byte type = b.ReadByte();
            string name;
            if (!MessageNames.TryGetValue(type, out name)) name = "unknown";
            string label = (kind == MatchMessage ? R.Name("Msg.Game") : kind == 1 ? R.Name("Msg.Debug") : "kind " + kind) + " " + type + " (" + name + ")";
            if (kind == MatchMessage && type == Loaded && !_loaded)
            {
                _loaded = true;
                _syncDue = true;
                Log.Info("match: client says " + label + "; spawning player 0 on the next frame");
                Log.Info("match: player 0 Health " + ServerHooks.GetStat(_player, R.Name("Stat.Health")) + " / Stat.HealthMax " + ServerHooks.GetStat(_player, R.Name("Stat.HealthMax")));
            }
            else if (kind == MatchMessage && (type == IntroSkipped || type == IntroCameraDone) && _game.TutorialStage == 0)
            {
                // End the intro: stage 1 (wake up), sent with the next ActiveMode sync.
                _game.TutorialStage = 1;
                _wakeUpAt = Time;
                _syncDue = true;
                Log.Info("match: client message " + label + "; tutorial stage 0 -> 1 (wake up)");
            }
            else if (kind == MatchMessage && ServerHooks.OnGameMessage != null && ServerHooks.OnGameMessage(type, b)) { }
            else Log.Info("match: client message " + label);
        }

        /// <summary>One 30 Hz step.</summary>
        public void Tick()
        {
            if (!_loaded)
            {
                _send(new byte[] { 0 });   // empty frame, as before loading completes
                FramesOut++;
                FirstLoadedFrame = FramesOut;
                return;
            }
            float t = Time;
            if (_wakeUpAt >= 0f && t - _wakeUpAt >= WakeUpSeconds && _game.TutorialStage == 1)
            {
                // Show the "Move" guide. Later stages need server tutorial logic we don't have yet.
                _game.TutorialStage = 2;
                _wakeUpAt = -1f;
                _syncDue = true;
                Log.Info("match: tutorial stage 1 -> 2 (Move guide)");
            }
            _frame++;
            _update.Invoke(_game.World, new object[] { TickSeconds, t, _frame });

            GameBuffer b = GameBuffer.Create();
            _sendOrdered.Invoke(_ordered, new[] { (object)t, b.Buffer });
            _sendUnordered.Invoke(_unordered, new[] { (object)t, b.Buffer });
            MatchFrames.WriteControllers(b, _client, _game);

            // Every few ticks send every active synchronizable. Objects that become
            // inactive are not sent again: the client drops an object synced as
            // inactive without letting it die. Despawns go out as destroy messages
            // (ServerHooks.OnDestroyed).
            List<object> syncs = new List<object>();
            if (_syncDue || ServerHooks.SyncNow || ++_ticksSinceSync >= SyncEveryTicks)
            {
                _syncDue = false;
                ServerHooks.SyncNow = false;
                _ticksSinceSync = 0;
                syncs = _game.ActiveSynchronizables(NoExtras);
                foreach (object o in syncs)
                    if (_typesSent.Add(o.GetType()))
                        Log.Info("match: first sync of " + o.GetType().FullName + " (index " + _game.SynchronizableIndex(o) + ")");
            }
            MatchFrames.WriteSyncs(_game, b, syncs, _client);
            byte[] frame = b.ToBytes();
            if (FramesOut == FirstLoadedFrame) Log.Info("match: first full frame (" + frame.Length + " bytes, " + syncs.Count + " synchronizables): " + BitConverter.ToString(frame));
            _send(frame);
            FramesOut++;
        }
    }
}

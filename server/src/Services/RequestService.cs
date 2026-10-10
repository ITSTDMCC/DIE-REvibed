using System;
using System.Collections.Generic;
using EpidemicServer.Net;
using EpidemicServer.Protocol;
using EpidemicServer.Wire;

namespace EpidemicServer.Services
{
    /// <summary>
    /// The request server (default port 1555): login, account, inventory and
    /// catalogue requests from the hub. Every player gets the single local account.
    /// </summary>
    public sealed class RequestService : ILinkHandler
    {
        private readonly Account _account;
        private readonly AccountStore _store;
        private readonly byte[] _sessionTicket = Guid.NewGuid().ToByteArray();
        private readonly byte[] _sessionId = Guid.NewGuid().ToByteArray();
        private readonly Dictionary<ushort, Func<Packet, byte[]>> _requests = new Dictionary<ushort, Func<Packet, byte[]>>();

        public RequestService(Account account, AccountStore store)
        {
            _account = account;
            _store = store;
            _requests[LinkMessageIds.OpenNode] = HandleUnlockStoryMapNode;
            _requests[LinkMessageIds.MatchTicket] = HandleMatchmakingTicket;
            _requests[LinkMessageIds.SignIn] = HandleAuth;
            _requests[LinkMessageIds.PlatformSignIn] = HandleSteamAuth;
            _requests[LinkMessageIds.OffFeatureList] = delegate (Packet p) { return RequestServerEncoders.DisabledFeaturesResponse(); };
            _requests[LinkMessageIds.AccountSheet] = delegate (Packet p) { return RequestServerEncoders.AccountDataResponse(_account); };
            _requests[LinkMessageIds.BagList] = delegate (Packet p) { return RequestServerEncoders.InventoryResponse(_account); };
            _requests[LinkMessageIds.Wallet] = delegate (Packet p) { return RequestServerEncoders.CurrencyResponse(_account); };
            _requests[LinkMessageIds.Shop] = delegate (Packet p) { return CatalogueEncoders.ShopResponse(Now()); };
            _requests[LinkMessageIds.UnlockTree] = delegate (Packet p) { return CatalogueEncoders.StoryMapResponse(AccountView.For(_account).UnlockTreeData); };
            _requests[LinkMessageIds.RewardTablesAsk] = delegate (Packet p) { return CatalogueEncoders.RewardSetupResponse(); };
            _requests[LinkMessageIds.Drops] = delegate (Packet p) { return CatalogueEncoders.DropTableResponse(); };
            _requests[LinkMessageIds.MailRead] = delegate (Packet p) { return CatalogueEncoders.MailDataResponse(); };
            _requests[LinkMessageIds.MailWrite] = delegate (Packet p) { return CatalogueEncoders.MailDataResponse(); };
            _requests[LinkMessageIds.ProfileLink] = delegate (Packet p) { return CatalogueEncoders.VanityUrlResponse(); };
            _requests[LinkMessageIds.MoneyCodeAsk] = delegate (Packet p) { return CatalogueEncoders.CurrencyIsoCodeResponse(); };
            _requests[LinkMessageIds.BalanceChange] = delegate (Packet p) { return CatalogueEncoders.BalanceChangeReply(); };
            // Statistics reports: acknowledged with an empty body.
            foreach (ushort id in new ushort[] { LinkMessageIds.PresenceNote, LinkMessageIds.UserInput, LinkMessageIds.HubTime })
                _requests[id] = delegate (Packet p) { return CatalogueEncoders.Empty(); };
        }

        public string Name { get { return "request"; } }

        public static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public void OnConnected(Connection connection) { }

        public void OnDisconnected(Connection connection) { }

        public void OnPacket(Connection c, Packet p)
        {
            if (p.Kind != PacketKind.Request)
            {
                Log.Warn("request: ignoring " + p.Kind + " type " + p.Type + " (" + p.Body.Length + " bytes)");
                return;
            }
            if (p.Type == LinkMessageIds.SignOn)
            {
                HandleLogin(c, p);
                return;
            }
            Func<Packet, byte[]> handler;
            if (_requests.TryGetValue(p.Type, out handler))
            {
                Log.Info("request: type " + p.Type);
                c.Respond(p.RequestId, ResultCode.OK, handler(p));
                return;
            }
            Log.Warn("request: unimplemented type " + p.Type + " body " + BitConverter.ToString(p.Body));
            c.Respond(p.RequestId, ResultCode.Unrecognised, null);
        }

        private void HandleLogin(Connection c, Packet p)
        {
            SignOn req = SignOn.Read(new WireReader(p.Body));
            Log.Info("request: login from '" + req.Name + "' version " + req.VerMajor + "." + req.VerMinor + "." +
                     req.VerBuild + "." + req.VerRevision + " branch '" + req.Branch + "' (" + req.SteamTicket.Length + "-byte Steam ticket, not checked)");
            if (!string.IsNullOrEmpty(req.Name)) _account.Name = req.Name;
            _account.Language = req.Language;
            if (_account.CreateTime == 0) _account.CreateTime = Now();
            lock (_account)
            {
                if (Inventory.EnsureStartingItems(_account))
                    Log.Info("account: granted default weapons " + Inventory.DefaultMeleeSchematic + " and " + Inventory.DefaultRangedSchematic + " (story map node " + Inventory.StartingRewardsNode + ")");
            }
            SaveAccount();   // also re-reads the preservation switches from the file
            if (_account.UnlockAll || _account.MaxLevel || _account.UnlimitedCurrency)
            {
                Account view = AccountView.For(_account);
                Log.Info("account: preservation switches unlockAll=" + _account.UnlockAll + " maxLevel=" + _account.MaxLevel + " unlimitedCurrency=" + _account.UnlimitedCurrency +
                         (UnlockCatalog.Ready ? "" : " (game data not loaded yet: unlocks not applied)") + "; the hub sees " + view.Characters.Count + " characters, " +
                         view.Uniques.Count + " weapons, " + view.Gadgets.Count + " gadgets, " + view.Stackables.Count + " stacks, story map XP " + view.StoryMapXp);
            }
            c.Respond(p.RequestId, ResultCode.OK, RequestServerEncoders.SignOnReply(SignInResult.Success));
            c.SendMessage(LinkMessageIds.SignOnData, RequestServerEncoders.LoginData(_account, _sessionTicket, _sessionId, Now()));
            // SignOnData.FirstSignIn makes the hub re-equip every character's starter weapons
            // (on login data message recieved -> Equipment.Initialize(true) -> auto equip starter), so it is
            // true only for an account's very first login. We never cleared it, and every login reset the
            // owner's loadouts to the paddle and pipe (2026-10-07).
            if (_account.FirstSignIn)
            {
                lock (_account) _account.FirstSignIn = false;
                SaveAccount();
                Log.Info("account: first login done; later logins keep the hub's saved loadouts");
            }
        }

        private byte[] HandleAuth(Packet p)
        {
            SignIn req = SignIn.Read(new WireReader(p.Body));
            Log.Info("request: re-auth with session ticket (" + req.Ticket.Length + " bytes)");
            return RequestServerEncoders.SignInReply(SignInResult.Success);
        }

        /// <summary>Story map node unlock; node 2 is the free first character.</summary>
        private byte[] HandleUnlockStoryMapNode(Packet p)
        {
            WireReader r = new WireReader(p.Body);
            int node = r.ReadVarInt32();
            byte[] choices = r.ReadRaw(r.ReadVarInt32());
            int revision = r.ReadVarInt32();
            UnlockOutcome result;
            lock (_account)
            {
                result = StoryMapRules.Unlock(_account, node, choices);
                if (result == UnlockOutcome.Success) SaveAccount();
            }
            Log.Info("request: unlock story map node " + node + " choices [" + string.Join(",", Array.ConvertAll(choices, b => b.ToString())) +
                     "] revision " + revision + ": " + result);
            return StoryMapRules.UnlockResponse(result, revision);
        }

        /// <summary>Hands out a matchmaking ticket; the matchmaking service itself is still a stub.</summary>
        private byte[] HandleMatchmakingTicket(Packet p)
        {
            Log.Info("request: matchmaking ticket issued (body " + BitConverter.ToString(p.Body) + ")");
            try
            {
                string text;
                MatchLoadout l = MatchLoadout.Read(p.Body, AccountView.For(_account), id => { int t; return UnlockCatalog.WeaponTypes.TryGetValue(id, out t) ? t : 0; }, out text);
                MatchLoadout.Current = l;
                Log.Info("request: queued loadout: " + text);
            }
            catch (Exception e) { Log.Warn("request: could not read the queued loadout: " + e.Message); }
            WireWriter w = new WireWriter();
            w.WriteBool(true);
            w.WriteBytes(Guid.NewGuid().ToByteArray());
            return w.ToArray();
        }

        private void SaveAccount()
        {
            try { lock (_account) _store.Save(_account); }
            catch (Exception e) { Log.Error("account: could not save to " + _store.Path + ": " + e.Message); }
        }

        private byte[] HandleSteamAuth(Packet p)
        {
            SignIn req = SignIn.Read(new WireReader(p.Body));
            Log.Info("request: Steam re-auth (" + req.Ticket.Length + "-byte ticket, not checked)");
            return RequestServerEncoders.PlatformSignInReply(SignInResult.Success, _sessionTicket, _account.UserId);
        }
    }

    /// <summary>
    /// Placeholder for the matchmaking (default 2555) and stats (default 4555)
    /// services: accepts connections and logs what the client asks for.
    /// </summary>
    public sealed class LoggingService : ILinkHandler
    {
        private readonly string _name;

        public LoggingService(string name) { _name = name; }

        public string Name { get { return _name; } }

        public void OnConnected(Connection connection) { }

        public void OnDisconnected(Connection connection) { }

        public void OnPacket(Connection c, Packet p)
        {
            Log.Warn(_name + ": unimplemented " + p.Kind + " type " + p.Type + " body " + BitConverter.ToString(p.Body));
            if (p.Kind == PacketKind.Request)
                c.Respond(p.RequestId, ResultCode.Unrecognised, null);
        }
    }
}

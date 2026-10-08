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
            _requests[GameMessageIds.UnlockStoryMapNodeRequest] = HandleUnlockStoryMapNode;
            _requests[GameMessageIds.GetMatchmakingTicketRequest] = HandleMatchmakingTicket;
            _requests[GameMessageIds.AuthRequest] = HandleAuth;
            _requests[GameMessageIds.SteamAuthRequest] = HandleSteamAuth;
            _requests[GameMessageIds.GetDisabledFeaturesRequest] = delegate (Packet p) { return RequestServerEncoders.DisabledFeaturesResponse(); };
            _requests[GameMessageIds.GetAccountDataRequest] = delegate (Packet p) { return RequestServerEncoders.AccountDataResponse(_account); };
            _requests[GameMessageIds.GetInventoryRequest] = delegate (Packet p) { return RequestServerEncoders.InventoryResponse(_account); };
            _requests[GameMessageIds.GetCurrencyRequest] = delegate (Packet p) { return RequestServerEncoders.CurrencyResponse(_account); };
            _requests[GameMessageIds.GetShopRequest] = delegate (Packet p) { return CatalogueEncoders.ShopResponse(Now()); };
            _requests[GameMessageIds.GetStoryMapRequest] = delegate (Packet p) { return CatalogueEncoders.StoryMapResponse(AccountView.For(_account).StoryMapData); };
            _requests[GameMessageIds.GetRewardSetupRequest] = delegate (Packet p) { return CatalogueEncoders.RewardSetupResponse(); };
            _requests[GameMessageIds.GetDropTableRequest] = delegate (Packet p) { return CatalogueEncoders.DropTableResponse(); };
            _requests[GameMessageIds.GetMailDataRequest] = delegate (Packet p) { return CatalogueEncoders.MailDataResponse(); };
            _requests[GameMessageIds.SetMailDataRequest] = delegate (Packet p) { return CatalogueEncoders.MailDataResponse(); };
            _requests[GameMessageIds.GetVanityURLRequest] = delegate (Packet p) { return CatalogueEncoders.VanityUrlResponse(); };
            _requests[GameMessageIds.GetCurrencyISOCodeRequest] = delegate (Packet p) { return CatalogueEncoders.CurrencyIsoCodeResponse(); };
            _requests[GameMessageIds.BalanceChangeRequest] = delegate (Packet p) { return CatalogueEncoders.BalanceChangeResponse(); };
            // Statistics reports: acknowledged with an empty body.
            foreach (ushort id in new ushort[] { GameMessageIds.UserStateMessage, GameMessageIds.SetUserInputData, GameMessageIds.AddActiveCribTimeMessage })
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
            if (p.Type == GameMessageIds.LoginRequest)
            {
                HandleLogin(c, p);
                return;
            }
            Func<Packet, byte[]> handler;
            if (_requests.TryGetValue(p.Type, out handler))
            {
                Log.Info("request: type " + p.Type);
                c.Respond(p.RequestId, RequestResult.OK, handler(p));
                return;
            }
            Log.Warn("request: unimplemented type " + p.Type + " body " + BitConverter.ToString(p.Body));
            c.Respond(p.RequestId, RequestResult.UnrecognizedError, null);
        }

        private void HandleLogin(Connection c, Packet p)
        {
            LoginRequest req = LoginRequest.Read(new WireReader(p.Body));
            Log.Info("request: login from '" + req.Name + "' version " + req.VersionMajor + "." + req.VersionMinor + "." +
                     req.VersionBuild + "." + req.VersionRevision + " branch '" + req.Branch + "' (" + req.SteamTicket.Length + "-byte Steam ticket, not checked)");
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
            c.Respond(p.RequestId, RequestResult.OK, RequestServerEncoders.LoginResponse(AuthResult.Success));
            c.SendMessage(GameMessageIds.LoginDataMessage, RequestServerEncoders.LoginData(_account, _sessionTicket, _sessionId, Now()));
            // LoginDataMessage.FirstLogin makes the hub re-equip every character's starter weapons
            // (CribStart.OnLoginDataMessageRecieved -> Equipment.Initialize(true) -> AutoEquipStarter), so it is
            // true only for an account's very first login. We never cleared it, and every login reset the
            // owner's loadouts to the paddle and pipe (2026-10-07).
            if (_account.FirstLogin)
            {
                lock (_account) _account.FirstLogin = false;
                SaveAccount();
                Log.Info("account: first login done; later logins keep the hub's saved loadouts");
            }
        }

        private byte[] HandleAuth(Packet p)
        {
            AuthRequest req = AuthRequest.Read(new WireReader(p.Body));
            Log.Info("request: re-auth with session ticket (" + req.Ticket.Length + " bytes)");
            return RequestServerEncoders.AuthResponse(AuthResult.Success);
        }

        /// <summary>Story map node unlock; node 2 is the free first character.</summary>
        private byte[] HandleUnlockStoryMapNode(Packet p)
        {
            WireReader r = new WireReader(p.Body);
            int node = r.ReadVarInt32();
            byte[] choices = r.ReadRaw(r.ReadVarInt32());
            int revision = r.ReadVarInt32();
            StoryMapUnlockResult result;
            lock (_account)
            {
                result = StoryMapRules.Unlock(_account, node, choices);
                if (result == StoryMapUnlockResult.Success) SaveAccount();
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
            AuthRequest req = AuthRequest.Read(new WireReader(p.Body));
            Log.Info("request: Steam re-auth (" + req.Ticket.Length + "-byte ticket, not checked)");
            return RequestServerEncoders.SteamAuthResponse(AuthResult.Success, _sessionTicket, _account.UserId);
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
                c.Respond(p.RequestId, RequestResult.UnrecognizedError, null);
        }
    }
}

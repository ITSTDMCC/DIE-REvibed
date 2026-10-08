using System;
using System.Linq;
using EpidemicServer.Wire;

namespace EpidemicServer.Protocol
{
    public sealed class LoginRequest
    {
        public byte[] SteamTicket;
        public int VersionMajor, VersionMinor, VersionBuild, VersionRevision;
        public string Branch;
        public int Language;
        public string Name;

        public static LoginRequest Read(WireReader r)
        {
            LoginRequest m = new LoginRequest();
            m.SteamTicket = r.ReadBytes();
            m.VersionMajor = r.ReadVarInt32();
            m.VersionMinor = r.ReadVarInt32();
            m.VersionBuild = r.ReadVarInt32();
            m.VersionRevision = r.ReadVarInt32();
            m.Branch = r.ReadString();
            m.Language = r.ReadVarInt32();
            m.Name = r.ReadString();
            return m;
        }
    }

    /// <summary>Body shared by AuthRequest (session ticket) and SteamAuthRequest (Steam ticket).</summary>
    public sealed class AuthRequest
    {
        public byte[] Ticket;
        public int VersionMajor, VersionMinor, VersionBuild, VersionRevision;
        public string Branch;
        public int DisabledFeaturesRevision;

        public static AuthRequest Read(WireReader r)
        {
            AuthRequest m = new AuthRequest();
            m.Ticket = r.ReadBytes();
            m.VersionMajor = r.ReadVarInt32();
            m.VersionMinor = r.ReadVarInt32();
            m.VersionBuild = r.ReadVarInt32();
            m.VersionRevision = r.ReadVarInt32();
            m.Branch = r.ReadString();
            m.DisabledFeaturesRevision = r.ReadVarInt32();
            return m;
        }
    }

    /// <summary>Encoders for the request server's replies, in the field order the client reads them.</summary>
    public static class RequestServerEncoders
    {
        public const int DisabledFeaturesRevision = 2;

        /// <summary>
        /// The hub's welcome popup (shown when the starter quest counts as done) loads its pages from
        /// LoginDataMessage.WelcomeDataURL with Unity's WWW (WelcomeDataManager.LoadData) and spins
        /// until that load finishes. We point it at our own empty welcome file (see WelcomeFile).
        /// </summary>
        public static string WelcomeDataUrl = "";

        /// <summary>
        /// Features the hub treats as disabled (ConductorCrafting.Features). With Crossroads (45) off,
        /// the hub's play window lists Practice (GUI_PlayLobbyInfo.SetIsOwner); with StarterQuest (36)
        /// locked, StarterQuestManager.IsUnlocked is true, which unlocks the ScoutMission lobby
        /// (CharacterData.HasUnlockedGameMode(14)). Owner's design, 2026-10-06.
        /// </summary>
        public static readonly int[] DisabledFeatures = { 45, 36 };

        /// <summary>
        /// Heroic Horde stays locked below account level 20 (owner, 2026-10-07): the hub let a level-4
        /// account into ResistanceHard, so the server also sends HoardHeroicMode (44) as disabled, which the
        /// hub's FeatureLocker.IsGameModeLocked maps to ResistanceHard. HeroicUnlockXp is the story map XP for
        /// level 20 (AccountLevelHelpers.GetRequiredExperience(20)), set by the match host once the game's
        /// libraries are loaded; until then Heroic stays locked.
        /// </summary>
        public const int HeroicHordeFeature = 44, HeroicUnlockLevel = 20;
        public static uint HeroicUnlockXp = uint.MaxValue;
        public static Account FeatureAccount;

        /// <summary>A different list gets a different revision, so the hub doesn't keep a cached one.</summary>
        public static int CurrentDisabledFeaturesRevision()
        {
            return DisabledFeaturesRevision + (CurrentDisabledFeatures().Length > DisabledFeatures.Length ? 1 : 0);
        }

        public static int[] CurrentDisabledFeatures()
        {
            Account a = FeatureAccount;
            bool locked = a == null || (!a.UnlockAll && AccountView.EffectiveXp(a) < HeroicUnlockXp);
            return locked ? DisabledFeatures.Concat(new[] { HeroicHordeFeature }).ToArray() : DisabledFeatures;
        }

        public static void WriteCurrency(WireWriter w, Account a)
        {
            w.WriteVarInt32(a.Gold);
            w.WriteVarInt32(a.Silver);
            w.WriteVarInt32(a.CharacterPoints);
            w.WriteVarInt32(a.ResearchPoints);
        }

        public static void WriteAccountData(WireWriter w, Account a)
        {
            w.WriteVarUInt64(a.UserId);
            w.WriteVarUInt32(a.StoryMapXp);
            w.WriteBytes(a.StoryMapData);
            w.WriteByte(a.StoryMapVersion);
            w.WriteVarInt64(a.RegularBoost);
            w.WriteVarInt64(a.PremiumBoost);
            w.WriteVarUInt32(a.UnboundXp);
            w.WriteVarInt64(a.CreateTime);
            w.WriteByte(a.VanityIcon);
            w.WriteVarUInt32(a.UnlockedDlc);
            w.WriteVarInt64(a.LastPremiumGain);
            w.WriteVarInt64(a.LastScavengerWinBonusTime);
            w.WriteVarInt64(a.LastHordeWinBonusTime);
            w.WriteBytes(a.CounterData);
            w.WriteUInt16(a.CrossroadProgress);
            w.WriteVarInt32(a.Language);
        }

        /// <summary>The disabled features above; no characters, languages or maps disabled.</summary>
        public static void WriteDisabledFeatures(WireWriter w)
        {
            int[] features = CurrentDisabledFeatures();
            w.WriteVarInt32(CurrentDisabledFeaturesRevision());
            w.WriteVarUInt32((uint)features.Length); // features, one varint each
            foreach (int f in features) w.WriteVarInt32(f);
            w.WriteVarUInt32(0); // characters
            w.WriteVarUInt32(0); // languages
            w.WriteVarUInt32(0); // maps
        }

        public static byte[] LoginResponse(AuthResult result)
        {
            WireWriter w = new WireWriter();
            w.WriteVarInt32((int)result);
            w.WriteVarInt32(0);   // queue position
            w.WriteSingle(100f);  // logins per second
            return w.ToArray();
        }

        public static byte[] LoginData(Account real, byte[] sessionTicket, byte[] sessionId, long serverTime)
        {
            Account a = AccountView.For(real);
            WireWriter w = new WireWriter();
            w.WriteVarInt32((int)AuthResult.Success);
            w.WriteBytes(sessionTicket);
            w.WriteVarUInt64(a.UserId);
            WriteCurrency(w, a);
            w.WriteVarInt32(0);   // gold spent
            w.WriteVarInt32(0);   // silver spent
            w.WriteVarInt32(0);   // character points spent
            w.WriteByte(0);       // upsell step
            w.WriteVarInt64(0);   // upsell time
            WriteAccountData(w, a);
            w.WriteBool(a.FirstLogin);
            Inventory.WriteStackables(w, a);
            Inventory.WriteUniques(w, a);
            Inventory.WriteGadgets(w, a);
            w.WriteVarUInt32(0);  // unlocked DLC packages
            WriteDisabledFeatures(w);
            w.WriteVarUInt32(0);  // gameplay changes
            w.WriteVarInt32(0);   // gameplay revision
            w.WriteVarUInt32(0);  // login rewards
            w.WriteBool(false);   // expo user
            w.WriteBytes(sessionId);
            w.WriteVarInt32(0);   // ban reason
            w.WriteUInt16(0);     // banned days
            w.WriteString(WelcomeDataUrl);    // welcome page data URL (WelcomeDataManager.URL)
            w.WriteVarInt64(serverTime);
            return w.ToArray();
        }

        public static byte[] AuthResponse(AuthResult result)
        {
            WireWriter w = new WireWriter();
            w.WriteVarInt32((int)result);
            WriteDisabledFeatures(w);
            w.WriteVarInt32(0);   // ban reason
            w.WriteUInt16(0);     // banned days
            return w.ToArray();
        }

        public static byte[] SteamAuthResponse(AuthResult result, byte[] sessionTicket, ulong userId)
        {
            WireWriter w = new WireWriter();
            w.WriteVarInt32((int)result);
            w.WriteBytes(sessionTicket);
            w.WriteVarUInt64(userId);
            WriteDisabledFeatures(w);
            w.WriteVarInt32(0);   // ban reason
            w.WriteUInt16(0);     // banned days
            return w.ToArray();
        }

        public static byte[] DisabledFeaturesResponse()
        {
            WireWriter w = new WireWriter();
            WriteDisabledFeatures(w);
            return w.ToArray();
        }

        public static byte[] AccountDataResponse(Account real)
        {
            Account a = AccountView.For(real);
            WireWriter w = new WireWriter();
            WriteAccountData(w, a);
            w.WriteBool(a.FirstLogin);
            w.WriteVarUInt32(0);  // uniques received
            return w.ToArray();
        }

        public static byte[] InventoryResponse(Account real)
        {
            Account a = AccountView.For(real);
            WireWriter w = new WireWriter();
            Inventory.WriteStackables(w, a);
            Inventory.WriteUniques(w, a);
            Inventory.WriteGadgets(w, a);
            return w.ToArray();
        }

        public static byte[] CurrencyResponse(Account real)
        {
            Account a = AccountView.For(real);
            WireWriter w = new WireWriter();
            w.WriteBool(true);
            WriteCurrency(w, a);
            w.WriteVarInt32(0);   // gold spent
            w.WriteVarInt32(0);   // silver spent
            w.WriteVarInt32(0);   // character points spent
            w.WriteVarInt32(0);   // research points spent
            w.WriteVarInt64(0);   // upsell time
            w.WriteByte(0);       // upsell step
            return w.ToArray();
        }
    }
}

namespace EpidemicServer.Protocol
{
    /// <summary>Replies for the hub's catalogue requests. All catalogues start empty.</summary>
    public static class CatalogueEncoders
    {
        public static byte[] Empty() { return new byte[0]; }

        public static byte[] ShopResponse(long serverTime)
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            // "Outdated", with an empty shop document: the hub then has an active ShopDocument. Answered
            // "up to date" with none, its LoadShopItems threw on the null document and never called back,
            // and that callback is what starts the welcome popup's loader (CribMain.LateUpdate).
            w.WriteBool(true);    // client shop outdated
            w.WriteVarInt32(1);   // revision
            for (int i = 0; i < 7; i++) w.WriteVarUInt32(0); // events, items, user groups, banners, gold prices, price changes, currency events
            // shop settings
            w.WriteVarInt32(0);   // XP unbind cost
            w.WriteVarInt32(0);   // XP unbind step
            w.WriteString("");    // shop URL
            w.WriteVarInt32(0);   // weekly silver
            w.WriteVarInt32(0);   // weekly gold
            w.WriteVarInt32(0);   // weekly character points
            w.WriteVarInt32(0);   // weekly research points
            w.WriteVarUInt32(0);  // boosts
            w.WriteVarInt64(serverTime);
            return w.ToArray();
        }

        public static byte[] StoryMapResponse(byte[] unlockedNodes)
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteBool(true);
            w.WriteBytes(unlockedNodes);
            return w.ToArray();
        }

        public static byte[] RewardSetupResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteBool(false);
            w.WriteVarInt32(1);
            w.WriteVarUInt32(0);
            return w.ToArray();
        }

        public static byte[] DropTableResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteBool(false);
            w.WriteVarInt32(1);
            w.WriteVarUInt32(0);  // cache boxes
            return w.ToArray();
        }

        public static byte[] MailDataResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteString("");    // email
            w.WriteVarInt32(0);   // newsletter flags
            w.WriteByte(0);       // sex
            w.WriteVarInt32(0);   // birth year
            w.WriteBool(true);    // success
            return w.ToArray();
        }

        public static byte[] VanityUrlResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteString("");
            w.WriteBytes(new byte[0]);
            return w.ToArray();
        }

        public static byte[] CurrencyIsoCodeResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteBool(true);
            w.WriteString("USD");
            return w.ToArray();
        }

        public static byte[] BalanceChangeResponse()
        {
            EpidemicServer.Wire.WireWriter w = new EpidemicServer.Wire.WireWriter();
            w.WriteVarUInt32(0);
            w.WriteVarInt32(0);
            return w.ToArray();
        }
    }
}

namespace EpidemicServer.Protocol
{
    /// <summary>
    /// Type ids used on the request-server link (default TCP port 1555).
    /// Requests carry these ids; responses are matched by request id instead.
    /// Only the ids this server handles or logs are listed.
    /// </summary>
    public static class LinkMessageIds
    {
        public const ushort SignIn = 1;
        public const ushort BagList = 2;
        public const ushort Wallet = 3;
        public const ushort MatchSignIn = 5;
        public const ushort Debug = 18;
        public const ushort UnlockTree = 19;
        public const ushort OpenNode = 20;
        public const ushort Shop = 24;
        public const ushort PlatformSignIn = 25;
        public const ushort AccountSheet = 27;
        public const ushort Drops = 30;
        public const ushort MailRead = 33;
        public const ushort MailWrite = 34;
        public const ushort Shutdown = 39;
        public const ushort MaintenanceNote = 40;
        public const ushort OffFeatureList = 41;
        public const ushort SignOn = 43;
        public const ushort SignOnData = 44;
        public const ushort QueueSpot = 45;
        public const ushort BalanceChange = 46;
        public const ushort MoneyCodeAsk = 47;
        public const ushort OffFeaturesChanged = 48;
        public const ushort ProfileLink = 56;
        public const ushort MatchTicket = 58;
        public const ushort DebugMatchTicket = 59;
        public const ushort RewardTablesAsk = 62;
        public const ushort PresenceNote = 65;
        public const ushort UserInput = 75;
        public const ushort HubTime = 76;
    }

    /// <summary>Login and authentication outcomes understood by the client.</summary>
    public enum SignInResult
    {
        UnknownError = 0,
        Success = 1,
        BadVersion = 2,
        BadTicket = 3,
        SignInFailed = 4,
        BadBranch = 5,
        Maintenance = 6,
        StaleFeatureList = 7,
        StaleTicket = 8,
        Suspended = 9
    }
}

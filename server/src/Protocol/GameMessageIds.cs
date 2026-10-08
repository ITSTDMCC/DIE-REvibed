namespace EpidemicServer.Protocol
{
    /// <summary>
    /// Type ids used on the request-server link (default TCP port 1555).
    /// Requests carry these ids; responses are matched by request id instead.
    /// Only the ids this server handles or logs are listed.
    /// </summary>
    public static class GameMessageIds
    {
        public const ushort AuthRequest = 1;
        public const ushort GetInventoryRequest = 2;
        public const ushort GetCurrencyRequest = 3;
        public const ushort GameplayAuthRequest = 5;
        public const ushort DebugRequest = 18;
        public const ushort GetStoryMapRequest = 19;
        public const ushort UnlockStoryMapNodeRequest = 20;
        public const ushort GetShopRequest = 24;
        public const ushort SteamAuthRequest = 25;
        public const ushort GetAccountDataRequest = 27;
        public const ushort GetDropTableRequest = 30;
        public const ushort GetMailDataRequest = 33;
        public const ushort SetMailDataRequest = 34;
        public const ushort ShutdownMessage = 39;
        public const ushort MaintenanceModeMessage = 40;
        public const ushort GetDisabledFeaturesRequest = 41;
        public const ushort LoginRequest = 43;
        public const ushort LoginDataMessage = 44;
        public const ushort LoginQueuePosUpdate = 45;
        public const ushort BalanceChangeRequest = 46;
        public const ushort GetCurrencyISOCodeRequest = 47;
        public const ushort DisabledFeaturesUpdatedMessage = 48;
        public const ushort GetVanityURLRequest = 56;
        public const ushort GetMatchmakingTicketRequest = 58;
        public const ushort GetDebugMatchmakingTicketRequest = 59;
        public const ushort GetRewardSetupRequest = 62;
        public const ushort UserStateMessage = 65;
        public const ushort SetUserInputData = 75;
        public const ushort AddActiveCribTimeMessage = 76;
    }

    /// <summary>Login and authentication outcomes understood by the client.</summary>
    public enum AuthResult
    {
        UnknownError = 0,
        Success = 1,
        InvalidVersion = 2,
        InvalidTicket = 3,
        LoginFail = 4,
        WrongBranch = 5,
        Maintenance = 6,
        OutdatedDisabledFeatures = 7,
        TicketOutdated = 8,
        AccountFrozen = 9
    }
}

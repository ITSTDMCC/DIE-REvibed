// Checks the server against the game's own protocol code.
//
// These tests load the game's assemblies from local/ref (made by
// tools/prepare_reference_assemblies.py from the owner's install) and use the
// game's own deserializers and TCP client as the reference. They never ship
// with the server. Build and run with tests/run_tests.sh.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using EpidemicServer.Net;
using EpidemicServer.Protocol;
using EpidemicServer.Services;
using EpidemicServer.Wire;
using GameProtocol;
using ConductorShop;
using StunMessage;
using StunTCP;
using GameResult = StunTCP.RequestResult;
using System.Reflection;

public static class ReferenceTests
{
    private static int _failures;
    private static int _passes;

    public static int Main()
    {
        Run("varints match the game's encoding", VarIntsMatch);
        Run("strings match the game's encoding", StringsMatch);
        Run("login response decodes", LoginResponseDecodes);
        Run("login data decodes", LoginDataDecodes);
        Run("auth responses decode", AuthResponsesDecode);
        Run("account, inventory and currency decode", AccountInventoryCurrencyDecode);
        Run("catalogue responses decode", CataloguesDecode);
        Run("game TCP client logs in against the server", EndToEndLogin);
        Console.WriteLine(_passes + " passed, " + _failures + " failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passes++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failures++; Console.WriteLine("FAIL " + name + ": " + e); }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new Exception("check failed: " + what);
    }

    /// <summary>Wraps bytes from our encoder in the game's Message reader.</summary>
    private static Message GameMessage(byte[] bytes)
    {
        Message m = new Message(new List<ArraySegment<byte>>(), () => new ArraySegment<byte>(new byte[4096]));
        m.Write(bytes, 0, bytes.Length);
        m.Position = 0;
        return m;
    }

    private static void FullyConsumed(Message m, string what)
    {
        Check(m.Position == m.Length, what + " left " + (m.Length - m.Position) + " unread bytes");
    }


    private static readonly Type SerializationType = typeof(LoginDataMessage).Assembly.GetType("GameProtocol.MessageSerialization");

    private static MethodInfo FindSerializer(string name, Type t)
    {
        foreach (MethodInfo m in SerializationType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] ps = m.GetParameters();
            if (m.Name == name && ps.Length == 2 && ps[0].ParameterType == t.MakeByRefType()) return m;
        }
        return null;
    }

    /// <summary>
    /// Checks our bytes against the game's code for type T: byte-for-byte
    /// against its serializer when the game has one, otherwise by decoding
    /// with its deserializer. Returns the decoded (or given) value.
    /// </summary>
    private static T Reference<T>(byte[] ours, T expected, string what) where T : struct
    {
        MethodInfo ser = FindSerializer("Serialize", typeof(T));
        if (ser != null)
        {
            Message m = new Message(new List<ArraySegment<byte>>(), () => new ArraySegment<byte>(new byte[4096]));
            object[] args = { expected, m };
            ser.Invoke(null, args);
            m = (Message)args[1];
            m.Position = 0;
            byte[] theirs = m.ToBytes();
            Check(theirs.SequenceEqual(ours), what + " bytes differ from the game's encoding:\n ours   " +
                  BitConverter.ToString(ours) + "\n theirs " + BitConverter.ToString(theirs));
            return expected;
        }
        MethodInfo de = FindSerializer("Deserialize", typeof(T));
        Check(de != null, "game has no serializer for " + typeof(T).Name);
        Message g = GameMessage(ours);
        object[] dargs = { default(T), g };
        Check((bool)de.Invoke(null, dargs), what + " failed to decode");
        g = (Message)dargs[1];
        FullyConsumed(g, what);
        return (T)dargs[0];
    }


    /// <summary>default(T) with every null array, list or string field (recursively) set to empty.</summary>
    private static T Filled<T>() where T : struct
    {
        return (T)Fill(typeof(T), null);
    }

    private static object Fill(Type t, object value)
    {
        if (t == typeof(string)) return value ?? "";
        if (t.IsArray) return value ?? Array.CreateInstance(t.GetElementType(), 0);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return value ?? Array.CreateInstance(t.GetGenericArguments()[0], 0);
        if (t.IsValueType && !t.IsPrimitive && !t.IsEnum)
        {
            object boxed = value ?? Activator.CreateInstance(t);
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                f.SetValue(boxed, Fill(f.FieldType, f.GetValue(boxed)));
            return boxed;
        }
        return value;
    }

    private static void VarIntsMatch()
    {
        long[] values = { 0, 1, 127, 128, 300, 16384, int.MaxValue, -1, int.MinValue, long.MaxValue, long.MinValue };
        foreach (long v in values)
        {
            WireWriter w = new WireWriter();
            w.WriteVarInt64(v);
            Message m = GameMessage(w.ToArray());
            long read = 0;
            Check(m.ReadVarInt(ref read) && read == v, "varint64 " + v);
            FullyConsumed(m, "varint64 " + v);

            if (v >= int.MinValue && v <= int.MaxValue)
            {
                w = new WireWriter();
                w.WriteVarInt32((int)v);
                m = GameMessage(w.ToArray());
                int read32 = 0;
                Check(m.ReadVarInt(ref read32) && read32 == (int)v, "varint32 " + v);
                FullyConsumed(m, "varint32 " + v);
            }
        }
    }

    private static void StringsMatch()
    {
        foreach (string s in new[] { "", "Player", "Zombie Ätare 生存" })
        {
            WireWriter w = new WireWriter();
            w.WriteString(s);
            Message m = GameMessage(w.ToArray());
            string read = null;
            Check(m.Read(ref read) && read == s, "string '" + s + "'");
            FullyConsumed(m, "string");
        }
    }

    private static void LoginResponseDecodes()
    {
        LoginResponse expected = Filled<LoginResponse>();
        expected.Result = AuthResponseEnum.Success;
        expected.LoginsPerSecond = 100f;
        Reference(RequestServerEncoders.LoginResponse(AuthResult.Success), expected, "LoginResponse");
    }

    private static Account SampleAccount()
    {
        Account a = new Account();
        a.UserId = 1234567890123UL;
        a.Name = "Tester";
        a.Silver = 4321;
        StoryMapRules.Unlock(a, StoryMapRules.FirstCharacterNode, new byte[] { 1 });
        a.CounterData = new byte[] { 9 };
        a.CrossroadProgress = 7;
        a.CreateTime = 1400000000;
        a.Language = 2;
        return a;
    }

    private static void LoginDataDecodes()
    {
        Account a = SampleAccount();
        byte[] ticket = { 5, 6, 7, 8 };
        byte[] session = { 1, 1, 2, 3, 5, 8 };
        LoginDataMessage d = Reference(RequestServerEncoders.LoginData(a, ticket, session, 1791000000), default(LoginDataMessage), "LoginDataMessage");
        Check(d.Result == AuthResponseEnum.Success, "result");
        Check(d.AuthTicket.SequenceEqual(ticket), "ticket");
        Check(d.UserID == a.UserId, "user id");
        Check(d.Currency.Silver == 4321, "silver");
        Check(d.AccountData.UserID == a.UserId, "account user id");
        Check(d.AccountData.StoryMapData.SequenceEqual(a.StoryMapData), "story map data");
        Check(d.AccountData.CrossroadProgress == 7, "crossroad progress");
        Check(d.AccountData.CreateTime == 1400000000, "create time");
        Check((int)d.AccountData.Language == 2, "language");
        Check(d.FirstLogin, "first login");
        Check(d.Stackables.Count() == 0 && d.Uniques.Count() == 0 && d.Gadgets.Count() == 0, "empty inventory");
        Check(d.DisabledFeatures.Revision == RequestServerEncoders.CurrentDisabledFeaturesRevision(), "disabled features revision");
        Check(d.SessionID.SequenceEqual(session), "session id");
        Check(d.ServerTime == 1791000000, "server time");
    }

    private static void AuthResponsesDecode()
    {
        AuthResponse auth = Filled<AuthResponse>();
        auth.Result = AuthResponseEnum.Success;
        auth.Data.Revision = RequestServerEncoders.CurrentDisabledFeaturesRevision();
        Reference(RequestServerEncoders.AuthResponse(AuthResult.Success), auth, "AuthResponse");

        SteamAuthResponse steam = Filled<SteamAuthResponse>();
        steam.Result = AuthResponseEnum.Success;
        steam.AuthTicket = new byte[] { 1, 2 };
        steam.UserID = 99;
        steam.Data.Revision = RequestServerEncoders.CurrentDisabledFeaturesRevision();
        Reference(RequestServerEncoders.SteamAuthResponse(AuthResult.Success, new byte[] { 1, 2 }, 99), steam, "SteamAuthResponse");

        GetDisabledFeaturesResponse f = Filled<GetDisabledFeaturesResponse>();
        f.Data.Revision = RequestServerEncoders.CurrentDisabledFeaturesRevision();
        Reference(RequestServerEncoders.DisabledFeaturesResponse(), f, "GetDisabledFeaturesResponse");
    }

    private static void AccountInventoryCurrencyDecode()
    {
        Account a = SampleAccount();
        GetAccountDataResponse ad = Filled<GetAccountDataResponse>();
        ad.Data.UserID = a.UserId;
        ad.Data.StoryMapData = a.StoryMapData;
        ad.Data.CreateTime = a.CreateTime;
        ad.Data.CounterData = a.CounterData;
        ad.Data.CrossroadProgress = a.CrossroadProgress;
        ad.Data.Language = (ConductorCrafting.Languages)a.Language;
        ad.FirstLogin = true;
        Reference(RequestServerEncoders.AccountDataResponse(a), ad, "GetAccountDataResponse");

        Reference(RequestServerEncoders.InventoryResponse(new Account()), Filled<GetInventoryResponse>(), "GetInventoryResponse");

        GetCurrencyResult currency = Filled<GetCurrencyResult>();
        currency.Succeeded = true;
        currency.Currency.Silver = 4321;
        Reference(RequestServerEncoders.CurrencyResponse(a), currency, "GetCurrencyResult");
    }

    private static void CataloguesDecode()
    {
        GetShopResponse shop = Filled<GetShopResponse>();
        shop.ClientShopOutdated = true;
        shop.Revision = 1;
        shop.ServerTime = 1791000000;
        Reference(CatalogueEncoders.ShopResponse(1791000000), shop, "GetShopResponse");

        GetStoryMapResponse sm = Filled<GetStoryMapResponse>();
        sm.Success = true;
        sm.UnlockedNodes = new byte[] { 4 };
        Reference(CatalogueEncoders.StoryMapResponse(new byte[] { 4 }), sm, "GetStoryMapResponse");

        GetRewardSetupResponse rs = Filled<GetRewardSetupResponse>();
        rs.Revision = 1;
        Reference(CatalogueEncoders.RewardSetupResponse(), rs, "GetRewardSetupResponse");

        GetDropTableResponse dt = Filled<GetDropTableResponse>();
        dt.Revision = 1;
        Reference(CatalogueEncoders.DropTableResponse(), dt, "GetDropTableResponse");

        GetMailDataResponse md = Filled<GetMailDataResponse>();
        md.Success = true;
        Reference(CatalogueEncoders.MailDataResponse(), md, "GetMailDataResponse");

        Reference(CatalogueEncoders.VanityUrlResponse(), Filled<GetVanityURLResponse>(), "GetVanityURLResponse");

        GetCurrencyISOCodeResponse iso = Filled<GetCurrencyISOCodeResponse>();
        iso.Success = true;
        iso.Currency = "USD";
        Reference(CatalogueEncoders.CurrencyIsoCodeResponse(), iso, "GetCurrencyISOCodeResponse");

        Reference(CatalogueEncoders.BalanceChangeResponse(), Filled<BalanceChangeResponse>(), "BalanceChangeResponse");
    }

    /// <summary>
    /// Runs the server on a free port and logs in with the game's own TCP
    /// client and message dispatch, as the hub would.
    /// </summary>
    private static void EndToEndLogin()
    {
        Account account = new Account();
        LinkServer server = new LinkServer(IPAddress.Loopback, 0, new RequestService(account, new AccountStore(System.IO.Path.GetTempFileName())));
        server.Start();
        try
        {
            TestLink link = new TestLink();
            ManualResetEvent connected = new ManualResetEvent(false);
            bool connectOk = false;
            link.Connect(new IPEndPoint(IPAddress.Loopback, server.Port), delegate (bool ok, System.Net.Sockets.SocketError err, System.Net.Sockets.SocketAsyncEventArgs args, TCPClient cl)
            {
                connectOk = ok;
                connected.Set();
            });
            Check(connected.WaitOne(5000) && connectOk, "connect");

            ManualResetEvent gotLoginData = new ManualResetEvent(false);
            ManualResetEvent gotResponse = new ManualResetEvent(false);
            LoginDataMessage loginData = default(LoginDataMessage);
            GameResult responseResult = GameResult.Timeout;
            int loginResult = -1;

            MessageCallbacks<TestLink> callbacks = new MessageCallbacks<TestLink>();
            callbacks.LoginDataMessage = delegate (LoginDataMessage msg, TestLink l) { loginData = msg; gotLoginData.Set(); };
            TCPClient.OnDataReceived += delegate (TCPClient client, byte[] buffer, int offset, int count)
            {
                if (client == link) link.Handle(ref callbacks, buffer, offset, count);
            };

            // Build the LoginRequest exactly as the hub lays it out.
            byte[] guid = null;
            Message req = MessageHandler.GetTempRequestBuffer(ref guid);
            req.Write((ushort)GameMessageType.LoginRequest);
            byte[] steamTicket = new byte[] { 0xAA, 0xBB, 0xCC };
            req.WriteVarInt((uint)steamTicket.Length);
            req.Write(steamTicket, 0, steamTicket.Length);
            req.WriteVarInt(0); req.WriteVarInt(9); req.WriteVarInt(1); req.WriteVarInt(0);
            req.Write("public");
            req.WriteVarInt(0);
            req.Write("Tester");
            link.SendRequest(delegate (GameResult result, ref Message body)
            {
                responseResult = result;
                if (result == GameResult.OK) { int v = 0; body.ReadVarInt(ref v); loginResult = v; }
                gotResponse.Set();
            }, ref req, guid, 5000);

            Check(gotResponse.WaitOne(5000), "login response arrived");
            Check(responseResult == GameResult.OK, "login response result " + responseResult);
            Check(loginResult == (int)AuthResponseEnum.Success, "login accepted");
            Check(gotLoginData.WaitOne(5000), "login data arrived");
            Check(loginData.Result == AuthResponseEnum.Success, "login data result");
            Check(loginData.UserID == account.UserId, "login data user id");
            Check(account.Name == "Tester", "server recorded the player name");
            link.Close();
        }
        finally
        {
            server.Stop();
        }
    }
}

/// <summary>The game's TCP message client with the game's own message dispatch.</summary>
public sealed class TestLink : TCPMessageClient
{
    private static bool OnMessage(ref Message msg, ushort type, ref MessageCallbacks<TestLink> callbacks, ref TestLink self)
    {
        return ((GameMessageType)type).HandleMessage(ref callbacks, ref msg, ref self);
    }

    private static bool OnRequest(ref Message msg, ushort type, ref MessageCallbacks<TestLink> callbacks, ref TestLink self, byte[] guid, IResponseHandler responder)
    {
        return false;
    }

    public int Handle(ref MessageCallbacks<TestLink> callbacks, byte[] buffer, int offset, int count)
    {
        TestLink self = this;
        return HandlePacket<MessageCallbacks<TestLink>, TestLink>(OnMessage, OnRequest, buffer, offset, count, ref callbacks, ref self, this);
    }
}

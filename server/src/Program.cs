using System;
using System.IO;
using System.Net;
using System.Threading;
using EpidemicServer.Match;
using EpidemicServer.Net;
using EpidemicServer.Protocol;
using EpidemicServer.Services;

namespace EpidemicServer
{
    public static class Program
    {
        public const int RequestPort = 1555;
        public const int MatchmakingPort = 2555;
        public const int StatsPort = 4555;
        /// <summary>Match server port: TCP for the match service, UDP for a logger.</summary>
        public const ushort MatchPort = 3555;

        public static int Main(string[] args)
        {
            IPAddress bind = IPAddress.Loopback;
            string gameInstall = null;
            foreach (string arg in args)
            {
                if (arg == "--lan") bind = IPAddress.Any;
                else if (arg.StartsWith("--log="))
                    Log.FilePath = Path.GetFullPath(arg.Substring("--log=".Length));
                else if (arg.StartsWith("--game="))
                    gameInstall = arg.Substring("--game=".Length);
            }
            if (Log.FilePath == null)
            {
                Directory.CreateDirectory("logs");
                Log.FilePath = Path.GetFullPath(Path.Combine("logs", "server-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log"));
            }

            // Our own welcome data for the hub's welcome popup: one page, a title card drawn by the server.
            try
            {
                RequestServerEncoders.WelcomeDataUrl = WelcomeCard.Write(AppDomain.CurrentDomain.BaseDirectory);
                Log.Info("welcome data: " + RequestServerEncoders.WelcomeDataUrl);
            }
            catch (Exception e) { Log.Warn("could not write the welcome data file: " + e.Message); }

            AccountStore store = new AccountStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "account.txt"));
            Account account;
            try { account = store.Load(); }
            catch (Exception e)
            {
                Log.Error("Could not read " + store.Path + ": " + e.Message);
                return 1;
            }
            Log.Info("account: " + store.Path + " (" + account.Nodes.Count + " story map nodes, " + account.Characters.Count + " characters)");

            // With --game, run the match client's own game logic as the match
            // server (needs 32-bit Mono). Its map loader opens "Maps\" relative to
            // the working directory, so that becomes the install folder; the log
            // and account paths are already absolute.
            MatchHost match = null;
            if (gameInstall != null)
            {
                Environment.CurrentDirectory = gameInstall;
                match = new MatchHost(gameInstall, account, store);
                match.Start();
            }
            LinkServer[] servers =
            {
                new LinkServer(bind, RequestPort, new RequestService(account, store)),
                new LinkServer(bind, MatchmakingPort, new MatchmakingService(IPAddress.Loopback, MatchPort, match)),
                new LinkServer(bind, StatsPort, new LoggingService("stats")),
                new LinkServer(bind, MatchPort, new MatchService(match)),
            };
            try
            {
                foreach (LinkServer s in servers) s.Start();
                new UdpLogger("match", bind, MatchPort).Start();
            }
            catch (Exception e)
            {
                Log.Error("Could not start: " + e.Message);
                return 1;
            }
            Log.Info("Dead Island: Epidemic local server running. Press Ctrl+C to stop.");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
    }
}

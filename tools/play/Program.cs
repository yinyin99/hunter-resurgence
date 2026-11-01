using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Play;

Console.OutputEncoding = Encoding.UTF8;
return await Cli.Run(args);

namespace Play
{
    static class Cli
    {
        /// <summary>The day the official servers close: <c>setup</c> refuses before it, so players keep them until then.</summary>
        public static readonly DateTimeOffset Shutdown = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);

        public static bool Refuses(DateTimeOffset now, bool force) => now < Shutdown && !force;

        public static async Task<int> Run(string[] args)
        {
            try
            {
                var flags = args.Skip(1).ToArray();
                switch (args.FirstOrDefault())
                {
                    case "setup" when flags.All(f => f is "--remove" or "--force"):
                        return Setup(flags.Contains("--remove"), flags.Contains("--force"));
                    case "run" when flags.Length == 0:
                        return await RunForwarder();
                    case "hosts" when flags is ["on"] or ["off"]: // what setup starts elevated: only this edits the hosts file as administrator
                        Hosts.ApplyAndFlush(Hosts.DefaultPath, flags[0] == "on" ? Settings.Load().Hosts : null);
                        return 0;
                    default:
                        Console.WriteLine(Loc.S("Usage"));
                        return 2;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        static int Setup(bool remove, bool force)
        {
            if (remove)
            {
                SetHosts(false, null);
                Pki.Uninstall();
                Console.WriteLine(Loc.S("Removed"));
                return 0;
            }
            var now = DateTimeOffset.UtcNow;
            if (Refuses(now, force)) { Console.WriteLine(Loc.F("TooEarly", Shutdown.ToString("yyyy-MM-dd"))); return 1; }
            var s = Settings.Load();
            Console.WriteLine(Loc.F("SetupPlan", s.Hosts.Length, Pki.CaSubject[3..], Pki.Dir, Hosts.DefaultPath));
            foreach (var h in s.Hosts) Console.WriteLine("  " + h);
            Console.WriteLine(Loc.S("SetupTrust"));
            Pki.Install(s.Hosts, now);
            SetHosts(true, s.Hosts);
            Console.WriteLine(Loc.S("SetupDone"));
            return 0;
        }

        /// <summary>Edits the hosts file. Without administrator rights, starts this exe again with "hosts on|off" through the UAC prompt: that child only edits the file, so the certificate steps stay in the user's own session.</summary>
        static void SetHosts(bool on, string[]? hosts)
        {
            var path = Hosts.DefaultPath;
            try { Hosts.ApplyAndFlush(path, hosts); return; }
            catch (UnauthorizedAccessException) { }
            Console.WriteLine(Loc.S("Elevate"));
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, on ? "hosts on" : "hosts off") { UseShellExecute = true, Verb = "runas" });
                p?.WaitForExit();
            }
            catch (Win32Exception) { } // the user said No
            if (Hosts.Has(File.ReadAllText(path)) != on) throw new InvalidOperationException(Loc.S("HostsFailed"));
        }

        static async Task<int> RunForwarder()
        {
            var s = Settings.Load();
            using var leaf = Pki.LoadLeaf(DateTimeOffset.UtcNow);
            if (!Hosts.Has(File.ReadAllText(Hosts.DefaultPath))) Console.WriteLine(Loc.S("RunNoHosts"));
            var app = Forwarder.Build(s.ServerUrl, leaf, 443, 80);
            try { await app.StartAsync(); }
            catch (IOException e) { throw new InvalidOperationException(Loc.F("RunPortBusy", e.Message)); }
            Console.WriteLine(Loc.F("RunStarted", s.ServerUrl.Host));
            await app.WaitForShutdownAsync();
            return 0;
        }
    }
}

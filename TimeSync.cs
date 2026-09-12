using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClockFix
{
    // ====================================================================
    //  Entry point
    // ====================================================================
    internal static class Program
    {
        private static Mutex _instanceLock;

        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (mode == "--export-icons")
            {
                string dir = Path.Combine(Engine.ExeDir, "icons");
                RabbitArt.ExportSet(dir);
                Engine.LogStatic("icon set written to " + dir);
                return 0;
            }

            if (mode == "--check")
            {
                // Headless one-shot, used for verification. The exe is built as
                // winexe so there is no console to print to; results go to the log.
                Engine.LogStatic("--- --check run ---");
                List<Sample> samples = Ntp.Gather(Engine.LogStatic);
                DateTime atZero;
                int agreeing;
                if (Ntp.TryConsensus(samples, out atZero, out agreeing))
                {
                    double off = ((atZero + Ntp.Mono.Elapsed) - DateTime.UtcNow).TotalSeconds;
                    Engine.LogStatic(string.Format(
                        "consensus {0} server(s); clock reads {1:yyyy-MM-dd HH:mm:ss}; offset {2:F3}s",
                        agreeing, DateTime.Now, off));
                    return 0;
                }
                Engine.LogStatic("no consensus");
                return 1;
            }

            // Only one tray instance: the Startup folder can fire more than once
            // (fast startup, re-login) and duplicate icons are confusing.
            bool isNew;
            _instanceLock = new Mutex(true, "Local\\ClockFix.TimeSync.Tray", out isNew);
            if (!isNew) return 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(mode == "--show"));
            return 0;
        }
    }

    // ====================================================================
    //  NTP sampling
    // ====================================================================
    internal sealed class Sample
    {
        public string Server;
        public DateTime TrueUtcAtMonoZero; // normalised so samples stay comparable
        public double RttMs;
    }

    internal static class Ntp
    {
        public static readonly string[] Servers =
        {
            "time.cloudflare.com",
            "time.windows.com",
            "ntp.jst.mfeed.ad.jp",
            "pool.ntp.org"
        };

        public const double AgreementSeconds = 5.0;
        public const int MinAgreeing = 2;
        private const int QueryTimeoutMs = 3000;

        /// <summary>
        /// Monotonic reference. Everything is anchored here rather than to the
        /// wall clock, because the wall clock is the thing being corrected and
        /// jumps mid-run - wall-clock arithmetic would be self-corrupting.
        /// </summary>
        public static readonly Stopwatch Mono = Stopwatch.StartNew();

        private static readonly DateTime NtpEpoch =
            new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static List<Sample> Gather(Action<string> log)
        {
            var samples = new List<Sample>();
            foreach (string host in Servers)
            {
                Sample s = Query(host, log);
                if (s != null)
                {
                    samples.Add(s);
                    log(string.Format("  {0}: ok ({1:F0} ms)", host, s.RttMs));
                }
                else
                {
                    log("  " + host + ": no reply");
                }
            }
            return samples;
        }

        private static Sample Query(string host, Action<string> log)
        {
            IPAddress[] addrs;
            try
            {
                addrs = Dns.GetHostAddresses(host);
            }
            catch (Exception ex)
            {
                log("  " + host + ": DNS failed - " + ex.Message);
                return null;
            }

            foreach (IPAddress addr in addrs)
            {
                if (addr.AddressFamily != AddressFamily.InterNetwork &&
                    addr.AddressFamily != AddressFamily.InterNetworkV6)
                    continue;

                try
                {
                    using (var sock = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                    {
                        sock.ReceiveTimeout = QueryTimeoutMs;
                        sock.SendTimeout = QueryTimeoutMs;
                        sock.Connect(new IPEndPoint(addr, 123));

                        var packet = new byte[48];
                        packet[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (client)

                        var rtt = Stopwatch.StartNew();
                        sock.Send(packet);
                        var buf = new byte[48];
                        int n = sock.Receive(buf);
                        rtt.Stop();
                        TimeSpan monoAtReceive = Mono.Elapsed;

                        if (n < 48) continue;
                        if ((buf[0] & 0x07) != 4) continue;        // must be a server reply
                        int stratum = buf[1];
                        if (stratum < 1 || stratum > 15) continue; // 0 == kiss-of-death

                        DateTime serverTx = ParseTimestamp(buf, 40);
                        if (serverTx == DateTime.MinValue) continue;

                        DateTime trueAtReceive = serverTx.AddTicks(rtt.Elapsed.Ticks / 2);

                        var s = new Sample();
                        s.Server = host;
                        s.TrueUtcAtMonoZero = trueAtReceive - monoAtReceive;
                        s.RttMs = rtt.Elapsed.TotalMilliseconds;
                        return s;
                    }
                }
                catch
                {
                    // next address: v6 may be dead while v4 works
                }
            }
            return null;
        }

        private static DateTime ParseTimestamp(byte[] b, int off)
        {
            ulong seconds = ((ulong)b[off] << 24) | ((ulong)b[off + 1] << 16) |
                            ((ulong)b[off + 2] << 8) | b[off + 3];
            ulong fraction = ((ulong)b[off + 4] << 24) | ((ulong)b[off + 5] << 16) |
                             ((ulong)b[off + 6] << 8) | b[off + 7];
            if (seconds == 0 && fraction == 0) return DateTime.MinValue;
            double ms = seconds * 1000.0 + (fraction * 1000.0 / 4294967296.0);
            return NtpEpoch.AddMilliseconds(ms);
        }

        /// <summary>
        /// Agreement-based, not magnitude-based. There is deliberately NO upper
        /// bound on how large a correction may be: an RTC that lost power can be
        /// years out, and capping the correction is exactly what stops a fix from
        /// working when it is needed most. Safety comes from requiring
        /// independent servers to agree instead.
        /// </summary>
        public static bool TryConsensus(List<Sample> samples, out DateTime atZero, out int agreeing)
        {
            atZero = DateTime.MinValue;
            agreeing = 0;
            if (samples.Count < MinAgreeing) return false;

            var sorted = new List<Sample>(samples);
            sorted.Sort(delegate(Sample a, Sample b)
            {
                return a.TrueUtcAtMonoZero.CompareTo(b.TrueUtcAtMonoZero);
            });
            DateTime median = sorted[sorted.Count / 2].TrueUtcAtMonoZero;

            var cluster = new List<Sample>();
            foreach (Sample s in samples)
            {
                if (Math.Abs((s.TrueUtcAtMonoZero - median).TotalSeconds) <= AgreementSeconds)
                    cluster.Add(s);
            }
            if (cluster.Count < MinAgreeing) return false;

            double meanTicks = 0;
            foreach (Sample s in cluster)
                meanTicks += s.TrueUtcAtMonoZero.Ticks / (double)cluster.Count;

            atZero = new DateTime((long)meanTicks, DateTimeKind.Utc);
            agreeing = cluster.Count;
            return true;
        }
    }

    // ====================================================================
    //  Privileged clock access
    // ====================================================================
    internal static class Clock
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSystemTime(ref SYSTEMTIME st);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint Count; public LUID_AND_ATTRIBUTES Priv; }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// Administrators hold SeSystemtimePrivilege but it is DISABLED in the
        /// token by default, so SetSystemTime fails with 1314 unless it is
        /// enabled first. On a UAC-filtered (unelevated) token it is absent
        /// entirely and this returns false.
        /// </summary>
        public static bool EnableTimePrivilege()
        {
            const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
            const uint TOKEN_QUERY = 0x0008;
            const uint SE_PRIVILEGE_ENABLED = 0x0002;

            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, "SeSystemtimePrivilege", out luid)) return false;

                var tp = new TOKEN_PRIVILEGES();
                tp.Count = 1;
                tp.Priv = new LUID_AND_ATTRIBUTES();
                tp.Priv.Luid = luid;
                tp.Priv.Attributes = SE_PRIVILEGE_ENABLED;

                // Returns true even on partial success; the real check is the
                // last-error value afterwards.
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        public static bool Set(DateTime utc, out int win32Error)
        {
            var st = new SYSTEMTIME();
            st.Year = (ushort)utc.Year;
            st.Month = (ushort)utc.Month;
            st.DayOfWeek = (ushort)(int)utc.DayOfWeek;
            st.Day = (ushort)utc.Day;
            st.Hour = (ushort)utc.Hour;
            st.Minute = (ushort)utc.Minute;
            st.Second = (ushort)utc.Second;
            st.Milliseconds = (ushort)utc.Millisecond;

            bool ok = SetSystemTime(ref st); // takes UTC, so no time-zone maths
            win32Error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }

        public static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }
    }

    // ====================================================================
    //  Artwork
    //
    //  An original White Rabbit, drawn from primitives at run time so the exe
    //  stays a single dependency-free file. The character (Lewis Carroll, 1865)
    //  is public domain; this rendering is original work, not a trace of any
    //  existing illustration, so the exported set is genuinely royalty free.
    //
    //  The watch face carries the state colour: it is the largest solid block
    //  in the design, so it still reads at 16x16 in the tray.
    // ====================================================================
    internal static class RabbitArt
    {
        public static readonly Color Ok   = Color.FromArgb(64, 190, 116);
        public static readonly Color Busy = Color.FromArgb(78, 154, 226);
        public static readonly Color Warn = Color.FromArgb(236, 173, 60);
        public static readonly Color Bad  = Color.FromArgb(222, 86, 82);

        private static readonly Color Fur     = Color.FromArgb(252, 251, 253);
        private static readonly Color Ink     = Color.FromArgb(44, 42, 54);
        private static readonly Color EarPink = Color.FromArgb(236, 172, 182);
        private static readonly Color EyePink = Color.FromArgb(206, 116, 128);
        private static readonly Color Brass   = Color.FromArgb(214, 176, 82);

        /// <summary>Renders at any size; the design is authored on a 64x64 grid.</summary>
        public static Bitmap Render(int size, Color accent)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                float k = size / 64f;
                g.ScaleTransform(k, k);

                // Outlines are thinner at small sizes or the face fills in.
                float lw = size <= 20 ? 1.4f : 2.1f;

                using (var fur = new SolidBrush(Fur))
                using (var pink = new SolidBrush(EarPink))
                using (var pen = new Pen(Ink, lw))
                {
                    pen.LineJoin = LineJoin.Round;

                    // Ears run long enough to root into the skull; any shorter and
                    // they read as floating detached above the head.
                    Ear(g, fur, pink, pen, 11f, 1f, 9f, 32f, -13f);
                    Ear(g, fur, pink, pen, 22f, 0f, 9f, 32f, 7f);

                    // head, sat left so the watch has room beside it
                    g.FillEllipse(fur, 2f, 25f, 34f, 31f);
                    g.DrawEllipse(pen, 2f, 25f, 34f, 31f);

                    // eye, kept modest so the watch stays the focal point
                    using (var eye = new SolidBrush(EyePink))
                        g.FillEllipse(eye, 12f, 33f, 9.5f, 9.5f);
                    using (var pupil = new SolidBrush(Ink))
                        g.FillEllipse(pupil, 13.9f, 34.9f, 5.7f, 5.7f);
                    if (size >= 32)
                    {
                        using (var glint = new SolidBrush(Color.White))
                            g.FillEllipse(glint, 15.1f, 36.1f, 2.1f, 2.1f);
                    }
                    g.DrawEllipse(pen, 12f, 33f, 9.5f, 9.5f);

                    // muzzle, kept inside the silhouette - further left it juts
                    // past the head outline and reads as an injury
                    using (var nose = new SolidBrush(EyePink))
                        g.FillEllipse(nose, 5.5f, 45f, 5.5f, 4f);
                }

                // Pocket watch, held low and to the right. It must NOT sit level
                // with the eye: at that height it reads as a second eye and the
                // rabbit just looks cross-eyed.
                using (var brass = new SolidBrush(Brass))
                using (var face = new SolidBrush(accent))
                using (var pen = new Pen(Ink, lw))
                using (var chain = new Pen(Brass, lw))
                {
                    chain.StartCap = LineCap.Round;
                    chain.EndCap = LineCap.Round;
                    g.DrawLine(chain, 51f, 21f, 51f, 29f);
                    g.FillEllipse(brass, 47f, 26f, 8f, 7f);   // crown
                    g.DrawEllipse(pen, 47f, 26f, 8f, 7f);

                    g.FillEllipse(brass, 38f, 32f, 25f, 25f); // case
                    g.FillEllipse(face, 41.5f, 35.5f, 18f, 18f);
                    g.DrawEllipse(pen, 38f, 32f, 25f, 25f);

                    using (var hands = new Pen(Ink, size <= 20 ? 1.7f : 2.3f))
                    {
                        hands.StartCap = LineCap.Round;
                        hands.EndCap = LineCap.Round;
                        g.DrawLine(hands, 50.5f, 44.5f, 50.5f, 38f);   // minute
                        g.DrawLine(hands, 50.5f, 44.5f, 55.5f, 46.5f); // hour
                    }
                }
            }
            return bmp;
        }

        private static void Ear(Graphics g, Brush fur, Brush pink, Pen pen,
                                float x, float y, float w, float h, float angle)
        {
            GraphicsState st = g.Save();
            g.TranslateTransform(x + w / 2f, y + h);
            g.RotateTransform(angle);
            g.TranslateTransform(-(x + w / 2f), -(y + h));

            g.FillEllipse(fur, x, y, w, h);
            g.DrawEllipse(pen, x, y, w, h);
            g.FillEllipse(pink, x + w * 0.27f, y + h * 0.16f, w * 0.46f, h * 0.62f);

            g.Restore(st);
        }

        public static Icon MakeIcon(int size, Color accent)
        {
            using (Bitmap b = Render(size, accent))
            {
                // Icon.FromHandle does not own the handle, but these live for the
                // life of the process, so there is nothing to leak.
                return Icon.FromHandle(b.GetHicon());
            }
        }

        private static readonly int[] IcoSizes = { 16, 24, 32, 48, 64, 128, 256 };

        public static void ExportSet(string dir)
        {
            Directory.CreateDirectory(dir);

            var set = new List<KeyValuePair<string, Color>>();
            set.Add(new KeyValuePair<string, Color>("rabbit-ok", Ok));
            set.Add(new KeyValuePair<string, Color>("rabbit-syncing", Busy));
            set.Add(new KeyValuePair<string, Color>("rabbit-warning", Warn));
            set.Add(new KeyValuePair<string, Color>("rabbit-error", Bad));

            foreach (KeyValuePair<string, Color> item in set)
            {
                WriteIco(Path.Combine(dir, item.Key + ".ico"), item.Value);
                foreach (int sz in new int[] { 16, 32, 64, 256 })
                {
                    using (Bitmap b = Render(sz, item.Value))
                        b.Save(Path.Combine(dir, item.Key + "-" + sz + ".png"),
                               System.Drawing.Imaging.ImageFormat.Png);
                }
                Engine.LogStatic("exported " + item.Key);
            }

            // Contact sheet so the whole set can be eyeballed at once.
            using (var sheet = new Bitmap(4 * 72, 96))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(246, 246, 248));
                for (int i = 0; i < set.Count; i++)
                {
                    using (Bitmap big = Render(64, set[i].Value))
                        g.DrawImage(big, i * 72 + 4, 4);
                    using (Bitmap small = Render(16, set[i].Value))
                        g.DrawImage(small, i * 72 + 28, 74);
                }
                sheet.Save(Path.Combine(dir, "contact-sheet.png"),
                           System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>Multi-resolution .ico with PNG-compressed entries.</summary>
        private static void WriteIco(string path, Color accent)
        {
            var pngs = new List<byte[]>();
            foreach (int sz in IcoSizes)
            {
                using (Bitmap b = Render(sz, accent))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // 1 == icon
                w.Write((ushort)IcoSizes.Length);

                int offset = 6 + 16 * IcoSizes.Length;
                for (int i = 0; i < IcoSizes.Length; i++)
                {
                    int sz = IcoSizes[i];
                    w.Write((byte)(sz >= 256 ? 0 : sz)); // 0 means 256
                    w.Write((byte)(sz >= 256 ? 0 : sz));
                    w.Write((byte)0);   // palette entries
                    w.Write((byte)0);   // reserved
                    w.Write((ushort)1); // colour planes
                    w.Write((ushort)32);// bits per pixel
                    w.Write(pngs[i].Length);
                    w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] p in pngs) w.Write(p);
            }
        }
    }

    // ====================================================================
    //  Background sync engine
    // ====================================================================
    internal enum SyncState { Starting, Ok, Retrying, NoPrivilege }

    internal sealed class Engine
    {
        public const double ThresholdSeconds = 2.0;
        private const int RetryMs = 10000;          // as asked: retry ~10s on failure
        private const int SettledMs = 60 * 60 * 1000; // re-check hourly once healthy
        private const long MaxLogBytes = 512 * 1024;

        private readonly object _gate = new object();
        private readonly List<string> _log = new List<string>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _worker;
        private volatile bool _stop;

        public SyncState State = SyncState.Starting;
        public double LastOffsetSeconds;
        public DateTime LastSuccessLocal = DateTime.MinValue;
        public int AgreeingServers;
        public int Attempts;
        public string LastMessage = "starting up";

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public static string LogPath
        {
            get { return Path.Combine(ExeDir, "timesync.log"); }
        }

        public void Start()
        {
            TrimLog();
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
        }

        public void TriggerNow()
        {
            _wake.Set();
        }

        public string[] Snapshot()
        {
            lock (_gate) return _log.ToArray();
        }

        private void Loop()
        {
            Log("tray app started (elevated: " + Clock.IsElevated() + ")");

            while (!_stop)
            {
                bool ok = Attempt();
                if (_stop) break;
                _wake.WaitOne(ok ? SettledMs : RetryMs);
            }
            Log("engine stopped");
        }

        private bool Attempt()
        {
            Attempts++;
            Log("--- attempt " + Attempts + " (clock reads " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ") ---");

            if (!Clock.EnableTimePrivilege())
            {
                State = SyncState.NoPrivilege;
                LastMessage = "no permission to set the clock - relaunch as administrator";
                Log("ERROR: SeSystemtimePrivilege unavailable (not elevated)");
                return false;
            }

            List<Sample> samples = Ntp.Gather(Log);
            DateTime atZero;
            int agreeing;

            if (!Ntp.TryConsensus(samples, out atZero, out agreeing))
            {
                State = SyncState.Retrying;
                LastMessage = "no server consensus (offline?) - retrying every 10s";
                Log("no consensus; retrying in 10s");
                return false;
            }

            AgreeingServers = agreeing;
            double offset = ((atZero + Ntp.Mono.Elapsed) - DateTime.UtcNow).TotalSeconds;
            LastOffsetSeconds = offset;

            if (Math.Abs(offset) <= ThresholdSeconds)
            {
                State = SyncState.Ok;
                LastSuccessLocal = DateTime.Now;
                LastMessage = string.Format("clock is good ({0:F3}s off, {1} servers)", offset, agreeing);
                Log(LastMessage);
                return true;
            }

            // Recompute at the last moment so logging delay does not make it stale.
            DateTime trueNow = atZero + Ntp.Mono.Elapsed;
            int err;
            if (Clock.Set(trueNow, out err))
            {
                State = SyncState.Ok;
                LastSuccessLocal = DateTime.Now;
                LastMessage = string.Format("corrected by {0:F3}s ({1} servers)", offset, agreeing);
                Log(string.Format("clock corrected by {0:F3}s -> now reads {1:yyyy-MM-dd HH:mm:ss}",
                    offset, DateTime.Now));
                return true;
            }

            State = err == 1314 ? SyncState.NoPrivilege : SyncState.Retrying;
            LastMessage = "SetSystemTime failed, win32=" + err;
            Log("ERROR: " + LastMessage);
            return false;
        }

        public void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (_gate)
            {
                _log.Add(line);
                if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
            }
            WriteLine(line);
        }

        public static void LogStatic(string msg)
        {
            WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg);
        }

        private static void WriteLine(string line)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* logging must never break the sync */ }
        }

        private static void TrimLog()
        {
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    string[] lines = File.ReadAllLines(LogPath);
                    var keep = new List<string>();
                    for (int i = Math.Max(0, lines.Length - 400); i < lines.Length; i++) keep.Add(lines[i]);
                    File.WriteAllLines(LogPath, keep.ToArray(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ====================================================================
    //  Tray
    // ====================================================================
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly Engine _engine = new Engine();
        private readonly System.Windows.Forms.Timer _uiTimer;
        private DebugForm _window;

        private readonly Icon _iconOk;
        private readonly Icon _iconWarn;
        private readonly Icon _iconBad;
        private SyncState _shown = (SyncState)(-1);

        public TrayApp(bool showAtStart)
        {
            _iconOk = RabbitArt.MakeIcon(32, RabbitArt.Ok);
            _iconWarn = RabbitArt.MakeIcon(32, RabbitArt.Warn);
            _iconBad = RabbitArt.MakeIcon(32, RabbitArt.Bad);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show details", null, delegate { ShowWindow(); });
            menu.Items.Add("Sync now", null, delegate { _engine.TriggerNow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { QuitApp(); });

            _icon = new NotifyIcon();
            _icon.Icon = _iconWarn;
            _icon.Text = "TimeSync - starting";
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
            };

            _engine.Start();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += delegate { Refresh(); };
            _uiTimer.Start();

            if (showAtStart) ShowWindow();
        }

        private void Refresh()
        {
            string tip = "TimeSync - " + _engine.LastMessage;
            if (tip.Length > 62) tip = tip.Substring(0, 62); // NotifyIcon.Text limit
            _icon.Text = tip;

            if (_engine.State != _shown)
            {
                _shown = _engine.State;
                if (_engine.State == SyncState.Ok) _icon.Icon = _iconOk;
                else if (_engine.State == SyncState.NoPrivilege) _icon.Icon = _iconBad;
                else _icon.Icon = _iconWarn;
            }

            if (_window != null && _window.Visible) _window.UpdateContent(_engine);
        }

        private void ShowWindow()
        {
            if (_window == null || _window.IsDisposed)
            {
                _window = new DebugForm(_engine, QuitApp);
            }
            _window.UpdateContent(_engine);
            _window.Show();
            if (_window.WindowState == FormWindowState.Minimized)
                _window.WindowState = FormWindowState.Normal;
            _window.BringToFront();
            _window.Activate();
        }

        private void QuitApp()
        {
            _engine.Log("quit requested from tray");
            _engine.Stop();
            _uiTimer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

    }

    // ====================================================================
    //  Debug window
    // ====================================================================
    internal sealed class DebugForm : Form
    {
        private readonly Engine _engine;
        private readonly Action _quit;
        private readonly TextBox _summary;
        private readonly TextBox _log;
        private readonly Button _syncBtn;
        private readonly Button _elevateBtn;

        public DebugForm(Engine engine, Action quit)
        {
            _engine = engine;
            _quit = quit;

            Text = "TimeSync";
            try { Icon = RabbitArt.MakeIcon(32, RabbitArt.Ok); }
            catch { /* window still works without an icon */ }
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 480);
            MinimumSize = new Size(480, 360);
            ShowInTaskbar = true;

            _summary = new TextBox();
            _summary.Multiline = true;
            _summary.ReadOnly = true;
            _summary.ScrollBars = ScrollBars.Vertical;
            _summary.Font = new Font(FontFamily.GenericMonospace, 9f);
            _summary.Dock = DockStyle.Top;
            _summary.Height = 150;
            _summary.BackColor = Color.White;

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Both;
            _log.WordWrap = false;
            _log.Font = new Font(FontFamily.GenericMonospace, 8.5f);
            _log.Dock = DockStyle.Fill;
            _log.BackColor = Color.White;

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.Height = 44;
            bar.Padding = new Padding(6);

            _syncBtn = new Button();
            _syncBtn.Text = "Sync now";
            _syncBtn.Width = 110;
            _syncBtn.Height = 30;
            _syncBtn.Click += delegate
            {
                _engine.Log("manual sync requested");
                _engine.TriggerNow();
            };

            _elevateBtn = new Button();
            _elevateBtn.Text = "Restart as admin";
            _elevateBtn.Width = 140;
            _elevateBtn.Height = 30;
            _elevateBtn.Visible = !Clock.IsElevated();
            _elevateBtn.Click += delegate { RestartElevated(); };

            var logBtn = new Button();
            logBtn.Text = "Open log";
            logBtn.Width = 100;
            logBtn.Height = 30;
            logBtn.Click += delegate
            {
                try { Process.Start("notepad.exe", Engine.LogPath); }
                catch { }
            };

            var hideBtn = new Button();
            hideBtn.Text = "Hide";
            hideBtn.Width = 90;
            hideBtn.Height = 30;
            hideBtn.Click += delegate { Hide(); };

            var quitBtn = new Button();
            quitBtn.Text = "Quit";
            quitBtn.Width = 90;
            quitBtn.Height = 30;
            quitBtn.Click += delegate { _quit(); };

            bar.Controls.Add(_syncBtn);
            bar.Controls.Add(_elevateBtn);
            bar.Controls.Add(logBtn);
            bar.Controls.Add(hideBtn);
            bar.Controls.Add(quitBtn);

            Controls.Add(_log);
            Controls.Add(bar);
            Controls.Add(_summary);
        }

        /// <summary>Closing the window only hides it - the app lives in the tray.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        public void UpdateContent(Engine engine)
        {
            var sb = new StringBuilder();
            sb.AppendLine("state         : " + engine.State + "   (" + engine.LastMessage + ")");
            sb.AppendLine("elevated      : " + (Clock.IsElevated()
                ? "yes"
                : "NO - cannot set the clock; use Restart as admin"));
            sb.AppendLine("system clock  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                          "  (" + TimeZoneInfo.Local.Id + ")");
            sb.AppendLine("last offset   : " + engine.LastOffsetSeconds.ToString("F3") + " s" +
                          (Math.Abs(engine.LastOffsetSeconds) > Engine.ThresholdSeconds
                              ? "  (beyond threshold)" : "  (within threshold)"));
            sb.AppendLine("agreeing srvs : " + engine.AgreeingServers + " of " + Ntp.Servers.Length +
                          "   (need " + Ntp.MinAgreeing + ")");
            sb.AppendLine("last success  : " + (engine.LastSuccessLocal == DateTime.MinValue
                ? "never this session"
                : engine.LastSuccessLocal.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine("attempts      : " + engine.Attempts);
            sb.AppendLine("servers       : " + string.Join(", ", Ntp.Servers));
            sb.AppendLine("log file      : " + Engine.LogPath);
            _summary.Text = sb.ToString();

            _elevateBtn.Visible = !Clock.IsElevated();

            string[] lines = engine.Snapshot();
            string joined = string.Join(Environment.NewLine, lines);
            if (_log.Text != joined)
            {
                _log.Text = joined;
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
        }

        private void RestartElevated()
        {
            try
            {
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location);
                psi.UseShellExecute = true;
                psi.Verb = "runas"; // triggers the UAC prompt
                Process.Start(psi);
                _quit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not relaunch elevated: " + ex.Message,
                    "TimeSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

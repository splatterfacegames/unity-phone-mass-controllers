using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Splatter.Pmc
{
    public sealed partial class PmcTunnel
    {
        static readonly Regex s_urlRegex = new Regex("https://([a-z0-9-]+)\\.trycloudflare\\.com", RegexOptions.Compiled);
        static readonly Regex s_connRegex = new Regex("connIndex=(\\d+)", RegexOptions.Compiled);
        static readonly Regex s_versionRegex = new Regex("cloudflared version (\\d+\\.\\d+(\\.\\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static bool IsWindows
        {
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        /// <summary>Kills [p], taking the whole tree where the runtime supports it. Unity's
        /// netstandard2.1 profile has no <c>Kill(entireProcessTree)</c> overload — cloudflared
        /// spawns no children, so a plain <c>Kill()</c> is equivalent there.</summary>
        internal static void KillTree(Process p)
        {
            if (p == null) return;
#if NETCOREAPP3_0_OR_GREATER
            try { p.Kill(true); }
            catch
            {
                try { p.Kill(); }
                catch { }
            }
#else
            try { p.Kill(); }
            catch { }
#endif
        }

        // --- process spawn ------------------------------------------------------------

        /// <summary>Spawns [bin] with [args], stderr+stdout redirected. Batch files go through cmd /c on
        /// Windows (CreateProcess can't exec .cmd/.bat directly).</summary>
        static Process Spawn(string bin, List<string> args)
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (IsWindows && IsBatch(bin))
            {
                psi.FileName = "cmd.exe";
                var sb = new StringBuilder("/d /s /c \"\"");
                sb.Append(bin).Append('"');
                foreach (var a in args)
                    sb.Append(" \"").Append((a ?? "").Replace("\"", "\"\"")).Append('"');
                sb.Append('"');
                psi.Arguments = sb.ToString();
            }
            else
            {
                psi.FileName = bin;
                foreach (var a in args)
                    psi.ArgumentList.Add(a ?? "");
            }
            return Process.Start(psi);
        }

        static bool IsBatch(string path)
        {
            var l = (path ?? "").ToLowerInvariant();
            return l.EndsWith(".cmd") || l.EndsWith(".bat");
        }

        // --- binary resolution -----------------------------------------------------------

        /// <summary>Returns a reason string when tunnels can't run on this platform, else "".
        /// Engine-free probe — the Unity glue additionally gates on Application.platform.</summary>
        public static string UnsupportedReason()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                // OSPlatform.FreeBSD only exists on .NET 7+; Create() keeps netstandard2.1 (Unity) working.
                RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
            {
                if (LooksMobile())
                    return "cloudflared can't run as a subprocess on this platform";
                return "";
            }
            return "cloudflared can't run as a subprocess on this platform";
        }

        static bool LooksMobile()
        {
            // Android shells export these; iOS devices have /var/mobile (unreachable on macOS/Linux).
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_ROOT")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANDROID_DATA")))
                return true;
            try
            {
                if (Directory.Exists("/var/mobile"))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>Executable file name on this OS (<c>cloudflared.exe</c> on Windows).</summary>
        public static string ExecutableName()
        {
            return IsWindows ? "cloudflared.exe" : "cloudflared";
        }

        /// <summary>Absolute path the downloader installs to under the default <see cref="TempDir"/>.</summary>
        public static string InstallPath()
        {
            return InstallPathFor(DefaultTempDir);
        }

        static string InstallPathFor(string tempDir)
        {
            return Path.Combine(tempDir, InstallDirName, ExecutableName());
        }

        /// <summary>Official release asset name for this OS/CPU, or "" if Cloudflare doesn't publish one.</summary>
        public static string AssetName()
        {
            var arch = RuntimeInformation.OSArchitecture;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                switch (arch)
                {
                    case Architecture.X64: return "cloudflared-windows-amd64.exe";
                    case Architecture.X86: return "cloudflared-windows-386.exe";
                    case Architecture.Arm64: return "cloudflared-windows-amd64.exe";
                    default: return "";
                }
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                switch (arch)
                {
                    case Architecture.X64: return "cloudflared-darwin-amd64.tgz";
                    case Architecture.Arm64: return "cloudflared-darwin-arm64.tgz";
                    default: return "";
                }
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (arch)
                {
                    case Architecture.X64: return "cloudflared-linux-amd64";
                    case Architecture.Arm64: return "cloudflared-linux-arm64";
                    case Architecture.Arm: return "cloudflared-linux-arm";
                    case Architecture.X86: return "cloudflared-linux-386";
                    default: return "";
                }
            }
            return "";
        }

        /// <summary>Finds cloudflared: [explicitPath], env <c>PMC_CLOUDFLARED</c>, <c>PATH</c>, then the
        /// downloaded copy under the default temp dir. Returns an absolute path, or "" when not found.</summary>
        public static string ResolveBinary(string explicitPath = "")
        {
            return ResolveBinary(explicitPath, DefaultTempDir);
        }

        internal static string ResolveBinary(string explicitPath, string tempDir)
        {
            if (!string.IsNullOrEmpty(explicitPath))
            {
                string p = null;
                try { p = Path.GetFullPath(explicitPath); }
                catch { }
                if (p != null && File.Exists(p))
                    return p;
                return SearchPath(explicitPath);
            }
            var env = Environment.GetEnvironmentVariable("PMC_CLOUDFLARED");
            if (!string.IsNullOrEmpty(env))
            {
                string p = null;
                try { p = Path.GetFullPath(env); }
                catch { }
                if (p != null && File.Exists(p))
                    return p;
            }
            var found = SearchPath(ExecutableName());
            if (found != "")
                return found;
            var local = InstallPathFor(tempDir);
            return File.Exists(local) ? local : "";
        }

        static string SearchPath(string exe)
        {
            if (exe.Contains("/") || exe.Contains("\\"))
                return "";
            var sep = IsWindows ? ';' : ':';
            var names = new List<string> { exe };
            if (IsWindows && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                names.Add(exe + ".exe");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries))
            {
                var d = dir.Trim().Trim('"');
                if (d == "")
                    continue;
                foreach (var n in names)
                {
                    try
                    {
                        var candidate = Path.Combine(d, n);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch { }
                }
            }
            return "";
        }

        // --- log parsing ------------------------------------------------------------------

        /// <summary>Extracts the public quick-tunnel URL from a cloudflared log line, or "".</summary>
        public static string ParseUrl(string line)
        {
            foreach (Match m in s_urlRegex.Matches(line))
            {
                if (m.Groups[1].Value != "api")
                    return m.Value;
            }
            return "";
        }

        internal static string ConnIndex(string line)
        {
            var m = s_connRegex.Match(line);
            return m.Success ? m.Groups[1].Value : "";
        }

        internal static bool IsQuicError(string line)
        {
            // "QuickTunnel" (the API object name in 429 errors) contains "quic" — strip it first.
            var l = line.ToLowerInvariant().Replace("quicktunnel", "");
            if (l.Contains("no recent network activity") || l.Contains("failed to create new quic connection"))
                return true;
            return (line.Contains(" ERR ") || line.StartsWith("ERR", StringComparison.Ordinal)) && l.Contains("quic");
        }

        /// <summary>Turns a cloudflared error line into an actionable hint (appended to the original line).</summary>
        public static string FriendlyError(string line)
        {
            var l = line.ToLowerInvariant();
            if (l.Contains("code: 1015") || l.Contains("429") || l.Contains("too many requests"))
                return "Cloudflare rate-limited the request (HTTP 429 / error 1015) — it usually succeeds on retry. " + line;
            if (l.Contains("no recent network activity") || l.Contains("failed to create new quic connection"))
                return "QUIC (UDP 7844) seems blocked on this network — forcing --protocol http2 helps. " + line;
            if (l.Contains("lookup api.trycloudflare.com") || (l.Contains("lookup ") && l.Contains("no such host")))
                return "DNS lookup failed — a DNS filter or the network may be blocking trycloudflare.com. " + line;
            return line;
        }

        // --- config isolation ---------------------------------------------------------------

        /// <summary>Directories searched for a default cloudflared config file (used when
        /// <see cref="ConfigDirs"/> is empty).</summary>
        public static List<string> DefaultConfigDirs()
        {
            var outp = new List<string>();
            if (IsWindows)
            {
                var home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrEmpty(home))
                    home = (Environment.GetEnvironmentVariable("HOMEDRIVE") ?? "") + (Environment.GetEnvironmentVariable("HOMEPATH") ?? "");
                if (!string.IsNullOrEmpty(home))
                    outp.Add(Path.Combine(home, ".cloudflared"));
            }
            else
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                {
                    outp.Add(Path.Combine(home, ".cloudflared"));
                    outp.Add(Path.Combine(home, ".cloudflare-warp"));
                    outp.Add(Path.Combine(home, "cloudflare-warp"));
                }
                outp.Add("/etc/cloudflared");
                outp.Add("/usr/local/etc/cloudflared");
            }
            return outp;
        }

        /// <summary>First existing config.yml/config.yaml in [dirs] (platform defaults when empty), else "".
        /// Cloudflare's docs say a default config.yml prevents quick tunnels from starting.</summary>
        public static string FindDefaultConfig(string[] dirs)
        {
            if (dirs == null || dirs.Length == 0)
                dirs = DefaultConfigDirs().ToArray();
            foreach (var d in dirs)
            {
                foreach (var n in new[] { "config.yml", "config.yaml" })
                {
                    try
                    {
                        var p = Path.Combine(d, n);
                        if (File.Exists(p))
                            return p;
                    }
                    catch { }
                }
            }
            return "";
        }

        // --- binary verification ------------------------------------------------------------

        /// <summary>The <c>--version</c> string of a cloudflared binary (e.g. "2025.8.1"), or "".</summary>
        public static string BinaryVersion(string path)
        {
            string outp;
            var code = RunBin(path, new[] { "--version" }, out outp);
            var text = outp.Trim();
            if (code != 0 || text.IndexOf("cloudflared", StringComparison.OrdinalIgnoreCase) < 0)
                return "";
            var m = s_versionRegex.Match(text);
            return m.Success ? m.Groups[1].Value : "";
        }

        /// <summary>True when [v] &gt;= [minV] (both dotted numbers).</summary>
        public static bool VersionAtLeast(string v, string minV)
        {
            var a = v.Split('.');
            var b = minV.Split('.');
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var x = i < a.Length ? ParseInt(a[i]) : 0;
                var y = i < b.Length ? ParseInt(b[i]) : 0;
                if (x != y)
                    return x > y;
            }
            return true;
        }

        static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        /// <summary>Checks a resolved cloudflared binary before running it: it must answer --version and be at
        /// least [minVersion] (when both parse), and — for the managed download when [signature] is on — pass
        /// Authenticode (Windows) or codesign (macOS) verification. A binary carrying a signature must be valid
        /// and issued to Cloudflare; an unsigned one falls back to the SHA-256 check. Returns "" when
        /// acceptable, else the rejection reason.</summary>
        public static string VerifyBinary(string path, string minVersion = "", bool signature = false)
        {
            var v = BinaryVersion(path);
            if (v == "")
                return "not a working cloudflared binary: " + path;
            if (minVersion != "" && !VersionAtLeast(v, minVersion))
                return "cloudflared " + v + " is older than the supported minimum " + minVersion;
            if (signature)
                return VerifySignatureOf(path);
            return "";
        }

        string CheckBinary(string bin)
        {
            lock (s_checkedBinsLock)
            {
                if (s_checkedBins.Contains(bin))
                    return "";
            }
            var why = VerifyBinary(bin, MinimumVersion, VerifySignature && bin == InstallPathFor(TempDir));
            if (why == "")
            {
                lock (s_checkedBinsLock)
                    s_checkedBins.Add(bin);
            }
            return why;
        }

        /// <summary>True when the managed download is older than <see cref="BinaryMaxAgeDays"/>.</summary>
        bool IsStale(string bin)
        {
            if (BinaryMaxAgeDays <= 0 || bin != InstallPathFor(TempDir))
                return false;
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(bin);
                return age.TotalSeconds > BinaryMaxAgeDays * 86400.0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>SHA-256 of a file as lowercase hex, or "" if it can't be read.</summary>
        public static string FileSha256(string path)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(f);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash)
                        sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "";
            }
        }

        // --- pid file + orphan reaping ---------------------------------------------------------

        void WritePidfile()
        {
            var p = Path.Combine(TempDir, PidFileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                var data = new JObject
                {
                    ["pid"] = _pid,
                    ["exe"] = _bin,
                    ["port"] = _port,
                    ["mode"] = Mode,
                    ["started"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                File.WriteAllText(p, data.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
        }

        void RemovePidfile(int expectPid)
        {
            var p = Path.Combine(TempDir, PidFileName);
            if (!File.Exists(p))
                return;
            // Another live tunnel may have overwritten it; only remove records that name us.
            JObject data = null;
            try { data = JObject.Parse(File.ReadAllText(p)); }
            catch { }
            if (data == null || (int?)data["pid"] == expectPid || (int?)data["pid"] == null)
            {
                try { File.Delete(p); }
                catch { }
            }
        }

        /// <summary>If the pid file records a still-running cloudflared from a dead host instance, kill it.
        /// Best-effort: a recycled pid whose command line doesn't mention cloudflared is left alone.</summary>
        void ReapStale()
        {
            var p = Path.Combine(TempDir, PidFileName);
            if (!File.Exists(p))
                return;
            JObject data = null;
            try { data = JObject.Parse(File.ReadAllText(p)); }
            catch { }
            if (data == null)
            {
                DeleteQuiet(p);
                return;
            }
            var pid = (int?)data["pid"] ?? -1;
            if (pid <= 0)
            {
                DeleteQuiet(p);
                return;
            }
            bool ownedHere;
            lock (s_livePidsLock)
                ownedHere = s_livePids.Contains(pid);
            if (pid == _pid || ownedHere)
                return; // a live tunnel in this process owns it
            if (!PidAlive(pid))
            {
                DeleteQuiet(p);
                return;
            }
            if (PidLooksLikeCloudflared(pid, (string)data["exe"] ?? ""))
            {
                Log(LogPrefix + " killing leftover cloudflared (pid " + pid + ") recorded in " + p);
                try { KillTree(Process.GetProcessById(pid)); }
                catch { }
            }
            else
            {
                Log(LogPrefix + " pid file names live pid " + pid + " that doesn't look like cloudflared; left alone");
            }
            DeleteQuiet(p);
        }

        static void DeleteQuiet(string p)
        {
            try { File.Delete(p); }
            catch { }
        }

        /// <summary>Is [pid] still running? Windows uses Process.GetProcessById; elsewhere `kill -0` via sh
        /// (EPERM counts as alive — better to keep a stranger's pid file than reap a live process), with a
        /// /proc fallback on Linux.</summary>
        internal static bool PidAlive(int pid)
        {
            if (IsWindows)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        return !p.HasExited;
                }
                catch
                {
                    return false;
                }
            }
            string outp;
            if (RunCapture("sh", new[] { "-c", "kill -0 " + pid + " 2>/dev/null" }, out outp) == 0)
                return true;
            // EPERM (alive, another user) vs ESRCH (gone): /proc exists on Linux; elsewhere err on "alive".
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return true;
            return Directory.Exists("/proc/" + pid);
        }

        /// <summary>Is [pid] plausibly the recorded cloudflared? Compares its command line / image name
        /// against "cloudflared" and the recorded exe path. False when it can't be verified — never kill a
        /// stranger.</summary>
        internal static bool PidLooksLikeCloudflared(int pid, string recordedExe)
        {
            var baseName = "";
            try
            {
                if (!string.IsNullOrEmpty(recordedExe))
                    baseName = Path.GetFileName(recordedExe).ToLowerInvariant();
            }
            catch { }
            var hay = PidCmdline(pid);
            if (hay == "")
                hay = PidImageName(pid);
            hay = hay.ToLowerInvariant();
            if (hay == "")
                return false;
            return hay.Contains("cloudflared") || (baseName != "" && hay.Contains(baseName));
        }

        internal static string PidCmdline(int pid)
        {
            string outp;
            if (IsWindows)
            {
                if (RunCapture("powershell", new[]
                    {
                        "-NoProfile", "-Command",
                        "(Get-CimInstance Win32_Process -Filter 'ProcessId=" + pid + "').CommandLine",
                    }, out outp) != 0)
                    return "";
            }
            else
            {
                if (RunCapture("ps", new[] { "-p", pid.ToString(), "-o", "args=" }, out outp) != 0)
                    return "";
            }
            return outp.Trim();
        }

        internal static string PidImageName(int pid)
        {
            string outp;
            if (IsWindows)
            {
                if (RunCapture("tasklist", new[] { "/FI", "PID eq " + pid, "/FO", "CSV", "/NH" }, out outp) != 0)
                    return "";
            }
            else
            {
                if (RunCapture("ps", new[] { "-p", pid.ToString(), "-o", "comm=" }, out outp) != 0)
                    return "";
            }
            return outp.Trim();
        }

        // --- small process helpers ----------------------------------------------------------------

        /// <summary>Runs [path] with [args] synchronously, collecting stdout+stderr. Batch files go through
        /// cmd /c on Windows.</summary>
        static int RunBin(string path, string[] args, out string output)
        {
            if (IsWindows && IsBatch(path))
            {
                // cmd /c wants the whole command wrapped in quotes when the path is quoted
                var sb = new StringBuilder("/d /s /c \"\"").Append(path).Append('"');
                foreach (var a in args)
                    sb.Append(" \"").Append((a ?? "").Replace("\"", "\"\"")).Append("\"");
                sb.Append('"');
                return RunCaptureArgs("cmd.exe", sb.ToString(), out output);
            }
            return RunCapture(path, args, out output);
        }

        static int RunCapture(string file, IList<string> args, out string output)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a ?? "");
            return RunPsi(psi, out output);
        }

        static int RunCaptureArgs(string file, string arguments, out string output)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return RunPsi(psi, out output);
        }

        static int RunPsi(ProcessStartInfo psi, out string output)
        {
            output = "";
            try
            {
                using (var p = Process.Start(psi))
                {
                    // Read both pipes concurrently so a chatty child can't deadlock on a full buffer.
                    var so = p.StandardOutput.ReadToEndAsync();
                    var se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(15000))
                    {
                        KillTree(p);
                        return -1;
                    }
                    // WaitForExit(ms) can return before the async pipe readers drain on Unix,
                    // which would report an empty output for fast-exiting children.
                    try { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { so, se }, 5000); }
                    catch { }
                    output = (SafeResult(so) + SafeResult(se)).Trim();
                    return p.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        static string SafeResult(System.Threading.Tasks.Task<string> t)
        {
            try { return t.IsCompleted ? t.Result : ""; }
            catch { return ""; }
        }
    }
}

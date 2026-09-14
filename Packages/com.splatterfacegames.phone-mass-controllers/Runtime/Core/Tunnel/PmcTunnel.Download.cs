using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Splatter.Pmc
{
    public sealed partial class PmcTunnel
    {
        static readonly HttpClient s_http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        static HttpClient SharedHttp
        {
            get { return s_http; }
        }

        CancellationTokenSource _dlCts;
        string _downloadTarget = "";
        string _downloadPart = "";
        bool _downloadThenStart;
        string _expectedSha = "";
        string _releaseTag = "";
        long _dlReceived;
        long _dlTotal = -1;

        /// <summary>
        /// Downloads cloudflared into <c>&lt;TempDir&gt;/bin</c> without starting a tunnel (ignores
        /// <see cref="AllowDownload"/>). Progress arrives as "downloading" states, the result as
        /// <see cref="DownloadFinished"/>.
        /// </summary>
        public void Download()
        {
            if (_dlCts != null)
                return;
            var why = UnsupportedReason();
            if (why != "")
            {
                var g = _gen;
                Post(g, () =>
                {
                    var h = DownloadFinished;
                    if (h != null)
                        h(false, why);
                });
                return;
            }
            _downloadThenStart = false;
            BeginDownload();
        }

        void BeginDownload()
        {
            var asset = AssetName();
            if (asset == "")
            {
                DownloadFailed("no official cloudflared build for " + RuntimePlatformDesc());
                return;
            }
            var dir = Path.Combine(TempDir, InstallDirName);
            try { Directory.CreateDirectory(dir); }
            catch { }
            _downloadTarget = Path.Combine(dir, asset.EndsWith(".tgz", StringComparison.Ordinal) ? asset : ExecutableName());
            _downloadPart = _downloadTarget + ".part";
            DeleteQuiet(_downloadPart);
            _expectedSha = "";
            _releaseTag = "";
            _dlReceived = 0;
            _dlTotal = -1;
            var gen = ++_gen;
            if (VerifyChecksum)
            {
                // Phase 1: ask the GitHub API for the latest release's asset URL and SHA-256 digest.
                SetState("downloading", "checking the latest release");
                var cts = new CancellationTokenSource();
                _dlCts = cts;
                var ct = cts.Token;
                Task.Run(async () =>
                {
                    try
                    {
                        using (var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        using (var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApi))
                        {
                            cts2.CancelAfter(TimeSpan.FromSeconds(60));
                            req.Headers.Accept.ParseAdd("application/vnd.github+json");
                            req.Headers.TryAddWithoutValidation("User-Agent", "unity-phone-mass-controllers");
                            using (var resp = await SharedHttp.SendAsync(req, cts2.Token).ConfigureAwait(false))
                            {
                                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                var code = (int)resp.StatusCode;
                                Post(gen, () => OnReleaseInfo(code, body, asset));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Post(gen, () => DownloadFailed("could not read the latest cloudflared release from the GitHub API (" + e.Message + "). Install cloudflared yourself or disable VerifyChecksum"));
                    }
                }, ct);
            }
            else
            {
                DownloadAsset(ReleaseBase + asset, asset, gen);
            }
        }

        void OnReleaseInfo(int code, string body, string asset)
        {
            _dlCts = null;
            if (code != 200)
            {
                DownloadFailed("could not read the latest cloudflared release from the GitHub API (HTTP " + code + "). Install cloudflared yourself or disable VerifyChecksum");
                return;
            }
            JObject info;
            try { info = JObject.Parse(body); }
            catch
            {
                DownloadFailed("unexpected GitHub API response");
                return;
            }
            var assets = info["assets"] as JArray;
            if (assets != null)
            {
                foreach (var a in assets)
                {
                    if ((string)a["name"] != asset)
                        continue;
                    var digest = (string)a["digest"] ?? "";
                    if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
                    {
                        DownloadFailed("the release lists no SHA-256 digest for " + asset);
                        return;
                    }
                    _expectedSha = digest.Substring("sha256:".Length).ToLowerInvariant();
                    _releaseTag = (string)info["tag_name"] ?? "";
                    // Refresh path: the installed binary already matches the latest release, so
                    // re-verify and reuse instead of downloading again.
                    if (!asset.EndsWith(".tgz", StringComparison.Ordinal) && File.Exists(_downloadTarget) && FileSha256(_downloadTarget) == _expectedSha)
                    {
                        var why = VerifyBinary(_downloadTarget, MinimumVersion, VerifySignature);
                        if (why == "")
                        {
                            Log(LogPrefix + " installed cloudflared still matches the latest release (" + _releaseTag + ")");
                            FinishInstall(_downloadTarget);
                            return;
                        }
                        Log(LogPrefix + " installed binary failed re-verification (" + why + "); re-downloading");
                    }
                    DownloadAsset((string)a["browser_download_url"] ?? (ReleaseBase + asset), asset, _gen);
                    return;
                }
            }
            DownloadFailed("latest cloudflared release has no asset named " + asset);
        }

        void DownloadAsset(string assetUrl, string asset, int gen)
        {
            SetState("downloading", "0 MB (" + asset + " " + _releaseTag + ")");
            var cts = new CancellationTokenSource();
            _dlCts = cts;
            var ct = cts.Token;
            var part = _downloadPart;
            Task.Run(async () =>
            {
                try
                {
                    using (var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    using (var resp = await SharedHttp.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cts2.Token).ConfigureAwait(false))
                    {
                        cts2.CancelAfter(TimeSpan.FromSeconds(300));
                        if (!resp.IsSuccessStatusCode)
                        {
                            var code = (int)resp.StatusCode;
                            Post(gen, () => DownloadFailed("download failed (HTTP " + code + ")"));
                            return;
                        }
                        var total = resp.Content.Headers.ContentLength ?? -1;
                        using (var inp = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var outp = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var buf = new byte[65536];
                            long got = 0;
                            long lastPost = 0;
                            int n;
                            while ((n = await inp.ReadAsync(buf, 0, buf.Length, cts2.Token).ConfigureAwait(false)) > 0)
                            {
                                await outp.WriteAsync(buf, 0, n, cts2.Token).ConfigureAwait(false);
                                got += n;
                                var now = NowMs;
                                if (now - lastPost >= 250)
                                {
                                    lastPost = now;
                                    var g = got;
                                    var tl = total;
                                    Post(gen, () =>
                                    {
                                        _dlReceived = g;
                                        _dlTotal = tl;
                                        SetState("downloading", ProgressText(g, tl));
                                    });
                                }
                            }
                            var fg = got;
                            var ft = total;
                            Post(gen, () =>
                            {
                                _dlReceived = fg;
                                _dlTotal = ft;
                            });
                        }
                        Post(gen, () => OnDownloadCompleted());
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Post(gen, () => DownloadFailed("download failed: " + e.Message));
                }
            }, ct);
        }

        static string ProgressText(long got, long total)
        {
            if (total > 0)
                return string.Format("{0:0.0} / {1:0.0} MB ({2}%)", got / 1048576.0, total / 1048576.0, (int)(100.0 * got / total));
            return string.Format("{0:0.0} MB", got / 1048576.0);
        }

        void OnDownloadCompleted()
        {
            _dlCts = null;
            if (_expectedSha != "")
            {
                var actual = FileSha256(_downloadPart);
                if (actual != _expectedSha)
                {
                    DeleteQuiet(_downloadPart);
                    DownloadFailed("checksum mismatch for the downloaded cloudflared (expected " + _expectedSha + ", got " + actual + ")");
                    return;
                }
                Log(LogPrefix + " SHA-256 verified " + actual);
            }
            DeleteQuiet(_downloadTarget);
            try
            {
                File.Move(_downloadPart, _downloadTarget);
            }
            catch (Exception e)
            {
                DownloadFailed("could not move download into place: " + e.Message);
                return;
            }
            var finalPath = InstallPathFor(TempDir);
            if (_downloadTarget.EndsWith(".tgz", StringComparison.Ordinal))
            {
                string outp;
                var code = RunCapture("tar", new[] { "-xzf", _downloadTarget, "-C", Path.GetDirectoryName(_downloadTarget) }, out outp);
                DeleteQuiet(_downloadTarget);
                if (code != 0 || !File.Exists(finalPath))
                {
                    DownloadFailed("could not extract cloudflared: " + outp);
                    return;
                }
            }
            if (!IsWindows)
            {
                string ignored;
                RunCapture("chmod", new[] { "+x", finalPath }, out ignored);
            }
            var why = VerifyBinary(finalPath, MinimumVersion, VerifySignature);
            if (why != "")
            {
                DeleteQuiet(finalPath);
                DownloadFailed("downloaded cloudflared rejected: " + why);
                return;
            }
            FinishInstall(finalPath);
        }

        void FinishInstall(string finalPath)
        {
            var v = BinaryVersion(finalPath);
            Log(v != "" ? "cloudflared version " + v : "cloudflared ready at " + finalPath);
            var h = DownloadFinished;
            if (h != null)
                h(true, finalPath);
            if (_downloadThenStart)
            {
                _downloadThenStart = false;
                Launch(finalPath);
            }
            else
            {
                SetState("stopped", "");
            }
        }

        void CancelDownload()
        {
            var c = _dlCts;
            _dlCts = null;
            if (c != null)
            {
                try { c.Cancel(); }
                catch { }
                c.Dispose();
            }
            if (_downloadPart != "" && File.Exists(_downloadPart))
                DeleteQuiet(_downloadPart);
        }

        void DownloadFailed(string reason)
        {
            _dlCts = null;
            var h = DownloadFinished;
            if (h != null)
                h(false, reason);
            _downloadThenStart = false;
            Fail(reason);
        }

        static string RuntimePlatformDesc()
        {
            var os = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
                : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
                : "unknown";
            return os + "/" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        }

        // --- code signature verification --------------------------------------------------------

        internal static string VerifySignatureOf(string path)
        {
            if (IsWindows)
            {
                string outp;
                var script = "$s = Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; '{0}|{1}' -f $s.Status, $s.SignerCertificate.Subject";
                if (RunCapture("powershell", new[] { "-NoProfile", "-Command", script }, out outp) != 0)
                    return ""; // no verifier available; the SHA-256 check is the floor
                var line = outp.Trim();
                if (line == "")
                    return "";
                var sep = line.IndexOf('|');
                var status = sep >= 0 ? line.Substring(0, sep) : line;
                var signer = sep >= 0 ? line.Substring(sep + 1) : "";
                if (status == "Valid")
                {
                    if (signer.Contains("Cloudflare"))
                        return "";
                    return "the downloaded cloudflared is signed, but not by Cloudflare (" + signer + ")";
                }
                if (status == "NotSigned")
                    return "";
                return "Authenticode check failed for the downloaded cloudflared: " + status;
            }
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string outp;
                if (RunCapture("codesign", new[] { "--verify", "--strict", path }, out outp) == 0)
                {
                    string ignored;
                    RunCapture("spctl", new[] { "-a", "-t", "execute", "-vv", path }, out ignored); // informational (notarization)
                    return "";
                }
                var text = outp.ToLowerInvariant();
                if (text.Contains("not signed") || text.Contains("code object is not signed"))
                    return "";
                return "codesign verification failed for the downloaded cloudflared";
            }
            return "";
        }
    }
}

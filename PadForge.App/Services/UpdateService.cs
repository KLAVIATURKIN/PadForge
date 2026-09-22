using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services
{
    /// <summary>
    /// What this exe is. The release version comes from SharedVersion.cs.
    /// A build made from a git checkout also carries the commit count and
    /// short hash (StampBuildIdentity in PadForge.App.csproj), which are the
    /// two numbers CI names a dev build by: "PadForge r3676@b500441". Every
    /// build between two releases reports the same version, so the count is
    /// the only way to tell an older dev build from a newer one (#457).
    /// </summary>
    internal static class BuildIdentity
    {
        public static Version Version { get; } =
            typeof(BuildIdentity).Assembly.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>The commit count, or 0 for a tree built without git.</summary>
        public static int BuildNumber { get; } = ParseNumber(Read("PadForgeBuildNumber"));

        /// <summary>The seven-character commit hash, or empty.</summary>
        public static string Commit { get; } = Read("PadForgeCommit");

        /// <summary>"4.5.2 (r3676@b500441)", or "4.5.2" without git.</summary>
        public static string Display =>
            BuildNumber > 0 && Commit.Length > 0
                ? $"{VersionText(Version)} (r{BuildNumber}@{Commit})"
                : VersionText(Version);

        /// <summary>Three parts, the way releases are tagged.</summary>
        internal static string VersionText(Version v) =>
            $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

        internal static int ParseNumber(string text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static string Read(string key) =>
            typeof(BuildIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
    }

    internal enum UpdateCheckOutcome
    {
        UpToDate,
        Available,
        /// <summary>The newer release carries no zip for this machine's
        /// processor, which happens when a dev build's ARM64 job fails.</summary>
        NoBuildForThisPc,
        RateLimited,
        Failed,
    }

    /// <summary>A newer build, and exactly which file to fetch for it.</summary>
    internal sealed record UpdateOffer(
        string DisplayVersion,
        bool IsPreRelease,
        int BuildNumber,
        Version Version,
        string ReleasePageUrl,
        string AssetName,
        string DownloadUrl,
        long Size,
        string Sha256)
    {
        /// <summary>The staging folder's name, one per offered build.</summary>
        public string StageKey => IsPreRelease
            ? "r" + BuildNumber.ToString(CultureInfo.InvariantCulture)
            : "v" + DisplayVersion;
    }

    /// <summary>A downloaded, verified exe, and the SHA-256 of the bytes
    /// that were written, so the file can be checked again right before it
    /// runs.</summary>
    internal sealed record StagedUpdate(string ExePath, string ExeSha256);

    /// <summary>The download's hash did not match the one GitHub published
    /// for it, or a staged exe changed after it was written. A bad download
    /// is deleted before this is thrown.</summary>
    internal sealed class UpdateVerificationException : Exception
    {
        public UpdateVerificationException() : base("The download did not match its published SHA-256.") { }
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        /// <summary>"sha256:&lt;hex&gt;", which GitHub computes on upload.</summary>
        [JsonPropertyName("digest")] public string Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
    }

    /// <summary>An update downloaded in the background by Install Updates
    /// Automatically, waiting for the next launch.</summary>
    internal sealed class PendingUpdate
    {
        public string DisplayVersion { get; set; }
        public bool IsPreRelease { get; set; }
        public int BuildNumber { get; set; }
        public string Version { get; set; }
        public string ExePath { get; set; }
        public string ExeSha256 { get; set; }
        /// <summary>Set before the installer is started. A launch that finds
        /// it set knows the last attempt never finished and does not try
        /// again, so a broken download cannot restart PadForge in a loop.</summary>
        public bool Attempted { get; set; }
    }

    /// <summary>
    /// In-app updates (#457): checks GitHub, downloads and verifies the build
    /// for this machine, and replaces the running exe.
    ///
    /// <para><b>Channels.</b> Releases come from <c>releases/latest</c>,
    /// which GitHub never answers with a pre-release. Pre-releases come from
    /// the rolling dev release CI rewrites on every push to the dev branch,
    /// tagged <c>latest-v{major}-dev</c> and titled
    /// <c>PadForge r{count}@{hash}</c> (.github/workflows/build.yml). The
    /// dev branch holds every release commit, so that build is never older
    /// than the newest release.</para>
    ///
    /// <para><b>Verification.</b> GitHub publishes a SHA-256 for every
    /// release asset over the same HTTPS API the check reads. The download
    /// is hashed as it streams and thrown away on a mismatch, and an asset
    /// with no digest, or a URL outside this repository's release
    /// downloads, is never offered. Only the zip's root PadForge.exe is
    /// extracted.</para>
    ///
    /// <para><b>Replacing a running exe.</b> Windows will not overwrite an
    /// exe while it runs. OpenTabletDriver moves the running files aside,
    /// which only works on the same volume and would leave a file beside
    /// PadForge.exe, where nothing but PadForge.xml and crash.log belongs.
    /// DS4Windows hands the swap to a helper that waits for the app to
    /// close, and OpenTabletDriver's elevated path runs its own binary with
    /// an update verb. PadForge does both: the downloaded exe is started
    /// from the temp folder with <c>--apply-update</c>, waits for this
    /// process to exit, copies itself over it and starts it again with the
    /// arguments it was launched with. PadForge always runs elevated, so the
    /// helper inherits the rights to write wherever PadForge lives.</para>
    /// </summary>
    internal static class UpdateService
    {
        internal const string Repo = "hifihedgehog/PadForge";
        internal const string ApplyUpdateSwitch = "--apply-update";
        /// <summary>Added to the relaunch so the new copy can say it was updated.</summary>
        internal const string UpdatedSwitch = "--updated";
        internal const string ExeName = "PadForge.exe";

        internal static string StableEndpoint => $"https://api.github.com/repos/{Repo}/releases/latest";

        internal static string DevEndpoint(Version current) =>
            $"https://api.github.com/repos/{Repo}/releases/tags/latest-v{current.Major}-dev";

        /// <summary>Beside the other download folders PadForge uses
        /// (PadForge_MidiServices), never beside the exe.</summary>
        internal static string StagingRoot => Path.Combine(Path.GetTempPath(), "PadForge_Update");

        internal static string PendingPath => Path.Combine(StagingRoot, "pending.json");

        private static readonly Regex DevTitle = new(@"^PadForge r(\d+)@([0-9a-f]{7,40})$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Lazy<HttpClient> LazyApi = new(() => CreateClient(TimeSpan.FromSeconds(15)));
        // A release zip is about 300 MB. The MIDI Services installer's
        // client allows ten minutes for 210 MB, so this one allows an hour.
        private static readonly Lazy<HttpClient> LazyDownload = new(() => CreateClient(TimeSpan.FromHours(1)));

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            // GitHub refuses API calls without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("PadForge", BuildIdentity.VersionText(BuildIdentity.Version)));
            return http;
        }

        // ─────────────────────────────────────────────
        //  Check
        // ─────────────────────────────────────────────

        internal static async Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)> CheckAsync(
            bool includePreReleases, CancellationToken ct)
        {
            try
            {
                string url = includePreReleases ? DevEndpoint(BuildIdentity.Version) : StableEndpoint;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await LazyApi.Value.SendAsync(request, ct).ConfigureAwait(false);
                if (IsRateLimited(response))
                    return (UpdateCheckOutcome.RateLimited, null, null);
                if (!response.IsSuccessStatusCode)
                    return (UpdateCheckOutcome.Failed, null,
                        "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));

                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(body, cancellationToken: ct)
                    .ConfigureAwait(false);
                var outcome = Evaluate(release, includePreReleases, BuildIdentity.Version,
                    BuildIdentity.BuildNumber, BuildIdentity.Commit,
                    PadForge.Engine.PlatformSupport.IsArm64Machine, out var offer);
                return (outcome, offer, outcome == UpdateCheckOutcome.Failed ? "unexpected release data" : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (UpdateCheckOutcome.Failed, null, ex.Message);
            }
        }

        /// <summary>GitHub answers an exhausted unauthenticated quota (60
        /// requests an hour per address) with 403 and a zero remaining
        /// count, or with 429.</summary>
        internal static bool IsRateLimited(HttpResponseMessage response)
        {
            if (response.StatusCode == (HttpStatusCode)429) return true;
            return response.StatusCode == HttpStatusCode.Forbidden
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
                && values.FirstOrDefault() == "0";
        }

        /// <summary>
        /// Decides whether <paramref name="release"/> is newer than the
        /// running build and, when it is, which asset to fetch. No I/O, so
        /// every branch is testable.
        /// </summary>
        internal static UpdateCheckOutcome Evaluate(GitHubRelease release, bool preReleaseChannel,
            Version currentVersion, int currentBuild, string currentCommit, bool arm64Machine,
            out UpdateOffer offer)
        {
            offer = null;
            if (release == null || release.Draft) return UpdateCheckOutcome.Failed;

            string display;
            int build = 0;
            Version version = null;
            bool newer;
            if (preReleaseChannel)
            {
                var m = DevTitle.Match(release.Name ?? string.Empty);
                if (!m.Success) return UpdateCheckOutcome.Failed;
                build = BuildIdentity.ParseNumber(m.Groups[1].Value);
                if (build <= 0) return UpdateCheckOutcome.Failed;
                string commit = m.Groups[2].Value.ToLowerInvariant();
                display = $"r{build} ({commit})";
                // A build with no count (made without git) cannot be placed,
                // so any published one counts as newer. The same count on a
                // different commit is a rebuilt history, and newer too.
                newer = currentBuild <= 0
                    || build > currentBuild
                    || (build == currentBuild && !SameCommit(commit, currentCommit));
            }
            else
            {
                version = ParseTag(release.TagName);
                if (version == null) return UpdateCheckOutcome.Failed;
                display = BuildIdentity.VersionText(version);
                newer = CompareVersions(version, currentVersion) > 0;
            }
            if (!newer) return UpdateCheckOutcome.UpToDate;

            var asset = SelectAsset(release.Assets, arm64Machine);
            if (asset == null) return UpdateCheckOutcome.NoBuildForThisPc;
            string sha = ParseSha256Digest(asset.Digest);
            if (sha == null || !IsTrustedDownloadUrl(asset.DownloadUrl)) return UpdateCheckOutcome.Failed;

            offer = new UpdateOffer(display, preReleaseChannel, build, version,
                release.HtmlUrl, asset.Name, asset.DownloadUrl, asset.Size, sha);
            return UpdateCheckOutcome.Available;
        }

        private static bool SameCommit(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

        /// <summary>"v4.5.2" or "4.5.2". A suffix such as "-rc1" is refused
        /// rather than guessed at.</summary>
        internal static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            return Version.TryParse(t, out var v) && v.Build >= 0 ? v : null;
        }

        /// <summary>Major, minor and patch. The fourth part is always 0 in
        /// SharedVersion.cs and never appears in a tag.</summary>
        internal static int CompareVersions(Version a, Version b)
        {
            int c = a.Major.CompareTo(b.Major);
            if (c != 0) return c;
            c = a.Minor.CompareTo(b.Minor);
            if (c != 0) return c;
            return Math.Max(a.Build, 0).CompareTo(Math.Max(b.Build, 0));
        }

        /// <summary>
        /// The two naming schemes in use: releases publish
        /// PadForge-v{version}-win-x64.zip and -win-arm64.zip, and the dev
        /// feed publishes PadForge.zip and PadForge-arm64.zip. The MACHINE
        /// picks the asset, as it does for the MIDI Services installer, so an
        /// x64 copy running under emulation on ARM64 moves to the native
        /// build.
        /// </summary>
        internal static GitHubAsset SelectAsset(IEnumerable<GitHubAsset> assets, bool arm64Machine)
        {
            foreach (var a in assets ?? Enumerable.Empty<GitHubAsset>())
            {
                if (a?.Name == null) continue;
                bool match = arm64Machine
                    ? a.Name.EndsWith("-win-arm64.zip", StringComparison.OrdinalIgnoreCase)
                        || a.Name.Equals("PadForge-arm64.zip", StringComparison.OrdinalIgnoreCase)
                    : a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)
                        || a.Name.Equals("PadForge.zip", StringComparison.OrdinalIgnoreCase);
                if (match) return a;
            }
            return null;
        }

        /// <summary>Lowercase hex from "sha256:&lt;64 hex&gt;", else null.</summary>
        internal static string ParseSha256Digest(string digest)
        {
            const string prefix = "sha256:";
            if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string hex = digest.Substring(prefix.Length).Trim();
            if (hex.Length != 64) return null;
            foreach (char c in hex)
                if (!Uri.IsHexDigit(c)) return null;
            return hex.ToLowerInvariant();
        }

        /// <summary>Only this repository's release downloads, over HTTPS.
        /// GitHub redirects them to its asset host, which HttpClient follows.</summary>
        internal static bool IsTrustedDownloadUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────
        //  Download
        // ─────────────────────────────────────────────

        /// <summary>
        /// Downloads the offer into its own staging folder, checks the
        /// SHA-256 as the bytes arrive, and extracts PadForge.exe. Progress
        /// is reported in whole percent.
        /// </summary>
        internal static async Task<StagedUpdate> DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken ct)
        {
            if (offer == null) throw new ArgumentNullException(nameof(offer));
            // The launch cleanup deletes every staging folder it does not
            // recognize, and a download started while it still runs would be
            // one of them.
            await _cleanup.ConfigureAwait(false);
            string dir = Path.Combine(StagingRoot, offer.StageKey);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "download.zip");
            try
            {
                using (var response = await LazyDownload.Value.GetAsync(offer.DownloadUrl,
                           HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? offer.Size;
                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = new FileStream(zip, FileMode.Create, FileAccess.Write,
                        FileShare.None, 81920, useAsync: true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long done = 0;
                    int lastPercent = -1;
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        if (total > 0)
                        {
                            int percent = (int)Math.Min(100, done * 100 / total);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                progress?.Report(percent);
                            }
                        }
                    }
                    string got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    if (got != offer.Sha256)
                        throw new UpdateVerificationException();
                }

                string exe = Path.Combine(dir, ExeName);
                string exeSha = ExtractExe(zip, exe);
                File.Delete(zip);
                return new StagedUpdate(exe, exeSha);
            }
            catch
            {
                TryDeleteDirectory(dir);
                throw;
            }
        }

        /// <summary>The zip's root PadForge.exe and nothing else. Returns the
        /// SHA-256 of the bytes written, hashed as they are written, so the
        /// hash describes what this process put there.</summary>
        internal static string ExtractExe(string zipPath, string exePath)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(ExeName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidDataException("The download has no " + ExeName + ".");
            using var source = entry.Open();
            using var target = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        // ─────────────────────────────────────────────
        //  Install at next launch
        // ─────────────────────────────────────────────

        internal static void WritePending(UpdateOffer offer, StagedUpdate staged)
        {
            WritePending(new PendingUpdate
            {
                DisplayVersion = offer.DisplayVersion,
                IsPreRelease = offer.IsPreRelease,
                BuildNumber = offer.BuildNumber,
                Version = offer.Version != null ? BuildIdentity.VersionText(offer.Version) : null,
                ExePath = staged.ExePath,
                ExeSha256 = staged.ExeSha256,
            });
        }

        private static void WritePending(PendingUpdate pending)
        {
            Directory.CreateDirectory(StagingRoot);
            File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending));
        }

        internal static PendingUpdate ReadPending()
        {
            try
            {
                return File.Exists(PendingPath)
                    ? JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        internal static void DeletePending()
        {
            try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
            catch { /* best effort, the next launch re-checks it */ }
        }

        /// <summary>Whether a staged update is newer than the running build.</summary>
        internal static bool IsNewerThan(PendingUpdate pending, Version currentVersion, int currentBuild)
        {
            if (pending == null) return false;
            if (pending.IsPreRelease)
                return pending.BuildNumber > 0 && (currentBuild <= 0 || pending.BuildNumber > currentBuild);
            var v = ParseTag(pending.Version);
            return v != null && CompareVersions(v, currentVersion) > 0;
        }

        /// <summary>A pending install that started and never finished, set
        /// by <see cref="TryStartPendingInstall"/> for the window to report.</summary>
        internal static string LastPendingFailure { get; private set; }

        /// <summary>
        /// Called at launch, after the single-instance check and before
        /// anything else starts. Returns true when the installer is running
        /// and this process has to exit at once.
        /// </summary>
        internal static bool TryStartPendingInstall(string[] args)
        {
            var pending = ReadPending();
            if (pending == null) return false;
            if (pending.Attempted)
            {
                LastPendingFailure = pending.DisplayVersion;
                DeletePending();
                return false;
            }
            try
            {
                // Installed by hand since, or cleaned out of the temp folder.
                // A file changed since it was written is refused by
                // StartInstaller, which checks it again before it runs.
                if (!IsNewerThan(pending, BuildIdentity.Version, BuildIdentity.BuildNumber)
                    || string.IsNullOrEmpty(pending.ExePath) || !File.Exists(pending.ExePath))
                {
                    DeletePending();
                    return false;
                }
                pending.Attempted = true;
                WritePending(pending);
                StartInstaller(new StagedUpdate(pending.ExePath, pending.ExeSha256), args);
                return true;
            }
            catch
            {
                LastPendingFailure = pending.DisplayVersion;
                DeletePending();
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Install
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts the downloaded exe as the installer for this one. The
        /// caller exits right after. <paramref name="relaunchArgs"/> are the
        /// arguments this process was started with, so a copy a launcher
        /// started with --profile comes back with it.
        ///
        /// <para>PadForge runs elevated and the file sits in the user's temp
        /// folder, which any program the user runs can write to. So the file
        /// is opened with writes and deletes denied, hashed again through
        /// that handle, and started while the handle still holds it. A file
        /// changed after it was written is refused.</para>
        /// </summary>
        internal static void StartInstaller(StagedUpdate staged, IEnumerable<string> relaunchArgs)
        {
            string target = Environment.ProcessPath
                ?? throw new InvalidOperationException("The running exe's path is unknown.");
            using var pin = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha = Convert.ToHexString(SHA256.HashData(pin)).ToLowerInvariant();
            if (!string.Equals(sha, staged.ExeSha256, StringComparison.Ordinal))
                throw new UpdateVerificationException();
            var psi = new ProcessStartInfo(staged.ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(staged.ExePath),
            };
            foreach (string a in BuildInstallerArgs(target, Environment.ProcessId, relaunchArgs))
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("The installer did not start.");
        }

        /// <summary><c>--apply-update &lt;target&gt; &lt;pid&gt; [relaunch args]</c>.
        /// A leftover --updated from the last update is not carried again.</summary>
        internal static List<string> BuildInstallerArgs(string target, int pid, IEnumerable<string> relaunchArgs)
        {
            var args = new List<string> { ApplyUpdateSwitch, target, pid.ToString(CultureInfo.InvariantCulture) };
            foreach (string a in relaunchArgs ?? Enumerable.Empty<string>())
                if (!string.Equals(a, UpdatedSwitch, StringComparison.OrdinalIgnoreCase))
                    args.Add(a);
            return args;
        }

        /// <summary>
        /// Runs in the downloaded exe when the old copy started it. Returns
        /// false for any other launch, which then starts PadForge as usual.
        /// </summary>
        internal static bool TryRunApplyMode(string[] args, Action<string> reportFailure)
        {
            if (args == null || args.Length < 3
                || !string.Equals(args[0], ApplyUpdateSwitch, StringComparison.Ordinal))
                return false;

            string target = args[1];
            int pid = BuildIdentity.ParseNumber(args[2]);
            var relaunch = args.Skip(3).ToList();
            string self = Environment.ProcessPath;

            string error = Apply(self, target,
                waitForOldCopy: () => WaitForExit(pid, Path.GetFileNameWithoutExtension(target), TimeSpan.FromMinutes(2)),
                copy: (from, to) => File.Copy(from, to, overwrite: true),
                attempts: 60, retryDelay: TimeSpan.FromMilliseconds(500));

            if (error == null)
            {
                DeletePending();
                relaunch.Add(UpdatedSwitch);
            }
            else
            {
                reportFailure?.Invoke(error);
            }
            // Start whatever copy is in place now, the new one or the old one,
            // unless the old one never closed and would refuse a second copy.
            if (error != OldCopyStillRunning)
                Launch(target, relaunch);
            return true;
        }

        /// <summary>The failure <see cref="Apply"/> reports when the old
        /// copy did not exit, compared by reference.</summary>
        internal static readonly string OldCopyStillRunning = "The running copy of PadForge did not close.";

        /// <summary>
        /// The swap itself: wait for the old copy to exit, then copy over it,
        /// retrying while antivirus or the loader still holds the file.
        /// Returns null on success, or the reason it failed.
        /// </summary>
        internal static string Apply(string source, string target, Func<bool> waitForOldCopy,
            Action<string, string> copy, int attempts, TimeSpan retryDelay)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return "The update was started without a source or a target.";
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return "The update cannot replace itself.";
            if (!waitForOldCopy())
                return OldCopyStillRunning;

            Exception last = null;
            for (int i = 0; i < Math.Max(1, attempts); i++)
            {
                try
                {
                    copy(source, target);
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(retryDelay);
                }
            }
            return last?.Message ?? "The file could not be replaced.";
        }

        /// <summary>A process ID can be reused once its process exits, so a
        /// process by that ID under another name is not the old copy.</summary>
        private static bool WaitForExit(int pid, string processName, TimeSpan timeout)
        {
            if (pid <= 0) return true;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    return true;
                return process.WaitForExit((int)timeout.TotalMilliseconds);
            }
            catch (ArgumentException)
            {
                return true; // already gone
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void Launch(string exe, IEnumerable<string> args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using var process = Process.Start(psi);
            }
            catch
            {
                /* nothing left to report to, the failure dialog already showed */
            }
        }

        // ─────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────

        /// <summary>
        /// Deletes staged downloads left by earlier updates, keeping a
        /// pending one. The installer that ran last may still be exiting, so
        /// a locked folder is tried again a few times before giving up until
        /// the next launch. Never runs inside the staging folder itself.
        /// </summary>
        private static Task _cleanup = Task.CompletedTask;

        internal static Task CleanupStagingAsync()
        {
            return _cleanup = Task.Run(async () =>
            {
                string root = StagingRoot;
                string self = Environment.ProcessPath ?? string.Empty;
                if (!Directory.Exists(root)
                    || self.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    string keep = ReadPending()?.ExePath;
                    string keepDir = keep != null ? Path.GetDirectoryName(keep) : null;
                    bool leftovers = false;
                    foreach (string dir in SafeDirectories(root))
                    {
                        if (keepDir != null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keepDir),
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TryDeleteDirectory(dir)) leftovers = true;
                    }
                    if (!leftovers) return;
                    await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            });
        }

        /// <summary>Deletes a staged update's folder, best effort.</summary>
        internal static void DiscardStaged(StagedUpdate staged)
        {
            string dir = staged?.ExePath != null ? Path.GetDirectoryName(staged.ExePath) : null;
            if (dir != null && dir.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(dir);
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try { return Directory.GetDirectories(root); }
            catch { return Array.Empty<string>(); }
        }

        private static bool TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

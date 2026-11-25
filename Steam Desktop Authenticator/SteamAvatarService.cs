using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    /// <summary>
    /// Fetches and caches Steam profile avatars for managed accounts via ProxyService,
    /// returning validated data: URLs so WebView2 renders real Steam profile pictures
    /// without bypassing the app's proxy configuration or leaking referrer headers.
    /// </summary>
    internal sealed class SteamAvatarService
    {
        private const ulong SteamId64Base = 76561197960265728UL;
        private const int MaxMetadataBytes = 256 * 1024;
        private const int MaxAvatarImageBytes = 256 * 1024;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan FailureRetryBackoff = TimeSpan.FromMinutes(10);
        private static readonly Regex AvatarMediumXmlRegex = new Regex(
            @"<avatarMedium>\s*(?:<!\[CDATA\[)?(https://[^<\]]+?)(?:\]\]>)?\s*</avatarMedium>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex AvatarFullXmlRegex = new Regex(
            @"<avatarFull>\s*(?:<!\[CDATA\[)?(https://[^<\]]+?)(?:\]\]>)?\s*</avatarFull>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex AvatarIconXmlRegex = new Regex(
            @"<avatarIcon>\s*(?:<!\[CDATA\[)?(https://[^<\]]+?)(?:\]\]>)?\s*</avatarIcon>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly string cacheDirectory;
        private readonly Func<string, int, CancellationToken, Task<byte[]>> downloadBytesAsync;
        private readonly Func<DateTime> utcNowProvider;
        internal Action BeforeInflightClaimHook;
        private readonly ConcurrentDictionary<ulong, CacheEntry> memoryCache = new ConcurrentDictionary<ulong, CacheEntry>();
        private readonly ConcurrentDictionary<ulong, DateTime> failedUntilUtc = new ConcurrentDictionary<ulong, DateTime>();
        private readonly ConcurrentDictionary<ulong, Lazy<Task<string>>> inflightFetches = new ConcurrentDictionary<ulong, Lazy<Task<string>>>();

        private sealed class CacheEntry
        {
            public string DataUrl { get; set; }
            public DateTime CachedAtUtc { get; set; }
        }

        public SteamAvatarService(string cacheDirectory = null)
            : this(cacheDirectory, null, null)
        {
        }

        internal SteamAvatarService(
            string cacheDirectory,
            Func<string, int, CancellationToken, Task<byte[]>> downloadBytesAsync,
            Func<DateTime> utcNowProvider = null)
        {
            this.cacheDirectory = !String.IsNullOrWhiteSpace(cacheDirectory)
                ? cacheDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Astro Steam Desktop Authenticator",
                    "AvatarCache");
            this.downloadBytesAsync = downloadBytesAsync ?? DownloadBytesAsync;
            this.utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        }

        public string TryGetCachedAvatarDataUrl(ulong steamId)
        {
            if (steamId < SteamId64Base)
                return null;

            if (memoryCache.TryGetValue(steamId, out CacheEntry entry) && !String.IsNullOrEmpty(entry?.DataUrl))
                return entry.DataUrl;

            string diskUrl = TryReadDiskCache(steamId, out DateTime cachedAtUtc);
            if (!String.IsNullOrEmpty(diskUrl))
            {
                memoryCache[steamId] = new CacheEntry { DataUrl = diskUrl, CachedAtUtc = cachedAtUtc };
                return diskUrl;
            }

            return null;
        }

        public bool IsCacheFresh(ulong steamId)
        {
            if (steamId < SteamId64Base)
                return true;

            DateTime now = utcNowProvider();
            if (!memoryCache.TryGetValue(steamId, out CacheEntry entry))
            {
                TryGetCachedAvatarDataUrl(steamId);
                memoryCache.TryGetValue(steamId, out entry);
            }

            if (entry != null &&
                !String.IsNullOrEmpty(entry.DataUrl) &&
                now - entry.CachedAtUtc < CacheTtl)
            {
                return true;
            }

            if (failedUntilUtc.TryGetValue(steamId, out DateTime retryAfter) && now < retryAfter)
                return true;

            return false;
        }

        public Task<string> GetOrFetchAvatarDataUrlAsync(ulong steamId, CancellationToken cancellationToken)
        {
            if (steamId < SteamId64Base)
                return Task.FromResult<string>(null);

            string cached = TryGetCachedAvatarDataUrl(steamId);
            if (!String.IsNullOrEmpty(cached) && IsCacheFresh(steamId))
                return Task.FromResult(cached);

            if (failedUntilUtc.TryGetValue(steamId, out DateTime retryAfter) && utcNowProvider() < retryAfter)
                return Task.FromResult(cached);

            BeforeInflightClaimHook?.Invoke();

            // Publish the Lazy entry into inflightFetches BEFORE invoking FetchAndCacheCoreAsync
            // so even a synchronous throw (e.g. blocked proxy) always cleans up its own entry,
            // and concurrent callers share a single execution.
            Lazy<Task<string>> candidate = null;
            candidate = new Lazy<Task<string>>(
                () => FetchAndCacheCoreAsync(steamId, cached, candidate, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<string>> active = inflightFetches.GetOrAdd(steamId, candidate);
            return active.Value;
        }

        private async Task<string> FetchAndCacheCoreAsync(
            ulong steamId,
            string fallbackCached,
            Lazy<Task<string>> ownerEntry,
            CancellationToken cancellationToken)
        {
            try
            {
                // Recheck cache freshness and failure backoff after winning the in-flight slot so an
                // overlapping caller preempted before GetOrAdd cannot trigger a duplicate network fetch.
                string freshCached = TryGetCachedAvatarDataUrl(steamId);
                if (!String.IsNullOrEmpty(freshCached) && IsCacheFresh(steamId))
                    return freshCached;

                if (failedUntilUtc.TryGetValue(steamId, out DateTime retryAfter) && utcNowProvider() < retryAfter)
                    return freshCached ?? fallbackCached;

                string avatarUrl = await ResolveAvatarUrlAsync(steamId, cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrEmpty(avatarUrl))
                {
                    failedUntilUtc[steamId] = utcNowProvider().Add(FailureRetryBackoff);
                    return fallbackCached;
                }

                byte[] imageBytes = await downloadBytesAsync(avatarUrl, MaxAvatarImageBytes, cancellationToken).ConfigureAwait(false);
                if (!TryValidateImageBytes(imageBytes, out string mimeType))
                {
                    failedUntilUtc[steamId] = utcNowProvider().Add(FailureRetryBackoff);
                    return fallbackCached;
                }

                string dataUrl = "data:" + mimeType + ";base64," + Convert.ToBase64String(imageBytes);
                DateTime now = utcNowProvider();
                memoryCache[steamId] = new CacheEntry { DataUrl = dataUrl, CachedAtUtc = now };
                failedUntilUtc.TryRemove(steamId, out _);
                TryWriteDiskCache(steamId, dataUrl);
                return dataUrl;
            }
            catch (OperationCanceledException)
            {
                return fallbackCached;
            }
            catch
            {
                failedUntilUtc[steamId] = utcNowProvider().Add(FailureRetryBackoff);
                return fallbackCached;
            }
            finally
            {
                inflightFetches.TryRemove(new System.Collections.Generic.KeyValuePair<ulong, Lazy<Task<string>>>(steamId, ownerEntry));
            }
        }

        private async Task<string> ResolveAvatarUrlAsync(ulong steamId, CancellationToken cancellationToken)
        {
            ulong accountId32 = steamId - SteamId64Base;
            if (accountId32 <= uint.MaxValue)
            {
                string miniUrl = "https://steamcommunity.com/miniprofile/" + accountId32.ToString(CultureInfo.InvariantCulture) + "/json";
                string miniJson = await DownloadTextAsync(miniUrl, 64 * 1024, cancellationToken).ConfigureAwait(false);
                string parsedFromMini = ExtractAvatarUrlFromMiniProfileJson(miniJson);
                if (!String.IsNullOrEmpty(parsedFromMini))
                    return parsedFromMini;
            }

            string xmlUrl = "https://steamcommunity.com/profiles/" + steamId.ToString(CultureInfo.InvariantCulture) + "/?xml=1";
            string xmlText = await DownloadTextAsync(xmlUrl, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
            return ExtractAvatarUrlFromProfileXml(xmlText);
        }

        internal static string ExtractAvatarUrlFromMiniProfileJson(string json)
        {
            if (String.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using (StringReader sr = new StringReader(json))
                using (JsonTextReader jr = new JsonTextReader(sr) { MaxDepth = 12, DateParseHandling = DateParseHandling.None })
                {
                    JObject obj = JObject.Load(jr);
                    string raw = obj.Value<string>("avatar_url");
                    return ConfirmationTypePresentation.SanitizeIconUrl(raw);
                }
            }
            catch
            {
                return null;
            }
        }

        internal static string ExtractAvatarUrlFromProfileXml(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml))
                return null;

            // Steam profile XML emits <avatarIcon> (32px) before <avatarMedium> (64px);
            // prefer medium, then full, then icon.
            Regex[] orderedTags = { AvatarMediumXmlRegex, AvatarFullXmlRegex, AvatarIconXmlRegex };
            foreach (Regex regex in orderedTags)
            {
                Match match = regex.Match(xml);
                if (!match.Success || match.Groups.Count < 2)
                    continue;

                string sanitized = ConfirmationTypePresentation.SanitizeIconUrl(match.Groups[1].Value);
                if (!String.IsNullOrEmpty(sanitized))
                    return sanitized;
            }

            return null;
        }

        internal static bool TryValidateImageDataUrl(string dataUrl)
        {
            if (String.IsNullOrWhiteSpace(dataUrl))
                return false;

            const string prefix = "data:";
            const string base64Marker = ";base64,";
            if (!dataUrl.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            int markerIndex = dataUrl.IndexOf(base64Marker, prefix.Length, StringComparison.Ordinal);
            if (markerIndex <= prefix.Length)
                return false;

            string declaredMime = dataUrl.Substring(prefix.Length, markerIndex - prefix.Length);
            if (!String.Equals(declaredMime, "image/png", StringComparison.Ordinal) &&
                !String.Equals(declaredMime, "image/jpeg", StringComparison.Ordinal) &&
                !String.Equals(declaredMime, "image/webp", StringComparison.Ordinal) &&
                !String.Equals(declaredMime, "image/gif", StringComparison.Ordinal))
            {
                return false;
            }

            string base64Payload = dataUrl.Substring(markerIndex + base64Marker.Length);
            if (String.IsNullOrWhiteSpace(base64Payload))
                return false;

            try
            {
                byte[] bytes = Convert.FromBase64String(base64Payload);
                return TryValidateImageBytes(bytes, out string actualMime) &&
                    String.Equals(declaredMime, actualMime, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static bool TryValidateImageBytes(byte[] bytes, out string mimeType)
        {
            mimeType = null;
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxAvatarImageBytes)
                return false;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                mimeType = "image/png";
                return true;
            }

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                mimeType = "image/jpeg";
                return true;
            }

            // WEBP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                mimeType = "image/webp";
                return true;
            }

            // GIF: GIF87a / GIF89a
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38 &&
                (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            {
                mimeType = "image/gif";
                return true;
            }

            return false;
        }

        private async Task<string> DownloadTextAsync(string url, int maxBytes, CancellationToken cancellationToken)
        {
            byte[] bytes = await downloadBytesAsync(url, maxBytes, cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
                return null;
            return Encoding.UTF8.GetString(bytes);
        }

        private static async Task<byte[]> DownloadBytesAsync(string url, int maxBytes, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
                using (ProxyService.ActiveHttpClientLease lease = ProxyService.AcquireActiveHttpClient())
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.UserAgent.ParseAdd("AstroSDA/1.3");
                    using (HttpResponseMessage response = await lease.Client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return null;

                        if (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength.Value > maxBytes)
                            return null;

                        using (Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false))
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[16384];
                            int read;
                            int total = 0;
                            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, timeoutCts.Token).ConfigureAwait(false)) > 0)
                            {
                                total += read;
                                if (total > maxBytes)
                                    return null;
                                buffer.Write(chunk, 0, read);
                            }

                            return buffer.ToArray();
                        }
                    }
                }
            }
        }

        private string GetCacheFilePath(ulong steamId)
        {
            return Path.Combine(cacheDirectory, steamId.ToString(CultureInfo.InvariantCulture) + ".url");
        }

        private string TryReadDiskCache(ulong steamId, out DateTime cachedAtUtc)
        {
            cachedAtUtc = DateTime.MinValue;
            try
            {
                DirectoryInfo dirInfo = new DirectoryInfo(cacheDirectory);
                if (!dirInfo.Exists || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    return null;

                string path = GetCacheFilePath(steamId);
                FileInfo info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length == 0 || info.Length > MaxAvatarImageBytes * 2)
                    return null;

                string content = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (!TryValidateImageDataUrl(content))
                    return null;

                cachedAtUtc = info.LastWriteTimeUtc;
                return content;
            }
            catch
            {
                return null;
            }
        }

        private void TryWriteDiskCache(ulong steamId, string dataUrl)
        {
            string temp = null;
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                DirectoryInfo dirInfo = new DirectoryInfo(cacheDirectory);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    return;

                string target = GetCacheFilePath(steamId);
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    return;

                temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, dataUrl, new UTF8Encoding(false));
                File.Move(temp, target, overwrite: true);
                temp = null;
            }
            catch
            {
                // Best-effort disk cache only.
            }
            finally
            {
                if (!String.IsNullOrEmpty(temp))
                {
                    try
                    {
                        if (File.Exists(temp))
                            File.Delete(temp);
                    }
                    catch
                    {
                        // Ignore cleanup errors for best-effort temp files.
                    }
                }
            }
        }
    }
}

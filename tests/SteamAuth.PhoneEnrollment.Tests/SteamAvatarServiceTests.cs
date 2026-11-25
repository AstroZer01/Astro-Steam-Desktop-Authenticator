using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamAvatarServiceTests
    {
        private static readonly byte[] ValidPngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        private static readonly string ValidPngDataUrl = "data:image/png;base64," + Convert.ToBase64String(ValidPngBytes);

        [Fact]
        public void ExtractAvatarUrlFromMiniProfileJson_AcceptsSteamCdnHttpsUrl()
        {
            const string json = "{\"avatar_url\":\"https://avatars.fastly.steamstatic.com/abc123_medium.jpg\",\"persona_name\":\"AstroUser\"}";

            string url = SteamAvatarService.ExtractAvatarUrlFromMiniProfileJson(json);

            Assert.Equal("https://avatars.fastly.steamstatic.com/abc123_medium.jpg", url);
        }

        [Theory]
        [InlineData("{\"avatar_url\":\"http://avatars.fastly.steamstatic.com/abc123.jpg\"}")]
        [InlineData("{\"avatar_url\":\"https://evil.example.com/avatar.png\"}")]
        [InlineData("{\"avatar_url\":\"javascript:alert(1)\"}")]
        [InlineData("not json at all")]
        [InlineData("")]
        public void ExtractAvatarUrlFromMiniProfileJson_RejectsUntrustedOrMalformedValues(string json)
        {
            string url = SteamAvatarService.ExtractAvatarUrlFromMiniProfileJson(json);

            Assert.Null(url);
        }

        [Fact]
        public void ExtractAvatarUrlFromProfileXml_ParsesCdataAndPrefersMediumOverIcon()
        {
            const string cdataXml = "<profile><avatarMedium><![CDATA[https://avatars.akamai.steamstatic.com/fedcba_medium.jpg]]></avatarMedium></profile>";
            const string plainXml = "<profile><avatarIcon>https://avatars.steamstatic.com/123456.jpg</avatarIcon></profile>";
            const string orderedXml =
                "<profile>" +
                "<avatarIcon><![CDATA[https://avatars.steamstatic.com/icon32.jpg]]></avatarIcon>" +
                "<avatarMedium><![CDATA[https://avatars.steamstatic.com/medium64.jpg]]></avatarMedium>" +
                "<avatarFull><![CDATA[https://avatars.steamstatic.com/full184.jpg]]></avatarFull>" +
                "</profile>";
            const string evilXml = "<profile><avatarMedium><![CDATA[https://untrusted.invalid/a.jpg]]></avatarMedium></profile>";

            Assert.Equal("https://avatars.akamai.steamstatic.com/fedcba_medium.jpg", SteamAvatarService.ExtractAvatarUrlFromProfileXml(cdataXml));
            Assert.Equal("https://avatars.steamstatic.com/123456.jpg", SteamAvatarService.ExtractAvatarUrlFromProfileXml(plainXml));
            Assert.Equal("https://avatars.steamstatic.com/medium64.jpg", SteamAvatarService.ExtractAvatarUrlFromProfileXml(orderedXml));
            Assert.Null(SteamAvatarService.ExtractAvatarUrlFromProfileXml(evilXml));
        }

        [Fact]
        public void TryValidateImageBytes_RecognizesValidImageHeadersAndRejectsHtmlOrOversized()
        {
            byte[] png = ValidPngBytes;
            byte[] jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
            byte[] webp = Encoding.ASCII.GetBytes("RIFF1234WEBP");
            byte[] gif = Encoding.ASCII.GetBytes("GIF89a000000");
            byte[] html = Encoding.ASCII.GetBytes("<html><body>nope</body></html>");
            byte[] oversized = new byte[(256 * 1024) + 16];
            Array.Copy(png, oversized, png.Length);

            Assert.True(SteamAvatarService.TryValidateImageBytes(png, out string pngMime));
            Assert.Equal("image/png", pngMime);

            Assert.True(SteamAvatarService.TryValidateImageBytes(jpg, out string jpgMime));
            Assert.Equal("image/jpeg", jpgMime);

            Assert.True(SteamAvatarService.TryValidateImageBytes(webp, out string webpMime));
            Assert.Equal("image/webp", webpMime);

            Assert.True(SteamAvatarService.TryValidateImageBytes(gif, out string gifMime));
            Assert.Equal("image/gif", gifMime);

            Assert.False(SteamAvatarService.TryValidateImageBytes(html, out _));
            Assert.False(SteamAvatarService.TryValidateImageBytes(oversized, out _));
            Assert.False(SteamAvatarService.TryValidateImageBytes(null, out _));
        }

        [Fact]
        public void TryGetCachedAvatarDataUrl_ReadsValidDiskCacheAndRejectsSvgOrMalformedEntries()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "astro-avatar-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                const ulong validSteamId = 76561198000001234UL;
                const ulong boundaryMinValid = 76561197960265728UL;
                const ulong boundaryInvalid = 76561197960265727UL;
                const ulong poisonedJsId = 76561198000005678UL;
                const ulong poisonedSvgId = 76561198000005679UL;
                const ulong poisonedMismatchId = 76561198000005680UL;
                const ulong expiredSteamId = 76561198000005681UL;

                string svgDataUrl = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"));
                byte[] jpgBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
                string mismatchedMimeDataUrl = "data:image/png;base64," + Convert.ToBase64String(jpgBytes);

                File.WriteAllText(Path.Combine(tempDir, validSteamId + ".url"), ValidPngDataUrl, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, boundaryMinValid + ".url"), ValidPngDataUrl, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, boundaryInvalid + ".url"), ValidPngDataUrl, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, poisonedJsId + ".url"), "javascript:alert(1)", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, poisonedSvgId + ".url"), svgDataUrl, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, poisonedMismatchId + ".url"), mismatchedMimeDataUrl, new UTF8Encoding(false));

                string expiredPath = Path.Combine(tempDir, expiredSteamId + ".url");
                File.WriteAllText(expiredPath, ValidPngDataUrl, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow.AddHours(-25));

                SteamAvatarService service = new SteamAvatarService(tempDir);

                // IsCacheFresh must hydrate disk cache even when called before TryGetCachedAvatarDataUrl.
                Assert.True(service.IsCacheFresh(validSteamId));
                Assert.Equal(ValidPngDataUrl, service.TryGetCachedAvatarDataUrl(validSteamId));
                Assert.Equal(ValidPngDataUrl, service.TryGetCachedAvatarDataUrl(boundaryMinValid));
                Assert.Null(service.TryGetCachedAvatarDataUrl(boundaryInvalid));
                Assert.Null(service.TryGetCachedAvatarDataUrl(poisonedJsId));
                Assert.Null(service.TryGetCachedAvatarDataUrl(poisonedSvgId));
                Assert.Null(service.TryGetCachedAvatarDataUrl(poisonedMismatchId));
                Assert.Null(service.TryGetCachedAvatarDataUrl(0));

                // Expired cache still returns fallback data URL, but IsCacheFresh reports false.
                Assert.False(service.IsCacheFresh(expiredSteamId));
                Assert.Equal(ValidPngDataUrl, service.TryGetCachedAvatarDataUrl(expiredSteamId));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task GetOrFetchAvatarDataUrlAsync_RecoversAfterSynchronousProxyFailureAndBackoff()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "astro-avatar-sync-throw-" + Guid.NewGuid().ToString("N"));
            try
            {
                const ulong steamId = 76561198000009001UL;
                DateTime now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
                bool failSynchronously = true;
                int downloadCalls = 0;

                SteamAvatarService service = new SteamAvatarService(
                    tempDir,
                    (url, maxBytes, ct) =>
                    {
                        Interlocked.Increment(ref downloadCalls);
                        if (failSynchronously)
                            throw new InvalidOperationException("Proxy blocks Steam networking");

                        if (url.Contains("/miniprofile/", StringComparison.Ordinal))
                            return Task.FromResult(Encoding.UTF8.GetBytes("{\"avatar_url\":\"https://avatars.fastly.steamstatic.com/recovered_medium.png\"}"));

                        return Task.FromResult(ValidPngBytes);
                    },
                    () => now);

                // First call throws synchronously before any await (simulating AcquireActiveHttpClient failure).
                string firstResult = await service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None);
                Assert.Null(firstResult);
                Assert.Equal(1, downloadCalls);

                // While within backoff window, no retry occurs.
                now = now.AddMinutes(5);
                string duringBackoff = await service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None);
                Assert.Null(duringBackoff);
                Assert.Equal(1, downloadCalls);

                // After proxy recovers and backoff expires, the service must not be stuck on the failed task.
                failSynchronously = false;
                now = now.AddMinutes(10);
                string recovered = await service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None);
                Assert.Equal(ValidPngDataUrl, recovered);
                Assert.Equal(3, downloadCalls); // 1 failed + 1 miniprofile + 1 image download
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task GetOrFetchAvatarDataUrlAsync_DeduplicatesConcurrentRequestsForSameSteamId()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "astro-avatar-concurrent-" + Guid.NewGuid().ToString("N"));
            try
            {
                const ulong steamId = 76561198000009002UL;
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int miniProfileCalls = 0;
                int imageCalls = 0;

                SteamAvatarService service = new SteamAvatarService(
                    tempDir,
                    async (url, maxBytes, ct) =>
                    {
                        if (url.Contains("/miniprofile/", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref miniProfileCalls);
                            await gate.Task.ConfigureAwait(false);
                            return Encoding.UTF8.GetBytes("{\"avatar_url\":\"https://avatars.fastly.steamstatic.com/concurrent_medium.png\"}");
                        }

                        Interlocked.Increment(ref imageCalls);
                        return ValidPngBytes;
                    });

                Task<string>[] callers = new Task<string>[8];
                for (int i = 0; i < callers.Length; i++)
                {
                    callers[i] = Task.Run(() => service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None));
                }

                gate.SetResult(true);
                string[] results = await Task.WhenAll(callers);

                Assert.Equal(1, miniProfileCalls);
                Assert.Equal(1, imageCalls);
                foreach (string result in results)
                    Assert.Equal(ValidPngDataUrl, result);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task GetOrFetchAvatarDataUrlAsync_DoesNotRefetchWhenPreemptedBeforeInflightClaim()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "astro-avatar-toctou-" + Guid.NewGuid().ToString("N"));
            try
            {
                const ulong steamId = 76561198000009003UL;
                int miniProfileCalls = 0;
                int imageCalls = 0;
                int hookEntered = 0;
                var caller1PausedBeforeClaim = new ManualResetEventSlim(false);
                var allowCaller1ToClaim = new ManualResetEventSlim(false);

                SteamAvatarService service = new SteamAvatarService(
                    tempDir,
                    (url, maxBytes, ct) =>
                    {
                        if (url.Contains("/miniprofile/", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref miniProfileCalls);
                            return Task.FromResult(Encoding.UTF8.GetBytes("{\"avatar_url\":\"https://avatars.fastly.steamstatic.com/toctou_medium.png\"}"));
                        }

                        Interlocked.Increment(ref imageCalls);
                        return Task.FromResult(ValidPngBytes);
                    });

                service.BeforeInflightClaimHook = () =>
                {
                    if (Interlocked.Increment(ref hookEntered) == 1)
                    {
                        caller1PausedBeforeClaim.Set();
                        allowCaller1ToClaim.Wait(TimeSpan.FromSeconds(5));
                    }
                };

                Task<string> caller1 = Task.Run(() => service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None));
                Assert.True(caller1PausedBeforeClaim.Wait(TimeSpan.FromSeconds(5)));

                // Caller 2 runs to completion (fetching and populating cache, then removing its inflight entry)
                // while Caller 1 is paused between its initial cache check and inflightFetches.GetOrAdd.
                string result2 = await service.GetOrFetchAvatarDataUrlAsync(steamId, CancellationToken.None);
                allowCaller1ToClaim.Set();
                string result1 = await caller1;

                Assert.Equal(ValidPngDataUrl, result1);
                Assert.Equal(ValidPngDataUrl, result2);
                Assert.Equal(1, miniProfileCalls);
                Assert.Equal(1, imageCalls);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}

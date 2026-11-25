using System;
using System.IO;
using System.Text;
using Steam_Desktop_Authenticator;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    public sealed class SteamAvatarServiceTests
    {
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
        public void ExtractAvatarUrlFromProfileXml_ParsesCdataAndPlainSteamCdnUrls()
        {
            const string cdataXml = "<profile><avatarMedium><![CDATA[https://avatars.akamai.steamstatic.com/fedcba_medium.jpg]]></avatarMedium></profile>";
            const string plainXml = "<profile><avatarIcon>https://avatars.steamstatic.com/123456.jpg</avatarIcon></profile>";
            const string evilXml = "<profile><avatarMedium><![CDATA[https://untrusted.invalid/a.jpg]]></avatarMedium></profile>";

            Assert.Equal("https://avatars.akamai.steamstatic.com/fedcba_medium.jpg", SteamAvatarService.ExtractAvatarUrlFromProfileXml(cdataXml));
            Assert.Equal("https://avatars.steamstatic.com/123456.jpg", SteamAvatarService.ExtractAvatarUrlFromProfileXml(plainXml));
            Assert.Null(SteamAvatarService.ExtractAvatarUrlFromProfileXml(evilXml));
        }

        [Fact]
        public void TryValidateImageBytes_RecognizesValidImageHeadersAndRejectsHtmlOrOversized()
        {
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
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
        public void TryGetCachedAvatarDataUrl_ReadsValidDiskCacheAndRejectsInvalidEntries()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "astro-avatar-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                const ulong validSteamId = 76561198000001234UL;
                const ulong poisonedSteamId = 76561198000005678UL;
                const string validDataUrl = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB";

                File.WriteAllText(Path.Combine(tempDir, validSteamId + ".url"), validDataUrl, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempDir, poisonedSteamId + ".url"), "javascript:alert(1)", new UTF8Encoding(false));

                SteamAvatarService service = new SteamAvatarService(tempDir);

                Assert.Equal(validDataUrl, service.TryGetCachedAvatarDataUrl(validSteamId));
                Assert.True(service.IsCacheFresh(validSteamId));
                Assert.Null(service.TryGetCachedAvatarDataUrl(poisonedSteamId));
                Assert.Null(service.TryGetCachedAvatarDataUrl(0));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}

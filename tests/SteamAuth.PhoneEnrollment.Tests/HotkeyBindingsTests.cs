using Newtonsoft.Json.Linq;
using Steam_Desktop_Authenticator;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace SteamAuth.PhoneEnrollment.Tests
{
    [Collection("Manifest storage")]
    public sealed class HotkeyBindingsTests : IDisposable
    {
        private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "asda-hotkey-tests", Guid.NewGuid().ToString("N"));
        private readonly string previousDataDirectory;

        public HotkeyBindingsTests()
        {
            previousDataDirectory = Environment.GetEnvironmentVariable("ASDA_DATA_DIRECTORY");
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", dataDirectory);
        }

        [Fact]
        public void DefaultQrHotkeys_MatchRightCtrlAndEscape()
        {
            Assert.Equal(0xA3, HotkeyBindings.DefaultQrScanVirtualKey);
            Assert.Equal(0x1B, HotkeyBindings.DefaultQrCancelVirtualKey);
            Assert.Equal("Right Ctrl", HotkeyBindings.GetDisplayName(HotkeyBindings.DefaultQrScanVirtualKey));
            Assert.Equal("Esc", HotkeyBindings.GetDisplayName(HotkeyBindings.DefaultQrCancelVirtualKey));
        }

        [Theory]
        [InlineData(0x78, 0x79)] // F9, F10
        [InlineData(0x1B, 0xA3)] // Esc, Right Ctrl (swapped)
        [InlineData(0x20, 0x08)] // Space, Backspace
        [InlineData(0xA2, 0xA4)] // Left Ctrl, Left Alt
        public void TryValidateQrHotkeys_AcceptsDistinctSupportedKeys(int scanVk, int cancelVk)
        {
            Assert.True(HotkeyBindings.TryValidateQrHotkeys(scanVk, cancelVk, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void TryValidateQrHotkeys_RejectsDuplicateKeys()
        {
            Assert.False(HotkeyBindings.TryValidateQrHotkeys(0x78, 0x78, out string error));
            Assert.Contains("different", error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0x01, 0x1B)] // Left mouse
        [InlineData(0x02, 0x1B)] // Right mouse
        [InlineData(0x10, 0x1B)] // Ambiguous VK_SHIFT
        [InlineData(0x11, 0x1B)] // Ambiguous VK_CONTROL
        [InlineData(0x5B, 0x1B)] // Left Win
        [InlineData(0xA3, 0x5C)] // Right Win
        [InlineData(0xA3, 0x5D)] // Apps key
        [InlineData(0x00, 0x1B)]
        [InlineData(-1, 0x1B)]
        [InlineData(0xA3, 999)]
        public void TryValidateQrHotkeys_RejectsUnsupportedVirtualKeys(int scanVk, int cancelVk)
        {
            Assert.False(HotkeyBindings.TryValidateQrHotkeys(scanVk, cancelVk, out string error));
            Assert.False(String.IsNullOrWhiteSpace(error));
        }

        [Theory]
        [InlineData(0x01, 0x79, 0xA3, 0x79)] // Invalid scan -> default Right Ctrl
        [InlineData(0x78, 0x01, 0x78, 0x1B)] // Invalid cancel -> default Esc
        [InlineData(0x78, 0x78, 0x78, 0x1B)] // Duplicate non-Esc -> cancel falls back to Esc
        [InlineData(0x1B, 0x1B, 0xA3, 0x1B)] // Duplicate Esc -> defaults
        [InlineData(0x00, 0x00, 0xA3, 0x1B)] // Both invalid -> defaults
        public void NormalizeQrHotkeys_RepairsInvalidAndDuplicateValues(int scanVk, int cancelVk, int expectedScan, int expectedCancel)
        {
            (int normalizedScan, int normalizedCancel) = HotkeyBindings.NormalizeQrHotkeys(scanVk, cancelVk);
            Assert.Equal(expectedScan, normalizedScan);
            Assert.Equal(expectedCancel, normalizedCancel);
        }

        [Fact]
        public void Manifest_PersistsAndNormalizesCustomQrHotkeysAcrossReload()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            Assert.Equal(HotkeyBindings.DefaultQrScanVirtualKey, manifest.QrScanHotkey);
            Assert.Equal(HotkeyBindings.DefaultQrCancelVirtualKey, manifest.QrCancelHotkey);

            StorageResult saveResult = manifest.SaveSettingsWithResult(staged =>
            {
                staged.QrScanHotkey = 0x77;   // F8
                staged.QrCancelHotkey = 0x08; // Backspace
            });
            Assert.True(saveResult.Succeeded, saveResult.UserMessage);

            Manifest reloaded = Manifest.GetManifest(forceLoad: true);
            Assert.Equal(0x77, reloaded.QrScanHotkey);
            Assert.Equal(0x08, reloaded.QrCancelHotkey);

            // Corrupt/duplicate hotkey entries in manifest.json are normalized safely on load.
            string manifestPath = Path.Combine(Manifest.GetExecutableDir(), "maFiles", "manifest.json");
            JObject json = JObject.Parse(File.ReadAllText(manifestPath));
            json["qr_scan_hotkey"] = 0x78;
            json["qr_cancel_hotkey"] = 0x78;
            File.WriteAllText(manifestPath, json.ToString());

            Manifest normalized = Manifest.GetManifest(forceLoad: true);
            Assert.Equal(0x78, normalized.QrScanHotkey);
            Assert.Equal(HotkeyBindings.DefaultQrCancelVirtualKey, normalized.QrCancelHotkey);

            // Legacy manifests missing hotkey keys default cleanly.
            json.Remove("qr_scan_hotkey");
            json.Remove("qr_cancel_hotkey");
            File.WriteAllText(manifestPath, json.ToString());

            Manifest legacy = Manifest.GetManifest(forceLoad: true);
            Assert.Equal(HotkeyBindings.DefaultQrScanVirtualKey, legacy.QrScanHotkey);
            Assert.Equal(HotkeyBindings.DefaultQrCancelVirtualKey, legacy.QrCancelHotkey);
        }

        [Fact]
        public async Task MainForm_SaveSettingsAsync_PersistsValidQrHotkeysAndRejectsInvalidOrDuplicateBinds()
        {
            Manifest manifest = Manifest.GenerateNewManifest(false);
            using (MainForm form = new MainForm(manifest, new UpdatePreferenceRevisionTracker(), null, _ => { }))
            {
                await form.SaveSettingsAsync(CreateSettingsPayload(qrScanHotkey: 0x78, qrCancelHotkey: 0x08));
                Assert.Equal(0x78, manifest.QrScanHotkey);
                Assert.Equal(0x08, manifest.QrCancelHotkey);

                Manifest persisted = Manifest.GetManifest(forceLoad: true);
                Assert.Equal(0x78, persisted.QrScanHotkey);
                Assert.Equal(0x08, persisted.QrCancelHotkey);

                // Duplicate keys must be rejected without mutating saved settings.
                await form.SaveSettingsAsync(CreateSettingsPayload(qrScanHotkey: 0x79, qrCancelHotkey: 0x79));
                Assert.Equal(0x78, manifest.QrScanHotkey);
                Assert.Equal(0x08, manifest.QrCancelHotkey);

                // Unsupported key must be rejected without mutating saved settings.
                await form.SaveSettingsAsync(CreateSettingsPayload(qrScanHotkey: 0x01, qrCancelHotkey: 0x1B));
                Assert.Equal(0x78, manifest.QrScanHotkey);
                Assert.Equal(0x08, manifest.QrCancelHotkey);

                // Omitted hotkey properties preserve the existing saved bindings.
                await form.SaveSettingsAsync(CreateSettingsPayload(qrScanHotkey: null, qrCancelHotkey: null));
                Assert.Equal(0x78, manifest.QrScanHotkey);
                Assert.Equal(0x08, manifest.QrCancelHotkey);
            }
        }

        [Fact]
        public void QrScanOverlayForm_SetHotkeyLabels_UpdatesLabelsAndFallsBackOnBlank()
        {
            using (QrScanOverlayForm overlay = new QrScanOverlayForm())
            {
                Assert.Equal("Right Ctrl", overlay.ScanKeyLabel);
                Assert.Equal("Esc", overlay.CancelKeyLabel);

                overlay.SetHotkeyLabels("F9", "Backspace");
                Assert.Equal("F9", overlay.ScanKeyLabel);
                Assert.Equal("Backspace", overlay.CancelKeyLabel);

                overlay.SetHotkeyLabels("   ", null);
                Assert.Equal("Right Ctrl", overlay.ScanKeyLabel);
                Assert.Equal("Esc", overlay.CancelKeyLabel);
            }
        }

        private static JObject CreateSettingsPayload(int? qrScanHotkey, int? qrCancelHotkey)
        {
            JObject payload = new JObject
            {
                ["saveContext"] = String.Empty,
                ["tradeConfirmationCustomIntervalEnabled"] = false,
                ["tradeConfirmationCheckInterval"] = 15,
                ["autoConfirmMarket"] = false,
                ["autoConfirmTrades"] = false,
                ["minimizeToTray"] = false,
                ["checkForUpdates"] = false,
                ["diagnosticErrorLoggingEnabled"] = false,
                ["loginActionMonitoringEnabled"] = false,
                ["loginActionMode"] = "manual",
                ["loginActionAutoAllowIpEnabled"] = false,
                ["loginActionAutoAllowCurrentDeviceIp"] = false,
                ["loginActionAutoAllowIp"] = String.Empty,
                ["proxyEnabled"] = false,
                ["proxyScheme"] = "http",
                ["proxyHost"] = String.Empty,
                ["proxyPort"] = 0,
                ["proxyUsername"] = String.Empty,
                ["proxyPasswordAction"] = "keep",
                ["proxyPassword"] = String.Empty
            };

            if (qrScanHotkey.HasValue)
                payload["qrScanHotkey"] = qrScanHotkey.Value;
            if (qrCancelHotkey.HasValue)
                payload["qrCancelHotkey"] = qrCancelHotkey.Value;

            return payload;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASDA_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, true);
        }
    }
}

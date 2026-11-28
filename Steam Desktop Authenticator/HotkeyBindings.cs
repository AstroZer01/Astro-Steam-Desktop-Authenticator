using System;
using System.Collections.Generic;
using System.Globalization;

namespace Steam_Desktop_Authenticator
{
    public static class HotkeyBindings
    {
        public const int DefaultQrScanVirtualKey = 0xA3;   // VK_RCONTROL
        public const int DefaultQrCancelVirtualKey = 0x1B; // VK_ESCAPE

        private static readonly Dictionary<int, string> SupportedVirtualKeys = new Dictionary<int, string>
        {
            // Control, navigation, and editing keys
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x14] = "Caps Lock",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "Page Up",
            [0x22] = "Page Down",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "Print Screen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x91] = "Scroll Lock",

            // Distinct left/right modifier keys
            [0xA0] = "Left Shift",
            [0xA1] = "Right Shift",
            [0xA2] = "Left Ctrl",
            [0xA3] = "Right Ctrl",
            [0xA4] = "Left Alt",
            [0xA5] = "Right Alt",

            // Top-row digits 0-9
            [0x30] = "0",
            [0x31] = "1",
            [0x32] = "2",
            [0x33] = "3",
            [0x34] = "4",
            [0x35] = "5",
            [0x36] = "6",
            [0x37] = "7",
            [0x38] = "8",
            [0x39] = "9",

            // Letters A-Z
            [0x41] = "A",
            [0x42] = "B",
            [0x43] = "C",
            [0x44] = "D",
            [0x45] = "E",
            [0x46] = "F",
            [0x47] = "G",
            [0x48] = "H",
            [0x49] = "I",
            [0x4A] = "J",
            [0x4B] = "K",
            [0x4C] = "L",
            [0x4D] = "M",
            [0x4E] = "N",
            [0x4F] = "O",
            [0x50] = "P",
            [0x51] = "Q",
            [0x52] = "R",
            [0x53] = "S",
            [0x54] = "T",
            [0x55] = "U",
            [0x56] = "V",
            [0x57] = "W",
            [0x58] = "X",
            [0x59] = "Y",
            [0x5A] = "Z",

            // Numpad keys
            [0x60] = "Num 0",
            [0x61] = "Num 1",
            [0x62] = "Num 2",
            [0x63] = "Num 3",
            [0x64] = "Num 4",
            [0x65] = "Num 5",
            [0x66] = "Num 6",
            [0x67] = "Num 7",
            [0x68] = "Num 8",
            [0x69] = "Num 9",
            [0x6A] = "Num *",
            [0x6B] = "Num +",
            [0x6D] = "Num -",
            [0x6E] = "Num .",
            [0x6F] = "Num /",

            // Function keys F1-F24
            [0x70] = "F1",
            [0x71] = "F2",
            [0x72] = "F3",
            [0x73] = "F4",
            [0x74] = "F5",
            [0x75] = "F6",
            [0x76] = "F7",
            [0x77] = "F8",
            [0x78] = "F9",
            [0x79] = "F10",
            [0x7A] = "F11",
            [0x7B] = "F12",
            [0x7C] = "F13",
            [0x7D] = "F14",
            [0x7E] = "F15",
            [0x7F] = "F16",
            [0x80] = "F17",
            [0x81] = "F18",
            [0x82] = "F19",
            [0x83] = "F20",
            [0x84] = "F21",
            [0x85] = "F22",
            [0x86] = "F23",
            [0x87] = "F24",

            // Common OEM punctuation keys
            [0xBA] = ";",
            [0xBB] = "=",
            [0xBC] = ",",
            [0xBD] = "-",
            [0xBE] = ".",
            [0xBF] = "/",
            [0xC0] = "`",
            [0xDB] = "[",
            [0xDC] = "\\",
            [0xDD] = "]",
            [0xDE] = "'"
        };

        public static bool IsSupportedVirtualKey(int virtualKey)
        {
            return SupportedVirtualKeys.ContainsKey(virtualKey);
        }

        public static string GetDisplayName(int virtualKey)
        {
            if (SupportedVirtualKeys.TryGetValue(virtualKey, out string name))
                return name;

            return String.Format(CultureInfo.InvariantCulture, "Key 0x{0:X2}", virtualKey & 0xFF);
        }

        public static bool TryValidateQrHotkeys(int scanVirtualKey, int cancelVirtualKey, out string error)
        {
            if (!IsSupportedVirtualKey(scanVirtualKey))
            {
                error = "The QR scan hotkey is not a supported keyboard key.";
                return false;
            }

            if (!IsSupportedVirtualKey(cancelVirtualKey))
            {
                error = "The QR cancel hotkey is not a supported keyboard key.";
                return false;
            }

            if (scanVirtualKey == cancelVirtualKey)
            {
                error = "QR scan and cancel hotkeys must be different keys.";
                return false;
            }

            error = null;
            return true;
        }

        public static (int ScanVirtualKey, int CancelVirtualKey) NormalizeQrHotkeys(int scanVirtualKey, int cancelVirtualKey)
        {
            int normalizedScan = IsSupportedVirtualKey(scanVirtualKey) ? scanVirtualKey : DefaultQrScanVirtualKey;
            int normalizedCancel = IsSupportedVirtualKey(cancelVirtualKey) ? cancelVirtualKey : DefaultQrCancelVirtualKey;

            if (normalizedScan == normalizedCancel)
            {
                if (normalizedScan != DefaultQrCancelVirtualKey)
                {
                    normalizedCancel = DefaultQrCancelVirtualKey;
                }
                else
                {
                    normalizedScan = DefaultQrScanVirtualKey;
                    normalizedCancel = DefaultQrCancelVirtualKey;
                }
            }

            return (normalizedScan, normalizedCancel);
        }
    }
}

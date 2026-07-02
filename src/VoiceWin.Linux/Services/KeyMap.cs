namespace VoiceWin.Services;

/// <summary>
/// Windows virtual-key code &lt;-&gt; Linux evdev key code mapping. Settings store
/// Windows VK codes (shared format with the WPF app); evdev codes are what
/// /dev/input delivers.
/// </summary>
public static class KeyMap
{
    public static readonly Dictionary<int, int> VkToEvdev = new()
    {
        // Modifiers
        [160] = 42,  // Left Shift
        [161] = 54,  // Right Shift
        [162] = 29,  // Left Ctrl
        [163] = 97,  // Right Ctrl
        [164] = 56,  // Left Alt
        [165] = 100, // Right Alt
        [91] = 125,  // Left Win/Super
        [92] = 126,  // Right Win/Super
        // Specials
        [8] = 14,    // Backspace
        [9] = 15,    // Tab
        [13] = 28,   // Enter
        [19] = 119,  // Pause
        [20] = 58,   // Caps Lock
        [27] = 1,    // Escape
        [32] = 57,   // Space
        [33] = 104,  // Page Up
        [34] = 109,  // Page Down
        [35] = 107,  // End
        [36] = 102,  // Home
        [37] = 105,  // Left Arrow
        [38] = 103,  // Up Arrow
        [39] = 106,  // Right Arrow
        [40] = 108,  // Down Arrow
        [45] = 110,  // Insert
        [46] = 111,  // Delete
        [93] = 127,  // Menu
        [144] = 69,  // Num Lock
        [145] = 70,  // Scroll Lock
        // Digits 0-9 (VK 48-57)
        [48] = 11, [49] = 2, [50] = 3, [51] = 4, [52] = 5,
        [53] = 6, [54] = 7, [55] = 8, [56] = 9, [57] = 10,
        // A-Z (VK 65-90)
        [65] = 30, [66] = 48, [67] = 46, [68] = 32, [69] = 18, [70] = 33,
        [71] = 34, [72] = 35, [73] = 23, [74] = 36, [75] = 37, [76] = 38,
        [77] = 50, [78] = 49, [79] = 24, [80] = 25, [81] = 16, [82] = 19,
        [83] = 31, [84] = 20, [85] = 22, [86] = 47, [87] = 17, [88] = 45,
        [89] = 21, [90] = 44,
        // Numpad 0-9 (VK 96-105)
        [96] = 82, [97] = 79, [98] = 80, [99] = 81, [100] = 75,
        [101] = 76, [102] = 77, [103] = 71, [104] = 72, [105] = 73,
        // F1-F12 (VK 112-123)
        [112] = 59, [113] = 60, [114] = 61, [115] = 62, [116] = 63, [117] = 64,
        [118] = 65, [119] = 66, [120] = 67, [121] = 68, [122] = 87, [123] = 88,
        // OEM punctuation
        [186] = 39,  // ;
        [187] = 13,  // =
        [188] = 51,  // ,
        [189] = 12,  // -
        [190] = 52,  // .
        [191] = 53,  // /
        [192] = 41,  // `
        [219] = 26,  // [
        [220] = 43,  // \
        [221] = 27,  // ]
        [222] = 40,  // '
    };

    public static readonly int[] EvdevCtrl = { 29, 97 };
    public static readonly int[] EvdevAlt = { 56, 100 };
    public static readonly int[] EvdevShift = { 42, 54 };
    public static readonly int[] EvdevMeta = { 125, 126 };
}

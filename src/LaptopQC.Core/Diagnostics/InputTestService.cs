using System.Runtime.InteropServices;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// Service for interactive device testing - allows technicians to verify devices work
/// </summary>
public class InputTestService
{
    /// <summary>
    /// Result of an input device test
    /// </summary>
    public class InputTestResult
    {
        public bool Passed { get; set; }
        public string DeviceType { get; set; } = "";
        public string Message { get; set; } = "";
        public int TotalActions { get; set; }
        public int SuccessfulActions { get; set; }
        public List<string> FailedItems { get; set; } = new();
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Keyboard test state
    /// </summary>
    public class KeyboardTestState
    {
        public HashSet<int> TestedKeys { get; } = new();
        public HashSet<int> ExpectedKeys { get; } = new();
        public DateTime StartTime { get; set; }
        public bool IsComplete { get; set; }
        
        // Standard laptop keyboard keys to test (Windows)
        public static readonly int[] CommonKeys = new[]
        {
            // Function row
            0x1B, // Escape
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, // F1-F12
            
            // Number row
            0xC0, // Tilde
            0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, // 1-0
            0xBD, 0xBB, 0x08, // Minus, Equals, Backspace
            
            // QWERTY row
            0x09, // Tab
            0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50, // Q-P
            0xDB, 0xDD, 0xDC, // Brackets, Backslash
            
            // ASDF row
            0x14, // Caps Lock
            0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, // A-L
            0xBA, 0xDE, 0x0D, // Semicolon, Quote, Enter
            
            // ZXCV row
            0xA0, // Left Shift
            0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, // Z-M
            0xBC, 0xBE, 0xBF, // Comma, Period, Slash
            0xA1, // Right Shift
            
            // Bottom row (Windows): LCtrl, LWin, LAlt, Space, RAlt, RCtrl
            0xA2, 0x5B, 0xA4, 0x20, 0xA5, 0xA3,
            0x25, 0x26, 0x27, 0x28 // Arrow keys
        };

        // macOS laptop keyboard keys to test.
        // Bottom row uses Cmd (0x5B/0x5C) and Option (0xA4/0xA5) instead of Win/RCtrl.
        public static readonly int[] MacCommonKeys = new[]
        {
            // Function row
            0x1B, // Escape
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, // F1-F12

            // Number row
            0xC0, // Tilde/backtick
            0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, // 1-0
            0xBD, 0xBB, 0x08, // Minus, Equals, Backspace

            // QWERTY row
            0x09, // Tab
            0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50, // Q-P
            0xDB, 0xDD, 0xDC, // Brackets, Backslash

            // ASDF row
            0x14, // Caps Lock
            0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, // A-L
            0xBA, 0xDE, 0x0D, // Semicolon, Quote, Enter

            // ZXCV row
            0xA0, // Left Shift
            0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, // Z-M
            0xBC, 0xBE, 0xBF, // Comma, Period, Slash
            0xA1, // Right Shift

            // Bottom row (macOS): LCtrl, Left Option, Left Command, Space, Right Command, Right Option
            0xA2, 0xA4, 0x5B, 0x20, 0x5C, 0xA5,
            0x25, 0x26, 0x27, 0x28 // Arrow keys
        };

        // Numpad keys
        public static readonly int[] NumpadKeys = new[]
        {
            0x90, // Num Lock
            0x6F, // Divide
            0x6A, // Multiply
            0x6D, // Subtract
            0x67, 0x68, 0x69, // 7 8 9
            0x6B, // Add
            0x64, 0x65, 0x66, // 4 5 6
            0x61, 0x62, 0x63, // 1 2 3
            0x60, // 0
            0x6E, // Decimal
            // Enter on numpad shares virtual key with main enter usually, 
            // but can be distinguished by extended bit. 
            // For simple virtual key matching we might just re-use 0x0D or skip specific check if key same
        };

        public KeyboardTestState(bool isMac = false)
        {
            ResetExpectedKeys(false, isMac);
            StartTime = DateTime.Now;
        }

        public void ResetExpectedKeys(bool includeNumpad, bool isMac = false)
        {
            ExpectedKeys.Clear();
            var baseKeys = isMac ? MacCommonKeys : CommonKeys;
            foreach (var key in baseKeys)
                ExpectedKeys.Add(key);
            
            if (includeNumpad)
            {
                foreach (var key in NumpadKeys)
                    ExpectedKeys.Add(key);
            }
        }

        public void RegisterKeyPress(int virtualKeyCode)
        {
            TestedKeys.Add(virtualKeyCode);
        }

        public double PercentComplete => ExpectedKeys.Count > 0 
            ? (TestedKeys.Intersect(ExpectedKeys).Count() * 100.0 / ExpectedKeys.Count) 
            : 0;

        public IEnumerable<int> MissingKeys => ExpectedKeys.Except(TestedKeys);
        
        public InputTestResult GetResult()
        {
            var tested = TestedKeys.Intersect(ExpectedKeys).Count();
            var missing = ExpectedKeys.Except(TestedKeys).ToList();
            
            return new InputTestResult
            {
                Passed = tested >= ExpectedKeys.Count * 0.95, // 95% threshold
                DeviceType = "Keyboard",
                Message = tested == ExpectedKeys.Count 
                    ? "All keys tested successfully" 
                    : $"{tested}/{ExpectedKeys.Count} keys tested ({missing.Count} missing)",
                TotalActions = ExpectedKeys.Count,
                SuccessfulActions = tested,
                FailedItems = missing.Select(k => GetKeyName(k)).ToList(),
                Duration = DateTime.Now - StartTime
            };
        }
    }

    /// <summary>
    /// Trackpad test state
    /// </summary>
    public class TrackpadTestState
    {
        public bool MovementDetected { get; set; }
        public bool LeftClickDetected { get; set; }
        public bool RightClickDetected { get; set; }
        public bool ScrollDetected { get; set; }
        public bool TwoFingerScrollDetected { get; set; }
        public int TotalMoveDistance { get; set; }
        public DateTime StartTime { get; set; }
        public bool IsComplete { get; set; }

        public TrackpadTestState()
        {
            StartTime = DateTime.Now;
        }

        public void RegisterMovement(int deltaX, int deltaY)
        {
            MovementDetected = true;
            TotalMoveDistance += Math.Abs(deltaX) + Math.Abs(deltaY);
        }

        public void RegisterClick(bool isRightClick)
        {
            if (isRightClick)
                RightClickDetected = true;
            else
                LeftClickDetected = true;
        }

        public void RegisterScroll(int delta)
        {
            ScrollDetected = true;
        }

        public double PercentComplete
        {
            get
            {
                int score = 0;
                if (MovementDetected) score += 25;
                if (LeftClickDetected) score += 25;
                if (RightClickDetected) score += 25;
                if (ScrollDetected) score += 25;
                return score;
            }
        }

        public InputTestResult GetResult()
        {
            var failed = new List<string>();
            if (!MovementDetected) failed.Add("Movement");
            if (!LeftClickDetected) failed.Add("Left Click");
            if (!RightClickDetected) failed.Add("Right Click");
            if (!ScrollDetected) failed.Add("Scroll");

            return new InputTestResult
            {
                Passed = failed.Count == 0,
                DeviceType = "Trackpad",
                Message = failed.Count == 0 
                    ? "All trackpad functions working" 
                    : $"Missing: {string.Join(", ", failed)}",
                TotalActions = 4,
                SuccessfulActions = 4 - failed.Count,
                FailedItems = failed,
                Duration = DateTime.Now - StartTime
            };
        }
    }

    /// <summary>
    /// USB port test state - tracks device insertions
    /// </summary>
    public class UsbTestState
    {
        public int ExpectedPortCount { get; set; }
        public HashSet<string> DetectedDeviceIds { get; } = new();
        public List<string> InsertionEvents { get; } = new();
        public DateTime StartTime { get; set; }
        public bool IsComplete { get; set; }

        public UsbTestState(int expectedPorts)
        {
            ExpectedPortCount = expectedPorts;
            StartTime = DateTime.Now;
        }

        public void RegisterDeviceInsertion(string deviceId, string deviceName)
        {
            if (DetectedDeviceIds.Add(deviceId))
            {
                InsertionEvents.Add($"{DateTime.Now:HH:mm:ss} - {deviceName}");
            }
        }

        public int PortsTested => DetectedDeviceIds.Count;
        public double PercentComplete => ExpectedPortCount > 0 
            ? Math.Min(100, PortsTested * 100.0 / ExpectedPortCount) 
            : 0;

        public InputTestResult GetResult()
        {
            return new InputTestResult
            {
                Passed = PortsTested >= ExpectedPortCount,
                DeviceType = "USB Ports",
                Message = $"{PortsTested}/{ExpectedPortCount} ports verified",
                TotalActions = ExpectedPortCount,
                SuccessfulActions = PortsTested,
                Duration = DateTime.Now - StartTime
            };
        }
    }

    /// <summary>
    /// Gets human-readable key name from virtual key code
    /// </summary>
    public static string GetKeyName(int virtualKeyCode)
    {
        return virtualKeyCode switch
        {
            0x1B => "Escape",
            0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
            0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
            0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0xC0 => "`", 
            0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4", 0x35 => "5",
            0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9", 0x30 => "0",
            0xBD => "-", 0xBB => "=", 0x08 => "Backspace",
            0x09 => "Tab",
            0x51 => "Q", 0x57 => "W", 0x45 => "E", 0x52 => "R", 0x54 => "T",
            0x59 => "Y", 0x55 => "U", 0x49 => "I", 0x4F => "O", 0x50 => "P",
            0xDB => "[", 0xDD => "]", 0xDC => "\\",
            0x14 => "CapsLock",
            0x41 => "A", 0x53 => "S", 0x44 => "D", 0x46 => "F", 0x47 => "G",
            0x48 => "H", 0x4A => "J", 0x4B => "K", 0x4C => "L",
            0xBA => ";", 0xDE => "'", 0x0D => "Enter",
            0x10 => "Shift", 0xA0 => "Left Shift", 0xA1 => "Right Shift",
            0x5A => "Z", 0x58 => "X", 0x43 => "C", 0x56 => "V", 0x42 => "B",
            0x4E => "N", 0x4D => "M",
            0xBC => ",", 0xBE => ".", 0xBF => "/",
            0x11 => "Ctrl", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
            0x5B => "Win", 0x5C => "Right Win",
            0x12 => "Alt", 0xA4 => "Left Alt", 0xA5 => "Right Alt",
            0x20 => "Space",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",

            
            // Numpad
            0x90 => "NumLock", 0x6F => "Div", 0x6A => "Mult", 0x6D => "Sub",
            0x67 => "Num7", 0x68 => "Num8", 0x69 => "Num9", 0x6B => "Add",
            0x64 => "Num4", 0x65 => "Num5", 0x66 => "Num6",
            0x61 => "Num1", 0x62 => "Num2", 0x63 => "Num3",
            0x60 => "Num0", 0x6E => "Dec",

            _ => $"Key_{virtualKeyCode:X2}"
        };
    }
}

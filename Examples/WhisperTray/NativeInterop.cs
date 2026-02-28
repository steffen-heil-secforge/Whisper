using System.Runtime.InteropServices;

namespace WhisperTray;

static class NativeInterop
{
	// --- Global hotkey ---

	[DllImport( "user32.dll", SetLastError = true )]
	public static extern bool RegisterHotKey( IntPtr hWnd, int id, uint fsModifiers, uint vk );

	[DllImport( "user32.dll", SetLastError = true )]
	public static extern bool UnregisterHotKey( IntPtr hWnd, int id );

	public const uint MOD_ALT = 0x0001;
	public const uint MOD_CONTROL = 0x0002;
	public const uint MOD_SHIFT = 0x0004;
	public const uint MOD_NOREPEAT = 0x4000;

	public const int WM_HOTKEY = 0x0312;

	// --- SendInput ---

	[DllImport( "user32.dll", SetLastError = true )]
	public static extern uint SendInput( uint nInputs, INPUT[] pInputs, int cbSize );

	public const uint INPUT_KEYBOARD = 1;
	public const uint KEYEVENTF_UNICODE = 0x0004;
	public const uint KEYEVENTF_KEYUP = 0x0002;

	[StructLayout( LayoutKind.Sequential )]
	public struct KEYBDINPUT
	{
		public ushort wVk;
		public ushort wScan;
		public uint dwFlags;
		public uint time;
		public IntPtr dwExtraInfo;
	}

	[StructLayout( LayoutKind.Explicit, Size = 32 )]
	public struct InputUnion
	{
		[FieldOffset( 0 )]
		public KEYBDINPUT ki;
	}

	// Win32 INPUT on x64: type(4) + pad(4) + union(32) = 40 bytes
	[StructLayout( LayoutKind.Explicit, Size = 40 )]
	public struct INPUT
	{
		[FieldOffset( 0 )] public uint type;
		[FieldOffset( 8 )] public InputUnion u;
	}

	[DllImport( "user32.dll" )]
	public static extern bool DestroyIcon( IntPtr hIcon );
}

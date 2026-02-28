using System.Runtime.InteropServices;

namespace WhisperTray;

static class TextInjector
{
	public static void InjectText( string? text )
	{
		if( string.IsNullOrWhiteSpace( text ) )
			return;

		// Filter using Rune enumeration — correctly handles surrogate pairs as atomic units
		// and replaces lone surrogates with U+FFFD. Strips control characters, bidi overrides,
		// zero-width chars, and Private Use Area codepoints before injection.
		var filtered = new List<System.Text.Rune>();
		foreach( var rune in text.AsSpan().EnumerateRunes() )
		{
			if( IsAllowedRune( rune ) )
				filtered.Add( rune );
		}
		if( filtered.Count == 0 )
			return;

		// Count UTF-16 code units needed (BMP = 1, supplementary = 2 surrogates)
		int codeUnitCount = 0;
		foreach( var rune in filtered )
			codeUnitCount += rune.Utf16SequenceLength;

		// Build INPUT array: 2 entries per UTF-16 code unit (keydown + keyup)
		var inputs = new NativeInterop.INPUT[codeUnitCount * 2];
		int cbSize = Marshal.SizeOf<NativeInterop.INPUT>();
		int idx = 0;

		Span<char> chars = stackalloc char[2];
		foreach( var rune in filtered )
		{
			int len = rune.EncodeToUtf16( chars );
			// For surrogate pairs: send all keydowns first, then keyups in reverse.
			// This matches the Win32 expected ordering (high-down, low-down, low-up, high-up)
			// so applications correctly reassemble the pair.
			for( int c = 0; c < len; c++ )
				AddKeyEvent( inputs, ref idx, chars[c], keyUp: false );
			for( int c = len - 1; c >= 0; c-- )
				AddKeyEvent( inputs, ref idx, chars[c], keyUp: true );
		}

		uint sent = NativeInterop.SendInput( (uint)inputs.Length, inputs, cbSize );
		if( sent != (uint)inputs.Length )
			DebugLog.Write( $"SendInput: sent {sent}/{inputs.Length} events; foreground window may be elevated" );
	}

	static bool IsAllowedRune( System.Text.Rune rune )
	{
		int v = rune.Value;
		if( v < 0x20 || v == 0x7F )       return false; // C0 controls, DEL
		if( v >= 0x80 && v <= 0x9F )      return false; // C1 controls (CSI, OSC, DCS, etc.)
		if( v == 0xFFFD )                  return false; // replacement char
		if( v >= 0x200B && v <= 0x200F )   return false; // zero-width spaces + direction marks
		if( v >= 0x202A && v <= 0x202E )   return false; // bidi embedding/override
		if( v >= 0x2066 && v <= 0x206F )   return false; // bidi isolates + deprecated format chars
		if( v == 0xFEFF )                  return false; // BOM / ZWNBSP
		if( v >= 0xE000 && v <= 0xF8FF )   return false; // BMP Private Use Area
		if( v >= 0xE0000 )                 return false; // Tags block + supplementary Private Use Areas
		return true;
	}

	static void AddKeyEvent( NativeInterop.INPUT[] inputs, ref int idx, ushort ch, bool keyUp )
	{
		inputs[idx].type = NativeInterop.INPUT_KEYBOARD;
		inputs[idx].u.ki.wVk = 0;
		inputs[idx].u.ki.wScan = ch;
		inputs[idx].u.ki.dwFlags = NativeInterop.KEYEVENTF_UNICODE | ( keyUp ? NativeInterop.KEYEVENTF_KEYUP : 0 );
		inputs[idx].u.ki.time = 0;
		inputs[idx].u.ki.dwExtraInfo = IntPtr.Zero;
		idx++;
	}
}

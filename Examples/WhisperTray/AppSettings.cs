using System.Text.Json;

namespace WhisperTray;

sealed class AppSettings
{
	public string? ModelPath { get; set; }
	public string? MicrophoneEndpoint { get; set; }
	public string? MicrophoneDisplayName { get; set; }
	public uint HotkeyModifiers { get; set; } = NativeInterop.MOD_CONTROL | NativeInterop.MOD_SHIFT;
	public uint HotkeyVirtualKey { get; set; } = 0x20; // VK_SPACE
	public string LanguageCode { get; set; } = "en";

	// Capture engine parameters (seconds)
	public float MinDuration { get; set; } = 3.0f;        // Minimum audio before transcribing
	public float MaxDuration { get; set; } = 11.0f;        // Maximum audio chunk length
	public float DropStartSilence { get; set; } = 0.25f;   // Leading silence to discard
	public float PauseDuration { get; set; } = 0.6f;       // Silence before triggering transcription
	public bool NoContext { get; set; } = true;             // Don't carry previous transcription as decoder context

	static readonly string settingsDir = Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
		"WhisperTray" );

	static readonly string settingsPath = Path.Combine( settingsDir, "settings.json" );

	static readonly JsonSerializerOptions jsonOptions = new()
	{
		WriteIndented = true
	};

	public static AppSettings Load()
	{
		AppSettings settings;
		try
		{
			if( File.Exists( settingsPath ) )
			{
				string json = File.ReadAllText( settingsPath );
				settings = JsonSerializer.Deserialize<AppSettings>( json, jsonOptions ) ?? new AppSettings();
			}
			else
			{
				settings = new AppSettings();
			}
		}
		catch( Exception ex )
		{
			DebugLog.Write( $"Failed to load settings: {ex.Message}" );
			settings = new AppSettings();
		}

		Sanitize( settings );
		return settings;
	}

	/// <summary>Clamp deserialized values to safe ranges. Guards against NaN/Infinity/extreme values
	/// from a hand-edited or corrupted settings.json reaching native code.</summary>
	static void Sanitize( AppSettings s )
	{
		s.MinDuration      = ClampFinite( s.MinDuration,      0.5f,  30.0f, 3.0f );
		s.MaxDuration      = ClampFinite( s.MaxDuration,      1.0f, 120.0f, 11.0f );
		s.DropStartSilence = ClampFinite( s.DropStartSilence, 0.0f,   5.0f, 0.25f );
		s.PauseDuration    = ClampFinite( s.PauseDuration,    0.1f,  10.0f, 0.6f );

		// Enforce MinDuration <= MaxDuration
		if( s.MinDuration > s.MaxDuration )
			s.MinDuration = s.MaxDuration;

		// Mask hotkey modifiers to known-good bits (ALT|CTRL|SHIFT|WIN)
		const uint knownModifiers = 0x0001 | 0x0002 | 0x0004 | 0x0008;
		s.HotkeyModifiers &= knownModifiers;

		// Reject invalid virtual key codes — reset to default Ctrl+Shift+Space
		if( s.HotkeyVirtualKey == 0 || s.HotkeyVirtualKey > 0xFE )
		{
			s.HotkeyModifiers = NativeInterop.MOD_CONTROL | NativeInterop.MOD_SHIFT;
			s.HotkeyVirtualKey = 0x20;
		}
	}

	static float ClampFinite( float value, float min, float max, float fallback )
	{
		if( !float.IsFinite( value ) )
			return fallback;
		return Math.Clamp( value, min, max );
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory( settingsDir );
			string json = JsonSerializer.Serialize( this, jsonOptions );
			// Write to temp file then rename for atomic update (prevents corrupt JSON on crash)
			string tmpPath = settingsPath + ".tmp";
			File.WriteAllText( tmpPath, json );
			File.Move( tmpPath, settingsPath, overwrite: true );
		}
		catch( Exception ex )
		{
			DebugLog.Write( $"Failed to save settings: {ex.Message}" );
		}
	}
}

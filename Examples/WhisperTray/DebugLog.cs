namespace WhisperTray;

static class DebugLog
{
	static readonly object sync = new();
	static readonly string logPath = Path.Combine(
		Environment.GetFolderPath( Environment.SpecialFolder.ApplicationData ),
		"WhisperTray", "debug.log" );

	const long maxLogSize = 1024 * 1024; // 1 MB

	public static void Write( string msg )
	{
		lock( sync )
		{
			try
			{
				string dir = Path.GetDirectoryName( logPath )!;
				Directory.CreateDirectory( dir );

				// Rotate when log exceeds 1 MB (atomic rename — no data loss on crash)
				var fi = new FileInfo( logPath );
				if( fi.Exists && fi.Length > maxLogSize )
				{
					string bak = Path.ChangeExtension( logPath, ".log.bak" );
					File.Move( logPath, bak, overwrite: true );
				}

					// Strip all control characters (C0, DEL, and C1) to prevent log injection
				msg = new string( msg.Select( c => c < 0x20 || c == 0x7F || ( c >= 0x80 && c <= 0x9F ) ? ' ' : c ).ToArray() );
				File.AppendAllText( logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n" );
			}
			catch { }
		}
	}
}

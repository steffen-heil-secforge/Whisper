using Whisper;

namespace WhisperTray;

sealed class TrayApplicationContext : ApplicationContext
{
	readonly NotifyIcon trayIcon;
	readonly AppSettings settings;
	readonly SynchronizationContext uiContext;
	GlobalHotKey? hotkey;
	CaptureSession? session;
	bool capturing;
	bool startPending; // True while model is loading on threadpool
	int startGeneration; // Incremented on each start/stop to cancel stale threadpool work

	// Programmatically generated icons
	static readonly Icon idleIcon = CreateCircleIcon( Color.Gray );
	static readonly Icon loadingIcon = CreateCircleIcon( Color.Gold );
	static readonly Icon listeningIcon = CreateCircleIcon( Color.LimeGreen );

	public TrayApplicationContext()
	{
		uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
		settings = AppSettings.Load();

		trayIcon = new NotifyIcon
		{
			Icon = idleIcon,
			Text = "WhisperTray — Idle",
			Visible = true,
			ContextMenuStrip = BuildContextMenu()
		};

		try
		{
			hotkey = new GlobalHotKey(
				settings.HotkeyModifiers,
				settings.HotkeyVirtualKey,
				ToggleCapture );
		}
		catch( Exception ex )
		{
			trayIcon.ShowBalloonTip( 5000, "WhisperTray",
				$"Failed to register hotkey: {ex.Message}", ToolTipIcon.Error );
		}

		// On first run, prompt for model selection
		if( string.IsNullOrEmpty( settings.ModelPath ) )
			Application.Idle += OnFirstIdle;
	}

	void OnFirstIdle( object? sender, EventArgs e )
	{
		Application.Idle -= OnFirstIdle;
		SelectModelFile();
	}

	ContextMenuStrip BuildContextMenu()
	{
		var menu = new ContextMenuStrip();
		menu.Items.Add( "Toggle Capture", null, ( _, _ ) => ToggleCapture() );
		menu.Items.Add( new ToolStripSeparator() );

		// Microphone submenu
		var micMenu = new ToolStripMenuItem( "Select Microphone" );
		micMenu.DropDownOpening += ( _, _ ) => PopulateMicMenu( micMenu );
		menu.Items.Add( micMenu );

		menu.Items.Add( "Select Model File...", null, ( _, _ ) => SelectModelFile() );
		menu.Items.Add( new ToolStripSeparator() );
		menu.Items.Add( "Exit", null, ( _, _ ) => ExitApp() );
		return menu;
	}

	void PopulateMicMenu( ToolStripMenuItem micMenu )
	{
		micMenu.DropDownItems.Clear();
		try
		{
			using iMediaFoundation mf = Library.initMediaFoundation();
			CaptureDeviceId[]? devices = mf.listCaptureDevices();
			if( devices == null || devices.Length == 0 )
			{
				micMenu.DropDownItems.Add( "(No devices found)" ).Enabled = false;
				return;
			}

			foreach( var dev in devices )
			{
				string endpoint = dev.endpoint;
				string displayName = Sanitize( dev.displayName );
				var item = new ToolStripMenuItem( displayName );
				item.Checked = endpoint == settings.MicrophoneEndpoint;
				item.Click += ( _, _ ) =>
				{
					settings.MicrophoneEndpoint = endpoint;
					settings.MicrophoneDisplayName = displayName;
					settings.Save();
					DebugLog.Write( $"Microphone changed: {displayName}" );
					trayIcon.ShowBalloonTip( 2000, "WhisperTray",
						$"Microphone: {displayName}", ToolTipIcon.Info );
				};
				micMenu.DropDownItems.Add( item );
			}
		}
		catch( Exception ex )
		{
			micMenu.DropDownItems.Add( $"Error: {ex.Message}" ).Enabled = false;
		}
	}

	void SelectModelFile()
	{
		using var dlg = new OpenFileDialog
		{
			Title = "Select Whisper GGML Model File",
			Filter = "GGML Models (*.bin)|*.bin|All Files (*.*)|*.*",
			CheckFileExists = true
		};

		if( !string.IsNullOrEmpty( settings.ModelPath ) )
			dlg.InitialDirectory = Path.GetDirectoryName( settings.ModelPath );

		if( dlg.ShowDialog() == DialogResult.OK )
		{
			settings.ModelPath = dlg.FileName;
			settings.Save();
			DebugLog.Write( $"Model selected: {Path.GetFileName( dlg.FileName )}" );
			trayIcon.ShowBalloonTip( 2000, "WhisperTray",
				$"Model: {Path.GetFileName( dlg.FileName )}", ToolTipIcon.Info );
		}
	}

	void ToggleCapture()
	{
		if( capturing || startPending )
			StopCapture();
		else
			StartCapture();
	}

	void StartCapture()
	{
		if( string.IsNullOrEmpty( settings.ModelPath ) || !File.Exists( settings.ModelPath ) )
		{
			trayIcon.ShowBalloonTip( 3000, "WhisperTray",
				"No model file configured. Right-click tray icon to select one.", ToolTipIcon.Warning );
			SelectModelFile();
			return;
		}

		// Resolve microphone endpoint
		string? endpoint = settings.MicrophoneEndpoint;
		if( string.IsNullOrEmpty( endpoint ) )
		{
			// Use first available device
			try
			{
				using iMediaFoundation mf = Library.initMediaFoundation();
				CaptureDeviceId[]? devices = mf.listCaptureDevices();
				if( devices == null || devices.Length == 0 )
				{
					trayIcon.ShowBalloonTip( 3000, "WhisperTray",
						"No audio capture devices found.", ToolTipIcon.Error );
					return;
				}
				endpoint = devices[0].endpoint;
				settings.MicrophoneEndpoint = endpoint;
				settings.MicrophoneDisplayName = Sanitize( devices[0].displayName );
				settings.Save();
			}
			catch( Exception ex )
			{
				trayIcon.ShowBalloonTip( 3000, "WhisperTray",
					$"Failed to list capture devices: {ex.Message}", ToolTipIcon.Error );
				return;
			}
		}

		startPending = true;
		trayIcon.Icon = loadingIcon;
		trayIcon.Text = "WhisperTray — Loading model...";
		trayIcon.ShowBalloonTip( 2000, "WhisperTray",
			"Loading model and starting capture...", ToolTipIcon.Info );

		string modelPath = settings.ModelPath;
		string lang = settings.LanguageCode;
		int gen = ++startGeneration;

		// Create capture session on threadpool to avoid blocking UI during model load
		ThreadPool.QueueUserWorkItem( _ =>
		{
			try
			{
				DebugLog.Write( $"Starting capture: model={Path.GetFileName( modelPath )}, mic={settings.MicrophoneDisplayName ?? endpoint}, lang={lang}" );
				var s = new CaptureSession( modelPath, endpoint, lang,
					settings.MinDuration, settings.MaxDuration, settings.DropStartSilence, settings.PauseDuration,
					settings.NoContext, OnSegmentReady, uiContext,
					onError: msg =>
					{
						// Only act on failure if this session is still current.
						// A stale failure callback must not tear down a newer active session.
						if( startGeneration == gen )
							OnCaptureFailed( msg );
					} );
				// Marshal back to UI thread for state updates
				uiContext.Post( _ =>
				{
					startPending = false;
					// If user toggled stop while model was loading, discard this session
					if( gen != startGeneration )
					{
						ThreadPool.QueueUserWorkItem( _ =>
						{
							try { s.Stop(); } catch { }
							s.Dispose();
						} );
						return;
					}
					session = s;
					capturing = true;
					trayIcon.Icon = listeningIcon;
					UpdateTrayText();
				}, null );
			}
			catch( Exception ex )
			{
				uiContext.Post( _ =>
				{
					startPending = false;
					if( gen != startGeneration )
						return;
					trayIcon.Icon = idleIcon;
					trayIcon.Text = "WhisperTray — Idle";
					trayIcon.ShowBalloonTip( 5000, "WhisperTray",
						$"Failed to start capture: {ex.Message}", ToolTipIcon.Error );
				}, null );
			}
		} );
	}

	void StopCapture()
	{
		DebugLog.Write( "Capture stopped" );
		capturing = false;
		startPending = false;
		startGeneration++; // Cancel any in-flight start
		trayIcon.Icon = idleIcon;
		trayIcon.Text = "WhisperTray — Stopping...";

		var s = session;
		session = null;

		if( s != null )
		{
			// Dispose on threadpool to avoid blocking UI
			ThreadPool.QueueUserWorkItem( _ =>
			{
				try
				{
					s.Stop();
				}
				catch( Exception ex )
				{
					uiContext.Post( _ =>
					{
						if( !disposed ) // trayIcon may already be disposed during shutdown
							trayIcon.ShowBalloonTip( 3000, "WhisperTray",
								$"Capture error: {ex.Message}", ToolTipIcon.Warning );
					}, null );
				}
				finally
				{
					s.Dispose();
					uiContext.Post( _ =>
					{
						if( !disposed )
							UpdateTrayText();
					}, null );
				}
			} );
		}
		else
		{
			UpdateTrayText();
		}
	}

	void OnSegmentReady( string text )
	{
		// Guard against stale Post callbacks delivered after StopCapture.
		// Both capturing and disposed are only modified on the UI thread,
		// and Post callbacks also run on the UI thread, so this is safe.
		if( disposed || !capturing )
			return;
		TextInjector.InjectText( text );
	}

	void OnCaptureFailed( string message )
	{
		// Called on UI thread when the capture thread dies unexpectedly.
		// Increment startGeneration to invalidate any pending success post from StartCapture.
		capturing = false;
		startPending = false;
		startGeneration++;
		var s = session;
		session = null;
		// Dispose on threadpool — releases model (VRAM) and Media Foundation COM objects
		if( s != null )
			ThreadPool.QueueUserWorkItem( _ => s.Dispose() );
		trayIcon.Icon = idleIcon;
		UpdateTrayText();
		DebugLog.Write( $"Capture failed: {message}" );
		trayIcon.ShowBalloonTip( 5000, "WhisperTray",
			$"Capture stopped: {message}", ToolTipIcon.Error );
	}

	void UpdateTrayText()
	{
		string status = capturing ? "Listening" : "Idle";
		string mic = settings.MicrophoneDisplayName ?? "default";
		// NotifyIcon.Text is limited to 127 chars
		string full = $"WhisperTray — {status}\nMic: {mic}";
		if( full.Length > 127 )
		{
			int cut = 127;
			// Avoid splitting a UTF-16 surrogate pair
			if( cut < full.Length && char.IsLowSurrogate( full[cut] ) )
				cut--;
			full = full[..cut];
		}
		trayIcon.Text = full;
	}

	void ExitApp()
	{
		Dispose();
		Application.Exit();
	}

	bool disposed;

	protected override void Dispose( bool disposing )
	{
		if( !disposing || disposed )
		{
			base.Dispose( disposing );
			return;
		}
		disposed = true;

		// Stop capture synchronously during shutdown so COM/MF cleanup completes
		// before Application.Exit() stops the message pump.
		capturing = false;
		startPending = false;
		startGeneration++;
		var s = session;
		session = null;
		if( s != null )
		{
			try { s.Stop(); } catch { }
			s.Dispose();
		}

		hotkey?.Dispose();
		hotkey = null;
		trayIcon.Visible = false;
		trayIcon.Dispose();
		base.Dispose( disposing );
	}

	/// <summary>Strip control characters from external strings (e.g. device names from OS).</summary>
	static string Sanitize( string? s )
	{
		if( string.IsNullOrEmpty( s ) )
			return string.Empty;
		return new string( s.Where( c =>
			c >= 0x20 && c != 0x7F &&
			!( c >= 0x80 && c <= 0x9F ) &&       // C1 controls
			!( c >= 0x200B && c <= 0x200F ) &&    // zero-width + direction marks
			!( c >= 0x202A && c <= 0x202E ) &&    // bidi embedding/override
			!( c >= 0x2066 && c <= 0x206F ) &&    // bidi isolates + deprecated
			c != 0xFEFF                            // BOM
		).ToArray() );
	}

	static Icon CreateCircleIcon( Color color )
	{
		using var bmp = new Bitmap( 16, 16 );
		using var g = Graphics.FromImage( bmp );
		g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		g.Clear( Color.Transparent );
		using var brush = new SolidBrush( color );
		g.FillEllipse( brush, 1, 1, 14, 14 );
		// Dark border
		using var pen = new Pen( Color.FromArgb( 80, 0, 0, 0 ), 1f );
		g.DrawEllipse( pen, 1, 1, 14, 14 );
		IntPtr hIcon = bmp.GetHicon();
		try
		{
			// Icon.FromHandle does NOT own the handle — we must DestroyIcon ourselves
			using var tempIcon = Icon.FromHandle( hIcon );
			return (Icon)tempIcon.Clone();
		}
		finally
		{
			NativeInterop.DestroyIcon( hIcon );
		}
	}
}

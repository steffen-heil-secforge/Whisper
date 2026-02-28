using System.Runtime.ExceptionServices;
using Whisper;

namespace WhisperTray;

sealed class CaptureSession : IDisposable
{
	readonly iModel model;
	readonly iMediaFoundation mf;
	readonly string micEndpoint;
	readonly string languageCode;
	readonly bool noContext;
	readonly sCaptureParams captureParams;
	readonly Thread thread;
	readonly Action<string> onTextReady;
	readonly SynchronizationContext uiContext;
	readonly Action<string>? onError;
	readonly object sync = new();
	volatile bool stopRequested;
	ExceptionDispatchInfo? edi;
	bool joinAttempted;
	bool disposed;

	public CaptureSession( string modelPath, string micEndpoint, string languageCode,
		float minDuration, float maxDuration, float dropStartSilence, float pauseDuration,
		bool noContext, Action<string> onTextReady, SynchronizationContext uiContext,
		Action<string>? onError = null )
	{
		this.micEndpoint = micEndpoint;
		this.languageCode = languageCode;
		this.noContext = noContext;
		this.onTextReady = onTextReady;
		this.uiContext = uiContext;
		this.onError = onError;
		mf = Library.initMediaFoundation();

		bool started = false;
		try
		{
			captureParams = new sCaptureParams( true );
			captureParams.minDuration = minDuration;
			captureParams.maxDuration = maxDuration;
			captureParams.dropStartSilence = dropStartSilence;
			captureParams.pauseDuration = pauseDuration;

			model = Library.loadModel( modelPath );

			thread = new Thread( ThreadMain ) { Name = "WhisperTray Capture", IsBackground = true };
			thread.Start();
			started = true;
		}
		finally
		{
			// If loadModel or thread.Start threw, dispose already-created native resources.
			// Dispose() will never be called on a partially-constructed object.
			if( !started )
			{
				model?.Dispose();
				mf.Dispose();
			}
		}
	}

	void JoinThread()
	{
		stopRequested = true;
		// Only wait once — Stop() then Dispose() should not double-wait 30s on a stuck thread
		lock( sync )
		{
			if( joinAttempted )
				return;
			joinAttempted = true;
		}
		if( !thread.Join( TimeSpan.FromSeconds( 15 ) ) )
			DebugLog.Write( "CaptureSession: capture thread did not exit within 15 seconds" );
	}

	public void Stop()
	{
		JoinThread();
		edi?.Throw();
	}

	public void Dispose()
	{
		lock( sync )
		{
			if( disposed )
				return;
			disposed = true;
		}

		JoinThread();

		// Only dispose COM objects if the background thread has actually exited.
		// If the join timed out, the thread may still hold references into native code;
		// leaking is safer than use-after-free.
		if( !thread.IsAlive )
		{
			model?.Dispose();
			mf?.Dispose();
		}
	}

	void ThreadMain()
	{
		try
		{
			// runCapture exits when the audio device disconnects (end of stream).
			// Create a fresh context each iteration to avoid hallucination loops.
			while( !stopRequested )
			{
				try
				{
					var cp = captureParams;
					using var capture = mf.openCaptureDevice( micEndpoint, ref cp );
					using var context = model.createContext();

					eLanguage? lang = Library.languageFromCode( languageCode );
					if( lang.HasValue )
						context.parameters.language = lang.Value;

					// NoContext prevents repetition loops by not carrying previous transcription as decoder context
					context.parameters.setFlag( eFullParamsFlags.NoContext, noContext );

					var callbacks = new TranscribeCallbacks( onTextReady, uiContext );
					var control = new CaptureControl( this );
					context.runCapture( capture, callbacks, control );
				}
				catch when( stopRequested )
				{
					// Stop was requested — exit the loop cleanly
					break;
				}
				catch( Exception ex )
				{
					// Transient error (device unplugged, temporarily unavailable, etc.)
					// Log and retry after a longer delay
					DebugLog.Write( $"Capture error, will retry: {ex.Message}" );
				}

				if( !stopRequested )
					Thread.Sleep( 2000 ); // Pause before reconnecting (longer for error recovery)
			}
		}
		catch( Exception ex )
		{
			// Notify UI that capture died unexpectedly.
			// Use onError callback if available; otherwise capture in edi for Stop() to rethrow.
			// Don't do both — that causes double error reporting to the user.
			if( onError != null )
				uiContext.Post( _ => onError( ex.Message ), null );
			else
				edi = ExceptionDispatchInfo.Capture( ex );
		}
	}

	// Regex to filter Whisper hallucination tokens like [BLANK_AUDIO], [ Pause ], (laughs), etc.
	static readonly System.Text.RegularExpressions.Regex hallucinationPattern =
		new( @"\[.*?\]|\(.*?\)", System.Text.RegularExpressions.RegexOptions.Compiled );

	static string? FilterText( string? text )
	{
		if( string.IsNullOrWhiteSpace( text ) )
			return null;
		string filtered = hallucinationPattern.Replace( text, "" ).Trim();
		return filtered.Length > 0 ? filtered : null;
	}

	sealed class TranscribeCallbacks : Callbacks
	{
		readonly Action<string> onTextReady;
		readonly SynchronizationContext syncCtx;

		public TranscribeCallbacks( Action<string> onTextReady, SynchronizationContext syncCtx )
		{
			this.onTextReady = onTextReady;
			this.syncCtx = syncCtx;
		}

		protected override void onNewSegment( Context sender, int countNew )
		{
			TranscribeResult res = sender.results( eResultFlags.None );
			int s0 = Math.Max( 0, res.segments.Length - countNew );

			for( int i = s0; i < res.segments.Length; i++ )
			{
				// Extract text immediately — TranscribeResult is a ref struct referencing native memory
				string? text = FilterText( res.segments[i].text );
				if( text == null )
					continue;

				syncCtx.Post( _ => onTextReady( text ), null );
			}
		}
	}

	sealed class CaptureControl : CaptureCallbacks
	{
		readonly CaptureSession session;
		public CaptureControl( CaptureSession session ) => this.session = session;

		protected override bool shouldCancel( Context sender ) => session.stopRequested;
	}
}

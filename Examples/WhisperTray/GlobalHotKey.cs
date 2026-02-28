namespace WhisperTray;

sealed class GlobalHotKey : NativeWindow, IDisposable
{
	const int hotkeyId = 0x7001;
	readonly Action callback;
	bool registered;
	bool disposed;

	public GlobalHotKey( uint modifiers, uint vk, Action callback )
	{
		this.callback = callback;

		var cp = new CreateParams();
		// HWND_MESSAGE parent creates a message-only window
		cp.Parent = new IntPtr( -3 );
		CreateHandle( cp );

		try
		{
			registered = NativeInterop.RegisterHotKey(
				Handle, hotkeyId,
				modifiers | NativeInterop.MOD_NOREPEAT, vk );

			if( !registered )
				throw new InvalidOperationException(
					$"Failed to register hotkey. Another application may already use this combination. Error: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}" );
		}
		catch
		{
			DestroyHandle();
			throw;
		}
	}

	protected override void WndProc( ref Message m )
	{
		if( m.Msg == NativeInterop.WM_HOTKEY && m.WParam.ToInt32() == hotkeyId )
		{
			callback();
			return;
		}
		base.WndProc( ref m );
	}

	public void Dispose()
	{
		if( disposed )
			return;
		disposed = true;

		if( registered )
		{
			NativeInterop.UnregisterHotKey( Handle, hotkeyId );
			registered = false;
		}
		DestroyHandle();
		GC.SuppressFinalize( this );
	}
}

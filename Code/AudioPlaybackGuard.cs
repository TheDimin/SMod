namespace Smod;

/// <summary>Contains presentation failures without interrupting its gameplay caller.</summary>
public sealed class AudioPlaybackGuard
{
	private readonly Action<Exception> _report;
	public bool IsFaulted { get; private set; }

	public AudioPlaybackGuard( Action<Exception> report )
	{
		_report = report ?? throw new ArgumentNullException( nameof( report ) );
	}

	public bool TryRun( Action operation )
	{
		if ( IsFaulted ) return false;
		try
		{
			operation();
			return true;
		}
		catch ( Exception exception )
		{
			Report( exception );
			return false;
		}
	}

	/// <summary>Cleanup must still run after a failure, and one cleanup failure must not prevent another.</summary>
	public void Cleanup( Action operation )
	{
		try { operation(); }
		catch ( Exception exception ) { Report( exception ); }
	}

	private void Report( Exception exception )
	{
		if ( IsFaulted ) return;
		IsFaulted = true;
		// Diagnostics are also presentation work and must not break the caller.
		try { _report( exception ); }
		catch ( Exception ) { }
	}
}

public static partial class AudioSystem
{
	private sealed class PlaybackFailureReport
	{
		public bool Reported;
	}
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioEvent, PlaybackFailureReport> PlaybackFailures = new();

	/// <summary>Play optional presentation audio. Creation, configuration, playback and cleanup failures never escape.</summary>
	public static bool TryPlay( AudioEvent audioEvent, Action<AudioEventInstance> configure = null )
	{
		if ( audioEvent is null ) return false;
		var report = PlaybackFailures.GetOrCreateValue( audioEvent );
		var guard = new AudioPlaybackGuard( exception =>
		{
			if ( report.Reported ) return;
			report.Reported = true;
			Log.Warning( $"Audio playback failed for '{audioEvent.ResourcePath}': {exception.Message}" );
		} );
		AudioEventInstance instance = null;
		var played = guard.TryRun( () =>
		{
			instance = GetEvent( audioEvent ).CreateInstance();
			configure?.Invoke( instance );
			instance.Start();
			instance.Release();
		} );
		if ( !played && instance is not null )
		{
			guard.Cleanup( () => instance.Stop( AudioStopMode.Immediate ) );
			guard.Cleanup( instance.Release );
		}
		return played;
	}
}

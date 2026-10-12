using System;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Editor;

internal enum AudioPreviewSource
{
	ProjectSoundFile,
	CloudSound,
	ExternalFile
}

internal enum AudioPreviewStatus
{
	Idle,
	Loading,
	Playing,
	Error
}

internal readonly record struct AudioPreviewState(
	AudioPreviewSource Source,
	string ItemId,
	string DisplayName,
	AudioPreviewStatus Status,
	string Message )
{
	public static AudioPreviewState Idle => new(
		AudioPreviewSource.ProjectSoundFile,
		null,
		null,
		AudioPreviewStatus.Idle,
		null );
}

internal interface IAudioPreviewHandle
{
	bool IsPlaying { get; }
	void Stop();
}

internal sealed class AudioPreviewPlaybackService
{
	private static readonly AudioPreviewPlaybackService Singleton = new();

	private readonly Func<CancellationToken, Task> _switchToMainThread;
	private readonly Func<SoundFile, IAudioPreviewHandle> _playSound;
	private readonly Func<SoundFile, bool> _isSoundValid;
	private CancellationTokenSource _loadCancellation;
	private IAudioPreviewHandle _handle;
	private object _owner;
	private long _generation;

	public static AudioPreviewPlaybackService Instance => Singleton;
	public AudioPreviewState State { get; private set; } = AudioPreviewState.Idle;
	public event Action<AudioPreviewState> StateChanged;

	public AudioPreviewPlaybackService()
		: this( SwitchToMainThreadAsync, PlaySound, sound => sound?.IsValid() ?? false )
	{
	}

	internal AudioPreviewPlaybackService(
		Func<CancellationToken, Task> switchToMainThread,
		Func<SoundFile, IAudioPreviewHandle> playSound,
		Func<SoundFile, bool> isSoundValid )
	{
		_switchToMainThread = switchToMainThread ?? throw new ArgumentNullException( nameof( switchToMainThread ) );
		_playSound = playSound ?? throw new ArgumentNullException( nameof( playSound ) );
		_isSoundValid = isSoundValid ?? throw new ArgumentNullException( nameof( isSoundValid ) );
	}

	public async Task PlayAsync(
		object owner,
		AudioPreviewSource source,
		string itemId,
		string displayName,
		Func<CancellationToken, Task<SoundFile>> loadSound,
		Action stopTimeline )
	{
		ArgumentNullException.ThrowIfNull( owner );
		ArgumentException.ThrowIfNullOrWhiteSpace( itemId );
		ArgumentNullException.ThrowIfNull( loadSound );
		ArgumentNullException.ThrowIfNull( stopTimeline );

		Stop();
		stopTimeline();

		var generation = ++_generation;
		var cancellation = new CancellationTokenSource();
		_loadCancellation = cancellation;
		_owner = owner;
		SetState( new AudioPreviewState(
			source,
			itemId,
			displayName,
			AudioPreviewStatus.Loading,
			"Loading preview..." ) );

		try
		{
			var sound = await loadSound( cancellation.Token );
			await _switchToMainThread( cancellation.Token );
			if ( !IsCurrent( generation, cancellation ) )
				return;
			if ( !_isSoundValid( sound ) )
				throw new InvalidOperationException( $"Could not decode '{displayName ?? itemId}'." );

			var handle = _playSound( sound );
			if ( handle is null || !handle.IsPlaying )
			{
				handle?.Stop();
				throw new InvalidOperationException( $"Could not preview '{displayName ?? itemId}'." );
			}
			if ( !IsCurrent( generation, cancellation ) )
			{
				handle.Stop();
				return;
			}

			_handle = handle;
			_loadCancellation = null;
			cancellation.Dispose();
			SetState( State with
			{
				Status = AudioPreviewStatus.Playing,
				Message = "Playing preview"
			} );
		}
		catch ( OperationCanceledException ) when ( cancellation.IsCancellationRequested || !IsCurrent( generation, cancellation ) )
		{
		}
		catch ( Exception exception )
		{
			await _switchToMainThread( CancellationToken.None );
			if ( !IsCurrent( generation, cancellation ) )
				return;

			_loadCancellation = null;
			cancellation.Dispose();
			SetState( State with
			{
				Status = AudioPreviewStatus.Error,
				Message = $"Preview failed: {exception.Message}"
			} );
		}
		finally
		{
			if ( ReferenceEquals( _loadCancellation, cancellation )
				&& State.Status != AudioPreviewStatus.Loading )
			{
				_loadCancellation = null;
				cancellation.Dispose();
			}
		}
	}

	public Task ToggleAsync(
		object owner,
		AudioPreviewSource source,
		string itemId,
		string displayName,
		Func<CancellationToken, Task<SoundFile>> loadSound,
		Action stopTimeline )
	{
		if ( IsActive( source, itemId ) )
		{
			Stop();
			return Task.CompletedTask;
		}

		return PlayAsync( owner, source, itemId, displayName, loadSound, stopTimeline );
	}

	public bool IsActive( AudioPreviewSource source, string itemId )
	{
		return State.Source == source
			&& string.Equals( State.ItemId, itemId, StringComparison.OrdinalIgnoreCase )
			&& State.Status is AudioPreviewStatus.Loading or AudioPreviewStatus.Playing;
	}

	public bool IsCurrentItem( AudioPreviewSource source, string itemId )
	{
		return State.Source == source
			&& string.Equals( State.ItemId, itemId, StringComparison.OrdinalIgnoreCase )
			&& State.Status != AudioPreviewStatus.Idle;
	}

	public void StopForOwner( object owner )
	{
		if ( ReferenceEquals( _owner, owner ) )
			Stop();
	}

	public void Stop()
	{
		var hadState = State.Status != AudioPreviewStatus.Idle
			|| _loadCancellation is not null
			|| _handle is not null;

		_generation++;
		_loadCancellation?.Cancel();
		_loadCancellation?.Dispose();
		_loadCancellation = null;
		_handle?.Stop();
		_handle = null;
		_owner = null;

		if ( hadState )
			SetState( AudioPreviewState.Idle );
	}

	internal void Update()
	{
		if ( _handle is not null && !_handle.IsPlaying )
			Stop();
	}

	private bool IsCurrent( long generation, CancellationTokenSource cancellation )
	{
		return generation == _generation
			&& ReferenceEquals( _loadCancellation, cancellation )
			&& !cancellation.IsCancellationRequested;
	}

	private void SetState( AudioPreviewState state )
	{
		State = state;
		StateChanged?.Invoke( state );
	}

	private static async Task SwitchToMainThreadAsync( CancellationToken token )
	{
		await GameTask.MainThread( token );
	}

	private static IAudioPreviewHandle PlaySound( SoundFile sound )
	{
		var handle = Sound.PlayFile( sound, 0.5f, 1f );
		if ( !(handle?.IsValid() ?? false) )
			return null;

		handle.TargetMixer = AudioEditorPreviewOutput.GetMixer();
		handle.ListenLocal = true;
		handle.Position = Vector3.Forward * 64f;
		return new SoundPreviewHandle( handle );
	}

	[EditorEvent.Frame]
	private static void UpdateSingleton()
	{
		Singleton.Update();
	}

	[Event( "app.exit" )]
	private static void StopSingleton()
	{
		Singleton.Stop();
	}

	private sealed class SoundPreviewHandle : IAudioPreviewHandle
	{
		private SoundHandle _handle;

		public SoundPreviewHandle( SoundHandle handle )
		{
			_handle = handle;
		}

		public bool IsPlaying => (_handle?.IsValid() ?? false) && _handle.IsPlaying;

		public void Stop()
		{
			_handle?.Stop();
			_handle = null;
		}
	}
}

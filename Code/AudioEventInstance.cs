using System.Threading;
using System.Threading.Tasks;
namespace Smod;

public enum AudioStopMode
{
	Immediate,
	AllowFadeout
}

public enum AudioPlaybackState
{
	Stopped,
	Starting,
	Playing,
	Stopping
}

public readonly record struct Audio3DAttributes
{
	public Audio3DAttributes( Vector3 position )
		: this( position, Vector3.Zero, Vector3.Forward, Vector3.Up )
	{
	}

	public Audio3DAttributes( Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up )
	{
		Position = position;
		Velocity = velocity;
		Forward = forward;
		Up = up;
		AssertValid();
	}

	public Vector3 Position { get; }
	public Vector3 Velocity { get; }
	public Vector3 Forward { get; }
	public Vector3 Up { get; }

	internal void AssertValid()
	{
		if ( !IsFinite( Position ) || !IsFinite( Velocity ) || !IsFinite( Forward ) || !IsFinite( Up ) )
			throw new ArgumentOutOfRangeException( nameof( Audio3DAttributes ), "3D audio attributes must be finite." );
		if ( Forward.LengthSquared <= 0.000001f )
			throw new ArgumentOutOfRangeException( nameof( Forward ), "3D audio forward direction cannot be zero." );
		if ( Up.LengthSquared <= 0.000001f )
			throw new ArgumentOutOfRangeException( nameof( Up ), "3D audio up direction cannot be zero." );
	}

	private static bool IsFinite( Vector3 value )
	{
		return float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
	}
}

public static class AudioSystem
{
	private static IAudioPlaybackBackend _backend = new SboxAudioPlaybackBackend();
	private static IAudioTimelineScheduler _timelineScheduler = new RealtimeAudioTimelineScheduler();

	public static AudioEventDescription GetEvent( AudioEvent audioEvent )
	{
		ArgumentNullException.ThrowIfNull( audioEvent );
		AudioEventValidator.Validate( audioEvent ).AssertValid();
		return new AudioEventDescription( audioEvent );
	}

	public static AudioEventDescription GetEvent( string resourcePath )
	{
		return GetEvent( RequireEventResource( resourcePath ) );
	}

	public static AudioEvent RequireEventResource( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			throw new ArgumentException( "Audio event resource path is required.", nameof( resourcePath ) );

		var resource = ResourceLibrary.Get<AudioEvent>( resourcePath );
		if ( resource is null )
			throw new InvalidOperationException( $"Required audio event resource '{resourcePath}' could not be loaded." );
		return resource;
	}

	internal static IAudioPlaybackBackend Backend => _backend;
	internal static IAudioTimelineScheduler TimelineScheduler => _timelineScheduler;

	internal static void SetBackendForTesting( IAudioPlaybackBackend backend )
	{
		_backend = backend ?? throw new ArgumentNullException( nameof( backend ) );
	}

	internal static void SetTimelineSchedulerForTesting( IAudioTimelineScheduler timelineScheduler )
	{
		_timelineScheduler = timelineScheduler ?? throw new ArgumentNullException( nameof( timelineScheduler ) );
	}

	internal static void ResetBackendForTesting()
	{
		_backend = new SboxAudioPlaybackBackend();
		_timelineScheduler = new RealtimeAudioTimelineScheduler();
	}
}

public sealed class AudioEventDescription
{
	internal AudioEventDescription( AudioEvent resource )
	{
		Resource = resource;
	}

	public AudioEvent Resource { get; }

	public AudioEventInstance CreateInstance()
	{
		return new AudioEventInstance( this );
	}
}

/// <summary>
/// Read-only playback state for one authored instrument. This is suitable for editor
/// audition visualization without exposing or taking ownership of the native voice.
/// </summary>
public readonly record struct AudioInstrumentPlaybackState(
	AudioTrack Track,
	AudioInstrument Instrument,
	bool IsPlaying,
	bool IsPending,
	bool IsPaused,
	float PlaybackPosition,
	SoundEventTrack SelectedTrack,
	SoundFile SelectedSound );

public sealed class AudioEventInstance
{
	private readonly AudioEventDescription _description;
	private readonly Dictionary<AudioParameter, AudioParameterValue> _parameters = new( ReferenceEqualityComparer.Instance );
	private readonly Dictionary<AudioInstrument, SoundEventTrack> _selectedTracks = new( ReferenceEqualityComparer.Instance );
	private readonly List<PlayingVoice> _voices = new();
	private readonly HashSet<AudioTrack> _auditionMutedTracks = new( ReferenceEqualityComparer.Instance );
	private readonly AudioTimelineControlEvaluator _timelineControl;
	private Audio3DAttributes _attributes3D = new( Vector3.Zero );
	private AudioPlaybackState _playbackState;
	private CancellationTokenSource _playbackCancellation;
	private float _timelinePosition;
	private float _timelineAnchor;
	private float _volume = 1f;
	private float _pitch = 1f;
	private bool _hasAttributes3D;
	private bool _paused;
	private bool _released;
	private AudioTimelineDebugSnapshot _timelineDebug;

	internal AudioEventInstance( AudioEventDescription description )
	{
		_description = description ?? throw new ArgumentNullException( nameof( description ) );
		_timelineControl = new AudioTimelineControlEvaluator( description.Resource );
		foreach ( var parameter in description.Resource.Parameters )
		{
			_parameters.Add(
				parameter,
				parameter.Type == AudioParameterType.Labeled
					? new AudioParameterValue( 0f, parameter.DefaultLabel, true )
					: new AudioParameterValue( parameter.DefaultValue, null, false ) );
		}
	}

	public bool IsValid => !_released;
	public AudioTimelineDebugSnapshot TimelineDebugSnapshot
	{
		get
		{
			EnsureValid();
			RefreshPlaybackState();
			return _timelineDebug;
		}
	}

	/// <summary>
	/// A current snapshot of the instruments owned by this instance, including the
	/// exact file selected by the native <see cref="SoundEvent"/> playlist.
	/// </summary>
	public IReadOnlyList<AudioInstrumentPlaybackState> InstrumentPlaybackStates
	{
		get
		{
			EnsureValid();
			RefreshPlaybackState();
			var states = new List<AudioInstrumentPlaybackState>( _voices.Count );
			foreach ( var voice in _voices )
			{
				states.Add( new AudioInstrumentPlaybackState(
					voice.Track,
					voice.Instrument,
					!voice.Pending && voice.Voice.IsPlaying,
					voice.Pending,
					!voice.Pending && voice.Voice.Paused,
					voice.Voice.Time,
					voice.SoundTrack,
					voice.Voice.SelectedSound ) );
			}
			return states;
		}
	}

	public AudioPlaybackState PlaybackState
	{
		get
		{
			RefreshPlaybackState();
			return _playbackState;
		}
	}

	public float TimelinePosition
	{
		get
		{
			RefreshPlaybackState();
			return CurrentTimelinePosition;
		}
		set
		{
			EnsureValid();
			if ( !float.IsFinite( value ) || value < 0f )
				throw new ArgumentOutOfRangeException( nameof( value ), "Timeline position must be finite and non-negative." );

			RefreshPlaybackState();
			_timelinePosition = value;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			_timelineControl.Reset( ToTimelineSample( value ) );
			_timelineDebug = default;
			if ( _playbackState != AudioPlaybackState.Stopped )
				ReevaluatePlayback( repositionExistingVoices: true );
		}
	}

	public bool Paused
	{
		get => _paused;
		set
		{
			EnsureValid();
			if ( _paused == value )
				return;

			RefreshPlaybackState();
			_timelinePosition = CurrentTimelinePosition;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			_paused = value;

			foreach ( var playingVoice in _voices )
			{
				playingVoice.Voice.Paused = value || playingVoice.Pending;
				if ( value )
				{
					playingVoice.CancelScheduledStart();
					playingVoice.CancelScheduledStop();
				}
				else if ( playingVoice.Pending )
				{
					SchedulePendingVoice( playingVoice );
				}
				else
				{
					ScheduleTrimStop( playingVoice );
				}
			}
		}
	}

	public Audio3DAttributes Attributes3D
	{
		get => _attributes3D;
		set
		{
			EnsureValid();
			value.AssertValid();
			_attributes3D = value;
			_hasAttributes3D = true;
			foreach ( var playingVoice in _voices )
			{
				playingVoice.Voice.ListenLocal = false;
				playingVoice.Voice.Position = value.Position;
			}
		}
	}

	public float Volume
	{
		get => _volume;
		set
		{
			EnsureValid();
			if ( !float.IsFinite( value ) || value < 0f )
				throw new ArgumentOutOfRangeException( nameof( value ), "Audio event instance volume must be finite and non-negative." );
			_volume = value;
			ApplyInstanceGain();
		}
	}

	public float Pitch
	{
		get => _pitch;
		set
		{
			EnsureValid();
			if ( !float.IsFinite( value ) || value <= 0f )
				throw new ArgumentOutOfRangeException( nameof( value ), "Audio event instance pitch must be finite and greater than zero." );
			_pitch = value;
			ApplyInstanceGain();
		}
	}

	/// <summary>
	/// Temporarily mutes one authored track on this instance without changing the AudioEvent resource.
	/// Intended for editor audition controls.
	/// </summary>
	public void SetTrackMutedForAudition( AudioTrack track, bool muted )
	{
		EnsureValid();
		ArgumentNullException.ThrowIfNull( track );

		var audioEvent = _description.Resource;
		var belongsToEvent = ReferenceEquals( audioEvent.MasterTrack, track )
			|| audioEvent.Tracks.Any( candidate => ReferenceEquals( candidate, track ) );
		if ( !belongsToEvent )
			throw new ArgumentException( "Audition tracks must belong to this audio event.", nameof( track ) );

		var changed = muted
			? _auditionMutedTracks.Add( track )
			: _auditionMutedTracks.Remove( track );
		if ( !changed )
			return;

		var masterChanged = ReferenceEquals( audioEvent.MasterTrack, track );
		foreach ( var voice in _voices )
		{
			if ( masterChanged || ReferenceEquals( voice.Track, track ) )
				ApplyGain( voice );
		}
	}

	/// <summary>
	/// Re-evaluates live-authored track, condition, and automation values without seeking unaffected voices.
	/// Intended for editor live update.
	/// </summary>
	public void RefreshAuthoredPropertiesForAudition()
	{
		EnsureValid();
		RefreshPlaybackState();
		if ( _playbackState != AudioPlaybackState.Stopped )
			ReevaluatePlayback( repositionExistingVoices: false );
	}

	public void SetParameterByName( string name, float value )
	{
		EnsureCanChangeParameters();
		var parameter = RequireParameter( name );
		if ( parameter.Type == AudioParameterType.Labeled )
			throw new InvalidOperationException( $"Audio parameter '{name}' is labeled; use {nameof( SetParameterByNameWithLabel )}." );
		if ( !float.IsFinite( value ) || value < parameter.Minimum || value > parameter.Maximum )
			throw new ArgumentOutOfRangeException( nameof( value ), $"Audio parameter '{name}' expects a value from {parameter.Minimum} to {parameter.Maximum}." );
		if ( parameter.Type == AudioParameterType.Discrete && MathF.Abs( value - MathF.Round( value ) ) > 0.0001f )
			throw new ArgumentOutOfRangeException( nameof( value ), $"Discrete audio parameter '{name}' requires a whole number." );
		_parameters[parameter] = new AudioParameterValue( value, null, false );
		ReevaluateIfPlaying();
	}

	public void SetParameterByNameWithLabel( string name, string label )
	{
		EnsureCanChangeParameters();
		var parameter = RequireParameter( name );
		if ( parameter.Type != AudioParameterType.Labeled )
			throw new InvalidOperationException( $"Audio parameter '{name}' is not labeled." );
		var resolved = parameter.Labels.FirstOrDefault( candidate => string.Equals( candidate, label, StringComparison.Ordinal ) );
		if ( resolved is null )
			throw new ArgumentOutOfRangeException( nameof( label ), $"Audio parameter '{name}' does not define label '{label}'." );
		_parameters[parameter] = new AudioParameterValue( 0f, resolved, true );
		ReevaluateIfPlaying();
	}

	public void Start()
	{
		EnsureValid();
		RefreshPlaybackState();
		if ( _playbackState != AudioPlaybackState.Stopped )
			Stop( AudioStopMode.Immediate );

		_playbackCancellation?.Dispose();
		_playbackCancellation = new CancellationTokenSource();
		_timelineAnchor = AudioSystem.TimelineScheduler.Now;
		_timelineControl.Reset( ToTimelineSample( _timelinePosition ) );
		_timelineDebug = default;
		_playbackState = AudioPlaybackState.Starting;
		_voices.Clear();
		_selectedTracks.Clear();
		ReevaluatePlayback( repositionExistingVoices: true );
	}

	public void Stop( AudioStopMode mode )
	{
		EnsureValid();
		_playbackCancellation?.Cancel();
		var fadeTime = mode == AudioStopMode.AllowFadeout ? _description.Resource.FadeOutDuration : 0f;
		foreach ( var voice in _voices )
		{
			voice.CancelScheduledStart();
			voice.CancelScheduledStop();
			voice.Voice.Stop( voice.Pending ? 0f : fadeTime );
		}
		_playbackState = fadeTime > 0f && _voices.Any( voice => !voice.Pending && voice.Voice.IsPlaying )
			? AudioPlaybackState.Stopping
			: AudioPlaybackState.Stopped;
	}

	public void Release()
	{
		EnsureValid();
		_released = true;
	}

	private void ApplyInstanceGain()
	{
		foreach ( var voice in _voices )
			ApplyGain( voice );
	}

	private void RefreshPlaybackState()
	{
		if ( _playbackState is not (AudioPlaybackState.Playing or AudioPlaybackState.Stopping) )
			return;

		if ( !_paused )
			ProcessTimelineControl();
		ApplyInstanceGain();

		if ( _voices.All( voice => !voice.Pending && !voice.Voice.IsPlaying )
			&& !HasConditionalPlayback()
			&& !HasActiveContentLoop() )
		{
			_timelinePosition = CurrentTimelinePosition;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			_playbackState = AudioPlaybackState.Stopped;
		}
	}

	private float CurrentTimelinePosition => _playbackState == AudioPlaybackState.Stopped || _paused
		? _timelinePosition
		: _timelinePosition + MathF.Max( 0f, AudioSystem.TimelineScheduler.Now - _timelineAnchor );

	private long ToTimelineSample( float seconds )
	{
		return checked((long)Math.Round( seconds * _description.Resource.TimelineSampleRate, MidpointRounding.AwayFromZero ));
	}

	private void ProcessTimelineControl()
	{
		var rawPosition = _timelinePosition + MathF.Max( 0f, AudioSystem.TimelineScheduler.Now - _timelineAnchor );
		var rawSample = ToTimelineSample( rawPosition );
		if ( rawSample == _timelineDebug.CurrentSample )
			return;
		var completedContentLoopId = FindCompletedContentLoop( rawSample );
		var result = _timelineControl.Advance( rawSample, _parameters, completedContentLoopId );
		var resolvedPosition = (float)result.Position / _description.Resource.TimelineSampleRate;
		var jumped = result.Jumps > 0;
		if ( jumped )
		{
			var retriggeredInstruments = PrepareLoopRetriggers( result.ExecutedLoopRegionIds );
			_timelinePosition = resolvedPosition;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			ReevaluatePlayback( repositionExistingVoices: true, retriggeredInstruments );
		}
		TransitionRegion pendingRegion = null;
		foreach ( var region in _description.Resource.TransitionRegions )
		{
			if ( region.Id == _timelineControl.PendingRegionId )
			{
				pendingRegion = region;
				break;
			}
		}
		_timelineDebug = new AudioTimelineDebugSnapshot(
			result.Position,
			FindActiveLoop( result.Position ),
			_timelineControl.PendingRegionId,
			pendingRegion?.DestinationMarkerId ?? Guid.Empty,
			_timelineControl.PendingExecutionSample,
			result.Jumps,
			result.LimitExceeded );
		if ( result.LimitExceeded )
			Log.Warning( $"[AdaptiveAudio] Event '{_description.Resource.ResourceName}' exceeded {_description.Resource.MaximumTimelineJumpsPerUpdate} timeline jumps in one update." );
	}

	private Guid FindActiveLoop( long sample )
	{
		foreach ( var loop in _description.Resource.LoopRegions )
		{
			if ( sample >= loop.Start.ToSamples( _description.Resource.TimelineSampleRate )
				&& sample < loop.End.ToSamples( _description.Resource.TimelineSampleRate ) )
				return loop.Id;
		}
		return Guid.Empty;
	}

	private Guid FindCompletedContentLoop( long sample )
	{
		var audioEvent = _description.Resource;
		foreach ( var loop in audioEvent.LoopRegions )
		{
			if ( loop.EndMode != LoopEndMode.ContentEnd
				|| sample < loop.Start.ToSamples( audioEvent.TimelineSampleRate )
				|| sample >= loop.End.ToSamples( audioEvent.TimelineSampleRate )
				|| !AudioTimelineControlEvaluator.MatchesLoopConditions( loop, _parameters ) )
				continue;

			var loopVoices = _voices
				.Where( voice => voice.Instrument.StartTime >= loop.Start.Seconds
					&& voice.Instrument.StartTime < loop.End.Seconds )
				.ToArray();
			if ( loopVoices.Length > 0 && loopVoices.All( voice => !voice.Pending && !voice.Voice.IsPlaying ) )
				return loop.Id;
		}

		return Guid.Empty;
	}

	private bool HasActiveContentLoop()
	{
		var sample = ToTimelineSample( CurrentTimelinePosition );
		var audioEvent = _description.Resource;
		foreach ( var loop in audioEvent.LoopRegions )
		{
			if ( loop.EndMode == LoopEndMode.ContentEnd
				&& sample >= loop.Start.ToSamples( audioEvent.TimelineSampleRate )
				&& sample < loop.End.ToSamples( audioEvent.TimelineSampleRate )
				&& AudioTimelineControlEvaluator.MatchesLoopConditions( loop, _parameters ) )
				return true;
		}

		return false;
	}

	private AudioParameter RequireParameter( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Audio parameter name is required.", nameof( name ) );
		return _description.Resource.Parameters.FirstOrDefault( parameter => string.Equals( parameter.Name, name, StringComparison.OrdinalIgnoreCase ) )
			?? throw new InvalidOperationException( $"Audio event '{_description.Resource.ResourcePath}' does not define parameter '{name}'." );
	}

	private void EnsureCanChangeParameters()
	{
		EnsureValid();
		RefreshPlaybackState();
	}

	private void ReevaluateIfPlaying()
	{
		if ( _playbackState != AudioPlaybackState.Stopped )
			ReevaluatePlayback( repositionExistingVoices: false );
	}

	private void ReevaluatePlayback(
		bool repositionExistingVoices,
		IReadOnlySet<AudioInstrument> retriggeredInstruments = null )
	{
		var position = CurrentTimelinePosition;
		var requests = AudioEventEvaluator.BuildPlaybackPlan( _description.Resource, _parameters, SelectSoundTrack );
		var requestedInstruments = requests.Select( request => request.Instrument ).ToHashSet( ReferenceEqualityComparer.Instance );

		for ( var i = _voices.Count - 1; i >= 0; i-- )
		{
			var playingVoice = _voices[i];
			if ( requestedInstruments.Contains( playingVoice.Instrument ) )
				continue;

			playingVoice.CancelScheduledStart();
			playingVoice.CancelScheduledStop();
			playingVoice.Voice.Stop( 0f );
			_voices.RemoveAt( i );
		}

		foreach ( var request in requests )
		{
			var playingVoice = _voices.FirstOrDefault( candidate => ReferenceEquals( candidate.Instrument, request.Instrument ) );
			var createdVoice = false;
			if ( playingVoice is not null && (retriggeredInstruments?.Contains( request.Instrument ) ?? false) )
			{
				playingVoice.CancelScheduledStart();
				playingVoice.CancelScheduledStop();
				playingVoice.Voice.Stop( 0f );
				_voices.Remove( playingVoice );
				playingVoice = null;
			}
			if ( playingVoice is not null && !ReferenceEquals( playingVoice.SoundTrack, request.SoundTrack ) )
			{
				playingVoice.CancelScheduledStart();
				playingVoice.CancelScheduledStop();
				playingVoice.Voice.Stop( 0f );
				_voices.Remove( playingVoice );
				playingVoice = null;
			}
			if ( retriggeredInstruments is not null
				&& playingVoice is not null
				&& !playingVoice.Voice.IsPlaying
				&& !retriggeredInstruments.Contains( request.Instrument ) )
				continue;
			if ( playingVoice is null || !playingVoice.Voice.IsPlaying )
			{
				if ( playingVoice is not null )
				{
					playingVoice.CancelScheduledStart();
					playingVoice.CancelScheduledStop();
					_voices.Remove( playingVoice );
				}

				var voice = AudioSystem.Backend.Play( request.Sound, _attributes3D, !_hasAttributes3D );
				voice.Paused = true;
				playingVoice = new PlayingVoice( request.Track, request.Instrument, request.SoundTrack, voice, voice.Volume, voice.Pitch );
				_voices.Add( playingVoice );
				createdVoice = true;
			}

			playingVoice.Track = request.Track;
			playingVoice.EventVolume = request.Volume;
			playingVoice.EventPitch = request.Pitch;
			ApplyGain( playingVoice );
			var trimChanged = playingVoice.AuthoredTrimStart != EffectiveTrimStart( playingVoice )
				|| playingVoice.AuthoredTrimEnd != EffectiveTrimEnd( playingVoice );
			if ( createdVoice || repositionExistingVoices || trimChanged )
				PositionVoice( playingVoice, position );
			playingVoice.AuthoredTrimStart = EffectiveTrimStart( playingVoice );
			playingVoice.AuthoredTrimEnd = EffectiveTrimEnd( playingVoice );
		}

		_playbackState = _voices.Count == 0 && !HasConditionalPlayback()
			? AudioPlaybackState.Stopped
			: AudioPlaybackState.Playing;
	}

	private IReadOnlySet<AudioInstrument> PrepareLoopRetriggers( IReadOnlyList<Guid> executedLoopRegionIds )
	{
		if ( executedLoopRegionIds.Count == 0 )
			return null;

		var retriggeredInstruments = new HashSet<AudioInstrument>( ReferenceEqualityComparer.Instance );
		var audioEvent = _description.Resource;
		foreach ( var loopRegionId in executedLoopRegionIds )
		{
			var loop = audioEvent.LoopRegions.First( candidate => candidate.Id == loopRegionId );
			foreach ( var track in audioEvent.Tracks.Prepend( audioEvent.MasterTrack ) )
			{
				foreach ( var instrument in track.Instruments )
				{
					if ( instrument.StartTime < loop.Start.Seconds || instrument.StartTime >= loop.End.Seconds )
						continue;

					_selectedTracks.Remove( instrument );
					retriggeredInstruments.Add( instrument );
				}
			}

			AudioEventEvaluator.BuildPlaybackPlan( audioEvent, _parameters, SelectSoundTrack );
		}

		return retriggeredInstruments;
	}

	private bool HasConditionalPlayback()
	{
		var audioEvent = _description.Resource;
		if ( audioEvent.MasterTrack.Conditions?.Count > 0 )
			return true;

		foreach ( var track in audioEvent.Tracks )
		{
			if ( track.Conditions?.Count > 0 )
				return true;
		}
		return false;
	}

	private SoundEventTrack SelectSoundTrack( AudioInstrument instrument, IReadOnlyList<SoundEventTrack> eligibleTracks )
	{
		if ( _selectedTracks.TryGetValue( instrument, out var selected ) && eligibleTracks.Contains( selected ) )
			return selected;

		selected = instrument.Sound.SelectTrack( eligibleTracks );
		_selectedTracks[instrument] = selected;
		return selected;
	}

	private void PositionVoice( PlayingVoice playingVoice, float position )
	{
		playingVoice.CancelScheduledStart();
		playingVoice.CancelScheduledStop();
		var delay = playingVoice.Instrument.StartTime - position;
		if ( delay > 0f )
		{
			playingVoice.Pending = true;
			playingVoice.Voice.Time = EffectiveTrimStart( playingVoice );
			playingVoice.Voice.Paused = true;
			if ( !_paused )
				SchedulePendingVoice( playingVoice );
			return;
		}

		playingVoice.Pending = false;
		playingVoice.Voice.Time = EffectiveTrimStart( playingVoice ) + MathF.Max( 0f, -delay );
		playingVoice.Voice.Paused = _paused;
		if ( !_paused )
			ScheduleTrimStop( playingVoice );
	}

	private void SchedulePendingVoice( PlayingVoice playingVoice )
	{
		if ( !playingVoice.Pending || _paused )
			return;

		var delay = playingVoice.Instrument.StartTime - CurrentTimelinePosition;
		if ( delay <= 0f )
		{
			playingVoice.Pending = false;
			playingVoice.Voice.Time = EffectiveTrimStart( playingVoice ) + MathF.Max( 0f, -delay );
			playingVoice.Voice.Paused = false;
			ScheduleTrimStop( playingVoice );
			return;
		}

		playingVoice.CancelScheduledStart();
		playingVoice.ScheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource( _playbackCancellation.Token );
		_ = StartPendingVoiceAsync( playingVoice, delay, playingVoice.ScheduleCancellation.Token );
	}

	private async Task StartPendingVoiceAsync( PlayingVoice playingVoice, float delay, CancellationToken cancellationToken )
	{
		try
		{
			await AudioSystem.TimelineScheduler.DelaySeconds( delay, cancellationToken );
		}
		catch ( OperationCanceledException )
		{
			return;
		}

		if ( cancellationToken.IsCancellationRequested || !playingVoice.Pending || !_voices.Contains( playingVoice ) )
			return;

		playingVoice.Pending = false;
		playingVoice.Voice.Time = EffectiveTrimStart( playingVoice );
		playingVoice.Voice.Paused = _paused;
		ApplyGain( playingVoice );
		if ( !_paused )
			ScheduleTrimStop( playingVoice );
	}

	private void ScheduleTrimStop( PlayingVoice playingVoice )
	{
		playingVoice.CancelScheduledStop();
		var trimEnd = ResolveTrimEnd( playingVoice );
		if ( !float.IsFinite( trimEnd ) )
			return;

		var remaining = playingVoice.Instrument.StartTime + trimEnd - EffectiveTrimStart( playingVoice ) - CurrentTimelinePosition;
		if ( remaining <= 0f )
		{
			playingVoice.Voice.Stop( 0f );
			return;
		}

		playingVoice.StopCancellation = CancellationTokenSource.CreateLinkedTokenSource( _playbackCancellation.Token );
		_ = StopTrimmedVoiceAsync( playingVoice, remaining, playingVoice.StopCancellation.Token );
	}

	private static float ResolveTrimEnd( PlayingVoice playingVoice )
	{
		var authoredEnd = EffectiveTrimEnd( playingVoice );
		if ( authoredEnd > 0f )
			return authoredEnd;
		var selectedSound = playingVoice.Voice.SelectedSound;
		return selectedSound.IsValid() && selectedSound.IsLoaded ? selectedSound.Duration : float.PositiveInfinity;
	}

	private static float EffectiveTrimStart( PlayingVoice playingVoice )
	{
		return playingVoice.Instrument.TrimStart + playingVoice.SoundTrack.Offset;
	}

	private static float EffectiveTrimEnd( PlayingVoice playingVoice )
	{
		var track = playingVoice.SoundTrack;
		if ( track.Length <= 0f )
			return playingVoice.Instrument.TrimEnd;
		var localEnd = playingVoice.Instrument.TrimEnd > 0f
			? MathF.Min( playingVoice.Instrument.TrimEnd, track.Length )
			: track.Length;
		return track.Offset + localEnd;
	}

	private async Task StopTrimmedVoiceAsync( PlayingVoice playingVoice, float delay, CancellationToken cancellationToken )
	{
		try
		{
			await AudioSystem.TimelineScheduler.DelaySeconds( delay, cancellationToken );
		}
		catch ( OperationCanceledException )
		{
			return;
		}

		if ( !cancellationToken.IsCancellationRequested && _voices.Contains( playingVoice ) )
			playingVoice.Voice.Stop( 0f );
	}

	private void ApplyGain( PlayingVoice playingVoice )
	{
		var muted = _auditionMutedTracks.Contains( _description.Resource.MasterTrack )
			|| _auditionMutedTracks.Contains( playingVoice.Track );
		playingVoice.Voice.Volume = muted
			? 0f
			: playingVoice.NativeVolume * playingVoice.EventVolume * _volume * InstrumentFadeGain( playingVoice );
		playingVoice.Voice.Pitch = playingVoice.NativePitch * playingVoice.EventPitch * _pitch;
	}

	private float InstrumentFadeGain( PlayingVoice playingVoice )
	{
		if ( playingVoice.Pending )
			return playingVoice.Instrument.FadeInDuration > 0f ? 0f : 1f;

		var localPosition = MathF.Max( 0f, CurrentTimelinePosition - playingVoice.Instrument.StartTime );
		var gain = 1f;
		if ( playingVoice.Instrument.FadeInDuration > 0f )
		{
			var progress = (localPosition / playingVoice.Instrument.FadeInDuration).Clamp( 0f, 1f );
			gain = MathF.Min( gain, EvaluateFadeCurve( 0f, playingVoice.Instrument.FadeInCurveMidpoint, 1f, progress ) );
		}

		var trimEnd = ResolveTrimEnd( playingVoice );
		if ( playingVoice.Instrument.FadeOutDuration > 0f && float.IsFinite( trimEnd ) )
		{
			var playbackDuration = MathF.Max( 0f, trimEnd - EffectiveTrimStart( playingVoice ) );
			var remaining = MathF.Max( 0f, playbackDuration - localPosition );
			var progress = (1f - remaining / playingVoice.Instrument.FadeOutDuration).Clamp( 0f, 1f );
			gain = MathF.Min( gain, EvaluateFadeCurve( 1f, playingVoice.Instrument.FadeOutCurveMidpoint, 0f, progress ) );
		}
		return gain.Clamp( 0f, 1f );
	}

	private static float EvaluateFadeCurve( float start, float midpoint, float end, float progress )
	{
		var inverse = 1f - progress;
		return inverse * inverse * start + 2f * inverse * progress * midpoint + progress * progress * end;
	}

	private void EnsureValid()
	{
		if ( _released )
			throw new InvalidOperationException( "Audio event instance has been released." );
	}

	private sealed class PlayingVoice
	{
		public PlayingVoice( AudioTrack track, AudioInstrument instrument, SoundEventTrack soundTrack, IAudioVoice voice, float nativeVolume, float nativePitch )
		{
			Track = track;
			Instrument = instrument;
			SoundTrack = soundTrack;
			Voice = voice;
			NativeVolume = nativeVolume;
			NativePitch = nativePitch;
		}

		public AudioTrack Track { get; set; }
		public AudioInstrument Instrument { get; }
		public SoundEventTrack SoundTrack { get; }
		public IAudioVoice Voice { get; }
		public float NativeVolume { get; }
		public float NativePitch { get; }
		public float EventVolume { get; set; } = 1f;
		public float EventPitch { get; set; } = 1f;
		public float AuthoredTrimStart { get; set; }
		public float AuthoredTrimEnd { get; set; }
		public bool Pending { get; set; }
		public CancellationTokenSource ScheduleCancellation { get; set; }
		public CancellationTokenSource StopCancellation { get; set; }

		public void CancelScheduledStart()
		{
			ScheduleCancellation?.Cancel();
			ScheduleCancellation?.Dispose();
			ScheduleCancellation = null;
		}

		public void CancelScheduledStop()
		{
			StopCancellation?.Cancel();
			StopCancellation?.Dispose();
			StopCancellation = null;
		}
	}
}

internal readonly record struct AudioParameterValue( float Number, string Label, bool IsLabel );

internal sealed record AudioVoiceRequest(
	AudioTrack Track,
	AudioInstrument Instrument,
	SoundEventTrack SoundTrack,
	SoundEvent Sound,
	float StartTime,
	float Volume,
	float Pitch );

internal static class AudioEventEvaluator
{
	public static IReadOnlyList<AudioVoiceRequest> BuildPlaybackPlan(
		AudioEvent audioEvent,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		Func<AudioInstrument, IReadOnlyList<SoundEventTrack>, SoundEventTrack> selectTrack = null )
	{
		var requests = new List<AudioVoiceRequest>();
		if ( !Matches( audioEvent.MasterTrack.Conditions, parameters ) )
			return requests;

		EvaluateTrack( audioEvent.MasterTrack, 1f, 1f, parameters, requests, selectTrack );
		foreach ( var track in audioEvent.Tracks )
		{
			if ( !Matches( track.Conditions, parameters ) )
				continue;
			EvaluateTrack(
				track,
				audioEvent.MasterTrack.Volume,
				audioEvent.MasterTrack.Pitch,
				parameters,
				requests,
				selectTrack );
		}
		return requests;
	}

	private static void EvaluateTrack(
		AudioTrack track,
		float parentVolume,
		float parentPitch,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		List<AudioVoiceRequest> requests,
		Func<AudioInstrument, IReadOnlyList<SoundEventTrack>, SoundEventTrack> selectTrack )
	{
		var trackVolume = parentVolume * track.Volume * EvaluateAutomations( track.Automations, AudioAutomationTarget.Volume, parameters );
		var trackPitch = parentPitch * track.Pitch * EvaluateAutomations( track.Automations, AudioAutomationTarget.Pitch, parameters );
		foreach ( var instrument in track.Instruments )
		{
			if ( !Matches( instrument.Conditions, parameters ) )
				continue;
			var eligibleTracks = instrument.Sound.Tracks
				.Where( soundTrack => Matches( soundTrack.Conditions, parameters ) )
				.ToArray();
			if ( eligibleTracks.Length == 0 )
				continue;
			var soundTrack = selectTrack is null
				? instrument.Sound.SelectTrack( eligibleTracks )
				: selectTrack( instrument, eligibleTracks );
			var volume = trackVolume * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Volume, parameters );
			var pitch = trackPitch * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Pitch, parameters );
			requests.Add( new AudioVoiceRequest(
				track,
				instrument,
				soundTrack,
				instrument.Sound.CreatePlaybackEvent( soundTrack ),
				instrument.StartTime,
				volume,
				pitch ) );
		}
	}

	private static bool Matches(
		IReadOnlyList<AudioParameterCondition> conditions,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		foreach ( var condition in conditions )
		{
			var value = parameters[condition.Parameter];
			var matches = value.IsLabel
				? string.Equals( value.Label, condition.Label, StringComparison.OrdinalIgnoreCase )
				: value.Number >= condition.Minimum && value.Number <= condition.Maximum;
			if ( matches == condition.Invert )
				return false;
		}
		return true;
	}

	private static float EvaluateAutomations(
		IReadOnlyList<AudioAutomation> automations,
		AudioAutomationTarget target,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		var result = 1f;
		foreach ( var automation in automations )
		{
			if ( automation.Target != target )
				continue;
			result *= automation.Evaluate( parameters[automation.Parameter].Number );
		}
		return result;
	}

}

internal interface IAudioPlaybackBackend
{
	IAudioVoice Play( SoundEvent sound, Audio3DAttributes attributes, bool listenLocal );
}

internal interface IAudioVoice
{
	bool IsPlaying { get; }
	SoundFile SelectedSound { get; }
	bool Paused { get; set; }
	float Time { get; set; }
	float Volume { get; set; }
	float Pitch { get; set; }
	Vector3 Position { get; set; }
	bool ListenLocal { get; set; }
	void Stop( float fadeTime );
}

internal interface IAudioTimelineScheduler
{
	float Now { get; }
	Task DelaySeconds( float seconds, CancellationToken cancellationToken );
}

internal sealed class RealtimeAudioTimelineScheduler : IAudioTimelineScheduler
{
	public float Now => RealTime.Now;

	public Task DelaySeconds( float seconds, CancellationToken cancellationToken )
	{
		return GameTask.DelaySeconds( seconds, cancellationToken );
	}
}

internal sealed class SboxAudioPlaybackBackend : IAudioPlaybackBackend
{
	public IAudioVoice Play( SoundEvent sound, Audio3DAttributes attributes, bool listenLocal )
	{
		if ( sound is null )
			throw new ArgumentNullException( nameof( sound ) );

		var handle = listenLocal
			? Sound.Play( sound )
			: Sound.Play( sound, attributes.Position );
		if ( listenLocal && handle.IsValid() )
		{
			handle.ListenLocal = true;
			handle.Position = Vector3.Forward * 64f;
		}
		return new SboxAudioVoice( handle );
	}
}

internal sealed class SboxAudioVoice : IAudioVoice
{
	private readonly SoundHandle _handle;

	public SboxAudioVoice( SoundHandle handle )
	{
		_handle = handle;
	}

	public bool IsPlaying => _handle.IsPlaying && !_handle.IsStopped;
	public SoundFile SelectedSound => _handle.SoundFile;
	public bool Paused { get => _handle.Paused; set => _handle.Paused = value; }
	public float Time { get => _handle.Time; set => _handle.Time = value; }
	public float Volume { get => _handle.Volume; set => _handle.Volume = value; }
	public float Pitch { get => _handle.Pitch; set => _handle.Pitch = value; }
	public Vector3 Position { get => _handle.Position; set => _handle.Position = value; }
	public bool ListenLocal { get => _handle.ListenLocal; set => _handle.ListenLocal = value; }

	public void Stop( float fadeTime )
	{
		_handle.Stop( fadeTime );
	}
}

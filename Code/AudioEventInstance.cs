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
	SoundFile SelectedSound, Guid ChildInstanceId = default, string ChildEvent = null );

public sealed partial class AudioEventInstance
{
	private readonly AudioEventDescription _description;
	private readonly Dictionary<AudioParameter, AudioParameterValue> _parameters = new( ReferenceEqualityComparer.Instance );
	private readonly Dictionary<AudioInstrument, SoundEventTrack> _selectedTracks = new( ReferenceEqualityComparer.Instance );
	private readonly List<PlayingVoice> _voices = new();
	private readonly HashSet<AudioTrack> _auditionMutedTracks = new( ReferenceEqualityComparer.Instance );
	private readonly AudioTimelineControlEvaluator _timelineControl;
    private readonly AudioMusicDirector _music;
    public string CurrentMusicSection => _music?.CurrentName ?? _voices.Select( voice => voice.Voice ).OfType<SubEventVoice>().FirstOrDefault()?.Child.CurrentMusicSection;
    public string PendingMusicSection => _music?.PendingName ?? (_pendingSubEvents.Values.FirstOrDefault()?.Choice.Event.ResourceName ?? _pendingSubEvents.Values.FirstOrDefault()?.Choice.Event.ResourcePath);
    public float MusicVariation => _music?.Variation ?? _voices.Select( voice => voice.Voice ).OfType<SubEventVoice>().FirstOrDefault()?.Child.MusicVariation ?? 0;
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
        AudioLiveDiagnostics.Register(this);
		_timelineControl = new AudioTimelineControlEvaluator( description.Resource );
        if(description.Resource.MusicPlaylist is not null)_music=new AudioMusicDirector(description.Resource);
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
			foreach ( var voice in _voices.Concat(_crossfadingSubEvents.Select(f=>f.Voice)) )
			{
				states.Add( new AudioInstrumentPlaybackState(
					voice.Track,
					voice.Instrument,
					!voice.Pending && voice.Voice.IsPlaying,
					voice.Pending,
					!voice.Pending && voice.Voice.Paused,
					voice.Voice.Time,
					voice.SoundTrack,
					voice.Voice.SelectedSound,
					voice.Voice is SubEventVoice child ? child.Child.LiveId : Guid.Empty,
					voice.Voice is SubEventVoice nested ? nested.Choice.Event.ResourcePath : null ) );
			}
            foreach(var overlay in overlayVoices)
                states.Add(new(overlay.Request.Track,overlay.Request.Instrument,overlay.Handle.IsPlaying,false,overlay.Handle.Paused,overlay.Handle.Time,overlay.Request.SoundTrack,overlay.Handle.SelectedSound));
			AddSharedMusicStates(states);
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

	/// <summary>Current beat-transition envelope; instance Volume remains the user's mix control.</summary>
    public float TransitionGain { get { RefreshPlaybackState(); return _timelineControl.TransitionGain; } }

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
			_instrumentTriggers.Clear();
			_timelinePosition = value;
			if(sharedMusic is not null)SeekSharedMusic(value);
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
            foreach(var echo in echoVoices)echo.Voice.PauseEcho(value);
			if(sharedMusic is not null)sharedMusic.Paused=value;
            foreach(var fade in _crossfadingSubEvents)fade.Voice.Voice.Paused=value;
            foreach(var overlay in overlayVoices)overlay.Handle.Paused=value;
            overlayLastClock=AudioSystem.TimelineScheduler.Now;

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
			if(_description.Resource.SharedMusicOutput)throw new InvalidOperationException("Shared music uses local playback and does not support spatial attributes.");
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
			if(_description.Resource.SharedMusicOutput && value>1)throw new ArgumentOutOfRangeException(nameof(value),"Shared music output volume must be within 0-1.");
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
			if(_description.Resource.SharedMusicOutput && value!=1)throw new InvalidOperationException("Shared music requires unit instance pitch.");
			_pitch = value;
			ApplyInstanceGain();
		}
	}

	/// <summary>
	/// Temporarily mutes one authored track on this instance without changing the AudioEvent resource.
	/// Intended for editor audition controls.
	/// </summary>
	internal AudioEvent AuditionResource => _description.Resource;
	internal bool IsTrackMutedForAudition(AudioTrack track) => _auditionMutedTracks.Contains(track);

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

		UpdateParentLayers();
		UpdateSharedMusicMix();
		ApplyOverlayGain();
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
			ReevaluatePlayback( repositionExistingVoices: false, refreshSourcesForAudition: true );
	}

	/// <summary>Validate and apply numeric world facts together before one playback reevaluation.</summary>
	public void SetParametersByName( IReadOnlyDictionary<string, float> values )
	{
		EnsureValid();
		ArgumentNullException.ThrowIfNull( values );
		var changes = new List<(AudioParameter Parameter, AudioParameterValue Value)>();
		foreach ( var pair in values )
		{
			var parameter = RequireParameter( pair.Key );
			if ( changes.Any(change => ReferenceEquals(change.Parameter, parameter)) )
				throw new ArgumentException( $"Audio parameter '{pair.Key}' occurs more than once in the batch.", nameof(values) );
			if ( parameter.Type == AudioParameterType.Labeled )
				throw new InvalidOperationException( $"Audio parameter '{pair.Key}' is labeled." );
			if ( !float.IsFinite(pair.Value) || pair.Value < parameter.Minimum || pair.Value > parameter.Maximum
				|| parameter.Type == AudioParameterType.Discrete && MathF.Abs(pair.Value-MathF.Round(pair.Value)) > 0.0001f )
				throw new ArgumentOutOfRangeException( nameof(values), $"Invalid value for audio parameter '{pair.Key}'." );
			changes.Add( (parameter, new AudioParameterValue(pair.Value, null, false)) );
		}
		if ( changes.Count == 0 ) return;
		foreach ( var change in changes ) _parameters[change.Parameter] = change.Value;
		ReevaluateIfPlaying();
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

		ResetMusicRequests();
		_instrumentTriggers.Clear();
		_playbackCancellation?.Dispose();
		_playbackCancellation = new CancellationTokenSource();
        ResetOverlays();
        parentLayers.Clear();
		_timelineAnchor = AudioSystem.TimelineScheduler.Now;
		if(_music is not null)_timelinePosition=(float)_music.Start(_parameters,_timelinePosition);
        overlayGrid=_description.Resource.BeatGridAt(_timelinePosition);
        _timelineControl.SetMusicTransition(null);
        _timelineControl.Reset( ToTimelineSample( _timelinePosition ) );
		_timelineDebug = default;
		_playbackState = AudioPlaybackState.Starting;
		_voices.Clear();
		_selectedTracks.Clear();
		_selectedSubEvents.Clear();
		ClearPreparedSubEvents();
		_pendingSubEvents.Clear();
		if(_description.Resource.SharedMusicOutput){StartSharedMusic();return;}
		ReevaluatePlayback( repositionExistingVoices: true );
	}

	public void Stop( AudioStopMode mode ) => StopWithFade( mode == AudioStopMode.AllowFadeout ? _description.Resource.FadeOutDuration : 0f );

	private void StopWithFade( float fadeTime )
	{
		EnsureValid();
		if(sharedMusic is not null){StopSharedMusic(fadeTime);return;}
		CancelMusicRequests();
        foreach(var echo in echoVoices)echo.Voice.CancelEcho();echoVoices.Clear();
        ClearSubEventCrossfades();
		ClearPreparedSubEvents();
		_pendingSubEvents.Clear();
		_playbackCancellation?.Cancel();
        StopOverlay();
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
		if ( (_description.Resource.SustainUntilStopped || _description.Resource.SharedMusicOutput) && _playbackState is AudioPlaybackState.Starting or AudioPlaybackState.Playing )
			throw new InvalidOperationException( "Stop a sustained process event before release." );
		_released = true;
	}

 private readonly List<(IAudioEchoVoice Voice,AudioTrack Track)> echoVoices=new();
 private void UpdateEchoOwnership(PlayingVoice voice)
 {
  if(voice.Voice is not IAudioEchoVoice echo || voice.Instrument.Echo is null && !echo.EchoActive)return;
  echo.SetEcho(voice.Instrument.Echo);
  echoVoices.RemoveAll(v=>!v.Voice.EchoActive);
  if(echo.EchoActive&&!echoVoices.Any(v=>ReferenceEquals(v.Voice,echo)))echoVoices.Add((echo,voice.Track));
  echo.SuppressEcho(_volume==0 || _auditionMutedTracks.Contains(voice.Track) || _auditionMutedTracks.Contains(_description.Resource.MasterTrack));
 }
	private void ApplyInstanceGain(bool publishSharedMix = true)
	{
        UpdateSubEventCrossfades();
        if (publishSharedMix) UpdateSharedMusicMix();
        ApplyOverlayGain();
        echoVoices.RemoveAll(v=>!v.Voice.EchoActive);
        foreach(var echo in echoVoices)echo.Voice.SuppressEcho(_volume==0 || _auditionMutedTracks.Contains(echo.Track) || _auditionMutedTracks.Contains(_description.Resource.MasterTrack));
		foreach ( var voice in _voices )
			ApplyGain( voice );
	}

	private void RefreshPlaybackState(bool publishSharedMix = true)
	{
        AudioLiveDiagnostics.Register(this);
		if ( _playbackState is not (AudioPlaybackState.Playing or AudioPlaybackState.Stopping) )
			return;

		if(sharedMusic is not null && _playbackState==AudioPlaybackState.Stopping)return;
		if ( _playbackState == AudioPlaybackState.Stopping )
		{
			if ( _voices.All( voice => !voice.Pending && !voice.Voice.IsPlaying ) )
			{
				_timelinePosition = CurrentTimelinePosition;
				_playbackState = AudioPlaybackState.Stopped;
			}
			return;
		}

		if ( !_paused )
			ProcessTimelineControl();
        else _music?.PauseClock();
        if(!_paused){UpdateParentLayers();UpdateOverlays();}
        if (!_paused && UpdateInstrumentTriggers()) ReevaluatePlayback(false);
		if ( !_paused && _playbackState == AudioPlaybackState.Playing && (_pendingSubEvents.Count > 0 || _voices.Any(v => ShouldRotateSubEvent(v.Instrument, v.Voice as SubEventVoice))) ) ReevaluatePlayback( false );
		ApplyInstanceGain(publishSharedMix);

		if ( sharedMusic is null && _voices.All( voice => !voice.Pending && !voice.Voice.IsPlaying )
			&& !HasConditionalPlayback()
			&& !HasActiveContentLoop()
			&& !(_description.Resource.SustainUntilStopped && _playbackState == AudioPlaybackState.Playing) )
		{
			_timelinePosition = CurrentTimelinePosition;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			_playbackState = AudioPlaybackState.Stopped;
			CancelMusicRequests();
		}
	}

	private float CurrentTimelinePosition => sharedMusic is not null ? SharedMusicPosition : _playbackState == AudioPlaybackState.Stopped || _paused
		? _timelinePosition
		: _timelinePosition + MathF.Max( 0f, AudioSystem.TimelineScheduler.Now - _timelineAnchor );

	private long ToTimelineSample( float seconds )
	{
		return checked((long)Math.Round( seconds * _description.Resource.TimelineSampleRate, MidpointRounding.AwayFromZero ));
	}

	private long _compositionCompletions;
	private void ProcessTimelineControl()
	{
		var rawPosition = CurrentTimelinePosition;
        if(handoffEntrySeconds>0 && rawPosition-handoffEntryStart>=handoffEntrySeconds*2)handoffEntrySeconds=0;
		var sourceGrid = _description.Resource.BeatGridAt((double)_timelineDebug.CurrentSample / _description.Resource.TimelineSampleRate);
        var musicPosition = sourceGrid is null ? rawPosition : Math.Min(rawPosition, sourceGrid.End.Seconds - .00001);
        _music?.Update(musicPosition,AudioSystem.TimelineScheduler.Now,_parameters);
        _timelineControl.SetMusicTransition(_music?.Transition);
        var rawSample = ToTimelineSample( rawPosition );
        var compositionEnd = ToTimelineSample(_description.Resource.TimelineDuration);
        if(compositionEnd > 0 && rawSample >= compositionEnd && _timelineDebug.CurrentSample < compositionEnd) _compositionCompletions++;
		if ( rawSample == _timelineDebug.CurrentSample )
			return;
		var completedContentLoopId = FindCompletedContentLoop( rawSample );
		var result = _timelineControl.Advance( rawSample, _parameters, completedContentLoopId );
		var resolvedPosition = (float)result.Position / _description.Resource.TimelineSampleRate;
		var jumped = result.Jumps > 0;
		if ( jumped )
		{
			var retriggeredInstruments = PrepareLoopRetriggers( result.ExecutedLoopRegionIds );
			if(sharedMusic is not null)sharedTimelineOffsetSamples+=(long)Math.Round(((double)result.Position-rawSample)*44100d/_description.Resource.TimelineSampleRate);
			_timelinePosition = resolvedPosition;
			_timelineAnchor = AudioSystem.TimelineScheduler.Now;
			ReevaluatePlayback( repositionExistingVoices: true, retriggeredInstruments );
		}
		TransitionRegion pendingRegion = null;
		foreach ( var region in _timelineControl.Regions )
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
		// The following parameter reevaluation publishes the current shared mix once.
		RefreshPlaybackState(publishSharedMix: false);
	}

	private void ReevaluateIfPlaying()
	{
        if(!_paused && _playbackState==AudioPlaybackState.Playing){UpdateParentLayers();UpdateOverlays();}
		if ( _playbackState != AudioPlaybackState.Stopped )
			ReevaluatePlayback( repositionExistingVoices: false );
	}

	private void ReevaluatePlayback(
		bool repositionExistingVoices,
		IReadOnlySet<AudioInstrument> retriggeredInstruments = null, bool refreshSourcesForAudition = false )
	{
		if(sharedMusic is not null){UpdateSharedMusicMix(parametersChanged:true);return;}
		UpdateInstrumentTriggers();
		var position = CurrentTimelinePosition;
		var requests = AudioEventEvaluator.BuildPlaybackPlan( _description.Resource, _parameters, SelectSoundTrack );
        if (_music is not null && _description.Resource.BeatGridAt(position) is { } musicGrid)
            requests = requests.Where(x => x.Instrument.StartTime >= musicGrid.Start.Seconds && x.Instrument.StartTime < musicGrid.End.Seconds).ToArray();
		requests = requests.Where(x=>x.Track.Overlay?.Enabled!=true && TriggerAllowsPlayback(x.Instrument)).ToArray();
		var requestedInstruments = requests.Select( request => request.Instrument ).ToHashSet( ReferenceEqualityComparer.Instance );

		for ( var i = _voices.Count - 1; i >= 0; i-- )
		{
			var playingVoice = _voices[i];
			if ( requestedInstruments.Contains( playingVoice.Instrument ) )
				continue;

			playingVoice.CancelScheduledStart();
			playingVoice.CancelScheduledStop();
			if(playingVoice.Instrument.SubEventExitFadeSeconds>0 && playingVoice.Voice is SubEventVoice)RetainCrossfade(playingVoice,null);
            else playingVoice.Voice.Stop( 0f );
			_voices.RemoveAt( i );
			CancelPreparedSubEvent(playingVoice.Instrument);
			_selectedSubEvents.Remove( playingVoice.Instrument );
			_pendingSubEvents.Remove( playingVoice.Instrument );
		}

		if(repositionExistingVoices)
            foreach(var fade in _crossfadingSubEvents.Where(f=>!f.Voice.Instrument.RetainSubEventPositionOnTimelineJump).ToArray())
            {fade.Voice.Voice.Stop(0);_crossfadingSubEvents.Remove(fade);}
		var voicesToPosition = new List<PlayingVoice>();
		foreach ( var request in requests )
		{
			var playingVoice = _voices.FirstOrDefault( candidate => ReferenceEquals( candidate.Instrument, request.Instrument ) );
			var createdVoice = false;
            var changingChild = false;
			var childChoice = request.Instrument.IsSubEvent ? SelectSubEvent( request.Instrument ) : null;
			if ( playingVoice?.Voice is SubEventVoice existingChild && !ReferenceEquals( existingChild.Choice, childChoice ) )
			{
				playingVoice.CancelScheduledStart();
				playingVoice.CancelScheduledStop();
				changingChild = true;
                RetainCrossfade(playingVoice,childChoice);
				_voices.Remove( playingVoice );
				playingVoice = null;
			}
			if(request.Instrument.IsSubEvent && childChoice is null)continue;
			if ( playingVoice?.Voice is SubEventVoice completedChild && !completedChild.IsPlaying
				&& !repositionExistingVoices && retriggeredInstruments is null ) continue;
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

				IAudioVoice voice = childChoice is null
					? AudioSystem.Backend.Play( request.Sound, _attributes3D, !_hasAttributes3D )
					: TakeSubEvent( request.Instrument, childChoice, MathF.Max( request.Instrument.StartTime, position ), changingChild ? childChoice.EntryFadeBeats ?? request.Instrument.SubEventTransitionBeats : 0 );
				voice.Paused = true;
				playingVoice = new PlayingVoice( request.Track, request.Instrument, request.SoundTrack, voice, voice.Volume, voice.Pitch, request.SourceData );
				if (_instrumentTriggers.TryGetValue(request.Instrument, out var trigger)) playingVoice.TriggeredStart = trigger.Start;
				_voices.Add( playingVoice );
				createdVoice = true;
			}

			if (refreshSourcesForAudition) playingVoice.RefreshSourceRanges(request.SourceData);
			playingVoice.Track = request.Track;
			if ( playingVoice.Voice is SubEventVoice childVoice )
			{
				childVoice.SyncParameters();
				if ( repositionExistingVoices && !request.Instrument.RetainSubEventPositionOnTimelineJump ) childVoice.ActivationPosition = playingVoice.Instrument.StartTime;
			}
			playingVoice.EventVolume = request.Volume;
			playingVoice.EventPitch = request.Pitch;
			ApplyGain( playingVoice );
			var trimChanged = playingVoice.AuthoredTrimStart != EffectiveTrimStart( playingVoice )
				|| playingVoice.AuthoredTrimEnd != EffectiveTrimEnd( playingVoice );
			if ( createdVoice || repositionExistingVoices && float.IsNaN(playingVoice.TriggeredStart) && !request.Instrument.RetainSubEventPositionOnTimelineJump || trimChanged )
				voicesToPosition.Add( playingVoice );
			playingVoice.AuthoredTrimStart = EffectiveTrimStart( playingVoice );
			playingVoice.AuthoredTrimEnd = EffectiveTrimEnd( playingVoice );
		}

        // Keep new voices paused until every source is ready. Creating a native decoder
        // can take time; starting each stem inside the creation loop offsets later stems.
        if ( _playbackState == AudioPlaybackState.Starting )
            _timelineAnchor = AudioSystem.TimelineScheduler.Now;
        position = CurrentTimelinePosition;
        foreach ( var voice in voicesToPosition )
            PositionVoice( voice, position );

		_playbackState = _voices.Count == 0 && !HasConditionalPlayback() && !_description.Resource.SustainUntilStopped
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
					if ( instrument.RetainSubEventPositionOnTimelineJump || instrument.LayerControl is not null || instrument.StartTriggerConditions.Count>0 || instrument.StartTime < loop.Start.Seconds || instrument.StartTime >= loop.End.Seconds )
						continue;

					_selectedTracks.Remove( instrument );
					_selectedSubEvents.Remove( instrument );
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
		if ( audioEvent.MasterTrack.Conditions?.Count > 0 || audioEvent.MasterTrack.Instruments.Any( instrument => instrument.Conditions?.Count > 0 || (instrument.SubEvents?.Any( choice => choice.Conditions?.Count > 0 || choice.EarlyEntryConditions?.Count > 0 ) ?? false) ) )
			return true;

		foreach ( var track in audioEvent.Tracks )
		{
			if ( track.Conditions?.Count > 0 || track.Instruments.Any( instrument => instrument.Conditions?.Count > 0 || (instrument.SubEvents?.Any( choice => choice.Conditions?.Count > 0 || choice.EarlyEntryConditions?.Count > 0 ) ?? false) ) )
				return true;
		}
		return false;
	}

	private SoundEventTrack SelectSoundTrack( AudioInstrument instrument, IReadOnlyList<SoundEventTrack> eligibleTracks )
	{
		if ( _selectedTracks.TryGetValue( instrument, out var selected ) && eligibleTracks.Contains( selected ) )
			return selected;

		var source = _description.Resource.Source(instrument);
		selected = AudioSourceSelectionHistory.Select( source, source.SelectionMode, eligibleTracks );
		_selectedTracks[instrument] = selected;
		return selected;
	}

	private void PositionVoice( PlayingVoice playingVoice, float position )
	{
		playingVoice.CancelScheduledStart();
		playingVoice.CancelScheduledStop();
		var delay = VoiceStartTime( playingVoice ) - position;
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

		var delay = VoiceStartTime( playingVoice ) - CurrentTimelinePosition;
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
		if (!float.IsNaN(playingVoice.TriggeredStart)) return;
		var trimEnd = ResolveTrimEnd( playingVoice );
		if ( !float.IsFinite( trimEnd ) )
			return;

		var remaining = VoiceStartTime( playingVoice ) + trimEnd - EffectiveTrimStart( playingVoice ) - CurrentTimelinePosition;
		if ( remaining <= 0f )
		{
			playingVoice.Voice.Stop( 0f );
			return;
		}

		playingVoice.StopCancellation = CancellationTokenSource.CreateLinkedTokenSource( _playbackCancellation.Token );
		_ = StopTrimmedVoiceAsync( playingVoice, remaining, playingVoice.StopCancellation.Token );
	}

	private static float VoiceStartTime( PlayingVoice voice ) => voice.Voice is SubEventVoice child
		? child.ActivationPosition : float.IsNaN(voice.TriggeredStart) ? voice.Instrument.StartTime : voice.TriggeredStart;

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
		return playingVoice.Instrument.TrimStart + (playingVoice.SoundTrack?.Offset ?? 0f);
	}

	private static float EffectiveTrimEnd( PlayingVoice playingVoice )
	{
		var track = playingVoice.SoundTrack;
		if ( track is null || track.Length <= 0f )
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
		if (playingVoice.Voice is IAudioToneVoice tone) tone.SetTone(_description.Resource.MasterTrack.Tone, ReferenceEquals(playingVoice.Track, _description.Resource.MasterTrack) ? null : playingVoice.Track.Tone);
		UpdateEchoOwnership(playingVoice);
		var muted = _auditionMutedTracks.Contains( _description.Resource.MasterTrack )
			|| _auditionMutedTracks.Contains( playingVoice.Track );
		playingVoice.Voice.Volume = muted
			? 0f
			: playingVoice.NativeVolume * playingVoice.EventVolume * _volume * _timelineControl.TransitionGain * (_music?.Gain(playingVoice.Track,CurrentTimelinePosition) ?? 1f) * InstrumentFadeGain( playingVoice ) * HandoffGain(playingVoice.Track);
		playingVoice.Voice.Pitch = playingVoice.NativePitch * playingVoice.EventPitch * _pitch;
	}

	private float InstrumentFadeGain( PlayingVoice playingVoice )
	{
		return AudioInstrumentFade.Gain(playingVoice.Instrument,
			float.IsNaN(playingVoice.TriggeredStart) ? CurrentTimelinePosition-VoiceStartTime(playingVoice) : playingVoice.Voice.Time-EffectiveTrimStart(playingVoice),
			ResolveTrimEnd(playingVoice)-EffectiveTrimStart(playingVoice),playingVoice.Pending);
	}

	internal AudioMusicMixState CaptureSharedMusicMix()
	{
		var position=CurrentTimelinePosition;
		var gains=new Dictionary<AudioTrack,float>();
        var blockedTails=new List<AudioTrack>();
		foreach(var track in _description.Resource.Tracks.Prepend(_description.Resource.MasterTrack))
		{
			var muted=_auditionMutedTracks.Contains(_description.Resource.MasterTrack)||_auditionMutedTracks.Contains(track);
			if(muted || blockedParentTails.Contains(track.Id))blockedTails.Add(track);
			gains[track]=muted?0:_timelineControl.TransitionGain*(_music?.Gain(track,position)??1)*HandoffGain(track)*parentLayerGains.GetValueOrDefault(track.Id,1f);
		}
		return new AudioMusicMixState(position,gains,blockedTails);
	}

	private void EnsureValid()
	{
		if ( _released )
			throw new InvalidOperationException( "Audio event instance has been released." );
	}

	private sealed class PlayingVoice
	{
		public PlayingVoice( AudioTrack track, AudioInstrument instrument, SoundEventTrack soundTrack, IAudioVoice voice, float nativeVolume, float nativePitch, AudioSourceDefinition source )
		{
			Track = track;
			Instrument = instrument;
			SoundTrack = soundTrack;
			Voice = voice;
			NativeVolume = nativeVolume;
			NativePitch = nativePitch;
			sourceVolume = source?.Volume ?? new(nativeVolume);
			sourcePitch = source?.Pitch ?? new(nativePitch);
		}

		public AudioTrack Track { get; set; }
		public AudioInstrument Instrument { get; }
		public SoundEventTrack SoundTrack { get; }
		public IAudioVoice Voice { get; }
		public float NativeVolume { get; private set; }
		public float NativePitch { get; private set; }
        private RangedFloat sourceVolume, sourcePitch;
        public void RefreshSourceRanges(AudioSourceDefinition source)
        {
            if(source is null)return;
            NativeVolume=RemapRange(NativeVolume,sourceVolume,source.Volume);
            NativePitch=RemapRange(NativePitch,sourcePitch,source.Pitch);
            sourceVolume=source.Volume;sourcePitch=source.Pitch;
        }
        private static float RemapRange(float selected,RangedFloat before,RangedFloat after)
        {
            if(before.Min==after.Min && before.Max==after.Max)return selected;
            // Keep the original random position in the range; do not roll again on each edit.
            var fraction=before.Max>before.Min?Math.Clamp((selected-before.Min)/(before.Max-before.Min),0f,1f):.5f;
            return after.Min+(after.Max-after.Min)*fraction;
        }
		public float EventVolume { get; set; } = 1f;
		public float EventPitch { get; set; } = 1f;
		public float AuthoredTrimStart { get; set; }
		public float AuthoredTrimEnd { get; set; }
		public float TriggeredStart { get; set; } = float.NaN;
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
	float Pitch )
{
 internal AudioSourceDefinition SourceData {get;init;}
}

internal readonly record struct AudioVoiceMix(bool Active, float Volume, float Pitch);

internal static class AudioEventEvaluator
{
    // Evaluate an already selected voice. Do not select sources or allocate native SoundEvents.
    internal static AudioVoiceMix EvaluateSelectedVoice(AudioEvent resource, AudioVoiceRequest voice,
        IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters)
    {
        var track=voice.Track; var instrument=voice.Instrument; var master=resource.MasterTrack;
        var source=voice.SourceData;
        if (!ReferenceEquals(track,master) && !resource.Tracks.Contains(track)
            || !track.Instruments.Contains(instrument) || instrument.LayerControl is not null
            || instrument.IsSubEvent || source is null || instrument.SourceId!=source.Id || !source.Tracks.Contains(voice.SoundTrack)
            || !Matches(master.Conditions,parameters) || !Matches(track.Conditions,parameters)
            || !Matches(instrument.Conditions,parameters) || !Matches(voice.SoundTrack.Conditions,parameters)) return default;
        var volume=track.Volume*EvaluateAutomations(track.Automations,AudioAutomationTarget.Volume,parameters);
        var pitch=track.Pitch*EvaluateAutomations(track.Automations,AudioAutomationTarget.Pitch,parameters);
        if (!ReferenceEquals(track,master))
        {
            volume*=master.Volume*EvaluateAutomations(master.Automations,AudioAutomationTarget.Volume,parameters);
            pitch*=master.Pitch*EvaluateAutomations(master.Automations,AudioAutomationTarget.Pitch,parameters);
        }
        return new(true,volume*EvaluateAutomations(instrument.Automations,AudioAutomationTarget.Volume,parameters),
            pitch*EvaluateAutomations(instrument.Automations,AudioAutomationTarget.Pitch,parameters));
    }

	public static IReadOnlyList<AudioVoiceRequest> BuildPlaybackPlan(
		AudioEvent audioEvent,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		Func<AudioInstrument, IReadOnlyList<SoundEventTrack>, SoundEventTrack> selectTrack = null, bool includeInactive = false )
	{
		var requests = new List<AudioVoiceRequest>();
		var masterMatches = Matches( audioEvent.MasterTrack.Conditions, parameters );
		if ( !masterMatches && !includeInactive ) return requests;

		EvaluateTrack( audioEvent, audioEvent.MasterTrack, masterMatches ? 1f : 0f, 1f, parameters, requests, selectTrack, includeInactive );
		foreach ( var track in audioEvent.Tracks )
		{
			var trackMatches = Matches( track.Conditions, parameters );
			if ( !trackMatches && !includeInactive ) continue;
			EvaluateTrack(
				audioEvent,
				track,
				masterMatches && trackMatches ? audioEvent.MasterTrack.Volume * EvaluateAutomations(audioEvent.MasterTrack.Automations,AudioAutomationTarget.Volume,parameters) : 0f,
				audioEvent.MasterTrack.Pitch * EvaluateAutomations(audioEvent.MasterTrack.Automations,AudioAutomationTarget.Pitch,parameters),
				parameters,
				requests,
				selectTrack, includeInactive );
		}
		return requests;
	}

	private static void EvaluateTrack(
		AudioEvent audioEvent,
		AudioTrack track,
		float parentVolume,
		float parentPitch,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters,
		List<AudioVoiceRequest> requests,
		Func<AudioInstrument, IReadOnlyList<SoundEventTrack>, SoundEventTrack> selectTrack, bool includeInactive )
	{
		var trackVolume = parentVolume * track.Volume * EvaluateAutomations( track.Automations, AudioAutomationTarget.Volume, parameters );
		var trackPitch = parentPitch * track.Pitch * EvaluateAutomations( track.Automations, AudioAutomationTarget.Pitch, parameters );
		foreach ( var instrument in track.Instruments )
		{
			if ( instrument.LayerControl is not null ) continue;
			var instrumentMatches = Matches( instrument.Conditions, parameters );
			if ( !instrumentMatches && !includeInactive ) continue;
			if ( instrument.IsSubEvent )
			{
				if ( instrument.SubEvents.Any( choice => choice.Eligible(parameters) ) )
					requests.Add( new AudioVoiceRequest( track, instrument, null, null, instrument.StartTime,
						trackVolume * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Volume, parameters ),
						trackPitch * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Pitch, parameters ) ) );
				continue;
			}
			var source=audioEvent.Source(instrument);
			var eligibleTracks = source.Tracks
				.Where( soundTrack => Matches( soundTrack.Conditions, parameters ) )
				.ToArray();
			var sourceMatches = eligibleTracks.Length > 0;
			if ( !sourceMatches && includeInactive ) eligibleTracks = source.Tracks.ToArray();
			if ( eligibleTracks.Length == 0 ) continue;
			// A plan without a playback owner must not advance audible variation history.
			var soundTrack = selectTrack is null
				? eligibleTracks[Random.Shared.Next( eligibleTracks.Length )]
				: selectTrack( instrument, eligibleTracks );
			var volume = (instrumentMatches && sourceMatches ? trackVolume : 0f) * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Volume, parameters );
			var pitch = trackPitch * EvaluateAutomations( instrument.Automations, AudioAutomationTarget.Pitch, parameters );
			requests.Add( new AudioVoiceRequest(
				track,
				instrument,
				soundTrack,
				audioEvent.CreatePlaybackEvent( source, soundTrack ),
				instrument.StartTime,
				volume,
				pitch ) {SourceData=source} );
		}
	}

	internal static bool Matches(
		IReadOnlyList<AudioParameterCondition> conditions,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		for ( var index = 0; index < conditions.Count; index++ )
		{
            var condition = conditions[index];
			var value = parameters[condition.Parameter];
			var matches = value.IsLabel
				? string.Equals( value.Label, condition.Label, StringComparison.OrdinalIgnoreCase )
				: value.Number >= condition.Minimum && value.Number <= condition.Maximum;
			if ( matches == condition.Invert )
				return false;
		}
		return true;
	}

	internal static float EvaluateAutomations(
		IReadOnlyList<AudioAutomation> automations,
		AudioAutomationTarget target,
		IReadOnlyDictionary<AudioParameter, AudioParameterValue> parameters )
	{
		var result = 1f;
		for ( var index = 0; index < automations.Count; index++ )
		{
            var automation = automations[index];
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

internal sealed class SboxAudioVoice : IAudioVoice, IAudioToneVoice, IAudioEchoVoice
{
	private readonly SoundHandle _handle;
	private Sandbox.Audio.Mixer toneMixer;
	private AudioToneProcessor toneProcessor;
 internal Sandbox.Audio.Mixer EffectMixerForCheck=>toneMixer;
 private AudioEchoProcessor echoProcessor;
 private SoundHandle echoCarrier;
 private static SoundFile echoClock;
 public bool EchoActive=>echoProcessor is not null && !echoProcessor.Finished;
 private void EnsureEffectMixer()
 {
  if(toneMixer is not null)return;
  toneMixer=(_handle.TargetMixer??Sandbox.Audio.Mixer.Default).AddChild();
  toneMixer.Name=$"Smod effects {Guid.NewGuid()}";_handle.TargetMixer=toneMixer;
  _=RetireToneMixer();
 }
 public void SetEcho(AudioEchoEffect effect)
 {
  if(effect is null || !effect.IsValid || effect.WetGain==0 || echoProcessor is not null || _handle.IsStopped)return;
  EnsureEffectMixer();echoProcessor=new(effect);toneMixer.AddProcessor(echoProcessor);
  echoClock??=SoundFile.FromPcm("smod/runtime/echo_clock.vsnd",new byte[4096],new SoundFile.PcmOptions{Channels=2,Rate=44100,Bits=16,Loop=true});
  echoCarrier=Sound.PlayFile(echoClock,1);echoCarrier.TargetMixer=toneMixer;echoCarrier.SpacialBlend=0;
  echoCarrier.ListenLocal=true;echoCarrier.DistanceAttenuation=false;echoCarrier.AirAbsorption=false;
  echoCarrier.OcclusionEnabled=false;echoCarrier.ReverbEnabled=false;
 }
 public void CancelEcho(){if(echoProcessor is not null)echoProcessor.Killed=true;echoCarrier?.Stop();}
 public void PauseEcho(bool paused){if(echoProcessor is not null)echoProcessor.Paused=paused;}
 public void SuppressEcho(bool suppressed){if(echoProcessor is not null)echoProcessor.Suppressed=suppressed;}
	public void SetTone(AudioTone master, AudioTone track)
	{
		if (master is { IsValid: false } || track is { IsValid: false }) return;
		if (toneProcessor is null)
		{
			if ((master?.BassGainDb ?? 0) == 0 && (track?.BassGainDb ?? 0) == 0) return;
			if (_handle.IsStopped) return;
			EnsureEffectMixer();
			toneProcessor = new();
			toneProcessor.Configure(master, track);
			toneMixer.AddProcessor(toneProcessor);
			_handle.TargetMixer = toneMixer;

		}
		else toneProcessor.Configure(master, track);
	}
	private async Task RetireToneMixer()
	{
		// Retain the route through pause, Release, and native fade-out.
		try
  {
   while(!_handle.IsStopped)await GameTask.DelayRealtime(10);
   echoProcessor?.BeginTail();
   var clock=System.Diagnostics.Stopwatch.StartNew();double elapsed=0,last=0;
   while(EchoActive && echoCarrier is {IsStopped:false} && elapsed<5)
   {
    await GameTask.DelayRealtime(10);var now=clock.Elapsed.TotalSeconds;
    if(!echoProcessor.Paused)elapsed+=now-last;last=now;
   }
  }
  finally {echoCarrier?.Stop();toneMixer.ClearProcessors();toneMixer.Destroy();toneMixer=null;}
	}


	public SboxAudioVoice( SoundHandle handle )
	{
		_handle = handle;
	}

	public bool IsPlaying => _handle.IsPlaying && !_handle.IsStopped;
	public SoundFile SelectedSound => _handle.SoundFile;
	public bool Paused { get => _handle.Paused; set { _handle.Paused = value; PauseEcho(value); } }
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



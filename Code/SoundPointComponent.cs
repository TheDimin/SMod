using System.Text.Json.Serialization;

namespace Smod;

[Icon( "speaker" ), Group( "Audio" ), Title( "Sound Point" )]
public sealed class SoundPointComponent : Component
{
	private AudioEvent _audioEvent;
	private AudioEventInstance _instance;
	private AudioEvent _instanceEvent;
	private bool _hasStarted;
	private bool _resumeWhenEnabled;
	private List<SoundPointParameterSnapshot> _appliedParameters = new();

	[Property, Group( "Playback" ), Title( "Audio Event" )]
	public AudioEvent AudioEvent
	{
		get => _audioEvent;
		set
		{
			if ( ReferenceEquals( _audioEvent, value ) )
				return;

			var resume = IsPlaying;
			StopAndRelease( AudioStopMode.Immediate );
			_audioEvent = value;
			Parameters = SynchronizeParameters( value, Parameters );
			_appliedParameters.Clear();

			if ( resume && Active )
				Play();
		}
	}

	[Property, Group( "Playback" ), Title( "Play On Start" )]
	public bool PlayOnStart { get; set; } = true;

	[Property, Group( "Parameters" ), Title( "Event Parameters" ), Editor( "SoundPointParameters" )]
	public List<SoundPointParameterValue> Parameters { get; set; } = new();

	[Property, ReadOnly, JsonIgnore, Group( "Runtime" )]
	public AudioPlaybackState PlaybackState => _instance?.PlaybackState ?? AudioPlaybackState.Stopped;

	[Property, ReadOnly, JsonIgnore, Group( "Runtime" )]
	public bool IsPlaying => PlaybackState is AudioPlaybackState.Starting or AudioPlaybackState.Playing;

	protected override void OnStart()
	{
		_hasStarted = true;
		Parameters = SynchronizeParameters( AudioEvent, Parameters );
		if ( PlayOnStart )
			Play();
	}

	protected override void OnEnabled()
	{
		if ( _hasStarted && _resumeWhenEnabled )
		{
			_resumeWhenEnabled = false;
			Play();
		}
	}

	protected override void OnUpdate()
	{
		if ( _instance is null )
			return;

		if ( !ReferenceEquals( _instanceEvent, AudioEvent ) )
		{
			var resume = IsPlaying;
			StopAndRelease( AudioStopMode.Immediate );
			Parameters = SynchronizeParameters( AudioEvent, Parameters );
			if ( resume )
				Play();
			return;
		}

		_instance.Attributes3D = new Audio3DAttributes( WorldPosition );
		ApplyChangedParameters();
		_ = _instance.PlaybackState;
	}

	protected override void OnDisabled()
	{
		_resumeWhenEnabled = IsPlaying;
		StopAndRelease( AudioStopMode.AllowFadeout );
	}

	protected override void OnDestroy()
	{
		StopAndRelease( AudioStopMode.Immediate );
	}

	[Button( Title = "Play", Icon = "play_arrow" )]
	public void Play()
	{
		if ( AudioEvent is null )
			throw new InvalidOperationException( $"{nameof( SoundPointComponent )} on '{GameObject.Name}' requires an Audio Event before playback." );

		Parameters = SynchronizeParameters( AudioEvent, Parameters );
		if ( _instance is null || !ReferenceEquals( _instanceEvent, AudioEvent ) )
		{
			StopAndRelease( AudioStopMode.Immediate );
			_instance = AudioSystem.GetEvent( AudioEvent ).CreateInstance();
			_instanceEvent = AudioEvent;
		}

		_instance.Attributes3D = new Audio3DAttributes( WorldPosition );
		ApplyAllParameters();
		_instance.Start();
	}

	[Button( Title = "Stop", Icon = "stop" )]
	public void Stop()
	{
		if ( _instance is null )
			return;

		_instance.Stop( AudioStopMode.AllowFadeout );
	}

	internal static List<SoundPointParameterValue> SynchronizeParameters(
		AudioEvent audioEvent,
		IEnumerable<SoundPointParameterValue> existingValues )
	{
		if ( audioEvent?.Parameters is null )
			return new();

		var existingByName = (existingValues ?? Enumerable.Empty<SoundPointParameterValue>())
			.Where( value => value?.Parameter is not null && !string.IsNullOrWhiteSpace( value.Parameter.Name ) )
			.GroupBy( value => value.Parameter.Name, StringComparer.OrdinalIgnoreCase )
			.ToDictionary( group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase );
		var synchronized = new List<SoundPointParameterValue>( audioEvent.Parameters.Count );

		foreach ( var parameter in audioEvent.Parameters )
		{
			if ( parameter is null )
				continue;

			existingByName.TryGetValue( parameter.Name ?? string.Empty, out var existing );
			synchronized.Add( SoundPointParameterValue.Create( parameter, existing ) );
		}

		return synchronized;
	}

	private void ApplyAllParameters()
	{
		_appliedParameters.Clear();
		foreach ( var value in Parameters )
		{
			value.ApplyTo( _instance );
			_appliedParameters.Add( value.CreateSnapshot() );
		}
	}

	private void ApplyChangedParameters()
	{
		if ( Parameters.Count != _appliedParameters.Count )
		{
			Parameters = SynchronizeParameters( AudioEvent, Parameters );
			ApplyAllParameters();
			return;
		}

		for ( var index = 0; index < Parameters.Count; index++ )
		{
			var value = Parameters[index];
			var snapshot = value.CreateSnapshot();
			if ( snapshot == _appliedParameters[index] )
				continue;

			value.ApplyTo( _instance );
			_appliedParameters[index] = snapshot;
		}
	}

	private void StopAndRelease( AudioStopMode stopMode )
	{
		if ( _instance is null )
			return;

		_instance.Stop( stopMode );
		_instance.Release();
		_instance = null;
		_instanceEvent = null;
		_appliedParameters.Clear();
	}
}

public sealed class SoundPointParameterValue
{
	[Property, ReadOnly]
	public AudioParameter Parameter { get; set; }

	[Property, ShowIf( nameof( IsNumeric ), true )]
	public float NumericValue { get; set; }

	[Property, ShowIf( nameof( IsLabeled ), true ), AudioParameterLabel( nameof( Parameter ) )]
	public string LabelValue { get; set; }

	[JsonIgnore, Hide]
	public bool IsNumeric => Parameter?.Type is AudioParameterType.Continuous or AudioParameterType.Discrete;

	[JsonIgnore, Hide]
	public bool IsLabeled => Parameter?.Type == AudioParameterType.Labeled;

	internal static SoundPointParameterValue Create( AudioParameter parameter, SoundPointParameterValue existing )
	{
		var compatible = existing?.Parameter?.Type == parameter.Type;
		if ( parameter.Type == AudioParameterType.Labeled )
		{
			var existingLabel = compatible
				? parameter.Labels?.FirstOrDefault( label => string.Equals( label, existing.LabelValue, StringComparison.OrdinalIgnoreCase ) )
				: null;
			var label = existingLabel
				?? parameter.Labels?.FirstOrDefault( candidate => string.Equals( candidate, parameter.DefaultLabel, StringComparison.OrdinalIgnoreCase ) )
				?? parameter.Labels?.FirstOrDefault();
			return new SoundPointParameterValue { Parameter = parameter, LabelValue = label };
		}

		var number = compatible && float.IsFinite( existing.NumericValue )
			? existing.NumericValue
			: parameter.DefaultValue;
		number = Math.Clamp( number, parameter.Minimum, parameter.Maximum );
		if ( parameter.Type == AudioParameterType.Discrete )
			number = MathF.Round( number );
		return new SoundPointParameterValue { Parameter = parameter, NumericValue = number };
	}

	internal void ApplyTo( AudioEventInstance instance )
	{
		ArgumentNullException.ThrowIfNull( instance );
		if ( Parameter is null )
			throw new InvalidOperationException( "A Sound Point parameter row has no AudioParameter resource." );

		if ( Parameter.Type == AudioParameterType.Labeled )
			instance.SetParameterByNameWithLabel( Parameter.Name, LabelValue );
		else
			instance.SetParameterByName( Parameter.Name, NumericValue );
	}

	internal SoundPointParameterSnapshot CreateSnapshot()
	{
		return new SoundPointParameterSnapshot( Parameter, NumericValue, LabelValue );
	}
}

internal readonly record struct SoundPointParameterSnapshot(
	AudioParameter Parameter,
	float NumericValue,
	string LabelValue );

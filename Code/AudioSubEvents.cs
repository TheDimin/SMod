namespace Smod;

/// <summary>One eligible child event in a timeline multi-instrument. Conditions use parent parameters.</summary>
public sealed class AudioSubEventChoice
{
	[Property] public AudioEvent Event { get; set; }
	[Property, InlineEditor] public List<AudioParameterCondition> Conditions { get; set; } = new();
}

public sealed partial class AudioEventInstance
{
	private readonly Dictionary<AudioInstrument, AudioSubEventChoice> _selectedSubEvents = new();
	private readonly Dictionary<AudioInstrument, List<AudioSubEventChoice>> _subEventHistory = new();
	private AudioEventInstance _parentInstance;
	private sealed record PendingSubEvent( AudioSubEventChoice Choice, float ParentPosition, double ChildBoundary = double.NaN );
	private readonly Dictionary<AudioInstrument, SubEventVoice> _preparedSubEvents = new();
	private void CancelPreparedSubEvent(AudioInstrument instrument)
	{
		if (_preparedSubEvents.Remove(instrument, out var voice)) voice.Stop(0);
	}
	private void ClearPreparedSubEvents()
	{
		foreach (var voice in _preparedSubEvents.Values) voice.Stop(0);
		_preparedSubEvents.Clear();
	}
	private SubEventVoice TakeSubEvent(AudioInstrument instrument, AudioSubEventChoice choice, float position, float entryBeats)
	{
		if (_preparedSubEvents.Remove(instrument, out var prepared))
		{
			if (ReferenceEquals(prepared.Choice, choice))
			{
				prepared.ActivationPosition=position;
				return prepared;
			}
			prepared.Stop(0);
		}
		return new SubEventVoice(this, choice, position, entryBeats);
	}
	private readonly Dictionary<AudioInstrument, PendingSubEvent> _pendingSubEvents = new();
	internal Guid ParentLiveId { get; private set; }
	public Guid LiveId => liveId;

 private bool ShouldRotateSubEvent(AudioInstrument instrument, SubEventVoice active)
 {
  if(active is null || active.Paused)return false;
  if(active.Choice.Event.SharedMusicOutput && !AudioEventEvaluator.Matches(active.Choice.Conditions,_parameters))return false;
  // Shared songs prepare a single alternative during the last phrase. Late preparation waits a full loop.
  if(active.Choice.Event.SharedMusicOutput && instrument.RotateSubEventsAtEnd
   && AudioEventEvaluator.Matches(active.Choice.Conditions,_parameters))
  {
   var alternatives=instrument.SubEvents.Where(c=>!ReferenceEquals(c,active.Choice)
    && AudioEventEvaluator.Matches(c.Conditions,_parameters)).ToArray();
   if(alternatives.Length==0)
   {
    CancelPreparedSubEvent(instrument);
    active.Child.handoffExitSeconds=0;
    return false;
   }
   if(alternatives.All(c=>c.Event.SharedMusicOutput))
    return PrepareSongEnd(instrument,active,alternatives);
  }
  if(!_pendingSubEvents.ContainsKey(instrument))
  {
   var hasAlternative=instrument.RotateSubEventsAtEnd && instrument.SubEvents.Any(c=>!ReferenceEquals(c,active.Choice) && AudioEventEvaluator.Matches(c.Conditions,_parameters));
   var grid=active.Choice.Event.BeatGrids.FirstOrDefault();
   active.Child.handoffExitEnd=active.Choice.Event.TimelineDuration;
   active.Child.handoffExitSeconds=hasAlternative && grid is not null ? (float)grid.SecondsPerBeat*instrument.SubEventTransitionBeats : 0;
  }
  if(!instrument.RotateSubEventsAtEnd)return false;
  active.Child.RefreshPlaybackState();
  if(active.Child._compositionCompletions<=active.ObservedCompletions)return false;
  if(instrument.SubEvents.Any(c=>!ReferenceEquals(c,active.Choice) && AudioEventEvaluator.Matches(c.Conditions,_parameters)))return true;
  active.ObservedCompletions=active.Child._compositionCompletions;
  return false;
 }

 private bool PrepareSongEnd(AudioInstrument instrument,SubEventVoice active,AudioSubEventChoice[] alternatives)
 {
  active.Child.RefreshPlaybackState();
  var completed=active.Child._compositionCompletions;
  var position=active.Child.CurrentTimelinePosition;
  var grid=active.Choice.Event.BeatGrids.FirstOrDefault();
  var remaining=Math.Max(0,active.Choice.Event.TimelineDuration-position);
  var fade=grid is null ? 0 : grid.SecondsPerBeat*instrument.SubEventTransitionBeats;
  var lead=grid is null ? 0 : Math.Max(fade,grid.SecondsPerBeat*grid.BeatsPerBar*grid.BarsPerPhrase);
  if(_preparedSubEvents.TryGetValue(instrument,out var prepared)
   && (!prepared.ForSongEnd || !alternatives.Contains(prepared.Choice)))
  {
   CancelPreparedSubEvent(instrument);
   _pendingSubEvents.Remove(instrument);
   prepared=null;
  }
  if(prepared is null && remaining<=lead)
  {
   var target=alternatives[Random.Shared.Next(alternatives.Length)];
   prepared=new SubEventVoice(this,target,0,instrument.SubEventTransitionBeats){ForSongEnd=true};
   _preparedSubEvents[instrument]=prepared;
   prepared.Prepare();
  }
  if(prepared is not null)
  {
   prepared.SyncParameters();
   if(prepared.Ready && prepared.SongEndCompletion is null && remaining>=fade && remaining>.0001)
    prepared.SongEndCompletion=completed+1;
   if(prepared.Ready && prepared.SongEndCompletion is long boundary)
   {
    active.Child.handoffExitEnd=active.Choice.Event.TimelineDuration;
    active.Child.handoffExitSeconds=(float)fade;
    if(completed>=boundary)return true;
   }
   else active.Child.handoffExitSeconds=0;
  }
  else active.Child.handoffExitSeconds=0;
  active.ObservedCompletions=completed;
  return false;
 }

	private AudioSubEventChoice SelectSubEvent( AudioInstrument instrument )
	{
		var active = _voices.FirstOrDefault( voice => voice.Instrument == instrument )?.Voice as SubEventVoice;
        var rotate = ShouldRotateSubEvent(instrument, active);
		var eligible = instrument.SubEvents.Where( choice => AudioEventEvaluator.Matches( choice.Conditions, _parameters ) ).ToArray();
		if ( eligible.Length == 0 ) { CancelPreparedSubEvent(instrument); _pendingSubEvents.Remove( instrument ); return null; }
		if ( _selectedSubEvents.TryGetValue( instrument, out var selected ) && eligible.Contains( selected ) && !rotate )
		{
			if (!_preparedSubEvents.TryGetValue(instrument,out var retained) || !retained.ForSongEnd) CancelPreparedSubEvent(instrument);
			_pendingSubEvents.Remove( instrument );
            ShouldRotateSubEvent(instrument,active);
			return selected;
		}
		var choices = rotate ? eligible.Where(c=>!ReferenceEquals(c,selected)).ToArray() : eligible;
		if ( !rotate && instrument.SubEventSelection == SoundTrackSelectionMode.RandomExclusive && eligible.Length > 1
			&& _subEventHistory.TryGetValue( instrument, out var history ) )
		{
			var previous = history.LastOrDefault( eligible.Contains );
			choices = eligible.Where( choice => !ReferenceEquals( choice, previous ) ).ToArray();
		}
		var target = _pendingSubEvents.TryGetValue( instrument, out var pending ) && eligible.Contains( pending.Choice )
			? pending.Choice : choices[Random.Shared.Next( choices.Length )];
		if (rotate && _preparedSubEvents.TryGetValue(instrument,out var rotation) && rotation.ForSongEnd && eligible.Contains(rotation.Choice))
            target=rotation.Choice;
		// A phase replacement stays paused while it decodes. Do not fade the current song until it is ready.
		if (!rotate && selected is not null && active is not null && active.IsPlaying && target.Event.SharedMusicOutput)
		{
			if (!_preparedSubEvents.TryGetValue(instrument, out var prepared) || !ReferenceEquals(prepared.Choice,target))
			{
				CancelPreparedSubEvent(instrument);
				prepared=new SubEventVoice(this,target,0,instrument.SubEventTransitionBeats);
				_preparedSubEvents[instrument]=prepared;
				prepared.Prepare();
				pending=null;
			}
			prepared.ForSongEnd=false;
			prepared.SongEndCompletion=null;
			prepared.SyncParameters();
			if (!prepared.Ready)
			{
				active.Child.handoffExitSeconds=0;
				_pendingSubEvents[instrument]=new(target,float.PositiveInfinity);
				return selected;
			}
			if (pending is not null && float.IsPositiveInfinity(pending.ParentPosition)) pending=null;
		}
		if ( !rotate && selected is not null && active is not null && active.IsPlaying && instrument.SubEventQuantization != TransitionQuantization.Immediate )
		{
			if ( pending is null || !ReferenceEquals( pending.Choice, target ) )
			{
				var childPosition = active.Child.CurrentTimelinePosition;
				var grid = active.Choice.Event.BeatGridAt( childPosition );
				if ( grid is not null )
				{
					var beats = instrument.SubEventQuantization switch
					{
						TransitionQuantization.NextBeat => 1,
						TransitionQuantization.NextBar => grid.BeatsPerBar,
						_ => grid.BeatsPerBar * grid.BarsPerPhrase
					};
					var quantum = grid.SecondsPerBeat * beats;
					var boundary = Math.Min( grid.End.Seconds, grid.Start.Seconds + (Math.Floor( (childPosition - grid.Start.Seconds) / quantum + .00001 ) + 1) * quantum );
					if(boundary-childPosition < grid.SecondsPerBeat*instrument.SubEventTransitionBeats)boundary=Math.Min(grid.End.Seconds,boundary+quantum);
					pending = new( target, CurrentTimelinePosition + (float)Math.Max( 0, boundary - childPosition ),
                        active.Child.HasSharedMusicOutput ? active.Child.sharedMusic.Position / 44100d + boundary - childPosition : double.NaN );
					_pendingSubEvents[instrument] = pending;
                    active.Child.handoffExitEnd=childPosition+Math.Max(0,pending.ParentPosition-CurrentTimelinePosition);
                    active.Child.handoffExitSeconds=(float)grid.SecondsPerBeat*instrument.SubEventTransitionBeats;
				}
			}
			if (pending is not null)
            {
                var waiting=double.IsNaN(pending.ChildBoundary)
                    ? CurrentTimelinePosition + .0001f < pending.ParentPosition
                    : active.Child.sharedMusic.Position / 44100d + .0001 < pending.ChildBoundary;
                if (waiting) return selected;
            }
		}
		_pendingSubEvents.Remove( instrument );
		selected = target;
		_selectedSubEvents[instrument] = selected;
		if ( !_subEventHistory.TryGetValue( instrument, out var recent ) ) _subEventHistory[instrument] = recent = new();
		recent.RemoveAll( choice => ReferenceEquals( choice, selected ) || !instrument.SubEvents.Contains( choice ) );
		recent.Add( selected );
		return selected;
	}

	/// <summary>Test a capability on this event or its currently playing descendants. Does not start events.</summary>
	public bool HasActiveEvent( Func<AudioEvent, bool> predicate )
	{
		ArgumentNullException.ThrowIfNull( predicate );
		if ( _playbackState != AudioPlaybackState.Playing ) return false;
		return predicate( _description.Resource ) || _voices.Any( voice => !voice.Pending && voice.Voice is SubEventVoice child
			&& child.Child.HasActiveEvent( predicate ) );
	}

	private void ReceiveParentParameters( AudioEventInstance parent )
	{
		var changed = false;
		foreach ( var parameter in _description.Resource.Parameters )
		{
			var inherited = parent._parameters.FirstOrDefault( item => string.Equals( item.Key.Name, parameter.Name, StringComparison.OrdinalIgnoreCase ) );
			if ( inherited.Key is null || _parameters[parameter].Equals( inherited.Value ) ) continue;
			_parameters[parameter] = inherited.Value;
			changed = true;
		}
		if ( changed && _playbackState is AudioPlaybackState.Starting or AudioPlaybackState.Playing ) ReevaluateIfPlaying();
	}

	/// <summary>Adapts a retained child event to the same scheduling and mix boundary as a sound voice.</summary>
	private sealed class SubEventVoice : IAudioVoice
	{
		private readonly AudioEventInstance parent;
		internal readonly AudioEventInstance Child;
		internal readonly AudioSubEventChoice Choice;
		private bool started;
		private bool stopped;
		private bool paused = true;
		private bool listenLocal;
		private Vector3 position;
		internal float ActivationPosition { get; set; }
        internal long ObservedCompletions;
        internal bool ForSongEnd;
        internal long? SongEndCompletion;
		internal SubEventVoice( AudioEventInstance parent, AudioSubEventChoice choice, float activationPosition, float entryBeats = 0 )
		{
			this.parent = parent;
			Choice = choice;
			var depth = 0;
			for ( var ancestor = parent; ancestor is not null; ancestor = ancestor._parentInstance )
			{
				if ( ++depth >= 16 || ReferenceEquals( ancestor._description.Resource, choice.Event ) )
					throw new InvalidOperationException( "Child event cycle or nesting deeper than 16 events." );
			}
			ActivationPosition = activationPosition;
			Child = new AudioEventDescription( choice.Event ).CreateInstance();
			Child.SharedMusicSessionFactory=parent.SharedMusicSessionFactory;
			Child.ParentLiveId = parent.LiveId;
			Child._parentInstance = parent;
            Child.handoffEntrySeconds=(float)(choice.Event.BeatGrids.FirstOrDefault()?.SecondsPerBeat ?? 0)*entryBeats;
			Child.Paused = true;
			listenLocal = !parent._hasAttributes3D;
			position = parent._attributes3D.Position;
			if ( !listenLocal ) Child.Attributes3D = parent._attributes3D;
		}
		internal bool Ready => started && !stopped && Child._playbackState == AudioPlaybackState.Playing;
		internal void Prepare()
		{
			if (started || stopped) return;
			Child.ReceiveParentParameters(parent);
			Child.Start();
			started=true;
		}
		internal void SyncParameters()
		{
			if ( started && !stopped && Child._playbackState is AudioPlaybackState.Starting or AudioPlaybackState.Playing ) Child.ReceiveParentParameters( parent );
		}
		public bool IsPlaying => stopped ? Child.PlaybackState == AudioPlaybackState.Stopping : !started || Child.PlaybackState != AudioPlaybackState.Stopped;
		public SoundFile SelectedSound => null;
		public bool Paused
		{
			get => paused;
			set
			{
				if ( stopped ) return;
				paused = value;
				if ( !started && !value )
				{
					Child.ReceiveParentParameters( parent );
					Child.Start();
					started = true;
				}
				Child.Paused = value;
			}
		}
		public float Time { get => Child.CurrentTimelinePosition; set { if ( !stopped ) Child.TimelinePosition = value; } }
		public float Volume { get => Child.Volume; set { if ( !stopped ) Child.Volume = value; } }
		public float Pitch { get => Child.Pitch; set { if ( !stopped ) Child.Pitch = value; } }
		public Vector3 Position { get => position; set { position = value; if ( !listenLocal && !stopped ) Child.Attributes3D = new Audio3DAttributes( value ); } }
		public bool ListenLocal { get => listenLocal; set { listenLocal = value; if ( !value && !stopped ) Child.Attributes3D = new Audio3DAttributes( position ); } }
		public void Stop( float fadeTime )
		{
			if ( stopped ) return;
			Child.StopWithFade( fadeTime );
			Child.Release();
			stopped = true;
		}
	}
}

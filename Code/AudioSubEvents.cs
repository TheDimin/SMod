namespace Smod;

/// <summary>One eligible child event in a timeline multi-instrument. Conditions use parent parameters.</summary>
public sealed class AudioSubEventChoice
{
	[Property] public AudioEvent Event { get; set; }
	[Property, Title( "Entry After Instrument" ), Description( "Parent sound instrument GUID. If its start trigger has fired, prepare this song and wait for that passage to finish. Completion bypasses entry quantization. No trigger in this session means normal entry. Same event only; no child-to-parent commands." )]
	public Guid EntryAfterInstrument { get; set; }
 [Property, Description("Optional shared music event played once between this song and a different phase. The parent prepares it, starts it on the exit boundary, then starts the destination. Initial playback and full-song loops do not use it.")] public AudioEvent ExitEvent {get;set;}
 [Property, Description("Start the next song this many seconds before the exit passage ends. The full exit tail stays alive without an extra fade. Zero waits for the whole passage. Choose a musical entry point in the parent.")] public float ExitEventOverlapSeconds {get;set;}
 internal bool PreserveExitTail {get;set;}
 [Property, Description("Remember this shared song position when leaving its phase and resume it on return. Position belongs to this parent instance, not the resource.")] public bool ResumePlayback {get;set;}
 [Property] public TransitionQuantization? EntryQuantization {get;set;}
 [Property, Range(0,16)] public float? EntryFadeBeats {get;set;}
 [Property, Range(0,10), Description("Overlap the outgoing shared song with this song for this many seconds. Zero keeps the sequential transition. The incoming song uses EntryFadeBeats.")] public float EntryCrossfadeSeconds {get;set;}
 [Property, InlineEditor, Description("Optional alternative entry conditions, such as a ready countdown. All must match. The same child continues when normal Conditions match; cancelling before then skips its ExitEvent.")] public List<AudioParameterCondition> EarlyEntryConditions {get;set;} = new();
 internal bool Eligible(IReadOnlyDictionary<AudioParameter,AudioParameterValue> parameters) => AudioEventEvaluator.Matches(Conditions,parameters) || EarlyEntryConditions is {Count:>0} && AudioEventEvaluator.Matches(EarlyEntryConditions,parameters);

	[Property, InlineEditor] public List<AudioParameterCondition> Conditions { get; set; } = new();
}

public sealed partial class AudioEventInstance
{
	private readonly Dictionary<AudioInstrument, AudioSubEventChoice> _selectedSubEvents = new();
 private readonly Dictionary<AudioSubEventChoice,float> _subEventResumePositions = new();
	private readonly Dictionary<AudioInstrument, List<AudioSubEventChoice>> _subEventHistory = new();
	private AudioEventInstance _parentInstance;
	private sealed record PendingSubEvent( AudioSubEventChoice Choice, float ParentPosition, double ChildBoundary = double.NaN );
	private readonly Dictionary<AudioInstrument, SubEventVoice> _preparedSubEvents = new();
	private void CancelPreparedSubEvent(AudioInstrument instrument)
	{
		if (_preparedSubEvents.Remove(instrument, out var voice)) voice.Stop(0);
        CancelSubEventBridge(instrument);
	}
	private void ClearPreparedSubEvents()
	{
		foreach (var voice in _preparedSubEvents.Values) voice.Stop(0);
		_preparedSubEvents.Clear();
        foreach(var bridge in _subEventBridges.Values)bridge.Voice.Stop(0);
        _subEventBridges.Clear();
	}
	private SubEventVoice TakeSubEvent(AudioInstrument instrument, AudioSubEventChoice choice, float position, float entryBeats)
	{
		if(_subEventBridges.TryGetValue(instrument,out var bridge) && ReferenceEquals(bridge.Choice,choice))
        { bridge.Voice.ActivationPosition=position;return bridge.Voice; }
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
  if(active is null || active.Paused || _subEventBridges.TryGetValue(instrument,out var bridge) && bridge.Started)return false;
  if(active.Choice.Event.SharedMusicOutput && !active.Choice.Eligible(_parameters))return false;
  // Shared songs prepare a single alternative during the last phrase. Late preparation waits a full loop.
  if(active.Choice.Event.SharedMusicOutput && instrument.RotateSubEventsAtEnd
   && active.Choice.Eligible(_parameters))
  {
   var alternatives=instrument.SubEvents.Where(c=>!ReferenceEquals(c,active.Choice)
    && c.Eligible(_parameters)).ToArray();
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
   var hasAlternative=instrument.RotateSubEventsAtEnd && instrument.SubEvents.Any(c=>!ReferenceEquals(c,active.Choice) && c.Eligible(_parameters));
   var grid=active.Choice.Event.BeatGrids.FirstOrDefault();
   active.Child.handoffExitEnd=active.Choice.Event.TimelineDuration;
   active.Child.handoffExitSeconds=hasAlternative && grid is not null ? (float)grid.SecondsPerBeat*instrument.SubEventTransitionBeats : 0;
  }
  if(!instrument.RotateSubEventsAtEnd)return false;
  active.Child.RefreshPlaybackState();
  if(active.Child._compositionCompletions<=active.ObservedCompletions)return false;
  if(instrument.SubEvents.Any(c=>!ReferenceEquals(c,active.Choice) && c.Eligible(_parameters)))return true;
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
        if(SelectRunningBridge(instrument,active,out var bridgeChoice))return bridgeChoice;
        var rotate = ShouldRotateSubEvent(instrument, active);
		var eligible = instrument.SubEvents.Where( choice => choice.Eligible(_parameters) ).ToArray();
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
		var entryQuantization=EntryInstrumentCompleted(target) || active?.EarlyEntry==true ? TransitionQuantization.Immediate : target.EntryQuantization ?? instrument.SubEventQuantization;
        var entryFadeBeats=target.EntryFadeBeats ?? instrument.SubEventTransitionBeats;
        // A phase replacement stays paused while it decodes. Do not fade the current song until it is ready.
		if (!rotate && selected is not null && active is not null && active.IsPlaying && target.Event.SharedMusicOutput)
		{
			if (!_preparedSubEvents.TryGetValue(instrument, out var prepared) || !ReferenceEquals(prepared.Choice,target))
			{
				CancelPreparedSubEvent(instrument);
				prepared=new SubEventVoice(this,target,0,entryFadeBeats);
				_preparedSubEvents[instrument]=prepared;
				prepared.Prepare();
				pending=null;
			}
			prepared.ForSongEnd=false;
			prepared.SongEndCompletion=null;
			prepared.SyncParameters();
			if (!prepared.Ready || !PrepareSubEventBridge(instrument,active))
			{
				active.Child.handoffExitSeconds=0;
				_pendingSubEvents[instrument]=new(target,float.PositiveInfinity);
				return selected;
			}
			if (pending is not null && float.IsPositiveInfinity(pending.ParentPosition)) pending=null;
		}
		if ( !rotate && WaitingForEntryInstrument( target ) )
		{
			if ( active is not null ) active.Child.handoffExitSeconds = 0;
			_pendingSubEvents[instrument] = new( target, float.PositiveInfinity );
			return selected;
		}
		if ( !rotate && selected is not null && active is not null && active.IsPlaying && entryQuantization != TransitionQuantization.Immediate )
		{
			if ( pending is null || !ReferenceEquals( pending.Choice, target ) )
			{
				var childPosition = active.Child.CurrentTimelinePosition;
				var grid = active.Choice.Event.BeatGridAt( childPosition );
				if ( grid is not null )
				{
					var beats = entryQuantization switch
					{
						TransitionQuantization.NextBeat => 1,
						TransitionQuantization.NextBar => grid.BeatsPerBar,
						_ => grid.BeatsPerBar * grid.BarsPerPhrase
					};
					var quantum = grid.SecondsPerBeat * beats;
					var boundary = Math.Min( grid.End.Seconds, grid.Start.Seconds + (Math.Floor( (childPosition - grid.Start.Seconds) / quantum + .00001 ) + 1) * quantum );
					if(target.EntryCrossfadeSeconds<=0 && boundary-childPosition < grid.SecondsPerBeat*entryFadeBeats)boundary=Math.Min(grid.End.Seconds,boundary+quantum);
					pending = new( target, CurrentTimelinePosition + (float)Math.Max( 0, boundary - childPosition ),
                        active.Child.HasSharedMusicOutput ? active.Child.sharedMusic.Position / 44100d + boundary - childPosition : double.NaN );
					_pendingSubEvents[instrument] = pending;
                    active.Child.handoffExitEnd=childPosition+Math.Max(0,pending.ParentPosition-CurrentTimelinePosition);
                    active.Child.handoffExitSeconds=target.EntryCrossfadeSeconds>0 ? 0 : (float)grid.SecondsPerBeat*entryFadeBeats;
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
        if(!rotate && selected is not null && BeginSubEventBridge(instrument,target) is {} transition)return transition;
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

	private (AudioParameter Child, AudioParameter Parent)[] _parentParameterBindings = Array.Empty<(AudioParameter, AudioParameter)>();

	private void ReceiveParentParameters( AudioEventInstance parent )
	{
		var changed = false;
		foreach ( var binding in _parentParameterBindings )
		{
			var value = parent._parameters[binding.Parent];
			if ( _parameters[binding.Child].Equals( value ) ) continue;
			_parameters[binding.Child] = value;
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
        private readonly float resumePosition;
		private Vector3 position;
		internal float ActivationPosition { get; set; }
        internal long ObservedCompletions;
        internal bool ForSongEnd;
        internal bool EarlyEntry;
        internal long? SongEndCompletion;
		internal SubEventVoice( AudioEventInstance parent, AudioSubEventChoice choice, float activationPosition, float entryBeats = 0 )
		{
			this.parent = parent;
			Choice = choice;
            EarlyEntry=!AudioEventEvaluator.Matches(choice.Conditions,parent._parameters);
            resumePosition=choice.ResumePlayback && choice.Event.SharedMusicOutput ? parent._subEventResumePositions.GetValueOrDefault(choice) : 0;
			var depth = 0;
			for ( var ancestor = parent; ancestor is not null; ancestor = ancestor._parentInstance )
			{
				if ( ++depth >= 16 || ReferenceEquals( ancestor._description.Resource, choice.Event ) )
					throw new InvalidOperationException( "Child event cycle or nesting deeper than 16 events." );
			}
			ActivationPosition = activationPosition;
            // Resolve names once for this child lifetime. Playback copies values through direct keys.
            var parentInputs = parent._parameters.Keys.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var bindings = choice.Event.Parameters.Select(p =>
                (Child: p, Parent: parentInputs.TryGetValue(p.Name, out var input) ? input
                    : throw new InvalidOperationException($"Child input '{p.Name}' is not declared by its direct parent."))).ToArray();
			Child = new AudioEventDescription( choice.Event ).CreateInstance();
            Child._parentParameterBindings = bindings;
			Child.SharedMusicSessionFactory=parent.SharedMusicSessionFactory;
            Child.PlaybackBackend=parent.PlaybackBackend;
			Child.ParentLiveId = parent.LiveId;
			Child._parentInstance = parent;
            foreach(var control in parent._description.Resource.Tracks.Prepend(parent._description.Resource.MasterTrack).SelectMany(t=>t.Instruments).Select(i=>i.LayerControl).Where(c=>c is not null && c.Mode==AudioMusicControlMode.SustainLayer))
            {
                var owner=parent._description.Resource.Tracks.Prepend(parent._description.Resource.MasterTrack).SelectMany(t=>t.Instruments).FirstOrDefault(i=>i.Id==control.SubEventInstrumentId);
                if(owner?.SubEvents.Contains(choice)==true && (control.ChildEvent is null || ReferenceEquals(control.ChildEvent,choice.Event)))Child.parentLayerGains[control.ChildTrackId]=control.IdleGain;
            }

            Child.handoffEntryStart=resumePosition;
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
            if(resumePosition>0)Child.TimelinePosition=resumePosition;
			started=true;
		}
		internal void SyncParameters()
		{
            if(AudioEventEvaluator.Matches(Choice.Conditions,parent._parameters))EarlyEntry=false;
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
                    if(resumePosition>0)Child.TimelinePosition=resumePosition;
					started = true;
				}
				Child.Paused = value;
			}
		}
		public float Time { get => Child.CurrentTimelinePosition; set { if ( !stopped ) Child.TimelinePosition = resumePosition+value; } }
		public float Volume { get => Child.Volume; set { if ( !stopped ) Child.Volume = value; } }
		public float Pitch { get => Child.Pitch; set { if ( !stopped ) Child.Pitch = value; } }
		public Vector3 Position { get => position; set { position = value; if ( !listenLocal && !stopped ) Child.Attributes3D = new Audio3DAttributes( value ); } }
		public bool ListenLocal { get => listenLocal; set { listenLocal = value; if ( !value && !stopped ) Child.Attributes3D = new Audio3DAttributes( position ); } }
		public void Stop( float fadeTime )
		{
			if ( stopped ) return;
            if(Choice.ResumePlayback && Choice.Event.SharedMusicOutput && started && !paused && parent._playbackState==AudioPlaybackState.Playing)
            {
                var at=Child.CurrentTimelinePosition;
                if(float.IsFinite(at) && at>=0 && at<Choice.Event.TimelineDuration)
                    parent._subEventResumePositions[Choice]=at;
            }
			Child.StopWithFade( fadeTime );
			Child.Release();
			stopped = true;
		}
	}
}

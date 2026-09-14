using Smod;

public sealed partial class AudioEventTests
{
	private static AudioEvent Nested( AudioEvent child ) => new()
	{
		MasterTrack = new AudioTrack { Name = "Master", Instruments = new() { new AudioInstrument { SubEvents = new() { new() { Event = child } } } } }
	};

 [TestMethod] public void SubGraph_GotoTargetsCannotCrossParentChildOrSiblingBoundaries()
 {
  var child=CreateEvent();var sibling=CreateEvent();var parent=Nested(child);
  parent.MasterTrack.Instruments[0].SubEvents.Add(new(){Event=sibling});
  var events=new[]{parent,child,sibling};
  foreach(var e in events)e.DestinationMarkers.Add(new(){Name="Local entry",Position=new(0)});
  foreach(var source in events)
  foreach(var target in events.Where(e=>!ReferenceEquals(e,source)))
  {
   source.TransitionMarkers.Add(new(){Name="Invalid cross-graph jump",Position=new(.5),DestinationMarkerId=target.DestinationMarkers[0].Id});
   var markerResult=AudioEventValidator.Validate(parent);
   Assert.IsFalse(markerResult.IsValid);
   Assert.IsTrue(markerResult.Issues.Any(i=>i.Message.Contains("cannot cross sub-graph")));
   source.TransitionMarkers.Clear();
   source.TransitionRegions.Add(new(){Name="Invalid cross-graph region",Start=new(.5),End=new(1),DestinationMarkerId=target.DestinationMarkers[0].Id});
   Assert.IsFalse(AudioEventValidator.Validate(parent).IsValid);
   source.TransitionRegions.Clear();
  }
  Assert.IsTrue(AudioEventValidator.Validate(parent).IsValid);
 }

	[TestMethod]
	public void SubGraph_InheritsCurrentParametersAndLiveUpdatesWithoutRestart()
	{
		var parameter = new AudioParameter { Name = "Intensity", Minimum = 0, Maximum = 1, DefaultValue = 0 };
		var child = CreateEvent(); child.Parameters.Add( parameter );
		child.MasterTrack.Automations.Add( new AudioAutomation { Parameter = parameter, Target = AudioAutomationTarget.Volume,
			Points = new() { new() { Input = 0, Output = .2f }, new() { Input = 1, Output = 1 } } } );
		var parent = Nested( child ); parent.Parameters.Add( parameter );
		var instance = AudioSystem.GetEvent( parent ).CreateInstance();
		instance.SetParameterByName( "Intensity", 1 ); instance.Volume = .5f; instance.Start();
		Assert.AreEqual( 1, _backend.Voices.Count ); Assert.AreEqual( .5f, _backend.Voices[0].Volume, .0001f );
		instance.SetParameterByName( "Intensity", 0 );
		Assert.AreEqual( 1, _backend.Voices.Count ); Assert.AreEqual( .1f, _backend.Voices[0].Volume, .0001f );
		instance.Paused = true; Assert.IsTrue( _backend.Voices[0].Paused );
		instance.Paused = false; Assert.IsFalse( _backend.Voices[0].Paused );
		var parentState = instance.CaptureLiveSnapshot();
		var childState = AudioLiveDiagnostics.CaptureActive().Single( state => state.Id == parentState.Voices[0].ChildInstanceId );
		Assert.AreEqual( parentState.Id, childState.ParentInstanceId );
		Assert.AreEqual( "0", childState.Parameters.Single().Value );
		instance.Stop( AudioStopMode.Immediate ); Assert.IsFalse( _backend.Voices[0].IsPlaying );
		Assert.IsNull( AudioLiveDiagnostics.CaptureActive().FirstOrDefault( state => state.Id == childState.Id ) );
	}

	[TestMethod]
	public void SubGraph_ChoiceStaysFixedUntilEligibilityChanges()
	{
		var phase = new AudioParameter { Name = "Phase", Minimum = 0, Maximum = 1, DefaultValue = 0 };
		var childA = CreateEvent(); var childB = CreateEvent();
		var parent = Nested( childA ); parent.Parameters.Add( phase );
		var instrument = parent.MasterTrack.Instruments[0];
		instrument.SubEvents[0].Conditions.Add( new() { Parameter = phase, Minimum = 0, Maximum = 0 } );
		instrument.SubEvents.Add( new() { Event = childB, Conditions = new() { new() { Parameter = phase, Minimum = 1, Maximum = 1 } } } );
		var instance = AudioSystem.GetEvent( parent ).CreateInstance(); instance.Start();
		var original = instance.InstrumentPlaybackStates.Single().ChildInstanceId;
		instance.SetParameterByName( "Phase", 0 ); Assert.AreEqual( original, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.SetParameterByName( "Phase", 1 );
		Assert.AreNotEqual( original, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		Assert.IsFalse( _backend.Voices[0].IsPlaying ); Assert.IsTrue( _backend.Voices[1].IsPlaying );
		instance.Stop( AudioStopMode.Immediate );
	}

	[TestMethod]
	public void SubGraph_FutureChildDoesNotStartBeforeItsBlockAndStopCancelsIt()
	{
		var parent = Nested( CreateEvent() ); parent.MasterTrack.Instruments[0].StartTime = 10;
		var instance = AudioSystem.GetEvent( parent ).CreateInstance(); instance.Start();
		Assert.AreEqual( 0, _backend.Voices.Count ); Assert.IsTrue( instance.InstrumentPlaybackStates.Single().IsPending );
		instance.Stop( AudioStopMode.Immediate );
		Assert.AreEqual( AudioPlaybackState.Stopped, instance.PlaybackState );
		Assert.AreEqual( 0, _backend.Voices.Count );
	}

	[TestMethod]
	public void SubGraph_ValidationRejectsCyclesAndIncompatibleInheritedParameters()
	{
		var parent = Nested( CreateEvent() );
		parent.MasterTrack.Instruments[0].SubEvents[0].Event = parent;
		Assert.IsTrue( AudioEventValidator.Validate( parent ).Issues.Any( issue => issue.Message.Contains( "cycle" ) ) );
		var child = CreateEvent(); child.Parameters.Add( new() { Name = "Intensity", Minimum = 0, Maximum = .5f } );
		parent.MasterTrack.Instruments[0].SubEvents[0].Event = child;
		parent.Parameters.Add( new() { Name = "Intensity", Minimum = 0, Maximum = 1 } );
		Assert.IsTrue( AudioEventValidator.Validate( parent ).Issues.Any( issue => issue.Message.Contains( "Inherited" ) ) );
	}

	[TestMethod]
	public void SubGraph_RandomSelectionIsPerInstanceAndDoesNotChangeOnGainUpdates()
	{
		var parent = Nested( CreateEvent() );
		parent.MasterTrack.Instruments[0].SubEvents.Add( new() { Event = CreateEvent() } );
		var first = AudioSystem.GetEvent( parent ).CreateInstance(); var second = AudioSystem.GetEvent( parent ).CreateInstance();
		first.Start(); second.Start();
		var firstId = first.InstrumentPlaybackStates.Single().ChildInstanceId;
		var secondId = second.InstrumentPlaybackStates.Single().ChildInstanceId;
		Assert.AreNotEqual( firstId, secondId );
		first.Volume = .3f; first.RefreshAuthoredPropertiesForAudition();
		Assert.AreEqual( firstId, first.InstrumentPlaybackStates.Single().ChildInstanceId );
		Assert.AreEqual( secondId, second.InstrumentPlaybackStates.Single().ChildInstanceId );
		first.Stop( AudioStopMode.Immediate ); second.Stop( AudioStopMode.Immediate );
	}
	[TestMethod]
	public void SubGraph_QuantizedChangeUsesChildBeatAndCancelsOnStateReversal()
	{
		var phase = new AudioParameter { Name = "Phase", Minimum = 0, Maximum = 1 };
		var a = CreateEvent(); a.BeatGrids.Add( new() { Name = "Song", Start = new( 0 ), End = new( 8 ), BeatsPerMinute = 120, BeatsPerBar = 4 } );
		var parent = Nested( a ); parent.Parameters.Add( phase );
		var instrument = parent.MasterTrack.Instruments[0]; instrument.SubEventQuantization = TransitionQuantization.NextBar;
		instrument.SubEvents[0].Conditions.Add( new() { Parameter = phase, Minimum = 0, Maximum = 0 } );
		instrument.SubEvents.Add( new() { Event = CreateEvent(), Conditions = new() { new() { Parameter = phase, Minimum = 1, Maximum = 1 } } } );
		var instance = AudioSystem.GetEvent( parent ).CreateInstance(); instance.Start();
		var first = instance.InstrumentPlaybackStates.Single().ChildInstanceId;
		_timeline.Advance( .3f ); instance.SetParameterByName( "Phase", 1 );
		Assert.AreEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.SetParameterByName( "Phase", 0 ); _timeline.Advance( 2 );
		Assert.AreEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.SetParameterByName( "Phase", 1 ); instance.Paused = true; _timeline.Advance( 5 );
		Assert.AreEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.Paused = false; _timeline.Advance( 1.8f );
		Assert.AreNotEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		Assert.AreEqual( 0f, _backend.Voices.Last().Time, .0001f, "A new song starts from its own beginning." );
		instance.Stop( AudioStopMode.Immediate );
	}

	[TestMethod]
	public void SubGraph_ChildLoopKeepsInstanceAndExclusiveChoiceChangesOnRestart()
	{
		var a = CreateEvent(); a.TimelineDuration = 2;
		a.LoopRegions.Add( new() { Start = new( 0 ), End = new( 2 ) } );
		var parent = Nested( a ); var instance = AudioSystem.GetEvent( parent ).CreateInstance(); instance.Start();
		var first = instance.InstrumentPlaybackStates.Single().ChildInstanceId;
		_timeline.Advance( 2.1f );
		Assert.AreEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.Stop( AudioStopMode.Immediate );
		var alternative = CreateEvent();
		parent.MasterTrack.Instruments[0].SubEvents.Add( new() { Event = alternative } );
		instance.Start();
#pragma warning disable CS0618
		Assert.AreSame( alternative.MasterTrack.Instruments[0].Sound.Tracks[0].Sound, _backend.Voices.Last().Sound.Sounds[0] );
#pragma warning restore CS0618
		Assert.AreNotEqual( first, instance.InstrumentPlaybackStates.Single().ChildInstanceId );
		instance.Stop( AudioStopMode.Immediate );
	}

	[TestMethod]
	public void SubGraph_ParentTrimAndFadeStopApplyToDescendantVoices()
	{
		var parent = Nested( CreateEvent() ); parent.MasterTrack.Instruments[0].TrimEnd = 1;
		var instance = AudioSystem.GetEvent( parent ).CreateInstance(); instance.Start();
		_timeline.Advance( 1.1f );
		Assert.AreEqual( AudioPlaybackState.Stopped, instance.PlaybackState );
		Assert.IsFalse( _backend.Voices[0].IsPlaying );
		instance = AudioSystem.GetEvent( Nested( CreateEvent() ) ).CreateInstance(); instance.Start();
		instance.Stop( AudioStopMode.AllowFadeout );
		Assert.AreEqual( .1f, _backend.Voices.Last().StopFadeTime, .0001f );
		Assert.AreEqual( AudioPlaybackState.Stopping, instance.PlaybackState );
		_backend.Voices.Last().IsPlaying = false;
		Assert.AreEqual( AudioPlaybackState.Stopped, instance.PlaybackState );
	}

	[TestMethod]
	public void SubGraph_InheritsNamedLabelsAndCountersThroughMultipleLevels()
	{
		var label = new AudioParameter { Name = "Stage", Type = AudioParameterType.Labeled, Labels = new() { "work", "finish" }, DefaultLabel = "work" };
		var count = new AudioParameter { Name = "Services", Type = AudioParameterType.Discrete, Minimum = 0, Maximum = 100 };
		var leaf = CreateEvent(); leaf.Parameters.Add( label ); leaf.Parameters.Add( count );
		var middle = Nested( leaf ); middle.Parameters.Add( label ); middle.Parameters.Add( count );
		var root = Nested( middle );
		root.Parameters.Add( new() { Name = "Stage", Type = AudioParameterType.Labeled, Labels = new() { "work", "finish" }, DefaultLabel = "work" } );
		root.Parameters.Add( new() { Name = "Services", Type = AudioParameterType.Discrete, Minimum = 0, Maximum = 100 } );
		var instance = AudioSystem.GetEvent( root ).CreateInstance(); instance.Start();
		instance.SetParameterByNameWithLabel( "Stage", "finish" ); instance.SetParameterByName( "Services", 7 );
		var mid = AudioLiveDiagnostics.CaptureActive().Single( state => state.ParentInstanceId == instance.LiveId );
		var child = AudioLiveDiagnostics.CaptureActive().Single( state => state.ParentInstanceId == mid.Id );
		Assert.AreEqual( "finish", child.Parameters.Single( value => value.Name == "Stage" ).Value );
		Assert.AreEqual( "7", child.Parameters.Single( value => value.Name == "Services" ).Value );
		Assert.IsTrue( instance.HasActiveEvent( candidate => ReferenceEquals( candidate, leaf ) ) );
		instance.Stop( AudioStopMode.Immediate );
		Assert.IsFalse( instance.HasActiveEvent( candidate => ReferenceEquals( candidate, leaf ) ) );
		Assert.IsFalse( _backend.Voices.Last().IsPlaying );
	}

	[TestMethod]
	public void SubGraph_InspectorDoesNotCreateAnUnconfiguredPlaylist()
	{
		var resource = Nested( CreateEvent() );
		Assert.IsFalse( Smod.Editor.AudioTimelineInspectorUx.ShowInGenericSheet( resource, nameof( AudioEvent.MusicPlaylist ) ) );
		Assert.IsNull( resource.MusicPlaylist );
		resource.MusicPlaylist = new AudioMusicPlaylist();
		Assert.IsTrue( Smod.Editor.AudioTimelineInspectorUx.ShowInGenericSheet( resource, nameof( AudioEvent.MusicPlaylist ) ) );
	}


 [TestMethod] public void SubGraph_RotatesOnlyAtFullSongEndAndHonorsPause()
 {
  var a=CreateEvent();a.TimelineDuration=2;a.LoopRegions.Add(new(){Start=new(0),End=new(2)});
  var b=CreateEvent();b.TimelineDuration=2;b.LoopRegions.Add(new(){Start=new(0),End=new(2)});
  var parent=Nested(a);var instrument=parent.MasterTrack.Instruments[0];
  instrument.SubEvents.Add(new(){Event=b});instrument.RotateSubEventsAtEnd=true;
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.Start();
  var first=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
  _timeline.Advance(1.9f);instance.Volume=.6f;
  Assert.AreEqual(first,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
  instance.Paused=true;_timeline.Advance(5);
  Assert.AreEqual(first,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
  instance.Paused=false;_timeline.Advance(.11f);
  var next=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
  Assert.AreNotEqual(first,next);
  Assert.IsFalse(_backend.Voices[0].IsPlaying);
  Assert.IsTrue(instance.InstrumentPlaybackStates.Single().PlaybackPosition<.01f);
  _timeline.Advance(1);Assert.AreEqual(next,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
  instance.Stop(AudioStopMode.Immediate);
 }
 [TestMethod] public void SubGraph_SingleEligibleSongKeepsItsLoopWhenRotationEnabled()
 {
  var song=CreateEvent();song.TimelineDuration=2;song.LoopRegions.Add(new(){Start=new(0),End=new(2)});
  var parent=Nested(song);parent.MasterTrack.Instruments[0].RotateSubEventsAtEnd=true;
  var instance=AudioSystem.GetEvent(parent).CreateInstance();instance.Start();
  var id=instance.InstrumentPlaybackStates.Single().ChildInstanceId;
  _timeline.Advance(2.1f);Assert.AreEqual(id,instance.InstrumentPlaybackStates.Single().ChildInstanceId);
  instance.Stop(AudioStopMode.Immediate);
 }
}

using Sandbox;
using System.Threading;
using System.Threading.Tasks;
using Smod;

[TestClass]
public sealed class AudioEventTests
{
	private FakeAudioBackend _backend;
	private FakeAudioTimelineScheduler _timeline;

	[TestInitialize]
	public void Initialize()
	{
		_backend = new FakeAudioBackend();
		_timeline = new FakeAudioTimelineScheduler();
		AudioSystem.SetBackendForTesting( _backend );
		AudioSystem.SetTimelineSchedulerForTesting( _timeline );
	}

	[TestCleanup]
	public void Cleanup()
	{
		AudioSystem.ResetBackendForTesting();
	}

	[TestMethod]
	public void Validation_RejectsAnEventWithoutInstruments()
	{
		var result = AudioEventValidator.Validate( new AudioEvent() );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "at least one instrument" ) ) );
	}

	[TestMethod]
	public void Validation_AssertValidFormatsEveryIssueOnItsOwnLine()
	{
		var result = AudioEventValidator.Validate( new AudioEvent { MasterTrack = null } );

		var exception = Assert.ThrowsExactly<InvalidOperationException>( result.AssertValid );
		var lines = exception.Message.Split( '\n', StringSplitOptions.RemoveEmptyEntries );

		Assert.AreEqual( result.Issues.Count + 1, lines.Length );
		foreach ( var issue in result.Issues )
			Assert.IsTrue( lines.Contains( $"{issue.Path}: {issue.Message}" ) );
	}

	[TestMethod]
	public void Validation_RejectsInvalidParameterAndInstrumentContracts()
	{
		var audioEvent = CreateEvent();
		var actionParameter = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "hit" },
			DefaultLabel = "missing"
		};
		audioEvent.Parameters.Add( actionParameter );
		audioEvent.MasterTrack.Instruments[0].Conditions.Add( new AudioParameterCondition
		{
			Parameter = new AudioParameter { Name = "Unknown" },
			Minimum = 0f,
			Maximum = 1f
		} );

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "Duplicate label" ) ) );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "must reference a parameter used by this AudioEvent" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsLabeledParameterValuesThatAreNotLowerCase()
	{
		var audioEvent = CreateEvent();
		audioEvent.Parameters.Add( new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "Hit" },
			DefaultLabel = "Hit"
		} );

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "must be lower-case" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsInvalidInstrumentStartTime()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = -1f;

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/StartTime" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsInvalidInstrumentTrimRange()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].TrimStart = 1f;
		audioEvent.MasterTrack.Instruments[0].TrimEnd = 0.5f;

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/TrimEnd" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsInvalidInstrumentFadeDurations()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].FadeInDuration = -1f;
		audioEvent.MasterTrack.Instruments[0].FadeInCurveMidpoint = 2f;
		audioEvent.MasterTrack.Instruments[0].FadeOutDuration = float.NaN;
		audioEvent.MasterTrack.Instruments[0].FadeOutCurveMidpoint = -0.1f;

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/FadeInDuration" ) ) );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/FadeInCurveMidpoint" ) ) );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/FadeOutDuration" ) ) );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/FadeOutCurveMidpoint" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsInstrumentWithoutSoundEvent()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].Sound = null;

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/Sound" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsOutOfRangeAndFractionalDiscreteConditions()
	{
		var audioEvent = CreateEvent();
		var surfaceParameter = new AudioParameter
		{
			Name = "Surface",
			Type = AudioParameterType.Discrete,
			Minimum = 0f,
			Maximum = 3f
		};
		audioEvent.Parameters.Add( surfaceParameter );
		audioEvent.MasterTrack.Instruments[0].Conditions.Add( new AudioParameterCondition
		{
			Parameter = surfaceParameter,
			Minimum = -1f,
			Maximum = 1.5f
		} );

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "within parameter" ) ) );
		Assert.IsTrue( result.Issues.Any( issue => issue.Message.Contains( "whole-number" ) ) );
	}

	[TestMethod]
	public void Validation_RejectsTrackConditionUsingAnUndeclaredParameter()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Conditions.Add( new AudioParameterCondition
		{
			Parameter = new AudioParameter { Name = "Undeclared" }
		} );

		var result = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( result.IsValid );
		Assert.IsTrue( result.Issues.Any( issue => issue.Path.EndsWith( "/MasterTrack/Conditions[0]/Parameter" ) ) );
	}

	[TestMethod]
	public void Start_PlaysEligibleTracksConcurrentlyAndSamplesAutomation()
	{
		var firstSound = CreateSoundEvent();
		var secondSound = CreateSoundEvent();
		var audioEvent = CreateEvent( firstSound );
		var intensityParameter = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f,
			DefaultValue = 0f
		};
		audioEvent.Parameters.Add( intensityParameter );
		audioEvent.MasterTrack.Volume = 0.5f;
		audioEvent.Tracks.Add( new AudioTrack
		{
			Name = "Second",
			Volume = 0.8f,
			Instruments = new()
			{
				new AudioInstrument
				{
					Sound = secondSound,
					Automations = new()
					{
						new AudioAutomation
						{
							Parameter = intensityParameter,
							Target = AudioAutomationTarget.Volume,
							Points = new()
							{
								new AudioAutomationPoint { Input = 0f, Output = 0.5f },
								new AudioAutomationPoint { Input = 1f, Output = 1.5f }
							}
						}
					}
				}
			}
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByName( "Intensity", 0.5f );
		instance.Start();

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.AreSame( firstSound.Tracks[0].Sound, _backend.Voices[0].Sound.Sounds[0] );
		Assert.AreSame( secondSound.Tracks[0].Sound, _backend.Voices[1].Sound.Sounds[0] );
		Assert.AreEqual( 0.4f, _backend.Voices[1].Volume, 0.0001f );
	}

	[TestMethod]
	public void LabeledParameter_SelectsTheMatchingInstrument()
	{
		var hit = CreateSoundEvent();
		var fall = CreateSoundEvent();
		var audioEvent = CreateLabeledEvent( hit, fall );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByNameWithLabel( "Action", "fall" );
		instance.Start();

		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.AreSame( fall.Tracks[0].Sound, _backend.Voices[0].Sound.Sounds[0] );
	}

	[TestMethod]
	public void LabeledParameter_RejectsAValueThatIsNotAuthored()
	{
		var audioEvent = CreateLabeledEvent( CreateSoundEvent(), CreateSoundEvent() );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		Assert.ThrowsExactly<ArgumentOutOfRangeException>(
			() => instance.SetParameterByNameWithLabel( "Action", "missing" ) );
		Assert.ThrowsExactly<ArgumentOutOfRangeException>(
			() => instance.SetParameterByNameWithLabel( "Action", "Fall" ) );
	}

	[TestMethod]
	public void TrackCondition_LiveChangeMutesAndRestoresOnlyThatTrack()
	{
		var audioEvent = CreateEvent();
		var action = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "fall" },
			DefaultLabel = "hit"
		};
		var conditionedSound = CreateSoundEvent();
		var conditionedTrack = new AudioTrack
		{
			Name = "Conditioned",
			Conditions = new() { new AudioParameterCondition { Parameter = action, Label = "fall" } },
			Instruments = new() { new AudioInstrument { Sound = conditionedSound } }
		};
		audioEvent.Parameters.Add( action );
		audioEvent.Tracks.Add( conditionedTrack );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();
		Assert.AreEqual( 1, _backend.Voices.Count );

		instance.SetParameterByNameWithLabel( "Action", "fall" );
		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsTrue( _backend.Voices[0].IsPlaying );
		Assert.IsTrue( _backend.Voices[1].IsPlaying );

		instance.SetParameterByNameWithLabel( "Action", "hit" );
		Assert.IsTrue( _backend.Voices[0].IsPlaying );
		Assert.IsFalse( _backend.Voices[1].IsPlaying );
	}

	[TestMethod]
	public void MasterTrackCondition_MutesTheWholeEvent()
	{
		var audioEvent = CreateEvent();
		var action = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "fall" },
			DefaultLabel = "hit"
		};
		audioEvent.Parameters.Add( action );
		audioEvent.MasterTrack.Conditions.Add( new AudioParameterCondition { Parameter = action, Label = "hit" } );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();
		Assert.IsTrue( _backend.Voices.Single().IsPlaying );

		instance.SetParameterByNameWithLabel( "Action", "fall" );
		Assert.IsFalse( _backend.Voices.Single().IsPlaying );

		instance.SetParameterByNameWithLabel( "Action", "hit" );
		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsTrue( _backend.Voices[1].IsPlaying );
	}

	[TestMethod]
	public void SharedParameter_RenameRemainsBoundAcrossEvents()
	{
		var sharedParameter = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "fall" },
			DefaultLabel = "hit"
		};
		var firstEvent = CreateEvent();
		var secondEvent = CreateEvent();
		firstEvent.Parameters.Add( sharedParameter );
		secondEvent.Parameters.Add( sharedParameter );
		firstEvent.MasterTrack.Instruments[0].Conditions.Add( new AudioParameterCondition { Parameter = sharedParameter, Label = "fall" } );
		secondEvent.MasterTrack.Instruments[0].Conditions.Add( new AudioParameterCondition { Parameter = sharedParameter, Label = "fall" } );
		sharedParameter.Name = "Outcome";

		var firstInstance = AudioSystem.GetEvent( firstEvent ).CreateInstance();
		firstInstance.SetParameterByNameWithLabel( "Outcome", "fall" );
		firstInstance.Start();
		var secondInstance = AudioSystem.GetEvent( secondEvent ).CreateInstance();
		secondInstance.SetParameterByNameWithLabel( "Outcome", "fall" );
		secondInstance.Start();

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsTrue( AudioEventValidator.Validate( firstEvent ).IsValid );
		Assert.IsTrue( AudioEventValidator.Validate( secondEvent ).IsValid );
	}

	[TestMethod]
	public void InvertedLabeledCondition_SelectsInstrumentWhenLabelDoesNotMatch()
	{
		var sound = CreateSoundEvent();
		var audioEvent = CreateLabeledEvent( sound, CreateSoundEvent() );
		audioEvent.MasterTrack.Instruments.RemoveAt( 1 );
		audioEvent.MasterTrack.Instruments[0].Conditions[0].Invert = true;

		var matchingInstance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		matchingInstance.SetParameterByNameWithLabel( "Action", "hit" );
		matchingInstance.Start();
		Assert.AreEqual( 0, _backend.Voices.Count );

		var differentInstance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		differentInstance.SetParameterByNameWithLabel( "Action", "fall" );
		differentInstance.Start();
		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.AreSame( sound.Tracks[0].Sound, _backend.Voices[0].Sound.Sounds[0] );
	}

	[TestMethod]
	public void InvertedNumericCondition_SelectsInstrumentOutsideRange()
	{
		var audioEvent = CreateEvent();
		var intensityParameter = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f
		};
		audioEvent.Parameters.Add( intensityParameter );
		audioEvent.MasterTrack.Instruments[0].Conditions.Add( new AudioParameterCondition
		{
			Parameter = intensityParameter,
			Minimum = 0.25f,
			Maximum = 0.75f,
			Invert = true
		} );

		var insideInstance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		insideInstance.SetParameterByName( "Intensity", 0.5f );
		insideInstance.Start();
		Assert.AreEqual( 0, _backend.Voices.Count );

		var outsideInstance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		outsideInstance.SetParameterByName( "Intensity", 1f );
		outsideInstance.Start();
		Assert.AreEqual( 1, _backend.Voices.Count );
	}

	[TestMethod]
	public void SetParameter_UpdatesAutomationWithoutRestartingTheVoice()
	{
		var audioEvent = CreateEvent();
		var intensityParameter = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f
		};
		audioEvent.Parameters.Add( intensityParameter );
		audioEvent.MasterTrack.Instruments[0].Automations.Add( new AudioAutomation
		{
			Parameter = intensityParameter,
			Target = AudioAutomationTarget.Volume,
			Points = new()
			{
				new AudioAutomationPoint { Input = 0f, Output = 0.5f },
				new AudioAutomationPoint { Input = 1f, Output = 1.5f }
			}
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices[0];
		voice.Time = 0.375f;

		instance.SetParameterByName( "Intensity", 1f );

		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.AreSame( voice, _backend.Voices[0] );
		Assert.AreEqual( 1.5f, voice.Volume, 0.0001f );
		Assert.AreEqual( 0.375f, voice.Time, 0.0001f );
	}

	[TestMethod]
	public void RefreshAuthoredPropertiesForAudition_UpdatesGainWithoutSeeking()
	{
		var audioEvent = CreateEvent();
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices[0];
		voice.Time = 0.65f;
		audioEvent.MasterTrack.Volume = 0.4f;

		instance.RefreshAuthoredPropertiesForAudition();

		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.AreSame( voice, _backend.Voices[0] );
		Assert.AreEqual( 0.4f, voice.Volume, 0.0001f );
		Assert.AreEqual( 0.65f, voice.Time, 0.0001f );
	}

	[TestMethod]
	public void AuditionEditorIntegration_IsPubliclyAccessibleToHotloadedEditorAssemblies()
	{
		var publicInstanceMethods = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

		Assert.IsNotNull( typeof( AudioEventInstance ).GetMethod(
			nameof( AudioEventInstance.RefreshAuthoredPropertiesForAudition ),
			publicInstanceMethods ) );
		Assert.IsNotNull( typeof( AudioEventInstance ).GetMethod(
			nameof( AudioEventInstance.SetTrackMutedForAudition ),
			publicInstanceMethods ) );
	}

	[TestMethod]
	public void AuditionMute_ChangesGainWithoutRestartingOrSeekingTheVoice()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Volume = 0.5f;
		var intensityParameter = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f
		};
		audioEvent.Parameters.Add( intensityParameter );
		var auditionTrack = new AudioTrack
		{
			Name = "Audition",
			Volume = 0.8f,
			Automations = new()
			{
				new AudioAutomation
				{
					Parameter = intensityParameter,
					Target = AudioAutomationTarget.Volume,
					Points = new()
					{
						new AudioAutomationPoint { Input = 0f, Output = 0.5f },
						new AudioAutomationPoint { Input = 1f, Output = 1.5f }
					}
				}
			},
			Instruments = new() { new AudioInstrument { Sound = CreateSoundEvent() } }
		};
		audioEvent.Tracks.Add( auditionTrack );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices[1];
		voice.Time = 0.3f;

		instance.SetTrackMutedForAudition( auditionTrack, true );
		instance.SetParameterByName( "Intensity", 1f );
		_timeline.Advance( 0.25f );

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.AreSame( voice, _backend.Voices[1] );
		Assert.AreEqual( 0f, voice.Volume, 0.0001f );
		Assert.AreEqual( 0.3f, voice.Time, 0.0001f );
		Assert.AreEqual( 0.25f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( AudioPlaybackState.Playing, instance.PlaybackState );

		instance.SetTrackMutedForAudition( auditionTrack, false );

		Assert.AreSame( voice, _backend.Voices[1] );
		Assert.AreEqual( 0.6f, voice.Volume, 0.0001f );
		Assert.AreEqual( 0.3f, voice.Time, 0.0001f );
	}

	[TestMethod]
	public void AuditionMute_RejectsTracksFromAnotherEvent()
	{
		var instance = AudioSystem.GetEvent( CreateEvent() ).CreateInstance();
		var foreignTrack = CreateEvent().MasterTrack;

		Assert.ThrowsExactly<ArgumentException>(
			() => instance.SetTrackMutedForAudition( foreignTrack, true ) );
	}

	[TestMethod]
	public void DelayedInstrument_StartsAtItsTimelineOffset()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();

		Assert.AreEqual( AudioPlaybackState.Playing, instance.PlaybackState );
		Assert.IsTrue( _backend.Voices[0].Paused );

		_timeline.Advance( 1f );

		Assert.IsFalse( _backend.Voices[0].Paused );
		Assert.AreEqual( 0f, _backend.Voices[0].Time, 0.0001f );
	}

	[TestMethod]
	public void InstrumentPlaybackStates_ExposePendingAndNativeSelectedSound()
	{
		var selectedSound = (SoundFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject( typeof( SoundFile ) );
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.StartTime = 1f;
		_backend.NextSelectedSound = selectedSound;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();

		Assert.AreEqual( 1, instance.InstrumentPlaybackStates.Count );
		var pending = instance.InstrumentPlaybackStates[0];
		Assert.AreSame( instrument, pending.Instrument );
		Assert.IsTrue( pending.IsPending );
		Assert.IsFalse( pending.IsPlaying );
		Assert.AreSame( selectedSound, pending.SelectedSound );

		_timeline.Advance( 1f );

		var playing = instance.InstrumentPlaybackStates[0];
		Assert.IsFalse( playing.IsPending );
		Assert.IsTrue( playing.IsPlaying );
		Assert.AreSame( selectedSound, playing.SelectedSound );
	}

	[TestMethod]
	public void TimelinePosition_SeeksAnActiveInstrument()
	{
		var instance = AudioSystem.GetEvent( CreateEvent() ).CreateInstance();
		instance.Start();

		instance.TimelinePosition = 0.75f;

		Assert.AreEqual( 0.75f, _backend.Voices[0].Time, 0.0001f );
		Assert.AreEqual( 1, _backend.Voices.Count );
	}

	[TestMethod]
	public void TrimmedInstrument_StartsAtTrimInAndStopsAtTrimOut()
	{
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.TrimStart = 0.25f;
		instrument.TrimEnd = 0.75f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();

		Assert.AreEqual( 0.25f, _backend.Voices[0].Time, 0.0001f );
		_timeline.Advance( 0.49f );
		Assert.IsTrue( _backend.Voices[0].IsPlaying );
		_timeline.Advance( 0.01f );
		Assert.IsFalse( _backend.Voices[0].IsPlaying );
	}

	[TestMethod]
	public void InstrumentFades_ModulateVolumeAcrossTheAuthoredClip()
	{
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.TrimEnd = 2f;
		instrument.FadeInDuration = 1f;
		instrument.FadeOutDuration = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();
		Assert.AreEqual( 0f, _backend.Voices[0].Volume, 0.0001f );

		_timeline.Advance( 0.5f );
		_ = instance.PlaybackState;
		Assert.AreEqual( 0.5f, _backend.Voices[0].Volume, 0.0001f );

		_timeline.Advance( 0.5f );
		_ = instance.PlaybackState;
		Assert.AreEqual( 1f, _backend.Voices[0].Volume, 0.0001f );

		_timeline.Advance( 0.5f );
		_ = instance.PlaybackState;
		Assert.AreEqual( 0.5f, _backend.Voices[0].Volume, 0.0001f );
	}

	[TestMethod]
	public void InstrumentFade_CombinesWithTrackAndInstanceGain()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Volume = 0.5f;
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.TrimEnd = 2f;
		instrument.FadeInDuration = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Volume = 0.5f;
		instance.Start();

		_timeline.Advance( 0.5f );
		_ = instance.PlaybackState;

		Assert.AreEqual( 0.125f, _backend.Voices[0].Volume, 0.0001f );
	}

	[TestMethod]
	public void InstrumentFadeCurveMidpoint_ChangesTheEnvelopeShape()
	{
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.TrimEnd = 2f;
		instrument.FadeInDuration = 1f;
		instrument.FadeInCurveMidpoint = 1f;
		instrument.FadeOutDuration = 1f;
		instrument.FadeOutCurveMidpoint = 0f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		_timeline.Advance( 0.5f );
		_ = instance.PlaybackState;
		Assert.AreEqual( 0.75f, _backend.Voices[0].Volume, 0.0001f );

		_timeline.Advance( 1f );
		_ = instance.PlaybackState;
		Assert.AreEqual( 0.25f, _backend.Voices[0].Volume, 0.0001f );
	}

	[TestMethod]
	public void TimelinePosition_SeeksRelativeToInstrumentTrimIn()
	{
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.TrimStart = 0.25f;
		instrument.TrimEnd = 0.75f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		instance.TimelinePosition = 0.2f;

		Assert.AreEqual( 0.45f, _backend.Voices[0].Time, 0.0001f );
		_timeline.Advance( 0.3f );
		Assert.IsFalse( _backend.Voices[0].IsPlaying );
	}

	[TestMethod]
	public void Paused_FreezesPendingTimelineStarts()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		instance.Paused = true;
		_timeline.Advance( 2f );

		Assert.IsTrue( _backend.Voices[0].Paused );
		Assert.AreEqual( 0f, instance.TimelinePosition, 0.0001f );

		instance.Paused = false;
		_timeline.Advance( 1f );

		Assert.IsFalse( _backend.Voices[0].Paused );
	}

	[TestMethod]
	public void Stop_CancelsPendingTimelineStarts()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		instance.Stop( AudioStopMode.Immediate );
		_timeline.Advance( 2f );

		Assert.IsFalse( _backend.Voices[0].IsPlaying );
		Assert.AreEqual( AudioPlaybackState.Stopped, instance.PlaybackState );
	}

	[TestMethod]
	public void Release_AllowsPendingTimelineStartsToFinishFireAndForget()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 1f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		instance.Release();

		_timeline.Advance( 1f );

		Assert.IsFalse( instance.IsValid );
		Assert.IsFalse( _backend.Voices[0].Paused );
		Assert.IsTrue( _backend.Voices[0].IsPlaying );
	}

	[TestMethod]
	public void LabeledParameter_LiveChangeSwitchesEligibleInstrument()
	{
		var hit = CreateSoundEvent();
		var fall = CreateSoundEvent();
		var instance = AudioSystem.GetEvent( CreateLabeledEvent( hit, fall ) ).CreateInstance();
		instance.Start();

		instance.SetParameterByNameWithLabel( "Action", "fall" );

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsFalse( _backend.Voices[0].IsPlaying );
		Assert.AreSame( fall.Tracks[0].Sound, _backend.Voices[1].Sound.Sounds[0] );
		Assert.IsTrue( _backend.Voices[1].IsPlaying );
	}

	[TestMethod]
	public void InstanceProperties_UpdateActiveVoices()
	{
		var instance = AudioSystem.GetEvent( CreateEvent() ).CreateInstance();
		instance.Start();
		Assert.IsTrue( _backend.Voices[0].ListenLocal );
		instance.Volume = 0.25f;
		instance.Pitch = 1.5f;
		instance.Attributes3D = new Audio3DAttributes( new Vector3( 10f, 20f, 30f ) );

		Assert.AreEqual( 0.25f, _backend.Voices[0].Volume, 0.0001f );
		Assert.AreEqual( 1.5f, _backend.Voices[0].Pitch, 0.0001f );
		Assert.AreEqual( new Vector3( 10f, 20f, 30f ), _backend.Voices[0].Position );
		Assert.IsFalse( _backend.Voices[0].ListenLocal );
	}

	[TestMethod]
	public void Stop_UsesTheAuthoredFadeForAllowFadeout()
	{
		var audioEvent = CreateEvent();
		audioEvent.FadeOutDuration = 0.35f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		instance.Stop( AudioStopMode.AllowFadeout );

		Assert.AreEqual( 0.35f, _backend.Voices[0].StopFadeTime, 0.0001f );
	}

	[TestMethod]
	public void Release_AllowsFireAndForgetButRejectsFurtherCalls()
	{
		var instance = AudioSystem.GetEvent( CreateEvent() ).CreateInstance();
		instance.Start();
		instance.Release();

		Assert.IsFalse( instance.IsValid );
		Assert.IsTrue( _backend.Voices[0].IsPlaying );
		Assert.ThrowsExactly<InvalidOperationException>( instance.Start );
	}

	[TestMethod]
	public void Start_SelectsFromCustomSoundTracks()
	{
		var sound = CreateSoundEvent();
		var audioEvent = CreateEvent( sound );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		_backend.Voices[^1].IsPlaying = false;
		instance.Start();

		Assert.AreEqual( 2, _backend.Voices.Count );
		CollectionAssert.AreEqual( sound.Tracks.Select( track => track.Sound ).ToArray(), new[] { _backend.Voices[0].Sound.Sounds[0] } );
		CollectionAssert.AreEqual( sound.Tracks.Select( track => track.Sound ).ToArray(), new[] { _backend.Voices[1].Sound.Sounds[0] } );
	}

	[TestMethod]
	public void RandomExclusive_AvoidsThePreviousSelectionAcrossInstances()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};

		AudioSystem.GetEvent( CreateEvent( sound ) ).CreateInstance().Start();
		AudioSystem.GetEvent( CreateEvent( sound ) ).CreateInstance().Start();

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.AreNotSame( _backend.Voices[0].Sound.Sounds.Single(), _backend.Voices[1].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void SoundTrackConditions_FilterRandomSelection()
	{
		var audioEvent = CreateEvent();
		var action = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "fall" },
			DefaultLabel = "hit"
		};
		var hit = CreateSoundEvent().Tracks[0].Sound;
		var fall = CreateSoundEvent().Tracks[0].Sound;
		audioEvent.Parameters.Add( action );
		audioEvent.MasterTrack.Instruments[0].Sound.Tracks = new()
		{
			new SoundEventTrack
			{
				Sound = hit,
				Conditions = new() { new AudioParameterCondition { Parameter = action, Label = "hit" } }
			},
			new SoundEventTrack
			{
				Sound = fall,
				Conditions = new() { new AudioParameterCondition { Parameter = action, Label = "fall" } }
			}
		};

		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByNameWithLabel( "Action", "fall" );
		instance.Start();

		Assert.AreSame( fall, _backend.Voices.Single().Sound.Sounds.Single() );

		instance.SetParameterByNameWithLabel( "Action", "hit" );

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsFalse( _backend.Voices[0].IsPlaying );
		Assert.AreSame( hit, _backend.Voices[1].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void SoundTrackOffsetAndLength_ControlTheSelectedClip()
	{
		var audioEvent = CreateEvent();
		var soundTrack = audioEvent.MasterTrack.Instruments[0].Sound.Tracks[0];
		soundTrack.Offset = 1.25f;
		soundTrack.Length = 0.75f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();

		Assert.AreEqual( 1.25f, _backend.Voices.Single().Time, 0.0001f );
		_timeline.Advance( 0.75f );
		Assert.IsFalse( _backend.Voices.Single().IsPlaying );
	}

	[TestMethod]
	public void InstrumentTrim_IsLocalToASlicedSoundTrack()
	{
		var audioEvent = CreateEvent();
		var instrument = audioEvent.MasterTrack.Instruments[0];
		instrument.Sound.Tracks[0].Offset = 1.25f;
		instrument.Sound.Tracks[0].Length = 0.75f;
		instrument.TrimStart = 0.25f;
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();

		instance.Start();

		Assert.AreEqual( 1.5f, _backend.Voices.Single().Time, 0.0001f );
		_timeline.Advance( 0.5f );
		Assert.IsFalse( _backend.Voices.Single().IsPlaying );
	}

	[TestMethod]
	public void SelectedTrack_PreservesDirectSoundEventSettings()
	{
		var sound = CreateSoundEvent();
		sound.UI = true;
		sound.OcclusionEnabled = false;
		sound.ReverbEnabled = false;
		sound.AirAbsorption = false;
		sound.DistanceAttenuation = false;
		sound.Volume = new RangedFloat( 0.25f, 0.5f );
		sound.Pitch = new RangedFloat( 0.8f, 1.2f );
		sound.Decibels = 83;
		sound.Distance = 4321f;

		var instance = AudioSystem.GetEvent( CreateEvent( sound ) ).CreateInstance();
		instance.Start();

		var playbackEvent = _backend.Voices.Single().Sound;
		Assert.IsTrue( playbackEvent.UI );
		Assert.IsFalse( playbackEvent.OcclusionEnabled );
		Assert.IsFalse( playbackEvent.ReverbEnabled );
		Assert.IsFalse( playbackEvent.AirAbsorption );
		Assert.IsFalse( playbackEvent.DistanceAttenuation );
		Assert.AreEqual( 0.25f, playbackEvent.Volume.Min );
		Assert.AreEqual( 0.5f, playbackEvent.Volume.Max );
		Assert.AreEqual( 0.8f, playbackEvent.Pitch.Min );
		Assert.AreEqual( 1.2f, playbackEvent.Pitch.Max );
#pragma warning disable CS0618
		Assert.AreEqual( 83, playbackEvent.Decibels );
#pragma warning restore CS0618
		Assert.AreEqual( 4321f, playbackEvent.Distance );
		Assert.AreSame( sound.Tracks[0].Sound, playbackEvent.Sounds.Single() );
	}

	[TestMethod]
	public void TimelineTransitionMarker_JumpsWhenCrossedAndConditionMatches()
	{
		var audioEvent = CreateEvent();
		var state = AddLabeledState( audioEvent );
		var destination = new DestinationMarker { Name = "CombatStart", Position = new TimelineTime( 0.25 ) };
		audioEvent.DestinationMarkers.Add( destination );
		audioEvent.TransitionMarkers.Add( new TransitionMarker
		{
			Position = new TimelineTime( 1.0 ),
			DestinationMarkerId = destination.Id,
			Conditions = LabelCondition( state, "combat" )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByNameWithLabel( "MusicState", "combat" );
		instance.Start();
		var voice = _backend.Voices.Single();
		var initialTimeAssignments = voice.TimeAssignments;

		_timeline.Advance( 1.2f );

		Assert.AreEqual( 0.45f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( initialTimeAssignments + 1, voice.TimeAssignments );
	}

	[TestMethod]
	public void TimelinePlayback_DoesNotSeekForSampleQuantization()
	{
		var audioEvent = CreateEvent();
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices.Single();
		var initialTimeAssignments = voice.TimeAssignments;

		for ( var frame = 0; frame < 120; frame++ )
		{
			_timeline.Advance( 1f / 60f );
			_ = instance.TimelinePosition;
		}

		Assert.AreEqual( initialTimeAssignments, voice.TimeAssignments );
		Assert.AreEqual( 0, instance.TimelineDebugSnapshot.JumpsLastUpdate );
	}

	[TestMethod]
	public void TimelinePosition_ExplicitChangeStillSeeks()
	{
		var audioEvent = CreateEvent();
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices.Single();
		var initialTimeAssignments = voice.TimeAssignments;

		instance.TimelinePosition = 0.25f;

		Assert.AreEqual( initialTimeAssignments + 1, voice.TimeAssignments );
		Assert.AreEqual( 0.25f, voice.Time, 0.0001f );
	}

	[TestMethod]
	public void TimelineLoop_PreservesOvershootAndIsPerInstance()
	{
		var audioEvent = CreateEvent();
		var state = AddLabeledState( audioEvent );
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 1.0 ),
			Conditions = LabelCondition( state, "exploration" )
		} );
		var looping = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		looping.SetParameterByNameWithLabel( "MusicState", "exploration" );
		looping.Start();
		var exiting = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		exiting.SetParameterByNameWithLabel( "MusicState", "combat" );
		exiting.Start();

		_timeline.Advance( 1.02f );

		Assert.AreEqual( 0.42f, looping.TimelinePosition, 0.0001f );
		Assert.AreEqual( 1.02f, exiting.TimelinePosition, 0.0001f );
	}

	[TestMethod]
	public void TimelineLoop_SeeksActiveVoiceOnceWhenCrossed()
	{
		var audioEvent = CreateEvent();
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 1.0 )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var voice = _backend.Voices.Single();
		var initialTimeAssignments = voice.TimeAssignments;

		_timeline.Advance( 1.02f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( initialTimeAssignments + 1, voice.TimeAssignments );
	}

	[TestMethod]
	public void TimelineLoop_RetriggersAnInsideRandomExclusiveInstrumentWithANewVariation()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};
		var audioEvent = CreateEvent( sound );
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.1f;
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 1 )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var initialVoice = _backend.Voices.Single();

		_timeline.Advance( 1.02f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsFalse( initialVoice.IsPlaying );
		Assert.AreNotSame( initialVoice.Sound.Sounds.Single(), _backend.Voices[1].Sound.Sounds.Single() );
		Assert.AreEqual( 0f, _backend.Voices[1].Time, 0.0001f );
		Assert.IsTrue( instance.InstrumentPlaybackStates.Single().IsPending );
	}

	[TestMethod]
	public void TimelineLoop_DoesNotRetriggerAnInstrumentThatStartsBeforeTheRegion()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};
		var audioEvent = CreateEvent( sound );
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.2f;
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 1 )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var initialVoice = _backend.Voices.Single();

		_timeline.Advance( 1.02f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.IsTrue( initialVoice.IsPlaying );
		Assert.AreSame( initialVoice.Sound.Sounds.Single(), _backend.Voices[0].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void TimelineTransition_DoesNotRetriggerAMultiInstrument()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};
		var audioEvent = CreateEvent( sound );
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.1f;
		var destination = new DestinationMarker { Name = "Destination", Position = new TimelineTime( 0.25 ) };
		audioEvent.DestinationMarkers.Add( destination );
		audioEvent.TransitionMarkers.Add( new TransitionMarker
		{
			Position = new TimelineTime( 1 ),
			DestinationMarkerId = destination.Id
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var initialVoice = _backend.Voices.Single();

		_timeline.Advance( 1.02f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, _backend.Voices.Count );
		Assert.IsTrue( initialVoice.IsPlaying );
		Assert.AreSame( initialVoice.Sound.Sounds.Single(), _backend.Voices[0].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void TimelineLoop_AdvancesVariationSelectionForEveryWrapCrossedInOneUpdate()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};
		var audioEvent = CreateEvent( sound );
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.4f;
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 0.6 )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var initialSound = _backend.Voices.Single().Sound.Sounds.Single();

		_timeline.Advance( 0.85f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 2, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 0.45f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.AreSame( initialSound, _backend.Voices[1].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void ContentEndLoop_WrapsWhenItsSelectedVariationFinishes()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.1f;
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 10 ),
			EndMode = LoopEndMode.ContentEnd
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		_timeline.Advance( 0.5f );
		_backend.Voices.Single().IsPlaying = false;
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 0f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.IsTrue( instance.InstrumentPlaybackStates.Single().IsPending );
	}

	[TestMethod]
	public void ContentEndLoop_WaitsForPlayingAndPendingInRegionInstruments()
	{
		var firstInstrument = new AudioInstrument { Sound = CreateSoundEvent(), StartTime = 0.1f };
		var secondInstrument = new AudioInstrument { Sound = CreateSoundEvent(), StartTime = 0.6f };
		var audioEvent = new AudioEvent
		{
			MasterTrack = new AudioTrack
			{
				Name = "Master",
				Instruments = new() { firstInstrument, secondInstrument }
			},
			LoopRegions = new()
			{
				new LoopRegion
				{
					Start = new TimelineTime( 0 ),
					End = new TimelineTime( 10 ),
					EndMode = LoopEndMode.ContentEnd
				}
			}
		};
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();

		_timeline.Advance( 0.2f );
		_backend.Voices[0].IsPlaying = false;
		_ = instance.TimelinePosition;

		Assert.AreEqual( 0, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.IsTrue( instance.InstrumentPlaybackStates.Single( state => ReferenceEquals( state.Instrument, secondInstrument ) ).IsPending );

		_timeline.Advance( 0.5f );
		_backend.Voices[1].IsPlaying = false;
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 0f, instance.TimelinePosition, 0.0001f );
	}

	[TestMethod]
	public void ContentEndLoop_IgnoresIneligibleAndOutOfRegionInstruments()
	{
		var audioEvent = CreateEvent();
		var state = AddLabeledState( audioEvent );
		var inside = audioEvent.MasterTrack.Instruments[0];
		inside.StartTime = 0.5f;
		var ineligible = new AudioInstrument
		{
			Sound = CreateSoundEvent(),
			StartTime = 0.6f,
			Conditions = new() { new AudioParameterCondition { Parameter = state, Label = "combat" } }
		};
		var outside = new AudioInstrument { Sound = CreateSoundEvent(), StartTime = 3f };
		audioEvent.MasterTrack.Instruments.AddRange( [ineligible, outside] );
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 2 ),
			EndMode = LoopEndMode.ContentEnd
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByNameWithLabel( "MusicState", "exploration" );
		instance.Start();

		_timeline.Advance( 0.8f );
		var insideVoice = _backend.Voices.Single( voice => ReferenceEquals( voice.Sound.Sounds.Single(), inside.Sound.Tracks[0].Sound ) );
		insideVoice.IsPlaying = false;
		_ = instance.TimelinePosition;

		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 0.4f, instance.TimelinePosition, 0.0001f );
		Assert.IsFalse( instance.InstrumentPlaybackStates.Any( state => ReferenceEquals( state.Instrument, ineligible ) ) );
	}

	[TestMethod]
	public void ContentEndLoop_WithoutEligibleContentFallsBackToAuthoredEnd()
	{
		var audioEvent = CreateEvent();
		audioEvent.MasterTrack.Instruments[0].StartTime = 0.1f;
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0.4 ),
			End = new TimelineTime( 1 ),
			EndMode = LoopEndMode.ContentEnd
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		_backend.Voices.Single().IsPlaying = false;

		_timeline.Advance( 0.6f );
		Assert.AreEqual( 0.6f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 0, instance.TimelineDebugSnapshot.JumpsLastUpdate );

		_timeline.Advance( 0.42f );

		Assert.AreEqual( 0.42f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 1, _backend.Voices.Count );
	}

	[TestMethod]
	public void FixedLoop_DoesNotWrapWhenContentFinishesEarly()
	{
		var audioEvent = CreateEvent();
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 10 )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		_backend.Voices.Single().IsPlaying = false;

		_timeline.Advance( 0.5f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( AudioPlaybackState.Stopped, instance.PlaybackState );
		Assert.AreEqual( 0, instance.TimelineDebugSnapshot.JumpsLastUpdate );
		Assert.AreEqual( 1, _backend.Voices.Count );
	}

	[TestMethod]
	public void ContentEndLoop_RetriggersRandomExclusiveWithANewVariation()
	{
		var first = CreateSoundEvent().Tracks[0].Sound;
		var second = CreateSoundEvent().Tracks[0].Sound;
		var sound = new SoundEventResource
		{
			SelectionMode = SoundTrackSelectionMode.RandomExclusive,
			Tracks = new()
			{
				new SoundEventTrack { Sound = first },
				new SoundEventTrack { Sound = second }
			}
		};
		var audioEvent = CreateEvent( sound );
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 10 ),
			EndMode = LoopEndMode.ContentEnd
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var initialVoice = _backend.Voices.Single();

		_timeline.Advance( 0.5f );
		initialVoice.IsPlaying = false;
		_ = instance.TimelinePosition;

		Assert.AreEqual( 2, _backend.Voices.Count );
		Assert.AreNotSame( initialVoice.Sound.Sounds.Single(), _backend.Voices[1].Sound.Sounds.Single() );
	}

	[TestMethod]
	public void ContentEndLoop_DoesNotOverrideACrossedTransition()
	{
		var audioEvent = CreateEvent();
		var destination = new DestinationMarker { Name = "Bridge", Position = new TimelineTime( 0.25 ) };
		audioEvent.DestinationMarkers.Add( destination );
		audioEvent.TransitionMarkers.Add( new TransitionMarker
		{
			Position = new TimelineTime( 0.5 ),
			DestinationMarkerId = destination.Id
		} );
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 10 ),
			EndMode = LoopEndMode.ContentEnd
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		_backend.Voices.Single().IsPlaying = false;

		_timeline.Advance( 0.6f );
		_ = instance.TimelinePosition;

		Assert.AreEqual( 0.35f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( 1, instance.TimelineDebugSnapshot.JumpsLastUpdate );
	}

	[TestMethod]
	public void QuantizedRegion_CancelsWhenConditionBecomesFalse()
	{
		var audioEvent = CreateEvent();
		audioEvent.TempoMap.BeatsPerMinute = 60f;
		var state = AddLabeledState( audioEvent );
		var destination = new DestinationMarker { Name = "BossStart", Position = new TimelineTime( 0.1 ) };
		audioEvent.DestinationMarkers.Add( destination );
		audioEvent.TransitionRegions.Add( new TransitionRegion
		{
			Start = new TimelineTime( 0.25 ),
			End = new TimelineTime( 2.0 ),
			DestinationMarkerId = destination.Id,
			Quantization = TransitionQuantization.NextBeat,
			Conditions = LabelCondition( state, "combat" )
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.SetParameterByNameWithLabel( "MusicState", "combat" );
		instance.Start();
		_timeline.Advance( 0.5f );
		Assert.AreEqual( 48000, instance.TimelineDebugSnapshot.ScheduledExecutionSample );

		instance.SetParameterByNameWithLabel( "MusicState", "exploration" );
		_timeline.Advance( 0.6f );

		Assert.AreEqual( 1.1f, instance.TimelinePosition, 0.0001f );
		Assert.AreEqual( Guid.Empty, instance.TimelineDebugSnapshot.PendingTransitionId );
	}

	[TestMethod]
	public void TimelineValidation_RejectsDuplicateDestinationsBrokenReferencesAndOverlappingLoops()
	{
		var audioEvent = CreateEvent();
		audioEvent.TimelineDuration = 3f;
		var duplicateId = Guid.NewGuid();
		audioEvent.DestinationMarkers.Add( new DestinationMarker { Id = duplicateId, Name = "Section", Position = new TimelineTime( 1 ) } );
		audioEvent.DestinationMarkers.Add( new DestinationMarker { Id = duplicateId, Name = "Section", Position = new TimelineTime( 2 ) } );
		audioEvent.TransitionMarkers.Add( new TransitionMarker { Position = new TimelineTime( 1 ), DestinationMarkerId = Guid.NewGuid() } );
		audioEvent.LoopRegions.Add( new LoopRegion { Start = new TimelineTime( 0.5 ), End = new TimelineTime( 1.5 ) } );
		audioEvent.LoopRegions.Add( new LoopRegion { Start = new TimelineTime( 1.0 ), End = new TimelineTime( 2.0 ) } );

		var validation = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( validation.IsValid );
		Assert.IsTrue( validation.Issues.Any( issue => issue.Message.Contains( "Duplicate timeline-control ID" ) ) );
		Assert.IsTrue( validation.Issues.Any( issue => issue.Message.Contains( "unique" ) ) );
		Assert.IsTrue( validation.Issues.Any( issue => issue.Message.Contains( "does not exist" ) ) );
		Assert.IsTrue( validation.Issues.Any( issue => issue.Message.Contains( "overlaps" ) ) );
	}

	[TestMethod]
	public void TimelineValidation_RejectsUnknownLoopEndMode()
	{
		var audioEvent = CreateEvent();
		audioEvent.LoopRegions.Add( new LoopRegion
		{
			Start = new TimelineTime( 0 ),
			End = new TimelineTime( 1 ),
			EndMode = (LoopEndMode)999
		} );

		var validation = AudioEventValidator.Validate( audioEvent );

		Assert.IsFalse( validation.IsValid );
		Assert.IsTrue( validation.Issues.Any( issue => issue.Path.EndsWith( "/EndMode" ) ) );
	}

	[TestMethod]
	public void SoundPointParameters_BuildFromEventDefaults()
	{
		var audioEvent = CreateEvent();
		var intensity = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f,
			DefaultValue = 0.35f
		};
		var station = new AudioParameter
		{
			Name = "Station",
			Type = AudioParameterType.Labeled,
			Labels = new() { "news", "music" },
			DefaultLabel = "music"
		};
		audioEvent.Parameters.AddRange( [intensity, station] );

		var values = Smod.SoundPointComponent.SynchronizeParameters( audioEvent, null );

		Assert.AreEqual( 2, values.Count );
		Assert.AreSame( intensity, values[0].Parameter );
		Assert.AreEqual( 0.35f, values[0].NumericValue );
		Assert.AreSame( station, values[1].Parameter );
		Assert.AreEqual( "music", values[1].LabelValue );
	}

	[TestMethod]
	public void SoundPointParameters_PreserveCompatibleNamesAndNormalizeValues()
	{
		var previousNumber = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 10f
		};
		var previousLabel = new AudioParameter
		{
			Name = "Station",
			Type = AudioParameterType.Labeled,
			Labels = new() { "news", "music" }
		};
		var existing = new List<SoundPointParameterValue>
		{
			new() { Parameter = previousNumber, NumericValue = 8f },
			new() { Parameter = previousLabel, LabelValue = "MUSIC" }
		};
		var audioEvent = CreateEvent();
		var replacementNumber = new AudioParameter
		{
			Name = "intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 5f
		};
		var replacementLabel = new AudioParameter
		{
			Name = "station",
			Type = AudioParameterType.Labeled,
			Labels = new() { "news", "music" },
			DefaultLabel = "news"
		};
		audioEvent.Parameters.AddRange( [replacementNumber, replacementLabel] );

		var values = Smod.SoundPointComponent.SynchronizeParameters( audioEvent, existing );

		Assert.AreEqual( 5f, values[0].NumericValue );
		Assert.AreEqual( "music", values[1].LabelValue );
		Assert.AreSame( replacementNumber, values[0].Parameter );
		Assert.AreSame( replacementLabel, values[1].Parameter );
	}

	[TestMethod]
	public void SoundPointParameters_ResetChangedTypesAndDiscardRemovedRows()
	{
		var oldParameter = new AudioParameter
		{
			Name = "Mode",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 10f
		};
		var removedParameter = new AudioParameter { Name = "Removed" };
		var existing = new List<SoundPointParameterValue>
		{
			new() { Parameter = oldParameter, NumericValue = 7f },
			new() { Parameter = removedParameter, NumericValue = 1f }
		};
		var replacement = new AudioParameter
		{
			Name = "Mode",
			Type = AudioParameterType.Discrete,
			Minimum = 0f,
			Maximum = 4f,
			DefaultValue = 2f
		};
		var audioEvent = CreateEvent();
		audioEvent.Parameters.Add( replacement );

		var values = Smod.SoundPointComponent.SynchronizeParameters( audioEvent, existing );

		Assert.AreEqual( 1, values.Count );
		Assert.AreSame( replacement, values[0].Parameter );
		Assert.AreEqual( 2f, values[0].NumericValue );
	}

	[TestMethod]
	public void SoundPointParameterValue_AppliesNumericAndLabeledChangesLive()
	{
		var audioEvent = CreateEvent();
		var intensity = new AudioParameter
		{
			Name = "Intensity",
			Type = AudioParameterType.Continuous,
			Minimum = 0f,
			Maximum = 1f
		};
		var station = new AudioParameter
		{
			Name = "Station",
			Type = AudioParameterType.Labeled,
			Labels = new() { "news", "music" },
			DefaultLabel = "news"
		};
		audioEvent.Parameters.AddRange( [intensity, station] );
		audioEvent.MasterTrack.Instruments[0].Automations.Add( new AudioAutomation
		{
			Parameter = intensity,
			Target = AudioAutomationTarget.Volume,
			Points = new()
			{
				new AudioAutomationPoint { Input = 0f, Output = 0.5f },
				new AudioAutomationPoint { Input = 1f, Output = 1.5f }
			}
		} );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		instance.Start();
		var numeric = new SoundPointParameterValue { Parameter = intensity, NumericValue = 1f };
		var labeled = new SoundPointParameterValue { Parameter = station, LabelValue = "music" };

		numeric.ApplyTo( instance );
		labeled.ApplyTo( instance );

		Assert.AreEqual( 1.5f, _backend.Voices[0].Volume, 0.0001f );
	}

	[TestMethod]
	public void SoundPointParameterValue_RejectsInvalidAuthoredRuntimeValues()
	{
		var audioEvent = CreateEvent();
		var station = new AudioParameter
		{
			Name = "Station",
			Type = AudioParameterType.Labeled,
			Labels = new() { "news" },
			DefaultLabel = "news"
		};
		audioEvent.Parameters.Add( station );
		var instance = AudioSystem.GetEvent( audioEvent ).CreateInstance();
		var value = new SoundPointParameterValue { Parameter = station, LabelValue = "missing" };

		Assert.ThrowsExactly<ArgumentOutOfRangeException>( () => value.ApplyTo( instance ) );
	}

	private static AudioParameter AddLabeledState( AudioEvent audioEvent )
	{
		var parameter = new AudioParameter
		{
			Name = "MusicState",
			Type = AudioParameterType.Labeled,
			Labels = new() { "exploration", "combat" },
			DefaultLabel = "exploration"
		};
		audioEvent.Parameters.Add( parameter );
		return parameter;
	}

	private static ParameterConditionSet LabelCondition( AudioParameter parameter, string label )
	{
		return new ParameterConditionSet
		{
			Conditions = new()
			{
				new TimelineParameterCondition
				{
					Parameter = parameter,
					Comparison = AudioParameterComparison.Equal,
					Label = label
				}
			}
		};
	}

	private static AudioEvent CreateEvent( SoundEventResource sound = null )
	{
		return new AudioEvent
		{
			MasterTrack = new AudioTrack
			{
				Name = "Master",
				Instruments = new()
				{
					new AudioInstrument
					{
						Sound = sound ?? CreateSoundEvent()
					}
				}
			}
		};
	}

	private static AudioEvent CreateLabeledEvent( SoundEventResource hit, SoundEventResource fall )
	{
		var actionParameter = new AudioParameter
		{
			Name = "Action",
			Type = AudioParameterType.Labeled,
			Labels = new() { "hit", "fall" },
			DefaultLabel = "hit"
		};
		return new AudioEvent
		{
			Parameters = new() { actionParameter },
			MasterTrack = new AudioTrack
			{
				Name = "Master",
				Instruments = new()
				{
					new AudioInstrument
					{
						Sound = hit,
						Conditions = new() { new AudioParameterCondition { Parameter = actionParameter, Label = "hit" } }
					},
					new AudioInstrument
					{
						Sound = fall,
						Conditions = new() { new AudioParameterCondition { Parameter = actionParameter, Label = "fall" } }
					}
				}
			}
		};
	}

	private static SoundEventResource CreateSoundEvent()
	{
		return new SoundEventResource
		{
			Tracks = new()
			{
				new SoundEventTrack
				{
					Sound = (SoundFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject( typeof( SoundFile ) )
				}
			}
		};
	}

	private sealed class FakeAudioBackend : IAudioPlaybackBackend
	{
		public List<FakeAudioVoice> Voices { get; } = new();
		public SoundFile NextSelectedSound { get; set; }

		public IAudioVoice Play( SoundEvent sound, Audio3DAttributes attributes, bool listenLocal )
		{
			var voice = new FakeAudioVoice
			{
				Sound = sound,
				SelectedSound = NextSelectedSound,
				Position = attributes.Position,
				ListenLocal = listenLocal
			};
			Voices.Add( voice );
			return voice;
		}
	}

	private sealed class FakeAudioVoice : IAudioVoice
	{
		private float _time;

		public SoundEvent Sound { get; set; }
		public bool IsPlaying { get; set; } = true;
		public SoundFile SelectedSound { get; set; }
		public bool Paused { get; set; }
		public float Time
		{
			get => _time;
			set
			{
				_time = value;
				TimeAssignments++;
			}
		}
		public int TimeAssignments { get; private set; }
		public float Volume { get; set; } = 1f;
		public float Pitch { get; set; } = 1f;
		public Vector3 Position { get; set; }
		public bool ListenLocal { get; set; }
		public float StopFadeTime { get; private set; }

		public void Stop( float fadeTime )
		{
			StopFadeTime = fadeTime;
			if ( fadeTime <= 0f )
				IsPlaying = false;
		}
	}

	private sealed class FakeAudioTimelineScheduler : IAudioTimelineScheduler
	{
		private readonly List<ScheduledDelay> _delays = new();

		public float Now { get; private set; }

		public Task DelaySeconds( float seconds, CancellationToken cancellationToken )
		{
			var completion = new TaskCompletionSource();
			var registration = cancellationToken.Register( () => completion.TrySetCanceled( cancellationToken ) );
			_delays.Add( new ScheduledDelay( Now + seconds, completion, registration ) );
			return completion.Task;
		}

		public void Advance( float seconds )
		{
			Now += seconds;
			foreach ( var delay in _delays.Where( delay => delay.Deadline <= Now ).ToArray() )
			{
				delay.Registration.Dispose();
				delay.Completion.TrySetResult();
				_delays.Remove( delay );
			}
		}

		private sealed record ScheduledDelay( float Deadline, TaskCompletionSource Completion, CancellationTokenRegistration Registration );
	}
}

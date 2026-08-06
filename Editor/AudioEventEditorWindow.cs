using Sandbox.Helpers;
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Smod;
using Smod.Editor;

namespace Editor;

[EditorForAssetType( "audevt" )]
public sealed class AudioEventEditorWindow : DockWindow, IAssetEditor, AssetSystem.IEventListener
{
	private readonly UndoSystem _undo = new();
	private readonly Dictionary<GameResource, JsonObject> _savedStates = new( ReferenceEqualityComparer.Instance );
	private readonly Dictionary<GameResource, JsonObject> _lastStates = new( ReferenceEqualityComparer.Instance );
	private readonly HashSet<GameResource> _dirtyResources = new( ReferenceEqualityComparer.Instance );
	private readonly HashSet<AudioTrack> _mutedTracks = new( ReferenceEqualityComparer.Instance );
	private readonly HashSet<AudioTrack> _soloTracks = new( ReferenceEqualityComparer.Instance );

	private Asset _asset;
	private AudioEvent _event;
	private AudioEventInstance _preview;
	private AudioEventWorkspaceWidget _workspace;
	private AudioSoundLibraryWidget _soundLibrary;
	private AudioCloudSoundLibraryWidget _cloudSoundLibrary;
	private AudioEventInspectorWidget _inspector;
	private AudioEventParameterWidget _parameters;
	private AudioEventIssuesWidget _issues;
	private ToolBar _toolbar;
	private Option _saveOption;
	private Option _undoOption;
	private Option _redoOption;
	private Option _playOption;
	private Option _stopOption;
	private Option _repeatOption;
	private Option _followOption;
	private Label _timecode;
	private object _selection;
	private bool _closingAfterPrompt;
	private bool _saving;
	private bool _repeatPreview;
	private bool _followPreview = true;

	public bool CanOpenMultipleAssets => true;
	internal AudioEvent Event => _event;
	internal object Selection => _selection;
	internal float PreviewPosition => _preview?.TimelinePosition ?? _workspace?.TimelinePosition ?? 0f;

	public AudioEventEditorWindow()
	{
		DeleteOnClose = true;
		Size = new Vector2( 1440f, 860f );
		MinimumSize = new Vector2( 1050f, 620f );
		StateCookie = "Smod.AudioEventEditor";
		SetWindowIcon( "graphic_eq" );

		_undo.Initialize();
		_undo.OnUndo += _ => UpdateUndoState();
		_undo.OnRedo += _ => UpdateUndoState();

		CreateMenuBar();
		CreateToolbar();
		CreateDocks();
		RestoreFromStateCookie();
		Show();
	}

	public void AssetOpen( Asset asset )
	{
		if ( _asset is not null )
			return;
		if ( asset is null )
			return;

		var audioEvent = asset.LoadResource<AudioEvent>();
		if ( audioEvent is null )
			throw new InvalidOperationException( $"Could not load AudioEvent '{asset.Path}'." );

		_asset = asset;
		_event = audioEvent;
		RegisterResource( audioEvent );
		_selection = audioEvent;
		ReloadUi();
		UpdateWindowTitle();
		Raise();
	}

	public void SelectMember( string memberName )
	{
		SelectObject( _event );
	}

	internal float LoadTimelineZoom()
	{
		if ( _asset is null )
			return 0f;

		return EditorCookie.Get( $"{StateCookie}.TimelineZoom.{_asset.Path}", 0f );
	}

	internal void RememberTimelineZoom( float pixelsPerSecond )
	{
		if ( _asset is null )
			return;

		EditorCookie.Set( $"{StateCookie}.TimelineZoom.{_asset.Path}", pixelsPerSecond );
	}

	protected override void BuildDefaultLayout()
	{
		var workspace = DockManager.OpenDock( "Timeline", DockArea.Center );
		var soundFiles = DockManager.OpenDock( "Sound Files", DockArea.Left, workspace );
		DockManager.OpenDock( "Cloud Sounds", DockArea.Bottom, soundFiles );
		DockManager.OpenDock( "Inspector", DockArea.Right, workspace );
		var parameters = DockManager.OpenDock( "Parameters", DockArea.Bottom, workspace );
		DockManager.OpenDock( "Validation", DockArea.Bottom, parameters );
		DockManager.SetSplitterProportions( soundFiles, 0.16f, 0.84f );
		DockManager.SetSplitterProportions( workspace, 0.78f, 0.22f );
		DockManager.SetSplitterProportions( parameters, 0.82f, 0.18f );
	}

	private void CreateDocks()
	{
		_workspace = new AudioEventWorkspaceWidget( this );
		_soundLibrary = new AudioSoundLibraryWidget( this );
		_cloudSoundLibrary = new AudioCloudSoundLibraryWidget( this );
		_inspector = new AudioEventInspectorWidget( this );
		_parameters = new AudioEventParameterWidget( this );
		_issues = new AudioEventIssuesWidget( this );

		DockManager.AddDock( "Timeline", "timeline", _workspace, DockArea.Center );
		DockManager.AddDock( "Sound Files", "audio_file", _soundLibrary, DockArea.Left );
		DockManager.AddDock( "Cloud Sounds", "audio_file", _cloudSoundLibrary, DockArea.Left );
		DockManager.AddDock( "Inspector", "edit", _inspector, DockArea.Right );
		DockManager.AddDock( "Parameters", "tune", _parameters, DockArea.Bottom );
		DockManager.AddDock( "Validation", "rule", _issues, DockArea.Bottom );
	}

	private void CreateMenuBar()
	{
		MenuBar.Clear();
		var view = MenuBar.AddMenu( "View" );
		view.AboutToShow += () => CreateDynamicViewMenu( view );
	}

	private void CreateToolbar()
	{
		if ( _toolbar is not null )
		{
			RemoveToolBar( _toolbar );
			_toolbar.Destroy();
		}
		_toolbar = new ToolBar( this, "AudioEventEditor.Toolbar" );
		AddToolBar( _toolbar, ToolbarPosition.Top );
		_toolbar.SetIconSize( 18 );

		_saveOption = _toolbar.AddOption( "Save", "save", () => Save() );
		_undoOption = _toolbar.AddOption( "Undo", "undo", () => Undo() );
		_redoOption = _toolbar.AddOption( "Redo", "redo", () => Redo() );
		_toolbar.AddSeparator();
		_playOption = _toolbar.AddOption( "Play", "play_arrow", TogglePreview );
		_stopOption = _toolbar.AddOption( "Stop", "stop", StopPreview );
		_toolbar.AddOption( "To Start", "skip_previous", () => SetPreviewPosition( 0f ) );
		_repeatOption = _toolbar.AddOption( "Repeat Off", "repeat", ToggleRepeatPreview );
		_followOption = _toolbar.AddOption( "Follow On", "my_location", ToggleFollowPreview );
		_toolbar.AddSeparator();
		_toolbar.AddOption( "Add Track", "add", AddTrack );
		_toolbar.AddOption( "Add Parameter", "tune", AddParameter );
		_toolbar.AddOption( "New Parameter", "note_add", CreateParameter );
		_toolbar.AddOption( "Add Destination", "flag", () => _workspace?.AddDestinationAtPlayhead() );
		_toolbar.AddOption( "Add Transition", "alt_route", () => _workspace?.AddTransitionAtPlayhead() );
		_toolbar.AddOption( "Add Transition Region", "compare_arrows", () => _workspace?.AddTransitionRegionAtPlayhead() );
		_toolbar.AddOption( "Add Loop", "repeat", () => _workspace?.AddLoopAtPlayhead() );
		_toolbar.AddSeparator();
		_toolbar.AddOption( "Zoom In", "zoom_in", () => _workspace?.Zoom( 1.2f ) );
		_toolbar.AddOption( "Zoom Out", "zoom_out", () => _workspace?.Zoom( 1f / 1.2f ) );
		_toolbar.AddOption( "Frame Event", "fit_screen", () => _workspace?.FrameEvent() );
		_toolbar.AddOption( "Event Settings", "tune", () => SelectObject( _event ) );

		_timecode = _toolbar.AddWidget( new Label( "00:00.000 / 00:00.000" ) );
		_timecode.MinimumWidth = 190f;
		_timecode.Alignment = TextFlag.RightCenter;
		UpdateUndoState();
	}

	[Shortcut( "editor.save", "CTRL+S", ShortcutType.Window )]
	private bool Save()
	{
		if ( _event is null )
			return false;

		var success = true;
		_saving = true;
		try
		{
			foreach ( var resource in _dirtyResources.ToArray() )
			{
				var asset = ResolveAsset( resource );
				if ( asset is null || !asset.SaveToDisk( resource ) )
				{
					Log.Warning( $"Failed to save audio resource '{resource.ResourcePath}'." );
					success = false;
					continue;
				}

				var state = resource.Serialize();
				_savedStates[resource] = state;
				_lastStates[resource] = state;
				_dirtyResources.Remove( resource );
			}
		}
		finally
		{
			_saving = false;
		}

		UpdateWindowTitle();
		UpdateUndoState();
		return success;
	}

	[Shortcut( "editor.undo", "CTRL+Z", ShortcutType.Window )]
	private bool Undo()
	{
		var result = _undo.Undo();
		UpdateUndoState();
		return result;
	}

	[Shortcut( "editor.redo", "CTRL+Y", ShortcutType.Window )]
	private bool Redo()
	{
		var result = _undo.Redo();
		UpdateUndoState();
		return result;
	}

	[Shortcut( "audio.preview", "SPACE", ShortcutType.Window )]
	private void TogglePreview()
	{
		if ( _event is null )
			return;

		var validation = AudioEventValidator.Validate( _event );
		if ( !validation.IsValid )
		{
			_issues.SetValidation( validation );
			return;
		}

		var startingPlayback = _preview is null
			|| !_preview.IsValid
			|| _preview.PlaybackState == AudioPlaybackState.Stopped
			|| _preview.Paused;
		if ( startingPlayback )
			AudioPreviewPlaybackService.Instance.Stop();

		if ( _preview is null || !_preview.IsValid )
		{
			_preview = AudioSystem.GetEvent( _event ).CreateInstance();
			_parameters.ApplyToPreview( _preview );
			ApplyTrackAuditionToPreview();
		}

		if ( _preview.PlaybackState == AudioPlaybackState.Stopped )
		{
			var startPosition = AudioPreviewTransportMath.ResolveStartPosition(
				_workspace.TimelinePosition,
				_workspace.AudioContentEnd );
			_workspace.TimelinePosition = startPosition;
			_preview.TimelinePosition = startPosition;
			_preview.Start();
		}
		else
		{
			_preview.Paused = !_preview.Paused;
		}

		UpdateTransport();
		UpdatePreviewVisualization();
	}

	private void StopPreview()
	{
		DisposePreview( true );
	}

	internal void StopTimelineForFilePreview()
	{
		DisposePreview( false );
	}

	private void ToggleRepeatPreview()
	{
		_repeatPreview = !_repeatPreview;
		UpdateTransport();
	}

	private void ToggleFollowPreview()
	{
		_followPreview = !_followPreview;
		UpdateTransport();
	}

	private void DisposePreview( bool resetPosition )
	{
		if ( _preview?.IsValid ?? false )
		{
			_preview.Stop( AudioStopMode.Immediate );
			_preview.Release();
		}
		_preview = null;
		if ( resetPosition )
			_workspace.TimelinePosition = 0f;
		UpdateTransport();
		UpdatePreviewVisualization();
	}

	internal void SetPreviewPosition( float position )
	{
		position = MathF.Max( 0f, position );
		_workspace.TimelinePosition = position;
		if ( _preview?.IsValid ?? false )
			_preview.TimelinePosition = position;
		UpdateTransport();
	}

	internal void SetPreviewParameter( AudioParameter parameter, float value, string label )
	{
		if ( !(_preview?.IsValid ?? false) )
			return;
		if ( parameter.Type == AudioParameterType.Labeled )
			_preview.SetParameterByNameWithLabel( parameter.Name, label );
		else
			_preview.SetParameterByName( parameter.Name, value );
	}

	internal void PreviewAuthoredChange()
	{
		if ( !(_preview?.IsValid ?? false) || _preview.PlaybackState == AudioPlaybackState.Stopped )
			return;
		_preview.RefreshAuthoredPropertiesForAudition();
	}

	[EditorEvent.Frame]
	private void OnFrame()
	{
		if ( _preview?.IsValid ?? false )
		{
			var instrumentStates = _preview.InstrumentPlaybackStates;
			_workspace.TimelinePosition = _preview.TimelinePosition;
			if ( _followPreview && _preview.PlaybackState != AudioPlaybackState.Stopped )
				_workspace.FollowTimelinePosition();
			if ( AudioPreviewTransportMath.ShouldRepeatPreview(
				_repeatPreview,
				_preview.Paused,
				_workspace.TimelinePosition,
				instrumentStates ) )
			{
				DisposePreview( false );
				_workspace.TimelinePosition = 0f;
				TogglePreview();
			}
			else if ( _preview.PlaybackState == AudioPlaybackState.Stopped )
			{
				_preview.Release();
				_preview = null;
			}
		}
		UpdateTransport();
		UpdatePreviewVisualization();
	}

	private void UpdatePreviewVisualization()
	{
		var states = (_preview?.IsValid ?? false)
			? _preview.InstrumentPlaybackStates
			: Array.Empty<AudioInstrumentPlaybackState>();
		_workspace?.UpdatePlaybackVisualization( states );
	}

	private void UpdateTransport()
	{
		var playing = (_preview?.IsValid ?? false) && _preview.PlaybackState != AudioPlaybackState.Stopped;
		var paused = playing && _preview.Paused;
		_playOption.Icon = playing && !paused ? "pause" : "play_arrow";
		_playOption.Text = playing && !paused ? "Pause" : "Play";
		_stopOption.Enabled = playing || _workspace.TimelinePosition > 0f;
		if ( _repeatOption is not null )
		{
			_repeatOption.Checked = _repeatPreview;
			_repeatOption.Text = _repeatPreview ? "Repeat On" : "Repeat Off";
		}
		if ( _followOption is not null )
		{
			_followOption.Checked = _followPreview;
			_followOption.Text = _followPreview ? "Follow On" : "Follow Off";
		}
		var current = TimeSpan.FromSeconds( _workspace.TimelinePosition );
		var duration = TimeSpan.FromSeconds( _workspace.Duration );
		_timecode.Text = $"{current:mm\\:ss\\.fff} / {duration:mm\\:ss\\.fff}";
	}

	internal void SelectObject( object value )
	{
		_selection = value ?? _event;
		_inspector.SetSelection( _selection );
		_workspace.SetSelection( _selection );
	}

	internal bool IsTrackMuted( AudioTrack track ) => track is not null && _mutedTracks.Contains( track );
	internal bool IsTrackSoloed( AudioTrack track ) => track is not null && _soloTracks.Contains( track );
	internal bool HasTrackAuditionOverrides => _mutedTracks.Count > 0 || _soloTracks.Count > 0;

	internal bool IsTrackAudible( AudioTrack track )
	{
		if ( track is null || ReferenceEquals( track, _event?.MasterTrack ) )
			return true;
		return !_mutedTracks.Contains( track ) && (_soloTracks.Count == 0 || _soloTracks.Contains( track ));
	}

	internal void ToggleTrackMute( AudioTrack track )
	{
		if ( track is null || ReferenceEquals( track, _event?.MasterTrack ) )
			return;
		if ( !_mutedTracks.Remove( track ) )
			_mutedTracks.Add( track );
		TrackAuditionChanged();
	}

	internal void ToggleTrackSolo( AudioTrack track )
	{
		if ( track is null || ReferenceEquals( track, _event?.MasterTrack ) )
			return;
		if ( !_soloTracks.Remove( track ) )
			_soloTracks.Add( track );
		TrackAuditionChanged();
	}

	internal void ClearTrackAudition()
	{
		if ( !HasTrackAuditionOverrides )
			return;
		_mutedTracks.Clear();
		_soloTracks.Clear();
		TrackAuditionChanged();
	}

	private void TrackAuditionChanged()
	{
		ApplyTrackAuditionToPreview();
		_workspace?.UpdateTrackAudition();
	}

	private void ApplyTrackAuditionToPreview()
	{
		if ( !(_preview?.IsValid ?? false) )
			return;
		foreach ( var track in _event?.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
			_preview.SetTrackMutedForAudition( track, !IsTrackAudible( track ) );
	}

	internal void AddTrack()
	{
		if ( _event is null )
			return;
		var index = (_event.Tracks?.Count ?? 0) + 1;
		ExecuteResourceAction( "Add Audio Track", _event, () =>
		{
			_event.Tracks ??= new();
			_event.Tracks.Add( new AudioTrack { Name = $"Track {index}" } );
		} );
	}

	internal void RenameTrack( AudioTrack track )
	{
		if ( track is null || ReferenceEquals( track, _event?.MasterTrack ) )
			return;
		Dialog.AskString(
			name =>
			{
				var trimmed = name?.Trim();
				if ( string.IsNullOrWhiteSpace( trimmed ) )
					return;
				MutateEvent( "Rename Audio Track", () => track.Name = trimmed );
			},
			"Track name:",
			okay: "Rename",
			title: $"Rename {track.Name}",
			minLength: 1 );
	}

	internal void ResetTrackControl( AudioTrack track, AudioAutomationTarget target )
	{
		if ( track is null )
			return;
		MutateEvent(
			target == AudioAutomationTarget.Volume ? "Reset Track Volume" : "Reset Track Pitch",
			() =>
			{
				if ( target == AudioAutomationTarget.Volume )
					track.Volume = 1f;
				else
					track.Pitch = 1f;
			} );
	}

	internal void RemoveTrack( AudioTrack track )
	{
		if ( track is null || _event.Tracks is null || !_event.Tracks.Contains( track ) )
			return;
		ExecuteResourceAction( "Remove Audio Track", _event, () => _event.Tracks.Remove( track ) );
	}

	internal void MoveTrack( AudioTrack track, int targetIndex )
	{
		if ( track is null || _event?.Tracks is null )
			return;
		var sourceIndex = _event.Tracks.IndexOf( track );
		if ( sourceIndex < 0 )
			return;
		targetIndex = Math.Clamp( targetIndex, 0, _event.Tracks.Count - 1 );
		if ( sourceIndex == targetIndex )
			return;

		ExecuteResourceAction( "Reorder Audio Track", _event, () =>
		{
			_event.Tracks.RemoveAt( sourceIndex );
			_event.Tracks.Insert( targetIndex, track );
		} );
	}

	internal void AddInstrument( AudioTrack track, SoundFile sound, float startTime )
	{
		if ( track is null || sound is null )
			return;
		AddInstruments( track, new[] { sound }, startTime );
	}

	internal void AddSoundFromLibrary( Asset asset )
	{
		var sound = asset is null ? null : SoundFile.Load( asset.Path );
		if ( sound is null || _event is null )
			return;

		AddInstrument( ResolveLibraryInsertionTrack(), sound, PreviewPosition );
	}

	internal async Task AddExternalSoundFromLibraryAsync( string sourcePath )
	{
		try
		{
			var asset = await ExternalAudioSourceCatalog.ImportAsync( sourcePath );
			AddSoundFromLibrary( asset );
		}
		catch ( Exception exception )
		{
			Log.Warning( exception, $"Could not import external sound '{sourcePath}'." );
		}
	}

	internal void OpenClipSlicer( Asset asset )
	{
		if ( asset?.AssetType != AssetType.SoundFile || _event is null )
			return;

		var sound = SoundFile.Load( asset.Path );
		if ( !sound.IsValid() )
			return;

		var targetTrack = ResolveLibraryInsertionTrack();
		var startTime = PreviewPosition;
		new AudioClipSlicerWindow( this, asset, ( slices ) => AddSlicedInstrument( targetTrack, sound, slices, startTime ) );
	}

	private AudioTrack ResolveLibraryInsertionTrack()
	{
		var track = _selection as AudioTrack;
		if ( track is null && _selection is AudioInstrument instrument )
		{
			track = EnumerateEventTracks()
				.FirstOrDefault( candidate => candidate.Instruments?.Contains( instrument ) ?? false );
		}
		return track
			?? _event.Tracks?.FirstOrDefault( candidate => candidate is not null )
			?? _event.MasterTrack;
	}

	private void AddSlicedInstrument( AudioTrack track, SoundFile sound, IReadOnlyList<AudioClipSlice> slices, float startTime )
	{
		if ( track is null || !sound.IsValid() || slices is null || slices.Count == 0 )
			return;
		if ( !ReferenceEquals( track, _event?.MasterTrack ) && !(_event?.Tracks?.Contains( track ) ?? false) )
			return;

		var validSlices = slices
			.Where( slice => float.IsFinite( slice.Offset ) && float.IsFinite( slice.Length )
				&& slice.Offset >= 0f && slice.Length > 0f )
			.OrderBy( slice => slice.Offset )
			.ToArray();
		if ( validSlices.Length == 0 )
			return;

		ExecuteResourceAction( $"Add Sliced Multi-Instrument ({validSlices.Length} variations)", _event, () =>
		{
			var soundEvent = new SoundEventResource
			{
				Tracks = validSlices.Select( slice => new SoundEventTrack
				{
					Sound = sound,
					Offset = slice.Offset,
					Length = slice.Length
				} ).ToList()
			};
			soundEvent.EmbeddedResource = new Sandbox.Resources.EmbeddedResource { ResourceCompiler = "embed" };
			track.Instruments ??= new();
			track.Instruments.Add( new AudioInstrument
			{
				Sound = soundEvent,
				StartTime = MathF.Max( 0f, startTime ).SnapToGrid( 0.01f )
			} );
		} );
	}

	internal void AddInstruments( AudioTrack track, IReadOnlyList<SoundFile> sounds, float startTime )
	{
		if ( track is null || sounds is null || sounds.Count == 0 )
			return;
		if ( !ReferenceEquals( track, _event?.MasterTrack ) && !(_event?.Tracks?.Contains( track ) ?? false) )
			return;
		var validSounds = sounds.Where( sound => sound.IsValid() ).ToArray();
		if ( validSounds.Length == 0 )
			return;
		ExecuteResourceAction( validSounds.Length == 1 ? "Add Audio Instrument" : "Add Audio Multi-Instrument", _event, () =>
		{
			track.Instruments ??= new();
			track.Instruments.Add( new AudioInstrument
			{
				Sound = CreateEmbeddedSoundEvent( validSounds ),
				StartTime = MathF.Max( 0f, startTime ).SnapToGrid( 0.01f )
			} );
		} );
	}
	internal void AddSoundsToInstrument( AudioInstrument instrument, IReadOnlyList<SoundFile> sounds )
	{
		if ( instrument?.Sound is null || sounds is null || !EnumerateInstruments().Contains( instrument ) )
			return;
		var validSounds = sounds.Where( sound => sound.IsValid() ).ToArray();
		if ( validSounds.Length == 0 )
			return;

		var soundEvent = instrument.Sound;
		GameResource owner = soundEvent.EmbeddedResource is not null ? _event : soundEvent;
		ExecuteResourceAction( validSounds.Length == 1 ? "Add Sound Variation" : "Add Sound Variations", owner, () =>
		{
			soundEvent.Tracks ??= new();
			soundEvent.Tracks.AddRange( validSounds.Select( sound => new SoundEventTrack { Sound = sound } ) );
		} );
	}

	internal static SoundEventResource CreateEmbeddedSoundEvent( IReadOnlyList<SoundFile> sounds )
	{
		return AudioSoundEventFactory.CreateEmbedded( sounds );
	}
internal void RemoveInstrument( AudioTrack track, AudioInstrument instrument )
	{
		if ( track is null || instrument is null || track.Instruments is null || !track.Instruments.Contains( instrument ) )
			return;
		ExecuteResourceAction( "Remove Audio Instrument", _event, () => track.Instruments.Remove( instrument ) );
	}

	internal void DuplicateInstrumentAfterSound( AudioTrack track, AudioInstrument instrument )
	{
		if ( track is null || instrument is null || track.Instruments is null || !track.Instruments.Contains( instrument ) )
			return;

		AudioInstrument duplicate = null;
		ExecuteResourceAction( "Duplicate Audio Instrument", _event, () =>
		{
			duplicate = new AudioInstrument
			{
				Sound = instrument.Sound,
				StartTime = instrument.StartTime + AudioTimelineView.EstimateDuration( instrument ),
				TrimStart = instrument.TrimStart,
				TrimEnd = instrument.TrimEnd,
				Conditions = instrument.Conditions?.Select( condition => new AudioParameterCondition
				{
					Parameter = condition.Parameter,
					Invert = condition.Invert,
					Minimum = condition.Minimum,
					Maximum = condition.Maximum,
					Label = condition.Label
				} ).ToList() ?? new(),
				Automations = instrument.Automations?.Select( automation => new AudioAutomation
				{
					Parameter = automation.Parameter,
					Target = automation.Target,
					Points = automation.Points?.Select( point => new AudioAutomationPoint
					{
						Input = point.Input,
						Output = point.Output
					} ).ToList() ?? new()
				} ).ToList() ?? new()
			};
			track.Instruments.Insert( track.Instruments.IndexOf( instrument ) + 1, duplicate );
		} );

		if ( duplicate is not null )
			SelectObject( duplicate );
	}

	internal void MoveInstrument( AudioTrack sourceTrack, AudioTrack targetTrack, AudioInstrument instrument, float startTime )
	{
		if ( sourceTrack?.Instruments is null || targetTrack is null || instrument is null || !sourceTrack.Instruments.Contains( instrument ) )
			return;
		var snapped = MathF.Max( 0f, startTime ).SnapToGrid( 0.01f );
		var changingTrack = !ReferenceEquals( sourceTrack, targetTrack );
		if ( !changingTrack && MathF.Abs( instrument.StartTime - snapped ) < 0.0001f )
			return;

		ExecuteResourceAction(
			changingTrack ? "Move Audio Instrument Between Tracks" : "Move Audio Instrument",
			_event,
			() =>
			{
				if ( changingTrack )
				{
					sourceTrack.Instruments.Remove( instrument );
					targetTrack.Instruments ??= new();
					targetTrack.Instruments.Add( instrument );
				}
				instrument.StartTime = snapped;
			},
			() => _workspace.RefreshInstrumentMove( sourceTrack, targetTrack, instrument ) );
		if ( (_preview?.IsValid ?? false) && _preview.PlaybackState != AudioPlaybackState.Stopped )
			_preview.TimelinePosition = _preview.TimelinePosition;
	}

	internal void AddCondition( AudioInstrument instrument, AudioParameter parameter )
	{
		if ( instrument is null || parameter is null )
			return;
		if ( instrument.Conditions?.Any( condition => ReferenceEquals( condition.Parameter, parameter ) ) ?? false )
			return;

		AudioParameterCondition condition = null;
		ExecuteResourceAction( "Add Audio Condition", _event, () =>
		{
			instrument.Conditions ??= new();
			condition = new AudioParameterCondition
			{
				Parameter = parameter,
				Minimum = parameter.DefaultValue,
				Maximum = parameter.DefaultValue,
				Label = parameter.Type == AudioParameterType.Labeled ? parameter.DefaultLabel : null
			};
			instrument.Conditions.Add( condition );
		} );

		if ( condition is not null )
			SelectObject( condition );
	}

	internal void AddCondition( AudioTrack track, AudioParameter parameter )
	{
		if ( track is null || parameter is null )
			return;
		if ( track.Conditions?.Any( condition => ReferenceEquals( condition.Parameter, parameter ) ) ?? false )
			return;

		AudioParameterCondition condition = null;
		ExecuteResourceAction( "Add Track Condition", _event, () =>
		{
			track.Conditions ??= new();
			condition = new AudioParameterCondition
			{
				Parameter = parameter,
				Minimum = parameter.DefaultValue,
				Maximum = parameter.DefaultValue,
				Label = parameter.Type == AudioParameterType.Labeled ? parameter.DefaultLabel : null
			};
			track.Conditions.Add( condition );
		} );

		if ( condition is not null )
			SelectObject( condition );
	}

	internal void RemoveCondition( AudioInstrument instrument, AudioParameterCondition condition )
	{
		if ( instrument?.Conditions is null || condition is null || !instrument.Conditions.Contains( condition ) )
			return;
		ExecuteResourceAction( "Remove Audio Condition", _event, () => instrument.Conditions.Remove( condition ) );
	}

	internal void RemoveCondition( AudioTrack track, AudioParameterCondition condition )
	{
		if ( track?.Conditions is null || condition is null || !track.Conditions.Contains( condition ) )
			return;
		ExecuteResourceAction( "Remove Track Condition", _event, () => track.Conditions.Remove( condition ) );
	}

	internal void AddParameter()
	{
		var assetType = AssetType.FromExtension( "audparam" );
		if ( assetType is null )
			return;
		var picker = AssetPicker.Create( this, assetType, new AssetPicker.PickerOptions
		{
			EnableMultiselect = true,
			EnableCloud = false
		} );
		picker.Window.Title = "Add Parameter Resources";
		picker.OnAssetPicked = assets => AddParameterResources( assets
			.Select( asset => asset?.LoadResource<AudioParameter>() )
			.Where( parameter => parameter is not null )
			.ToArray() );
		picker.Window.Show();
	}

	internal void CreateParameter()
	{
		Dialog.AskString(
			CreateParameterResource,
			"Parameter name:",
			okay: "Create",
			title: "Create Audio Parameter Resource",
			minLength: 1 );
	}

	private void CreateParameterResource( string name )
	{
		var trimmed = name?.Trim();
		if ( string.IsNullOrWhiteSpace( trimmed ) )
			return;
		if ( _event.Parameters?.Any( parameter => string.Equals( parameter?.Name, trimmed, StringComparison.OrdinalIgnoreCase ) ) ?? false )
		{
			Log.Warning( $"Audio event already contains a parameter named '{trimmed}'." );
			return;
		}

		var root = Project.Current?.GetRootPath();
		if ( string.IsNullOrWhiteSpace( root ) )
			return;
		var directory = Path.Combine( root, "Assets", "Audio", "Parameters" );
		Directory.CreateDirectory( directory );
		var slug = string.Concat( trimmed.ToLowerInvariant().Select( character => char.IsLetterOrDigit( character ) ? character : '_' ) ).Trim( '_' );
		if ( string.IsNullOrWhiteSpace( slug ) )
			slug = "parameter";
		var path = Path.Combine( directory, $"{slug}.audparam" );
		for ( var suffix = 2; File.Exists( path ); suffix++ )
			path = Path.Combine( directory, $"{slug}_{suffix}.audparam" );

		var asset = AssetSystem.CreateResource( "audparam", path );
		var created = new AudioParameter
		{
			Name = trimmed,
			Minimum = 0f,
			Maximum = 1f,
			DefaultValue = 0f
		};
		if ( asset is null || !asset.SaveToDisk( created ) )
		{
			Log.Warning( $"Could not create audio parameter resource '{path}'." );
			return;
		}
		AddParameterResources( new[] { asset.LoadResource<AudioParameter>() } );
	}

	private void AddParameterResources( IReadOnlyList<AudioParameter> parameters )
	{
		var additions = parameters
			.Where( parameter => parameter is not null
				&& !(_event.Parameters?.Contains( parameter ) ?? false)
				&& !(_event.Parameters?.Any( existing => string.Equals( existing?.Name, parameter.Name, StringComparison.OrdinalIgnoreCase ) ) ?? false) )
			.ToArray();
		if ( additions.Length == 0 )
			return;
		ExecuteResourceAction( additions.Length == 1 ? "Add Audio Parameter" : "Add Audio Parameters", _event, () =>
		{
			_event.Parameters ??= new();
			_event.Parameters.AddRange( additions );
		} );
		SelectObject( additions[0] );
	}

	internal void RenameParameter( AudioParameter parameter )
	{
		if ( parameter is null || !(_event.Parameters?.Contains( parameter ) ?? false) )
			return;
		Dialog.AskString(
			name =>
			{
				var trimmed = name?.Trim();
				if ( string.IsNullOrWhiteSpace( trimmed ) || string.Equals( trimmed, parameter.Name, StringComparison.Ordinal ) )
					return;
				if ( _event.Parameters.Any( candidate => !ReferenceEquals( candidate, parameter )
					&& string.Equals( candidate?.Name, trimmed, StringComparison.OrdinalIgnoreCase ) ) )
				{
					Log.Warning( $"Audio event already contains a parameter named '{trimmed}'." );
					return;
				}
				ExecuteResourceAction( "Rename Audio Parameter", parameter, () =>
				{
					parameter.Name = trimmed;
				} );
				SelectObject( parameter );
			},
			"Parameter name:",
			okay: "Rename",
			title: $"Rename {parameter.Name}",
			minLength: 1 );
	}

	internal void RemoveParameter( AudioParameter parameter )
	{
		if ( parameter is null || _event.Parameters is null || !_event.Parameters.Contains( parameter ) )
			return;
		ExecuteResourceAction( "Remove Audio Parameter", _event, () =>
		{
			foreach ( var track in EnumerateEventTracks() )
			{
				track.Conditions?.RemoveAll( condition => ReferenceEquals( condition?.Parameter, parameter ) );
				track.Automations?.RemoveAll( automation => ReferenceEquals( automation?.Parameter, parameter ) );
				foreach ( var instrument in track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
				{
					instrument.Conditions?.RemoveAll( condition => ReferenceEquals( condition?.Parameter, parameter ) );
					instrument.Automations?.RemoveAll( automation => ReferenceEquals( automation?.Parameter, parameter ) );
				}
			}
			_event.Parameters.Remove( parameter );
		} );
		if ( ReferenceEquals( _selection, parameter ) )
			SelectObject( _event );
	}

	internal void AddAutomation( AudioTrack track, AudioInstrument instrument, AudioParameter parameter, AudioAutomationTarget target )
	{
		if ( (instrument is null && track is null) || parameter is null || parameter.Type == AudioParameterType.Labeled )
			return;
		ExecuteResourceAction( "Add Audio Automation", _event, () =>
		{
			if ( instrument is not null )
				instrument.Automations ??= new();
			else
				track.Automations ??= new();
			var automations = instrument?.Automations ?? track.Automations;
			automations.Add( new AudioAutomation
			{
				Parameter = parameter,
				Target = target,
				Points = new()
				{
					new AudioAutomationPoint { Input = parameter.Minimum, Output = 1f },
					new AudioAutomationPoint { Input = parameter.Maximum, Output = 1f }
				}
			} );
		} );
	}

	internal void RemoveAutomation( AudioTrack track, AudioInstrument instrument, AudioAutomation automation )
	{
		var automations = instrument?.Automations ?? track?.Automations;
		if ( automations is null || automation is null || !automations.Contains( automation ) )
			return;
		ExecuteResourceAction( "Remove Audio Automation", _event, () => automations.Remove( automation ) );
	}

	internal void MutateEvent( string title, Action mutation )
	{
		ExecuteResourceAction( title, _event, mutation );
	}

	internal JsonObject CaptureEventState()
	{
		return _event.Serialize();
	}

	internal void CommitEventMutation( string title, JsonObject before, Action targetedRefresh = null )
	{
		var after = _event.Serialize();
		if ( StatesEqual( before, after ) )
			return;
		InsertUndo( title, _event, before, after );
		_lastStates[_event] = after;
		UpdateDirtyState( _event );
		if ( targetedRefresh is null )
			RefreshAfterMutation();
		else
			RefreshAfterTargetedMutation( targetedRefresh );
	}

	internal void RegisterReferencedResource( GameResource resource )
	{
		RegisterResource( resource );
	}

	internal void InspectorChanged( GameResource owner )
	{
		if ( owner is null )
			return;
		RegisterResource( owner );
		var before = _lastStates[owner];
		var after = owner.Serialize();
		if ( StatesEqual( before, after ) )
			return;

		InsertUndo( "Edit Audio Property", owner, before, after );
		_lastStates[owner] = after;
		UpdateDirtyState( owner );
		RefreshAfterMutation( owner is SoundEvent );
	}

	private void ExecuteResourceAction( string title, GameResource resource, Action mutation, Action targetedRefresh = null )
	{
		RegisterResource( resource );
		var before = resource.Serialize();
		mutation();
		var after = resource.Serialize();
		if ( StatesEqual( before, after ) )
			return;

		InsertUndo( title, resource, before, after );
		_lastStates[resource] = after;
		UpdateDirtyState( resource );
		if ( targetedRefresh is null )
			RefreshAfterMutation();
		else
			RefreshAfterTargetedMutation( targetedRefresh );
	}

	private void InsertUndo( string title, GameResource resource, JsonObject before, JsonObject after )
	{
		_undo.Insert( title,
			() => RestoreState( resource, before ),
			() => RestoreState( resource, after ) );
		UpdateUndoState();
	}

	private void RestoreState( GameResource resource, JsonObject state )
	{
		resource.Deserialize( state );
		_lastStates[resource] = resource.Serialize();
		UpdateDirtyState( resource );
		_selection = resource == _event ? _event : resource;
		RefreshAfterMutation();
	}

	private void RegisterResource( GameResource resource )
	{
		if ( resource is null || _savedStates.ContainsKey( resource ) )
			return;
		var state = resource.Serialize();
		_savedStates[resource] = state;
		_lastStates[resource] = state;
	}

	private void UpdateDirtyState( GameResource resource )
	{
		var current = resource.Serialize();
		_lastStates[resource] = current;
		if ( _savedStates.TryGetValue( resource, out var saved ) && StatesEqual( current, saved ) )
			_dirtyResources.Remove( resource );
		else
			_dirtyResources.Add( resource );
		UpdateWindowTitle();
	}

	private static bool StatesEqual( JsonObject left, JsonObject right )
	{
		return string.Equals( left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal );
	}

	private void RefreshAfterMutation( bool restartPreview = false )
	{
		var position = PreviewPosition;
		var wasPlaying = (_preview?.IsValid ?? false) && _preview.PlaybackState != AudioPlaybackState.Stopped;
		var wasPaused = wasPlaying && _preview.Paused;
		if ( restartPreview )
			DisposePreview( false );
		ReloadUi();
		_workspace.TimelinePosition = MathF.Min( position, _workspace.Duration );

		if ( !AudioEventValidator.Validate( _event ).IsValid )
		{
			DisposePreview( false );
			return;
		}

		if ( restartPreview && wasPlaying )
		{
			TogglePreview();
			if ( wasPaused && (_preview?.IsValid ?? false) )
				_preview.Paused = true;
		}
		else if ( wasPlaying )
		{
			_preview.RefreshAuthoredPropertiesForAudition();
		}
	}

	private void RefreshAfterTargetedMutation( Action targetedRefresh )
	{
		targetedRefresh();
		if ( (_preview?.IsValid ?? false) && _preview.PlaybackState != AudioPlaybackState.Stopped )
			_preview.RefreshAuthoredPropertiesForAudition();
		_inspector.SetSelection( _selection ?? _event );
		var validation = AudioEventValidator.Validate( _event );
		_issues.SetValidation( validation );
		_playOption.Enabled = validation.IsValid;
		UpdateTransport();
	}

	private void ReloadUi()
	{
		PruneTransientEditorState();
		ApplyTrackAuditionToPreview();
		_workspace.SetEvent( _event );
		_parameters.SetEvent( _event );
		_inspector.SetSelection( _selection ?? _event );
		var validation = _event is null ? null : AudioEventValidator.Validate( _event );
		_issues.SetValidation( validation );
		_playOption.Enabled = validation?.IsValid ?? false;
		UpdateTransport();
	}

	private void PruneTransientEditorState()
	{
		var tracks = new HashSet<AudioTrack>(
			_event?.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>(),
			ReferenceEqualityComparer.Instance );
		_mutedTracks.RemoveWhere( track => !tracks.Contains( track ) );
		_soloTracks.RemoveWhere( track => !tracks.Contains( track ) );
	}

	private void UpdateUndoState()
	{
		if ( _undoOption is null )
			return;
		_undoOption.Enabled = _undo.Back.Count > 0;
		_redoOption.Enabled = _undo.Forward.Count > 0;
		_saveOption.Enabled = _dirtyResources.Count > 0;
	}

	private void UpdateWindowTitle()
	{
		Title = $"Audio Event Editor - {(_asset?.Name ?? "untitled")}{(_dirtyResources.Count > 0 ? "*" : "")}";
		UpdateUndoState();
	}

	private Asset ResolveAsset( GameResource resource )
	{
		if ( ReferenceEquals( resource, _event ) )
			return _asset;
		return string.IsNullOrWhiteSpace( resource.ResourcePath ) ? null : AssetSystem.FindByPath( resource.ResourcePath );
	}

	internal void OpenSoundAsset( SoundEventResource sound )
	{
		if ( sound is null )
			return;
		var asset = ResolveAsset( sound );
		if ( asset is not null )
			asset.OpenInEditor();
	}

	internal void InspectSoundAsset( SoundEventResource sound )
	{
		if ( sound is null )
			return;
		var asset = ResolveAsset( sound );
		if ( asset is not null )
			EditorUtility.InspectorObject = asset;
	}

	internal void OpenSoundAsset( SoundFile sound )
	{
		AssetSystem.FindByPath( sound?.ResourcePath )?.OpenInEditor();
	}

	internal void InspectSoundAsset( SoundFile sound )
	{
		var asset = AssetSystem.FindByPath( sound?.ResourcePath );
		if ( asset is not null )
			EditorUtility.InspectorObject = asset;
	}

	internal void PlayEventFrom( float timelinePosition )
	{
		DisposePreview( false );
		_workspace.TimelinePosition = MathF.Max( 0f, timelinePosition );
		TogglePreview();
	}

	void AssetSystem.IEventListener.OnAssetChanged( Asset asset )
	{
		if ( asset is null || _saving )
			return;
		if ( asset == _asset )
		{
			if ( _dirtyResources.Contains( _event ) )
				PromptForExternalReload( asset, _event );
			else
				ReloadEventAsset( asset );
			return;
		}

		var sound = _savedStates.Keys.OfType<SoundEventResource>().FirstOrDefault( candidate => ResolveAsset( candidate ) == asset );
		if ( sound is not null )
		{
			if ( _dirtyResources.Contains( sound ) )
				PromptForExternalReload( asset, sound );
			else
				ReloadSoundAsset( asset, sound );
			return;
		}

		var parameter = _event.Parameters?.FirstOrDefault( candidate => ResolveAsset( candidate ) == asset );
		if ( parameter is null )
			return;
		if ( _dirtyResources.Contains( parameter ) )
			PromptForExternalReload( asset, parameter );
		else
			ReloadParameterAsset( asset, parameter );
	}

	private void PromptForExternalReload( Asset asset, GameResource resource )
	{
		var popup = new PopupWindow(
			"Audio Asset Changed Externally",
			$"'{asset.Path}' changed outside this editor and also has local changes. Reloading will discard the local version.",
			"Keep Local",
			new Dictionary<string, Action>
			{
				["Reload"] = () =>
				{
					if ( ReferenceEquals( resource, _event ) )
						ReloadEventAsset( asset );
					else if ( resource is SoundEventResource sound )
						ReloadSoundAsset( asset, sound );
					else if ( resource is AudioParameter parameter )
						ReloadParameterAsset( asset, parameter );
				}
			} );
		popup.Show();
	}

	private void ReloadEventAsset( Asset asset )
	{
		DisposePreview( false );
		_dirtyResources.Remove( _event );
		_savedStates.Remove( _event );
		_lastStates.Remove( _event );
		_event = asset.LoadResource<AudioEvent>();
		RegisterResource( _event );
		_selection = _event;
		ReloadUi();
		UpdateWindowTitle();
	}

	private void ReloadSoundAsset( Asset asset, SoundEventResource previous )
	{
		DisposePreview( false );
		_dirtyResources.Remove( previous );
		_savedStates.Remove( previous );
		_lastStates.Remove( previous );
		var replacement = asset.LoadResource<SoundEventResource>();
		if ( replacement is not null )
		{
			foreach ( var instrument in EnumerateInstruments() )
			{
				if ( ReferenceEquals( instrument.Sound, previous ) )
					instrument.Sound = replacement;
			}
			RegisterResource( replacement );
		}
		_selection = _event;
		ReloadUi();
		UpdateWindowTitle();
	}

	private void ReloadParameterAsset( Asset asset, AudioParameter previous )
	{
		DisposePreview( false );
		_dirtyResources.Remove( previous );
		_savedStates.Remove( previous );
		_lastStates.Remove( previous );
		var replacement = asset.LoadResource<AudioParameter>();
		if ( replacement is not null )
		{
			for ( var index = 0; index < _event.Parameters.Count; index++ )
			{
				if ( ReferenceEquals( _event.Parameters[index], previous ) )
					_event.Parameters[index] = replacement;
			}
			foreach ( var track in EnumerateEventTracks() )
			{
				foreach ( var condition in track.Conditions?.Where( condition => condition is not null ) ?? Enumerable.Empty<AudioParameterCondition>() )
				{
					if ( ReferenceEquals( condition.Parameter, previous ) )
						condition.Parameter = replacement;
				}
				foreach ( var automation in track.Automations?.Where( automation => automation is not null ) ?? Enumerable.Empty<AudioAutomation>() )
				{
					if ( ReferenceEquals( automation.Parameter, previous ) )
						automation.Parameter = replacement;
				}
				foreach ( var instrument in track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
				{
					foreach ( var condition in instrument.Conditions?.Where( condition => condition is not null ) ?? Enumerable.Empty<AudioParameterCondition>() )
					{
						if ( ReferenceEquals( condition.Parameter, previous ) )
							condition.Parameter = replacement;
					}
					foreach ( var automation in instrument.Automations?.Where( automation => automation is not null ) ?? Enumerable.Empty<AudioAutomation>() )
					{
						if ( ReferenceEquals( automation.Parameter, previous ) )
							automation.Parameter = replacement;
					}
				}
			}
			RegisterResource( replacement );
		}
		_selection = (GameResource)replacement ?? _event;
		ReloadUi();
		UpdateWindowTitle();
	}

	private IEnumerable<AudioInstrument> EnumerateInstruments()
	{
		foreach ( var instrument in _event.MasterTrack?.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
			yield return instrument;
		foreach ( var track in _event.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
		{
			foreach ( var instrument in track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
				yield return instrument;
		}
	}

	private IEnumerable<AudioTrack> EnumerateEventTracks()
	{
		if ( _event?.MasterTrack is not null )
			yield return _event.MasterTrack;
		foreach ( var track in _event?.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
			yield return track;
	}

	[EditorEvent.Hotload]
	private void OnHotload()
	{
		AudioPreviewPlaybackService.Instance.StopForOwner( this );
		DisposePreview( false );
		CreateMenuBar();
		CreateToolbar();
		CreateDocks();
		if ( _event is null )
			return;
		_selection ??= _event;
		ReloadUi();
	}

	protected override bool OnClose()
	{
		if ( _closingAfterPrompt || _dirtyResources.Count == 0 )
			return true;

		var popup = new PopupWindow(
			"Save Audio Changes",
			"This audio event or one of its referenced audio resources has unsaved changes.",
			"Cancel",
			new Dictionary<string, Action>
			{
				["Discard"] = () =>
				{
					foreach ( var pair in _savedStates )
						pair.Key.Deserialize( pair.Value );
					_closingAfterPrompt = true;
					Close();
				},
				["Save"] = () =>
				{
					if ( !Save() )
						return;
					_closingAfterPrompt = true;
					Close();
				}
			} );
		popup.Show();
		return false;
	}

	protected override void OnClosed()
	{
		AudioPreviewPlaybackService.Instance.StopForOwner( this );
		StopPreview();
		SaveToStateCookie();
		base.OnClosed();
	}
}

internal sealed class AudioEventWorkspaceWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private AudioTimelineView _timeline;
	private AudioEvent _event;

	public float TimelinePosition
	{
		get => _timeline?.TimelinePosition ?? 0f;
		set
		{
			if ( _timeline is not null )
				_timeline.TimelinePosition = value;
		}
	}

	public float Duration => _timeline?.Duration ?? 1f;
	public float AudioContentEnd => _timeline?.AudioContentEnd ?? 0f;

	public AudioEventWorkspaceWidget( AudioEventEditorWindow window ) : base( window )
	{
		_window = window;
		WindowTitle = "Timeline";
		SetWindowIcon( "timeline" );
		Layout = Layout.Column();
	}

	public void SetEvent( AudioEvent audioEvent )
	{
		var timelinePosition = _timeline?.TimelinePosition ?? 0f;
		var savedPixelsPerSecond = _window.LoadTimelineZoom();
		_event = audioEvent;
		Layout.Clear( true );
		_timeline = new AudioTimelineView( _window );
		if ( savedPixelsPerSecond > 0f )
			_timeline.PixelsPerSecond = savedPixelsPerSecond;

		Layout.Add( _timeline, 1 );
		_timeline.SetEvent( audioEvent );
		_timeline.TimelinePosition = MathF.Min( timelinePosition, _timeline.Duration );
		if ( savedPixelsPerSecond <= 0f )
			_timeline.FrameWhenLaidOut();
	}

	public void SetSelection( object selection )
	{
		_timeline?.SetSelection( selection );
	}

	public void Zoom( float factor )
	{
		_timeline?.Zoom( factor );
	}

	public void FrameEvent()
	{
		_timeline?.FrameEvent();
	}

	public void FollowTimelinePosition()
	{
		_timeline?.FollowTimelinePosition();
	}

	public void UpdateTrackAudition()
	{
		_timeline?.UpdateTrackAudition();
	}

	public void UpdatePlaybackVisualization( IReadOnlyList<AudioInstrumentPlaybackState> states )
	{
		_timeline?.UpdatePlaybackVisualization( states );
	}

	public void RefreshInstrumentMove( AudioTrack sourceTrack, AudioTrack targetTrack, AudioInstrument instrument )
	{
		_timeline?.RefreshInstrumentMove( sourceTrack, targetTrack, instrument );
	}

	public void RefreshInstrumentTrim( AudioInstrument instrument )
	{
		_timeline?.RefreshInstrumentTrim( instrument );
	}

	public void AddDestinationAtPlayhead() => _timeline?.AddDestinationAtPlayhead();
	public void AddTransitionAtPlayhead() => _timeline?.AddTransitionAtPlayhead();
	public void AddTransitionRegionAtPlayhead() => _timeline?.AddTransitionRegionAtPlayhead();
	public void AddLoopAtPlayhead() => _timeline?.AddLoopAtPlayhead();
}

internal sealed class AudioSoundLibraryWidget : Widget, AssetSystem.IEventListener
{
	private readonly AudioEventEditorWindow _window;
	private readonly SearchWidget _search;
	private readonly Label _count;
	private readonly AudioSoundLibraryList _list;
	private AudioLibraryItem[] _items = [];

	public AudioSoundLibraryWidget( AudioEventEditorWindow window ) : base( window )
	{
		_window = window;
		WindowTitle = "Sound Files";
		SetWindowIcon( "audio_file" );
		MinimumWidth = 220f;
		Layout = Layout.Column();
		Layout.Margin = 6f;
		Layout.Spacing = 6f;

		var header = Layout.AddRow();
		header.Add( new Label( "Sound Files" ), 1 );
		_count = header.Add( new Label { Alignment = TextFlag.RightCenter } );
		var refresh = header.Add( new ToolButton( string.Empty, "refresh", this ) );
		refresh.ToolTip = $"Refresh project sounds and {ExternalAudioSourceCatalog.SourceRoot}";
		refresh.MouseLeftPress = RefreshAssets;

		_search = Layout.Add( new SearchWidget( this, false ) );
		_search.PlaceholderText = "Search project and external sounds";
		_search.ValueChanged += ApplySearch;

		var hint = Layout.Add( new Label( "Ctrl/Shift-select sounds, then drag to create one multi-instrument. Double-click adds at the playhead." ) );
		hint.WordWrap = true;
		hint.MaximumHeight = 38f;

		_list = Layout.Add( new AudioSoundLibraryList( this, _window, AddSound, SliceSound, OpenSound, InspectSound ), 1 );
		RefreshAssets();
	}

	private void RefreshAssets()
	{
		var project = AssetSystem.All
			.Where( asset => asset?.AssetType == AssetType.SoundFile )
			.Select( AudioLibraryItem.FromAsset );
		var external = ExternalAudioSourceCatalog.Scan().Select( AudioLibraryItem.FromExternal );
		_items = project.Concat( external )
			.OrderBy( item => item.RelativePath, StringComparer.OrdinalIgnoreCase )
			.ToArray();
		ApplySearch();
	}

	private void ApplySearch()
	{
		var query = _search.Value?.Trim() ?? string.Empty;
		var matches = _items
			.Select( item => (Item: item, Score: ScoreItem( item, query )) )
			.Where( match => match.Score >= 0 )
			.OrderByDescending( match => match.Score )
			.ThenBy( match => match.Item.RelativePath, StringComparer.OrdinalIgnoreCase )
			.Select( match => match.Item )
			.ToArray();
		_count.Text = string.IsNullOrWhiteSpace( query ) ? _items.Length.ToString() : $"{matches.Length}/{_items.Length}";
		_list.SetSounds( matches );
	}

	private static int ScoreItem( AudioLibraryItem item, string query )
	{
		if ( string.IsNullOrWhiteSpace( query ) ) return 0;
		var fields = new[] { item.Name, item.RelativePath, item.Source == AudioLibrarySource.External ? "external" : "project" }
			.Concat( item.Asset?.Tags.GetAll() ?? [] )
			.Where( value => !string.IsNullOrWhiteSpace( value ) )
			.ToArray();
		return ScoreFields( fields, query );
	}

	private static int ScoreFields( IReadOnlyList<string> fields, string query )
	{
		var total = 0;
		foreach ( var token in query.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var best = fields.Select( field => FuzzyScore( field, token ) ).DefaultIfEmpty( -1 ).Max();
			if ( best < 0 ) return -1;
			total += best;
		}
		return total;
	}

	private static int FuzzyScore( string candidate, string query )
	{
		if ( string.IsNullOrEmpty( query ) ) return 0;
		var exactIndex = candidate.IndexOf( query, StringComparison.OrdinalIgnoreCase );
		if ( exactIndex >= 0 )
		{
			var wordStart = exactIndex == 0 || !char.IsLetterOrDigit( candidate[exactIndex - 1] );
			return 1000 - exactIndex * 2 - (candidate.Length - query.Length) + (wordStart ? 150 : 0);
		}

		var score = 0;
		var candidateIndex = 0;
		var previousMatch = -2;
		foreach ( var queryCharacter in query )
		{
			while ( candidateIndex < candidate.Length && char.ToUpperInvariant( candidate[candidateIndex] ) != char.ToUpperInvariant( queryCharacter ) ) candidateIndex++;
			if ( candidateIndex >= candidate.Length ) return -1;
			var wordStart = candidateIndex == 0 || !char.IsLetterOrDigit( candidate[candidateIndex - 1] );
			score += candidateIndex == previousMatch + 1 ? 24 : 8;
			if ( wordStart ) score += 18;
			previousMatch = candidateIndex++;
		}
		return score - candidate.Length - (candidateIndex - query.Length) * 2;
	}

	private async void AddSound( AudioLibraryItem item )
	{
		if ( item.Source == AudioLibrarySource.Project )
			_window.AddSoundFromLibrary( item.Asset );
		else
			await _window.AddExternalSoundFromLibraryAsync( item.ExternalPath );
	}

	private void SliceSound( AudioLibraryItem item ) => _window.OpenClipSlicer( item.Asset );
	private void OpenSound( AudioLibraryItem item ) => item.Asset?.OpenInEditor();
	private void InspectSound( AudioLibraryItem item )
	{
		if ( item.Asset is not null ) EditorUtility.InspectorObject = item.Asset;
	}

	void AssetSystem.IEventListener.OnAssetSystemChanges() => RefreshAssets();
	void AssetSystem.IEventListener.OnAssetTagsChanged() => RefreshAssets();

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.45f ) );
		Paint.DrawRect( LocalRect );
	}
}

internal sealed class AudioSoundLibraryList : ListView
{
	private readonly Action<AudioLibraryItem> _addSound;
	private readonly Action<AudioLibraryItem> _sliceSound;
	private readonly Action<AudioLibraryItem> _openSound;
	private readonly Action<AudioLibraryItem> _inspectSound;
	private readonly AudioEventEditorWindow _window;
	private readonly AudioPreviewPlaybackService _preview = AudioPreviewPlaybackService.Instance;

	public AudioSoundLibraryList( Widget parent, AudioEventEditorWindow window, Action<AudioLibraryItem> addSound, Action<AudioLibraryItem> sliceSound, Action<AudioLibraryItem> openSound, Action<AudioLibraryItem> inspectSound ) : base( parent )
	{
		_window = window;
		_addSound = addSound;
		_sliceSound = sliceSound;
		_openSound = openSound;
		_inspectSound = inspectSound;
		_preview.StateChanged += OnPreviewStateChanged;
		ItemSize = new Vector2( 0f, 46f );
		ItemSpacing = 2f;
		MultiSelect = true;
		FocusMode = FocusMode.TabOrClick;
		ItemClicked = HandleItemClicked;
		ItemActivated = HandleItemActivated;
		ItemContextMenu = ShowContextMenu;
	}

	private void HandleItemClicked( object value )
	{
		if ( value is AudioLibraryItem item ) _ = TogglePreviewAsync( item );
	}

	private void HandleItemActivated( object value )
	{
		if ( value is not AudioLibraryItem item ) return;
		_ = RestartPreviewAsync( item );
		_addSound( item );
	}

	public void SetSounds( IReadOnlyList<AudioLibraryItem> items ) => SetItems( items.Cast<object>() );

	private Task TogglePreviewAsync( AudioLibraryItem item )
	{
		if ( item.Source == AudioLibrarySource.External )
		{
			return _preview.ToggleAsync(
				_window, AudioPreviewSource.ExternalFile, item.Id, item.Name,
				token => ExternalAudioSourceCatalog.LoadPreviewAsync( item.ExternalPath, token ),
				_window.StopTimelineForFilePreview );
		}

		return _preview.ToggleAsync(
			_window, AudioPreviewSource.ProjectSoundFile, item.Id, item.Name,
			_ => Task.FromResult( SoundFile.Load( item.Asset.Path ) ),
			_window.StopTimelineForFilePreview );
	}

	private Task RestartPreviewAsync( AudioLibraryItem item )
	{
		if ( item.Source == AudioLibrarySource.External )
		{
			return _preview.PlayAsync(
				_window, AudioPreviewSource.ExternalFile, item.Id, item.Name,
				token => ExternalAudioSourceCatalog.LoadPreviewAsync( item.ExternalPath, token ),
				_window.StopTimelineForFilePreview );
		}

		return _preview.PlayAsync(
			_window, AudioPreviewSource.ProjectSoundFile, item.Id, item.Name,
			_ => Task.FromResult( SoundFile.Load( item.Asset.Path ) ),
			_window.StopTimelineForFilePreview );
	}

	private void OnPreviewStateChanged( AudioPreviewState state ) => Update();

	protected override bool OnDragItem( VirtualWidget widget )
	{
		if ( widget.Object is not AudioLibraryItem item ) return false;
		var items = SelectedItems.OfType<AudioLibraryItem>().ToArray();
		if ( !items.Contains( item ) ) items = new[] { item };
		var drag = new Drag( this );
		drag.Data.Object = new AudioLibraryDragData( libraryItems: items );
		drag.Data.Text = items.Length == 1
			? item.Source == AudioLibrarySource.External ? item.ExternalPath : item.Asset.RelativePath
			: $"{items.Length} sound files";
		drag.Execute();
		return true;
	}
	private void ShowContextMenu( object value )
	{
		if ( value is not AudioLibraryItem item ) return;
		var menu = new ContextMenu( this );
		menu.AddHeading( item.Name );
		menu.AddOption( "Add at Playhead", "add", () => _addSound( item ) );
		if ( item.Source == AudioLibrarySource.Project )
		{
			menu.AddOption( "Slice into Multi-Instrument...", "content_cut", () => _sliceSound( item ) );
			menu.AddSeparator();
			menu.AddOption( "Open Sound", "open_in_new", () => _openSound( item ) );
			menu.AddOption( "Inspect Sound", "manage_search", () => _inspectSound( item ) );
			menu.AddOption( "Show in Asset Browser", "folder_open", () => MainAssetBrowser.Instance?.Local.FocusOnAsset( item.Asset ) );
		}
		menu.AddSeparator();
		var previewSource = item.Source == AudioLibrarySource.External ? AudioPreviewSource.ExternalFile : AudioPreviewSource.ProjectSoundFile;
		if ( _preview.IsActive( previewSource, item.Id ) )
			menu.AddOption( "Stop Preview", "stop", _preview.Stop );
		else
			menu.AddOption( "Preview", "play_arrow", () => _ = RestartPreviewAsync( item ) );
		menu.OpenAtCursor();
	}

	protected override void PaintItem( VirtualWidget widget )
	{
		if ( widget.Object is not AudioLibraryItem item ) return;
		if ( Paint.HasSelected || Paint.HasPressed || Paint.HasMouseOver )
		{
			Paint.ClearPen();
			Paint.SetBrush( (Paint.HasSelected || Paint.HasPressed ? Theme.Primary : Theme.SurfaceLightBackground).WithAlpha( 0.35f ) );
			Paint.DrawRect( widget.Rect.Shrink( 1f ), 3f );
		}

		var previewSource = item.Source == AudioLibrarySource.External ? AudioPreviewSource.ExternalFile : AudioPreviewSource.ProjectSoundFile;
		var previewState = _preview.IsCurrentItem( previewSource, item.Id ) ? _preview.State : AudioPreviewState.Idle;
		var iconRect = widget.Rect.Shrink( 6f );
		iconRect.Width = 22f;
		Paint.SetPen( previewState.Status == AudioPreviewStatus.Error ? Theme.Red : item.Source == AudioLibrarySource.External ? Theme.Green : Theme.Blue );
		Paint.DrawIcon( iconRect, previewState.Status is AudioPreviewStatus.Loading or AudioPreviewStatus.Playing ? "stop_circle" : "audio_file", 19f, TextFlag.Center );

		var textRect = widget.Rect.Shrink( 34f, 4f, 5f, 4f );
		Paint.SetDefaultFont( 8f, 500 );
		Paint.SetPen( Theme.Text );
		Paint.DrawText( textRect, Paint.GetElidedText( item.Name, textRect.Width, ElideMode.Right ), TextFlag.LeftTop | TextFlag.SingleLine );

		var detail = previewState.Status == AudioPreviewStatus.Idle ? SoundFolderLabel( item ) : previewState.Message;
		textRect.Top += 20f;
		Paint.SetDefaultFont( 7f );
		Paint.SetPen( previewState.Status == AudioPreviewStatus.Error ? Theme.Red : Theme.Text.WithAlpha( 0.55f ) );
		Paint.DrawText( textRect, Paint.GetElidedText( detail, textRect.Width, ElideMode.Middle ), TextFlag.LeftTop | TextFlag.SingleLine );
	}

	private static string SoundFolderLabel( AudioLibraryItem item )
	{
		var folder = Path.GetDirectoryName( item.RelativePath )?.Replace( '\\', '/' );
		var location = string.IsNullOrWhiteSpace( folder ) ? "root" : folder;
		return item.Source == AudioLibrarySource.External ? $"External � {location}" : $"Project � {location}";
	}

	public override void OnDestroyed()
	{
		_preview.StateChanged -= OnPreviewStateChanged;
		_preview.StopForOwner( _window );
		base.OnDestroyed();
	}
}
internal readonly record struct AudioTimelineTrackLayout( AudioTrack Track, bool IsMaster, int Index, float Top, float Height )
{
	public float Bottom => Top + Height;
}

internal readonly record struct AudioInstrumentLane( int Index, int Count );

internal sealed class AudioTimelineView : GraphicsView
{
	internal const float HeaderWidth = 220f;
	internal const float RulerHeight = 32f;
	internal const float DefaultTrackHeight = 72f;
	internal const float MinimumTrackHeight = 72f;
	internal const float MaximumTrackHeight = 320f;
	private const float MinimumDuration = 5f;

	private readonly AudioEventEditorWindow _window;
	private readonly List<GraphicsItem> _items = new();
	private readonly List<AudioTimelineTrackLayout> _trackLayouts = new();
	private AudioEvent _event;
	private AudioTimelineBackgroundItem _background;
	private AudioTimelinePlayheadItem _playhead;
	private float _pixelsPerSecond = 140f;
	private float _duration = MinimumDuration;
	private float _audioContentEnd;
	private float _timelinePosition;
	private float _contentWidth;
	private float _contentHeight;
	private bool _anchoredToTopLeft;
	private bool _frameEventOnNextLayout;
	private object _selection;
	private int _reorderTargetRegularIndex = -1;

	public float Duration => _duration;
	public float AudioContentEnd => _audioContentEnd;
	internal IReadOnlyList<AudioTimelineTrackLayout> TrackLayouts => _trackLayouts;
	internal int ReorderTargetRegularIndex => _reorderTargetRegularIndex;
	public float PixelsPerSecond
	{
		get => _pixelsPerSecond;
		set => _pixelsPerSecond = AudioTimelineScaleMath.ConstrainPixelsPerSecond( value );
	}
	public float TimelinePosition
	{
		get => _timelinePosition;
		set
		{
			var clamped = value.Clamp( 0f, _duration );
			if ( MathF.Abs( _timelinePosition - clamped ) < 0.0001f )
				return;
			_timelinePosition = clamped;
			if ( _playhead is not null )
				_playhead.Position = _playhead.Position.WithX( PositionFromTime( _timelinePosition ) );
		}
	}

	public AudioTimelineView( AudioEventEditorWindow window ) : base( window )
	{
		_window = window;
		HorizontalScrollbar = ScrollbarMode.Auto;
		VerticalScrollbar = ScrollbarMode.Auto;
		AcceptDrops = true;
		FocusMode = FocusMode.Click;
	}

	public void SetEvent( AudioEvent audioEvent )
	{
		_event = audioEvent;
		Rebuild();
	}

	public void SetSelection( object selection )
	{
		_selection = selection;
		foreach ( var block in _items.OfType<AudioInstrumentBlockItem>() )
		{
			var selected = ReferenceEquals( block.Instrument, selection );
			if ( block.Selected == selected )
				continue;
			block.Selected = selected;
			block.Update();
		}
		foreach ( var marker in _items.OfType<AudioTimelineMarkerItem>() )
		{
			var selected = ReferenceEquals( marker.Marker, selection );
			if ( marker.Selected == selected )
				continue;
			marker.Selected = selected;
			marker.Update();
		}
		foreach ( var region in _items.OfType<AudioTimelineRegionItem>() )
		{
			var selected = ReferenceEquals( region.Region, selection );
			if ( region.Selected == selected )
				continue;
			region.Selected = selected;
			region.Update();
		}
	}

	public void UpdatePlaybackVisualization( IReadOnlyList<AudioInstrumentPlaybackState> states )
	{
		foreach ( var block in _items.OfType<AudioInstrumentBlockItem>() )
		{
			AudioInstrumentPlaybackState? state = null;
			foreach ( var candidate in states )
			{
				if ( !ReferenceEquals( candidate.Instrument, block.Instrument ) )
					continue;
				state = candidate;
				break;
			}
			block.SetPlaybackState( state );
		}
	}

	private void Rebuild()
	{
		foreach ( var item in _items )
			item.Destroy();
		_items.Clear();
		if ( _event is null )
			return;

		CalculateTrackLayouts();
		RecalculateTimelineExtent();

		_background = new AudioTimelineBackgroundItem( this );
		_background.SceneRect = SceneRect;
		AddTracked( _background );

		foreach ( var layout in _trackLayouts )
		{
			var lanes = CalculateInstrumentLanes( layout.Track );
			var header = new AudioTrackHeaderItem( this, layout.Track, layout.IsMaster, layout.Index );
			header.SceneRect = new Rect( 0f, layout.Top, HeaderWidth, layout.Height );
			AddTracked( header );

			foreach ( var instrument in layout.Track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
			{
				var block = new AudioInstrumentBlockItem( this, layout.Track, instrument, layout.Index );
				ApplyInstrumentBlockLayout( block, layout, lanes[instrument] );
				block.Selected = ReferenceEquals( instrument, _selection );
				AddTracked( block );
			}

			var resizeHandle = new AudioTrackResizeHandleItem( this, layout.Track );
			resizeHandle.SceneRect = new Rect( 0f, layout.Bottom - 3f, SceneRect.Width, 6f );
			AddTracked( resizeHandle );
		}

		foreach ( var destination in _event.DestinationMarkers?.Where( marker => marker is not null ) ?? Enumerable.Empty<DestinationMarker>() )
		{
			var item = new AudioTimelineMarkerItem( this, destination, true );
			item.Position = new Vector2( PositionFromTime( (float)destination.Position.Seconds ) - 8f, 0f );
			item.Size = new Vector2( 16f, SceneRect.Height );
			item.Selected = ReferenceEquals( destination, _selection );
			AddTracked( item );
		}
		foreach ( var transition in _event.TransitionMarkers?.Where( marker => marker is not null ) ?? Enumerable.Empty<TransitionMarker>() )
		{
			var item = new AudioTimelineMarkerItem( this, transition, false );
			item.Position = new Vector2( PositionFromTime( (float)transition.Position.Seconds ) - 8f, 0f );
			item.Size = new Vector2( 16f, SceneRect.Height );
			item.Selected = ReferenceEquals( transition, _selection );
			AddTracked( item );
		}
		foreach ( var region in _event.TransitionRegions?.Where( region => region is not null ) ?? Enumerable.Empty<TransitionRegion>() )
		{
			var item = new AudioTimelineRegionItem( this, region );
			LayoutTimelineControl( item );
			item.Selected = ReferenceEquals( region, _selection );
			AddTracked( item );
		}
		foreach ( var region in _event.LoopRegions?.Where( region => region is not null ) ?? Enumerable.Empty<LoopRegion>() )
		{
			var item = new AudioTimelineRegionItem( this, region );
			LayoutTimelineControl( item );
			item.Selected = ReferenceEquals( region, _selection );
			AddTracked( item );
		}

		_playhead = new AudioTimelinePlayheadItem( this );
		_playhead.Position = new Vector2( PositionFromTime( _timelinePosition ), 0f );
		_playhead.Size = new Vector2( 9f, SceneRect.Height );
		AddTracked( _playhead );
		Update();
	}

	protected override void DoLayout()
	{
		base.DoLayout();
		UpdateSceneExtent();
		if ( _frameEventOnNextLayout && Width > HeaderWidth + 40f )
		{
			_frameEventOnNextLayout = false;
			FrameEvent();
			_anchoredToTopLeft = true;
			return;
		}
		if ( _anchoredToTopLeft || Width <= 0f || Height <= 0f )
			return;

		CenterOn( new Vector2( Width * 0.5f, Height * 0.5f ) );
		_anchoredToTopLeft = true;
	}

	private void UpdateSceneExtent()
	{
		SceneRect = new Rect(
			0f,
			0f,
			MathF.Max( _contentWidth, Width ),
			MathF.Max( _contentHeight, Height ) );
		if ( _background?.IsValid ?? false )
			_background.SceneRect = SceneRect;
		if ( _playhead?.IsValid ?? false )
			_playhead.Size = _playhead.Size.WithY( SceneRect.Height );
		foreach ( var resizeHandle in _items.OfType<AudioTrackResizeHandleItem>() )
			resizeHandle.Size = resizeHandle.Size.WithX( SceneRect.Width );
		foreach ( var marker in _items.OfType<AudioTimelineMarkerItem>() )
			marker.Size = marker.Size.WithY( SceneRect.Height );
	}

	private void CalculateTrackLayouts()
	{
		_trackLayouts.Clear();
		var top = RulerHeight;
		var index = 0;
		foreach ( var pair in EnumerateTracks() )
		{
			var height = pair.Track.TimelineHeight.Clamp( MinimumTrackHeight, MaximumTrackHeight );
			_trackLayouts.Add( new AudioTimelineTrackLayout( pair.Track, pair.IsMaster, index, top, height ) );
			top += height;
			index++;
		}
		_contentHeight = top + 3f;
	}

	private void RelayoutTracks()
	{
		CalculateTrackLayouts();
		UpdateSceneExtent();
		foreach ( var layout in _trackLayouts )
		{
			var lanes = CalculateInstrumentLanes( layout.Track );
			foreach ( var header in _items.OfType<AudioTrackHeaderItem>().Where( item => ReferenceEquals( item.Track, layout.Track ) ) )
				header.SceneRect = new Rect( 0f, layout.Top, HeaderWidth, layout.Height );
			foreach ( var block in _items.OfType<AudioInstrumentBlockItem>().Where( item => ReferenceEquals( item.Track, layout.Track ) ) )
				ApplyInstrumentBlockLayout( block, layout, lanes[block.Instrument] );
			foreach ( var resizeHandle in _items.OfType<AudioTrackResizeHandleItem>().Where( item => ReferenceEquals( item.Track, layout.Track ) ) )
				resizeHandle.SceneRect = new Rect( 0f, layout.Bottom - 3f, SceneRect.Width, 6f );
		}
		_background?.Update();
		Update();
	}

	private void RelayoutTrack( AudioTrack track )
	{
		var layout = _trackLayouts.FirstOrDefault( candidate => ReferenceEquals( candidate.Track, track ) );
		if ( layout.Track is null )
			return;

		var lanes = CalculateInstrumentLanes( track );
		foreach ( var block in _items.OfType<AudioInstrumentBlockItem>().Where( item => ReferenceEquals( item.Track, track ) ) )
		{
			block.SetTrack( track, layout.Index );
			ApplyInstrumentBlockLayout( block, layout, lanes[block.Instrument] );
		}
	}

	private void RecalculateTimelineExtent()
	{
		_audioContentEnd = _trackLayouts.SelectMany( layout => layout.Track.Instruments ?? new List<AudioInstrument>() )
			.Where( instrument => instrument is not null )
			.Select( instrument => instrument.StartTime + EstimateDuration( instrument ) ).DefaultIfEmpty().Max();
		var authoredDuration = _audioContentEnd;
		authoredDuration = MathF.Max( authoredDuration, (float)(_event.DestinationMarkers?.Select( marker => marker?.Position.Seconds ?? 0d ).DefaultIfEmpty().Max() ?? 0d) );
		authoredDuration = MathF.Max( authoredDuration, (float)(_event.TransitionMarkers?.Select( marker => marker?.Position.Seconds ?? 0d ).DefaultIfEmpty().Max() ?? 0d) );
		authoredDuration = MathF.Max( authoredDuration, (float)(_event.TransitionRegions?.Select( region => region?.End.Seconds ?? 0d ).DefaultIfEmpty().Max() ?? 0d) );
		authoredDuration = MathF.Max( authoredDuration, (float)(_event.LoopRegions?.Select( region => region?.End.Seconds ?? 0d ).DefaultIfEmpty().Max() ?? 0d) );
		_duration = MathF.Max( MinimumDuration, MathF.Max( _event.TimelineDuration, authoredDuration + 1f ) );
		_contentWidth = HeaderWidth + _duration * _pixelsPerSecond;
		UpdateSceneExtent();
	}

	private void ApplyInstrumentBlockLayout( AudioInstrumentBlockItem block, AudioTimelineTrackLayout layout, AudioInstrumentLane lane )
	{
		var availableHeight = MathF.Max( 1f, layout.Height - 16f );
		var gap = lane.Count > 1 ? MathF.Min( 2f, availableHeight / (lane.Count * 2f) ) : 0f;
		var laneHeight = MathF.Max( 1f, (availableHeight - gap * (lane.Count - 1)) / lane.Count);
		block.Position = new Vector2(
			PositionFromTime( block.Instrument.StartTime ),
			layout.Top + 8f + lane.Index * (laneHeight + gap) );
		block.Resize( new Vector2(
			MathF.Max( 28f, EstimateDuration( block.Instrument ) * _pixelsPerSecond ),
			laneHeight ) );
	}

	private static Dictionary<AudioInstrument, AudioInstrumentLane> CalculateInstrumentLanes( AudioTrack track )
	{
		var result = new Dictionary<AudioInstrument, AudioInstrumentLane>( ReferenceEqualityComparer.Instance );
		var ordered = track.Instruments?
			.Where( instrument => instrument is not null )
			.Select( instrument => (
				Instrument: instrument,
				Start: instrument.StartTime,
				End: instrument.StartTime + EstimateDuration( instrument ) ) )
			.OrderBy( entry => entry.Start )
			.ThenBy( entry => entry.End )
			.ToArray() ?? Array.Empty<(AudioInstrument Instrument, float Start, float End)>();
		var cluster = new List<(AudioInstrument Instrument, float Start, float End)>();
		var clusterEnd = float.NegativeInfinity;

		foreach ( var entry in ordered )
		{
			if ( cluster.Count > 0 && entry.Start >= clusterEnd - 0.0001f )
			{
				AssignClusterLanes( cluster, result );
				cluster.Clear();
				clusterEnd = float.NegativeInfinity;
			}
			cluster.Add( entry );
			clusterEnd = MathF.Max( clusterEnd, entry.End );
		}

		if ( cluster.Count > 0 )
			AssignClusterLanes( cluster, result );
		return result;
	}

	private static void AssignClusterLanes(
		IReadOnlyList<(AudioInstrument Instrument, float Start, float End)> cluster,
		Dictionary<AudioInstrument, AudioInstrumentLane> result )
	{
		var laneEnds = new List<float>();
		var assignments = new List<(AudioInstrument Instrument, int Lane)>( cluster.Count );
		foreach ( var entry in cluster )
		{
			var lane = -1;
			for ( var index = 0; index < laneEnds.Count; index++ )
			{
				if ( laneEnds[index] > entry.Start + 0.0001f )
					continue;
				lane = index;
				break;
			}
			if ( lane < 0 )
			{
				lane = laneEnds.Count;
				laneEnds.Add( entry.End );
			}
			else
			{
				laneEnds[lane] = entry.End;
			}
			assignments.Add( (entry.Instrument, lane) );
		}

		foreach ( var assignment in assignments )
			result[assignment.Instrument] = new AudioInstrumentLane( assignment.Lane, laneEnds.Count );
	}

	private void AddTracked( GraphicsItem item )
	{
		_items.Add( item );
		Add( item );
	}

	private IEnumerable<(AudioTrack Track, bool IsMaster)> EnumerateTracks()
	{
		if ( _event.MasterTrack is not null )
			yield return (_event.MasterTrack, true);
		foreach ( var track in _event.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
			yield return (track, false);
	}

	public float PositionFromTime( float time )
	{
		return HeaderWidth + time * _pixelsPerSecond;
	}

	public float TimeFromPosition( float position )
	{
		return MathF.Max( 0f, (position - HeaderWidth) / _pixelsPerSecond );
	}

	public void MovePlayhead( float sceneX )
	{
		_window.SetPreviewPosition( TimeFromPosition( sceneX ) );
	}

	public void InstrumentMoved( AudioTrack sourceTrack, AudioTrack targetTrack, AudioInstrument instrument, float sceneX )
	{
		_window.MoveInstrument( sourceTrack, targetTrack, instrument, TimeFromPosition( sceneX ) );
	}

	public int TrackIndexFromPosition( float sceneY )
	{
		if ( _trackLayouts.Count == 0 )
			return -1;
		for ( var index = 0; index < _trackLayouts.Count; index++ )
		{
			if ( sceneY < _trackLayouts[index].Bottom )
				return index;
		}
		return _trackLayouts.Count - 1;
	}

	public AudioTrack TrackAtIndex( int trackIndex )
	{
		return trackIndex >= 0 && trackIndex < _trackLayouts.Count ? _trackLayouts[trackIndex].Track : null;
	}

	public float InstrumentRowPosition( int trackIndex )
	{
		return trackIndex >= 0 && trackIndex < _trackLayouts.Count ? _trackLayouts[trackIndex].Top + 8f : RulerHeight + 8f;
	}

	public float InstrumentRowHeight( int trackIndex )
	{
		return trackIndex >= 0 && trackIndex < _trackLayouts.Count
			? _trackLayouts[trackIndex].Height - 16f
			: DefaultTrackHeight - 16f;
	}

	public float TrackTop( AudioTrack track )
	{
		return _trackLayouts.FirstOrDefault( layout => ReferenceEquals( layout.Track, track ) ).Top;
	}

	public int RegularTrackIndexFromPosition( float sceneY )
	{
		if ( _event?.Tracks is null || _event.Tracks.Count == 0 )
			return -1;
		return Math.Clamp( TrackIndexFromPosition( sceneY ) - 1, 0, _event.Tracks.Count - 1 );
	}

	public void PreviewTrackHeight( AudioTrack track, float height )
	{
		if ( track is null )
			return;
		track.TimelineHeight = height.Clamp( MinimumTrackHeight, MaximumTrackHeight ).SnapToGrid( 1f );
		RelayoutTracks();
	}

	public void CommitTrackHeight( JsonObject before )
	{
		_window.CommitEventMutation( "Resize Audio Track", before );
	}

	public void PreviewTrackReorder( int regularTrackIndex )
	{
		_reorderTargetRegularIndex = regularTrackIndex;
		_background?.Update();
	}

	public void CommitTrackReorder( AudioTrack track, int regularTrackIndex )
	{
		_reorderTargetRegularIndex = -1;
		_background?.Update();
		_window.MoveTrack( track, regularTrackIndex );
	}

	public void CancelTrackReorder()
	{
		_reorderTargetRegularIndex = -1;
		_background?.Update();
	}

	public void WaveformLoaded( AudioInstrumentBlockItem block )
	{
		if ( !IsValid || block is null || !block.IsValid || !_items.Contains( block ) )
			return;
		RefreshInstrumentTrim( block.Instrument );
	}

	public void RefreshInstrumentMove( AudioTrack sourceTrack, AudioTrack targetTrack, AudioInstrument instrument )
	{
		var block = _items.OfType<AudioInstrumentBlockItem>()
			.FirstOrDefault( candidate => ReferenceEquals( candidate.Instrument, instrument ) );
		var targetLayout = _trackLayouts.FirstOrDefault( candidate => ReferenceEquals( candidate.Track, targetTrack ) );
		if ( block is null || targetLayout.Track is null )
		{
			Rebuild();
			return;
		}

		block.SetTrack( targetTrack, targetLayout.Index );
		RecalculateTimelineExtent();
		RelayoutTrack( sourceTrack );
		if ( !ReferenceEquals( sourceTrack, targetTrack ) )
			RelayoutTrack( targetTrack );
	}

	public void RefreshInstrumentTrim( AudioInstrument instrument )
	{
		var block = _items.OfType<AudioInstrumentBlockItem>()
			.FirstOrDefault( candidate => ReferenceEquals( candidate.Instrument, instrument ) );
		if ( block is null )
		{
			Rebuild();
			return;
		}

		RecalculateTimelineExtent();
		RelayoutTrack( block.Track );
	}

	public void SelectInstrument( AudioInstrument instrument )
	{
		_window.SelectObject( instrument );
	}

	public void SelectTrack( AudioTrack track )
	{
		_window.SelectObject( track );
	}

	public void SelectTimelineControl( object control )
	{
		_window.SelectObject( control );
	}

	public void LayoutTimelineControl( AudioTimelineRegionItem item )
	{
		var range = item.Region switch
		{
			LoopRegion loop => (loop.Start.Seconds, loop.End.Seconds),
			TransitionRegion transition => (transition.Start.Seconds, transition.End.Seconds),
			_ => default
		};
		item.Position = new Vector2(
			PositionFromTime( (float)range.Item1 ),
			item.Region is LoopRegion ? RulerHeight - 15f : RulerHeight - 29f );
		item.Size = new Vector2( MathF.Max( 12f, (float)(range.Item2 - range.Item1) * PixelsPerSecond ), 13f );
	}

	public void RemoveTimelineControl( object control )
	{
		if ( control is DestinationMarker destination )
		{
			var references = (_event.TransitionMarkers?.Where( marker => marker.DestinationMarkerId == destination.Id ).Select( marker => marker.Name ?? marker.Id.ToString() )
				?? Enumerable.Empty<string>())
				.Concat( _event.TransitionRegions?.Where( region => region.DestinationMarkerId == destination.Id ).Select( region => region.Name ?? region.Id.ToString() )
					?? Enumerable.Empty<string>() )
				.ToArray();
			if ( references.Length > 0 )
			{
				Log.Warning( $"Cannot delete destination marker '{destination.Name}'. Referenced by: {string.Join( ", ", references )}." );
				return;
			}
		}
		_window.MutateEvent( "Delete Timeline Control", () =>
		{
			switch ( control )
			{
				case DestinationMarker destinationToRemove: _event.DestinationMarkers.Remove( destinationToRemove ); break;
				case TransitionMarker markerToRemove: _event.TransitionMarkers.Remove( markerToRemove ); break;
				case TransitionRegion transitionToRemove: _event.TransitionRegions.Remove( transitionToRemove ); break;
				case LoopRegion loopToRemove: _event.LoopRegions.Remove( loopToRemove ); break;
			}
		} );
	}

	public bool IsTrackMuted( AudioTrack track ) => _window.IsTrackMuted( track );
	public bool IsTrackSoloed( AudioTrack track ) => _window.IsTrackSoloed( track );
	public bool IsTrackAudible( AudioTrack track ) => _window.IsTrackAudible( track );
	public bool HasTrackAuditionOverrides => _window.HasTrackAuditionOverrides;
	public void ToggleTrackMute( AudioTrack track ) => _window.ToggleTrackMute( track );
	public void ToggleTrackSolo( AudioTrack track ) => _window.ToggleTrackSolo( track );
	public void ClearTrackAudition() => _window.ClearTrackAudition();

	public JsonObject CaptureEventState()
	{
		return _window.CaptureEventState();
	}

	public void PreviewTrackChange()
	{
		_window.PreviewAuthoredChange();
	}

	public void CommitInstrumentEdit( string title, JsonObject before, AudioInstrument instrument )
	{
		_window.CommitEventMutation( title, before, () => RefreshInstrumentTrim( instrument ) );
	}

	public void CommitTrackChange( string title, JsonObject before )
	{
		_window.CommitEventMutation( title, before );
	}

	public void RemoveInstrument( AudioTrack track, AudioInstrument instrument )
	{
		_window.RemoveInstrument( track, instrument );
	}

	public void RemoveTrack( AudioTrack track )
	{
		_window.RemoveTrack( track );
	}

	public void Zoom( float factor )
	{
		ApplyTimelineScale( _pixelsPerSecond * factor );
		CenterOn( new Vector2( PositionFromTime( _timelinePosition ), 0f ) );
	}

	public void FrameEvent()
	{
		ApplyTimelineScale( (Width - HeaderWidth - 40f) / MathF.Max( 1f, _duration ) );
		CenterOn( new Vector2( SceneRect.Width * 0.5f, 0f ) );
	}

	public void FrameWhenLaidOut()
	{
		_frameEventOnNextLayout = true;
	}

	private void ApplyTimelineScale( float pixelsPerSecond )
	{
		_pixelsPerSecond = AudioTimelineScaleMath.ConstrainPixelsPerSecond( pixelsPerSecond );
		_window.RememberTimelineZoom( _pixelsPerSecond );
		if ( _event is null )
		{
			_contentWidth = HeaderWidth + _duration * _pixelsPerSecond;
			UpdateSceneExtent();
			return;
		}

		RecalculateTimelineExtent();
		foreach ( var block in _items.OfType<AudioInstrumentBlockItem>() )
		{
			block.Position = block.Position.WithX( PositionFromTime( block.Instrument.StartTime ) );
			block.Resize( block.Size.WithX( MathF.Max( 28f, EstimateDuration( block.Instrument ) * _pixelsPerSecond ) ) );
		}
		foreach ( var marker in _items.OfType<AudioTimelineMarkerItem>() )
		{
			var time = marker.Marker switch
			{
				DestinationMarker destination => (float)destination.Position.Seconds,
				TransitionMarker transition => (float)transition.Position.Seconds,
				_ => 0f
			};
			marker.Position = marker.Position.WithX( PositionFromTime( time ) - marker.Width * 0.5f );
		}
		foreach ( var region in _items.OfType<AudioTimelineRegionItem>() )
			LayoutTimelineControl( region );
		if ( _playhead is not null )
			_playhead.Position = _playhead.Position.WithX( PositionFromTime( _timelinePosition ) );

		_background?.Update();
		Update();
	}

	public void FollowTimelinePosition()
	{
		var scenePosition = new Vector2( PositionFromTime( _timelinePosition ), 0f );
		var localPosition = FromScene( scenePosition );
		if ( localPosition.x >= HeaderWidth + 48f && localPosition.x <= Width - 80f )
			return;
		var currentCenter = ToScene( ContentRect.Center );
		CenterOn( new Vector2( scenePosition.x, currentCenter.y ) );
	}

	public void UpdateTrackAudition()
	{
		foreach ( var item in _items )
		{
			if ( item is AudioTrackHeaderItem )
				item.Update();
			else if ( item is AudioInstrumentBlockItem block )
				block.RefreshAudibility();
		}
	}

	protected override void OnContextMenu( ContextMenuEvent e )
	{
		base.OnContextMenu( e );

		var scenePosition = ToScene( e.LocalPosition );
		var control = _items.OfType<AudioTimelineMarkerItem>()
			.LastOrDefault( candidate => candidate.SceneRect.IsInside( scenePosition ) )?.Marker
			?? _items.OfType<AudioTimelineRegionItem>()
				.LastOrDefault( candidate => candidate.SceneRect.IsInside( scenePosition ) )?.Region;
		if ( control is not null )
		{
			OpenTimelineControlContextMenu( e, control );
			return;
		}
		var block = _items.OfType<AudioInstrumentBlockItem>()
			.LastOrDefault( candidate => candidate.SceneRect.IsInside( scenePosition ) );
		if ( block is null )
		{
			OpenTimelineContextMenu( e, scenePosition );
			return;
		}

		var instrument = block.Instrument;
		var track = block.Track;
		SelectInstrument( instrument );

		var menu = new ContextMenu( this );
		menu.AddHeading( InstrumentName( instrument ) );
		menu.AddOption( "Play Event From Instrument Start", "play_arrow", () => _window.PlayEventFrom( instrument.StartTime ) );
		menu.AddOption( "Move Playhead to Instrument", "vertical_align_center", () => MovePlayhead( PositionFromTime( instrument.StartTime ) ) );
		menu.AddOption( $"Move Instrument to Playhead ({_timelinePosition:0.00}s)", "start", () => _window.MoveInstrument( track, track, instrument, _timelinePosition ) );
		menu.AddOption( "Move Instrument to Start (0.00s)", "first_page", () => _window.MoveInstrument( track, track, instrument, 0f ) );

		menu.AddSeparator();
		BuildConditionMenu( menu, instrument );
		BuildAutomationMenu( menu, track, instrument );

		menu.AddSeparator();
		var properties = menu.AddOption( "Edit Instrument Properties", "tune", () => SelectInstrument( instrument ) );
		properties.StatusTip = "Show start time, conditions, and automations in the context inspector.";
		if ( instrument.TrimStart > 0f || instrument.TrimEnd > 0f )
		{
			menu.AddOption( "Reset Trimming", "restart_alt", () => _window.MutateEvent( "Reset Instrument Trimming", () =>
			{
				instrument.TrimStart = 0f;
				instrument.TrimEnd = 0f;
			} ) );
		}
		if ( instrument.Sound?.EmbeddedResource is not null )
		{
			var soundFile = instrument.Sound.Tracks?.Select( track => track?.Sound ).FirstOrDefault( sound => sound.IsValid() );
			if ( soundFile.IsValid() )
			{
				menu.AddOption( "Open Sound File", "open_in_new", () => _window.OpenSoundAsset( soundFile ) );
				menu.AddOption( "Inspect Sound File", "manage_search", () => _window.InspectSoundAsset( soundFile ) );
			}
		}
		else if ( instrument.Sound is not null )
		{
			menu.AddOption( "Open Sound Event", "open_in_new", () => _window.OpenSoundAsset( instrument.Sound ) );
			menu.AddOption( "Inspect Sound Event", "manage_search", () => _window.InspectSoundAsset( instrument.Sound ) );
		}

		menu.AddSeparator();
		menu.AddOption( "Duplicate After Sound", "content_copy", () => _window.DuplicateInstrumentAfterSound( track, instrument ) );
		menu.AddOption( "Delete Instrument", "delete", () => _window.RemoveInstrument( track, instrument ) );
		menu.OpenAt( e.ScreenPosition );
	}

	private void OpenTimelineControlContextMenu( ContextMenuEvent e, object control )
	{
		SelectTimelineControl( control );
		var menu = new ContextMenu( this );
		menu.AddHeading( control switch
		{
			DestinationMarker headingDestination => $"Destination: {headingDestination.Name}",
			TransitionMarker headingMarker => $"Transition: {headingMarker.Name ?? headingMarker.Id.ToString()}",
			TransitionRegion headingRegion => $"Transition Region: {headingRegion.Name ?? headingRegion.Id.ToString()}",
			LoopRegion headingLoop => $"Loop: {headingLoop.Name ?? headingLoop.Id.ToString()}",
			_ => "Timeline Control"
		} );
		menu.AddOption( "Edit Properties", "tune", () => SelectTimelineControl( control ) );
		if ( control is DestinationMarker destination )
			menu.AddOption( "Move Playhead Here", "vertical_align_center", () => MovePlayhead( PositionFromTime( (float)destination.Position.Seconds ) ) );
		if ( control is TransitionMarker marker )
			AddDestinationMenu( menu, marker.DestinationMarkerId, id => _window.MutateEvent( "Set Transition Destination", () => marker.DestinationMarkerId = id ) );
		if ( control is TransitionRegion region )
			AddDestinationMenu( menu, region.DestinationMarkerId, id => _window.MutateEvent( "Set Transition Destination", () => region.DestinationMarkerId = id ) );
		menu.AddSeparator();
		menu.AddOption( "Delete", "delete", () => RemoveTimelineControl( control ) );
		menu.OpenAt( e.ScreenPosition );
	}

	private void AddDestinationMenu( ContextMenu menu, Guid selectedId, Action<Guid> select )
	{
		var destinations = menu.AddMenu( "Destination", "flag" );
		foreach ( var destination in _event.DestinationMarkers )
		{
			var option = destinations.AddOption( destination.Name, null, () => select( destination.Id ) );
			option.Checked = destination.Id == selectedId;
		}
		var selected = _event.DestinationMarkers.FirstOrDefault( destination => destination.Id == selectedId );
		if ( selected is not null )
			destinations.AddOption( "Jump View to Destination", "my_location", () => CenterOn( new Vector2( PositionFromTime( (float)selected.Position.Seconds ), 0f ) ) );
	}

	private void OpenTimelineContextMenu( ContextMenuEvent e, Vector2 scenePosition )
	{
		var menu = new ContextMenu( this );
		var controlTime = scenePosition.x <= HeaderWidth ? _timelinePosition : TimeFromPosition( scenePosition.x );
		menu.AddHeading( $"Timeline at {controlTime:0.00}s" );
		menu.AddOption( "Add Destination Marker", "flag", () => AddDestinationMarker( controlTime ) );
		menu.AddOption( "Add Transition Marker", "alt_route", () => AddTransitionMarker( controlTime ) );
		menu.AddOption( "Add Transition Region", "compare_arrows", () => AddTransitionRegion( controlTime ) );
		menu.AddOption( "Add Loop Region", "repeat", () => AddLoopRegion( controlTime ) );
		menu.AddSeparator();
		var trackIndex = TrackIndexFromPosition( scenePosition.y );
		if ( scenePosition.y < RulerHeight || _trackLayouts.Count == 0 || scenePosition.y >= _trackLayouts[^1].Bottom || trackIndex < 0 || trackIndex >= _trackLayouts.Count )
		{
			menu.AddOption( "Edit Event Settings", "tune", () => _window.SelectObject( _event ) );
			menu.AddOption( "Add Audio Track", "add", _window.AddTrack );
			menu.AddOption( "Add Event Parameter", "tune", _window.AddParameter );
			menu.OpenAt( e.ScreenPosition );
			return;
		}

		var layout = _trackLayouts[trackIndex];
		var track = layout.Track;
		SelectTrack( track );
		var startTime = scenePosition.x <= HeaderWidth ? _timelinePosition : TimeFromPosition( scenePosition.x );
		var rowY = scenePosition.y - layout.Top;
		AudioAutomationTarget? focusedTarget = scenePosition.x <= HeaderWidth
			? rowY switch
			{
				>= 27f and < 47f => AudioAutomationTarget.Volume,
				>= 47f and < 68f => AudioAutomationTarget.Pitch,
				_ => null
			}
			: null;

		menu.AddHeading( layout.IsMaster ? "Master Track" : track.Name );
		menu.AddOption( $"Add Sound Instrument at {startTime:0.00}s", "audio_file", () => ShowSoundPicker( track, startTime ) );
		menu.AddOption( "Add Audio Track", "add", _window.AddTrack );
		menu.AddOption( "Add Event Parameter", "tune", _window.AddParameter );
		menu.AddSeparator();

		if ( !layout.IsMaster )
		{
			var mute = menu.AddOption( "Mute Track", "volume_off", () => _window.ToggleTrackMute( track ) );
			mute.Checked = _window.IsTrackMuted( track );
			var solo = menu.AddOption( "Solo Track", "headphones", () => _window.ToggleTrackSolo( track ) );
			solo.Checked = _window.IsTrackSoloed( track );
		}
		if ( _window.HasTrackAuditionOverrides )
			menu.AddOption( "Clear All Mute/Solo", "hearing", _window.ClearTrackAudition );

		BuildTrackAutomationMenu( menu, track, focusedTarget );
		BuildConditionMenu( menu, track );

		menu.AddSeparator();
		menu.AddOption( "Edit Track Properties", "tune", () => SelectTrack( track ) );
		if ( !layout.IsMaster )
		{
			menu.AddOption( "Rename Track", "edit", () => _window.RenameTrack( track ) );
			menu.AddOption( "Delete Track", "delete", () => _window.RemoveTrack( track ) );
		}
		menu.OpenAt( e.ScreenPosition );
	}

	private void AddDestinationMarker( float time )
	{
		DestinationMarker created = null;
		_window.MutateEvent( "Add Destination Marker", () =>
		{
			var baseName = "Destination";
			var suffix = 1;
			var name = baseName;
			while ( _event.DestinationMarkers.Any( marker => string.Equals( marker.Name, name, StringComparison.OrdinalIgnoreCase ) ) )
				name = $"{baseName} {++suffix}";
			created = new DestinationMarker { Name = name, Position = new TimelineTime( time ) };
			_event.DestinationMarkers.Add( created );
		} );
		SelectTimelineControl( created );
	}

	public void AddDestinationAtPlayhead()
	{
		AddDestinationMarker( _timelinePosition );
	}

	public void AddTransitionAtPlayhead()
	{
		AddTransitionMarker( _timelinePosition );
	}

	public void AddTransitionRegionAtPlayhead()
	{
		AddTransitionRegion( _timelinePosition );
	}

	public void AddLoopAtPlayhead()
	{
		AddLoopRegion( _timelinePosition );
	}

	private void AddTransitionMarker( float time )
	{
		if ( _event.DestinationMarkers.Count == 0 )
		{
			Log.Warning( "Create a destination marker before adding a transition marker." );
			return;
		}
		TransitionMarker created = null;
		_window.MutateEvent( "Add Transition Marker", () =>
		{
			created = new TransitionMarker
			{
				Name = "Transition",
				Position = new TimelineTime( time ),
				DestinationMarkerId = _event.DestinationMarkers[0].Id
			};
			_event.TransitionMarkers.Add( created );
		} );
		SelectTimelineControl( created );
	}

	private void AddTransitionRegion( float time )
	{
		if ( _event.DestinationMarkers.Count == 0 )
		{
			Log.Warning( "Create a destination marker before adding a transition region." );
			return;
		}
		TransitionRegion created = null;
		_window.MutateEvent( "Add Transition Region", () =>
		{
			created = new TransitionRegion
			{
				Name = "Transition Region",
				Start = new TimelineTime( time ),
				End = new TimelineTime( time + 1f ),
				DestinationMarkerId = _event.DestinationMarkers[0].Id
			};
			_event.TransitionRegions.Add( created );
		} );
		SelectTimelineControl( created );
	}

	private void AddLoopRegion( float time )
	{
		LoopRegion created = null;
		_window.MutateEvent( "Add Loop Region", () =>
		{
			created = new LoopRegion
			{
				Name = "Loop",
				Start = new TimelineTime( time ),
				End = new TimelineTime( time + 1f )
			};
			_event.LoopRegions.Add( created );
		} );
		SelectTimelineControl( created );
	}

	private void ShowSoundPicker( AudioTrack track, float startTime )
	{
		var picker = AssetPicker.Create( this, AssetType.SoundFile, new AssetPicker.PickerOptions
		{
			EnableMultiselect = false,
			EnableCloud = false
		} );
		picker.Window.StateCookie = "AudioEventEditor.AddSoundInstrument";
		picker.Window.RestoreFromStateCookie();
		picker.Window.Title = $"Add Sound Instrument to {track.Name}";
		picker.OnAssetPicked = assets =>
		{
			var asset = assets.FirstOrDefault();
			var sound = asset is null ? null : SoundFile.Load( asset.Path );
			if ( sound.IsValid() )
				_window.AddInstrument( track, sound, startTime );
		};
		picker.Window.Show();
	}

	private void BuildTrackAutomationMenu( ContextMenu menu, AudioTrack track, AudioAutomationTarget? focusedTarget )
	{
		var automationMenu = menu.AddMenu( focusedTarget is null ? "Track Automation" : $"{focusedTarget} Automation", "timeline" );
		var parameters = _event.Parameters?
			.Where( parameter => parameter is not null && parameter.Type != AudioParameterType.Labeled )
			.ToArray() ?? Array.Empty<AudioParameter>();
		var targets = focusedTarget is null
			? new[] { AudioAutomationTarget.Volume, AudioAutomationTarget.Pitch }
			: new[] { focusedTarget.Value };

		foreach ( var target in targets )
		{
			var targetMenu = focusedTarget is null
				? automationMenu.AddMenu( target.ToString(), target == AudioAutomationTarget.Volume ? "volume_up" : "speed" )
				: automationMenu;
			var available = parameters.Where( parameter =>
				!(track.Automations?.Any( automation => automation.Target == target
					&& ReferenceEquals( automation.Parameter, parameter ) ) ?? false) ).ToArray();
			foreach ( var parameter in available )
				targetMenu.AddOption( $"Add {parameter.Name}", "add", () => _window.AddAutomation( track, null, parameter, target ) );

			var authored = track.Automations?.Where( automation => automation is not null && automation.Target == target ).ToArray()
				?? Array.Empty<AudioAutomation>();
			foreach ( var automation in authored )
				targetMenu.AddOption( $"Remove {automation.Parameter?.Name}", "remove", () => _window.RemoveAutomation( track, null, automation ) );
			targetMenu.AddOption( $"Reset {target} to 1.00", "restart_alt", () => _window.ResetTrackControl( track, target ) );
			if ( available.Length == 0 && authored.Length == 0 )
				targetMenu.AddOption( "Create a parameter first", null, _window.AddParameter );
		}
	}

	private void BuildConditionMenu( ContextMenu menu, AudioInstrument instrument )
	{
		var conditions = menu.AddMenu( "Conditions", "rule" );
		var add = conditions.AddMenu( "Add Condition", "add" );
		var parameters = _event.Parameters?.Where( parameter => parameter is not null ).ToArray() ?? Array.Empty<AudioParameter>();
		var available = parameters
			.Where( parameter => !(instrument.Conditions?.Any( condition => ReferenceEquals( condition.Parameter, parameter ) ) ?? false) )
			.ToArray();

		foreach ( var parameter in available )
		{
			var defaultValue = parameter.Type == AudioParameterType.Labeled
				? parameter.DefaultLabel
				: parameter.DefaultValue.ToString( "0.##" );
			add.AddOption( $"{parameter.Name} (default: {defaultValue})", null, () => _window.AddCondition( instrument, parameter ) );
		}
		if ( available.Length == 0 )
			add.AddOption( "No available parameters", null ).Enabled = false;

		var authored = instrument.Conditions?.Where( condition => condition is not null ).ToArray() ?? Array.Empty<AudioParameterCondition>();
		if ( authored.Length == 0 )
			return;

		var remove = conditions.AddMenu( "Remove Condition", "remove" );
		foreach ( var condition in authored )
		{
			var value = string.IsNullOrWhiteSpace( condition.Label )
				? $"{condition.Minimum:0.##} – {condition.Maximum:0.##}"
				: condition.Label;
			if ( condition.Invert )
				value = $"NOT {value}";
			remove.AddOption( $"{condition.Parameter?.Name}: {value}", null, () => _window.RemoveCondition( instrument, condition ) );
		}
	}

	private void BuildConditionMenu( ContextMenu menu, AudioTrack track )
	{
		var conditions = menu.AddMenu( "Track Conditions", "rule" );
		var add = conditions.AddMenu( "Add Condition", "add" );
		var parameters = _event.Parameters?.Where( parameter => parameter is not null ).ToArray() ?? Array.Empty<AudioParameter>();
		var available = parameters
			.Where( parameter => !(track.Conditions?.Any( condition => ReferenceEquals( condition.Parameter, parameter ) ) ?? false) )
			.ToArray();

		foreach ( var parameter in available )
		{
			var defaultValue = parameter.Type == AudioParameterType.Labeled
				? parameter.DefaultLabel
				: parameter.DefaultValue.ToString( "0.##" );
			add.AddOption( $"{parameter.Name} (default: {defaultValue})", null, () => _window.AddCondition( track, parameter ) );
		}
		if ( available.Length == 0 )
			add.AddOption( "No available parameters", null ).Enabled = false;

		var authored = track.Conditions?.Where( condition => condition is not null ).ToArray() ?? Array.Empty<AudioParameterCondition>();
		if ( authored.Length == 0 )
			return;

		var remove = conditions.AddMenu( "Remove Condition", "remove" );
		foreach ( var condition in authored )
		{
			var value = string.IsNullOrWhiteSpace( condition.Label )
				? $"{condition.Minimum:0.##} – {condition.Maximum:0.##}"
				: condition.Label;
			if ( condition.Invert )
				value = $"NOT {value}";
			remove.AddOption( $"{condition.Parameter?.Name}: {value}", null, () => _window.RemoveCondition( track, condition ) );
		}
	}

	private void BuildAutomationMenu( ContextMenu menu, AudioTrack track, AudioInstrument instrument )
	{
		var automationMenu = menu.AddMenu( "Automation", "timeline" );
		var parameters = _event.Parameters?
			.Where( parameter => parameter is not null && parameter.Type != AudioParameterType.Labeled )
			.ToArray() ?? Array.Empty<AudioParameter>();

		foreach ( var target in new[] { AudioAutomationTarget.Volume, AudioAutomationTarget.Pitch } )
		{
			var targetMenu = automationMenu.AddMenu( $"Add {target} Automation", target == AudioAutomationTarget.Volume ? "volume_up" : "speed" );
			var available = parameters
				.Where( parameter => !(instrument.Automations?.Any( automation => automation.Target == target && ReferenceEquals( automation.Parameter, parameter ) ) ?? false) )
				.ToArray();
			foreach ( var parameter in available )
				targetMenu.AddOption( parameter.Name, null, () => _window.AddAutomation( track, instrument, parameter, target ) );
			if ( available.Length == 0 )
				targetMenu.AddOption( "No available parameters", null ).Enabled = false;
		}

		var authored = instrument.Automations?.Where( automation => automation is not null ).ToArray() ?? Array.Empty<AudioAutomation>();
		if ( authored.Length == 0 )
			return;

		var remove = automationMenu.AddMenu( "Remove Automation", "remove" );
		foreach ( var automation in authored )
			remove.AddOption( $"{automation.Parameter?.Name} → {automation.Target}", null, () => _window.RemoveAutomation( track, instrument, automation ) );
	}

	protected override void OnMouseWheel( WheelEvent e )
	{
		Zoom( e.Delta > 0f ? 1.1f : 1f / 1.1f );
		e.Accept();
	}

	public override void OnDragHover( DragEvent ev )
	{
		var payload = ev.Data.OfType<AudioLibraryDragData>().FirstOrDefault();
		var acceptsExternal = payload?.LibraryItems.Any( item => item.Source == AudioLibrarySource.External && ExternalAudioSourceCatalog.IsWithinSourceRoot( item.ExternalPath ) ) == true;
		var acceptsSound = payload?.HasItems == true || acceptsExternal || ev.Data.Assets.Any( drag =>
			drag.PackageIdent is not null
			|| AssetSystem.FindByPath( drag.AssetPath )?.AssetType == AssetType.SoundFile );
		ev.Action = acceptsSound ? DropAction.Copy : DropAction.Ignore;
	}

	public override void OnDragDrop( DragEvent ev )
	{
		var payload = ev.Data.OfType<AudioLibraryDragData>().FirstOrDefault();
		if ( ev.Data.Assets.Count == 0 && payload?.HasItems != true )
			return;
		ev.Action = DropAction.Copy;
		_ = AddDroppedSoundsAsync( ev.Data.Assets, payload, ToScene( ev.LocalPosition ) );
	}
	private async Task AddDroppedSoundsAsync( IReadOnlyList<DragAssetData> draggedAssets, AudioLibraryDragData payload, Vector2 scenePosition )
	{
		var trackIndex = TrackIndexFromPosition( scenePosition.y );
		if ( scenePosition.y < RulerHeight || _trackLayouts.Count == 0 || scenePosition.y >= _trackLayouts[^1].Bottom || trackIndex < 0 || trackIndex >= _trackLayouts.Count )
			return;
		var targetInstrument = _items.OfType<AudioInstrumentBlockItem>()
			.LastOrDefault( block => block.SceneRect.IsInside( scenePosition ) )?
			.Instrument;
		var startTime = TimeFromPosition( scenePosition.x );
		var sounds = new List<SoundFile>();

		foreach ( var item in payload?.LibraryItems ?? Array.Empty<AudioLibraryItem>() )
		{
			try
			{
				var asset = item.Source == AudioLibrarySource.External
					? await ExternalAudioSourceCatalog.ImportAsync( item.ExternalPath )
					: item.Asset;
				var sound = SoundFile.Load( asset.Path );
				if ( sound.IsValid() ) sounds.Add( sound );
			}
			catch ( Exception exception ) { Log.Warning( exception, $"Could not import sound '{item.Name}'." ); }
		}

		foreach ( var package in payload?.CloudPackages ?? Array.Empty<Package>() )
		{
			try
			{
				var asset = await AudioCloudSoundCatalog.InstallAsync( package.FullIdent );
				var sound = SoundFile.Load( asset.Path );
				if ( sound.IsValid() ) sounds.Add( sound );
			}
			catch ( Exception exception ) { Log.Warning( exception, $"Could not install cloud sound '{package.FullIdent}'." ); }
		}
		foreach ( var dragged in draggedAssets )
		{
			Asset asset;
			try
			{
				asset = await dragged.GetAssetAsync();
			}
			catch ( Exception exception )
			{
				Log.Warning( exception, $"Soundfalls could not import '{dragged.PackageIdent ?? dragged.AssetPath}'." );
				continue;
			}

			if ( asset?.AssetType != AssetType.SoundFile )
				continue;
			var sound = SoundFile.Load( asset.Path );
			if ( sound.IsValid() )
				sounds.Add( sound );
		}
		if ( targetInstrument is not null )
			_window.AddSoundsToInstrument( targetInstrument, sounds );
		else
			_window.AddInstruments( _trackLayouts[trackIndex].Track, sounds, startTime );
	}

	internal static float EstimateDuration( AudioInstrument instrument )
	{
		var sound = instrument?.Sound;
		if ( sound?.Tracks is null || sound.Tracks.Count == 0 )
			return 1f;
		var sourceDuration = MathF.Max( 0.1f, sound.Tracks
			.Where( track => track?.Sound.IsValid() == true && track.Sound.IsLoaded )
			.Select( track => track.Length > 0f ? track.Length : MathF.Max( 0f, track.Sound.Duration - track.Offset ) )
			.DefaultIfEmpty( 1f )
			.Max() );
		var trimEnd = instrument.TrimEnd > 0f ? MathF.Min( instrument.TrimEnd, sourceDuration ) : sourceDuration;
		return MathF.Max( 0.01f, trimEnd - MathF.Min( instrument.TrimStart, trimEnd ) );
	}

	internal static string InstrumentName( AudioInstrument instrument )
	{
		var sound = instrument?.Sound;
		if ( !string.IsNullOrWhiteSpace( sound?.ResourceName ) )
			return sound.ResourceName;

		var file = sound?.Tracks?.Select( track => track?.Sound ).FirstOrDefault( candidate => candidate.IsValid() );
		if ( file is null )
			return "Missing Sound Event";
		var assetName = AssetSystem.FindByPath( file.ResourcePath )?.Name;
		return !string.IsNullOrWhiteSpace( assetName ) ? assetName : file.ResourceName ?? "Embedded Sound Event";
	}

	internal static string InstrumentDescription( AudioInstrument instrument )
	{
		var sound = instrument?.Sound;
		if ( !string.IsNullOrWhiteSpace( sound?.ResourcePath ) )
			return sound.ResourcePath;
		return sound?.Tracks?.Select( track => track?.Sound ).FirstOrDefault( file => file.IsValid() )?.ResourcePath ?? "Missing Sound Event";
	}
}

internal sealed class AudioTimelineBackgroundItem : GraphicsItem
{
	private readonly AudioTimelineView _view;

	public AudioTimelineBackgroundItem( AudioTimelineView view )
	{
		_view = view;
		ZIndex = -10;
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.WindowBackground );
		Paint.DrawRect( LocalRect );
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( new Rect( 0f, 0f, LocalRect.Width, AudioTimelineView.RulerHeight ) );

		foreach ( var layout in _view.TrackLayouts )
		{
			var target = !layout.IsMaster && layout.Index - 1 == _view.ReorderTargetRegularIndex;
			Paint.SetBrush( target
				? Theme.Primary.WithAlpha( 0.16f )
				: (layout.Index & 1) == 0 ? Theme.WidgetBackground : Theme.ControlBackground.WithAlpha( 0.6f ) );
			Paint.DrawRect( new Rect( 0f, layout.Top, LocalRect.Width, layout.Height ) );
			Paint.SetPen( Theme.Text.WithAlpha( 0.12f ) );
			Paint.DrawLine( new Vector2( 0f, layout.Top ), new Vector2( LocalRect.Width, layout.Top ) );
			if ( target )
			{
				Paint.SetPen( Theme.Primary, 2f );
				Paint.DrawRect( new Rect( 1f, layout.Top + 1f, LocalRect.Width - 2f, layout.Height - 2f ) );
			}
		}

		Paint.SetDefaultFont( 8f );
		var tickInterval = AudioTimelineScaleMath.CalculateTickInterval( _view.PixelsPerSecond );
		var tickCount = (int)MathF.Ceiling( _view.Duration / tickInterval );
		for ( var tickIndex = 0; tickIndex <= tickCount; tickIndex++ )
		{
			var seconds = tickIndex * tickInterval;
			var x = _view.PositionFromTime( seconds );
			Paint.SetPen( Theme.Text.WithAlpha( tickIndex % 5 == 0 ? 0.28f : 0.13f ) );
			Paint.DrawLine( new Vector2( x, 0f ), new Vector2( x, LocalRect.Height ) );
			Paint.SetPen( Theme.TextLight );
			Paint.DrawText( new Vector2( x + 4f, 7f ), $"{seconds:0}s" );
		}

		Paint.SetPen( Theme.Text.WithAlpha( 0.35f ) );
		Paint.DrawLine( new Vector2( AudioTimelineView.HeaderWidth, 0f ), new Vector2( AudioTimelineView.HeaderWidth, LocalRect.Height ) );
	}
}

internal sealed class AudioTrackHeaderItem : GraphicsItem
{
	private const float SliderLeft = 72f;
	private const float SliderWidth = 100f;
	private static readonly Rect TrackDragGrip = new( 4f, 4f, 22f, 22f );
	private static readonly Rect MuteButton = new( 174f, 5f, 18f, 18f );
	private static readonly Rect SoloButton = new( 196f, 5f, 18f, 18f );
	private static readonly Rect ClearButton = new( 174f, 5f, 40f, 18f );
	private readonly AudioTimelineView _view;
	private readonly AudioTrack _track;
	private readonly bool _isMaster;
	private readonly int _trackIndex;
	private JsonObject _dragStartState;
	private TrackControl _draggingControl;
	private int _dragTargetRegularIndex;
	private bool _draggingTrack;

	public AudioTrack Track => _track;

	public AudioTrackHeaderItem( AudioTimelineView view, AudioTrack track, bool isMaster, int trackIndex )
	{
		_view = view;
		_track = track;
		_isMaster = isMaster;
		_trackIndex = trackIndex;
		HoverEvents = true;
		Focusable = true;
		Cursor = CursorShape.Arrow;
		ToolTip = isMaster
			? "Drag volume or pitch. Drag the lower separator to resize the master track."
			: "Drag the grip to reorder. Use M/S to audition, the lower separator to resize, or the faders to mix.";
	}

	protected override void OnMousePressed( GraphicsMouseEvent e )
	{
		if ( e.LeftMouseButton )
		{
			if ( !_isMaster && TrackDragGrip.IsInside( e.LocalPosition ) )
			{
				_draggingTrack = true;
				_dragTargetRegularIndex = Math.Max( 0, _trackIndex - 1 );
				Movable = true;
				ZIndex = 30;
				_view.SelectTrack( _track );
				_view.PreviewTrackReorder( _dragTargetRegularIndex );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}
			if ( !_isMaster && MuteButton.IsInside( e.LocalPosition ) )
			{
				_view.ToggleTrackMute( _track );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}
			if ( !_isMaster && SoloButton.IsInside( e.LocalPosition ) )
			{
				_view.ToggleTrackSolo( _track );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}
			if ( _isMaster && _view.HasTrackAuditionOverrides && ClearButton.IsInside( e.LocalPosition ) )
			{
				_view.ClearTrackAudition();
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}
			_view.SelectTrack( _track );
			var overSlider = e.LocalPosition.x >= SliderLeft - 6f && e.LocalPosition.x <= SliderLeft + SliderWidth + 6f;
			_draggingControl = !overSlider ? TrackControl.None : e.LocalPosition.y switch
			{
				>= 27f and < 47f => TrackControl.Volume,
				>= 47f and < 68f => TrackControl.Pitch,
				_ => TrackControl.None
			};
			if ( _draggingControl != TrackControl.None )
			{
				_dragStartState = _view.CaptureEventState();
				UpdateControl( e.LocalPosition.x );
			}
			e.Accepted = true;
		}
		base.OnMousePressed( e );
	}

	protected override void OnMoved()
	{
		base.OnMoved();
		if ( !_draggingTrack )
			return;
		Position = Position.WithX( 0f );
		_dragTargetRegularIndex = _view.RegularTrackIndexFromPosition( Position.y + Height * 0.5f );
		_view.PreviewTrackReorder( _dragTargetRegularIndex );
		Update();
	}

	protected override void OnMouseMove( GraphicsMouseEvent e )
	{
		base.OnMouseMove( e );
		if ( !_draggingTrack )
			Cursor = !_isMaster && TrackDragGrip.IsInside( e.LocalPosition ) ? CursorShape.SizeV : CursorShape.Arrow;
		if ( _draggingControl == TrackControl.None || !e.LeftMouseButton )
			return;
		UpdateControl( e.LocalPosition.x );
		e.Accepted = true;
	}

	protected override void OnMouseReleased( GraphicsMouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _draggingTrack )
		{
			_draggingTrack = false;
			Movable = false;
			ZIndex = 0;
			Position = Position.WithX( 0f ).WithY( _view.TrackTop( _track ) );
			e.Accepted = true;
			_view.CommitTrackReorder( _track, _dragTargetRegularIndex );
			return;
		}
		if ( _draggingControl == TrackControl.None )
			return;

		var title = _draggingControl == TrackControl.Volume ? "Change Track Volume" : "Change Track Pitch";
		var before = _dragStartState;
		_draggingControl = TrackControl.None;
		_dragStartState = null;
		e.Accepted = true;
		_view.CommitTrackChange( title, before );
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( !_isMaster && e.Key == KeyCode.Delete )
			_view.RemoveTrack( _track );
		base.OnKeyPress( e );
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Paint.HasMouseOver ? Theme.Primary.WithAlpha( 0.12f ) : Theme.ControlBackground.WithAlpha( 0.9f ) );
		Paint.DrawRect( LocalRect.Shrink( 1f ) );
		Paint.SetPen( Theme.Text );
		Paint.SetDefaultFont( 10f, 600 );
		Paint.DrawText( new Rect( _isMaster ? 10f : 30f, 4f, LocalRect.Width - (_isMaster ? 62f : 82f), 22f ), _isMaster ? "MASTER" : _track.Name, TextFlag.LeftCenter | TextFlag.SingleLine );
		if ( _isMaster )
		{
			if ( _view.HasTrackAuditionOverrides )
				DrawAuditionButton( ClearButton, "CLR", true );
		}
		else
		{
			DrawTrackDragGrip();
			DrawAuditionButton( MuteButton, "M", _view.IsTrackMuted( _track ) );
			DrawAuditionButton( SoloButton, "S", _view.IsTrackSoloed( _track ) );
		}
		DrawControl( "VOL", _track.Volume, 0f, 4f, 36f );
		DrawControl( "PITCH", _track.Pitch, 0.01f, 4f, 57f );
	}

	private void DrawTrackDragGrip()
	{
		Paint.ClearPen();
		Paint.SetBrush( _draggingTrack ? Theme.Primary : Theme.TextLight.WithAlpha( 0.65f ) );
		for ( var column = 0; column < 2; column++ )
		{
			for ( var row = 0; row < 3; row++ )
				Paint.DrawCircle( new Vector2( 10f + column * 6f, 9f + row * 5f ), 1.25f );
		}
	}

	private void DrawAuditionButton( Rect rect, string text, bool active )
	{
		Paint.ClearPen();
		Paint.SetBrush( active ? Theme.Primary : Theme.WidgetBackground );
		Paint.DrawRect( rect, 2f );
		Paint.SetPen( active ? Color.White : Theme.TextLight );
		Paint.SetDefaultFont( 7f, 700 );
		Paint.DrawText( rect, text, TextFlag.Center );
	}

	private void DrawControl( string label, float value, float minimum, float maximum, float y )
	{
		var fraction = MathX.LerpInverse( value, minimum, maximum, true );
		Paint.SetDefaultFont( 7f, 600 );
		Paint.SetPen( Theme.TextLight );
		Paint.DrawText( new Rect( 10f, y - 9f, 56f, 18f ), label, TextFlag.LeftCenter | TextFlag.SingleLine );
		Paint.ClearPen();
		Paint.SetBrush( Theme.Text.WithAlpha( 0.16f ) );
		Paint.DrawRect( new Rect( SliderLeft, y - 2f, SliderWidth, 4f ), 2f );
		Paint.SetBrush( Theme.Primary.WithAlpha( 0.75f ) );
		Paint.DrawRect( new Rect( SliderLeft, y - 2f, SliderWidth * fraction, 4f ), 2f );
		Paint.DrawCircle( new Vector2( SliderLeft + SliderWidth * fraction, y ), 4f );
		Paint.SetPen( Theme.Text );
		Paint.SetDefaultFont( 8f );
		Paint.DrawText( new Rect( 178f, y - 9f, 36f, 18f ), value.ToString( "0.00" ), TextFlag.RightCenter | TextFlag.SingleLine );
	}

	private void UpdateControl( float localX )
	{
		var fraction = ((localX - SliderLeft) / SliderWidth).Clamp( 0f, 1f );
		if ( _draggingControl == TrackControl.Volume )
			_track.Volume = MathX.LerpTo( 0f, 4f, fraction ).SnapToGrid( 0.01f );
		else if ( _draggingControl == TrackControl.Pitch )
			_track.Pitch = MathX.LerpTo( 0.01f, 4f, fraction ).SnapToGrid( 0.01f );
		Update();
		_view.PreviewTrackChange();
	}

	private enum TrackControl
	{
		None,
		Volume,
		Pitch
	}
}

internal sealed class AudioTimelinePlayheadItem : GraphicsItem
{
	private readonly AudioTimelineView _view;

	public AudioTimelinePlayheadItem( AudioTimelineView view )
	{
		_view = view;
		Movable = true;
		ZIndex = -1;
		Cursor = CursorShape.SizeH;
	}

	protected override void OnMoved()
	{
		base.OnMoved();
		Position = new Vector2( Position.x.Clamp( AudioTimelineView.HeaderWidth - 4f, _view.SceneRect.Width ), 0f );
		_view.MovePlayhead( Position.x + 4f );
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.Red.WithAlpha( 0.85f ) );
		Paint.DrawRect( new Rect( 3f, 0f, 2f, LocalRect.Height ) );
		Paint.DrawRect( new Rect( 0f, 0f, 8f, 8f ), 2f );
	}
}

internal sealed class AudioEventInspectorWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private readonly ScrollArea _scroll;
	private readonly Widget _canvas;
	private SerializedObject _serialized;
	private GameResource _owner;
	private CancellationTokenSource _tempoAnalysisCancellation;
	private TransitionRegion _tempoRegion;
	private Label _tempoStatus;
	private Button _applyTempo;
	private AudioTempoAnalysisSummary _tempoSummary;

	public AudioEventInspectorWidget( AudioEventEditorWindow window ) : base( window )
	{
		_window = window;
		WindowTitle = "Inspector";
		SetWindowIcon( "edit" );
		MinimumWidth = 360f;
		Layout = Layout.Column();
		_scroll = Layout.Add( new ScrollArea( this ), 1 );
		_canvas = new Widget( _scroll ) { Layout = Layout.Column() };
		_canvas.Layout.Margin = 6f;
		_canvas.Layout.Spacing = 6f;
		_scroll.Canvas = _canvas;
	}

	public void SetSelection( object selection )
	{
		Detach();
		_canvas.Layout.Clear( true );
		if ( selection is null )
			return;

		_owner = selection is SoundEventResource { EmbeddedResource: not null }
			? _window.Event
			: selection as GameResource ?? _window.Event;
		var title = selection switch
		{
			AudioEvent => "Audio Event",
			AudioTrack track => track.Name,
			AudioInstrument audioInstrument => AudioTimelineView.InstrumentName( audioInstrument ),
			SoundEventResource => "Sound Source / Variations",
			AudioParameter parameter => parameter.Name,
			_ => selection.GetType().Name
		};
		_canvas.Layout.Add( new Label( title ) { FixedHeight = 26f } );

		_serialized = selection.GetSerialized();
		_serialized.OnPropertyFinishEdit += OnPropertyFinishedEdit;
		if ( selection is AudioParameter selectedParameter )
			_window.RegisterReferencedResource( selectedParameter );
		if ( selection is SoundEventResource selectedSound && selectedSound.EmbeddedResource is null )
			_window.RegisterReferencedResource( selectedSound );
		var sheet = new ControlSheet { IncludePropertyNames = true };
		sheet.AddObject( _serialized, property => AudioTimelineInspectorUx.ShowInGenericSheet( selection, property.Name ) );
		_canvas.Layout.Add( sheet );
		AddTimelineControlEditors( selection );
		if ( selection is TransitionRegion transitionRegion
			&& transitionRegion.Quantization is TransitionQuantization.NextBeat or TransitionQuantization.NextBar )
			AddTempoAssistant( transitionRegion );

		if ( selection is DestinationMarker destination )
		{
			var references = (_window.Event.TransitionMarkers?.Where( marker => marker.DestinationMarkerId == destination.Id )
				.Select( marker => marker.Name ?? marker.Id.ToString() ) ?? Enumerable.Empty<string>())
				.Concat( _window.Event.TransitionRegions?.Where( region => region.DestinationMarkerId == destination.Id )
					.Select( region => region.Name ?? region.Id.ToString() ) ?? Enumerable.Empty<string>() )
				.ToArray();
			var referenceText = references.Length == 0
				? "Referenced by: none"
				: $"Referenced by: {string.Join( ", ", references )}";
			_canvas.Layout.Add( new Label( referenceText ) { WordWrap = true } );
		}

		if ( selection is AudioInstrument instrument && instrument.Sound is not null )
		{
			var soundHeader = _canvas.Layout.AddRow();
			soundHeader.Add( new Label( "Sound Source / Variations" ), 1 );
			switch ( AudioTimelineInspectorUx.ResolveSoundSourceAccess( instrument.Sound ) )
			{
				case AudioSoundSourceAccess.EditEmbedded:
					var edit = soundHeader.Add( new Button( "Edit", "tune" ) );
					edit.Clicked += () => _window.SelectObject( instrument.Sound );
					break;
				case AudioSoundSourceAccess.OpenExternal:
					var open = soundHeader.Add( new Button( "Open Asset", "open_in_new" ) );
					open.Clicked += () => _window.OpenSoundAsset( instrument.Sound );
					break;
			}
		}

		_canvas.Layout.AddStretchCell();
		_canvas.Update();
	}

	private void AddTimelineControlEditors( object selection )
	{
		switch ( selection )
		{
			case DestinationMarker destination:
				AddSecondsEditor( "Position", destination.Position, "Set Destination Position", value => destination.Position = value );
				break;
			case TransitionMarker marker:
				AddSecondsEditor( "Position", marker.Position, "Set Transition Marker Position", value => marker.Position = value );
				AddDestinationEditor( marker.DestinationMarkerId, value => marker.DestinationMarkerId = value );
				break;
			case TransitionRegion region:
				AddSecondsEditor( "Start", region.Start, "Set Transition Region Start", value => region.Start = value );
				AddSecondsEditor( "End", region.End, "Set Transition Region End", value => region.End = value );
				AddDestinationEditor( region.DestinationMarkerId, value => region.DestinationMarkerId = value );
				break;
			case LoopRegion loop:
				AddSecondsEditor( "Start", loop.Start, "Set Loop Start", value => loop.Start = value );
				AddSecondsEditor( "End", loop.End, "Set Loop End", value => loop.End = value );
				break;
		}
	}

	private void AddSecondsEditor( string title, TimelineTime current, string undoTitle, Action<TimelineTime> assign )
	{
		var row = _canvas.Layout.AddRow();
		row.Spacing = 6f;
		row.Add( new Label( title ) { MinimumWidth = 140f } );
		var edit = row.Add( new LineEdit( current.Seconds.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture ) ), 1 );
		var editSession = new AudioTimelineSecondsEditSession();
		edit.ToolTip = "Timeline position in seconds.";
		row.Add( new Label( "seconds" ) { MinimumWidth = 52f } );
		edit.TextEdited += _ => editSession.MarkUserEdit();
		edit.EditingFinished += () =>
		{
			if ( !editSession.TryGetEditedValue( edit.Text, out var value ) )
			{
				if ( editSession.HasUserEdit )
					edit.Text = current.Seconds.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );
				return;
			}

			_window.MutateEvent( undoTitle, () => assign( value ) );
		};
	}

	private void AddDestinationEditor( Guid selectedId, Action<Guid> assign )
	{
		var row = _canvas.Layout.AddRow();
		row.Spacing = 6f;
		row.Add( new Label( "Destination" ) { MinimumWidth = 140f } );
		var choices = AudioTimelineInspectorUx.BuildDestinationChoices( _window.Event );
		if ( choices.Count == 0 )
		{
			row.Add( new Label( "Create a Destination Marker first." ), 1 );
			return;
		}

		var combo = row.Add( new ComboBox( this ), 1 );
		foreach ( var choice in choices )
		{
			var captured = choice;
			combo.AddItem(
				choice.Label,
				onSelected: () => _window.MutateEvent( "Set Transition Destination", () => assign( captured.Id ) ),
				selected: choice.Id == selectedId );
		}
	}

	private void AddTempoAssistant( TransitionRegion region )
	{
		_tempoRegion = region;
		var header = _canvas.Layout.AddRow();
		header.Spacing = 6f;
		header.Add( new Label( "Tempo Assistance" ), 1 );
		var analyze = header.Add( new Button( "Analyze Again", "refresh" ) );
		analyze.Clicked += () => _ = AnalyzeTempoAsync( region );

		_tempoStatus = _canvas.Layout.Add( new Label() { WordWrap = true } );
		_applyTempo = _canvas.Layout.Add( new Button( "Apply Tempo", "check" ) );
		_applyTempo.Enabled = false;
		_applyTempo.Clicked += ApplyDetectedTempo;

		var tempo = _window.Event.TempoMap?.BeatsPerMinute ?? 0f;
		var meterMessage = (_window.Event.TempoMap?.BeatsPerBar ?? 0) > 0
			? ""
			: " Beats per bar is invalid; 4 is suggested and will be set when applying a detected tempo.";
		if ( AudioTempoAnalyzer.IsPlausibleTempo( tempo ) )
		{
			_tempoStatus.Text = $"Authored tempo is {tempo:0.##} BPM. Analysis only loads decoded samples when requested.{meterMessage}";
		}
		else
		{
			_tempoStatus.Text = $"Authored tempo ({tempo:0.##} BPM) is outside the 40–240 BPM detection range. Analyzing overlapping clips…{meterMessage}";
			_ = AnalyzeTempoAsync( region );
		}
	}

	private async Task AnalyzeTempoAsync( TransitionRegion region )
	{
		_tempoAnalysisCancellation?.Cancel();
		_tempoAnalysisCancellation?.Dispose();
		_tempoAnalysisCancellation = new CancellationTokenSource();
		var cancellation = _tempoAnalysisCancellation;
		_tempoSummary = null;
		_applyTempo.Enabled = false;
		_tempoStatus.Text = "Loading and analyzing overlapping audio clips…";

		try
		{
			var summary = await AudioTempoAnalysisService.AnalyzeAsync( _window.Event, region, cancellation.Token );
			if ( cancellation.IsCancellationRequested || !ReferenceEquals( _tempoRegion, region ) )
				return;

			_tempoSummary = summary;
			var clipLines = summary.Clips.Select( result =>
			{
				var estimate = result.Estimate;
				if ( !string.IsNullOrWhiteSpace( estimate.Failure ) )
					return $"{result.Source}: {estimate.Failure}";
				var ambiguity = estimate.HalfDoubleTimeAmbiguity ? ", half/double-time ambiguity" : "";
				return $"{result.Source}: {estimate.BeatsPerMinute:0.##} BPM, {estimate.Confidence:P0} confidence{ambiguity}";
			} );
			_tempoStatus.Text = string.Join( "\n", new[] { summary.Message }.Concat( clipLines ) );
			_applyTempo.Enabled = summary.SuggestedBeatsPerMinute.HasValue;
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception exception )
		{
			if ( !cancellation.IsCancellationRequested )
				_tempoStatus.Text = $"Tempo analysis failed: {exception.Message}";
		}
	}

	private void ApplyDetectedTempo()
	{
		if ( _tempoSummary?.SuggestedBeatsPerMinute is not float beatsPerMinute )
			return;

		_window.MutateEvent( "Apply Detected Tempo", () =>
		{
			_window.Event.TempoMap ??= new AudioTempoMap();
			_window.Event.TempoMap.BeatsPerMinute = beatsPerMinute;
			if ( _window.Event.TempoMap.BeatsPerBar <= 0 )
				_window.Event.TempoMap.BeatsPerBar = 4;
		} );
	}

	private void OnPropertyFinishedEdit( SerializedProperty property )
	{
		_window.InspectorChanged( _owner );
	}

	private void Detach()
	{
		_tempoAnalysisCancellation?.Cancel();
		_tempoAnalysisCancellation?.Dispose();
		_tempoAnalysisCancellation = null;
		_tempoRegion = null;
		_tempoStatus = null;
		_applyTempo = null;
		_tempoSummary = null;
		if ( _serialized is not null )
			_serialized.OnPropertyFinishEdit -= OnPropertyFinishedEdit;
		_serialized = null;
		_owner = null;
	}

	public override void OnDestroyed()
	{
		Detach();
		base.OnDestroyed();
	}
}

internal sealed class AudioEventParameterWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private readonly ScrollArea _scroll;
	private readonly Widget _canvas;
	private readonly Dictionary<string, ParameterPreviewValue> _values = new( StringComparer.OrdinalIgnoreCase );
	private AudioEvent _event;

	public AudioEventParameterWidget( AudioEventEditorWindow window ) : base( window )
	{
		_window = window;
		WindowTitle = "Parameters";
		SetWindowIcon( "tune" );
		Layout = Layout.Column();
		var toolbar = Layout.AddRow();
		toolbar.Add( new Label( "Event Parameters" ), 1 );
		var add = toolbar.Add( new Button( "Add", "add" ) );
		add.Clicked += _window.AddParameter;
		var create = toolbar.Add( new Button( "New", "note_add" ) );
		create.Clicked += _window.CreateParameter;
		_scroll = Layout.Add( new ScrollArea( this ), 1 );
		_canvas = new Widget( _scroll ) { Layout = Layout.Column() };
		_canvas.Layout.Margin = 4f;
		_canvas.Layout.Spacing = 4f;
		_scroll.Canvas = _canvas;
	}

	public void SetEvent( AudioEvent audioEvent )
	{
		_event = audioEvent;
		_canvas.Layout.Clear( true );
		if ( audioEvent?.Parameters is null )
			return;

		var parameters = audioEvent.Parameters.Where( parameter => parameter is not null ).ToArray();
		var currentNames = parameters.Select( parameter => parameter.Name ?? string.Empty )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );
		foreach ( var stale in _values.Keys.Where( name => !currentNames.Contains( name ) ).ToArray() )
			_values.Remove( stale );

		foreach ( var parameter in parameters )
		{
			var parameterKey = parameter.Name ?? string.Empty;
			if ( !_values.TryGetValue( parameterKey, out var value ) )
			{
				value = parameter.Type == AudioParameterType.Labeled
					? new ParameterPreviewValue( 0f, parameter.DefaultLabel )
					: new ParameterPreviewValue( parameter.DefaultValue, null );
			}
			value = NormalizeValue( parameter, value );
			_values[parameterKey] = value;

			var row = new Widget( _canvas ) { Layout = Layout.Row(), FixedHeight = 30f };
			var select = row.Layout.Add( new Button( parameter.Name ) );
			select.MinimumWidth = 140f;
			select.Clicked += () => _window.SelectObject( parameter );

			if ( parameter.Type == AudioParameterType.Labeled )
			{
				var combo = row.Layout.Add( new ComboBox( row ), 1 );
				foreach ( var label in parameter.Labels )
					combo.AddItem( label, selected: string.Equals( label, value.Label, StringComparison.OrdinalIgnoreCase ) );
				combo.ItemChanged += () =>
				{
					_values[parameterKey] = value with { Label = combo.CurrentText };
					_window.SetPreviewParameter( parameter, 0f, combo.CurrentText );
				};
			}
			else
			{
				var slider = row.Layout.Add( new FloatSlider( row )
				{
					Minimum = parameter.Minimum,
					Maximum = parameter.Maximum,
					Step = parameter.Type == AudioParameterType.Discrete ? 1f : 0.01f,
					Value = value.Number
				}, 1 );
				var readout = row.Layout.Add( new Label( value.Number.ToString( "0.00" ) ) { MinimumWidth = 64f } );
				slider.OnValueEdited = () =>
				{
					_values[parameterKey] = value with { Number = slider.Value };
					readout.Text = slider.Value.ToString( parameter.Type == AudioParameterType.Discrete ? "0" : "0.00" );
					_window.SetPreviewParameter( parameter, slider.Value, null );
				};
			}

			var rename = row.Layout.Add( new IconButton( "edit" ) { ToolTip = "Rename parameter" } );
			rename.OnClick = () => _window.RenameParameter( parameter );
			var remove = row.Layout.Add( new IconButton( "delete" ) { ToolTip = "Remove parameter and its conditions/automation" } );
			remove.OnClick = () => _window.RemoveParameter( parameter );
			_canvas.Layout.Add( row );
		}
		_canvas.Layout.AddStretchCell();
		_canvas.Update();
	}

	public void ApplyToPreview( AudioEventInstance preview )
	{
		if ( preview is null || _event?.Parameters is null )
			return;
		foreach ( var parameter in _event.Parameters.Where( parameter => parameter is not null ) )
		{
			if ( !_values.TryGetValue( parameter.Name ?? string.Empty, out var value ) )
				continue;
			value = NormalizeValue( parameter, value );
			_values[parameter.Name ?? string.Empty] = value;
			if ( parameter.Type == AudioParameterType.Labeled )
			{
				if ( !string.IsNullOrWhiteSpace( value.Label ) )
					preview.SetParameterByNameWithLabel( parameter.Name, value.Label );
			}
			else
				preview.SetParameterByName( parameter.Name, value.Number );
		}
	}

	private static ParameterPreviewValue NormalizeValue( AudioParameter parameter, ParameterPreviewValue value )
	{
		if ( parameter.Type == AudioParameterType.Labeled )
		{
			var label = parameter.Labels?.FirstOrDefault( candidate => string.Equals( candidate, value.Label, StringComparison.OrdinalIgnoreCase ) )
				?? parameter.Labels?.FirstOrDefault( candidate => string.Equals( candidate, parameter.DefaultLabel, StringComparison.OrdinalIgnoreCase ) )
				?? parameter.Labels?.FirstOrDefault();
			return new ParameterPreviewValue( 0f, label );
		}

		var number = float.IsFinite( value.Number ) ? value.Number : parameter.DefaultValue;
		number = number.Clamp( parameter.Minimum, parameter.Maximum );
		if ( parameter.Type == AudioParameterType.Discrete )
			number = MathF.Round( number );
		return new ParameterPreviewValue( number, null );
	}

	private sealed record ParameterPreviewValue( float Number, string Label );
}

internal sealed class AudioParameterSheetWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private readonly AudioParameter _parameter;
	private Layout _automationAddRow;

	public AudioParameterSheetWidget( AudioEventEditorWindow window, AudioEvent audioEvent, AudioParameter parameter ) : base( window )
	{
		_window = window;
		_parameter = parameter;
		Layout = Layout.Column();
		Layout.Margin = 8f;
		Layout.Spacing = 6f;
		var header = Layout.AddRow();
		header.Add( new Label( $"{parameter.Name} automation and conditions" ), 1 );
		if ( parameter.Type == AudioParameterType.Labeled )
		{
			foreach ( var entry in EnumerateConditions( audioEvent, parameter ) )
				Layout.Add( new Label( $"{entry.Label}: {(entry.Condition.Invert ? "NOT " : string.Empty)}{entry.Condition.Label}" ) );
			Layout.Add( new Label( "Select an instrument and use the inspector to add or edit labeled conditions." ) );
			Layout.AddStretchCell();
			return;
		}

		foreach ( var entry in EnumerateAutomations( audioEvent, parameter ) )
		{
			var automationHeader = Layout.AddRow();
			automationHeader.Add( new Label( entry.Label ), 1 );
			var remove = automationHeader.Add( new IconButton( "delete" ) { ToolTip = "Remove automation" } );
			remove.OnClick = () => window.RemoveAutomation( entry.Track, entry.Instrument, entry.Automation );
			Layout.Add( new AudioAutomationCurveWidget( window, parameter, entry.Automation ), 1 );
		}

		_automationAddRow = Layout.AddRow();
		RebuildAutomationAddRow();

		foreach ( var entry in EnumerateConditions( audioEvent, parameter ) )
		{
			Layout.Add( new Label( $"{entry.Label} condition" ) );
			Layout.Add( new AudioConditionRegionWidget( window, parameter, entry.Condition ), 0 );
		}
		Layout.AddStretchCell();
	}

	public void SetSelection( object selection )
	{
		if ( _automationAddRow is not null )
			RebuildAutomationAddRow();
	}

	private void RebuildAutomationAddRow()
	{
		_automationAddRow.Clear( true );
		var selectedTrack = _window.Selection as AudioTrack;
		var selectedInstrument = _window.Selection as AudioInstrument;
		var selectedAutomations = selectedInstrument?.Automations ?? selectedTrack?.Automations;
		_automationAddRow.Add( new Label( selectedAutomations is null
			? "Select a track or instrument to add automation."
			: $"Add {_parameter.Name} automation to the selected object:" ), 1 );
		if ( selectedAutomations is null )
			return;

		foreach ( var target in new[] { AudioAutomationTarget.Volume, AudioAutomationTarget.Pitch } )
		{
			if ( selectedAutomations.Any( automation => automation.Target == target
				&& ReferenceEquals( automation.Parameter, _parameter ) ) )
				continue;
			var add = _automationAddRow.Add( new Button( target.ToString(), target == AudioAutomationTarget.Volume ? "volume_up" : "speed" ) );
			add.Clicked += () => _window.AddAutomation( selectedTrack, selectedInstrument, _parameter, target );
		}
	}

	private static IEnumerable<(string Label, AudioTrack Track, AudioInstrument Instrument, AudioAutomation Automation)> EnumerateAutomations( AudioEvent audioEvent, AudioParameter parameter )
	{
		if ( audioEvent.MasterTrack is not null )
		{
			foreach ( var automation in audioEvent.MasterTrack.Automations?.Where( automation => automation is not null && ReferenceEquals( automation.Parameter, parameter ) ) ?? Enumerable.Empty<AudioAutomation>() )
				yield return ($"Master / {automation.Target}", audioEvent.MasterTrack, null, automation);
			foreach ( var instrument in audioEvent.MasterTrack.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
			{
				foreach ( var automation in instrument.Automations?.Where( automation => automation is not null && ReferenceEquals( automation.Parameter, parameter ) ) ?? Enumerable.Empty<AudioAutomation>() )
					yield return ($"Master / {AudioTimelineView.InstrumentName( instrument )} / {automation.Target}", null, instrument, automation);
			}
		}
		foreach ( var track in audioEvent.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
		{
			foreach ( var automation in track.Automations?.Where( automation => automation is not null && ReferenceEquals( automation.Parameter, parameter ) ) ?? Enumerable.Empty<AudioAutomation>() )
				yield return ($"{track.Name} / {automation.Target}", track, null, automation);
			foreach ( var instrument in track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
			{
				foreach ( var automation in instrument.Automations?.Where( automation => automation is not null && ReferenceEquals( automation.Parameter, parameter ) ) ?? Enumerable.Empty<AudioAutomation>() )
					yield return ($"{track.Name} / {AudioTimelineView.InstrumentName( instrument )} / {automation.Target}", null, instrument, automation);
			}
		}
	}

	private static IEnumerable<(string Label, AudioParameterCondition Condition)> EnumerateConditions( AudioEvent audioEvent, AudioParameter parameter )
	{
		foreach ( var condition in audioEvent.MasterTrack?.Conditions?.Where( condition => condition is not null && ReferenceEquals( condition.Parameter, parameter ) ) ?? Enumerable.Empty<AudioParameterCondition>() )
			yield return ("Master", condition);
		foreach ( var instrument in audioEvent.MasterTrack?.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
		{
			foreach ( var condition in instrument.Conditions?.Where( condition => condition is not null && ReferenceEquals( condition.Parameter, parameter ) ) ?? Enumerable.Empty<AudioParameterCondition>() )
				yield return ($"Master / {AudioTimelineView.InstrumentName( instrument )}", condition);
		}
		foreach ( var track in audioEvent.Tracks?.Where( track => track is not null ) ?? Enumerable.Empty<AudioTrack>() )
		{
			foreach ( var condition in track.Conditions?.Where( condition => condition is not null && ReferenceEquals( condition.Parameter, parameter ) ) ?? Enumerable.Empty<AudioParameterCondition>() )
				yield return (track.Name, condition);
			foreach ( var instrument in track.Instruments?.Where( instrument => instrument is not null ) ?? Enumerable.Empty<AudioInstrument>() )
			{
				foreach ( var condition in instrument.Conditions?.Where( condition => condition is not null && ReferenceEquals( condition.Parameter, parameter ) ) ?? Enumerable.Empty<AudioParameterCondition>() )
					yield return ($"{track.Name} / {AudioTimelineView.InstrumentName( instrument )}", condition);
			}
		}
	}
}

internal sealed class AudioAutomationCurveWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private readonly AudioParameter _parameter;
	private readonly AudioAutomation _automation;
	private AudioAutomationPoint _draggedPoint;
	private JsonObject _dragStartState;
	private float _dragOutputMaximum = 2f;

	public AudioAutomationCurveWidget( AudioEventEditorWindow window, AudioParameter parameter, AudioAutomation automation ) : base( window )
	{
		_window = window;
		_parameter = parameter;
		_automation = automation;
		MinimumHeight = 120f;
		MouseTracking = true;
		ToolTip = "Drag automation points. Double-click to add a point.";
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 3f );
		Paint.SetPen( Theme.Text.WithAlpha( 0.15f ) );
		for ( var i = 1; i < 4; i++ )
		{
			var x = LocalRect.Width * i / 4f;
			var y = LocalRect.Height * i / 4f;
			Paint.DrawLine( new Vector2( x, 0f ), new Vector2( x, LocalRect.Height ) );
			Paint.DrawLine( new Vector2( 0f, y ), new Vector2( LocalRect.Width, y ) );
		}

		if ( _automation.Points is null || _automation.Points.Count == 0 )
			return;
		Paint.SetPen( Theme.Primary, 2f );
		for ( var i = 1; i < _automation.Points.Count; i++ )
			Paint.DrawLine( ToCanvas( _automation.Points[i - 1] ), ToCanvas( _automation.Points[i] ) );
		Paint.SetBrush( Theme.Primary );
		foreach ( var point in _automation.Points )
			Paint.DrawCircle( ToCanvas( point ), 4f );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( !e.LeftMouseButton )
			return;
		var mousePosition = e.LocalPosition;
		_draggedPoint = _automation.Points?.OrderBy( point => Vector2.DistanceBetween( ToCanvas( point ), mousePosition ) ).FirstOrDefault();
		if ( _draggedPoint is not null && Vector2.DistanceBetween( ToCanvas( _draggedPoint ), e.LocalPosition ) > 12f )
			_draggedPoint = null;
		if ( _draggedPoint is not null )
		{
			_dragStartState = _window.CaptureEventState();
			_dragOutputMaximum = OutputMaximum;
		}
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		if ( _draggedPoint is null )
			return;
		var point = FromCanvas( e.LocalPosition, _dragOutputMaximum );
		var ordered = _automation.Points.OrderBy( candidate => candidate.Input ).ToArray();
		var index = Array.IndexOf( ordered, _draggedPoint );
		var inputGap = MathF.Max( 0.0001f, (_parameter.Maximum - _parameter.Minimum) * 0.001f );
		var minimumInput = index > 0 ? ordered[index - 1].Input + inputGap : _parameter.Minimum;
		var maximumInput = index >= 0 && index < ordered.Length - 1 ? ordered[index + 1].Input - inputGap : _parameter.Maximum;
		_draggedPoint.Input = minimumInput <= maximumInput
			? point.Input.Clamp( minimumInput, maximumInput )
			: (minimumInput + maximumInput) * 0.5f;
		_draggedPoint.Output = point.Output;
		Update();
		_window.PreviewAuthoredChange();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _draggedPoint is null )
			return;
		var before = _dragStartState;
		_draggedPoint = null;
		_dragStartState = null;
		_dragOutputMaximum = 2f;
		_window.CommitEventMutation( "Move Automation Point", before );
	}

	protected override void OnDoubleClick( MouseEvent e )
	{
		var point = FromCanvas( e.LocalPosition, OutputMaximum );
		var duplicateTolerance = MathF.Max( 0.0001f, (_parameter.Maximum - _parameter.Minimum) * 0.001f );
		if ( _automation.Points?.Any( candidate => MathF.Abs( candidate.Input - point.Input ) <= duplicateTolerance ) ?? false )
			return;
		_window.MutateEvent( "Add Automation Point", () =>
		{
			_automation.Points ??= new();
			_automation.Points.Add( point );
			_automation.Points = _automation.Points.OrderBy( candidate => candidate.Input ).ToList();
		} );
	}

	protected override void OnContextMenu( ContextMenuEvent e )
	{
		var localPosition = e.LocalPosition;
		var menu = new ContextMenu( this );
		var nearest = _automation.Points?
			.OrderBy( point => Vector2.DistanceBetween( ToCanvas( point ), localPosition ) )
			.FirstOrDefault();
		if ( nearest is not null && Vector2.DistanceBetween( ToCanvas( nearest ), localPosition ) <= 12f )
		{
			menu.AddHeading( $"Automation Point {_automation.Points.IndexOf( nearest ) + 1}" );
			if ( _automation.Points.Count > 2 )
				menu.AddOption( "Delete Point", "delete", () => _window.MutateEvent( "Delete Automation Point", () => _automation.Points.Remove( nearest ) ) );
		}
		else
		{
			var point = FromCanvas( localPosition, OutputMaximum );
			menu.AddOption( "Add Point Here", "add", () => _window.MutateEvent( "Add Automation Point", () =>
			{
				_automation.Points ??= new();
				_automation.Points.Add( point );
				_automation.Points = _automation.Points.OrderBy( candidate => candidate.Input ).ToList();
			} ) );
		}
		menu.AddOption( "Reset Curve to 1.00", "restart_alt", () => _window.MutateEvent( "Reset Automation Curve", () =>
		{
			_automation.Points = new()
			{
				new AudioAutomationPoint { Input = _parameter.Minimum, Output = 1f },
				new AudioAutomationPoint { Input = _parameter.Maximum, Output = 1f }
			};
		} ) );
		menu.OpenAt( e.ScreenPosition );
	}

	private Vector2 ToCanvas( AudioAutomationPoint point )
	{
		var x = MathX.LerpInverse( point.Input, _parameter.Minimum, _parameter.Maximum, true ) * LocalRect.Width;
		var y = LocalRect.Height - point.Output / OutputMaximum * LocalRect.Height;
		return new Vector2( x, y );
	}

	private float OutputMaximum => MathF.Max( 2f, _automation.Points?.Select( candidate => candidate.Output ).DefaultIfEmpty( 1f ).Max() ?? 2f );

	private AudioAutomationPoint FromCanvas( Vector2 position, float outputMaximum )
	{
		var input = MathX.LerpTo( _parameter.Minimum, _parameter.Maximum, (position.x / MathF.Max( 1f, LocalRect.Width )).Clamp( 0f, 1f ) );
		var output = (1f - position.y / MathF.Max( 1f, LocalRect.Height )).Clamp( 0f, 1f ) * outputMaximum;
		if ( _automation.Target == AudioAutomationTarget.Pitch )
			output = MathF.Max( 0.01f, output );
		return new AudioAutomationPoint { Input = input, Output = output };
	}
}

internal sealed class AudioConditionRegionWidget : Widget
{
	private readonly AudioEventEditorWindow _window;
	private readonly AudioParameter _parameter;
	private readonly AudioParameterCondition _condition;
	private JsonObject _dragStartState;
	private bool _movingMinimum;
	private bool _dragging;

	public AudioConditionRegionWidget( AudioEventEditorWindow window, AudioParameter parameter, AudioParameterCondition condition ) : base( window )
	{
		_window = window;
		_parameter = parameter;
		_condition = condition;
		FixedHeight = 54f;
		MouseTracking = true;
		ToolTip = "Drag either edge to edit the condition range.";
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 3f );
		var minimumX = ToCanvas( _condition.Minimum );
		var maximumX = ToCanvas( _condition.Maximum );
		Paint.SetBrush( Theme.Primary.WithAlpha( 0.32f ) );
		if ( _condition.Invert )
		{
			Paint.DrawRect( new Rect( 0f, 8f, MathF.Max( 0f, minimumX ), LocalRect.Height - 24f ), 3f );
			Paint.DrawRect( new Rect( maximumX, 8f, MathF.Max( 0f, LocalRect.Width - maximumX ), LocalRect.Height - 24f ), 3f );
		}
		else
		{
			Paint.DrawRect( new Rect( minimumX, 8f, MathF.Max( 2f, maximumX - minimumX ), LocalRect.Height - 24f ), 3f );
		}
		Paint.SetPen( Theme.Primary, 2f );
		Paint.DrawLine( new Vector2( minimumX, 5f ), new Vector2( minimumX, LocalRect.Height - 12f ) );
		Paint.DrawLine( new Vector2( maximumX, 5f ), new Vector2( maximumX, LocalRect.Height - 12f ) );
		Paint.SetDefaultFont( 8f );
		Paint.SetPen( Theme.TextLight );
		Paint.DrawText( new Rect( 4f, LocalRect.Height - 16f, LocalRect.Width - 8f, 14f ), $"{(_condition.Invert ? "NOT " : string.Empty)}{_condition.Minimum:0.##} – {_condition.Maximum:0.##}", TextFlag.Center );
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( !e.LeftMouseButton )
			return;
		var minimumDistance = MathF.Abs( e.LocalPosition.x - ToCanvas( _condition.Minimum ) );
		var maximumDistance = MathF.Abs( e.LocalPosition.x - ToCanvas( _condition.Maximum ) );
		_movingMinimum = minimumDistance <= maximumDistance;
		_dragStartState = _window.CaptureEventState();
		_dragging = true;
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		if ( !_dragging )
			return;
		var value = FromCanvas( e.LocalPosition.x );
		if ( _parameter.Type == AudioParameterType.Discrete )
			value = MathF.Round( value );
		if ( _movingMinimum )
			_condition.Minimum = MathF.Min( value, _condition.Maximum );
		else
			_condition.Maximum = MathF.Max( value, _condition.Minimum );
		Update();
		_window.PreviewAuthoredChange();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( !_dragging )
			return;
		_dragging = false;
		var before = _dragStartState;
		_dragStartState = null;
		_window.CommitEventMutation( "Edit Audio Condition", before );
	}

	private float ToCanvas( float value )
	{
		return MathX.LerpInverse( value, _parameter.Minimum, _parameter.Maximum, true ) * LocalRect.Width;
	}

	private float FromCanvas( float x )
	{
		return MathX.LerpTo( _parameter.Minimum, _parameter.Maximum, (x / MathF.Max( 1f, LocalRect.Width )).Clamp( 0f, 1f ) );
	}
}

internal sealed class AudioEventIssuesWidget : Widget
{
	private readonly Widget _canvas;

	public AudioEventIssuesWidget( AudioEventEditorWindow window ) : base( window )
	{
		WindowTitle = "Validation";
		SetWindowIcon( "rule" );
		Layout = Layout.Column();
		var scroll = Layout.Add( new ScrollArea( this ), 1 );
		_canvas = new Widget( scroll ) { Layout = Layout.Column() };
		_canvas.Layout.Margin = 6f;
		_canvas.Layout.Spacing = 3f;
		scroll.Canvas = _canvas;
	}

	public void SetValidation( AudioEventValidationResult validation )
	{
		_canvas.Layout.Clear( true );
		if ( validation is null )
			return;
		if ( validation.IsValid )
		{
			_canvas.Layout.Add( new Label( "Audio event is valid and ready to preview." ) );
		}
		else
		{
			foreach ( var issue in validation.Issues )
			{
				var label = _canvas.Layout.Add( new Label( $"{issue.Path}: {issue.Message}" ) );
				label.ToolTip = label.Text;
			}
		}
		_canvas.Layout.AddStretchCell();
		_canvas.Update();
	}
}

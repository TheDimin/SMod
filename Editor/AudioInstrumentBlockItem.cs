using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Sandbox.Helpers;
using Smod;

namespace Editor;

/// <summary>
/// Owns selection, movement, trimming, and fade-handle interaction for an authored audio instrument.
/// Rendering and waveform loading are deliberately delegated to focused collaborators.
/// </summary>
internal sealed class AudioInstrumentBlockItem : GraphicsItem
{
	private const int StaticTileWidth = 512;

	private readonly AudioTimelineView _view;
	private readonly AudioInstrumentWaveformPreview _waveformPreview;
	private readonly Dictionary<int, Pixmap> _staticTiles = new();
	private AudioTrack _track;
	private int _trackIndex;
	private int _dragTargetTrackIndex;
	private float _dragStartTime;
	private bool _dragging;
	private TrimEdge _trimEdge;
	private JsonObject _trimBefore;
	private float _trimDragSceneX;
	private float _trimOriginalStart;
	private float _trimOriginalEnd;
	private float _trimSourceDuration;
	private FadeEdge _fadeEdge;
	private JsonObject _fadeBefore;
	private float _fadeDragSceneX;
	private float _fadeOriginalDuration;
	private float _fadeInstrumentDuration;
	private FadeEdge _curveEdge;
	private JsonObject _curveBefore;
	private AudioInstrumentVisualState _visualState;
	private AudioInstrumentRenderCacheKey? _staticCacheKey;
	private bool _audible;

	public AudioInstrument Instrument { get; }
	public AudioTrack Track => _track;

	public AudioInstrumentBlockItem( AudioTimelineView view, AudioTrack track, AudioInstrument instrument, int trackIndex )
	{
		_view = view;
		_track = track;
		Instrument = instrument;
		_trackIndex = trackIndex;
		_audible = _view.IsTrackAudible( track );
		HoverEvents = true;
		Selectable = true;
		Movable = true;
		Focusable = true;
		Cursor = CursorShape.SizeAll;
		ToolTip = $"{AudioTimelineView.InstrumentDescription( instrument )}\nDrag the block to move it. Drag either edge to trim. Drag the top fade handles inward to fade volume.";
		_waveformPreview = new AudioInstrumentWaveformPreview( instrument.Sound, OnWaveformsLoaded );
		_ = _waveformPreview.LoadAsync();
	}

	private Guid previewChildId;
	private string previewChildEvent;
	internal void OpenActiveChild()
	{
		var id = liveState?.ChildInstanceId ?? previewChildId;
		var path = liveState?.ChildEvent ?? previewChildEvent;
		if ( string.IsNullOrWhiteSpace( path ) ) path = Instrument.SubEvents?.FirstOrDefault()?.Event?.ResourcePath;
		if ( !string.IsNullOrWhiteSpace( path ) ) AudioEventEditorWindow.OpenChildEvent( path, id );
	}

	public void SetPlaybackState( AudioInstrumentPlaybackState? state )
	{
		previewChildId = state?.ChildInstanceId ?? Guid.Empty;
		previewChildEvent = state?.ChildEvent;
		var next = AudioInstrumentVisualState.FromPlayback( state );
		if ( _visualState == next )
			return;
		_visualState = next;
		Update();
	}

    private AudioLiveVoice liveState;
    public AudioLiveVoice SetLiveStates(IReadOnlyList<AudioLiveVoice> states)
    {
        var selected=states.OrderByDescending(x=>x.State=="Playing").ThenBy(x=>x.IsReleaseTail).ThenByDescending(x=>x.Start).FirstOrDefault();
        var aggregate=selected is null ? null : selected with {Gain=states.Sum(x=>Math.Max(0,x.Gain))};
        SetLiveState(aggregate);
        if(states.Count>1)
            ToolTip=$"{selected.Track} | {states.Count} overlapping voices\nSum of voice gains: {aggregate.Gain:0.####}. Not measured output loudness.\n"
                +string.Join("\n",states.Select(x=>$"{(x.IsReleaseTail ? "Tail" : "Body")} | {x.State} | Gain {x.Gain:0.####} | Source {x.SourcePosition:0.000}s | {x.Source}"));
        else if(selected?.IsReleaseTail==true)ToolTip+="\nRetained release tail";
        return selected;
    }
    public void SetLiveState(AudioLiveVoice state)
    {
        liveState=state;
        if(state is null) {ToolTip=AudioTimelineView.InstrumentDescription(Instrument);SetPlaybackState(null);Update();return;}
        var source=Instrument.Sound?.Tracks.FirstOrDefault(x=>x.Sound?.ResourcePath==state.Source);
        SetPlaybackState(new AudioInstrumentPlaybackState(Track,Instrument,state.State is "Playing" or "Silent" or "Paused",state.State=="Scheduled",state.State=="Paused",state.SourcePosition,source,source?.Sound,state.ChildInstanceId,state.ChildEvent));
        var decibels=state.Gain>0 ? $"{20f*MathF.Log10(state.Gain):0.0} dB" : "silent";
        ToolTip=$"{state.Track} | {state.State}\nVoice gain (before mixer): {state.Gain:0.####} ({decibels}) | Pitch: {state.Pitch:0.###} | Source time: {state.SourcePosition:0.000}s\nFill scale: -60 to 0 dB gain. Not measured output loudness.\n{state.Source}";
        Update();
    }

	public void SetTrack( AudioTrack track, int trackIndex )
	{
		_track = track;
		_trackIndex = trackIndex;
		_dragTargetTrackIndex = trackIndex;
		RefreshAudibility();
	}

	public void RefreshAudibility()
	{
		var audible = _view.IsTrackAudible( _track );
		if ( _audible == audible )
			return;
		_audible = audible;
		InvalidateStaticContent();
	}

	public void InvalidateStaticContent()
	{
		_staticCacheKey = null;
		Update();
	}

	public void Resize( Vector2 size )
	{
		if ( Size == size )
			return;

		PrepareGeometryChange();
		Size = size;
		InvalidateStaticContent();
	}

	protected override void OnMousePressed( GraphicsMouseEvent e )
	{
        if(liveState is not null) {_view.SelectInstrument(Instrument);e.Accepted=true;return;}
		if ( e.LeftMouseButton )
		{
			_curveEdge = HitCurvePoint( e.LocalPosition );
			if ( _curveEdge != FadeEdge.None )
			{
				_curveBefore = _view.CaptureEventState();
				Movable = false;
				_view.SelectInstrument( Instrument );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}

			_fadeEdge = HitFadeEdge( e.LocalPosition );
			if ( _fadeEdge != FadeEdge.None )
			{
				_fadeBefore = _view.CaptureEventState();
				_fadeDragSceneX = e.ScenePosition.x;
				_fadeInstrumentDuration = MathF.Max( 0.01f, AudioTimelineView.EstimateDuration( Instrument ) );
				_fadeOriginalDuration = _fadeEdge == FadeEdge.Start ? Instrument.FadeInDuration : Instrument.FadeOutDuration;
				Movable = false;
				_view.SelectInstrument( Instrument );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}

			_trimEdge = e.LocalPosition.x <= 7f
				? TrimEdge.Start
				: e.LocalPosition.x >= Width - 7f ? TrimEdge.End : TrimEdge.None;
			if ( _trimEdge != TrimEdge.None )
			{
				_trimBefore = _view.CaptureEventState();
				_trimDragSceneX = e.ScenePosition.x;
				_trimOriginalStart = Instrument.TrimStart;
				_trimSourceDuration = Instrument.IsSubEvent ? AudioTimelineView.EstimateDuration( Instrument ) + Instrument.TrimStart : MathF.Max( 0.01f, _waveformPreview.Duration );
				_trimOriginalEnd = Instrument.TrimEnd > 0f ? MathF.Min( Instrument.TrimEnd, _trimSourceDuration ) : _trimSourceDuration;
				Movable = false;
				_view.SelectInstrument( Instrument );
				e.Accepted = true;
				base.OnMousePressed( e );
				return;
			}

			_dragStartTime = Instrument.StartTime;
			_dragTargetTrackIndex = _trackIndex;
			_dragging = true;
			_view.SelectInstrument( Instrument );
			e.Accepted = true;
		}
		base.OnMousePressed( e );
	}

	protected override void OnMouseMove( GraphicsMouseEvent e )
	{
		base.OnMouseMove( e );
		if ( _curveEdge != FadeEdge.None )
		{
			if ( !e.LeftMouseButton )
				return;

			var midpoint = ((Height - 3f - e.LocalPosition.y) / MathF.Max( 1f, Height - 6f )).Clamp( 0f, 1f ).SnapToGrid( 0.01f );
			if ( _curveEdge == FadeEdge.Start )
				Instrument.FadeInCurveMidpoint = midpoint;
			else
				Instrument.FadeOutCurveMidpoint = midpoint;
			_view.PreviewTrackChange();
			InvalidateStaticContent();
			e.Accepted = true;
			return;
		}
		if ( _fadeEdge != FadeEdge.None )
		{
			if ( !e.LeftMouseButton )
				return;

			var fadeDelta = (e.ScenePosition.x - _fadeDragSceneX) / _view.PixelsPerSecond;
			if ( _fadeEdge == FadeEdge.Start )
			{
				var maximum = MathF.Max( 0f, _fadeInstrumentDuration - Instrument.FadeOutDuration );
				Instrument.FadeInDuration = (_fadeOriginalDuration + fadeDelta).Clamp( 0f, maximum ).SnapToGrid( 0.01f );
			}
			else
			{
				var maximum = MathF.Max( 0f, _fadeInstrumentDuration - Instrument.FadeInDuration );
				Instrument.FadeOutDuration = (_fadeOriginalDuration - fadeDelta).Clamp( 0f, maximum ).SnapToGrid( 0.01f );
			}
			_view.PreviewTrackChange();
			InvalidateStaticContent();
			e.Accepted = true;
			return;
		}
		if ( _trimEdge == TrimEdge.None )
		{
			if ( HitCurvePoint( e.LocalPosition ) != FadeEdge.None )
				Cursor = CursorShape.SizeV;
			else
				Cursor = HitFadeEdge( e.LocalPosition ) != FadeEdge.None
					|| e.LocalPosition.x <= 7f
					|| e.LocalPosition.x >= Width - 7f
					? CursorShape.SizeH
					: CursorShape.SizeAll;
			return;
		}
		if ( !e.LeftMouseButton )
			return;

		var delta = (e.ScenePosition.x - _trimDragSceneX) / _view.PixelsPerSecond;
		if ( _trimEdge == TrimEdge.Start )
			Instrument.TrimStart = (_trimOriginalStart + delta).Clamp( 0f, MathF.Max( 0f, _trimOriginalEnd - 0.01f ) ).SnapToGrid( 0.01f );
		else
			Instrument.TrimEnd = (_trimOriginalEnd + delta).Clamp( Instrument.TrimStart + 0.01f, _trimSourceDuration ).SnapToGrid( 0.01f );

		Resize( Size.WithX( MathF.Max( 28f, AudioTimelineView.EstimateDuration( Instrument ) * _view.PixelsPerSecond ) ) );
		_view.PreviewTrackChange();
		InvalidateStaticContent();
		e.Accepted = true;
	}

	protected override void OnMoved()
	{
		base.OnMoved();
		var trackIndex = _dragging ? _view.TrackIndexFromPosition( Position.y + Height * 0.5f ) : _trackIndex;
		if ( trackIndex >= 0 )
			_dragTargetTrackIndex = trackIndex;
		Position = new Vector2(
			MathF.Max( AudioTimelineView.HeaderWidth, Position.x ),
			_view.InstrumentRowPosition( Math.Max( 0, _dragTargetTrackIndex ) ) );
		Resize( Size.WithY( _view.InstrumentRowHeight( Math.Max( 0, _dragTargetTrackIndex ) ) ) );
	}

	protected override void OnMouseReleased( GraphicsMouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _curveEdge != FadeEdge.None )
		{
			var before = _curveBefore;
			var title = _curveEdge == FadeEdge.Start ? "Curve Instrument Fade In" : "Curve Instrument Fade Out";
			_curveEdge = FadeEdge.None;
			_curveBefore = null;
			Movable = true;
			e.Accepted = true;
			_view.CommitInstrumentEdit( title, before, Instrument );
			return;
		}
		if ( _fadeEdge != FadeEdge.None )
		{
			var before = _fadeBefore;
			var title = _fadeEdge == FadeEdge.Start ? "Fade Instrument In" : "Fade Instrument Out";
			_fadeEdge = FadeEdge.None;
			_fadeBefore = null;
			Movable = true;
			e.Accepted = true;
			_view.CommitInstrumentEdit( title, before, Instrument );
			return;
		}
		if ( _trimEdge != TrimEdge.None )
		{
			var before = _trimBefore;
			var title = _trimEdge == TrimEdge.Start ? "Trim Instrument Start" : "Trim Instrument End";
			_trimEdge = TrimEdge.None;
			_trimBefore = null;
			Movable = true;
			e.Accepted = true;
			_view.CommitInstrumentEdit( title, before, Instrument );
			return;
		}
		if ( !_dragging )
			return;

		_dragging = false;
		var newTime = _view.TimeFromPosition( Position.x ).SnapToGrid( 0.01f );
		var targetTrack = _view.TrackAtIndex( _dragTargetTrackIndex ) ?? _track;
		Position = new Vector2( _view.PositionFromTime( _dragStartTime ), _view.InstrumentRowPosition( _trackIndex ) );
		Resize( Size.WithY( _view.InstrumentRowHeight( _trackIndex ) ) );
		e.Accepted = true;
		_view.InstrumentMoved( _track, targetTrack, Instrument, _view.PositionFromTime( newTime ) );
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Delete )
			_view.RemoveInstrument( _track, Instrument );
		base.OnKeyPress( e );
	}

	protected override void OnPaint()
	{
		var hovered = Paint.HasMouseOver;
		var selected = Paint.HasSelected;
		var key = CreateStaticCacheKey( hovered, selected );
		EnsureStaticCache( key );

		foreach ( var pair in _staticTiles )
		{
			var tile = pair.Value;
			Paint.Draw( new Rect( pair.Key * StaticTileWidth, 0f, tile.Width, tile.Height ), tile );
		}

		DrawLiveGainLayer();
		if ( Instrument.IsSubEvent && (liveState?.ChildEvent ?? previewChildEvent) is { } childPath )
		{
			Paint.SetPen( Theme.Text );
			Paint.SetDefaultFont( 8f, 600 );
			Paint.DrawText( new Rect( 7f, 36f, MathF.Max( 8f, Width - 14f ), 18f ), $"Active: {System.IO.Path.GetFileNameWithoutExtension( childPath )}", TextFlag.LeftCenter | TextFlag.SingleLine );
		}
		AudioInstrumentBlockRenderer.DrawOverlay(
			LocalRect,
			Instrument,
			_waveformPreview,
			_visualState,
			_audible,
			hovered,
			selected );

	}

    private void DrawLiveGainLayer()
    {
        if(liveState is null || Width<=6 || Height<=24)return;
        // A dB scale keeps quiet stem gains legible. Never present this as an audio peak meter.
        var gain=liveState.State=="Playing" && float.IsFinite(liveState.Gain) ? Math.Max(0,liveState.Gain) : 0;
        var fill=gain>0 ? Math.Clamp((20f*MathF.Log10(gain)+60f)/60f,0,1) : 0;
        var body=new Rect(3,21,Width-6,Height-24);
        Paint.ClearPen();Paint.SetBrush(Color.Black.WithAlpha(.18f));Paint.DrawRect(body);
        if(fill<=0)return;
        var color=gain>1 ? Theme.Yellow : Theme.Green;
        var height=body.Height*fill;
        var top=body.Top+body.Height-height;
        Paint.SetBrush(color.WithAlpha(.24f));Paint.DrawRect(new Rect(body.Left,top,body.Width,height));
        Paint.SetPen(color.WithAlpha(.85f),1f);
        Paint.DrawLine(new Vector2(body.Left,top),new Vector2(body.Right,top));
    }

	private void OnWaveformsLoaded()
	{
		if ( !IsValid )
			return;
		InvalidateStaticContent();
		_view.WaveformLoaded( this );
	}

	private AudioInstrumentRenderCacheKey CreateStaticCacheKey( bool hovered, bool selected )
	{
		var width = Math.Max( 1, (int)MathF.Ceiling( Width ) );
		var height = Math.Max( 1, (int)MathF.Ceiling( Height ) );
		var contentSignature = HashCode.Combine(
			AudioTimelineView.InstrumentName( Instrument ),
			Instrument.Conditions?.Count ?? 0,
			Instrument.Automations?.Count ?? 0,
			HashCode.Combine( ReferenceSignature( Instrument.Sound ), Instrument.SubEvents?.Count ?? 0 ) );
		var selectedVariationSignature = HashCode.Combine(
			ReferenceSignature( _visualState.SelectedTrack ),
			ReferenceSignature( _visualState.SelectedSound ) );
		var themeSignature = HashCode.Combine(
			Theme.Primary,
			Theme.Text,
			Theme.TextLight,
			Theme.Green,
			Theme.Yellow,
			Theme.ControlBackground,
			Theme.WidgetBackground );

		return AudioInstrumentRenderCacheKey.Create(
			width,
			height,
			_waveformPreview.Revision,
			Instrument.TrimStart,
			Instrument.TrimEnd,
			Instrument.FadeInDuration,
			Instrument.FadeInCurveMidpoint,
			Instrument.FadeOutDuration,
			Instrument.FadeOutCurveMidpoint,
			contentSignature,
			_audible,
			_visualState.IsPlaying,
			_visualState.IsPending,
			_visualState.IsPaused,
			selectedVariationSignature,
			themeSignature,
			Position.x,
			Position.y,
			_visualState.PlaybackPosition,
			hovered,
			selected );
	}

 internal long CheckStaticTiles()
 {
  EnsureStaticCache(CreateStaticCacheKey(false,false));
  return _staticTiles.Values.Sum(tile=>(long)tile.Width*tile.Height*4);
 }

	private void EnsureStaticCache( AudioInstrumentRenderCacheKey key )
	{
		if ( _staticCacheKey != key )
		{
			_staticCacheKey = key;
			_staticTiles.Clear();
		}
        var left = _view.ToScene(_view.ContentRect.TopLeft).x - Position.x;
        var right = _view.ToScene(_view.ContentRect.BottomRight).x - Position.x;
        var range = AudioVisibleTileRange.ForViewport(key.Width, left, right, StaticTileWidth);
        foreach(var index in _staticTiles.Keys.ToArray())
            if(index < range.First || index >= range.End)_staticTiles.Remove(index);
		for ( var index = range.First; index < range.End; index++ )
		{
            if(_staticTiles.ContainsKey(index))continue;
            var tileLeft = index * StaticTileWidth;
			var tileWidth = Math.Min( StaticTileWidth, key.Width - tileLeft );
			var tile = new Pixmap( tileWidth, key.Height );
			tile.Clear( Color.Transparent );
			using ( Paint.ToPixmap( tile ) )
			{
				Paint.Translate( new Vector2( -tileLeft, 0f ) );
				AudioInstrumentBlockRenderer.DrawStatic(
					new Rect( 0f, 0f, key.Width, key.Height ),
					new Rect( tileLeft, 0f, tileWidth, key.Height ),
					Instrument,
					_waveformPreview,
					_visualState,
					_audible );
			}
			_staticTiles.Add( index, tile );
		}
	}

	private static int ReferenceSignature( object value )
	{
		return value is null ? 0 : RuntimeHelpers.GetHashCode( value );
	}

	private FadeEdge HitFadeEdge( Vector2 position )
	{
		if ( position.y > 12f )
			return FadeEdge.None;
		var fadeInX = (Instrument.FadeInDuration * _view.PixelsPerSecond).Clamp( 0f, Width );
		var fadeOutX = Width - (Instrument.FadeOutDuration * _view.PixelsPerSecond).Clamp( 0f, Width );
		if ( MathF.Abs( position.x - fadeInX ) <= 7f )
			return FadeEdge.Start;
		if ( MathF.Abs( position.x - fadeOutX ) <= 7f )
			return FadeEdge.End;
		return FadeEdge.None;
	}

	private FadeEdge HitCurvePoint( Vector2 position )
	{
		var fadeInX = (Instrument.FadeInDuration * _view.PixelsPerSecond).Clamp( 0f, Width );
		if ( Instrument.FadeInDuration > 0f )
		{
			var point = new Vector2(
				(fadeInX + 2f) * 0.5f,
				Height - 3f - Instrument.FadeInCurveMidpoint * (Height - 6f) );
			if ( (position - point).Length <= 7f )
				return FadeEdge.Start;
		}

		var fadeOutX = Width - (Instrument.FadeOutDuration * _view.PixelsPerSecond).Clamp( 0f, Width );
		if ( Instrument.FadeOutDuration > 0f )
		{
			var point = new Vector2(
				(fadeOutX + Width - 2f) * 0.5f,
				Height - 3f - Instrument.FadeOutCurveMidpoint * (Height - 6f) );
			if ( (position - point).Length <= 7f )
				return FadeEdge.End;
		}
		return FadeEdge.None;
	}

	private enum TrimEdge
	{
		None,
		Start,
		End
	}

	private enum FadeEdge
	{
		None,
		Start,
		End
	}
}

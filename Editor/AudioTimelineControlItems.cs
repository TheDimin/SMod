using System;
using System.Text.Json.Nodes;
using Smod;

namespace Editor;

internal sealed class AudioTimelineMarkerItem : GraphicsItem
{
	private readonly AudioTimelineView _view;
	private readonly object _marker;
	private readonly bool _destination;
	private JsonObject _before;

	public object Marker => _marker;

	public AudioTimelineMarkerItem( AudioTimelineView view, object marker, bool destination )
	{
		_view = view;
		_marker = marker;
		_destination = destination;
		HoverEvents = true;
		Selectable = true;
		Focusable = true;
		Movable = true;
		Cursor = CursorShape.SizeH;
		ToolTip = destination
			? "Destination marker. Drag to move; transitions reference its stable ID."
			: "Transition marker. Drag to move; select to edit destination, conditions, and priority.";
	}

	protected override void OnMousePressed( GraphicsMouseEvent e )
	{
		if ( e.LeftMouseButton )
		{
			_before = _view.CaptureEventState();
			_view.SelectTimelineControl( _marker );
			e.Accepted = true;
		}
		base.OnMousePressed( e );
	}

	protected override void OnMoved()
	{
		base.OnMoved();
		Position = new Vector2( MathF.Max( AudioTimelineView.HeaderWidth - Width * 0.5f, Position.x ), 0f );
		var seconds = _view.TimeFromPosition( Position.x + Width * 0.5f ).SnapToGrid( 0.01f );
		if ( _marker is DestinationMarker destination )
			destination.Position = new TimelineTime( seconds );
		else if ( _marker is TransitionMarker transition )
			transition.Position = new TimelineTime( seconds );
		Update();
	}

	protected override void OnMouseReleased( GraphicsMouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _before is null )
			return;
		var before = _before;
		_before = null;
		_view.CommitTrackChange( _destination ? "Move Destination Marker" : "Move Transition Marker", before );
		e.Accepted = true;
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Delete )
			_view.RemoveTimelineControl( _marker );
		base.OnKeyPress( e );
	}

	protected override void OnPaint()
	{
		var color = _destination ? Theme.Green : Theme.Blue;
		Paint.ClearPen();
		Paint.SetBrush( Paint.HasSelected ? color.WithAlpha( 1f ) : color.WithAlpha( 0.8f ) );
		if ( _destination )
			Paint.DrawRect( new Rect( 3f, 2f, 10f, 10f ), 2f );
		else
			Paint.DrawCircle( new Vector2( 8f, 7f ), 6f );
		Paint.SetBrush( color.WithAlpha( Paint.HasSelected ? 0.9f : 0.55f ) );
		Paint.DrawRect( new Rect( 7f, 12f, 2f, MathF.Max( 0f, Height - 12f ) ) );
	}
}

internal sealed class AudioTimelineRegionItem : GraphicsItem
{
	private const float EdgeWidth = 7f;
	private readonly AudioTimelineView _view;
	private readonly object _region;
	private JsonObject _before;
	private RegionEdge _edge;
	private float _pressSceneX;
	private double _originalStart;
	private double _originalEnd;
	private bool _dragging;

	public object Region => _region;

	public AudioTimelineRegionItem( AudioTimelineView view, object region )
	{
		_view = view;
		_region = region;
		HoverEvents = true;
		Selectable = true;
		Focusable = true;
		Movable = true;
		Cursor = CursorShape.SizeAll;
		ToolTip = region is LoopRegion
			? "Loop region. Drag to move; drag either edge to resize."
			: "Transition region. Drag to move; drag either edge to resize.";
	}

	protected override void OnMousePressed( GraphicsMouseEvent e )
	{
		if ( e.LeftMouseButton )
		{
			_before = _view.CaptureEventState();
			_pressSceneX = e.ScenePosition.x;
			(_originalStart, _originalEnd) = Range;
			_dragging = true;
			_edge = e.LocalPosition.x <= EdgeWidth
				? RegionEdge.Start
				: e.LocalPosition.x >= Width - EdgeWidth ? RegionEdge.End : RegionEdge.None;
			Movable = _edge == RegionEdge.None;
			_view.SelectTimelineControl( _region );
			e.Accepted = true;
		}
		base.OnMousePressed( e );
	}

	protected override void OnMouseMove( GraphicsMouseEvent e )
	{
		base.OnMouseMove( e );
		Cursor = e.LocalPosition.x <= EdgeWidth || e.LocalPosition.x >= Width - EdgeWidth ? CursorShape.SizeH : CursorShape.SizeAll;
		if ( _edge == RegionEdge.None || !e.LeftMouseButton )
			return;

		var delta = (e.ScenePosition.x - _pressSceneX) / _view.PixelsPerSecond;
		var start = _edge == RegionEdge.Start
			? Math.Clamp( _originalStart + delta, 0d, _originalEnd - 0.01d )
			: _originalStart;
		var end = _edge == RegionEdge.End
			? Math.Max( _originalStart + 0.01d, _originalEnd + delta )
			: _originalEnd;
		SetRange( start, end );
		_view.LayoutTimelineControl( this );
		e.Accepted = true;
	}

	protected override void OnMoved()
	{
		base.OnMoved();
		if ( !_dragging || _edge != RegionEdge.None )
			return;
		var length = _originalEnd - _originalStart;
		var start = _view.TimeFromPosition( Position.x ).SnapToGrid( 0.01f );
		SetRange( start, start + length );
		Position = Position.WithY( _region is LoopRegion ? AudioTimelineView.RulerHeight - 15f : AudioTimelineView.RulerHeight - 29f );
	}

	protected override void OnMouseReleased( GraphicsMouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _before is null )
			return;
		var before = _before;
		_before = null;
		_dragging = false;
		_edge = RegionEdge.None;
		Movable = true;
		_view.CommitTrackChange( _region is LoopRegion ? "Edit Loop Region" : "Edit Transition Region", before );
		e.Accepted = true;
	}

	protected override void OnKeyPress( KeyEvent e )
	{
		if ( e.Key == KeyCode.Delete )
			_view.RemoveTimelineControl( _region );
		base.OnKeyPress( e );
	}

	protected override void OnPaint()
	{
		var loop = _region is LoopRegion;
		var color = loop ? Theme.Yellow : Theme.Primary;
		Paint.ClearPen();
		Paint.SetBrush( color.WithAlpha( Paint.HasSelected ? 0.7f : 0.42f ) );
		Paint.DrawRect( LocalRect, 3f );
		Paint.SetPen( color, Paint.HasSelected ? 2f : 1f );
		Paint.DrawRect( LocalRect.Shrink( 0.5f ), 3f );
		Paint.SetDefaultFont( 7f, 700 );
		Paint.SetPen( Theme.Text );
		Paint.DrawText( LocalRect.Shrink( 7f, 0f ), loop ? "↩ LOOP" : "→ TRANSITION", TextFlag.LeftCenter | TextFlag.SingleLine );
	}

	private (double Start, double End) Range => _region switch
	{
		LoopRegion loop => (loop.Start.Seconds, loop.End.Seconds),
		TransitionRegion transition => (transition.Start.Seconds, transition.End.Seconds),
		_ => default
	};

	private void SetRange( double start, double end )
	{
		if ( _region is LoopRegion loop )
		{
			loop.Start = new TimelineTime( start );
			loop.End = new TimelineTime( end );
		}
		else if ( _region is TransitionRegion transition )
		{
			transition.Start = new TimelineTime( start );
			transition.End = new TimelineTime( end );
		}
	}

	private enum RegionEdge
	{
		None,
		Start,
		End
	}
}

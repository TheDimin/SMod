using System.Text.Json.Nodes;
using Smod;

namespace Editor;

/// <summary>
/// Draggable separator at the bottom of a timeline track. Height changes are previewed
/// live and committed as one undoable AudioEvent mutation on release.
/// </summary>
internal sealed class AudioTrackResizeHandleItem : GraphicsItem
{
	private readonly AudioTimelineView _view;
	private JsonObject _before;
	private bool _dragging;

	public AudioTrack Track { get; }

	public AudioTrackResizeHandleItem( AudioTimelineView view, AudioTrack track )
	{
		_view = view;
		Track = track;
		HoverEvents = true;
		Cursor = CursorShape.SizeV;
		ZIndex = 20;
		ToolTip = "Drag to change this track's timeline height.";
	}

	protected override void OnMousePressed( GraphicsMouseEvent e )
	{
		if ( e.LeftMouseButton )
		{
			_before = _view.CaptureEventState();
			_dragging = true;
			e.Accepted = true;
		}
		base.OnMousePressed( e );
	}

	protected override void OnMouseMove( GraphicsMouseEvent e )
	{
		base.OnMouseMove( e );
		if ( !_dragging || !e.LeftMouseButton )
			return;
		_view.PreviewTrackHeight( Track, e.ScenePosition.y - _view.TrackTop( Track ) );
		e.Accepted = true;
	}

	protected override void OnMouseReleased( GraphicsMouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( !_dragging )
			return;
		_dragging = false;
		var before = _before;
		_before = null;
		e.Accepted = true;
		_view.CommitTrackHeight( before );
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Paint.HasMouseOver || _dragging
			? Theme.Primary.WithAlpha( 0.7f )
			: Theme.Text.WithAlpha( 0.12f ) );
		Paint.DrawRect( new Rect( 0f, 2f, LocalRect.Width, 2f ) );
	}
}

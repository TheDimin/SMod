using System;
using System.Threading.Tasks;

namespace Editor;

internal sealed class AudioCloudSoundLibraryList : ListView
{
	private readonly Dictionary<string, AudioCloudSoundTransferState> _states = new( StringComparer.OrdinalIgnoreCase );
	private readonly AudioEventEditorWindow _window;
	private readonly AudioPreviewPlaybackService _preview = AudioPreviewPlaybackService.Instance;

	public AudioCloudSoundLibraryList( Widget parent, AudioEventEditorWindow window ) : base( parent )
	{
		_window = window;
		ItemSize = new Vector2( 0f, 50f );
		ItemSpacing = 2f;
		MultiSelect = true;
		FocusMode = FocusMode.TabOrClick;
		ItemClicked = item => { if ( item is Package package ) _ = TogglePreviewAsync( package ); };
		ItemActivated = item => { if ( item is Package package ) _ = TogglePreviewAsync( package ); };
		ItemContextMenu = ShowContextMenu;
		AudioCloudSoundCatalog.TransferChanged += OnTransferChanged;
		_preview.StateChanged += OnPreviewStateChanged;
	}

	public void SetPackages( IReadOnlyList<Package> packages ) => SetItems( packages.Cast<object>() );

	private Task TogglePreviewAsync( Package package )
	{
		return _preview.ToggleAsync(
			_window,
			AudioPreviewSource.CloudSound,
			package.FullIdent,
			package.Title ?? package.Ident,
			token => AudioCloudSoundCatalog.LoadPreviewAsync( package.FullIdent, token ),
			_window.StopTimelineForFilePreview );
	}

	protected override bool OnDragItem( VirtualWidget item )
	{
		if ( item.Object is not Package package ) return false;
		var packages = SelectedItems.OfType<Package>().ToArray();
		if ( !packages.Contains( package ) ) packages = new[] { package };
		var drag = new Drag( this );
		drag.Data.Object = new AudioLibraryDragData( cloudPackages: packages );
		drag.Data.Text = packages.Length == 1 ? package.Title ?? package.Ident : $"{packages.Length} cloud sounds";
		drag.Execute();
		return true;
	}

	private void ShowContextMenu( object item )
	{
		if ( item is not Package package ) return;
		var menu = new ContextMenu( this );
		menu.AddHeading( package.Title ?? package.Ident );
		AudioEventEditorWindow.AddLibraryGetPath( menu, "package", package.FullIdent );
		menu.AddOption( _preview.IsActive( AudioPreviewSource.CloudSound, package.FullIdent ) ? "Stop Preview" : "Preview", "play_arrow",
			() => _ = TogglePreviewAsync( package ) );
		menu.OpenAtCursor();
	}

	private void OnTransferChanged( AudioCloudSoundTransferState state ) => SetRowState( state );

	private void OnPreviewStateChanged( AudioPreviewState state )
	{
		if ( state.Source == AudioPreviewSource.CloudSound && !string.IsNullOrWhiteSpace( state.ItemId ) )
		{
			SetRowState( new AudioCloudSoundTransferState(
				state.ItemId,
				state.Message,
				state.Status == AudioPreviewStatus.Playing ? 1f : 0f,
				state.Status == AudioPreviewStatus.Loading,
				state.Status == AudioPreviewStatus.Error ) );
		}
		Update();
	}

	private void SetRowState( AudioCloudSoundTransferState state )
	{
		_states[state.PackageIdent] = state;
		Update();
	}

	protected override void PaintItem( VirtualWidget item )
	{
		if ( item.Object is not Package package ) return;
		if ( Paint.HasSelected || Paint.HasPressed || Paint.HasMouseOver )
		{
			Paint.ClearPen();
			Paint.SetBrush( (Paint.HasSelected || Paint.HasPressed ? Theme.Primary : Theme.SurfaceLightBackground).WithAlpha( 0.35f ) );
			Paint.DrawRect( item.Rect.Shrink( 1f ), 3f );
		}
		var iconRect = item.Rect.Shrink( 6f );
		iconRect.Width = 22f;
		Paint.SetPen( Theme.Blue );
		Paint.DrawIcon( iconRect, "audio_file", 19f, TextFlag.Center );
		var textRect = item.Rect.Shrink( 34f, 4f, 5f, 4f );
		Paint.SetDefaultFont( 8f, 500 );
		Paint.SetPen( Theme.Text );
		Paint.DrawText( textRect, Paint.GetElidedText( package.Title ?? package.Ident, textRect.Width, ElideMode.Right ), TextFlag.LeftTop | TextFlag.SingleLine );
		var detail = _states.TryGetValue( package.FullIdent, out var state )
			? state.Message
			: $"Cloud UGC · {package.Org?.Title ?? package.Org?.Ident ?? "Unknown creator"}";
		textRect.Top += 20f;
		Paint.SetDefaultFont( 7f );
		Paint.SetPen( _states.TryGetValue( package.FullIdent, out state ) && state.Failed ? Theme.Red : Theme.Text.WithAlpha( 0.55f ) );
		Paint.DrawText( textRect, Paint.GetElidedText( detail, textRect.Width, ElideMode.Right ), TextFlag.LeftTop | TextFlag.SingleLine );
	}

	public override void OnDestroyed()
	{
		AudioCloudSoundCatalog.TransferChanged -= OnTransferChanged;
		_preview.StateChanged -= OnPreviewStateChanged;
		base.OnDestroyed();
	}
}

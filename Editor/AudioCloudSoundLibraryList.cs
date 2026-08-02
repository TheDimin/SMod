using System;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

internal sealed class AudioCloudSoundLibraryList : ListView
{
	private readonly Dictionary<string, AudioCloudSoundTransferState> _states = new( StringComparer.OrdinalIgnoreCase );
	private Package _auditionedPackage;
	private SoundHandle _auditionHandle;
	private CancellationTokenSource _previewCancellation;
	private string _previewRequestIdent;
	private long _previewGeneration;

	public AudioCloudSoundLibraryList( Widget parent ) : base( parent )
	{
		ItemSize = new Vector2( 0f, 50f );
		ItemSpacing = 2f;
		MultiSelect = false;
		FocusMode = FocusMode.TabOrClick;
		ItemClicked = item =>
		{
			if ( item is Package package )
				_ = RestartAuditionAsync( package );
		};
		ItemActivated = item =>
		{
			if ( item is Package package )
				_ = RestartAuditionAsync( package );
		};
		ItemContextMenu = ShowContextMenu;
		AudioCloudSoundCatalog.TransferChanged += OnTransferChanged;
	}

	public void SetPackages( IReadOnlyList<Package> packages )
	{
		SetItems( packages.Cast<object>() );
	}

	private async Task RestartAuditionAsync( Package package )
	{
		StopAudition();
		if ( package is null )
			return;

		var generation = ++_previewGeneration;
		var cancellation = new CancellationTokenSource();
		_previewCancellation = cancellation;
		_previewRequestIdent = package.FullIdent;
		SetRowState( package.FullIdent, "Loading preview…", 0f, true, false );
		try
		{
			var sound = await AudioCloudSoundCatalog.LoadPreviewAsync( package.FullIdent, cancellation.Token );
			await GameTask.MainThread( cancellation.Token );
			if ( cancellation.IsCancellationRequested || generation != _previewGeneration )
				return;

			_auditionHandle = Sound.PlayFile( sound, 0.5f, 1f );
			if ( !(_auditionHandle?.IsValid() ?? false) )
				throw new InvalidOperationException( $"Could not preview '{package.Title}'." );

			_auditionedPackage = package;
			_auditionHandle.ListenLocal = true;
			_auditionHandle.Position = Vector3.Forward * 64f;
			_previewRequestIdent = null;
			SetRowState( package.FullIdent, "Playing preview", 1f, false, false );
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception exception )
		{
			await GameTask.MainThread();
			if ( generation != _previewGeneration )
				return;
			_previewRequestIdent = null;
			SetRowState( package.FullIdent, $"Preview failed: {exception.Message}", 0f, false, true );
			Log.Warning( exception, $"Soundfalls preview failed for '{package.FullIdent}'." );
		}
		finally
		{
			if ( ReferenceEquals( _previewCancellation, cancellation ) )
			{
				_previewCancellation = null;
				cancellation.Dispose();
			}
		}
	}

	private void StopAudition()
	{
		_previewGeneration++;
		_previewRequestIdent = null;
		_previewCancellation?.Cancel();
		_previewCancellation?.Dispose();
		_previewCancellation = null;
		_auditionHandle?.Stop();
		_auditionHandle = null;
		_auditionedPackage = null;
	}

	protected override bool OnDragItem( VirtualWidget item )
	{
		if ( item.Object is not Package package )
			return false;

		var drag = new Drag( this );
		drag.Data.Text = package.FullIdent;
		drag.Execute();
		return true;
	}

	private void ShowContextMenu( object item )
	{
		if ( item is not Package package )
			return;

		var menu = new ContextMenu( this );
		menu.AddHeading( package.Title ?? package.Ident );
		menu.AddOption( "Preview", "play_arrow", () => _ = RestartAuditionAsync( package ) );
		menu.OpenAtCursor();
	}

	private void OnTransferChanged( AudioCloudSoundTransferState state )
	{
		if ( string.Equals( state.PackageIdent, _previewRequestIdent, StringComparison.OrdinalIgnoreCase ) )
			return;

		SetRowState( state.PackageIdent, state.Message, state.Progress, state.IsBusy, state.Failed );
	}

	private void SetRowState( string packageIdent, string message, float progress, bool isBusy, bool failed )
	{
		_states[packageIdent] = new AudioCloudSoundTransferState(
			packageIdent, message, progress, isBusy, failed );
		Update();
	}

	protected override void PaintItem( VirtualWidget item )
	{
		if ( item.Object is not Package package )
			return;

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
		Paint.SetPen( _states.TryGetValue( package.FullIdent, out state ) && state.Failed
			? Theme.Red
			: Theme.Text.WithAlpha( 0.55f ) );
		Paint.DrawText( textRect, Paint.GetElidedText( detail, textRect.Width, ElideMode.Right ), TextFlag.LeftTop | TextFlag.SingleLine );
	}

	public override void OnDestroyed()
	{
		AudioCloudSoundCatalog.TransferChanged -= OnTransferChanged;
		StopAudition();
		base.OnDestroyed();
	}
}

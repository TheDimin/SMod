using System;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

internal sealed class AudioCloudSoundLibraryWidget : Widget
{
	private readonly SearchWidget _search;
	private readonly Label _count;
	private readonly Label _status;
	private readonly AudioCloudSoundLibraryList _list;
	private Package[] _packages = Array.Empty<Package>();
	private CancellationTokenSource _catalogCancellation;
	private long _searchGeneration;

	public AudioCloudSoundLibraryWidget( AudioEventEditorWindow window ) : base( window )
	{
		WindowTitle = "Cloud Sounds";
		SetWindowIcon( "audio_file" );
		MinimumWidth = 220f;
		Layout = Layout.Column();
		Layout.Margin = 6f;
		Layout.Spacing = 6f;

		var header = Layout.AddRow();
		header.Add( new Label( "Cloud Sounds" ), 1 );
		_count = header.Add( new Label { Alignment = TextFlag.RightCenter } );
		var refresh = header.Add( new ToolButton( string.Empty, "refresh", this ) );
		refresh.ToolTip = "Refresh sandbox UGC sounds";
		refresh.MouseLeftPress = RefreshCatalog;

		_search = Layout.Add( new SearchWidget( this, false ) );
		_search.PlaceholderText = "Search all cloud sounds";
		_search.ValueChanged += QueueSearch;

		var hint = Layout.Add( new Label( "Click to preview. Ctrl/Shift-select, then drag to create a multi-instrument." ) );
		hint.WordWrap = true;
		hint.MaximumHeight = 38f;
		_status = Layout.Add( new Label( "Loading sandbox UGC sound catalog…" ) );
		_status.WordWrap = true;
		_status.MaximumHeight = 38f;
		_list = Layout.Add( new AudioCloudSoundLibraryList( this, window ), 1 );
		RefreshCatalog();
	}

	private void RefreshCatalog() => StartSearch( _search.Value?.Trim() ?? string.Empty, false );

	private void QueueSearch()
	{
		var query = _search.Value?.Trim() ?? string.Empty;
		StartSearch( query, true );
	}

	private void StartSearch( string query, bool debounce )
	{
		_catalogCancellation?.Cancel();
		_catalogCancellation?.Dispose();
		_catalogCancellation = new CancellationTokenSource();
		var generation = ++_searchGeneration;
		_ = SearchAsync( query, debounce, generation, _catalogCancellation.Token );
	}

	private async Task SearchAsync( string query, bool debounce, long generation, CancellationToken token )
	{
		try
		{
			if ( debounce ) await Task.Delay( 250, token );
			_status.Text = string.IsNullOrWhiteSpace( query ) ? "Loading popular cloud sounds…" : $"Searching cloud sounds for ‘{query}’…";
			var packages = await AudioCloudSoundCatalog.BrowseAsync( query, token );
			token.ThrowIfCancellationRequested();
			if ( generation != _searchGeneration ) return;

			_packages = packages.Select( package => (Package: package, Score: ScorePackage( package, query )) )
				.Where( match => match.Score >= 0 )
				.OrderByDescending( match => match.Score )
				.ThenBy( match => match.Package.Title ?? match.Package.Ident, StringComparer.OrdinalIgnoreCase )
				.Select( match => match.Package ).ToArray();
			_count.Text = _packages.Length.ToString();
			_status.Text = _packages.Length == 0 ? "No matching cloud sounds." : $"{_packages.Length} sandbox UGC sounds";
			_list.SetPackages( _packages );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception exception )
		{
			if ( generation != _searchGeneration ) return;
			_status.Text = $"Cloud catalog failed: {exception.Message}";
			Log.Warning( exception, "Soundfalls cloud catalog failed." );
		}
	}

	private static int ScorePackage( Package package, string query )
	{
		return AudioCloudSoundSearch.Score(
			new[] { package.Title, package.Ident, package.Org?.Ident, package.Org?.Title, package.Summary }
				.Concat( package.Tags ?? Array.Empty<string>() ), query );
	}

	public override void OnDestroyed()
	{
		_catalogCancellation?.Cancel();
		_catalogCancellation?.Dispose();
		_catalogCancellation = null;
		base.OnDestroyed();
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( 0.45f ) );
		Paint.DrawRect( LocalRect );
	}
}

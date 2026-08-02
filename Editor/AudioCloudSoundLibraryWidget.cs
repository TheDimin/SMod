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

	public AudioCloudSoundLibraryWidget( Widget parent ) : base( parent )
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
		_search.PlaceholderText = "Search cloud sounds";
		_search.ValueChanged += ApplySearch;

		var hint = Layout.Add( new Label( "Click to preview. Drag onto a track to import and add." ) );
		hint.WordWrap = true;
		hint.MaximumHeight = 38f;

		_status = Layout.Add( new Label( "Loading sandbox UGC sound catalog…" ) );
		_status.WordWrap = true;
		_status.MaximumHeight = 38f;
		_list = Layout.Add( new AudioCloudSoundLibraryList( this ), 1 );
		RefreshCatalog();
	}

	private void RefreshCatalog()
	{
		_catalogCancellation?.Cancel();
		_catalogCancellation?.Dispose();
		_catalogCancellation = new CancellationTokenSource();
		_ = RefreshCatalogAsync( _catalogCancellation.Token );
	}

	private async Task RefreshCatalogAsync( CancellationToken token )
	{
		_status.Text = "Loading sandbox UGC sound catalog…";
		try
		{
			_packages = await AudioCloudSoundCatalog.BrowseAsync( token );
			token.ThrowIfCancellationRequested();
			_status.Text = _packages.Length == 0
				? "No cloud sounds were returned. Refresh to try again."
				: $"{_packages.Length} sandbox UGC sounds";
			ApplySearch();
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception exception )
		{
			_status.Text = $"Cloud catalog failed: {exception.Message}";
			Log.Warning( exception, "Soundfalls cloud catalog failed." );
		}
	}

	private void ApplySearch()
	{
		var query = _search.Value?.Trim() ?? string.Empty;
		var matches = _packages
			.Select( package => (Package: package, Score: ScorePackage( package, query )) )
			.Where( match => match.Score >= 0 )
			.OrderByDescending( match => match.Score )
			.ThenBy( match => match.Package.Title ?? match.Package.Ident, StringComparer.OrdinalIgnoreCase )
			.Select( match => match.Package )
			.ToArray();

		_count.Text = string.IsNullOrWhiteSpace( query )
			? _packages.Length.ToString()
			: $"{matches.Length}/{_packages.Length}";
		_list.SetPackages( matches );
	}

	private static int ScorePackage( Package package, string query )
	{
		if ( string.IsNullOrWhiteSpace( query ) )
			return 0;

		var fields = new[] { package.Title, package.Ident, package.Org?.Ident, package.Org?.Title, package.Summary }
			.Concat( package.Tags ?? Array.Empty<string>() )
			.Where( value => !string.IsNullOrWhiteSpace( value ) )
			.ToArray();
		var total = 0;
		foreach ( var token in query.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var best = fields.Select( field => FuzzyScore( field, token ) ).DefaultIfEmpty( -1 ).Max();
			if ( best < 0 )
				return -1;
			total += best;
		}
		return total;
	}

	private static int FuzzyScore( string candidate, string query )
	{
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
			while ( candidateIndex < candidate.Length && char.ToUpperInvariant( candidate[candidateIndex] ) != char.ToUpperInvariant( queryCharacter ) )
				candidateIndex++;
			if ( candidateIndex >= candidate.Length )
				return -1;

			var wordStart = candidateIndex == 0 || !char.IsLetterOrDigit( candidate[candidateIndex - 1] );
			score += candidateIndex == previousMatch + 1 ? 24 : 8;
			if ( wordStart )
				score += 18;
			previousMatch = candidateIndex;
			candidateIndex++;
		}

		return score - candidate.Length - (candidateIndex - query.Length) * 2;
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

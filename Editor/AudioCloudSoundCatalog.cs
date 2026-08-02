using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

internal readonly record struct AudioCloudSoundTransferState(
	string PackageIdent,
	string Message,
	float Progress,
	bool IsBusy,
	bool Failed );

internal static class AudioCloudSoundCatalog
{
	private const string CatalogQuery = "type:vsnd sort:popular";
	private static readonly string CacheRoot = Path.Combine( Path.GetTempPath(), "Smod", "Soundfalls" );

	public static event Action<AudioCloudSoundTransferState> TransferChanged;

	public static async Task<Package[]> BrowseAsync( CancellationToken token )
	{
		var result = await Package.FindAsync( CatalogQuery, 200, 0, token );
		return result?.Packages?
			.Where( package => package is not null
				&& string.Equals( package.TypeName, "vsnd", StringComparison.OrdinalIgnoreCase ) )
			.ToArray() ?? Array.Empty<Package>();
	}

	public static async Task<SoundFile> LoadPreviewAsync( string packageIdent, CancellationToken token = default )
	{
		var source = await DownloadSelectedSourceAsync( packageIdent, token );
		var bytes = await File.ReadAllBytesAsync( source.CachePath, token );
		var resourceName = $"soundfalls/{SafeName( source.Package.FullIdent )}_{source.Package.Revision.VersionId}.vsnd";
		var extension = Path.GetExtension( source.ManifestFile.Path );
		await GameTask.MainThread( token );
		token.ThrowIfCancellationRequested();

		var sound = extension.ToLowerInvariant() switch
		{
			".wav" => SoundFile.FromWav( resourceName, bytes ),
			".mp3" => SoundFile.FromMp3( resourceName, bytes ),
			".ogg" or ".opus" => SoundFile.FromOgg( resourceName, bytes ),
			_ => throw new InvalidOperationException(
				$"'{source.ManifestFile.Path}' can be imported, but its format cannot be previewed directly." )
		};

		if ( !sound.IsValid() )
			throw new InvalidOperationException( $"Sandbox could not decode '{source.ManifestFile.Path}'." );

		Report( packageIdent, "Ready to preview", 1f, false, false );
		return sound;
	}

	public static async Task<Asset> ImportAsync( string packageIdent, CancellationToken token = default )
	{
		var source = await DownloadSelectedSourceAsync( packageIdent, token );
		Report( packageIdent, "Importing into Audio/Source…", 1f, true, false );

		var sourceDirectory = Path.Combine( Project.Current.GetAssetsPath(), "Audio", "Source" );
		Directory.CreateDirectory( sourceDirectory );

		var sourceName = Path.GetFileName( source.ManifestFile.Path );
		var importName = $"{SafeName( source.Package.FullIdent )}_{source.Package.Revision.VersionId}_{sourceName}";
		var destination = Path.Combine( sourceDirectory, importName );
		if ( !File.Exists( destination ) )
			File.Copy( source.CachePath, destination, false );

		await GameTask.MainThread( token );
		token.ThrowIfCancellationRequested();
		var asset = AssetSystem.RegisterFile( destination );
		if ( asset?.AssetType != AssetType.SoundFile )
			throw new InvalidOperationException( $"Sandbox did not register '{destination}' as a SoundFile." );

		Report( packageIdent, $"Imported {asset.Name}", 1f, false, false );
		return asset;
	}

	private static async Task<DownloadedSource> DownloadSelectedSourceAsync( string packageIdent, CancellationToken token )
	{
		try
		{
			Report( packageIdent, "Loading cloud metadata…", 0f, true, false );
			var package = await Package.FetchAsync( packageIdent, false );
			if ( package?.Revision is null )
				throw new InvalidOperationException( $"Cloud sound '{packageIdent}' is unavailable." );

			await package.Revision.DownloadManifestAsync( token );
			var manifestFile = FindPrimarySoundSource( package );
			if ( string.IsNullOrWhiteSpace( manifestFile.Url ) )
				throw new InvalidOperationException( $"Cloud sound '{packageIdent}' has no downloadable source URL." );

			var cacheDirectory = Path.Combine(
				CacheRoot,
				SafeName( package.FullIdent ),
				package.Revision.VersionId.ToString() );
			Directory.CreateDirectory( cacheDirectory );
			var cachePath = Path.Combine( cacheDirectory, Path.GetFileName( manifestFile.Path ) );
			if ( !File.Exists( cachePath ) || new FileInfo( cachePath ).Length != manifestFile.Size )
			{
				Report( packageIdent, "Downloading selected clip…", 0.15f, true, false );
				var bytes = await Sandbox.Http.RequestBytesAsync( manifestFile.Url, cancellationToken: token );
				await File.WriteAllBytesAsync( cachePath, bytes, token );
			}

			Report( packageIdent, "Selected clip cached", 1f, false, false );
			return new DownloadedSource( package, manifestFile, cachePath );
		}
		catch ( OperationCanceledException )
		{
			throw;
		}
		catch ( Exception exception )
		{
			Report( packageIdent, exception.Message, 0f, false, true );
			throw;
		}
	}

	private static ManifestSchema.File FindPrimarySoundSource( Package package )
	{
		var files = package.Revision.Manifest?.Files ?? Array.Empty<ManifestSchema.File>();
		var selectedPath = AudioCloudSoundSourceSelector.SelectPath(
			package.FullIdent,
			package.PrimaryAsset,
			package.GetValue<string>( "SingleAssetSource", null ),
			files.Select( file => file.Path ).ToArray() );
		return files.First( file =>
			string.Equals( file.Path, selectedPath, StringComparison.OrdinalIgnoreCase ) );
	}

	private static string SafeName( string value )
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string( (value ?? "cloud_sound")
			.Select( character => invalid.Contains( character ) ? '_' : character )
			.ToArray() );
	}

	private static void Report( string packageIdent, string message, float progress, bool isBusy, bool failed )
	{
		if ( !ThreadSafe.IsMainThread )
		{
			MainThread.Queue( () => Report( packageIdent, message, progress, isBusy, failed ) );
			return;
		}

		TransferChanged?.Invoke( new AudioCloudSoundTransferState( packageIdent, message, progress, isBusy, failed ) );
	}

	private readonly record struct DownloadedSource(
		Package Package,
		ManifestSchema.File ManifestFile,
		string CachePath );
}

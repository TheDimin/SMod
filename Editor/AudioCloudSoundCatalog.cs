using System;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

internal readonly record struct AudioCloudSoundTransferState( string PackageIdent, string Message, float Progress, bool IsBusy, bool Failed );

internal static class AudioCloudSoundCatalog
{
	public static event Action<AudioCloudSoundTransferState> TransferChanged;

	public static async Task<Package[]> BrowseAsync( string query, CancellationToken token )
	{
		var result = await Package.FindAsync( AudioCloudSoundSearch.BuildQuery( query ), 400, 0, token );
		return result?.Packages?.Where( package => package is not null && string.Equals( package.TypeName, "vsnd", StringComparison.OrdinalIgnoreCase ) ).ToArray()
			?? Array.Empty<Package>();
	}

	public static async Task<SoundFile> LoadPreviewAsync( string packageIdent, CancellationToken token = default )
	{
		var asset = await InstallAsync( packageIdent, token );
		await GameTask.MainThread( token );
		var sound = SoundFile.Load( asset.Path );
		if ( !sound.IsValid() ) throw new InvalidOperationException( $"Cloud sound '{packageIdent}' did not load as a SoundFile." );
		return sound;
	}

	public static async Task<Asset> InstallAsync( string packageIdent, CancellationToken token = default )
	{
		try
		{
			Report( packageIdent, "Installing cloud sound…", 0f, true, false );
			var asset = await AssetSystem.InstallAsync( packageIdent, true,
				progress => Report( packageIdent, "Installing cloud sound…", progress, true, false ), token );
			if ( asset?.AssetType != AssetType.SoundFile )
				throw new InvalidOperationException( $"Cloud package '{packageIdent}' has no valid SoundFile primary asset." );
			Report( packageIdent, $"Ready: {asset.Name}", 1f, false, false );
			return asset;
		}
		catch ( OperationCanceledException ) { throw; }
		catch ( Exception exception )
		{
			Report( packageIdent, exception.Message, 0f, false, true );
			throw;
		}
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
}

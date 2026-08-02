using System;
using System.IO;

namespace Editor;

internal static class AudioCloudSoundSourceSelector
{
	public static string SelectPath(
		string packageIdent,
		string primaryAsset,
		string singleAssetSource,
		IReadOnlyList<string> paths )
	{
		var decodableSources = paths
			.Where( IsDirectlyDecodable )
			.ToArray();
		var normalizedSingleAssetSource = NormalizePath( singleAssetSource );
		if ( !string.IsNullOrWhiteSpace( normalizedSingleAssetSource ) )
		{
			var exactSource = decodableSources.FirstOrDefault( path =>
				string.Equals( NormalizePath( path ), normalizedSingleAssetSource, StringComparison.OrdinalIgnoreCase ) );
			if ( !string.IsNullOrWhiteSpace( exactSource ) )
				return exactSource;
		}

		var normalizedPrimary = NormalizePath( primaryAsset );
		var primary = decodableSources.FirstOrDefault( path =>
			string.Equals( NormalizePath( Path.ChangeExtension( path, ".vsnd" ) ), normalizedPrimary, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( NormalizePath( path ), normalizedPrimary, StringComparison.OrdinalIgnoreCase ) );
		if ( !string.IsNullOrWhiteSpace( primary ) )
			return primary;
		if ( decodableSources.Length == 1 )
			return decodableSources[0];

		throw new InvalidOperationException(
			decodableSources.Length == 0
				? $"Cloud sound '{packageIdent}' does not expose a directly decodable raw audio source."
				: $"Cloud sound '{packageIdent}' has no unambiguous raw audio source." );
	}

	private static bool IsDirectlyDecodable( string path )
	{
		var extension = Path.GetExtension( path );
		return extension.Equals( ".wav", StringComparison.OrdinalIgnoreCase )
			|| extension.Equals( ".mp3", StringComparison.OrdinalIgnoreCase )
			|| extension.Equals( ".ogg", StringComparison.OrdinalIgnoreCase )
			|| extension.Equals( ".opus", StringComparison.OrdinalIgnoreCase );
	}

	private static string NormalizePath( string path )
	{
		return (path ?? string.Empty).Replace( '\\', '/' ).TrimStart( '/' );
	}
}

using Sandbox;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Editor;

internal enum AudioLibrarySource { Project, External }
internal enum ExternalAudioPreviewFormat { Wav, Ogg }

internal sealed record AudioLibraryItem( AudioLibrarySource Source, string Id, string Name, string RelativePath, Asset Asset, string ExternalPath )
{
	public static AudioLibraryItem FromAsset( Asset asset ) => new( AudioLibrarySource.Project, asset.Path, asset.Name, asset.RelativePath, asset, null );
	public static AudioLibraryItem FromExternal( ExternalAudioSourceFile file ) => new( AudioLibrarySource.External, file.FullPath, Path.GetFileNameWithoutExtension( file.FullPath ), file.RelativePath, null, file.FullPath );
}

internal readonly record struct ExternalAudioSourceFile( string FullPath, string RelativePath );

internal static class ExternalAudioSourceCatalog
{
	public const string SourceRoot = @"AudioSource";
	private static readonly string[] SupportedExtensions = [".wav", ".ogg"];

	public static ExternalAudioSourceFile[] Scan( string sourceRoot = SourceRoot )
	{
		if ( !Directory.Exists( sourceRoot ) ) return [];
		var root = CanonicalRoot( sourceRoot );
		try
		{
			return Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories )
				.Where( IsSupportedSource ).Select( path => CreateFile( root, path ) )
				.OrderBy( file => file.RelativePath, StringComparer.OrdinalIgnoreCase ).ToArray();
		}
		catch ( Exception exception ) when ( exception is UnauthorizedAccessException or IOException )
		{
			Log.Warning( exception, $"Could not scan external sounds under '{root}'." );
			return [];
		}
	}

	public static bool IsSupportedSource( string path ) => SupportedExtensions.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase );

	public static bool IsWithinSourceRoot( string path, string sourceRoot = SourceRoot )
	{
		if ( string.IsNullOrWhiteSpace( path ) || string.IsNullOrWhiteSpace( sourceRoot ) ) return false;
		try { return Path.GetFullPath( path ).StartsWith( CanonicalRoot( sourceRoot ), StringComparison.OrdinalIgnoreCase ); }
		catch ( Exception exception ) when ( exception is ArgumentException or NotSupportedException ) { return false; }
	}

	public static string GetRelativePath( string path, string sourceRoot = SourceRoot )
	{
		if ( !IsWithinSourceRoot( path, sourceRoot ) ) throw new InvalidOperationException( $"'{path}' is outside the configured AudioSource root." );
		return Path.GetRelativePath( Path.GetFullPath( sourceRoot ), Path.GetFullPath( path ) );
	}

	public static string ResolveImportPath( string sourcePath, string assetsPath, string sourceRoot = SourceRoot, Func<string, string, bool> filesEqual = null )
	{
		if ( !IsSupportedSource( sourcePath ) ) throw new InvalidOperationException( $"'{sourcePath}' is not a supported external audio file." );
		var relativePath = GetRelativePath( sourcePath, sourceRoot );
		var importRoot = Path.Combine( assetsPath, "Audio", "Source", "External" );
		var destination = Path.GetFullPath( Path.Combine( importRoot, relativePath ) );
		if ( !destination.StartsWith( CanonicalRoot( importRoot ), StringComparison.OrdinalIgnoreCase ) ) throw new InvalidOperationException( $"'{relativePath}' resolves outside the import folder." );

		filesEqual ??= FilesEqual;
		if ( !File.Exists( destination ) || filesEqual( sourcePath, destination ) ) return destination;
		var directory = Path.GetDirectoryName( destination )!;
		var stem = Path.GetFileNameWithoutExtension( destination );
		var extension = Path.GetExtension( destination );
		for ( var suffix = 2; ; suffix++ )
		{
			var candidate = Path.Combine( directory, $"{stem}_{suffix}{extension}" );
			if ( !File.Exists( candidate ) || filesEqual( sourcePath, candidate ) ) return candidate;
		}
	}

	public static ExternalAudioPreviewFormat GetPreviewFormat( string path )
	{
		return Path.GetExtension( path ).ToLowerInvariant() switch
		{
			".wav" => ExternalAudioPreviewFormat.Wav,
			".ogg" => ExternalAudioPreviewFormat.Ogg,
			_ => throw new InvalidOperationException( $"'{path}' is not a supported external audio preview format." )
		};
	}

	public static async Task<SoundFile> LoadPreviewAsync( string sourcePath, CancellationToken token = default )
	{
		if ( !File.Exists( sourcePath ) )
			throw new FileNotFoundException( "The external audio source no longer exists.", sourcePath );
		if ( !IsWithinSourceRoot( sourcePath ) )
			throw new InvalidOperationException( $"'{sourcePath}' is outside '{SourceRoot}'." );

		var format = GetPreviewFormat( sourcePath );
		var bytes = await File.ReadAllBytesAsync( sourcePath, token );
		token.ThrowIfCancellationRequested();
		var pcm = format == ExternalAudioPreviewFormat.Wav
			? ExternalWavPreviewDecoder.Decode( bytes )
			: default;
		var identity = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( Path.GetFullPath( sourcePath ) ) ) )[..16];
		var resourceName = $"external-audio-preview/{identity}{Path.GetExtension( sourcePath ).ToLowerInvariant()}";
		await GameTask.MainThread( token );
		token.ThrowIfCancellationRequested();

		var sound = format switch
		{
			ExternalAudioPreviewFormat.Wav => SoundFile.FromPcm( resourceName, pcm.Bytes, new SoundFile.PcmOptions
			{
				Channels = pcm.Channels,
				Rate = (uint)pcm.Rate,
				Bits = 16
			} ),
			ExternalAudioPreviewFormat.Ogg => SoundFile.FromOgg( resourceName, bytes ),
			_ => throw new InvalidOperationException( $"Unsupported external audio preview format '{format}'." )
		};
		if ( !sound.IsValid() )
			throw new InvalidOperationException( $"Sandbox could not decode '{sourcePath}'." );
		return sound;
	}

	public static async Task<Asset> ImportAsync( string sourcePath, CancellationToken token = default )
	{
		if ( !File.Exists( sourcePath ) ) throw new FileNotFoundException( "The external audio source no longer exists.", sourcePath );
		if ( !IsWithinSourceRoot( sourcePath ) ) throw new InvalidOperationException( $"'{sourcePath}' is outside '{SourceRoot}'." );
		var destination = ResolveImportPath( sourcePath, Project.Current.GetAssetsPath() );
		Directory.CreateDirectory( Path.GetDirectoryName( destination )! );
		if ( !File.Exists( destination ) ) File.Copy( sourcePath, destination, false );
		await GameTask.MainThread( token );
		token.ThrowIfCancellationRequested();
		var asset = AssetSystem.RegisterFile( destination );
		if ( asset?.AssetType != AssetType.SoundFile ) throw new InvalidOperationException( $"Sandbox did not register '{destination}' as a SoundFile." );
		return asset;
	}

	private static ExternalAudioSourceFile CreateFile( string root, string path )
	{
		var fullPath = Path.GetFullPath( path );
		if ( !IsWithinSourceRoot( fullPath, root ) ) throw new InvalidOperationException( $"'{fullPath}' escaped the external audio source root." );
		return new( fullPath, Path.GetRelativePath( root, fullPath ) );
	}

	private static string CanonicalRoot( string path ) => Path.GetFullPath( path ).TrimEnd( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar ) + Path.DirectorySeparatorChar;

	private static bool FilesEqual( string left, string right )
	{
		if ( new FileInfo( left ).Length != new FileInfo( right ).Length ) return false;
		using var leftStream = File.OpenRead( left );
		using var rightStream = File.OpenRead( right );
		return SHA256.HashData( leftStream ).SequenceEqual( SHA256.HashData( rightStream ) );
	}
}

internal readonly record struct ExternalPcmPreview( byte[] Bytes, int Channels, int Rate );

internal static class ExternalWavPreviewDecoder
{
	private const ushort PcmFormat = 1;
	private const ushort FloatFormat = 3;
	private const ushort ExtensibleFormat = 0xFFFE;

	public static ExternalPcmPreview Decode( ReadOnlySpan<byte> wav )
	{
		if ( wav.Length < 12 || !wav[..4].SequenceEqual( "RIFF"u8 ) || !wav.Slice( 8, 4 ).SequenceEqual( "WAVE"u8 ) )
			throw new InvalidDataException( "The preview source is not a RIFF/WAVE file." );

		ReadOnlySpan<byte> formatChunk = default;
		ReadOnlySpan<byte> dataChunk = default;
		for ( var offset = 12; offset <= wav.Length - 8; )
		{
			var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( wav.Slice( offset + 4, 4 ) ));
			var dataOffset = offset + 8;
			if ( chunkSize < 0 || dataOffset > wav.Length - chunkSize )
				throw new InvalidDataException( "The WAV file contains a truncated chunk." );

			var chunkId = wav.Slice( offset, 4 );
			if ( chunkId.SequenceEqual( "fmt "u8 ) )
				formatChunk = wav.Slice( dataOffset, chunkSize );
			else if ( chunkId.SequenceEqual( "data"u8 ) )
				dataChunk = wav.Slice( dataOffset, chunkSize );

			offset = checked(dataOffset + chunkSize + (chunkSize & 1));
		}

		if ( formatChunk.Length < 16 )
			throw new InvalidDataException( "The WAV file has no valid format chunk." );
		if ( dataChunk.IsEmpty )
			throw new InvalidDataException( "The WAV file has no audio data." );

		var format = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk );
		var sourceChannels = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk.Slice( 2, 2 ) );
		var rate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian( formatChunk.Slice( 4, 4 ) ));
		var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk.Slice( 12, 2 ) );
		var containerBits = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk.Slice( 14, 2 ) );
		var validBits = containerBits;

		if ( format == ExtensibleFormat )
		{
			if ( formatChunk.Length < 40 )
				throw new InvalidDataException( "The extensible WAV format chunk is truncated." );
			validBits = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk.Slice( 18, 2 ) );
			format = BinaryPrimitives.ReadUInt16LittleEndian( formatChunk.Slice( 24, 2 ) );
		}

if ( validBits == 0 )
			validBits = containerBits;

		if ( format is not PcmFormat and not FloatFormat )
			throw new InvalidDataException( $"WAV format tag {format} is not supported for preview." );
		if ( sourceChannels == 0 || rate <= 0 || blockAlign == 0 )
			throw new InvalidDataException( "The WAV format contains invalid channel, rate, or frame-size data." );

		var bytesPerSample = (containerBits + 7) / 8;
		if ( bytesPerSample == 0 || blockAlign < sourceChannels * bytesPerSample )
			throw new InvalidDataException( "The WAV sample layout is invalid." );
		if ( format == PcmFormat && containerBits is not (8 or 16 or 24 or 32) )
			throw new InvalidDataException( $"Preview does not support {containerBits}-bit PCM WAV data." );
		if ( format == FloatFormat && containerBits is not (32 or 64) )
			throw new InvalidDataException( $"Preview does not support {containerBits}-bit floating-point WAV data." );

		var outputChannels = sourceChannels == 1 ? 1 : 2;
		var frameCount = dataChunk.Length / blockAlign;
		var output = new byte[checked(frameCount * outputChannels * sizeof(short))];
		for ( var frame = 0; frame < frameCount; frame++ )
		{
			var frameOffset = frame * blockAlign;
			for ( var channel = 0; channel < outputChannels; channel++ )
			{
				var sampleOffset = frameOffset + channel * bytesPerSample;
				var sample = format == FloatFormat
					? ReadFloat( dataChunk.Slice( sampleOffset, bytesPerSample ), containerBits )
					: ReadPcm( dataChunk.Slice( sampleOffset, bytesPerSample ), containerBits, validBits );
				var pcm16 = (short)Math.Clamp( MathF.Round( sample * short.MaxValue ), short.MinValue, short.MaxValue );
				BinaryPrimitives.WriteInt16LittleEndian(
					output.AsSpan( (frame * outputChannels + channel) * sizeof(short), sizeof(short) ),
					pcm16 );
			}
		}

		return new ExternalPcmPreview( output, outputChannels, rate );
	}

	private static float ReadPcm( ReadOnlySpan<byte> sample, int containerBits, int validBits )
	{
		if ( containerBits == 8 )
			return (sample[0] - 128f) / 128f;

		var value = containerBits switch
		{
			16 => BinaryPrimitives.ReadInt16LittleEndian( sample ),
			24 => sample[0] | sample[1] << 8 | sample[2] << 16,
			32 => BinaryPrimitives.ReadInt32LittleEndian( sample ),
			_ => throw new InvalidDataException( $"Unsupported PCM container size {containerBits}." )
		};
		if ( containerBits == 24 && (value & 0x800000) != 0 )
			value |= unchecked((int)0xFF000000);
		if ( validBits > 0 && validBits < containerBits )
			value >>= containerBits - validBits;
		var scale = Math.Pow( 2d, Math.Max( 1, validBits ) - 1 );
		return (float)Math.Clamp( value / scale, -1d, 1d );
	}

	private static float ReadFloat( ReadOnlySpan<byte> sample, int bits )
	{
		var value = bits == 32
			? BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( sample ) )
			: (float)BitConverter.Int64BitsToDouble( BinaryPrimitives.ReadInt64LittleEndian( sample ) );
		return float.IsFinite( value ) ? Math.Clamp( value, -1f, 1f ) : 0f;
	}
}
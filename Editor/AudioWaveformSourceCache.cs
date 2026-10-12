using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace Editor;

/// <summary>Shares pending source decodes. Keeps compact display envelopes, not source PCM.</summary>
internal static class AudioWaveformSourceCache
{
	internal sealed record Waveform( AudioWaveformEnvelope Envelope, float Duration );
	private static readonly Dictionary<string, Task<Waveform>> Entries = new( StringComparer.OrdinalIgnoreCase );
	private static readonly SemaphoreSlim DecodeGate = new( 1, 1 );
	private const long MaximumCachedBytes = 64L * 1024 * 1024;
	internal static int DecodeCount { get; private set; }
	internal static int CacheHits { get; private set; }
	internal static long CachedBytes => Entries.Values.Where( task => task.IsCompletedSuccessfully ).Sum( task => task.Result.Envelope.StorageBytes );
	internal static string Key( SoundFile file ) => AssetSystem.FindByPath( file?.ResourcePath )?.RelativePath ?? file?.ResourcePath ?? "";
	internal static void Invalidate( string path ) => Entries.Remove( path );

	// Dictionary access and completion run on the editor thread. Only the pure PCM scan runs on a worker.
	internal static Task<Waveform> LoadAsync( SoundFile file )
	{
		if ( !file.IsValid() )
			return Task.FromResult( new Waveform( AudioWaveformEnvelope.Create( Array.Empty<short>(), 1 ), .1f ) );
		var key = Key( file );
		if ( Entries.TryGetValue( key, out var existing ) )
		{
			CacheHits++;
			return existing;
		}
		var completion = new TaskCompletionSource<Waveform>();
		Entries[key] = completion.Task;
		_ = LoadCoreAsync( file, key, completion );
		return completion.Task;
	}

	private static async Task LoadCoreAsync( SoundFile file, string key, TaskCompletionSource<Waveform> completion )
	{
		await DecodeGate.WaitAsync();
		try
		{
			await GameTask.MainThread();
			if ( !await file.LoadAsync() )
				throw new InvalidOperationException( "Sound source did not load." );
			DecodeCount++;
			var samples = await file.GetSamplesInterleavedAsync() ?? Array.Empty<short>();
			var channels = Math.Max( 1, file.Channels );
			var duration = MathF.Max( .1f, file.Duration );
			var envelope = await GameTask.RunInThreadAsync( () => AudioWaveformEnvelope.Create( samples, channels ) );
			samples = null;
			await GameTask.MainThread();
			// Widgets retain their envelope even when its completed cache entry is evicted.
			foreach ( var entry in Entries.ToArray() )
			{
				if ( CachedBytes + envelope.StorageBytes <= MaximumCachedBytes )
					break;
				if ( entry.Value.IsCompleted )
					Entries.Remove( entry.Key );
			}
			if ( envelope.StorageBytes > MaximumCachedBytes )
				RemoveIfCurrent( key, completion.Task );
			completion.SetResult( new Waveform( envelope, duration ) );
		}
		catch ( Exception exception )
		{
			await GameTask.MainThread();
			RemoveIfCurrent( key, completion.Task );
			completion.SetException( exception );
		}
		finally
		{
			DecodeGate.Release();
		}
	}

	private static void RemoveIfCurrent( string key, Task<Waveform> task )
	{
		if ( Entries.TryGetValue( key, out var current ) && ReferenceEquals( current, task ) )
			Entries.Remove( key );
	}
}

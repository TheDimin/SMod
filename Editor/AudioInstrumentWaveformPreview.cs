using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Smod;

namespace Editor;

/// <summary>
/// Loads and retains one waveform per graph-owned source track. Track order
/// is preserved so a selected SoundFile maps directly to the visible A/B/C/D lane.
/// </summary>
internal sealed class AudioInstrumentWaveformPreview
{
	private readonly AudioSourceDefinition _source;
	private readonly Action _loaded;
	private IReadOnlyList<AudioVariationWaveform> _variations = Array.Empty<AudioVariationWaveform>();

	public IReadOnlyList<AudioVariationWaveform> Variations => _variations;
	public int Revision { get; private set; }
	public float Duration => MathF.Max( 0.1f, _variations.Select( variation => variation.Duration ).DefaultIfEmpty( 1f ).Max() );

	public AudioInstrumentWaveformPreview( AudioSourceDefinition source, Action loaded )
	{
		_source = source;
		_loaded = loaded;
	}

	public async Task LoadAsync()
	{
		try
		{
			var tracks = _source?.Tracks?.ToArray() ?? Array.Empty<SoundEventTrack>();
			if ( tracks.Length == 0 )
				return;

			var loaded = new List<AudioVariationWaveform>( tracks.Length );
			for ( var index = 0; index < tracks.Length; index++ )
			{
				var file = tracks[index].Sound;
				var waveform = await AudioWaveformSourceCache.LoadAsync( file );
				var sourceDuration = waveform.Duration;
				var offset = MathF.Min( MathF.Max( 0f, tracks[index].Offset ), sourceDuration );
				var duration = tracks[index].Length > 0f
					? MathF.Min( tracks[index].Length, sourceDuration - offset )
					: sourceDuration - offset;
				loaded.Add( new AudioVariationWaveform(
					index,
					tracks[index],
					file,
					waveform.Envelope,
					sourceDuration,
					offset,
					MathF.Max( 0.01f, duration ) ) );
			}

			_variations = loaded;
			Revision++;
			_loaded?.Invoke();
		}
		catch ( Exception exception )
		{
			Log.Warning( $"Could not load waveform preview for source '{_source?.Id}': {exception.Message}" );
		}
	}
}

internal sealed record AudioVariationWaveform(
	int Index,
	SoundEventTrack Track,
	SoundFile Sound,
	AudioWaveformEnvelope Envelope,
	float SourceDuration,
	float Offset,
	float Duration );

using System;
using System.Linq;
using Smod;

namespace Editor;

internal readonly record struct AudioInstrumentVisualState(
	bool IsPlaying,
	bool IsPending,
	bool IsPaused,
	float PlaybackPosition,
	SoundEventTrack SelectedTrack,
	SoundFile SelectedSound )
{
	public static AudioInstrumentVisualState FromPlayback( AudioInstrumentPlaybackState? playback )
	{
		if ( playback is not { } state )
			return default;
		return new AudioInstrumentVisualState(
			state.IsPlaying,
			state.IsPending,
			state.IsPaused,
			state.PlaybackPosition,
			state.SelectedTrack,
			state.SelectedSound );
	}
}

/// <summary>
/// Paints single and multi-instruments. An AudioSourceDefinition with multiple tracks is
/// presented as stacked variation lanes, matching FMOD's multi-instrument language.
/// </summary>
internal static class AudioInstrumentBlockRenderer
{
	private const float HeaderHeight = 18f;
	private const int MaximumVisibleVariations = 12;

	public static void DrawStatic(
		Rect rect,
		Rect clipRect,
		AudioInstrument instrument,
		AudioInstrumentWaveformPreview preview,
		AudioInstrumentVisualState state,
		bool audible,
		double beatsPerMinute = 120d )
	{
		var activeColor = state.IsPaused ? Theme.Yellow : Theme.Green;
		var isActive = state.IsPlaying || state.IsPaused;
		var bodyAlpha = audible ? 0.22f : 0.07f;
		Paint.SetPen( isActive ? activeColor : Theme.Text.WithAlpha( 0.18f ), isActive ? 2f : 1f );
		Paint.SetBrush( Theme.Green.WithAlpha( bodyAlpha ) );
		Paint.DrawRect( rect.Shrink( 1f ), 2f );

		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground.WithAlpha( audible ? 0.72f : 0.45f ) );
		Paint.DrawRect( new Rect( 2f, 2f, MathF.Max( 0f, rect.Width - 4f ), HeaderHeight ) );

		if ( isActive )
		{
			Paint.SetBrush( activeColor.WithAlpha( 0.95f ) );
			Paint.DrawRect( new Rect( 2f, HeaderHeight + 2f, 3f, MathF.Max( 0f, rect.Height - HeaderHeight - 4f ) ), 1f );
		}

		DrawHeader( rect, instrument, preview.Variations.Count, state );
		if ( instrument.IsSubEvent )
		{
			Paint.SetPen( Theme.TextLight );
			Paint.SetDefaultFont( 8f );
			Paint.DrawText( new Rect( 7f, 21f, MathF.Max( 8f, rect.Width - 14f ), 16f ), $"{(instrument.RotateSubEventsAtEnd ? "PARENT LOOP" : "SUB-GRAPH")} | {instrument.SubEvents.Count} choices | {instrument.SubEventSelection}", TextFlag.LeftCenter | TextFlag.SingleLine );
		}
		else if ( instrument.LayerControl is {} control )
        {
            Paint.SetPen(Theme.TextLight);Paint.SetDefaultFont(8f);
            Paint.DrawText(new Rect(7f,21f,MathF.Max(8f,rect.Width-14f),16f),
                control.Mode==AudioMusicControlMode.SustainLayer ? "PARENT CONTROL | continuous source" : "PARENT CONTROL | complete phrases",TextFlag.LeftCenter | TextFlag.SingleLine);
        }
        else if ( instrument.CompoundHits is { Count: > 0 } ) DrawCompoundHits( rect, instrument, beatsPerMinute, audible );
		else DrawVariationLanes( rect, clipRect, instrument, preview, state, audible );
	}

	public static void DrawOverlay(
		Rect rect,
		AudioInstrument instrument,
		AudioInstrumentWaveformPreview preview,
		AudioInstrumentVisualState state,
		bool audible,
		bool hovered,
		bool selected,
		double beatsPerMinute = 120d )
	{
		if ( hovered || selected )
		{
			Paint.ClearPen();
			Paint.SetBrush( Theme.Green.WithAlpha( audible ? 0.12f : 0.025f ) );
			Paint.DrawRect( rect.Shrink( 2f ), 2f );
			Paint.SetPen( Theme.Primary, 2f );
			Paint.DrawLine( new Vector2( 2f, 3f ), new Vector2( 2f, rect.Height - 3f ) );
			Paint.DrawLine( new Vector2( rect.Width - 2f, 3f ), new Vector2( rect.Width - 2f, rect.Height - 3f ) );
		}

		if(instrument.LayerControl is not null)return;
        DrawFadeEnvelope( rect, instrument, preview, audible, hovered || selected, beatsPerMinute );
		if ( instrument.CompoundHits is { Count: > 0 } )
		{
			if ( state.IsPlaying || state.IsPaused )
			{
				var x = rect.Width * (state.PlaybackPosition / AudioTimelineView.EstimateDuration( instrument, beatsPerMinute )).Clamp( 0f, 1f );
				Paint.SetPen( Color.White.WithAlpha( audible ? .82f : .24f ), 1f );
				Paint.DrawLine( new Vector2( x, HeaderHeight + 3f ), new Vector2( x, rect.Bottom - 3f ) );
			}
			return;
		}
		if ( !instrument.IsSubEvent ) DrawPlaybackPosition( rect, instrument, preview, state, audible );
	}

	private static void DrawCompoundHits( Rect rect, AudioInstrument instrument, double beatsPerMinute, bool audible )
	{
		var duration = AudioTimelineView.EstimateDuration( instrument, beatsPerMinute );
		var hits = instrument.CompoundHits;
		var height = MathF.Max( 1f, (rect.Height - HeaderHeight - 6f) / hits.Count );
		for ( var index = 0; index < hits.Count; index++ )
		{
			var hit = hits[index];
			if ( hit is null || !hit.IsValid ) continue;
			var start = (float)(hit.BeatOffset * 60d / Math.Max( 20d, beatsPerMinute ));
			var x = start / duration * rect.Width;
			var width = MathF.Max( 1f, hit.Length / duration * rect.Width );
			Paint.ClearPen();
			Paint.SetBrush( Theme.Green.WithAlpha( (audible ? .65f : .15f) * hit.Gain ) );
			Paint.DrawRect( new Rect( x, HeaderHeight + 3f + index * height, width, MathF.Max( 1f, height - 1f ) ), 1f );
		}
	}

	private static void DrawFadeEnvelope(
		Rect rect,
		AudioInstrument instrument,
		AudioInstrumentWaveformPreview preview,
		bool audible,
		bool showHandles, double beatsPerMinute )
	{
		var duration = preview.Variations
			.Select( variation => TrimmedDuration( instrument, variation.Duration ) )
			.DefaultIfEmpty( 0.01f )
			.Max();
		if ( instrument.IsSubEvent || instrument.CompoundHits is { Count: > 0 } ) duration = AudioTimelineView.EstimateDuration( instrument, beatsPerMinute );
		var fadeInX = (instrument.FadeInDuration / duration * rect.Width).Clamp( 0f, rect.Width );
		var fadeOutX = rect.Width - (instrument.FadeOutDuration / duration * rect.Width).Clamp( 0f, rect.Width );
		if ( instrument.FadeInDuration > 0f )
		{
			Paint.SetPen( Theme.Primary.WithAlpha( audible ? 0.9f : 0.35f ), 2f );
			DrawFadeCurve( 2f, fadeInX, 0f, instrument.FadeInCurveMidpoint, 1f, rect );
		}
		if ( instrument.FadeOutDuration > 0f )
		{
			Paint.SetPen( Theme.Primary.WithAlpha( audible ? 0.9f : 0.35f ), 2f );
			DrawFadeCurve( fadeOutX, rect.Right - 2f, 1f, instrument.FadeOutCurveMidpoint, 0f, rect );
		}
		if ( !showHandles )
			return;

		Paint.ClearPen();
		Paint.SetBrush( Theme.Primary );
		Paint.DrawRect( new Rect( fadeInX - 3f, 0f, 6f, 6f ), 1f );
		Paint.DrawRect( new Rect( fadeOutX - 3f, 0f, 6f, 6f ), 1f );
		if ( instrument.FadeInDuration > 0f )
		{
			var controlY = rect.Bottom - 3f - instrument.FadeInCurveMidpoint * (rect.Height - 6f);
			var control = new Vector2( (fadeInX + 2f) * 0.5f, controlY );
			Paint.SetPen( Theme.Primary.WithAlpha( 0.3f ), 1f );
			Paint.DrawLine( new Vector2( 2f, rect.Bottom - 3f ), control );
			Paint.DrawLine( control, new Vector2( fadeInX, 3f ) );
			Paint.ClearPen();
			Paint.SetBrush( Theme.Primary );
			Paint.DrawRect( new Rect( control.x - 3f, control.y - 3f, 6f, 6f ), 3f );
		}
		if ( instrument.FadeOutDuration > 0f )
		{
			var controlY = rect.Bottom - 3f - instrument.FadeOutCurveMidpoint * (rect.Height - 6f);
			var control = new Vector2( (fadeOutX + rect.Right - 2f) * 0.5f, controlY );
			Paint.SetPen( Theme.Primary.WithAlpha( 0.3f ), 1f );
			Paint.DrawLine( new Vector2( fadeOutX, 3f ), control );
			Paint.DrawLine( control, new Vector2( rect.Right - 2f, rect.Bottom - 3f ) );
			Paint.ClearPen();
			Paint.SetBrush( Theme.Primary );
			Paint.DrawRect( new Rect( control.x - 3f, control.y - 3f, 6f, 6f ), 3f );
		}
	}

	private static void DrawFadeCurve( float startX, float endX, float startGain, float midpointGain, float endGain, Rect rect )
	{
		const int segments = 20;
		var previous = new Vector2( startX, rect.Bottom - 3f - startGain * (rect.Height - 6f) );
		for ( var segment = 1; segment <= segments; segment++ )
		{
			var progress = segment / (float)segments;
			var inverse = 1f - progress;
			var gain = inverse * inverse * startGain
				+ 2f * inverse * progress * midpointGain
				+ progress * progress * endGain;
			var current = new Vector2(
				startX + (endX - startX) * progress,
				rect.Bottom - 3f - gain * (rect.Height - 6f) );
			Paint.DrawLine( previous, current );
			previous = current;
		}
	}

	private static void DrawHeader( Rect rect, AudioInstrument instrument, int variationCount, AudioInstrumentVisualState state )
	{
		var behaviorCount = (instrument.Conditions?.Count ?? 0) + (instrument.Automations?.Count ?? 0);
		var rightWidth = behaviorCount > 0 ? 54f : 8f;
		Paint.SetPen( Theme.Text );
		Paint.SetDefaultFont( 8f, 650 );
		Paint.DrawText(
			new Rect( 7f, 2f, MathF.Max( 12f, rect.Width - rightWidth - 7f ), HeaderHeight ),
			AudioTimelineView.InstrumentName( instrument ),
			TextFlag.LeftCenter | TextFlag.SingleLine );

		if ( behaviorCount > 0 )
		{
			Paint.SetDefaultFont( 6.5f, 700 );
			Paint.SetPen( Theme.TextLight );
			Paint.DrawText(
				new Rect( MathF.Max( 4f, rect.Width - 50f ), 2f, 44f, HeaderHeight ),
				$"{instrument.Conditions?.Count ?? 0}C {instrument.Automations?.Count ?? 0}A",
				TextFlag.RightCenter | TextFlag.SingleLine );
		}

		if ( state.IsPending )
		{
			Paint.SetPen( Theme.Yellow );
			Paint.SetDefaultFont( 6.5f, 700 );
			Paint.DrawText( new Rect( 7f, 2f, rect.Width - 14f, HeaderHeight ), "WAIT", TextFlag.RightCenter );
		}
		else if ( variationCount > MaximumVisibleVariations )
		{
			Paint.SetPen( Theme.TextLight );
			Paint.SetDefaultFont( 6.5f, 700 );
			Paint.DrawText( new Rect( 7f, 2f, rect.Width - 14f, HeaderHeight ), $"+{variationCount - MaximumVisibleVariations}", TextFlag.RightCenter );
		}
	}

	private static void DrawVariationLanes(
		Rect rect,
		Rect clipRect,
		AudioInstrument instrument,
		AudioInstrumentWaveformPreview preview,
		AudioInstrumentVisualState state,
		bool audible )
	{
		var variations = preview.Variations;
		var visibleCount = Math.Min( MaximumVisibleVariations, variations.Count );
		if ( visibleCount == 0 )
			return;

		var multiInstrument = variations.Count > 1;
		var labelWidth = multiInstrument ? 16f : 0f;
		var laneLeft = 6f;
		var laneTop = HeaderHeight + 3f;
		var laneAreaHeight = MathF.Max( 4f, rect.Height - laneTop - 3f );
		var laneHeight = laneAreaHeight / visibleCount;
		var laneWidth = MathF.Max( 4f, rect.Width - laneLeft - labelWidth - 4f );
		var longestTrimmedDuration = variations
			.Select( variation => TrimmedDuration( instrument, variation.Duration ) )
			.DefaultIfEmpty( 0.01f )
			.Max();

		for ( var visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++ )
		{
			var variation = variations[visibleIndex];
			var lane = new Rect( laneLeft, laneTop + visibleIndex * laneHeight, laneWidth, laneHeight );
			var selectedVariation = (state.IsPlaying || state.IsPaused)
				&& IsSelectedVariation( variation, state );
			DrawLaneBackground( lane, clipRect, selectedVariation, state, audible );
			DrawWaveform( lane, clipRect, instrument, variation, longestTrimmedDuration, selectedVariation, state, audible );

			if ( multiInstrument )
				DrawVariationLabel( new Rect( lane.Right, lane.Top, labelWidth, lane.Height ), variation.Index, selectedVariation, state );
		}
	}

	private static void DrawLaneBackground( Rect lane, Rect clipRect, bool selectedVariation, AudioInstrumentVisualState state, bool audible )
	{
		Paint.ClearPen();
		Paint.SetBrush( selectedVariation
			? (state.IsPaused ? Theme.Yellow : Theme.Green).WithAlpha( audible ? 0.34f : 0.12f )
			: Theme.WidgetBackground.WithAlpha( audible ? 0.42f : 0.18f ) );
		Paint.DrawRect( lane.Shrink( 0.5f ) );
		Paint.SetPen( Theme.Text.WithAlpha( selectedVariation ? 0.2f : 0.08f ) );
		var patternStart = lane.Left + 2f;
		var firstPattern = Math.Max( 0, (int)MathF.Floor( (clipRect.Left - patternStart) / 7f ) );
		var finalX = MathF.Min( lane.Right - 1f, clipRect.Right );
		for ( var x = patternStart + firstPattern * 7f; x < finalX; x += 7f )
		{
			var length = MathF.Min( 5f, lane.Right - x - 1f );
			Paint.DrawLine(
				new Vector2( x, lane.Bottom - 1f ),
				new Vector2( x + length, MathF.Max( lane.Top + 1f, lane.Bottom - 1f - length ) ) );
		}
	}

	private static void DrawWaveform(
		Rect lane,
		Rect clipRect,
		AudioInstrument instrument,
		AudioVariationWaveform variation,
		float longestDuration,
		bool selectedVariation,
		AudioInstrumentVisualState state,
		bool audible )
	{
		if ( variation.Envelope.FrameCount == 0 )
			return;

		var trimStart = MathF.Min( instrument.TrimStart, variation.Duration );
		var trimEnd = instrument.TrimEnd > 0f ? MathF.Min( instrument.TrimEnd, variation.Duration ) : variation.Duration;
		var trimmedDuration = MathF.Max( 0.01f, trimEnd - MathF.Min( trimStart, trimEnd ) );
		var durationFraction = (trimmedDuration / MathF.Max( 0.01f, longestDuration )).Clamp( 0.02f, 1f );
		var waveformWidth = MathF.Max( 1f, lane.Width * durationFraction );
		var columns = Math.Max( 1, (int)waveformWidth );
		var frames = variation.Envelope.FrameCount;
		if ( frames == 0 )
			return;
		var firstFrame = Math.Clamp( (int)((variation.Offset + trimStart) / MathF.Max( 0.01f, variation.SourceDuration ) * frames), 0, frames );
		var finalFrame = Math.Clamp( (int)MathF.Ceiling( (variation.Offset + trimEnd) / MathF.Max( 0.01f, variation.SourceDuration ) * frames ), firstFrame, frames );
		var trimmedFrames = Math.Max( 1, finalFrame - firstFrame );

		var playingColor = state.IsPaused ? Theme.Yellow : Theme.Green;
		Paint.SetPen( selectedVariation
			? playingColor.WithAlpha( audible ? 0.98f : 0.3f )
			: Theme.Text.WithAlpha( audible ? 0.46f : 0.14f ) );
		var firstColumn = Math.Clamp( (int)MathF.Floor( clipRect.Left - lane.Left ), 0, columns );
		var finalColumn = Math.Clamp( (int)MathF.Ceiling( clipRect.Right - lane.Left ), firstColumn, columns );
		for ( var column = firstColumn; column < finalColumn; column++ )
		{
			var startFrame = firstFrame + column * trimmedFrames / columns;
			var endFrame = Math.Min( finalFrame, Math.Max( startFrame + 1, firstFrame + (column + 1) * trimmedFrames / columns ) );
			var peak = variation.Envelope.GetPeak( startFrame, endFrame );

			var center = lane.Top + lane.Height * 0.5f;
			var amplitude = MathF.Max( 1f, lane.Height * 0.43f );
			var y1 = center - peak.Maximum / (float)short.MaxValue * amplitude;
			var y2 = center - peak.Minimum / (float)short.MaxValue * amplitude;
			Paint.DrawLine( new Vector2( lane.Left + column, y1 ), new Vector2( lane.Left + column, y2 ) );
		}
	}

	private static void DrawPlaybackPosition(
		Rect rect,
		AudioInstrument instrument,
		AudioInstrumentWaveformPreview preview,
		AudioInstrumentVisualState state,
		bool audible )
	{
		if ( !state.IsPlaying && !state.IsPaused )
			return;

		var variations = preview.Variations;
		var visibleCount = Math.Min( MaximumVisibleVariations, variations.Count );
		if ( visibleCount == 0 )
			return;

		var multiInstrument = variations.Count > 1;
		var labelWidth = multiInstrument ? 16f : 0f;
		var laneLeft = 6f;
		var laneTop = HeaderHeight + 3f;
		var laneAreaHeight = MathF.Max( 4f, rect.Height - laneTop - 3f );
		var laneHeight = laneAreaHeight / visibleCount;
		var laneWidth = MathF.Max( 4f, rect.Width - laneLeft - labelWidth - 4f );
		var longestTrimmedDuration = variations
			.Select( variation => TrimmedDuration( instrument, variation.Duration ) )
			.DefaultIfEmpty( 0.01f )
			.Max();

		for ( var visibleIndex = 0; visibleIndex < visibleCount; visibleIndex++ )
		{
			var variation = variations[visibleIndex];
			if ( !IsSelectedVariation( variation, state ) )
				continue;

			var lane = new Rect( laneLeft, laneTop + visibleIndex * laneHeight, laneWidth, laneHeight );
			var trimStart = MathF.Min( instrument.TrimStart, variation.Duration );
			var trimEnd = instrument.TrimEnd > 0f ? MathF.Min( instrument.TrimEnd, variation.Duration ) : variation.Duration;
			var trimmedDuration = MathF.Max( 0.01f, trimEnd - MathF.Min( trimStart, trimEnd ) );
			var durationFraction = (trimmedDuration / MathF.Max( 0.01f, longestTrimmedDuration )).Clamp( 0.02f, 1f );
			var waveformWidth = MathF.Max( 1f, lane.Width * durationFraction );
			var playbackFraction = ((state.PlaybackPosition - variation.Offset - trimStart) / trimmedDuration).Clamp( 0f, 1f );
			var playbackX = lane.Left + waveformWidth * playbackFraction;
			Paint.SetPen( Color.White.WithAlpha( audible ? 0.82f : 0.24f ), 1f );
			Paint.DrawLine( new Vector2( playbackX, lane.Top + 1f ), new Vector2( playbackX, lane.Bottom - 1f ) );
			return;
		}
	}

	private static float TrimmedDuration( AudioInstrument instrument, float sourceDuration )
	{
		var trimEnd = instrument.TrimEnd > 0f ? MathF.Min( instrument.TrimEnd, sourceDuration ) : sourceDuration;
		return MathF.Max( 0.01f, trimEnd - MathF.Min( instrument.TrimStart, trimEnd ) );
	}

	private static void DrawVariationLabel( Rect rect, int index, bool selectedVariation, AudioInstrumentVisualState state )
	{
		var color = state.IsPaused ? Theme.Yellow : Theme.Green;
		Paint.ClearPen();
		Paint.SetBrush( selectedVariation ? color.WithAlpha( 0.9f ) : Theme.ControlBackground.WithAlpha( 0.9f ) );
		Paint.DrawRect( rect.Shrink( 1f ), 2f );
		Paint.SetPen( selectedVariation ? Color.White : Theme.TextLight );
		Paint.SetDefaultFont( MathF.Max( 5f, MathF.Min( 7f, rect.Height - 1f ) ), 700 );
		var label = index < 26 ? ((char)('A' + index)).ToString() : (index + 1).ToString();
		Paint.DrawText( rect, label, TextFlag.Center | TextFlag.SingleLine );
	}

	private static bool IsSelectedVariation( AudioVariationWaveform variation, AudioInstrumentVisualState state )
	{
		if ( ReferenceEquals( variation.Track, state.SelectedTrack ) )
			return true;
		if ( variation.Sound is null || state.SelectedSound is null || state.SelectedTrack is not null )
			return false;
		return !string.IsNullOrWhiteSpace( variation.Sound.ResourcePath )
			&& string.Equals( variation.Sound.ResourcePath, state.SelectedSound.ResourcePath, StringComparison.OrdinalIgnoreCase );
	}
}

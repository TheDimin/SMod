using System;

namespace Editor;

internal readonly record struct AudioInstrumentRenderCacheKey(
	int Width,
	int Height,
	int WaveformRevision,
	float TrimStart,
	float TrimEnd,
	float FadeInDuration,
	float FadeInCurveMidpoint,
	float FadeOutDuration,
	float FadeOutCurveMidpoint,
	int ContentSignature,
	bool Audible,
	bool IsPlaying,
	bool IsPending,
	bool IsPaused,
	int SelectedVariationSignature,
	int ThemeSignature )
{
	public static AudioInstrumentRenderCacheKey Create(
		int width,
		int height,
		int waveformRevision,
		float trimStart,
		float trimEnd,
		float fadeInDuration,
		float fadeInCurveMidpoint,
		float fadeOutDuration,
		float fadeOutCurveMidpoint,
		int contentSignature,
		bool audible,
		bool isPlaying,
		bool isPending,
		bool isPaused,
		int selectedVariationSignature,
		int themeSignature,
		float positionX,
		float positionY,
		float playbackPosition,
		bool hovered,
		bool selected )
	{
		_ = positionX;
		_ = positionY;
		_ = playbackPosition;
		_ = hovered;
		_ = selected;
		return new AudioInstrumentRenderCacheKey(
			width,
			height,
			waveformRevision,
			trimStart,
			trimEnd,
			fadeInDuration,
			fadeInCurveMidpoint,
			fadeOutDuration,
			fadeOutCurveMidpoint,
			contentSignature,
			audible,
			isPlaying,
			isPending,
			isPaused,
			selectedVariationSignature,
			themeSignature );
	}
}

namespace Editor;

internal static class AudioPreviewTransportMath
{
	public static float ResolveStartPosition( float timelinePosition, float audioContentEnd )
	{
		if ( !float.IsFinite( timelinePosition ) || timelinePosition < 0f )
			return 0f;
		if ( timelinePosition >= audioContentEnd )
			return 0f;
		return timelinePosition;
	}

	public static bool ShouldRepeatPreview(
		bool repeatEnabled,
		bool paused,
		float timelinePosition,
		IReadOnlyList<Smod.AudioInstrumentPlaybackState> instrumentStates )
	{
		return repeatEnabled
			&& !paused
			&& timelinePosition > 0f
			&& instrumentStates.All( state => !state.IsPlaying && !state.IsPending );
	}
}

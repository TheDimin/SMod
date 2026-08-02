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
}

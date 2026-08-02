using System;

namespace Editor;

internal static class AudioTimelineScaleMath
{
	public const float MaximumPixelsPerSecond = 900f;
	public const float TargetTickSpacing = 56f;

	public static float ConstrainPixelsPerSecond( float pixelsPerSecond )
	{
		if ( !float.IsFinite( pixelsPerSecond ) || pixelsPerSecond <= 0f )
			return float.Epsilon;
		return MathF.Min( MaximumPixelsPerSecond, pixelsPerSecond );
	}

	public static float CalculateTickInterval( float pixelsPerSecond )
	{
		pixelsPerSecond = ConstrainPixelsPerSecond( pixelsPerSecond );
		var requiredSeconds = Math.Max( 1d, TargetTickSpacing / (double)pixelsPerSecond );
		var magnitude = Math.Pow( 10d, Math.Floor( Math.Log10( requiredSeconds ) ) );
		var normalized = requiredSeconds / magnitude;
		var multiplier = normalized <= 1d
			? 1d
			: normalized <= 2d ? 2d : normalized <= 5d ? 5d : 10d;
		return (float)Math.Min( float.MaxValue, multiplier * magnitude );
	}
}

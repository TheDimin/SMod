using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Smod;

namespace Smod.Editor;

internal static class AudioTimelineInspectorUx
{
	public static bool ShowInGenericSheet( object selection, string propertyName )
	{
		if ( selection is DestinationMarker or TransitionMarker or TransitionRegion or LoopRegion
			&& propertyName == nameof( DestinationMarker.Id ) )
			return false;

		return selection switch
		{
			DestinationMarker => propertyName != nameof( DestinationMarker.Position ),
			TransitionMarker => propertyName is not nameof( TransitionMarker.Position )
				and not nameof( TransitionMarker.DestinationMarkerId ),
			TransitionRegion => propertyName is not nameof( TransitionRegion.Start )
				and not nameof( TransitionRegion.End )
				and not nameof( TransitionRegion.DestinationMarkerId ),
			LoopRegion => propertyName is not nameof( LoopRegion.Start )
				and not nameof( LoopRegion.End ),
			_ => true
		};
	}

	public static IReadOnlyList<AudioDestinationChoice> BuildDestinationChoices( AudioEvent audioEvent )
	{
		var markers = audioEvent?.DestinationMarkers ?? new();
		var choices = new List<AudioDestinationChoice>( markers.Count );
		for ( var index = 0; index < markers.Count; index++ )
		{
			var marker = markers[index];
			if ( marker is null )
				continue;
			var name = string.IsNullOrWhiteSpace( marker.Name ) ? $"Destination {index + 1}" : marker.Name.Trim();
			choices.Add( new AudioDestinationChoice(
				marker.Id,
				$"{name} ({marker.Position.Seconds.ToString( "0.000", CultureInfo.InvariantCulture )} s)" ) );
		}
		return choices;
	}

	public static AudioSoundSourceAccess ResolveSoundSourceAccess( SoundEventResource sound )
	{
		if ( sound is null )
			return AudioSoundSourceAccess.None;

		return sound.EmbeddedResource is not null
			? AudioSoundSourceAccess.EditEmbedded
			: AudioSoundSourceAccess.OpenExternal;
	}

	public static bool TryParseSeconds( string text, out TimelineTime value )
	{
		value = default;
		if ( !double.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds )
			|| !double.IsFinite( seconds )
			|| seconds < 0d )
			return false;

		value = new TimelineTime( seconds );
		return true;
	}
}

internal enum AudioSoundSourceAccess
{
	None,
	EditEmbedded,
	OpenExternal
}

internal readonly record struct AudioDestinationChoice( Guid Id, string Label );

internal sealed class AudioTimelineSecondsEditSession
{
	private bool _hasUserEdit;

	public bool HasUserEdit => _hasUserEdit;

	public void MarkUserEdit()
	{
		_hasUserEdit = true;
	}

	public bool TryGetEditedValue( string text, out TimelineTime value )
	{
		if ( !_hasUserEdit )
		{
			value = default;
			return false;
		}

		return AudioTimelineInspectorUx.TryParseSeconds( text, out value );
	}
}

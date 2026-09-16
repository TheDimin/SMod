using System.Runtime.CompilerServices;

namespace Smod;

/// <summary>Runtime variation history shared by successive plays of one source definition.
/// Weak keys let unused definitions unload. Authored definitions hold no playback state.</summary>
internal static class AudioSourceSelectionHistory
{
 private sealed class History
 {
  public SoundEventTrack Last;
 }
 private static readonly ConditionalWeakTable<object, History> Histories = new();

 internal static SoundEventTrack Select( object source, SoundTrackSelectionMode mode,
  IReadOnlyList<SoundEventTrack> eligible )
 {
  ArgumentNullException.ThrowIfNull( source );
  ArgumentNullException.ThrowIfNull( eligible );
  if ( eligible.Count == 0 )
   throw new ArgumentException( "At least one eligible sound track is required.", nameof( eligible ) );

  var history = Histories.GetValue( source, static _ => new History() );
  lock ( history )
  {
   var count = 0;
   if ( mode == SoundTrackSelectionMode.RandomExclusive )
    for ( var i = 0; i < eligible.Count; i++ )
     if ( !ReferenceEquals( eligible[i], history.Last ) ) count++;

   SoundEventTrack selected;
   if ( count == 0 )
    selected = eligible[Random.Shared.Next( eligible.Count )];
   else
   {
    var index = Random.Shared.Next( count );
    selected = null;
    for ( var i = 0; i < eligible.Count; i++ )
     if ( !ReferenceEquals( eligible[i], history.Last ) && index-- == 0 )
     {
      selected = eligible[i];
      break;
     }
   }
   history.Last = selected;
   return selected;
  }
 }
}

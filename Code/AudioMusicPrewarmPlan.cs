namespace Smod;

/// <summary>Exact authored action ranges prepared before the shared song clock starts.</summary>
internal sealed record AudioMusicPrewarmRange(SoundFile Source,float Offset,float Length,AudioTone Tone);
internal static class AudioMusicPrewarmPlan
{
 internal static AudioMusicPrewarmRange[] Build(AudioEvent resource)
 {
  var ranges=new List<AudioMusicPrewarmRange>();
  foreach(var track in resource.Tracks.Prepend(resource.MasterTrack).Where(t=>t?.Overlay?.Enabled==true))
  foreach(var instrument in track.Instruments)
  foreach(var source in resource.Source(instrument)?.Tracks ?? new())
  {
   if(source?.Sound is null)continue;
   var tone=ReferenceEquals(track,resource.MasterTrack) || track.Tone is null?null:new AudioTone{BassGainDb=track.Tone.BassGainDb,BassFrequencyHz=track.Tone.BassFrequencyHz};
   if(instrument.CompoundHits is {Count:>0} hits)
   {
    foreach(var hit in hits)ranges.Add(new(source.Sound,source.Offset+hit.SourceOffset,hit.Length,tone));
   }
   else
   {
    var range=AudioMusicSourcePreparation.ResolveRange(instrument,source);
    ranges.Add(new(source.Sound,range.Offset,range.Length,tone));
   }
  }
  return ranges.ToArray();
 }
}

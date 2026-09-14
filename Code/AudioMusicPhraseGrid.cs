namespace Smod;

/// <summary>Find a complete phrase entry on the authored grid. Restricted bars mean bar starts, never a later beat within that bar.</summary>
internal static class AudioMusicPhraseGrid
{
 public static long? Next(AudioBeatGrid grid,long earliest,int quantumBeats,int phraseFrames,IReadOnlyCollection<int> entryBars=null)
 {
  if(earliest<0 || quantumBeats<1 || phraseFrames<1)throw new ArgumentOutOfRangeException(nameof(earliest));
  var end=grid.End.Seconds*44100;
  if(entryBars is not {Count:>0})
  {
   var next=grid.NextSampleBoundary(earliest,44100,quantumBeats);
   return next.HasValue && next.Value<=end-phraseFrames ? next : null;
  }
  long? best=null;
  foreach(var bar in entryBars)
  {
   if(bar<1)throw new ArgumentOutOfRangeException(nameof(entryBars),"Entry bars are one-based.");
   var beat=(long)(bar-1)*grid.BeatsPerBar;
   if(beat%quantumBeats!=0)continue;
   var frame=(grid.Start.Seconds+beat*grid.SecondsPerBeat)*44100;
   if(!double.IsFinite(frame) || frame<earliest-.5 || frame>end-phraseFrames+.5 || frame>=long.MaxValue)continue;
   var candidate=(long)Math.Round(frame,MidpointRounding.AwayFromZero);
   if(candidate<earliest || candidate>long.MaxValue-phraseFrames)continue;
   if(!best.HasValue || candidate<best.Value)best=candidate;
  }
  return best;
 }
}

namespace Smod;

/// <summary>Maps an endless session clock to one retained composition's local beat grid.</summary>
internal sealed class AudioMusicLoopGrid
{
 readonly AudioBeatGrid local;
 readonly long frames;
 public AudioMusicLoopGrid(AudioBeatGrid authored,long backingFrames)
 {
  ArgumentNullException.ThrowIfNull(authored);
  var duration=authored.End.Seconds-authored.Start.Seconds;
  if(!authored.IsValid || !double.IsFinite(duration) || backingFrames<1 || Math.Abs(duration*44100-backingFrames)>.51)
   throw new ArgumentException("Backing length must match the authored composition grid.");
  var bars=duration/authored.SecondsPerBeat/authored.BeatsPerBar;
  if(Math.Abs(bars-Math.Round(bars))*authored.SecondsPerBeat*authored.BeatsPerBar*44100>.51)
   throw new ArgumentException("A repeating composition must contain complete bars.");
  frames=backingFrames;
  local=new AudioBeatGrid{Name=authored.Name,Start=new(0),End=new(duration),BeatsPerMinute=authored.BeatsPerMinute,BeatsPerBar=authored.BeatsPerBar,BarsPerPhrase=authored.BarsPerPhrase};
 }
 public long? Next(long earliest,int quantumBeats,int phraseFrames,IReadOnlyCollection<int> entryBars=null)
 {
  if(earliest<0 || phraseFrames<1 || quantumBeats<1)throw new ArgumentOutOfRangeException(nameof(earliest));
  if(phraseFrames>frames)return null;
  var cycle=earliest/frames;var offset=earliest%frames;
  var candidate=AudioMusicPhraseGrid.Next(local,offset,quantumBeats,phraseFrames,entryBars);
  if(candidate is null || candidate.Value>frames-phraseFrames)
  {
   // Defer the complete phrase to the next composition entry; never trim its ending.
   if(cycle>=long.MaxValue/frames)return null;
   cycle++;candidate=AudioMusicPhraseGrid.Next(local,0,quantumBeats,phraseFrames,entryBars);
   if(candidate is null)return null;
  }
  if(cycle>(long.MaxValue-candidate.Value)/frames)return null;
  var result=cycle*frames+candidate.Value;
  return result>long.MaxValue-phraseFrames ? null : result;
 }
}

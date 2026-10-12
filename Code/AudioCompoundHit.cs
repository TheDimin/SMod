namespace Smod;

/// <summary>One complete hit from the selected Sound Event source. Offsets are relative to its source-track offset.</summary>
public sealed class AudioCompoundHit
{
 [Property] public float SourceOffset {get;set;}
 [Property] public float Length {get;set;}
 [Property, Range(0,16)] public float BeatOffset {get;set;}
 [Property, Range(0,1)] public float Gain {get;set;}=1;
 internal bool IsValid => float.IsFinite(SourceOffset)&&SourceOffset>=0 && float.IsFinite(Length)&&Length>0
  && float.IsFinite(BeatOffset)&&BeatOffset>=0&&BeatOffset<=16 && float.IsFinite(Gain)&&Gain>=0&&Gain<=1;
}

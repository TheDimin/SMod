namespace Smod;

/// <summary>Audio-owned selection and arrangement rules. Conditions read world-state parameters.</summary>
public sealed class AudioMusicPlaylist
{
 [Property] public AudioParameter ContextParameter { get; set; }
 [Property, InlineEditor] public List<AudioMusicSection> Sections { get; set; } = new();
 [Property] public int VariationBars { get; set; } = 8;
 [Property] public int VariationCount { get; set; } = 4;
 [Property] public float VariationBlendBeats { get; set; } = 1;
 [Property] public float TransitionFadeBeats { get; set; } = .25f;
}
public sealed class AudioMusicSection
{
 [Property] public Guid DestinationMarkerId { get; set; }
 [Property, InlineEditor] public ParameterConditionSet Conditions { get; set; } = new();
 [Property, InlineEditor] public ParameterConditionSet EntryConditions { get; set; } = new();
 [Property] public bool RotateAtEnd { get; set; } = true;
}
public sealed partial class AudioEvent
{
 [Property, InlineEditor, System.Diagnostics.CodeAnalysis.AllowNull] public AudioMusicPlaylist MusicPlaylist { get; set; }
}

/// <summary>Per-instance choices. Authored event resources remain unchanged.</summary>
internal sealed class AudioMusicDirector
{
 readonly AudioEvent resource;
 readonly Random random;
 readonly List<AudioMusicSection> bag = new();
 AudioParameterValue context;
 AudioMusicSection current, requested;
 int block = -1, choice;
 double lastTime;
 float target;
 public float Variation { get; private set; }
 public TransitionRegion Transition { get; private set; }
 public string CurrentName => Marker(current)?.Name;
 public string PendingName => Marker(requested)?.Name;
 AudioMusicPlaylist Definition => resource.MusicPlaylist;
 DestinationMarker Marker(AudioMusicSection section) => resource.DestinationMarkers.FirstOrDefault(x=>x.Id==section?.DestinationMarkerId);
 bool Matches(ParameterConditionSet conditions,IReadOnlyDictionary<AudioParameter,AudioParameterValue> values) => AudioTimelineControlEvaluator.Matches(conditions,values);
 List<AudioMusicSection> Eligible(IReadOnlyDictionary<AudioParameter,AudioParameterValue> values) => Definition.Sections.Where(x=>Matches(x.Conditions,values)).ToList();
 AudioMusicSection Entry(List<AudioMusicSection> eligible,IReadOnlyDictionary<AudioParameter,AudioParameterValue> values) => eligible.FirstOrDefault(x=>Matches(x.EntryConditions,values)) ?? eligible.FirstOrDefault();
 public AudioMusicDirector(AudioEvent resource,int? seed=null) {this.resource=resource;random=seed.HasValue ? new Random(seed.Value) : new Random();}
 public double Start(IReadOnlyDictionary<AudioParameter,AudioParameterValue> values,double fallback)
 {
  bag.Clear();Transition=null;requested=null;block=-1;choice=0;Variation=target=0;lastTime=0;
  context=values[Definition.ContextParameter];current=Entry(Eligible(values),values);
  return Marker(current)?.Position.Seconds ?? fallback;
 }
 public void Update(double position,double now,IReadOnlyDictionary<AudioParameter,AudioParameterValue> values)
 {
  var grid=resource.BeatGridAt(position);
  if(grid is null)return;
  var active=Definition.Sections.FirstOrDefault(x=>Math.Abs((Marker(x)?.Position.Seconds ?? -1)-grid.Start.Seconds)<.0001);
  if(active!=current) {current=active;requested=null;Transition=null;block=-1;}
  var elapsed=lastTime==0 ? 0 : Math.Max(0,now-lastTime);lastTime=now;
  var nextBlock=(int)((position-grid.Start.Seconds)/(grid.SecondsPerBeat*grid.BeatsPerBar*Definition.VariationBars));
  if(nextBlock!=block)
  {
   if(block>=0 && Definition.VariationCount>1) {choice=(choice+1+random.Next(Definition.VariationCount-1))%Definition.VariationCount;target=choice/(float)(Definition.VariationCount-1);}
   block=nextBlock;
  }
  var step=(float)(elapsed/(grid.SecondsPerBeat*Definition.VariationBlendBeats));
  Variation+=Math.Clamp(target-Variation,-step,step);
  var eligible=Eligible(values);
  var changed=!context.Equals(values[Definition.ContextParameter]);context=values[Definition.ContextParameter];
  if(changed || (current is not null && !eligible.Contains(current)))
  {
   bag.Clear();Request(Entry(eligible,values),grid);
  }
  else if(requested is not null && !eligible.Contains(requested)) {requested=null;Transition=null;}
  var phrase=grid.SecondsPerBeat*grid.BeatsPerBar*grid.BarsPerPhrase;
  if(Transition is null && current?.RotateAtEnd==true && grid.End.Seconds-position<phrase-.05)
  {
   bag.RemoveAll(x=>x==current || !eligible.Contains(x));
   if(bag.Count==0)bag.AddRange(eligible.Where(x=>x!=current));
   if(bag.Count>0) {var i=random.Next(bag.Count);var next=bag[i];bag.RemoveAt(i);Request(next,grid);}
  }
 }
 void Request(AudioMusicSection next,AudioBeatGrid grid)
 {
  if(next==current || next is null) {requested=null;Transition=null;return;}
  if(requested==next && Transition is not null)return;
  requested=next;
  Transition=new TransitionRegion {Name="Music selection",Start=new(Math.Max(grid.Start.Seconds,grid.End.Seconds-grid.SecondsPerBeat*grid.BeatsPerBar*grid.BarsPerPhrase+.001)),End=grid.End,DestinationMarkerId=next.DestinationMarkerId,
   Quantization=TransitionQuantization.NextPhrase,Priority=int.MaxValue,FadeOutBeats=Definition.TransitionFadeBeats,FadeInBeats=Definition.TransitionFadeBeats};
 }
 public float Gain(AudioTrack track,double position)
 {
  var gains=track.MusicVariationGains;
  var grid=resource.BeatGridAt(position);
  var lead=1f;
  if(track.MusicLead && grid is not null)
  {
   var bar=grid.SecondsPerBeat*grid.BeatsPerBar;
   lead=(float)Math.Clamp((position-grid.Start.Seconds)/bar,0,1);
   if(Transition is not null)lead*= (float)Math.Clamp((grid.End.Seconds-position)/bar,0,1);
  }
  if(gains is null || gains.Count==0)return lead;
  var at=Variation*(gains.Count-1);var lo=(int)at;var hi=Math.Min(lo+1,gains.Count-1);
  return lead*(gains[lo]+(gains[hi]-gains[lo])*(at-lo));
 }
 public void PauseClock() {lastTime=0;}
}

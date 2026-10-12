namespace Smod;
public static partial class AudioEventValidator
{
 static void ValidateMusic(AudioEvent e,string path,IReadOnlyDictionary<string,AudioParameter> parameters,AudioEventValidationResult result)
 {
  var m=e.MusicPlaylist;if(m is null)return;
  path+="/MusicPlaylist";
  if(m.ContextParameter is null || !parameters.Values.Any(x=>ReferenceEquals(x,m.ContextParameter)))result.Error(path,"Context must reference a declared world-state parameter.");
  if(m.VariationBars<1 || m.VariationCount<1 || m.VariationCount>32)result.Error(path,"Variation bars and count must be positive; at most 32 balances are supported.");
  if(!float.IsFinite(m.VariationBlendBeats) || m.VariationBlendBeats<=0 || !float.IsFinite(m.TransitionFadeBeats) || m.TransitionFadeBeats<0)result.Error(path,"Blend beats must be positive and transition fade beats non-negative.");
  if(m.Sections is null || m.Sections.Count==0) {result.Error(path,"At least one music section is required.");return;}
  var ids=new HashSet<Guid>();
  foreach(var section in m.Sections)
  {
   if(section is null) {result.Error(path,"Music section is required.");continue;}
   var marker=e.DestinationMarkers?.FirstOrDefault(x=>x?.Id==section.DestinationMarkerId);
   if(marker is null || !ids.Add(section.DestinationMarkerId))result.Error(path,"Music sections need unique existing destination markers.");
   else if(!(e.BeatGrids ?? new()).Any(x=>x is not null && Math.Abs(x.Start.Seconds-marker.Position.Seconds)<.0001))result.Error(path,"Music destinations must start a beat grid.");
   ValidateTimelineConditions(section.Conditions,path+"/Conditions",parameters,result);
   ValidateTimelineConditions(section.EntryConditions,path+"/EntryConditions",parameters,result);
  }
  foreach(var track in e.Tracks ?? new())
  {
   if(track?.MusicVariationGains is not {Count:>0} gains)continue;
   if(gains.Count!=m.VariationCount || gains.Any(x=>!float.IsFinite(x) || x<0 || x>4))result.Error(path,"Track music gains must match the variation count and be finite values between 0 and 4.");
  }
 }
}

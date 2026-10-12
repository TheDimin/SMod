using System.Runtime.CompilerServices;
namespace Smod;

/// <summary>Prepared source bindings owned outside authored resources. Local source
/// reference edits require validation and a new instance before playback.</summary>
internal static class AudioSourceRuntime
{
 sealed class Binding
 {
  internal AudioPreparedSources Sources;
  internal readonly Dictionary<AudioInstrument,int> Slots=new();
 }
 static readonly ConditionalWeakTable<AudioEvent,Binding> Bindings=new();
 internal static void Prepare(AudioEvent graph,IReadOnlyDictionary<string,AudioParameter> parameters)
 {
  Bindings.Remove(graph);
  var binding=new Binding{Sources=new(graph.Sources,parameters)};
  void Check(Guid id)
  {
   if(binding.Sources.ContainsId(id))throw new InvalidOperationException("A source ID collides with another graph element ID.");
  }
  void CheckAutomations(List<AudioAutomation> automations)
  {
   foreach(var automation in automations ?? new())
   {
    if(automation is null)continue;Check(automation.Id);
    foreach(var point in automation.Points ?? new())if(point is not null)Check(point.Id);
   }
  }
  foreach(var marker in graph.DestinationMarkers ?? new())if(marker is not null)Check(marker.Id);
  foreach(var marker in graph.TransitionMarkers ?? new())if(marker is not null)Check(marker.Id);
  foreach(var region in graph.TransitionRegions ?? new())if(region is not null)Check(region.Id);
  foreach(var loop in graph.LoopRegions ?? new())if(loop is not null)Check(loop.Id);

  var tracks=graph.Tracks is null ? Enumerable.Empty<AudioTrack>() : graph.Tracks;
  foreach(var track in tracks.Prepend(graph.MasterTrack))
  {
   if(track is not null){Check(track.Id);CheckAutomations(track.Automations);}
   foreach(var clip in track?.Instruments ?? new())
   {
    if(clip is null)continue;
    Check(clip.Id);CheckAutomations(clip.Automations);
    if(clip.SourceId==Guid.Empty)continue;
    if(clip.IsSubEvent || clip.LayerControl is not null)
     throw new InvalidOperationException("Control and sub-graph nodes cannot reference source data.");
    binding.Slots.Add(clip,binding.Sources.ResolveIndex(clip.SourceId));
   }
  }
  Bindings.Add(graph,binding);
 }
 internal static AudioSourceDefinition Resolve(AudioEvent graph,AudioInstrument clip)
 {
  if(clip.SourceId==Guid.Empty)return null;
  if(!Bindings.TryGetValue(graph,out var binding) || !binding.Slots.TryGetValue(clip,out var slot))
   throw new InvalidOperationException("Local source references require graph validation before playback.");
  var source=binding.Sources[slot];
  if(source.Id!=clip.SourceId)throw new InvalidOperationException("Source references changed. Validate the graph and create a new playback instance.");
  return source;
 }
}

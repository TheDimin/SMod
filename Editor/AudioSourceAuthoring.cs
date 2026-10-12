using System;
using System.Linq;
using Smod;
namespace Smod.Editor;

/// <summary>Graph-owned source edits. The caller supplies the graph undo transaction.</summary>
internal static class AudioSourceAuthoring
{
 internal static AudioSourceDefinition Resolve(AudioEvent graph,AudioInstrument clip)
 {
  if(graph is null || clip is null || clip.SourceId==Guid.Empty)return null;
  return graph.Sources?.SingleOrDefault(source=>source?.Id==clip.SourceId);
 }
 internal static AudioSourceDefinition Add(AudioEvent graph,AudioInstrument clip,AudioSourceDefinition source)
 {
  ArgumentNullException.ThrowIfNull(graph);ArgumentNullException.ThrowIfNull(clip);ArgumentNullException.ThrowIfNull(source);
  if(!graph.Tracks.Prepend(graph.MasterTrack).Any(t=>t?.Instruments?.Contains(clip)==true))
   throw new InvalidOperationException("The clip must belong to this graph.");
  if(clip.IsSubEvent || clip.LayerControl is not null)
   throw new InvalidOperationException("Only sound clips can use a source.");
  graph.Sources??=new();
  if(source.Id==Guid.Empty || graph.Sources.Any(s=>s?.Id==source.Id))
   throw new InvalidOperationException("The source requires a new unique ID.");
  graph.Sources.Add(source);clip.SourceId=source.Id;
  return source;
 }
}

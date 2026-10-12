using System.Threading;
using System.Threading.Tasks;
namespace Smod;

/// <summary>Prepare referenced shared-music ranges at a loading barrier, without starting voices.</summary>
public static class AudioMusicPreload
{
 internal static AudioEvent[] SharedGraphs(AudioEvent root)
 {
  var visited=new HashSet<AudioEvent>();var result=new List<AudioEvent>();var pending=new Stack<AudioEvent>();pending.Push(root);
  while(pending.Count>0)
  {
   var graph=pending.Pop();if(graph is null || !visited.Add(graph))continue;
   if(graph.SharedMusicOutput)result.Add(graph);
   foreach(var track in graph.Tracks.Prepend(graph.MasterTrack))
   foreach(var instrument in track.Instruments)
   foreach(var choice in instrument.SubEvents)
   {if(choice.Event is not null)pending.Push(choice.Event);if(choice.ExitEvent is not null)pending.Push(choice.ExitEvent);}
  }
  return result.ToArray();
 }
 /// <summary>Call on the owning thread during scene loading. Cancellation is checked between native decodes; the engine cannot interrupt a decode. No output, source files or song positions are changed.</summary>
 public static async Task<(int Graphs,int Ranges,long NativeDecodes,long RetainedBytes)> PrepareAsync(AudioEvent root,CancellationToken cancellation=default)
 {
  cancellation.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(root);
  AudioEventValidator.Validate(root).AssertValid();
  var graphs=SharedGraphs(root);var before=AudioMusicPreparationCache.NativeDecodes;var ranges=0;
  foreach(var graph in graphs)
  {
   cancellation.ThrowIfCancellationRequested();
   using var bank=new AudioMusicSourceBank(AudioMusicEventPlayback.DefaultPreparationBudgetBytes);
   foreach(var track in graph.Tracks.Prepend(graph.MasterTrack))
   foreach(var instrument in track.Instruments)
   {
    if(instrument.IsSubEvent || instrument.LayerControl is not null || track.Overlay?.Enabled==true)continue;
    foreach(var source in graph.Source(instrument)?.Tracks ?? new())
    {
     if(source.Sound is null)continue;
     cancellation.ThrowIfCancellationRequested();
     // Yield between full native calls. This does not make the decoder asynchronous.
     await GameTask.DelayRealtime(1);
     if(await bank.PrepareAsync(instrument,source,graph.MasterTrack.Tone,ReferenceEquals(track,graph.MasterTrack)?null:track.Tone) is null)
      throw new InvalidOperationException("Music preload exceeds its preparation budget. "+bank.LastBudgetRejection);
     ranges++;
    }
   }
   cancellation.ThrowIfCancellationRequested();
   var actions=AudioMusicPrewarmPlan.Build(graph);
   if(!await bank.PrewarmAsync(actions,graph.MasterTrack.Tone))throw new InvalidOperationException("Music action preload exceeds its preparation budget.");
   ranges+=actions.Length;
  }
  cancellation.ThrowIfCancellationRequested();
  return (graphs.Length,ranges,AudioMusicPreparationCache.NativeDecodes-before,AudioMusicPreparationCache.ResidentBytes);
 }
}

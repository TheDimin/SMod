using System;
using System.Linq;
using System.Threading.Tasks;
namespace Editor.Mcp;

[McpToolset("audio_build", "Measure audio in the current project publish manifest without uploading.")]
public static class AudioBuildMcpTools
{
 public sealed record Report(int AudioFiles,long AudioBytes,long BudgetBytes,bool WithinBudget,int TotalFiles,long AssetBytes);
 /// <summary>Build a fresh publisher asset manifest and sum sound resource sizes. May compile stale assets through the native publisher pipeline. Does not upload files or publish a revision. AssetBytes excludes assemblies added by the publish wizard.</summary>
 [McpTool("inspect_audio_build_size")]
 public static async Task<Report> Inspect(long budgetBytes=300000000)
 {
  if(budgetBytes<1)throw new ArgumentOutOfRangeException(nameof(budgetBytes));
  var publisher=await ProjectPublisher.FromProject(Project.Current);
  var files=publisher.Manifest.Assets;
  var audio=files.Where(x=>x.Name.EndsWith(".vsnd_c",StringComparison.OrdinalIgnoreCase)||x.Name.EndsWith(".vsnd",StringComparison.OrdinalIgnoreCase)).ToArray();
  var bytes=audio.Sum(x=>(long)x.Size);
  return new(audio.Length,bytes,budgetBytes,bytes<budgetBytes,files.Count,files.Sum(x=>(long)x.Size));
 }
}

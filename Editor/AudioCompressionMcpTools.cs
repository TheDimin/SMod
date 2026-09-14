using System;
namespace Editor.Mcp;

[McpToolset("audio_compression", "Set native sound import compression without changing audio source files.")]
public static class AudioCompressionMcpTools
{
 /// <summary>Set compression on one project sound asset and request native compilation. Changes only compress and bitrate metadata. Preserves source audio, sample rate, channels, loop, gain, and trim settings. Read the compiled asset size after compilation completes.</summary>
 [McpTool("set_sound_compression")]
 public static object Set(string resourcePath,int bitrate=128,bool enabled=true)
 {
  if(string.IsNullOrWhiteSpace(resourcePath))throw new ArgumentException("Sound path is required.");
  if(bitrate<128 || bitrate>256)throw new ArgumentOutOfRangeException(nameof(bitrate));
  var asset=AssetSystem.FindByPath(resourcePath);
  if(asset is null || asset.AssetType!=AssetType.SoundFile || asset.MetaData is null)throw new InvalidOperationException("A registered sound asset is required.");
  var root=System.IO.Path.GetFullPath(Project.Current.GetAssetsPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar;
  if(!System.IO.Path.GetFullPath(asset.AbsolutePath).StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Only project sound assets can be changed.");
  var previousEnabled=asset.MetaData.Get("compress",false);var previousBitrate=asset.MetaData.Get("bitrate",256);
  asset.MetaData.Set("compress",enabled);asset.MetaData.Set("bitrate",bitrate);
  var compiled=asset.Compile(false);
  return new {Path=asset.RelativePath,PreviousEnabled=previousEnabled,PreviousBitrate=previousBitrate,Enabled=enabled,Bitrate=bitrate,CompileRequested=compiled};
 }
}

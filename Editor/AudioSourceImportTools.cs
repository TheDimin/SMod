using System;
using System.Threading.Tasks;

namespace Editor.Mcp;

public static class AudioSourceImportTools
{
 /// <summary>Import one WAV or OGG from the configured SMOD_AUDIO_SOURCE_ROOT through the editor library. Preserves source bytes, reuses identical imports, and enables native compression. Returns the registered sound path after compilation.</summary>
 [McpTool("import_audio_source")]
 public static async Task<object> Import(string sourcePath, int bitrate = 128)
 {
  if(bitrate is <128 or >256)throw new ArgumentOutOfRangeException(nameof(bitrate));
  var asset=await ExternalAudioSourceCatalog.ImportAsync(sourcePath);
  asset.MetaData.Set("compress",true);
  asset.MetaData.Set("bitrate",bitrate);
  await asset.CompileIfNeededAsync();
  if(!asset.IsCompiledAndUpToDate || asset.IsCompileFailed)throw new InvalidOperationException("Sound compile failed: "+asset.RelativePath);
  var sound=Sandbox.SoundFile.Load(asset.Path);
  if(sound is null || !await sound.LoadAsync())throw new InvalidOperationException("Sound decode failed: "+asset.RelativePath);
  return new{Path=asset.RelativePath,Sound=sound.ResourcePath,Duration=sound.Duration,Channels=sound.Channels,Compressed=true,Bitrate=bitrate};
 }
}

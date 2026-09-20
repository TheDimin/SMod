using Smod;
namespace Editor;

internal sealed class AudioMusicCacheInvalidation : AssetSystem.IEventListener
{
 static AudioMusicCacheInvalidation listener;
 [EditorEvent.Frame]
 static void EnsureListener()
 {
  if(listener is not null)return;
  listener=new();EditorEvent.Register(listener);
  AudioMusicPreparationCache.Invalidate();
  AudioMusicPreparationCache.EditorInvalidationReady=true;
 }
 void AssetSystem.IEventListener.OnAssetChanged(Asset asset)
 {
  if(asset.AssetType==AssetType.SoundFile)AudioMusicPreparationCache.Invalidate();
 }
}

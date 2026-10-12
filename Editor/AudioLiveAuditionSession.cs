using System;
using System.Linq;
using System.Collections.Generic;
namespace Smod.Editor;

/// <summary>Editor-owned temporary controls. No runtime polling or resource writes.</summary>
internal sealed class AudioLiveAuditionSession : IDisposable
{
 readonly AudioEventInstance instance;
 readonly Dictionary<AudioTrack,bool> original;
 bool disposed;
 internal AudioLiveAuditionSession(AudioEventInstance instance)
 {
  this.instance=instance??throw new ArgumentNullException(nameof(instance));
  original=instance.AuditionResource.Tracks.Prepend(instance.AuditionResource.MasterTrack)
   .ToDictionary(t=>t,t=>instance.IsTrackMutedForAudition(t));
 }
 internal void Apply(Func<Guid,bool> muted)
 {
  if(disposed || !instance.IsValid)return;
  foreach(var pair in original)instance.SetTrackMutedForAudition(pair.Key,pair.Value||muted(pair.Key.Id));
 }
 public void Dispose()
 {
  if(disposed)return;disposed=true;
  if(!instance.IsValid)return;
  foreach(var pair in original)
   if(ReferenceEquals(instance.AuditionResource.MasterTrack,pair.Key)||instance.AuditionResource.Tracks.Contains(pair.Key))
    instance.SetTrackMutedForAudition(pair.Key,pair.Value);
 }
}

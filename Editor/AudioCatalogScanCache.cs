using System;
using System.Threading.Tasks;

namespace Editor;

/// <summary>Shares one pending scan and its completed result. Explicit refresh retries failed scans.</summary>
internal sealed class AudioCatalogScanCache<T>
{
 private Task<T> current;
 public Task<T> Get(Func<Task<T>> scan, bool refresh = false)
 {
  // Called on the editor thread. A second window or refresh joins pending work.
  if(current is not null && (!current.IsCompleted || (!refresh && current.IsCompletedSuccessfully)))return current;
  return current=scan();
 }
}

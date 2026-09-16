namespace Smod;

/// <summary>One graph-local source lookup prepared before playback. Source IDs never
/// fall back to a parent graph or a legacy resource when they are invalid.</summary>
internal sealed class AudioPreparedSources
{
 readonly AudioSourceDefinition[] sources;
 readonly Dictionary<Guid,int> indexes = new();
 internal int Count => sources.Length;
 internal bool ContainsId(Guid id) => indexes.ContainsKey(id);
 internal AudioPreparedSources(IReadOnlyList<AudioSourceDefinition> definitions,
  IReadOnlyDictionary<string,AudioParameter> parameters)
 {
  ArgumentNullException.ThrowIfNull(definitions);
  ArgumentNullException.ThrowIfNull(parameters);
  sources=new AudioSourceDefinition[definitions.Count];
  var validation=new AudioEventValidationResult();
  for(var i=0;i<definitions.Count;i++)
  {
   var source=definitions[i];var path=$"Sources[{i}]";
   if(source is null){validation.Error(path,"Source cannot be null.");continue;}
   if(source.Id==Guid.Empty)validation.Error(path,"Source ID cannot be empty.");
   else if(!indexes.TryAdd(source.Id,i))validation.Error(path,"Source ID must be unique in this graph.");
   AudioEventValidator.ValidateSoundEvent(source,path,parameters,validation);
   sources[i]=source;
  }
  validation.AssertValid();
 }
 internal int ResolveIndex(Guid sourceId)
 {
  if(sourceId==Guid.Empty)throw new InvalidOperationException("A local source ID is required.");
  if(!indexes.TryGetValue(sourceId,out var index))
   throw new InvalidOperationException($"Source {sourceId:N} is not defined in this graph.");
  return index;
 }
 internal AudioSourceDefinition this[int index] => sources[index];
}

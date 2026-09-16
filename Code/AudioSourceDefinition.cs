namespace Smod;

/// <summary>Plain graph-local source data. Source edits belong to the owning graph.</summary>
public sealed class AudioSourceDefinition
{
 [Property, Hide] public Guid Id { get; set; } = Guid.NewGuid();
 [Property] public RangedFloat Volume { get; set; } = new(1f);
 [Property] public RangedFloat Pitch { get; set; } = new(1f);
 [Property] public SoundTrackSelectionMode SelectionMode { get; set; }
 [Property, InlineEditor] public List<SoundEventTrack> Tracks { get; set; } = new();
}

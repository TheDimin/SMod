using Smod;
using System.Text.Json;
namespace Editor;

public sealed partial class AudioEventEditorWindow
{
 private ComboBox liveSelector;
 private Guid liveSelection;
 private string liveMembership;
 private AudioLiveSnapshot liveSnapshot;
 private bool liveFrozen;
 private bool liveSelectorUpdating;
 private float liveNextUpdate;
 internal bool LiveAttached => liveSelection!=Guid.Empty;
 private Smod.Editor.AudioLiveAuditionSession liveAudition;
 private void ReleaseLiveAudition()
 {
  liveAudition?.Dispose();liveAudition=null;
  _mutedTracks.Clear();_soloTracks.Clear();_workspace?.UpdateTrackAudition();
 }
 private void ApplyLiveAudition()
 {
  if(liveAudition is null)
  {
   var instance=AudioLiveDiagnostics.FindForAudition(liveSelection);
   if(instance is null)return;
   liveAudition=new(instance);
  }
  liveAudition.Apply(id=>_event.Tracks.Any(track=>track.Id==id && !IsTrackAudible(track)));
 }


 private void CreateLiveToolbar()
 {
  liveSelector=_toolbar.AddWidget(new ComboBox(this){MinimumWidth=240});
  liveSelector.AddItem("Preview / authoring",null,DetachLive,null,true);
  _toolbar.AddOption("Open parent event","arrow_upward",()=>
  {
   var current=AudioLiveDiagnostics.CaptureActive().FirstOrDefault(item=>item.Id==liveSelection);
   var parent=AudioLiveDiagnostics.CaptureActive().FirstOrDefault(item=>item.Id==current?.ParentInstanceId);
   if(parent is not null)OpenChildEvent(parent.Resource,parent.Id);
  });
  _toolbar.AddOption("Clear mute / solo","hearing",ClearTrackAudition);
  _toolbar.AddOption("Freeze live view","pause_circle",()=>liveFrozen=!liveFrozen);
  _toolbar.AddOption("Copy live report","content_copy",()=>{if(liveSnapshot is not null)EditorUtility.Clipboard.Copy(JsonSerializer.Serialize(liveSnapshot,new JsonSerializerOptions{WriteIndented=true}));});
 }
 private void AttachLive(Guid id)
 {
  if(liveSelectorUpdating)return;
  ReleaseLiveAudition();DisposePreview(false);liveSelection=id;liveFrozen=false;liveSnapshot=null;liveNextUpdate=0;
 }
 private void DetachLive()
 {
  if(liveSelectorUpdating)return;
  ReleaseLiveAudition();liveSelection=Guid.Empty;liveSnapshot=null;liveFrozen=false;
  _workspace?.UpdateLiveOverlay(null);_parameters?.SetLiveValues(null);
  if(_playOption is not null)_playOption.Enabled=true;
  UpdateTransport();
 }
 private bool UpdateLiveOverlay()
 {
  if(_event is null || liveSelector is null)return false;
  if(RealTime.Now>=liveNextUpdate)
  {
   liveNextUpdate=RealTime.Now+.1f;
   var active=AudioLiveDiagnostics.CaptureActive().Where(x=>x.Resource==_event.ResourcePath).ToArray();
   var membership=string.Join("|",active.Select(x=>x.Id));
   if(membership!=liveMembership)
   {
    liveMembership=membership;liveSelectorUpdating=true;liveSelector.Clear();
    liveSelector.AddItem("Preview / authoring",null,DetachLive,null,!LiveAttached);
    foreach(var item in active)
    {
     var id=item.Id;
     liveSelector.AddItem($"LIVE [{id.ToString()[..8]}] {item.State}{(item.ParentInstanceId!=Guid.Empty ? " | child" : "")}",null,()=>AttachLive(id),null,id==liveSelection);
    }
    if(LiveAttached && !active.Any(x=>x.Id==liveSelection))liveSelector.AddItem("LIVE instance ended",null,()=>{},null,true);
    liveSelectorUpdating=false;
   }
   if(LiveAttached && !liveFrozen)
   {
    liveSnapshot=active.FirstOrDefault(x=>x.Id==liveSelection);
    if(liveSnapshot is not null)
    {
     _workspace.TimelinePosition=liveSnapshot.Position;
     if(_followPreview)_workspace.FollowTimelinePosition();
     _workspace.UpdateLiveOverlay(liveSnapshot);
     _parameters.SetLiveValues(liveSnapshot);
    }
    else {ReleaseLiveAudition();_workspace.UpdateLiveOverlay(null);_parameters.SetLiveValues(null);}
   }
  }
  if(!LiveAttached)return false;
  _playOption.Enabled=true;_playOption.Text="Preview";_playOption.Icon="play_arrow";_stopOption.Enabled=false;
  _timecode.Text=liveSnapshot is null ? "LIVE — instance ended" : $"{(liveFrozen ? "FROZEN" : "LIVE")} {liveSnapshot.Position:0.00}s | {liveSnapshot.Section ?? liveSnapshot.State} | next {liveSnapshot.PendingSection ?? "none"}";
  _timecode.MinimumWidth=300;
  return true;
 }

 internal static async System.Threading.Tasks.Task<object> CheckSubGraphEditorAsync(string resourcePath)
 {
  UseSingleWindow();
  var asset=AssetSystem.FindByPath(resourcePath) ?? throw new InvalidOperationException("Audio event asset is missing.");
  if(!IAssetEditor.OpenInEditor(asset,out var editor) || editor is not AudioEventEditorWindow parent)throw new InvalidOperationException("Audio Event editor did not open.");
  var instance=AudioSystem.GetEvent(parent._event).CreateInstance();instance.Volume=0;
  AudioEventEditorWindow childWindow=null;
  try
  {
   instance.SetParameterByName("WorldState",2);instance.Start();
   parent.AttachLive(instance.LiveId);parent.UpdateLiveOverlay();
   var parentBlocks=parent._workspace.SubGraphBlockCount;
   var parentAttached=parent.liveSnapshot?.Id==instance.LiveId;
   var child=instance.InstrumentPlaybackStates.Single();
   OpenChildEvent(child.ChildEvent,child.ChildInstanceId);
   IAssetEditor.OpenInEditor(AssetSystem.FindByPath(child.ChildEvent),out var nestedEditor);
   childWindow=nestedEditor as AudioEventEditorWindow;
   await System.Threading.Tasks.Task.Delay(150);
   childWindow.liveNextUpdate=0;childWindow.UpdateLiveOverlay();
   var target=AudioLiveDiagnostics.FindForAudition(child.ChildInstanceId)??throw new InvalidOperationException("Live child is missing.");
   var tracks=childWindow._event.Tracks.Where(t=>t is not null).ToArray();
   if(tracks.Length<2)throw new InvalidOperationException("The control check needs at least two child tracks.");
   bool Muted(AudioTrack track)=>target.IsTrackMutedForAudition(target.AuditionResource.Tracks.Single(t=>t.Id==track.Id));
   childWindow.ToggleTrackMute(tracks[0]);var muteApplied=Muted(tracks[0])&&!Muted(tracks[1]);
   childWindow.ClearTrackAudition();var clearRestored=tracks.All(t=>!Muted(t));
   childWindow.ToggleTrackSolo(tracks[0]);var soloApplied=!Muted(tracks[0])&&tracks.Skip(1).All(Muted);
   childWindow.DetachLive();var detachRestored=tracks.All(t=>!Muted(t));
   childWindow.AttachLive(child.ChildInstanceId);childWindow.UpdateLiveOverlay();
   return new {SingleWindow=ReferenceEquals(parent,childWindow),ParentBlocks=parentBlocks,ParentAttached=parentAttached,
    ChildAttached=childWindow.liveSnapshot?.Id==child.ChildInstanceId,ChildResource=childWindow._event.ResourcePath,
    ChildTracks=childWindow._event.Tracks.Count,InheritedWorldState=childWindow.liveSnapshot?.Parameters.FirstOrDefault(value=>value.Name=="WorldState")?.Value,
    ParentLink=childWindow.liveSnapshot?.ParentInstanceId==instance.LiveId,MuteApplied=muteApplied,SoloApplied=soloApplied,ClearRestored=clearRestored,DetachRestored=detachRestored};
  }
  finally
  {
   instance.Stop(AudioStopMode.Immediate);instance.Release();
   childWindow?.DetachLive();parent.DetachLive();parent.AssetOpen(asset);parent.Focus();
  }
 }
 public static void OpenLiveEvent(string resourcePath) => OpenChildEvent(resourcePath, AudioLiveDiagnostics.CaptureActive().FirstOrDefault(x=>x.Resource==resourcePath)?.Id ?? Guid.Empty);
 public static void OpenChildEvent(string resourcePath, Guid instanceId = default)
 {
  UseSingleWindow();
  var asset=AssetSystem.FindByPath(resourcePath) ?? throw new InvalidOperationException("Audio event asset was not found: "+resourcePath);
  if(!IAssetEditor.OpenInEditor(asset,out var editor) || editor is not AudioEventEditorWindow window)return;
  if(instanceId!=Guid.Empty)window.AttachLive(instanceId);
 }
}

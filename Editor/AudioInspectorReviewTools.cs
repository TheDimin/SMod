using System;
using System.Text.Json.Nodes;
using Smod;
namespace Editor.Mcp;

[McpToolset("audio", "Inspect and validate authored AudioEvent resources.")]
public static class AudioInspectorReviewTools
{
 /// <summary>Check native inspector null handling using temporary audio objects and controls. Verifies that absent playlist, tone, overlay, and layer controls stay absent, and existing values survive inspection. Writes no assets, opens no window, and does not change playback or user drafts.</summary>
 [McpTool("check_audio_inspector_nulls")]
 public static object CheckOptionalFields()
 {
  var audio=new AudioEvent();var track=new AudioTrack();
  var instrument=new AudioInstrument(); audio.MasterTrack.Instruments.Add(instrument);
  var before=audio.Serialize();
  Inspect(audio,nameof(AudioEvent.MusicPlaylist));
  Inspect(track,nameof(AudioTrack.Tone));
  Inspect(track,nameof(AudioTrack.Overlay));
  Inspect(instrument,nameof(AudioInstrument.LayerControl));
  Inspect(instrument,nameof(AudioInstrument.Echo));
  var playlistNull=audio.MusicPlaylist is null;var toneNull=track.Tone is null;var overlayNull=track.Overlay is null;
  var layerNull=instrument.LayerControl is null;
  var echoNull=instrument.Echo is null;
  var absent=playlistNull&&toneNull&&overlayNull&&layerNull&&echoNull;
  var unchanged=JsonNode.DeepEquals(before,audio.Serialize());
  var playlist=new AudioMusicPlaylist{VariationBars=12};
  var tone=new AudioTone{BassGainDb=-3};
  var overlay=new AudioOverlayRule{Volume=.2f};
  var layer=new AudioMusicLayerControl{ActiveGain=1.7f};
  instrument.LayerControl=layer;
  var echo=new AudioEchoEffect{WetGain=.17f};instrument.Echo=echo;
  audio.MusicPlaylist=playlist;track.Tone=tone;track.Overlay=overlay;
  Inspect(audio,nameof(AudioEvent.MusicPlaylist));
  Inspect(track,nameof(AudioTrack.Tone));
  Inspect(track,nameof(AudioTrack.Overlay));
  Inspect(instrument,nameof(AudioInstrument.LayerControl));
  Inspect(instrument,nameof(AudioInstrument.Echo));
  var retained=ReferenceEquals(audio.MusicPlaylist,playlist) && playlist.VariationBars==12
   && ReferenceEquals(track.Tone,tone) && tone.BassGainDb==-3
   && ReferenceEquals(track.Overlay,overlay) && overlay.Volume==.2f
   && ReferenceEquals(instrument.LayerControl,layer) && layer.ActiveGain==1.7f
   && ReferenceEquals(instrument.Echo,echo) && echo.WetGain==.17f;
  // The unprotected property proves this check exercises native auto-creation.
  var probe=new AudioInspectorNullProbe();Inspect(probe,nameof(AudioInspectorNullProbe.Playlist),true);
  var reproduces=probe.Playlist is not null;
  return new {EchoNull=echoNull,LayerControlNull=layerNull,PlaylistNull=playlistNull,ToneNull=toneNull,OverlayNull=overlayNull,AbsentFeaturesStayNull=absent,EventUnchanged=unchanged,ExistingValuesRetained=retained,
   UnprotectedControlCreatesObject=reproduces,Passed=absent&&unchanged&&retained&&reproduces};
 }
 static void Inspect(object target,string name,bool useUnprotectedControl=false)
 {
  var property=target.GetSerialized().GetProperty(name) ?? throw new InvalidOperationException("Missing inspector property: "+name);
  var widget=useUnprotectedControl ? new ControlObjectWidget(property,true) : ControlWidget.Create(property);
  try { if(widget is null)throw new InvalidOperationException("Inspector control was not created."); }
  finally { widget.Destroy(); }
 }
}
public sealed class AudioInspectorNullProbe
{
 [Property, InlineEditor] public AudioMusicPlaylist Playlist {get;set;}
}

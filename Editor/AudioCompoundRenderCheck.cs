using System;
using System.Linq;
using Smod;
using Smod.Editor;
namespace Editor;
public static class AudioCompoundRenderCheck
{
 public static string Capture(AudioEvent resource,string outputPath)
 {
  var track=resource.Tracks.Single(t=>t.Name=="Service | Mellow handling drum sets");
  var bpm=resource.BeatGrids[0].BeatsPerMinute;
  var pixmap=new Pixmap(800,240);pixmap.Clear(Theme.WindowBackground);
  using(Paint.ToPixmap(pixmap))
  {
   for(var index=0;index<track.Instruments.Count;index++)
   {
    var clip=track.Instruments[index];
    var duration=AudioTimelineView.EstimateDuration(clip,bpm);
    var preview=new AudioInstrumentWaveformPreview(AudioSourceAuthoring.Resolve(resource,clip),()=>{});
    var rect=new Sandbox.Rect(0,0,MathF.Max(28,duration*500),64);
    Paint.Translate(new Vector2(12,index==0?8:76));
    AudioInstrumentBlockRenderer.DrawStatic(rect,rect,clip,preview,default,true,bpm);
    AudioInstrumentBlockRenderer.DrawOverlay(rect,clip,preview,default,true,false,false,bpm);
    Paint.Translate(new Vector2(-12,0));
   }
  }
  pixmap.SavePng(outputPath);return outputPath;
 }
}

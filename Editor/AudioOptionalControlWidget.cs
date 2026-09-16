using System;
using Smod;
namespace Editor;

// GenericControlWidget creates null reference values in its constructor. Optional
// audio features must stay absent until the author explicitly adds them.
internal abstract class AudioOptionalControlWidget : ControlWidget
{
 readonly Func<object> create;
 public override bool IsWideMode => true;
 public AudioOptionalControlWidget(SerializedProperty property,Func<object> create) : base(property)
 {
  this.create=create;Layout=Layout.Column();Build();
 }
 void Build()
 {
  Layout.Clear(true);
  if(SerializedProperty.GetValue<object>() is not null)
  {
   Layout.Add(new GenericControlWidget(SerializedProperty));
   return;
  }
  var add=Layout.Add(new Button("Add", "add"));
  add.Enabled=SerializedProperty.IsEditable;
  add.Clicked+=()=>{SerializedProperty.SetValue(create());Build();};
 }
}
[CustomEditor(typeof(AudioMusicPlaylist))]
internal sealed class AudioOptionalPlaylistControl : AudioOptionalControlWidget
{
 public AudioOptionalPlaylistControl(SerializedProperty property):base(property,()=>new AudioMusicPlaylist()){}
}
[CustomEditor(typeof(AudioTone))]
internal sealed class AudioOptionalToneControl : AudioOptionalControlWidget
{
 public AudioOptionalToneControl(SerializedProperty property):base(property,()=>new AudioTone()){}
}
[CustomEditor(typeof(AudioOverlayRule))]
internal sealed class AudioOptionalOverlayControl : AudioOptionalControlWidget
{
 public AudioOptionalOverlayControl(SerializedProperty property):base(property,()=>new AudioOverlayRule()){}
}


[CustomEditor(typeof(AudioMusicLayerControl))]
internal sealed class AudioOptionalLayerControl : AudioOptionalControlWidget
{
 public AudioOptionalLayerControl(SerializedProperty property):base(property,()=>new AudioMusicLayerControl()){}
}

[CustomEditor(typeof(AudioEchoEffect))]
internal sealed class AudioOptionalEchoControl : AudioOptionalControlWidget
{
 public AudioOptionalEchoControl(SerializedProperty property):base(property,()=>new AudioEchoEffect()){}
}

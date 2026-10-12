using System;
namespace Editor;
internal readonly record struct AudioVisibleTileRange(int First, int End)
{
 public static AudioVisibleTileRange ForViewport(int width, float left, float right, int tileWidth)
 {
  if(width<=0 || right<=0 || left>=width || right<=left)return new(0,0);
  var first=Math.Max(0,(int)MathF.Floor(Math.Max(0,left)/tileWidth));
  var end=Math.Min((width+tileWidth-1)/tileWidth,(int)MathF.Ceiling(Math.Min(width,right)/tileWidth));
  return new(first,end);
 }
}

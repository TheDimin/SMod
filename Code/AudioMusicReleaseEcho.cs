namespace Smod;

/// <summary>Render-owned release echo. Closed input never reads the following note.</summary>
internal sealed class AudioMusicReleaseEcho
{
 readonly float[] left,right;
 readonly float wet,feedback;
 readonly int tailFrames;
 int cursor,remaining;
 bool wasOpen;
 float lastLeft,lastRight;
 internal AudioMusicReleaseEcho(AudioEchoEffect settings)
 {
  if(!settings.IsValid)throw new ArgumentException("Invalid echo settings.");
  left=new float[Math.Max(1,(int)Math.Round(settings.DelaySeconds*44100))];right=new float[left.Length];
  wet=settings.WetGain;feedback=settings.Feedback;tailFrames=(int)Math.Round(settings.TailSeconds*44100);
 }
 internal void Reset(){Array.Clear(left);Array.Clear(right);cursor=remaining=0;wasOpen=false;lastLeft=lastRight=0;}
 internal void Process(ref float l,ref float r,bool open,bool allowed)
 {
  if(!allowed){if(wasOpen||remaining>0)Reset();return;}
  if(open)
  {
   if(!wasOpen){Reset();wasOpen=true;}
   left[cursor]=l;right[cursor]=r;lastLeft=l;lastRight=r;
  }
  else
  {
   if(wasOpen){remaining=tailFrames;wasOpen=false;}
   l=r=0;
   if(remaining>0)
   {
    var elapsed=tailFrames-remaining;
    var head=Math.Clamp(elapsed/220f,0,1);var tail=Math.Clamp(remaining/6615f,0,1);
    l=left[cursor]*wet*head*tail+lastLeft*(1-head);
    r=right[cursor]*wet*head*tail+lastRight*(1-head);
    left[cursor]*=feedback;right[cursor]*=feedback;remaining--;
   }
  }
  if(++cursor==left.Length)cursor=0;
 }
}

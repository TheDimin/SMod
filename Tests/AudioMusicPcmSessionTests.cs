using Smod;
public sealed partial class AudioEventTests
{
 [TestMethod]
 public void MusicSession_ShortActionMarginUsesFinerGridWithoutChangingDefault()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(8),BeatsPerMinute=100});
  try
  {
   var shortAction=session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},.125,1,schedulingLeadSamples:1058);
   Assert.AreEqual(3308L,shortAction.Sample,"An eighth of a beat at 100 BPM is 75 ms, rounded from the grid origin.");
   Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},.125,1,schedulingLeadSamples:1));
   var ordinary=session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},.25,1);
   Assert.AreEqual(6615L,ordinary.Sample);
  }
  finally{session.CloseRequests();}
 }
 [TestMethod]
 public void MusicSession_ExactChainJoinCannotStartEarlyOrMoveLate()
 {
  var session=new AudioMusicPcmSession(new AudioBeatGrid{End=new(8),BeatsPerMinute=100});
  try
  {
   var joined=session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},1,1,latestSample:52920,earliestSample:52920);
   Assert.AreEqual(52920L,joined.Sample);
   Assert.IsNull(session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},1,1,latestSample:52921,earliestSample:52921),"An invalid exact join cannot move to the next beat.");
   for(var i=0;i<110;i++){session.Processor.MixChannel(0,new float[512]);session.Processor.MixChannel(1,new float[512]);}
   Assert.IsNull(session.TrySchedule(new AudioMusicRequestOwner(),new short[]{1,1},1,1,latestSample:52920,earliestSample:52920),"Late preparation must not start a delayed continuation.");
  }
  finally{session.CloseRequests();}
 }
	[TestMethod] public void MusicSession_TwoProcessesChooseSameBeatAndRetainIndependentCancellation()
	{
		var grid=new AudioBeatGrid{End=new(8),BeatsPerMinute=100};var session=new AudioMusicPcmSession(grid);
		var coffee=AudioSystem.GetEvent(CreateEvent()).CreateInstance();var oven=AudioSystem.GetEvent(CreateEvent()).CreateInstance();coffee.Start();oven.Start();
		var a=session.TrySchedule(coffee,new short[]{8192,8192},1,1);var b=session.TrySchedule(oven,new short[]{16384,-16384},1,1);
		Assert.AreEqual(26460L,a.Sample);Assert.AreEqual(a.Sample,b.Sample);coffee.Stop(AudioStopMode.Immediate);
		float leftPeak=0,rightPeak=0;
		for(var block=0;block<53;block++)
		{
			var left=new float[512];var right=new float[512];session.Processor.MixChannel(0,left);session.Processor.MixChannel(1,right);
			leftPeak=Math.Max(leftPeak,left.Max());rightPeak=Math.Min(rightPeak,right.Min());
		}
		Assert.IsFalse(a.Admission.Result);Assert.IsTrue(b.Admission.Result);Assert.AreEqual(.5f,leftPeak);Assert.AreEqual(-.5f,rightPeak);
		session.CloseRequests();oven.Stop(AudioStopMode.Immediate);
	}
	[TestMethod] public void MusicSession_CopiesGridAndClosesPendingAdmissions()
	{
		var grid=new AudioBeatGrid{End=new(8),BeatsPerMinute=100};var session=new AudioMusicPcmSession(grid);grid.BeatsPerMinute=200;
		var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();var owner=process.MusicRequestOwner;
		var request=session.TrySchedule(process,new short[]{1,1},1,1);Assert.AreEqual(26460L,request.Sample);
		session.CloseRequests();Assert.IsFalse(request.Admission.Result);Assert.IsTrue(owner.IsCancelled);
		Assert.IsNull(session.TrySchedule(process,new short[]{1,1},1,1));process.Stop(AudioStopMode.Immediate);
	}
    [TestMethod] public void MusicSession_ProcessLifetimeCannotJoinTwoSessions()
    {
        var grid=new AudioBeatGrid{End=new(8),BeatsPerMinute=100};var first=new AudioMusicPcmSession(grid);var second=new AudioMusicPcmSession(grid);
        var process=AudioSystem.GetEvent(CreateEvent()).CreateInstance();process.Start();var owner=process.MusicRequestOwner;
        Assert.IsNotNull(first.TrySchedule(process,new short[]{1,1},1,1));Assert.IsNull(second.TrySchedule(process,new short[]{1,1},1,1));
        second.CloseRequests();Assert.IsFalse(owner.IsCancelled);first.CloseRequests();Assert.IsTrue(owner.IsCancelled);process.Stop(AudioStopMode.Immediate);
    }    [TestMethod] public void MusicSession_QueueRejectionDoesNotBindProcessToSession()
    {
        var grid=new AudioBeatGrid{End=new(8),BeatsPerMinute=100};var full=new AudioMusicPcmSession(grid,pending:1);var other=new AudioMusicPcmSession(grid);
        var first=AudioSystem.GetEvent(CreateEvent()).CreateInstance();var rejected=AudioSystem.GetEvent(CreateEvent()).CreateInstance();first.Start();rejected.Start();
        Assert.IsNotNull(full.TrySchedule(first,new short[]{1,1},1,1));Assert.IsNull(full.TrySchedule(rejected,new short[]{1,1},1,1));
        Assert.IsNotNull(other.TrySchedule(rejected,new short[]{1,1},1,1));full.CloseRequests();Assert.IsFalse(rejected.MusicRequestOwner.IsCancelled);
        other.CloseRequests();first.Stop(AudioStopMode.Immediate);rejected.Stop(AudioStopMode.Immediate);
    }}



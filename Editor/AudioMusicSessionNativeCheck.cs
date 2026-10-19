namespace Smod;

/// <summary>Editor-only access to the actual session scheduler for native verification.</summary>
public sealed class AudioMusicSessionNativeCheck
{
	readonly AudioMusicPcmSession session;
	public AudioMusicSessionNativeCheck(short[] backing = null) => session = new(new AudioBeatGrid { End = new(12), BeatsPerMinute = 100 }, voices:2, pending:2, backingPcm:backing, backingGain:.1f);
	public void SetPaused(bool paused) => session.SetPaused(paused);
	public long PausedFrames => session.Processor.PublishedPausedFrames;
	public sealed record Request( long Sample, System.Threading.Tasks.Task<bool> Admission );
	public Sandbox.Audio.AudioProcessor Processor => session.Processor.NativeProcessor;
	public long Position => session.Position;
	public Request Schedule( AudioEventInstance process, short[] pcm )
	{
		var result = session.TrySchedule( process, pcm, 1, .1f );
		return result is null ? null : new Request( result.Sample, result.Admission );
	}
	public void Close() => session.CloseRequests();
}

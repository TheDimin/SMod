namespace Smod;

/// <summary>Editor-only bridge for native capture of the internal PCM implementation.</summary>
public sealed class AudioMusicPcmCaptureSession
{
	readonly AudioMusicPcmRenderer renderer = new( 2 );
	readonly AudioMusicRequestOwner owner = new();
	readonly AudioMusicPcmCommands commands = new( 2 );
	readonly AudioMusicPcmProcessor processor;
	public AudioMusicPcmCaptureSession() { processor = new( renderer, commands: commands ); }
	public Sandbox.Audio.AudioProcessor Processor => processor.NativeProcessor;
	public long Position => processor.PublishedPosition;
	public bool UnsupportedListener => processor.UnsupportedListener;
	public bool InvalidChannelSequence => processor.InvalidChannelSequence;
	/// <summary>Prepare before attaching the processor. Do not call from the main thread during mixing.</summary>
	public System.Threading.Tasks.Task<bool> Queue( short[] pcm, long sample, float gain ) => commands.TryQueue( owner, pcm, sample, gain )?.Admission ?? System.Threading.Tasks.Task.FromResult( false );
	public bool Schedule( short[] pcm, long sample, float gain ) => renderer.Schedule( owner, pcm, sample, gain );
}




using System.Collections.Concurrent;
using System.Threading.Tasks;
namespace Smod;

/// <summary>One session-thread producer, one audio-thread consumer. PCM arrays must remain immutable.</summary>
internal sealed class AudioMusicPcmCommands
{
	internal sealed class Request
	{
		internal AudioMusicRequestOwner Owner;
		internal short[] Pcm;
		internal long Start;
		internal float Gain;
		internal long Generation;
		internal readonly TaskCompletionSource<bool> Completion = new( TaskCreationOptions.RunContinuationsAsynchronously );
		/// <summary>True means admitted by the renderer, not finished playing.</summary>
		public Task<bool> Admission => Completion.Task;
	}
	readonly ConcurrentQueue<Request> requests = new();
	readonly int capacity;
	int closedState;
	bool closed => System.Threading.Interlocked.CompareExchange(ref closedState,0,0)!=0;
	int fadeStop;
	sealed class SeekRequest
	{
		public long Sample,Generation;
		public readonly TaskCompletionSource<bool> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
	SeekRequest pendingSeek,appliedSeek;
	long producerGeneration,renderGeneration;
	/// <summary>Coalesce seeks. Completion means the complete stereo block has published its new position.</summary>
	public Task<bool> Seek(long sample)
	{
		if(sample<0 || sample>long.MaxValue-8192)throw new ArgumentOutOfRangeException(nameof(sample));
		if(closed)return Task.FromResult(false);
		var request=new SeekRequest{Sample=sample,Generation=++producerGeneration};
		System.Threading.Interlocked.Exchange(ref pendingSeek,request)?.Completion.TrySetResult(false);
		return request.Completion.Task;
	}
	public void CompleteStereoBlock()
	{
		var completed=System.Threading.Interlocked.Exchange(ref appliedSeek,null);completed?.Completion.TrySetResult(!closed);
	}
	int requestedPause;
	/// <summary>Coalesced session-thread control; applied once at the next complete stereo block.</summary>
	public void SetPaused(bool paused) => System.Threading.Interlocked.Exchange(ref requestedPause, paused ? 1 : 0);
	public AudioMusicPcmCommands( int capacity = 32 )
	{
		if ( capacity < 1 || capacity > 256 ) throw new ArgumentOutOfRangeException( nameof( capacity ) );
		this.capacity = capacity;
	}
	/// <summary>Call only from the owning session thread. Null means the bounded queue is full or closed.</summary>
	public Request TryQueue( AudioMusicRequestOwner owner, short[] pcm, long start, float gain )
	{
		ArgumentNullException.ThrowIfNull( owner ); ArgumentNullException.ThrowIfNull( pcm );
		if ( pcm.Length == 0 || pcm.Length % 2 != 0 ) throw new ArgumentException( "Expected stereo PCM.", nameof( pcm ) );
		if ( start < 0 || start > long.MaxValue - pcm.Length / 2 ) throw new ArgumentOutOfRangeException( nameof( start ) );
		if ( !float.IsFinite( gain ) || gain < 0 ) throw new ArgumentOutOfRangeException( nameof( gain ) );
		if ( closed || requests.Count >= capacity ) return null;
		var request = new Request { Owner = owner, Pcm = pcm, Start = start, Gain = gain, Generation=producerGeneration };
		requests.Enqueue( request ); return request;
	}
	/// <summary>Session-thread shutdown. Rejects pending and future requests. Already consumed requests keep their renderer result; the session must stop their owners separately.</summary>
	public void Close()
	{
		System.Threading.Interlocked.Exchange(ref closedState,1);
		System.Threading.Interlocked.Exchange(ref pendingSeek,null)?.Completion.TrySetResult(false);
		System.Threading.Interlocked.Exchange(ref appliedSeek,null)?.Completion.TrySetResult(false);
		while ( requests.TryDequeue( out var request ) ) request.Completion.TrySetResult( false );
	}
	/// <summary>Close admission and discard future phrases at the next stereo block; leave current sound for the output fade.</summary>
	public void BeginFade()
	{
		System.Threading.Interlocked.Exchange(ref fadeStop,1);
		Close();
	}
	/// <summary>Called by the audio thread once before rendering a complete stereo block.</summary>
	public void Drain( AudioMusicPcmRenderer renderer )
	{
		renderer.Paused = System.Threading.Interlocked.CompareExchange(ref requestedPause, 0, 0) != 0;
		if(System.Threading.Interlocked.CompareExchange(ref fadeStop,0,0)!=0)
		{
			renderer.DiscardPendingPhrases();
			return;
		}
		if(closed)return;
		var seek=System.Threading.Interlocked.Exchange(ref pendingSeek,null);
		if(seek is not null)
		{
			renderer.Seek(seek.Sample);renderGeneration=seek.Generation;appliedSeek=seek;
		}
		for ( var i = 0; i < capacity && requests.TryPeek(out var next); i++ )
		{
			// A newer seek can be published after this block takes its control snapshot.
			if(next.Generation>renderGeneration)break;
			if(!requests.TryDequeue(out var request))continue;
			var accepted = request.Generation==renderGeneration && renderer.Schedule( request.Owner, request.Pcm, request.Start, request.Gain );
			request.Completion.TrySetResult( accepted );
		}
	}
}


namespace Smod;

/// <summary>
/// Render-thread-owned stereo PCM core. Callers must publish commands to the render
/// thread before calling Schedule, Pause, or Render. No engine or gameplay clock is used.
/// Source arrays must remain immutable while a voice is active.
/// </summary>
internal sealed class AudioMusicPcmRenderer
{
	sealed class Voice
	{
		public bool Active;
		public AudioMusicRequestOwner Owner;
		public short[] Pcm;
		public long Start;
		public float Gain,CurrentOwnerGain;
		public int ReleaseRemaining = -1;
	}
	readonly Voice[] voices;
	readonly int releaseFrames;
	readonly AudioMusicPcmBacking backing;
	public AudioMusicPcmRenderer( int capacity, int cancellationReleaseFrames = 128, AudioMusicPcmBacking backing = null )
	{
		if ( capacity < 1 || capacity > 64 ) throw new ArgumentOutOfRangeException( nameof( capacity ) );
		if ( cancellationReleaseFrames < 1 || cancellationReleaseFrames > 44100 ) throw new ArgumentOutOfRangeException( nameof( cancellationReleaseFrames ) );
		voices = new Voice[capacity]; releaseFrames = cancellationReleaseFrames; this.backing = backing;
		for ( var i = 0; i < capacity; i++ ) voices[i] = new Voice();
	}
	public long Position { get; private set; }
	/// <summary>Render-thread seek. Discard phrases from the previous timeline; backing uses the new common sample.</summary>
	public void Seek(long sample)
	{
		if(sample<0 || sample>long.MaxValue-8192)throw new ArgumentOutOfRangeException(nameof(sample));
		for(var i=0;i<voices.Length;i++)if(voices[i].Active)Remove(i);
		Position=sample;
	}
	/// <summary>Render-thread fade boundary: keep sounding phrases, remove those that have not started.</summary>
	public void DiscardPendingPhrases()
	{
		for(var i=0;i<voices.Length;i++)
			if(voices[i].Active && voices[i].Start>=Position)Remove(i);
	}
	public bool Paused { get; set; }
	public int ActiveCount { get; private set; }
	public bool Schedule( AudioMusicRequestOwner owner, short[] stereoPcm, long startSample, float gain )
	{
		ArgumentNullException.ThrowIfNull( owner ); ArgumentNullException.ThrowIfNull( stereoPcm );
		if ( stereoPcm.Length == 0 || stereoPcm.Length % 2 != 0 ) throw new ArgumentException( "Expected nonempty interleaved stereo PCM.", nameof( stereoPcm ) );
		if ( !float.IsFinite( gain ) || gain < 0 || gain > 1 ) throw new ArgumentOutOfRangeException( nameof( gain ) );
		if ( startSample < Position || startSample > long.MaxValue - stereoPcm.Length / 2 || owner.IsCancelled ) return false;
		for ( var i = 0; i < voices.Length; i++ )
		{
			if ( voices[i].Active ) continue;
			var voice = voices[i]; voice.Owner = owner; voice.Pcm = stereoPcm; voice.Start = startSample; voice.Gain = gain; voice.CurrentOwnerGain=owner.Gain; voice.ReleaseRemaining = -1; voice.Active = true; ActiveCount++; return true;
		}
		return false;
	}
	/// <summary>Add the owned backing and phrases to existing stereo output. A paused core adds nothing and holds position.</summary>
	public void Render( Span<float> stereoOutput )
	{
		if ( stereoOutput.Length % 2 != 0 ) throw new ArgumentException( "Expected an interleaved stereo output block.", nameof( stereoOutput ) );
		if ( Paused ) return;
		var frames = stereoOutput.Length / 2;
		if ( Position > long.MaxValue - frames ) throw new InvalidOperationException( "Music sample clock overflow." );
		backing?.Render(stereoOutput, Position);
		for ( var slot = 0; slot < voices.Length; slot++ )
		{
			var voice = voices[slot]; if ( !voice.Active ) continue;
			// Snapshot cancellation once per stereo block so both channels use the same envelope.
			if ( voice.Owner.IsCancelled && voice.ReleaseRemaining < 0 )
			{
				if ( voice.Start >= Position ) { Remove( slot ); continue; }
				voice.ReleaseRemaining = releaseFrames;
			}
			var ownerGain=voice.Owner.Gain;
			for ( var i = 0; i < frames; i++ )
			{
				var sourceFrame = Position + i - voice.Start;
				if ( sourceFrame < 0 ) continue;
				if ( sourceFrame >= voice.Pcm.Length / 2 || voice.ReleaseRemaining == 0 ) break;
				voice.CurrentOwnerGain+=Math.Clamp(ownerGain-voice.CurrentOwnerGain,-1f/882,1f/882);
				var gain = voice.Gain * voice.CurrentOwnerGain * (voice.ReleaseRemaining < 0 ? 1f : voice.ReleaseRemaining / (float)releaseFrames);
				if(voice.Owner.Envelope is {} envelope)gain*=AudioInstrumentFade.Gain(envelope,sourceFrame/44100f,voice.Pcm.Length/88200f);
				var source = (int)sourceFrame * 2;
				stereoOutput[i * 2] += voice.Pcm[source] / 32768f * gain;
				stereoOutput[i * 2 + 1] += voice.Pcm[source + 1] / 32768f * gain;
				if ( voice.ReleaseRemaining > 0 ) voice.ReleaseRemaining--;
			}
			if ( Position + frames >= voice.Start + voice.Pcm.Length / 2 || voice.ReleaseRemaining == 0 ) Remove( slot );
		}
		Position += frames;
	}
	void Remove( int slot ) { var voice = voices[slot]; voice.Active = false; voice.Pcm = null; voice.Owner = null; ActiveCount--; }
}


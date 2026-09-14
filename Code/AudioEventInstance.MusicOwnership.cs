namespace Smod;

public sealed partial class AudioEventInstance
{
	AudioMusicRequestOwner musicRequestOwner;
	/// <summary>Audio-owned lifetime token. Gameplay supplies facts; it does not select phrases.</summary>
	internal AudioMusicRequestOwner MusicRequestOwner
	{
		get
		{
			EnsureValid(); RefreshPlaybackState();
			if ( _playbackState is not (AudioPlaybackState.Starting or AudioPlaybackState.Playing) )
				throw new InvalidOperationException( "Musical requests require a running process event." );
			return musicRequestOwner ??= new AudioMusicRequestOwner();
		}
	}
	void CancelMusicRequests() => musicRequestOwner?.Cancel();
	void ResetMusicRequests() { CancelMusicRequests(); musicRequestOwner = null; }
}

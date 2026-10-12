
namespace Smod;

/// <summary>One retained process instance's musical lifetime. Restart creates a new owner.</summary>
internal sealed class AudioMusicRequestOwner
{
	readonly System.Threading.CancellationTokenSource cancellation = new();
	object session;
	float gain=1;
	internal AudioInstrument Envelope {get;}
	public AudioMusicRequestOwner(AudioInstrument envelope=null)
	{
		if(envelope is not null)Envelope=new AudioInstrument{FadeInDuration=envelope.FadeInDuration,FadeInCurveMidpoint=envelope.FadeInCurveMidpoint,FadeOutDuration=envelope.FadeOutDuration,FadeOutCurveMidpoint=envelope.FadeOutCurveMidpoint};
	}
	public float Gain
	{
		get=>System.Threading.Interlocked.CompareExchange(ref gain,0,0);
		set {if(!float.IsFinite(value)||value<0)throw new ArgumentOutOfRangeException(nameof(value));System.Threading.Interlocked.Exchange(ref gain,value);}
	}
	public bool CanBindSession( object value )
	{
		ArgumentNullException.ThrowIfNull( value );
		return !IsCancelled && (session is null || ReferenceEquals( session, value ));
	}
	public bool TryBindSession( object value )
	{
		ArgumentNullException.ThrowIfNull( value );
		if ( IsCancelled || (session is not null && !ReferenceEquals( session, value )) ) return false;
		session = value; return true;
	}
	public bool IsCancelled => cancellation.IsCancellationRequested;
	public void Cancel() => cancellation.Cancel();
}

/// <summary>
/// Session-thread voice reservations. The renderer returns the exact lease it completed.
/// Reference identity prevents an old completion from releasing a reused slot.
/// This policy does not start audio or choose instruments.
/// </summary>
internal sealed class AudioMusicVoicePool
{
	internal sealed class Lease
	{
		internal Lease( AudioMusicRequestOwner owner, int slot ) { Owner = owner; Slot = slot; }
		public AudioMusicRequestOwner Owner { get; }
		public int Slot { get; }
		public bool IsCancelled => Owner.IsCancelled;
	}
	readonly Lease[] slots;
	public AudioMusicVoicePool( int capacity )
	{
		if ( capacity < 1 || capacity > 64 ) throw new ArgumentOutOfRangeException( nameof( capacity ) );
		slots = new Lease[capacity];
	}
	public int Capacity => slots.Length;
	public Lease TryReserve( AudioMusicRequestOwner owner )
	{
		ArgumentNullException.ThrowIfNull( owner );
		if ( owner.IsCancelled ) return null;
		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( slots[i] is not null ) continue;
			var lease = new Lease( owner, i ); slots[i] = lease; return lease;
		}
		return null;
	}
	public bool Complete( Lease lease )
	{
		if ( lease is null || lease.Slot >= slots.Length || !ReferenceEquals( slots[lease.Slot], lease ) ) return false;
		slots[lease.Slot] = null; return true;
	}
}





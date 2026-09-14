namespace Smod;

/// <summary>A section-local musical grid. Start is bar 1, beat 1; End is exclusive.</summary>
public sealed class AudioBeatGrid
{
    [Property] public string Name { get; set; } = "Music";
    [Property] public TimelineTime Start { get; set; }
    [Property] public TimelineTime End { get; set; } = new( 8 );
    [Property, Range( 1, 400 )] public float BeatsPerMinute { get; set; } = 120;
    [Property, Range( 1, 32 )] public int BeatsPerBar { get; set; } = 4;
    [Property, Range( 1, 32 )] public int BarsPerPhrase { get; set; } = 4;
    public double SecondsPerBeat => 60d / BeatsPerMinute;
    public bool IsValid => float.IsFinite( BeatsPerMinute ) && BeatsPerMinute > 0
        && BeatsPerBar > 0 && BarsPerPhrase > 0 && End.Seconds > Start.Seconds;

    /// <summary>Next quantized sample at or after earliestSample. Null means the grid has no remaining boundary. End is exclusive. Computes from the grid origin, without adding rounded beat lengths.</summary>
    internal long? NextSampleBoundary( long earliestSample, int sampleRate, int quantumBeats )
    {
        if ( !IsValid || earliestSample < 0 || sampleRate < 1 || quantumBeats < 1 )
            throw new ArgumentOutOfRangeException( nameof( earliestSample ) );
        var origin = Start.Seconds * sampleRate;
        var quantum = SecondsPerBeat * sampleRate * quantumBeats;
        if ( !double.IsFinite( quantum ) || quantum < 1 ) throw new ArgumentOutOfRangeException( nameof( quantumBeats ) );
        var index = Math.Max( 0, Math.Floor( (earliestSample - origin) / quantum ) );
        var target = origin + index * quantum;
        if ( Math.Round( target, MidpointRounding.AwayFromZero ) < earliestSample ) target = origin + (++index) * quantum;
        if ( target >= End.Seconds * sampleRate || target >= long.MaxValue || target < 0 ) return null;
        return checked((long)Math.Round( target, MidpointRounding.AwayFromZero ));
    }
    /// <summary>Convert a one-based bar and beat to an event time, including the section end.</summary>
    public TimelineTime TimeAt( int bar, double beat = 1 )
    {
        if ( !IsValid || bar < 1 || !double.IsFinite( beat ) || beat < 1 || beat >= BeatsPerBar + 1 )
            throw new ArgumentOutOfRangeException( nameof( beat ), "Use a valid grid and one-based bar/beat." );
        var seconds = Start.Seconds + ((bar - 1d) * BeatsPerBar + beat - 1d) * SecondsPerBeat;
        if ( seconds > End.Seconds + 0.0001d )
            throw new ArgumentOutOfRangeException( nameof( bar ), "Position is outside the beat grid." );
        return new TimelineTime( seconds );
    }
}

public sealed partial class AudioEvent
{
    [Property, InlineEditor, Title( "Beat Grids" )]
    public List<AudioBeatGrid> BeatGrids { get; set; } = new();

    public AudioBeatGrid BeatGridAt( double seconds ) =>
        BeatGrids?.FirstOrDefault( grid => grid is { IsValid: true }
            && seconds >= grid.Start.Seconds && seconds < grid.End.Seconds );

    internal double SecondsPerBeatAt( double seconds ) =>
        BeatGridAt( seconds )?.SecondsPerBeat ?? (TempoMap is { IsValid: true } ? 60d / TempoMap.BeatsPerMinute : 0d);
}


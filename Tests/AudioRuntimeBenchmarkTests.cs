using System.Diagnostics;
using Smod;

public sealed partial class AudioEventTests
{
    [TestMethod]
    public void AudioRuntimeBenchmark_PlainSourcePreparationAndWarmUpdatesStayBounded()
    {
        const int layers = 16;
        const int preparationIterations = 24;
        const int updateIterations = 256;

        var graph = CreateBenchmarkGraph(layers);
        var parameter = graph.Parameters.Single(parameter => parameter.Name == "Activity");
        var values = new Dictionary<AudioParameter, AudioParameterValue>
        {
            [parameter] = new AudioParameterValue(0.5f, null, false)
        };

        AudioEventValidator.Validate(graph).AssertValid();

        var preparation = MeasureAudio("graph preparation", preparationIterations,
            () => AudioEventValidator.Validate(graph).AssertValid());

        var plan = AudioEventEvaluator.BuildPlaybackPlan(graph, values, (_, tracks) => tracks[0]);
        Assert.AreEqual(layers + 1, plan.Count, "The benchmark graph must prepare one voice per layer.");
        var selected = plan[0];

        var selectedVoice = MeasureAudio("retained voice", updateIterations,
            () => _ = AudioEventEvaluator.EvaluateSelectedVoice(graph, selected, values));
        Assert.AreEqual(0L, selectedVoice.AllocatedBytes,
            "A retained voice update must not allocate after source selection.");

        var instance = AudioSystem.GetEvent(graph).CreateInstance();
        AudioBenchmarkMeasurement warmRefresh;
        AudioBenchmarkMeasurement sharedUpdate;
        try
        {
            instance.Start();
            warmRefresh = MeasureAudio("warm audition refresh", updateIterations,
                instance.RefreshAuthoredPropertiesForAudition);

            var sharedGraph = SharedSong();
            var sharedState = new AudioParameter
            {
                Name = "Activity",
                Minimum = 0,
                Maximum = 1,
                DefaultValue = 0.5f
            };
            sharedGraph.Parameters.Add(sharedState);
            AudioEventValidator.Validate(sharedGraph).AssertValid();
            var sharedSession = new EventMusicSessionFake();
            sharedSession.Loaded.SetResult(true);
            var sharedInstance = AudioSystem.GetEvent(sharedGraph).CreateInstance();
            sharedInstance.SharedMusicSessionFactory = () => sharedSession;
            try
            {
                sharedInstance.Start();
                var nextState = 0;
                sharedUpdate = MeasureAudio("shared parameter update", updateIterations, () =>
                {
                    nextState ^= 1;
                    sharedInstance.SetParameterByName("Activity", nextState);
                });
            }
            finally
            {
                sharedInstance.Stop(AudioStopMode.Immediate);
                sharedInstance.Release();
            }

            // These smoke thresholds apply to a controlled test host. Native output
            // and frame-time evidence still require a live s&box capture.
            Assert.IsTrue(preparation.P95Milliseconds < 100,
                $"Graph preparation p95 was {preparation.P95Milliseconds:F3} ms.");
            Assert.IsTrue(warmRefresh.P95Milliseconds < 25,
                $"Warm refresh p95 was {warmRefresh.P95Milliseconds:F3} ms.");
            Assert.IsTrue(sharedUpdate.P95Milliseconds < 25,
                $"Shared parameter update p95 was {sharedUpdate.P95Milliseconds:F3} ms.");
            WriteBenchmarkLine(layers, preparation, selectedVoice, warmRefresh, sharedUpdate);
        }
        finally
        {
            instance.Stop(AudioStopMode.Immediate);
            instance.Release();
        }
    }

    [TestMethod]
    public void SharedMusicParameterUpdate_UsesOneSessionMixDispatch()
    {
        var graph = SharedSong();
        var activity = new AudioParameter
        {
            Name = "Activity",
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5f
        };
        graph.Parameters.Add(activity);
        AudioEventValidator.Validate(graph).AssertValid();

        var session = new EventMusicSessionFake();
        session.Loaded.SetResult(true);
        var instance = AudioSystem.GetEvent(graph).CreateInstance();
        instance.SharedMusicSessionFactory = () => session;
        try
        {
            instance.Start();
            var before = session.Updates;
            instance.SetParameterByName("Activity", 0.75f);
            Assert.AreEqual(1, session.Updates - before,
                "One world-state update must publish one combined mix to the music session.");
            Assert.IsNotNull(session.LastParameters,
                "The combined update must include the current world-state parameters.");
            Assert.AreEqual(0.75f, session.LastParameters[activity].Number, 0.00001f,
                "The session must receive the latest parameter value.");
            Assert.IsNotNull(session.LastMix,
                "The combined update must include the current track mix.");
            Assert.AreEqual(1f, session.LastMix.Gain(graph.MasterTrack), 0.00001f,
                "The session must receive the current master track gain.");
        }
        finally
        {
            instance.Stop(AudioStopMode.Immediate);
            instance.Release();
        }
    }

    private static AudioEvent CreateBenchmarkGraph(int layers)
    {
        var graph = CreateEvent();
        graph.Parameters.Add(new AudioParameter
        {
            Name = "Activity",
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5f
        });

        for (var index = 0; index < layers; index++)
        {
            var source = CreateSoundEvent();
            graph.Sources.Add(source);

            var track = new AudioTrack
            {
                Name = $"Layer {index}",
                Volume = 0.5f + index / (float)(layers * 4)
            };
            track.Instruments.Add(new AudioInstrument { SourceId = source.Id });
            graph.Tracks.Add(track);
        }

        return graph;
    }

    private static AudioBenchmarkMeasurement MeasureAudio(string name, int iterations, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(action);
        if (iterations < 2)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        for (var index = 0; index < 8; index++)
            action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new double[iterations];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var generationZeroBefore = GC.CollectionCount(0);
        for (var index = 0; index < samples.Length; index++)
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var end = Stopwatch.GetTimestamp();
            samples[index] = (end - start) * 1000d / Stopwatch.Frequency;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var generationZeroCollections = GC.CollectionCount(0) - generationZeroBefore;
        Array.Sort(samples);
        var p95Index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95d) - 1, 0, samples.Length - 1);
        return new AudioBenchmarkMeasurement(
            name,
            samples[samples.Length / 2],
            samples[p95Index],
            samples[^1],
            allocatedBytes,
            generationZeroCollections);
    }

    private static void WriteBenchmarkLine(
        int layers,
        AudioBenchmarkMeasurement preparation,
        AudioBenchmarkMeasurement selectedVoice,
        AudioBenchmarkMeasurement warmRefresh,
        AudioBenchmarkMeasurement sharedUpdate)
    {
        var line = $"Audio benchmark: layers={layers}; " +
            $"{FormatBenchmark(preparation)}; " +
            $"{FormatBenchmark(selectedVoice)}; " +
            $"{FormatBenchmark(warmRefresh)}; " +
            $"{FormatBenchmark(sharedUpdate)}. " +
            "Controlled test host; native device and frame-time evidence require a live s&box capture.";
        Console.WriteLine(line);
    }

    private static string FormatBenchmark(AudioBenchmarkMeasurement measurement)
        => $"{measurement.Name}=p50:{measurement.P50Milliseconds:F3}ms,p95:{measurement.P95Milliseconds:F3}ms,max:{measurement.MaxMilliseconds:F3}ms,allocated:{measurement.AllocatedBytes}B,gen0:{measurement.GenerationZeroCollections}";

    private readonly record struct AudioBenchmarkMeasurement(
        string Name,
        double P50Milliseconds,
        double P95Milliseconds,
        double MaxMilliseconds,
        long AllocatedBytes,
        int GenerationZeroCollections);
}

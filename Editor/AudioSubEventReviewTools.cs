using Smod;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Editor.Mcp;

[McpToolset( "audio_subgraphs", "Validate nested music playback with muted native voices. No source files or game scene changes." )]
public static class AudioSubEventReviewTools
{
	/// <summary>Open the existing Audio Event editor and verify parent/child live attachment, track mute, solo, clear and detach restoration with a temporary muted build instance. Preserves unsaved resources, stops the test instance, and leaves the parent in authoring mode. Requires a WorldState music parameter. No audio files or game scene changes.</summary>
	[McpTool( "check_subgraph_editor" )]
	public static Task<object> CheckEditor( string resourcePath ) => AudioEventEditorWindow.CheckSubGraphEditorAsync( resourcePath );

	/// <summary>Run an isolated muted parent music instance through service, intensity changes, pause, build and stop. Returns child identities and inherited values. Requires WorldState and Intensity parameters. Does not change the active game or write audio. Next inspect the returned states and read_console.</summary>
	[McpTool( "check_music_subgraphs" )]
	public static async Task<object> Check( string resourcePath )
	{
		var resource = AudioSystem.RequireEventResource( resourcePath );
        ApplySavedHandoff(resource,resourcePath);
		var instance = AudioSystem.GetEvent( resource ).CreateInstance();
		instance.Volume = 0;
		try
		{
			instance.SetParameterByName( "WorldState", 0 );
			instance.SetParameterByName( "Intensity", .2f );
			instance.Start();
			var before = instance.InstrumentPlaybackStates.Single();
			instance.SetParameterByName( "Intensity", .8f );
			var after = instance.InstrumentPlaybackStates.Single();
			var child = AudioLiveDiagnostics.CaptureActive().Single( state => state.Id == after.ChildInstanceId );
			instance.Paused = true;
			var paused = AudioLiveDiagnostics.CaptureActive().Single( state => state.Id == after.ChildInstanceId );
			instance.Paused = false;
			instance.SetParameterByName( "WorldState", 2 );
			var pending = instance.PendingMusicSection;
			var waitingChild = instance.InstrumentPlaybackStates.Single().ChildInstanceId;
			var transitionClock = System.Diagnostics.Stopwatch.StartNew();
			var deadline = DateTime.UtcNow.AddSeconds( 15 );
			while ( instance.InstrumentPlaybackStates.Single().ChildInstanceId == after.ChildInstanceId && DateTime.UtcNow < deadline )
				await Task.Delay( 20 );
			var build = instance.InstrumentPlaybackStates.Single();
			transitionClock.Stop();
			var buildSnapshot = AudioLiveDiagnostics.CaptureActive().Single( state => state.Id == build.ChildInstanceId );
			instance.Stop( AudioStopMode.Immediate );
			var stopped = !AudioLiveDiagnostics.CaptureActive().Any( state => state.Id == instance.LiveId || state.ParentInstanceId == instance.LiveId );
			return new { IntensityKeptChild = before.ChildInstanceId == after.ChildInstanceId, Service = child, Paused = paused.State,
				Pending = pending, WaitedAtBoundary = waitingChild == after.ChildInstanceId, TransitionWaitSeconds = transitionClock.Elapsed.TotalSeconds, Build = buildSnapshot, ChangedChild = build.ChildInstanceId != after.ChildInstanceId, Stopped = stopped };
		}
		finally
		{
			instance.Stop( AudioStopMode.Immediate );
			instance.Release();
		}
	}

 [McpTool("check_music_song_rotation")]
 public static async Task<object> CheckRotation(string resourcePath)
 {
  var resource=AssetSystem.FindByPath(resourcePath)?.LoadResource<AudioEvent>() ?? throw new ArgumentException("Audio event required.");
  // Apply only the persisted rotation flag; preserve all other unsaved editor values.
  var saved=System.Text.Json.Nodes.JsonNode.Parse(AssetSystem.FindByPath(resourcePath).ReadJson());
  resource.MasterTrack.Instruments.Single().RotateSubEventsAtEnd=saved["MasterTrack"]["Instruments"][0]["RotateSubEventsAtEnd"]?.GetValue<bool>() ?? false;
  ApplySavedHandoff(resource,resourcePath);
  var instance=AudioSystem.GetEvent(resource).CreateInstance();instance.Volume=0;
  try
  {
   instance.SetParameterByName("WorldState",0);instance.Start();
   var first=instance.InstrumentPlaybackStates.Single();
   var child=AudioLiveDiagnostics.CaptureActive().Single(x=>x.Id==first.ChildInstanceId);
   instance.TimelinePosition=(float)child.GridEnd-.25f;
   var beforeEnd=instance.InstrumentPlaybackStates.Single();
   await Task.Delay(350);
   var next=instance.InstrumentPlaybackStates.Single();
   return new{Enabled=resource.MasterTrack.Instruments.Single().RotateSubEventsAtEnd,OldSong=first.ChildEvent,NewSong=next.ChildEvent,
    KeptBeforeEnd=first.ChildInstanceId==beforeEnd.ChildInstanceId,ChangedAtEnd=first.ChildEvent!=next.ChildEvent,NewPosition=next.PlaybackPosition};
  }
  finally{instance.Stop(AudioStopMode.Immediate);instance.Release();}
 }

 private static void ApplySavedHandoff(AudioEvent resource,string path)
 {
  var saved=System.Text.Json.Nodes.JsonNode.Parse(AssetSystem.FindByPath(path).ReadJson());
  var setting=saved["MasterTrack"]["Instruments"][0];
  var block=resource.MasterTrack.Instruments.Single();
  block.SubEventTransitionBeats=setting["SubEventTransitionBeats"]?.GetValue<float>() ?? 0;
  block.SubEventQuantization=Enum.Parse<TransitionQuantization>(setting["SubEventQuantization"].GetValue<string>());
  foreach(var choice in block.SubEvents)
  {
   var child=System.Text.Json.Nodes.JsonNode.Parse(AssetSystem.FindByPath(choice.Event.ResourcePath).ReadJson());
   foreach(var track in choice.Event.Tracks)
   {
    var row=child["Tracks"].AsArray().FirstOrDefault(x=>x["Name"].GetValue<string>()==track.Name);
    if(row?["TransitionRole"] is {} role)track.TransitionRole=Enum.Parse<AudioTransitionRole>(role.GetValue<string>());
   }
  }
 }
}



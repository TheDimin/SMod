using System;
using System.Globalization;
using System.Threading.Tasks;
using Smod;

namespace Editor;

internal sealed class AudioClipSlice
{
	public float Offset { get; set; }
	public float Length { get; set; }
	public float End => Offset + Length;

	public AudioClipSlice Clone()
	{
		return new AudioClipSlice { Offset = Offset, Length = Length };
	}
}

internal static class AudioClipSliceDetector
{
	public static IReadOnlyList<AudioClipSlice> Detect(
		short[] samples,
		int channels,
		float duration,
		float thresholdDecibels,
		float minimumSilence,
		float padding,
		float minimumClipLength )
	{
		if ( samples is null || samples.Length == 0 || channels <= 0 || duration <= 0f )
			return Array.Empty<AudioClipSlice>();

		var frameCount = samples.Length / channels;
		if ( frameCount == 0 )
			return Array.Empty<AudioClipSlice>();

		var sampleRate = frameCount / duration;
		var windowFrames = Math.Max( 32, (int)MathF.Round( sampleRate * 0.004f ) );
		var windowCount = (frameCount + windowFrames - 1) / windowFrames;
		var threshold = MathF.Pow( 10f, thresholdDecibels / 20f ) * short.MaxValue;
		var active = new bool[windowCount];

		for ( var window = 0; window < windowCount; window++ )
		{
			var firstFrame = window * windowFrames;
			var finalFrame = Math.Min( frameCount, firstFrame + windowFrames );
			var peak = 0;
			for ( var frame = firstFrame; frame < finalFrame; frame++ )
			{
				var sampleOffset = frame * channels;
				for ( var channel = 0; channel < channels; channel++ )
					peak = Math.Max( peak, Math.Abs( (int)samples[sampleOffset + channel] ) );
			}
			active[window] = peak >= threshold;
		}

		var windowDuration = windowFrames / sampleRate;
		var silentWindowsToSplit = Math.Max( 1, (int)MathF.Ceiling( minimumSilence / windowDuration ) );
		var detected = new List<AudioClipSlice>();
		var clipStartWindow = -1;
		var lastActiveWindow = -1;

		for ( var window = 0; window <= windowCount; window++ )
		{
			var isActive = window < windowCount && active[window];
			if ( isActive )
			{
				if ( clipStartWindow < 0 )
					clipStartWindow = window;
				lastActiveWindow = window;
				continue;
			}

			if ( clipStartWindow < 0 || window - lastActiveWindow < silentWindowsToSplit )
				continue;

			AddDetectedSlice( detected, clipStartWindow * windowDuration, (lastActiveWindow + 1) * windowDuration, duration, padding, minimumClipLength );
			clipStartWindow = -1;
			lastActiveWindow = -1;
		}

		return detected;
	}

	private static void AddDetectedSlice(
		List<AudioClipSlice> slices,
		float activeStart,
		float activeEnd,
		float duration,
		float padding,
		float minimumClipLength )
	{
		var start = MathF.Max( 0f, activeStart - padding );
		var end = MathF.Min( duration, activeEnd + padding );
		if ( end - start < minimumClipLength )
			return;

		if ( slices.Count > 0 && start < slices[^1].End )
		{
			var boundary = (slices[^1].End + start) * 0.5f;
			slices[^1].Length = MathF.Max( 0.01f, boundary - slices[^1].Offset );
			start = boundary;
		}

		slices.Add( new AudioClipSlice
		{
			Offset = start.SnapToGrid( 0.001f ),
			Length = (end - start).SnapToGrid( 0.001f )
		} );
	}
}

internal sealed class AudioClipSlicerWindow : Window
{
	private readonly Asset _asset;
	private readonly Action<IReadOnlyList<AudioClipSlice>> _create;
	private readonly Widget _content;
	private readonly Label _status;
	private readonly Widget _sliceRows;
	private readonly Button _createButton;
	private readonly SoundFile _sound;
	private AudioClipSliceWaveformWidget _waveform;
	private short[] _samples = Array.Empty<short>();
	private List<AudioClipSlice> _slices = new();
	private int _channels = 1;
	private float _duration;
	private float _thresholdDecibels = -40f;
	private float _minimumSilence = 0.08f;
	private SoundHandle _auditionHandle;
	private int _auditionGeneration;

	public AudioClipSlicerWindow( AudioEventEditorWindow parent, Asset asset, Action<IReadOnlyList<AudioClipSlice>> create )
	{
		_asset = asset;
		_create = create;
		_sound = SoundFile.Load( asset.Path );

		Parent = parent;
		WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.CloseButton | WindowFlags.WindowSystemMenuHint | WindowFlags.WindowTitle;
		DeleteOnClose = true;
		WindowTitle = $"Slice Sound — {asset.Name}";
		Size = new Vector2( 920f, 720f );
		MinimumSize = new Vector2( 720f, 520f );
		SetWindowIcon( "content_cut" );

		Canvas = new Widget( this ) { Layout = Layout.Column() };
		Canvas.Layout.Margin = 10f;
		Canvas.Layout.Spacing = 8f;

		var heading = Canvas.Layout.Add( new Label( asset.RelativePath ) );
		heading.ToolTip = asset.Path;
		var explanation = Canvas.Layout.Add( new Label( "Detect silence-separated clips, drag waveform edges to refine them, then create one embedded multi-instrument." ) );
		explanation.WordWrap = true;

		var detection = Canvas.Layout.AddRow();
		detection.Spacing = 6f;
		detection.Add( new Label( "Sensitivity" ) { FixedWidth = 72f } );
		var sensitivity = detection.Add( new ComboBox( this ) { FixedWidth = 150f } );
		AddSensitivityOption( sensitivity, "Sensitive  (-46 dB)", -46f );
		AddSensitivityOption( sensitivity, "Balanced  (-40 dB)", -40f, true );
		AddSensitivityOption( sensitivity, "Tight  (-34 dB)", -34f );
		detection.Add( new Label( "Split silence" ) { FixedWidth = 78f } );
		var silence = detection.Add( new ComboBox( this ) { FixedWidth = 120f } );
		AddSilenceOption( silence, "50 ms", 0.05f );
		AddSilenceOption( silence, "80 ms", 0.08f, true );
		AddSilenceOption( silence, "140 ms", 0.14f );
		var detect = detection.Add( new Button( "Detect Clips", "auto_fix_high" ) );
		detect.Clicked += DetectSlices;
		var wholeFile = detection.Add( new Button( "Whole File", "select_all" ) );
		wholeFile.Clicked += UseWholeFile;

		_content = Canvas.Layout.Add( new Widget( this ) { Layout = Layout.Column() }, 1 );
		_content.Layout.Spacing = 8f;
		_status = _content.Layout.Add( new Label( "Loading waveform..." ) );
		_waveform = _content.Layout.Add( new AudioClipSliceWaveformWidget( _content, AuditionSlice, OnSliceBoundaryChanged ) );
		_waveform.FixedHeight = 260f;

		var scroll = _content.Layout.Add( new ScrollArea( this ), 1 );
		_sliceRows = new Widget( scroll ) { Layout = Layout.Column() };
		_sliceRows.Layout.Spacing = 3f;
		scroll.Canvas = _sliceRows;

		var footer = Canvas.Layout.AddRow();
		footer.Spacing = 6f;
		footer.AddStretchCell();
		var cancel = footer.Add( new Button( "Cancel" ) );
		cancel.Clicked += Close;
		_createButton = footer.Add( new Button( "Create Multi-Instrument", "library_music" ) );
		_createButton.Enabled = false;
		_createButton.Clicked += CreateInstrument;

		Show();
		_ = LoadSoundAsync();
	}

	private void AddSensitivityOption( ComboBox combo, string label, float value, bool selected = false )
	{
		combo.AddItem( label, null, () =>
		{
			_thresholdDecibels = value;
			DetectSlices();
		}, null, selected );
	}

	private void AddSilenceOption( ComboBox combo, string label, float value, bool selected = false )
	{
		combo.AddItem( label, null, () =>
		{
			_minimumSilence = value;
			DetectSlices();
		}, null, selected );
	}

	private async Task LoadSoundAsync()
	{
		if ( !_sound.IsValid() || !await _sound.LoadAsync() )
		{
			_status.Text = "Could not load this sound file.";
			return;
		}

		_samples = await _sound.GetSamplesInterleavedAsync() ?? Array.Empty<short>();
		_channels = Math.Max( 1, _sound.Channels );
		_duration = MathF.Max( 0f, _sound.Duration );
		if ( _samples.Length == 0 || _duration <= 0f )
		{
			_status.Text = "This sound has no readable waveform samples.";
			return;
		}

		_waveform.SetSound( _samples, _channels, _duration, _slices );
		DetectSlices();
	}

	private void DetectSlices()
	{
		if ( _samples.Length == 0 )
			return;

		_slices = AudioClipSliceDetector.Detect(
			_samples,
			_channels,
			_duration,
			_thresholdDecibels,
			_minimumSilence,
			padding: 0.025f,
			minimumClipLength: 0.06f ).ToList();
		RefreshSlices();
	}

	private void UseWholeFile()
	{
		if ( _duration <= 0f )
			return;
		_slices = new List<AudioClipSlice> { new() { Offset = 0f, Length = _duration } };
		RefreshSlices();
	}

	private void RefreshSlices()
	{
		_slices = _slices.OrderBy( slice => slice.Offset ).ToList();
		_waveform?.SetSound( _samples, _channels, _duration, _slices );
		_status.Text = _slices.Count == 0
			? "No clips detected. Try Sensitive detection or use Whole File."
			: $"{_slices.Count} clip{(_slices.Count == 1 ? string.Empty : "s")} detected — click a region to preview; drag either edge to refine.";
		_createButton.Enabled = _slices.Count > 0;

		_sliceRows.Layout.Clear( true );
		for ( var index = 0; index < _slices.Count; index++ )
			AddSliceRow( index, _slices[index] );
		_sliceRows.Layout.AddStretchCell();
		_sliceRows.Update();
	}

	private void AddSliceRow( int index, AudioClipSlice slice )
	{
		var row = _sliceRows.Layout.AddRow();
		row.Spacing = 5f;
		row.Add( new Label( $"{index + 1:00}" ) { FixedWidth = 28f, Alignment = TextFlag.Center } );
		row.Add( new Label( "Start" ) { FixedWidth = 34f } );
		var offset = row.Add( new LineEdit( slice.Offset.ToString( "0.000", CultureInfo.InvariantCulture ) ) { FixedWidth = 90f } );
		row.Add( new Label( "Length" ) { FixedWidth = 42f } );
		var length = row.Add( new LineEdit( slice.Length.ToString( "0.000", CultureInfo.InvariantCulture ) ) { FixedWidth = 90f } );
		row.Add( new Label( $"End {slice.End:0.000}s" ), 1 );
		var play = row.Add( new ToolButton( string.Empty, "play_arrow", this ) );
		play.ToolTip = $"Preview clip {index + 1}";
		play.MouseLeftPress = () => AuditionSlice( slice );
		var remove = row.Add( new ToolButton( string.Empty, "delete", this ) );
		remove.ToolTip = $"Remove clip {index + 1}";
		remove.MouseLeftPress = () =>
		{
			_slices.Remove( slice );
			RefreshSlices();
		};

		void Commit()
		{
			if ( float.TryParse( offset.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOffset ) )
				slice.Offset = parsedOffset.Clamp( 0f, MathF.Max( 0f, _duration - 0.01f ) );
			if ( float.TryParse( length.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLength ) )
				slice.Length = parsedLength.Clamp( 0.01f, MathF.Max( 0.01f, _duration - slice.Offset ) );
			RefreshSlices();
		}

		offset.EditingFinished += Commit;
		length.EditingFinished += Commit;
	}

	private void OnSliceBoundaryChanged()
	{
		RefreshSlices();
	}

	private async void AuditionSlice( AudioClipSlice slice )
	{
		_auditionGeneration++;
		var generation = _auditionGeneration;
		_auditionHandle?.Stop();
		_auditionHandle = Sound.PlayFile( _sound, 0.5f, 1f );
		if ( !(_auditionHandle?.IsValid() ?? false) )
			return;

		_auditionHandle.ListenLocal = true;
		_auditionHandle.Position = Vector3.Forward * 64f;
		_auditionHandle.Time = slice.Offset;
		await Task.Delay( Math.Max( 1, (int)MathF.Round( slice.Length * 1000f ) ) );
		if ( generation == _auditionGeneration )
		{
			_auditionHandle?.Stop();
			_auditionHandle = null;
		}
	}

	private void CreateInstrument()
	{
		if ( _slices.Count == 0 )
			return;

		_create( _slices.Select( slice => slice.Clone() ).ToArray() );
		Close();
	}

	protected override void OnClosed()
	{
		_auditionGeneration++;
		_auditionHandle?.Stop();
		_auditionHandle = null;
		base.OnClosed();
	}
}

internal sealed class AudioClipSliceWaveformWidget : Widget
{
	private readonly Action<AudioClipSlice> _audition;
	private readonly Action _boundariesChanged;
	private short[] _samples = Array.Empty<short>();
	private IReadOnlyList<AudioClipSlice> _slices = Array.Empty<AudioClipSlice>();
	private int _channels = 1;
	private float _duration;
	private AudioClipSlice _draggedSlice;
	private bool _draggingStart;

	public AudioClipSliceWaveformWidget( Widget parent, Action<AudioClipSlice> audition, Action boundariesChanged ) : base( parent )
	{
		_audition = audition;
		_boundariesChanged = boundariesChanged;
		Cursor = CursorShape.Finger;
	}

	public void SetSound( short[] samples, int channels, float duration, IReadOnlyList<AudioClipSlice> slices )
	{
		_samples = samples ?? Array.Empty<short>();
		_channels = Math.Max( 1, channels );
		_duration = MathF.Max( 0f, duration );
		_slices = slices ?? Array.Empty<AudioClipSlice>();
		Update();
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, 3f );

		if ( _samples.Length == 0 || _duration <= 0f )
			return;

		DrawWaveform();
		for ( var index = 0; index < _slices.Count; index++ )
			DrawSlice( _slices[index], index );
		DrawTimeAxis();
	}

	private void DrawWaveform()
	{
		var frames = _samples.Length / _channels;
		var columns = Math.Max( 1, (int)Width );
		Paint.SetPen( Theme.Text.WithAlpha( 0.42f ) );
		for ( var column = 0; column < columns; column++ )
		{
			var firstFrame = column * frames / columns;
			var finalFrame = Math.Min( frames, Math.Max( firstFrame + 1, (column + 1) * frames / columns ) );
			var peak = 0;
			for ( var frame = firstFrame; frame < finalFrame; frame++ )
			{
				var sampleOffset = frame * _channels;
				for ( var channel = 0; channel < _channels; channel++ )
					peak = Math.Max( peak, Math.Abs( (int)_samples[sampleOffset + channel] ) );
			}
			var amplitude = peak / (float)short.MaxValue * (Height - 28f) * 0.48f;
			var center = (Height - 20f) * 0.5f;
			Paint.DrawLine( new Vector2( column, center - amplitude ), new Vector2( column, center + amplitude ) );
		}
	}

	private void DrawSlice( AudioClipSlice slice, int index )
	{
		var left = TimeToPosition( slice.Offset );
		var right = TimeToPosition( slice.End );
		var rect = new Rect( left, 0f, MathF.Max( 2f, right - left ), Height - 20f );
		var color = index % 2 == 0 ? Theme.Blue : Theme.Green;
		Paint.ClearPen();
		Paint.SetBrush( color.WithAlpha( 0.16f ) );
		Paint.DrawRect( rect );
		Paint.SetPen( color, 2f );
		Paint.DrawLine( rect.TopLeft, rect.BottomLeft );
		Paint.DrawLine( rect.TopRight, rect.BottomRight );
		Paint.SetPen( Color.White.WithAlpha( 0.85f ) );
		Paint.SetDefaultFont( 7f, 700 );
		Paint.DrawText( rect.Shrink( 5f, 3f ), (index + 1).ToString(), TextFlag.LeftTop );
	}

	private void DrawTimeAxis()
	{
		var y = Height - 20f;
		Paint.SetPen( Theme.Text.WithAlpha( 0.22f ) );
		Paint.DrawLine( new Vector2( 0f, y ), new Vector2( Width, y ) );
		var interval = _duration > 30f ? 5f : _duration > 12f ? 2f : 1f;
		Paint.SetDefaultFont( 6.5f );
		for ( var time = 0f; time <= _duration; time += interval )
		{
			var x = TimeToPosition( time );
			Paint.DrawLine( new Vector2( x, y ), new Vector2( x, y + 4f ) );
			Paint.DrawText( new Rect( x + 3f, y, 50f, 18f ), $"{time:0.#}s", TextFlag.LeftCenter );
		}
	}

	protected override void OnMousePress( MouseEvent e )
	{
		base.OnMousePress( e );
		if ( !e.LeftMouseButton || _duration <= 0f )
			return;

		var handleDistance = 7f;
		var closestDistance = float.MaxValue;
		foreach ( var slice in _slices )
		{
			var startDistance = MathF.Abs( e.LocalPosition.x - TimeToPosition( slice.Offset ) );
			if ( startDistance < handleDistance && startDistance < closestDistance )
			{
				_draggedSlice = slice;
				_draggingStart = true;
				closestDistance = startDistance;
			}
			var endDistance = MathF.Abs( e.LocalPosition.x - TimeToPosition( slice.End ) );
			if ( endDistance < handleDistance && endDistance < closestDistance )
			{
				_draggedSlice = slice;
				_draggingStart = false;
				closestDistance = endDistance;
			}
		}

		if ( _draggedSlice is not null )
		{
			e.Accepted = true;
			return;
		}

		var time = PositionToTime( e.LocalPosition.x );
		var selected = _slices.FirstOrDefault( slice => time >= slice.Offset && time <= slice.End );
		if ( selected is not null )
			_audition( selected );
	}

	protected override void OnMouseMove( MouseEvent e )
	{
		base.OnMouseMove( e );
		if ( _draggedSlice is null )
		{
			Cursor = IsNearBoundary( e.LocalPosition.x ) ? CursorShape.SizeH : CursorShape.Finger;
			return;
		}

		var time = PositionToTime( e.LocalPosition.x ).SnapToGrid( 0.001f );
		if ( _draggingStart )
		{
			var end = _draggedSlice.End;
			_draggedSlice.Offset = time.Clamp( 0f, end - 0.01f );
			_draggedSlice.Length = end - _draggedSlice.Offset;
		}
		else
		{
			var end = time.Clamp( _draggedSlice.Offset + 0.01f, _duration );
			_draggedSlice.Length = end - _draggedSlice.Offset;
		}
		Cursor = CursorShape.SizeH;
		e.Accepted = true;
		Update();
	}

	protected override void OnMouseReleased( MouseEvent e )
	{
		base.OnMouseReleased( e );
		if ( _draggedSlice is null )
			return;

		_draggedSlice = null;
		Cursor = CursorShape.Finger;
		_boundariesChanged();
		e.Accepted = true;
	}

	private bool IsNearBoundary( float position )
	{
		return _slices.Any( slice =>
			MathF.Abs( position - TimeToPosition( slice.Offset ) ) < 7f
			|| MathF.Abs( position - TimeToPosition( slice.End ) ) < 7f );
	}

	private float TimeToPosition( float time )
	{
		return time / MathF.Max( 0.001f, _duration ) * Width;
	}

	private float PositionToTime( float position )
	{
		return (position / MathF.Max( 1f, Width )).Clamp( 0f, 1f ) * _duration;
	}
}

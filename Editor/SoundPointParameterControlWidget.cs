using System;
using Smod;

namespace Editor;

[CustomEditor( typeof( List<SoundPointParameterValue> ), NamedEditor = "SoundPointParameters" )]
internal sealed class SoundPointParameterControlWidget : ControlWidget
{
	private readonly SerializedCollection _collection;
	private readonly Layout _content;

	public override bool SupportsMultiEdit => false;

	public SoundPointParameterControlWidget( SerializedProperty property ) : base( property )
	{
		Layout = Layout.Column();
		Layout.Spacing = 4;
		VerticalSizeMode = SizeMode.CanGrow;
		if ( property.TryGetAsObject( out var serializedObject ) && serializedObject is SerializedCollection collection )
		{
			_collection = collection;
			_collection.OnEntryAdded = Rebuild;
			_collection.OnEntryRemoved = Rebuild;
		}

		_content = Layout.AddColumn();
		Rebuild();
	}

	protected override void OnValueChanged()
	{
		Rebuild();
	}

	private void Rebuild()
	{
		if ( _content is null )
			return;

		using var _ = SuspendUpdates.For( this );
		_content.Clear( true );
		if ( _collection is null || !_collection.Any() )
		{
			_content.Add( new Label( "Select an Audio Event to expose its parameters." ) );
			return;
		}

		foreach ( var entry in _collection )
		{
			var value = entry.GetValue<SoundPointParameterValue>();
			if ( value?.Parameter is null )
				continue;

			var row = new Widget( this ) { Layout = Layout.Row(), FixedHeight = 30f };
			row.Layout.Spacing = 4;
			row.Layout.Add( new Label( value.Parameter.Name ) { MinimumWidth = 140f } );

			if ( value.Parameter.Type == AudioParameterType.Labeled )
			{
				var combo = row.Layout.Add( new ComboBox( row ), 1 );
				foreach ( var label in value.Parameter.Labels ?? new() )
					combo.AddItem( label, selected: string.Equals( label, value.LabelValue, StringComparison.OrdinalIgnoreCase ) );
				combo.ItemChanged += () =>
				{
					value.LabelValue = combo.CurrentText;
					Commit( entry, value );
				};
			}
			else
			{
				var slider = row.Layout.Add( new FloatSlider( row )
				{
					Minimum = value.Parameter.Minimum,
					Maximum = value.Parameter.Maximum,
					Step = value.Parameter.Type == AudioParameterType.Discrete ? 1f : 0.01f,
					Value = value.NumericValue
				}, 1 );
				var format = value.Parameter.Type == AudioParameterType.Discrete ? "0" : "0.00";
				var readout = row.Layout.Add( new Label( value.NumericValue.ToString( format ) ) { MinimumWidth = 64f } );
				slider.OnValueEdited = () =>
				{
					value.NumericValue = slider.Value;
					readout.Text = slider.Value.ToString( format );
					Commit( entry, value );
				};
			}

			_content.Add( row );
		}
	}

	private void Commit( SerializedProperty entry, SoundPointParameterValue value )
	{
		entry.SetValue( value );
		entry.Parent?.NoteChanged( entry );
		SerializedProperty.Parent?.NoteChanged( SerializedProperty );
	}
}

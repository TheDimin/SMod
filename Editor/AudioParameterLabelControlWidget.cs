using System;
using Smod;

namespace Editor;

[CustomEditor( typeof( string ), WithAllAttributes = [typeof( AudioParameterLabelAttribute )] )]
internal sealed class AudioParameterLabelControlWidget : DropdownControlWidget<string>
{
	public AudioParameterLabelControlWidget( SerializedProperty property ) : base( property )
	{
	}

	protected override IEnumerable<object> GetDropdownValues()
	{
		return ResolveLabels()
			.Where( label => !string.IsNullOrWhiteSpace( label ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.Cast<object>();
	}

	private IEnumerable<string> ResolveLabels()
	{
		if ( !SerializedProperty.TryGetAttribute<AudioParameterLabelAttribute>( out var attribute ) )
			return Enumerable.Empty<string>();

		if ( string.IsNullOrWhiteSpace( attribute.ParameterProperty ) )
			return SerializedProperty.Parent.GetProperty( nameof( AudioParameter.Labels ) )?.GetValue<List<string>>() ?? Enumerable.Empty<string>();

		var parameter = SerializedProperty.Parent.GetProperty( attribute.ParameterProperty )?.GetValue<AudioParameter>();
		return parameter?.Labels ?? Enumerable.Empty<string>();
	}
}

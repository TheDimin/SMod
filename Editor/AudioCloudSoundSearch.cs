using System;

namespace Editor;

internal static class AudioCloudSoundSearch
{
	public static string BuildQuery( string query )
	{
		query = query?.Trim() ?? string.Empty;
		return string.IsNullOrWhiteSpace( query )
			? "type:vsnd sort:popular"
			: $"{query} type:vsnd";
	}

	public static int Score( IEnumerable<string> fields, string query )
	{
		if ( string.IsNullOrWhiteSpace( query ) ) return 0;
		var searchable = fields.Where( value => !string.IsNullOrWhiteSpace( value ) ).ToArray();
		var total = 0;
		foreach ( var token in query.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			var best = searchable.Select( field => FuzzyScore( field, token ) ).DefaultIfEmpty( -1 ).Max();
			if ( best < 0 ) return -1;
			total += best;
		}
		return total;
	}

	private static int FuzzyScore( string candidate, string query )
	{
		var exactIndex = candidate.IndexOf( query, StringComparison.OrdinalIgnoreCase );
		if ( exactIndex >= 0 )
		{
			var wordStart = exactIndex == 0 || !char.IsLetterOrDigit( candidate[exactIndex - 1] );
			return 1000 - exactIndex * 2 - (candidate.Length - query.Length) + (wordStart ? 150 : 0);
		}
		var score = 0;
		var candidateIndex = 0;
		var previousMatch = -2;
		foreach ( var queryCharacter in query )
		{
			while ( candidateIndex < candidate.Length && char.ToUpperInvariant( candidate[candidateIndex] ) != char.ToUpperInvariant( queryCharacter ) ) candidateIndex++;
			if ( candidateIndex >= candidate.Length ) return -1;
			var wordStart = candidateIndex == 0 || !char.IsLetterOrDigit( candidate[candidateIndex - 1] );
			score += candidateIndex == previousMatch + 1 ? 24 : 8;
			if ( wordStart ) score += 18;
			previousMatch = candidateIndex;
			candidateIndex++;
		}
		return score - candidate.Length - (candidateIndex - query.Length) * 2;
	}
}

namespace Editor;

internal sealed class AudioLibraryDragData
{
	public IReadOnlyList<AudioLibraryItem> LibraryItems { get; }
	public IReadOnlyList<Package> CloudPackages { get; }

	public AudioLibraryDragData( IReadOnlyList<AudioLibraryItem> libraryItems = null, IReadOnlyList<Package> cloudPackages = null )
	{
		LibraryItems = libraryItems ?? Array.Empty<AudioLibraryItem>();
		CloudPackages = cloudPackages ?? Array.Empty<Package>();
	}

	public bool HasItems => LibraryItems.Count > 0 || CloudPackages.Count > 0;
}

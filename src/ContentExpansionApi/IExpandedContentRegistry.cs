namespace FFTModLoader.ContentExpansion.Api;

/// <summary>
/// New IDs are reserved atomically by the loader, not by individual gameplay
/// DLLs. Registration validates ownership only; it does NOT activate native
/// commands or transformations, nor grant learning or modify saves.
/// </summary>
public interface IExpandedContentRegistry
{
    int RegistryVersion { get; }
    void RegisterContent(string contentDirectory);
}

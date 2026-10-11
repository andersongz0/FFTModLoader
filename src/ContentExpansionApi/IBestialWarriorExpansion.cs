namespace FFTModLoader.ContentExpansion.Api;

/// <summary>Separate optional contract preserves existing consumers' ABI.</summary>
public interface IBestialWarriorExpansion
{
    int BestialContractVersion { get; }
    void ActivateBestialWarrior(string contentDirectory);
}

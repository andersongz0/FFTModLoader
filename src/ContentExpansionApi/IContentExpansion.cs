namespace FFTModLoader.ContentExpansion.Api;

/// <summary>Loader-owned expansion service. Registration alone never patches gameplay.</summary>
public interface IContentExpansion
{
    int ContractVersion { get; }
    void ActivateReworkedChemist(string contentDirectory);
}

using System.Text;

namespace FFTModLoader.ContentExpansion;

/// <summary>Sidecar namespaces only; does not authenticate or launch the game.</summary>
public static class SaveProfileIdentity
{
    public static string Resolve(ulong? steamAccount,string? localWindowsSid)
    {
        // Retain the exact historical namespace for existing Steam saves.
        if(steamAccount is >0)return ExpandedSaveRegistry.Hash(BitConverter.GetBytes(steamAccount.Value));
        if(string.IsNullOrWhiteSpace(localWindowsSid))
            throw new InvalidDataException("No stable local save-profile identity is available.");
        // Domain separation prevents collisions with Steam/account namespaces.
        // Never emit the account identifier in logs or package metadata.
        return ExpandedSaveRegistry.Hash(Encoding.UTF8.GetBytes("FFTModLoader.LocalProfile.v1\0"+localWindowsSid));
    }
}

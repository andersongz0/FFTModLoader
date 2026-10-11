namespace FFTModLoader.ContentExpansion.Api;

/// <summary>Capabilities verified by the owner of expanded sprite operands.</summary>
public sealed record ExpandedShapeResources(byte ShapeId,int PageId,nint ShapeHelper,nint SequenceHelper);
public interface IExpandedJobVisuals
{
    int VisualContractVersion { get; }
    ExpandedShapeResources GetRuntimeResources(byte shape);
    byte[]? GetOwnedHookEntry(int rva);
}

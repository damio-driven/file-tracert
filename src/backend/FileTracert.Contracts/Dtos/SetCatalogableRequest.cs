namespace FileTracert.Contracts.Dtos;

/// <summary>
/// Manual override of a volume's catalogable flag. Re-enables a false positive
/// (cloud/system wrongly excluded) or excludes a volume the user doesn't want
/// catalogued. The sync preserves this override afterwards.
/// </summary>
public sealed record SetCatalogableRequest(bool IsCatalogable);

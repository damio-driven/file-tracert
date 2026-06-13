namespace FileTracert.Contracts.Dtos;

/// <summary>PATCH result: the updated root plus, when the filter changed, the reconcile outcome.</summary>
public sealed record WatchedRootUpdateResponse(WatchedRootDto Root, ReconcileResultDto? Reconcile);

namespace SchedularrTV.Service.Components;

/// <summary>
/// Cascaded to every page: whether the browser is on the machine running the server. Actions that run code or
/// open things on this machine (installing plugins, opening folders) are only offered when it is.
/// </summary>
public sealed record LocalAccess(bool IsLocal);

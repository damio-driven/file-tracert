using Microsoft.AspNetCore.SignalR;

namespace FileTracert.Host.Realtime;

/// <summary>
/// The application's single SignalR hub, mapped on <c>/hubs/events</c> (§7).
///
/// Deliberately empty: the flow is one-way, server → client. There are no groups and no
/// per-volume subscriptions either — FileTracert is single-user on loopback, so "broadcast" and
/// "send to the one connected UI" are the same thing, and a subscription protocol would only add
/// a handshake that can go out of sync with what the client is showing.
///
/// Authentication is the loopback token, enforced by <see cref="Infrastructure.TokenAuthMiddleware"/>
/// before the connection is negotiated — the hub itself never sees an unauthenticated client.
/// </summary>
public sealed class FileTracertHub : Hub;

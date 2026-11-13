using System;
using System.Net.Http;
using Jellyfin.Plugin.P2PShare.Core;
using Jellyfin.Plugin.P2PShare.Core.Model;
using Jellyfin.Plugin.P2PShare.Core.Sync;
using Jellyfin.Plugin.P2PShare.Core.Tokens;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.P2PShare.Services;

/// <summary>
/// Creates <see cref="RemoteShareClient"/> instances for incoming connections.
/// </summary>
public sealed class PeerClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public PeerClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Decodes a token and validates it against the global settings.</summary>
    public static ShareInvite DecodeToken(string token, GlobalSettings settings)
    {
        if (!ShareInviteCodec.TryDecode(token, out var invite, out var error) || invite is null)
        {
            throw new ArgumentException(error ?? "Invalid token.");
        }

        if (settings.RequireHttps && !UrlHelper.IsHttps(invite.ServerUrl))
        {
            throw new ArgumentException("This server requires HTTPS peers, but the token points to " + invite.ServerUrl + ".");
        }

        return invite;
    }

    public RemoteShareClient Create(ShareInvite invite)
        => new(_httpClientFactory.CreateClient(NamedClient.Default), invite);

    public RemoteShareClient Create(IncomingConnection connection, GlobalSettings settings)
        => Create(DecodeToken(connection.Token, settings));
}

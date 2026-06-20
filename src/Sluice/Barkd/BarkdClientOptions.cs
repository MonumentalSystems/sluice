namespace Sluice.Barkd;

/// <summary>
/// Binding for the barkd REST client. Standalone configuration (base URL, bearer token, timeout) so the
/// client stands alone. Unset <see cref="BaseUrl"/> ⇒ the client reports <see cref="IBarkdClient.IsConfigured"/>
/// false and minting/paying fails loud.
/// </summary>
public sealed class BarkdClientOptions
{
    /// <summary>e.g. <c>http://barkd:3535</c> (cluster-internal only).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Bearer token (barkd datadir secret — <c>barkd secret show</c>).</summary>
    public string? Token { get; set; }

    /// <summary>HTTP timeout in seconds (floored at 5 inside the client).</summary>
    public int TimeoutSeconds { get; set; } = 20;
}

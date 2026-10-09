namespace DotMarc.Graph;

public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Makes the next token a fresh one from Entra rather than the cached one. A token carries the permissions
    /// granted when it was issued, so after permissions change (Mail.Send added, say) the cached one would still be
    /// refused until it expired.</summary>
    void Invalidate()
    {
    }
}

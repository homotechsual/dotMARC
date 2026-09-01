namespace DotMarc.DnsPush;

/// <summary>PushTarget is "mta-sts" or "dmarc" — which record kind this push is for. Deliberately
/// carries no record VALUE: the callback endpoint re-derives what to push at push time (see
/// DmarcTxtLookup's doc comment for why), so this only needs enough to know which domain and which
/// flow, plus the PKCE verifier the /start step generated.</summary>
public sealed record DnsPushState(
    int DomainId,
    string PushTarget,
    string CodeVerifier,
    DateTimeOffset ExpiresAtUtc);

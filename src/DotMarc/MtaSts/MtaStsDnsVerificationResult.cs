namespace DotMarc.MtaSts;

/// <summary>The outcome of one MtaStsDnsVerifier.VerifyAsync call. PointsElsewhere is worth
/// distinguishing from NotFound in MtaStsCheckDetail - it usually means a copy-paste mistake in
/// the CNAME's target, or that the deployment was rebuilt and its hostname changed, not that the
/// record simply hasn't been added yet.</summary>
public enum MtaStsDnsVerificationResult
{
    Resolved,
    NotFound,
    PointsElsewhere
}

/// <summary>What VerifyAsync found. CurrentTarget is the CNAME's live target (without its trailing
/// dot) when there is one, so a check can say where the record points now.</summary>
public sealed record MtaStsDnsVerification(MtaStsDnsVerificationResult Result, string? CurrentTarget = null);

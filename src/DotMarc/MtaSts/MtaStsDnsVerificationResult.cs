namespace DotMarc.MtaSts;

/// <summary>The outcome of one MtaStsDnsVerifier.VerifyAsync call. PointsElsewhere is worth
/// distinguishing from NotFound in MtaStsCheckDetail — it usually means a copy-paste mistake in
/// the CNAME's target, not that the record simply hasn't been added yet.</summary>
public enum MtaStsDnsVerificationResult
{
    Resolved,
    NotFound,
    PointsElsewhere
}

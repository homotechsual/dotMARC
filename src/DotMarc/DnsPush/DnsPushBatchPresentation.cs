namespace DotMarc.DnsPush;

public static class DnsPushBatchPresentation
{
    public static string Label(DnsPushBatchDomain row) => row.Status switch
    {
        DnsPushBatchDomainStatus.Planned => row.Included ? "Waiting" : "Excluded",
        DnsPushBatchDomainStatus.Pushed => "Pushed",
        DnsPushBatchDomainStatus.NothingToChange => "Already correct",
        DnsPushBatchDomainStatus.Refused => "Needs attention",
        DnsPushBatchDomainStatus.ChangedSinceReview => "Changed since review",
        DnsPushBatchDomainStatus.ZoneNotFound => "Zone not found",
        DnsPushBatchDomainStatus.FailedAfterChange => "Failed after changing DNS",
        DnsPushBatchDomainStatus.NotAttempted => "Not attempted",
        _ => "Failed"
    };

    public static string Explain(DnsPushBatchDomain row) => row.Detail switch
    {
        "report-address-conflict" => "Groups specify conflicting report addresses. Set a domain override and create a fresh review.",
        "not-found" => "The domain is no longer available.",
        "zone-not-found" => "This provider does not host the domain's current DNS zone.",
        "unmergeable" => "The record needs manual review before it can be changed safely.",
        "error" => "Could not complete this operation. Check current DNS before retrying.",
        { Length: > 0 } detail => detail,
        _ => row.Status switch
        {
            DnsPushBatchDomainStatus.Planned => row.Included ? "Included in the reviewed push." : "This domain will not be pushed.",
            DnsPushBatchDomainStatus.Pushed => "Provider accepted the records. DNS propagation and verification are still pending.",
            DnsPushBatchDomainStatus.NothingToChange => "DNS already matches this plan.",
            DnsPushBatchDomainStatus.ChangedSinceReview => "DNS or settings changed. Nothing was pushed; create a fresh review.",
            DnsPushBatchDomainStatus.FailedAfterChange => "DNS may have missing or duplicate records. Check the provider before retrying.",
            DnsPushBatchDomainStatus.NotAttempted => "The batch stopped before this domain was pushed.",
            _ => "Check this domain's DNS and create a fresh review."
        }
    };
}

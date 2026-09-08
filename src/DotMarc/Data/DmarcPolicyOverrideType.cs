namespace DotMarc.Data;

// Mirrors DmarcRua's PolicyOverrideType member-for-member, so DmarcReportParser can Enum.Parse
// directly from its ToString() output.
public enum DmarcPolicyOverrideType
{
    None,
    Forwarded,
    SampledOut,
    TrustedForwarder,
    MailingList,
    LocalPolicy,
    Other
}

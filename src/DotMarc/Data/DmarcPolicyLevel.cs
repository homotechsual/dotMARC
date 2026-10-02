namespace DotMarc.Data;

/// <summary>A DMARC policy (the p= or sp= tag), weakest first, so a lower value is a weaker policy.</summary>
public enum DmarcPolicyLevel
{
    None,
    Quarantine,
    Reject
}

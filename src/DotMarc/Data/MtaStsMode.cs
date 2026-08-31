namespace DotMarc.Data;

/// <summary>The `mode` field of a hosted MTA-STS policy, per RFC 8461 §3. Testing is listed first
/// so it is the enum's (and therefore the database column's) default value: a wrong MX host list
/// under Enforce causes real mail rejection, so a newly-configured domain should never start
/// there.</summary>
public enum MtaStsMode
{
    Testing,
    Enforce,
    None
}

namespace DotMarc.Data;

// Mirrors the union of DmarcRua's DKIMResultType/SpfResultType (both IANA-registered, stable
// result codes) - deliberately NOT the existing AuthResult enum (Pass/Fail only), which is the
// collapsed DMARC-alignment result already on ReportRecord. Collapsing away TempError vs PermError
// vs Neutral here would throw away exactly the operational distinction this feature exists to
// surface ("transient DNS hiccup" vs "sender never published a valid record at all"). Member names
// match both source enums' ToString() output exactly, so DmarcReportParser can Enum.Parse directly
// from either without a manual mapping switch.
public enum DmarcMechanismResult
{
    None,
    Default,
    Neutral,
    Pass,
    Fail,
    Policy,
    SoftFail,
    TempError,
    Invalid,
    Unknown,
    PermError,
    HardFail
}

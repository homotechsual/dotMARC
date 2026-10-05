namespace DotMarc.Notifications;

/// <summary>How a new SPF record ends: <c>~all</c> (softfail) or <c>-all</c> (fail).</summary>
public enum SpfAllQualifier
{
    SoftFail,
    Fail
}

/// <summary>Singleton settings row for the records dotMARC writes, seeded as Id 1 by the migration like the other
/// settings rows.</summary>
public sealed class DnsRecordSettings
{
    public int Id { get; set; }
    public SpfAllQualifier SpfAllQualifier { get; set; } = SpfAllQualifier.SoftFail;

    public static char ToQualifier(SpfAllQualifier qualifier) => qualifier == SpfAllQualifier.Fail ? '-' : '~';
}

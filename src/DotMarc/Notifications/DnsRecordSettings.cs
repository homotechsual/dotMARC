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

    /// <summary>When a DMARC push rewrites rua=, keep the other addresses already in it (up to the two a record can
    /// reliably list, counting dotMARC's own) instead of replacing them all with dotMARC's mailbox.</summary>
    public bool KeepOtherRuaAddresses { get; set; } = true;

    /// <summary>A second report destination written into every DMARC record dotMARC pushes, beside its own mailbox.
    /// Null when not set.</summary>
    public string? AdditionalRuaAddress { get; set; }

    public const int MaxAdditionalRuaAddressLength = 320;

    public static char ToQualifier(SpfAllQualifier qualifier) => qualifier == SpfAllQualifier.Fail ? '-' : '~';

    /// <summary>The address trimmed, or null when blank. False when what is left is not a single plain address (it
    /// would break the rua= list: spaces, commas, semicolons, a scheme, a size suffix).</summary>
    public static bool TryNormaliseRuaAddress(string? input, out string? address)
    {
        address = null;
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        var at = trimmed.IndexOf('@');
        var valid = trimmed.Length <= MaxAdditionalRuaAddressLength
            && at > 0 && at < trimmed.Length - 1 && at == trimmed.LastIndexOf('@')
            && trimmed.IndexOfAny([' ', ',', ';', ':', '!', '<', '>', '"']) < 0;
        if (!valid)
        {
            return false;
        }

        address = trimmed;
        return true;
    }
}

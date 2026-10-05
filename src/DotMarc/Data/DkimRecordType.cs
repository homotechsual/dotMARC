namespace DotMarc.Data;

public enum DkimRecordType
{
    /// <summary>A CNAME to a key the mail platform publishes, as Microsoft 365, Fastmail and Proton Mail use.</summary>
    Cname,

    /// <summary>The key itself, as Google Workspace and Zoho Mail use.</summary>
    Txt
}

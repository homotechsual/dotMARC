using DotMarc.Psa;

namespace DotMarc.DomainImport;

public sealed record PsaImportColumn(PsaKind Psa, ImportColumn Column, ImportNameKind NameKind);

/// <summary>The import column for each PSA's company. Their ImportColumn members sit at the end of the enum, because
/// headerless input is read in enum order.</summary>
public static class PsaImportColumns
{
    public static IReadOnlyList<PsaImportColumn> All { get; } =
    [
        new(PsaKind.HaloPsa, ImportColumn.HaloClient, ImportNameKind.HaloClient),
    ];

    public static PsaImportColumn? ForNameKind(ImportNameKind kind) => All.FirstOrDefault(column => column.NameKind == kind);
}

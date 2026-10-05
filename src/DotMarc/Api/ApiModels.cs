namespace DotMarc.Api;

public sealed record ApiNamedRef(int Id, string Name);

public sealed record ApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record ApiGroup(int Id, string Name, int DomainCount);

public sealed record ApiTag(int Id, string Name, string Color, int DomainCount);

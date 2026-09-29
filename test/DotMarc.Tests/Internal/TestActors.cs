using DotMarc.Audit;

namespace DotMarc.Tests.Internal;

/// <summary>The person tests act as when calling audited services.</summary>
internal static class TestActors
{
    public static readonly AuditActor Admin = AuditActor.ForUser("00000000-0000-0000-0000-00000000000a", "admin@example.com", "Test Admin");
}

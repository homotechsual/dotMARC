namespace DotMarc.Psa;

/// <summary>A customer in a PSA. The id is text because Autotask, ConnectWise and Halo number them differently.</summary>
public sealed record PsaCompany(string Id, string Name);

/// <summary>One choice in a PSA pick-list (board, queue, status, priority and so on).</summary>
public sealed record PsaOption(int Id, string Name);

public sealed record PsaTicketRequest(string CompanyId, string DomainName, string AlertType, string Title, string Message)
{
    /// <summary>The ticket body: the alert's message, then where it came from.</summary>
    public string Body => $"{Message}\n\nDomain: {DomainName}\nAlert type: {AlertType}\nRaised automatically by dotMARC.";
}

public enum PsaTicketState { Open, Closed, Missing }

/// <summary>Whether a PSA can take tickets: switched on, with every setting ticketing needs saved.</summary>
public sealed record PsaReadiness(bool Enabled, IReadOnlyList<string> Missing)
{
    public bool IsReady => Enabled && Missing.Count == 0;
}

public sealed record PsaCloseResult(int Closed, int Failed)
{
    public static PsaCloseResult None { get; } = new(0, 0);

    public PsaCloseResult Add(PsaCloseResult other) => new(Closed + other.Closed, Failed + other.Failed);
}

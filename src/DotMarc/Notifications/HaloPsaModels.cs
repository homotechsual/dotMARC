// src/DotMarc/Notifications/HaloPsaModels.cs
namespace DotMarc.Notifications;

public sealed record HaloClient(int Id, string Name);
public sealed record HaloTicketType(int Id, string Name);
public sealed record HaloTicketStatus(int Id, string Name);
public sealed record HaloPriority(int Id, string Name);

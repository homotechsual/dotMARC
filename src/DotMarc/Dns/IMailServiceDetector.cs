namespace DotMarc.Dns;

public interface IMailServiceDetector
{
    Task<List<DetectedMailService>> DetectAsync(string domainName, CancellationToken cancellationToken);
}

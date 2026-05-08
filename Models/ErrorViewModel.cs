namespace EvaluacionDesempenoAB.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public int StatusCode { get; set; }

    public string? Path { get; set; }

    public string? Method { get; set; }

    public string? ExceptionType { get; set; }

    public string? ExceptionMessage { get; set; }

    public string? TechnicalDetail { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public bool HasTechnicalDetail =>
        !string.IsNullOrWhiteSpace(ExceptionType) ||
        !string.IsNullOrWhiteSpace(ExceptionMessage) ||
        !string.IsNullOrWhiteSpace(TechnicalDetail);
}

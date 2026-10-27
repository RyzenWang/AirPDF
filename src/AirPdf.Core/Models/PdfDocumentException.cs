namespace AirPdf.Core.Models;

public enum PdfFailure
{
    NotFound,
    AccessDenied,
    PasswordRequired,
    InvalidPassword,
    Corrupt,
    Unsupported,
    Unknown
}

public sealed class PdfDocumentException : Exception
{
    public PdfDocumentException(PdfFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public PdfFailure Failure { get; }
}

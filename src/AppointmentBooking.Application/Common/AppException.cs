namespace AppointmentBooking.Application.Common;

public enum AppErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict
}

public sealed class AppException : Exception
{
    public string Code { get; }
    public AppErrorKind Kind { get; }

    public AppException(
        string code,
        string message,
        AppErrorKind kind)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "An error code is required.",
                nameof(code));
        }

        if (!Enum.IsDefined(typeof(AppErrorKind), kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Code = code;
        Kind = kind;
    }
}
namespace Mfr.Core.Exceptions;

/// <summary>Server answered HTTP 503 — it is in maintenance mode.</summary>
public sealed class ServerMaintenanceException : Exception
{
    public ServerMaintenanceException()
        : base("Сервер находится на техническом обслуживании, попробуйте позже.")
    {
    }
}

/// <summary>Could not reach the server after retries.</summary>
public sealed class ServerConnectionException : Exception
{
    public ServerConnectionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>File transfer failed (server error, bad MD5, unexpected protocol state).
/// StatusCode — код ответа хранилища, когда он был: 5xx повторяется, остальное — нет.</summary>
public sealed class DownloadFileException : Exception
{
    public DownloadFileException(string message, Exception? inner = null, int? statusCode = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

}

/// <summary>Not enough free disk space for install/update.</summary>
public sealed class NotEnoughSpaceException : Exception
{
    public NotEnoughSpaceException(string message)
        : base(message)
    {
    }
}


public sealed class TaskExecuteException : Exception
{
    public TaskExecuteException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

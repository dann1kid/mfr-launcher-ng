namespace Mfr.Core.Exceptions;

/// <summary>Server answered HTTP 503 — it is in maintenance mode (Kotlin: ServerMaintenanceException).</summary>
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

/// <summary>File transfer failed (server error, bad MD5, unexpected protocol state).</summary>
public sealed class DownloadFileException : Exception
{
    public DownloadFileException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Not enough free disk space for install/update (Kotlin: NotEnoughSpaceException).</summary>
public sealed class NotEnoughSpaceException : Exception
{
    public NotEnoughSpaceException(string message)
        : base(message)
    {
    }
}

/// <summary>A launcher task failed; the original cause is in InnerException (Kotlin: TaskExecuteException).</summary>
public sealed class TaskExecuteException : Exception
{
    public TaskExecuteException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

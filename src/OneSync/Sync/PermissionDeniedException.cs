using System;

namespace OneSync.Sync;

/// <summary>
/// Thrown when Microsoft Graph returns 403 Forbidden - the user lacks the
/// SharePoint/OneDrive permission for the operation. Distinct from
/// <see cref="RemoteConflictException"/> (412) because permission errors are
/// permanent: retrying won't help, the admin needs to grant access.
/// </summary>
internal sealed class PermissionDeniedException : Exception
{
    public string Operation { get; }
    public string RelativePath { get; }
    public string? ServerMessage { get; }

    public PermissionDeniedException(string operation, string relativePath, string? serverMessage)
        : base($"Permission denied for {operation} of {relativePath}" +
               (serverMessage is null ? "" : $": {serverMessage}"))
    {
        Operation = operation;
        RelativePath = relativePath;
        ServerMessage = serverMessage;
    }
}

using System;

namespace OneSync.Widget;

/// <summary>Turns the uploader's error text into something a member of staff can act on,
/// and says whether pressing Retry could ever help.</summary>
internal static class SyncErrorText
{
    public static (string Text, bool CanRetry) Describe(string? raw)
    {
        var e = raw ?? "";

        if (Has(e, "permission", "access denied", "forbidden", "403"))
            return ("You don't have permission to save here", false);
        if (Has(e, "MB limit", "too large", "exceeds the maximum"))
            return ("Too big to upload" + Limit(e), false);
        if (Has(e, "invalid character", "invalid name", "not allowed in"))
            return ("The name has characters SharePoint won't accept", false);
        if (Has(e, "being used by another process", "locked", "sharing violation"))
            return ("Open in another app. Close it, then retry", true);
        if (Has(e, "timed out", "timeout", "no such host", "network", "connection", "unreachable"))
            return ("Couldn't reach SharePoint. Check your connection", true);
        if (Has(e, "quota", "insufficient storage", "507"))
            return ("The library is full", false);
        if (Has(e, "not found", "404"))
            return ("The folder has gone from SharePoint", true);

        return ("Couldn't upload this file", true);
    }

    private static bool Has(string s, params string[] bits)
    {
        foreach (var b in bits)
            if (s.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // "larger than the 250 MB limit" -> " (over 250 MB)"
    private static string Limit(string e)
    {
        var i = e.IndexOf(" MB", StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return "";
        var start = i;
        while (start > 0 && char.IsDigit(e[start - 1])) start--;
        return start < i ? $" (over {e[start..i]} MB)" : "";
    }
}

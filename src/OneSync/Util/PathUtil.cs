using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace OneSync.Util;

internal static class PathUtil
{
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Environment.ExpandEnvironmentVariables(path);
    }

    public static string EnsureDirectory(string path)
    {
        var expanded = Expand(path);
        Directory.CreateDirectory(expanded);
        return expanded;
    }

    public static string CombineExpanded(string root, params string[] parts)
    {
        var expanded = Expand(root);
        return Path.Combine(new[] { expanded }.Concat(parts).ToArray());
    }

    public static string NormalizeRelative(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return "/";
        var p = relativePath.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        return p;
    }

    public static string ToWindowsRelative(string posixRelative)
    {
        if (string.IsNullOrEmpty(posixRelative) || posixRelative == "/") return string.Empty;
        var trimmed = posixRelative.TrimStart('/');
        return trimmed.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Case-insensitive glob match of a single file name against one pattern.
    /// Supports * and ? wildcards; a pattern with no wildcards is an exact match.
    /// </summary>
    public static bool MatchesGlob(string name, string pattern)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pattern)) return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
        }
        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if the file name component of <paramref name="path"/> matches any glob pattern.</summary>
    public static bool MatchesAnyGlob(string path, IEnumerable<string> patterns)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var pattern in patterns)
            if (MatchesGlob(name, pattern)) return true;
        return false;
    }
}

internal static class ArrayExt
{
    public static T[] Concat<T>(this T[] head, T[] tail)
    {
        var result = new T[head.Length + tail.Length];
        Array.Copy(head, result, head.Length);
        Array.Copy(tail, 0, result, head.Length, tail.Length);
        return result;
    }
}

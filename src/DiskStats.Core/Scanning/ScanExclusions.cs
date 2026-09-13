using System.Text;
using System.Text.RegularExpressions;

namespace DiskStats.Core.Scanning;

/// <summary>Case-insensitive, root-relative globs. A bare name also matches at any depth.</summary>
public sealed class ScanExclusions
{
    private readonly string _root;
    private readonly (Regex Pattern, bool NameOnly)[] _patterns;

    public ScanExclusions(string root, IEnumerable<string> patterns)
    {
        _root = Path.GetFullPath(root);
        _patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
        {
            string pattern = p.Trim().Replace('\\', '/').TrimEnd('/');
            return (Glob(pattern, descendants: true), !pattern.Contains('/'));
        }).ToArray();
    }

    public bool IsExcluded(string path)
    {
        string relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
        // Ausserhalb der Wurzel ("../x" oder ein anderes Laufwerk) gibt es nichts auszuschliessen —
        // ein "**" wuerde sonst auch dort zuschlagen, wo der Scan gar nicht hinsieht.
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return _patterns.Any(p => p.Pattern.IsMatch(p.NameOnly ? name : relative));
    }

    public static Regex Glob(string pattern, bool descendants = false)
    {
        pattern = pattern.Replace('\\', '/');
        var regex = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { regex.Append("(?:.*/)?"); i++; }
                else regex.Append(".*");
            }
            else if (pattern[i] == '*') regex.Append("[^/]*");
            else if (pattern[i] == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(pattern[i].ToString()));
        }
        if (descendants) regex.Append("(?:/.*)?");
        regex.Append('$');
        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100));
    }
}

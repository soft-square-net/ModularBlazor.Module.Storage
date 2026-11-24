using System.Text;

namespace soft.square.net.WebApi.Modules.Storage.Core.Common;

/// <summary>
/// File and folder names follow Windows: at most 255 characters, none of <c>\ / : * ? " &lt; &gt; |</c> or control
/// characters, not a reserved device name (CON, NUL, COM1, ...), and trailing spaces and dots are dropped (as Explorer
/// does). Names are compared without case (<see cref="Key"/>), so "Report.pdf" and "REPORT.PDF" cannot share a folder.
/// </summary>
public static class ItemNames
{
    public const int MaxLength = 255;

    private static readonly char[] Invalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>The name as stored (NFC, trailing spaces and dots removed), or a <see cref="DomainException"/>.</summary>
    public static string Require(string? name)
    {
        var value = (name ?? "").Normalize(NormalizationForm.FormC).TrimEnd(' ', '.');
        if (value.Length == 0) throw new DomainException("A name is required.");
        if (value.Length > MaxLength) throw new DomainException($"A name can have at most {MaxLength} characters.");
        if (value.IndexOfAny(Invalid) >= 0 || value.Any(char.IsControl))
            throw new DomainException("A name cannot contain any of these characters: \\ / : * ? \" < > |");
        var stem = value.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) throw new DomainException($"'{value}' is a reserved name.");
        return value;
    }

    /// <summary>The comparison key of a valid name: names that differ only by case share a key.</summary>
    public static string Key(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    /// <summary>The key a deleted item keeps, so its name is free again in its folder (and unique among deleted ones).</summary>
    public static string DeletedKey(Guid id) => $"|DELETED|{id:N}";

    /// <summary>"Report (2).pdf": the name Explorer gives when "keep both" is chosen.</summary>
    public static string Numbered(string name, int number, bool isFolder)
    {
        var dot = isFolder ? -1 : name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({number}){name[dot..]}" : $"{name} ({number})";
    }

    /// <summary>"Report - Copy.pdf", then "Report - Copy (2).pdf": the name of a pasted copy when the name is taken.</summary>
    public static string CopyName(string name, int number, bool isFolder)
    {
        var dot = isFolder ? -1 : name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        return number <= 1 ? $"{stem} - Copy{extension}" : $"{stem} - Copy ({number}){extension}";
    }

    /// <summary>The lowercase extension without the dot, or null ("report.PDF" is "pdf").</summary>
    public static string? Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : null;
    }
}

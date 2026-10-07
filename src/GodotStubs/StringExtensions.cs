namespace Godot;

public static class StringExtensions
{
    public static string Capitalize(this string instance)
    {
        // Godot: "snake_case_name" -> "Snake Case Name"
        var parts = instance.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    // Preserve Godot resource/user prefixes rather than using OS path semantics.
    public static string PathJoin(this string instance, string file)
    {
        if (instance.Length == 0) return file;
        if (instance.EndsWith('/') || file.StartsWith('/')) return instance + file;
        return instance + "/" + file;
    }
}

namespace ModularDiff.Core;

/// <summary>The per-user directory ModularDiff keeps its state in: sessions, settings, sync history.
/// <para>Setting <see cref="OverrideVariable"/> redirects it. Tests and sandboxes MUST set it before
/// touching any persisted state — otherwise a test run records its temporary files as the user's
/// recent comparisons and overwrites the user's theme/language choice.</para></summary>
public static class AppPaths
{
    public const string OverrideVariable = "MODULARDIFF_DATA_DIR";

    public static string DataDirectory
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(over)) return Path.GetFullPath(over.Trim());
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // GetFolderPath returns "" when the system has no such location; never fall back to a
            // relative path (that would scatter state into the working directory).
            return Path.Combine(root.Length == 0 ? AppContext.BaseDirectory : root, "ModularDiff");
        }
    }
}

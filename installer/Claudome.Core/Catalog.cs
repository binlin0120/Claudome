using System.Reflection;
using System.Text.Json;

namespace ClaudeSetup;

public static class Catalog
{
    private static readonly object Sync = new();
    private static readonly List<Assembly> ResourceAssemblies = [typeof(Catalog).Assembly];

    /// <summary>Registers an entry/platform assembly so platform-specific embedded resources resolve cleanly.</summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        lock (Sync)
        {
            if (!ResourceAssemblies.Contains(assembly))
                ResourceAssemblies.Add(assembly);
        }
    }

    private static Stream OpenEmbeddedStream(string suffix)
    {
        Assembly[] candidates;
        lock (Sync) candidates = [.. ResourceAssemblies];
        foreach (var assembly in candidates)
        {
            var match = assembly.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (match is not null)
                return assembly.GetManifestResourceStream(match)!;
        }
        var entry = Assembly.GetEntryAssembly();
        if (entry is not null && !candidates.Contains(entry))
        {
            var match = entry.GetManifestResourceNames()
                .SingleOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (match is not null)
                return entry.GetManifestResourceStream(match)!;
        }
        throw new InvalidOperationException("Embedded resource not found: " + suffix);
    }

    public static byte[] EmbeddedBytes(string suffix)
    {
        using var stream = OpenEmbeddedStream(suffix);
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    public static string EmbeddedText(string suffix)
    {
        using var stream = OpenEmbeddedStream(suffix);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static List<Resource> Resources => JsonSerializer.Deserialize<List<Resource>>(EmbeddedText("catalog.json"), JsonFile.Format)!;
    public static bool IsGuestResource(Resource resource) => resource.Id is not ("vbox" or "windows-media" or "bootstrap-core");
}

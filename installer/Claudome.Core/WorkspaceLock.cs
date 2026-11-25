using System.IO;

namespace ClaudeSetup;

internal sealed class WorkspaceLock : IDisposable
{
    private sealed class Entry(SessionState state, string root, FileStream stream)
    {
        public SessionState State { get; } = state;
        public string Root { get; } = root;
        public FileStream Stream { get; } = stream;
        public int Count { get; set; } = 1;
    }

    private static readonly Lock Sync = new();
    private static readonly List<Entry> Active = [];
    private readonly Entry entry;
    private bool disposed;

    private WorkspaceLock(Entry entry) => this.entry = entry;

    public static bool IsHeldBy(SessionState state)
    {
        if (string.IsNullOrWhiteSpace(state.Options.Root)) return false;
        var full = Normalize(state.Options.Root);
        lock (Sync) return Active.Any(x => ReferenceEquals(x.State, state) && x.Root.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    public static WorkspaceLock Acquire(SessionState state)
    {
        var full = Normalize(state.Options.Root);
        lock (Sync)
        {
            var existing = Active.FirstOrDefault(x => ReferenceEquals(x.State, state) && x.Root.Equals(full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Count++;
                return new(existing);
            }
            var stream = new FileStream(Paths.Under(state.Options.Root, ".setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var created = new Entry(state, full, stream);
            Active.Add(created);
            return new(created);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (Sync)
        {
            if (--entry.Count > 0) return;
            Active.Remove(entry);
            entry.Stream.Dispose();
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

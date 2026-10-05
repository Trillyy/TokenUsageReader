using System.Buffers;
using System.Text.Json;

namespace TokenUsageReader.Providers;

/// <summary>
/// Incremental reader for append-only JSONL logs. Per file it remembers how many bytes it has consumed and
/// the <typeparamref name="T"/> state built from them, so each refresh parses only the lines appended since
/// the previous one instead of whole files that can be hundreds of MB.
/// </summary>
/// <param name="needle">Raw bytes a line must contain to be parsed: a cheap filter before the JSON parser.</param>
/// <param name="onLine">Folds one parsed line into its file's state.</param>
internal sealed class JsonlTail<T>(byte[] needle, Action<T, JsonElement> onLine) where T : new()
{
    private sealed class Entry(DateTime createdUtc)
    {
        public readonly DateTime CreatedUtc = createdUtc;
        public long Offset;
        public readonly T State = new();
    }

    private const int InitialBuffer = 64 * 1024;
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();

    /// <summary>
    /// When true, a file that was rewritten instead of appended to (shrunk or recreated) drops the whole cache,
    /// for states that depend on each other (e.g. de-duplication across files); otherwise only that file is reread.
    /// </summary>
    public bool ResetAllOnRewrite { get; init; }

    /// <summary>Raised whenever the cache is emptied, so owners can drop state shared across files.</summary>
    public event Action? Cleared;

    /// <summary>
    /// Brings the cache in line with <paramref name="files"/> (files not listed are forgotten) and returns
    /// the state of each. Cancelling leaves the cache consistent: offsets advance one line at a time.
    /// </summary>
    public List<T> Update(IEnumerable<FileInfo> files, CancellationToken ct)
    {
        lock (_sync)
        {
            var current = files.ToList();

            var rewritten = current.Where(f => _files.TryGetValue(f.FullName, out var e)
                                               && (f.Length < e.Offset || f.CreationTimeUtc != e.CreatedUtc)).ToList();
            if (rewritten.Count > 0 && ResetAllOnRewrite) Clear();
            else foreach (var f in rewritten) _files.Remove(f.FullName);

            var names = current.Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _files.Keys.Where(k => !names.Contains(k)).ToList()) _files.Remove(gone);

            var states = new List<T>(current.Count);
            foreach (var f in current)
            {
                ct.ThrowIfCancellationRequested();
                if (!_files.TryGetValue(f.FullName, out var entry))
                    _files[f.FullName] = entry = new Entry(f.CreationTimeUtc);
                if (f.Length > entry.Offset)
                {
                    try { ReadNewLines(f.FullName, entry, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // deleted or locked meanwhile: retry next time
                }
                states.Add(entry.State);
            }
            return states;
        }
    }

    /// <summary>Forgets every file: the next <see cref="Update"/> rereads them from the start.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _files.Clear();
            Cleared?.Invoke();
        }
    }

    private void ReadNewLines(string path, Entry entry, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan);
        stream.Seek(entry.Offset, SeekOrigin.Begin);

        var buf = ArrayPool<byte>.Shared.Rent(InitialBuffer);
        try
        {
            int len = 0;      // unconsumed bytes at the start of buf
            int searched = 0; // prefix of those bytes already known to hold no '\n'
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (len == buf.Length)
                {
                    // A single line longer than the buffer: grow it.
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Buffer.BlockCopy(buf, 0, bigger, 0, len);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                var read = stream.Read(buf, len, buf.Length - len);
                // At EOF any leftover is a line still being written: it is picked up whole next time.
                if (read == 0) return;
                len += read;

                var start = 0;
                int nl;
                while ((nl = buf.AsSpan(searched, len - searched).IndexOf((byte)'\n')) >= 0)
                {
                    var end = searched + nl;
                    ProcessLine(entry.State, buf.AsMemory(start, end - start));
                    entry.Offset += end + 1 - start;
                    start = searched = end + 1;
                }

                Buffer.BlockCopy(buf, start, buf, 0, len - start);
                len -= start;
                searched = len;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void ProcessLine(T state, ReadOnlyMemory<byte> line)
    {
        if (line.Span.IndexOf(needle) < 0) return;
        if (line.Span is [.., (byte)'\r']) line = line[..^1];
        try
        {
            using var doc = JsonDocument.Parse(line);
            onLine(state, doc.RootElement);
        }
        // Malformed lines, or unexpected value kinds making the accessors throw: skip just that line.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
    }
}

using System.Text.Json;

namespace PaspanCodeGraphMcp;

/// <param name="Target">A symbol id, <c>N:Namespace</c> or <c>project:Name</c>.</param>
public sealed record Annotation(string Target, string Note, DateTimeOffset UpdatedAt);

/// <summary>
/// Notes that agents keep about symbols, namespaces and projects (what a module is for, how a flow works), so
/// that later context and module maps carry them. Stored as JSON next to the graph cache
/// (<c>.paspan/annotations.json</c>), apart from the cache so that a rebuilt graph keeps them.
/// </summary>
public sealed class AnnotationStore
{
    private readonly Lock _lock = new();
    private readonly string _file;
    private readonly Dictionary<string, Annotation> _notes;

    private AnnotationStore(string file, Dictionary<string, Annotation> notes)
    {
        _file = file;
        _notes = notes;
    }

    public string File => _file;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _notes.Count;
            }
        }
    }

    public static AnnotationStore Open(string file)
    {
        var notes = new Dictionary<string, Annotation>(StringComparer.Ordinal);
        try
        {
            if (System.IO.File.Exists(file))
            {
                foreach (var annotation in JsonSerializer.Deserialize(System.IO.File.ReadAllText(file), AnnotationJsonContext.Default.ListAnnotation) ?? [])
                {
                    notes[annotation.Target] = annotation;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable file starts empty; the next note writes it again
        }

        return new AnnotationStore(file, notes);
    }

    public Annotation? Get(string target)
    {
        lock (_lock)
        {
            return _notes.GetValueOrDefault(target);
        }
    }

    public string? NoteOf(string target) => Get(target)?.Note;

    public IReadOnlyList<Annotation> All()
    {
        lock (_lock)
        {
            return _notes.Values.OrderBy(a => a.Target, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Sets the note of <paramref name="target"/>, or removes it when <paramref name="note"/> is empty, and writes the file.</summary>
    public Annotation? Set(string target, string note)
    {
        lock (_lock)
        {
            Annotation? annotation = null;
            if (string.IsNullOrWhiteSpace(note))
            {
                _notes.Remove(target);
            }
            else
            {
                _notes[target] = annotation = new Annotation(target, note.Trim(), DateTimeOffset.UtcNow);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(_notes.Values.OrderBy(a => a.Target, StringComparer.Ordinal).ToList(), AnnotationJsonContext.Default.ListAnnotation));
            System.IO.File.Move(temporary, _file, overwrite: true);
            return annotation;
        }
    }
}

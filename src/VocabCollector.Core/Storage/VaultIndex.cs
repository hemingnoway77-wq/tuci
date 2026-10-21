namespace VocabCollector.Core.Storage;
public sealed class VaultIndex(IReadOnlyList<NoteLocation> notes)
{
    private readonly IReadOnlyList<NoteLocation> _notes = notes;
}

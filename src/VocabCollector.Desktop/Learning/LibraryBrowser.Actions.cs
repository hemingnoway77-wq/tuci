using System.IO;
using System.Windows;
using System.Windows.Controls;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop.Learning;

public partial class LibraryBrowser
{
    private bool _operating;

    private async void DeleteNote_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || _operating || _catalog is not { } catalog || Reader.Note is not { } note) return;
        if (!NotebookDialog.Confirm(this, "删除笔记", $"将“{note.RelativePath}”移入回收站？删除会同步到其他设备，可从左侧回收站恢复。")) return;
        await MutateAsync(catalog, async () =>
        {
            // Use the displayed revision, so an external change while the prompt
            // was open prevents deleting content the reader has not seen.
            await catalog.TrashNoteAsync(note);
            if (!Owns(catalog)) return;
            Reader.ForgetNote(note.Id);
            ApplyNotes(catalog, _notes.Where(current => current.Id != note.Id).ToArray());
            var snapshot = await catalog.ScanAsync();
            if (!Owns(catalog)) return;
            ApplyNotes(catalog, snapshot.Notes);
            SetMessage("笔记已移入回收站，可从左侧回收站恢复。");
        });
    }

    private async void Trash_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || _operating || _catalog is not { } catalog) return;
        await MutateAsync(catalog, async () =>
        {
            var items = await catalog.GetTrashAsync();
            if (!Owns(catalog)) return;
            if (items.Count == 0) { SetMessage("回收站是空的。"); return; }
            var picker = new NotebookPicker(this, "回收站 · 选择笔记恢复", items.Select(item => new NotebookPickerItem(
                Path.GetFileNameWithoutExtension(item.RelativePath), item.RelativePath + " · " + item.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), item)).ToArray());
            if (picker.ShowDialog() != true || !Owns(catalog) || picker.Selected?.Value is not LibraryTrashItem item) return;
            var restored = await catalog.RestoreNoteAsync(item);
            if (!Owns(catalog)) return;
            var snapshot = await catalog.ScanAsync();
            if (!Owns(catalog)) return;
            ApplyNotes(catalog, snapshot.Notes);
            var opening = snapshot.Notes.FirstOrDefault(note => note.Id == restored.Id) ?? restored;
            CancelTreeRestore(); _refreshing = true;
            try { Reader.OpenNote(opening); }
            finally { _refreshing = false; }
            UpdateReaderChrome(); RevealNote(opening);
            SetMessage("笔记已恢复到原目录。");
        });
    }

    private bool Owns(LibraryCatalog catalog) => !_disposed && ReferenceEquals(_catalog, catalog);
    private void ApplyNotes(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes)
    {
        if (!Owns(catalog)) return;
        var ids = notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _notes.Where(note => !ids.Contains(note.Id))) Reader.ForgetNote(removed.Id);
        Configure(catalog, notes, _wordRoot);
        NotesChanged?.Invoke(this, notes); ReloadRequested?.Invoke(this, EventArgs.Empty);
    }
    private async Task MutateAsync(LibraryCatalog catalog, Func<Task> action)
    {
        if (_operating || !Owns(catalog)) return;
        _operating = true; var enabled = IsEnabled; IsEnabled = false; UpdateReaderChrome();
        try { await action(); }
        catch (LibraryConflictException)
        { if (Owns(catalog)) { SetMessage("笔记已在其他地方修改，操作未执行。请刷新后重试。"); ReloadRequested?.Invoke(this, EventArgs.Empty); } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { if (Owns(catalog)) { SetMessage(error.Message); ReloadRequested?.Invoke(this, EventArgs.Empty); } }
        finally { _operating = false; IsEnabled = enabled; if (!_disposed) UpdateReaderChrome(); }
    }
}

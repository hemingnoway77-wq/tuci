using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop.Learning;

public partial class LibraryShell
{
    private sealed record Preferences(bool Dark = true, double Left = 240, double Right = 238, bool LeftOpen = true, bool RightOpen = true, string[]? Bookmarks = null);
    private void LoadPreferences()
    {
        if (_catalog == null) return;
        try
        {
            var path = Path.Combine(_catalog.LocalStateDirectory, "workspace-ui.json");
            EnsurePreferencePath(path);
            if (File.Exists(path) && JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) is { } prefs)
            {
                _dark = prefs.Dark; _leftSize = Math.Clamp(prefs.Left, 180, 340); _rightSize = Math.Clamp(prefs.Right, 180, 300); _leftOpen = prefs.LeftOpen; _rightOpen = prefs.RightOpen;
                _bookmarks.Clear(); foreach (var id in prefs.Bookmarks ?? []) _bookmarks.Add(id);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        ApplyPalette(); LeftWidth.Width = new(_leftOpen ? _leftSize : 0); RightWidth.Width = new(_rightOpen ? _rightSize : 0);
        LeftDividerWidth.Width = new(_leftOpen ? 4 : 0); RightDividerWidth.Width = new(_rightOpen ? 4 : 0);
        ShowLeftButton.Visibility = _leftOpen ? Visibility.Collapsed : Visibility.Visible; ShowRightButton.Visibility = _rightOpen ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SavePreferences()
    {
        if (_disposed || _catalog == null) return;
        string? temporary = null;
        try
        {
            EnsurePreferencePath(Path.Combine(_catalog.LocalStateDirectory, "workspace-ui.json"));
            Directory.CreateDirectory(_catalog.LocalStateDirectory);
            temporary = Path.Combine(_catalog.LocalStateDirectory, ".workspace-ui-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences(_dark, _leftSize, _rightSize, _leftOpen, _rightOpen, _bookmarks.ToArray())));
            EnsurePreferencePath(Path.Combine(_catalog.LocalStateDirectory, "workspace-ui.json"));
            File.Move(temporary, Path.Combine(_catalog.LocalStateDirectory, "workspace-ui.json"), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { SetMessage("工作区偏好暂未保存，当前笔记不受影响。"); }
        finally { if (temporary != null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
    private static void EnsurePreferencePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("工作区偏好路径包含文件或目录链接，未访问该路径。");
    }
    private string SelectedFolder => LibraryFilesTree.SelectedItem is LibraryTreeEntry entry ? entry.IsFolder ? entry.RelativePath : entry.Note?.Folder ?? "" : "";
    private async Task PerformAsync(Func<Task> operation)
    {
        if (_operating || _disposed || _catalog == null) return;
        _operating = true;
        try
        {
            if (!await Workspace.SaveAllAsync()) { SetMessage("编辑内容尚未保存，请先处理当前笔记的保存提示。"); return; }
            await operation(); ReloadRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (LibraryConflictException) { SetMessage("文件已在别处改变，操作已停止。请刷新后再试，未覆盖现有内容。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { SetMessage(error.Message); }
        finally { _operating = false; }
    }
    private async Task<LibraryNote> LatestAsync(LibraryNote note)
    {
        var snapshot = await _catalog!.ScanAsync();
        return snapshot.Notes.FirstOrDefault(n => n.Id == note.Id) ?? throw new IOException("这篇笔记已不在词库中，请刷新后重试。");
    }
    private async Task CreateNoteAsync(string? given = null)
    {
        if (_catalog == null) return;
        var prefix = SelectedFolder.Length == 0 ? "" : SelectedFolder + "/";
        var name = given ?? NotebookDialog.Prompt(this, "新建笔记", prefix + "未命名笔记", "笔记路径，相对于当前词库。可使用 / 放入文件夹。");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        await PerformAsync(async () =>
        {
            var note = await _catalog.CreateNoteAsync(name, "# " + Path.GetFileNameWithoutExtension(name) + "\n\n");
            OpenDocument(note, true); Workspace.ToggleEditing(); SetMessage("已新建笔记。");
        });
    }
    private async void NewNote_Click(object sender, RoutedEventArgs e) => await CreateNoteAsync();
    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = NotebookDialog.Prompt(this, "新建文件夹", (SelectedFolder.Length == 0 ? "" : SelectedFolder + "/") + "未命名文件夹", "文件夹路径，相对于当前词库。");
        if (name != null) await PerformAsync(async () => { await _catalog!.CreateFolderAsync(name); SetMessage("已新建文件夹。"); });
    }
    private async Task MoveAsync(LibraryNote note, string path)
    {
        if (path.Equals(note.RelativePath, StringComparison.Ordinal)) return;
        await PerformAsync(async () => { var moved = await _catalog!.MoveNoteAsync(await LatestAsync(note), path); OpenDocument(moved); SetMessage("笔记已移动。"); });
    }
    private async Task RenameAsync(LibraryNote note)
    {
        var name = NotebookDialog.Prompt(this, "重命名笔记", Path.GetFileNameWithoutExtension(note.RelativePath), "输入新的文件名，正文保留。");
        if (name == null) return;
        var path = (note.Folder.Length == 0 ? "" : note.Folder + "/") + name;
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) path += ".md";
        await MoveAsync(note, path);
    }
    private async Task MoveWithPickerAsync(LibraryNote note)
    {
        var folder = NotebookDialog.Prompt(this, "移动笔记", note.Folder, "输入目标文件夹的相对路径。根目录使用 / 。");
        if (folder == null) return;
        folder = folder.Trim('/');
        await MoveAsync(note, (folder.Length == 0 ? "" : folder + "/") + Path.GetFileName(note.RelativePath));
    }
    private async Task TrashAsync(LibraryNote note)
    {
        if (!NotebookDialog.Confirm(this, "删除笔记", $"将“{note.Title}”移入本机回收站？可以从左下角回收站恢复。")) return;
        await PerformAsync(async () =>
        {
            await _catalog!.TrashNoteAsync(await LatestAsync(note));
            if (Workspace.Note?.Id == note.Id) await Workspace.CloseActiveTabAsync();
            SetMessage("笔记已移入回收站，可随时恢复。");
        });
    }
    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog == null) return;
        try
        {
            var trash = await _catalog.GetTrashAsync();
            if (trash.Count == 0) { SetMessage("回收站是空的。"); return; }
            var picker = new NotebookPicker(this, "回收站 · 选择笔记恢复", trash.Select(item => new NotebookPickerItem(Path.GetFileNameWithoutExtension(item.RelativePath), item.RelativePath + " · " + item.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), item)).ToArray());
            if (picker.ShowDialog() == true && picker.Selected?.Value is LibraryTrashItem item)
                await PerformAsync(async () => { var restored = await _catalog.RestoreNoteAsync(item); OpenDocument(restored, true); SetMessage("笔记已恢复到原目录。"); });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { SetMessage(error.Message); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入 Markdown", Filter = "Markdown|*.md", Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await PerformAsync(async () => { var notes = await _catalog!.ImportAsync(picker.FileNames); SetMessage($"已导入 {notes.Count} 篇 Markdown。"); });
    }
    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = LibraryFilesTree.SelectedItem as LibraryTreeEntry;
        var menu = new ContextMenu(); menu.Resources.MergedDictionaries.Add(Resources);
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        if (selected?.Note is { } note)
        {
            Add("在新标签页打开", () => OpenDocument(note, true));
            Add("重命名…", async () => await RenameAsync(note));
            Add("移动到…", async () => await MoveWithPickerAsync(note));
            Add("添加／移除书签", () => { if (!_bookmarks.Add(note.Id)) _bookmarks.Remove(note.Id); SavePreferences(); });
            Add("复制相对路径", () => { try { Clipboard.SetText(note.RelativePath); } catch (System.Runtime.InteropServices.COMException) { SetMessage("暂时无法访问剪贴板。"); } });
            menu.Items.Add(new Separator()); Add("删除", async () => await TrashAsync(note));
        }
        else
        {
            Add("新建笔记…", async () => await CreateNoteAsync());
            Add("新建文件夹…", () => NewFolder_Click(this, new()));
            if (selected != null) Add("在此文件夹搜索", () => { FocusSearch(); LibrarySearchBox.Text = "path:\"" + selected.RelativePath + "/\" "; LibrarySearchBox.CaretIndex = LibrarySearchBox.Text.Length; });
        }
        LibraryFilesTree.ContextMenu = menu;
        e.Handled = true; menu.PlacementTarget = LibraryFilesTree; menu.IsOpen = true;
    }
    private void QuickOpen_Click(object sender, RoutedEventArgs e) => QuickOpen();
    public async void QuickOpen()
    {
        var picker = new NotebookPicker(this, "快速切换", _notes.Select(n => new NotebookPickerItem(n.Title, n.RelativePath, n)).ToArray(), (item, query) => NotebookSearch.MatchFileName(item.Title, query) || item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase), true);
        if (picker.ShowDialog() != true) return;
        if (picker.Selected?.Value is LibraryNote note) OpenDocument(note, picker.OpenInNewTab);
        else if (picker.CreateNew) await CreateNoteAsync(picker.Query);
    }
    private void Commands_Click(object sender, RoutedEventArgs e) => Commands();
    public void Commands()
    {
        var commands = new (string Name, string Key, Action Run)[]
        {
            ("新建笔记", "", async () => await CreateNoteAsync()), ("新建标签页", "Ctrl+T", () => { ShowDocuments(); Workspace.NewTab(); }),
            ("关闭当前标签页", "Ctrl+W", async () => { if (GraphHost.IsVisible) ShowDocuments(); else await Workspace.CloseActiveTabAsync(); }), ("重新打开已关闭标签页", "Ctrl+Shift+T", () => { ShowDocuments(); Workspace.ReopenClosedTab(); }),
            ("切换阅读／编辑模式", "Ctrl+E", () => { ShowDocuments(); Workspace.ToggleEditing(); }), ("保存全部笔记", "Ctrl+S", async () => { if (!await Workspace.SaveAllAsync()) ShowDocuments(); }),
            ("快速切换", "Ctrl+O", QuickOpen), ("在全部文件中搜索", "Ctrl+Shift+F", FocusSearch), ("查找当前文档", "Ctrl+F", () => { ShowDocuments(); Workspace.Find(); }),
            ("切换左侧栏", "", () => SetLeft(!_leftOpen)), ("切换右侧栏", "", () => SetRight(!_rightOpen)),
            ("打开关系图谱", "", () => Graph_Click(this, new())), ("显示文件", "", () => Files_Click(this, new())),
            ("显示大纲", "", () => { SetRight(true); Outline_Click(this, new()); }), ("显示反向链接", "", () => { SetRight(true); Backlinks_Click(this, new()); }),
            ("切换明暗主题", "", () => Theme_Click(this, new())), ("收录单词", "", () => PageRequested?.Invoke(this, "capture")),
            ("漫游复习", "", () => PageRequested?.Invoke(this, "roaming")), ("每日阅读", "", () => PageRequested?.Invoke(this, "reading"))
        };
        var picker = new NotebookPicker(this, "命令面板", commands.Select(command => new NotebookPickerItem(command.Name, command.Key, command.Run)).ToArray());
        if (picker.ShowDialog() == true && picker.Selected?.Value is Action run) run();
    }
    private async void Shell_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.O) { QuickOpen(); e.Handled = true; }
        else if (e.Key == Key.P) { Commands(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { FocusSearch(); e.Handled = true; }
        else if (e.Key == Key.N) { e.Handled = true; await CreateNoteAsync(); }
        else if (!Workspace.IsKeyboardFocusWithin)
        {
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var number = (int)e.Key - (int)Key.D0;
            if (!shift && number is >= 1 and <= 9) { e.Handled = true; ShowDocuments(); Workspace.SelectTabNumber(number); return; }
            switch (e.Key)
            {
                case Key.T: e.Handled = true; ShowDocuments(); if (shift) Workspace.ReopenClosedTab(); else Workspace.NewTab(); break;
                case Key.W: e.Handled = true; if (GraphHost.IsVisible) ShowDocuments(); else await Workspace.CloseActiveTabAsync(); break;
                case Key.Tab: e.Handled = true; ShowDocuments(); Workspace.NextTab(shift ? -1 : 1); break;
                case Key.E: e.Handled = true; ShowDocuments(); Workspace.ToggleEditing(); break;
                case Key.S: e.Handled = true; await Workspace.SaveAllAsync(); break;
                case Key.F: e.Handled = true; ShowDocuments(); Workspace.Find(); break;
            }
        }
    }
}

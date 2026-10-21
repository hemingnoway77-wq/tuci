using System.Diagnostics;
using System.IO;
using VocabCollector.Core;
namespace VocabCollector.Desktop;
public sealed class WindowsNavigator : IAppNavigator
{
    public static Uri BuildOpenUri(NoteLocation note) => new("obsidian://open?path=" + Uri.EscapeDataString(Path.GetFullPath(note.AbsolutePath)));
    public void OpenNote(NoteLocation note)
    {
        if (!File.Exists(note.AbsolutePath)) throw new IOException("笔记已被移动或删除。");
        Process.Start(new ProcessStartInfo(BuildOpenUri(note).OriginalString) { UseShellExecute = true });
    }
    public void OpenFolder(NoteLocation note)
    {
        var directory = Path.GetDirectoryName(note.AbsolutePath)!;
        if (!Directory.Exists(directory)) throw new IOException("文件夹不存在。");
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(directory); Process.Start(start);
    }
}

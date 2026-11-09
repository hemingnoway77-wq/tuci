namespace VocabCollector.Updater;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args is not ["--install", var requestPath]) return 2;
        try
        {
            var result = await new UpdateInstaller().RunAsync(requestPath);
            if (!result.Success)
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<VocabCollector.Core.Updates.UpdateInstallRequest>(
                    await File.ReadAllTextAsync(requestPath));
                if (request?.Restart == true)
                    MessageBoxW(0, result.Message, "兔词更新", 0x00000010);
            }
            return result.Success ? 0 : 1;
        }
        catch
        {
            // Invalid or unwritable transaction paths must not cause writes elsewhere.
            return 2;
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}

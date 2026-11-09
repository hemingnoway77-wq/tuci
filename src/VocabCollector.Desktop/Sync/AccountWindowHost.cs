using System.Windows;

namespace VocabCollector.Desktop;

/// <summary>Owns the login/main-window boundary. No main surface is built before account access is granted.</summary>
public sealed class AccountWindowHost(DesktopSync sync, Func<CancellationToken, Task<bool>> requestLogin,
    Func<CancellationToken, Task<Window>> createMain, Action<Window>? selectMain = null)
{
    public Window? CurrentMain { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        void AccessChanged(object? sender, EventArgs args)
        {
            if (sync.CanUseAccount || CurrentMain is not { } window) return;
            if (window is MainWindow desktop) desktop.LockForSignIn();
            else { window.IsEnabled = false; window.Hide(); }
            window.Close();
        }
        sync.AccessChanged += AccessChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (sync.SignedIn && !sync.CanUseAccount)
                    await sync.VerifySessionAsync(cancellationToken);
                if (!sync.CanUseAccount)
                {
                    if (!await requestLogin(cancellationToken) || !sync.CanUseAccount) return;
                }
                Window window;
                try { window = await createMain(cancellationToken); }
                catch (InvalidOperationException) when (!sync.CanUseAccount) { continue; }
                if (!sync.CanUseAccount) { window.Close(); continue; }
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                CurrentMain = window;
                selectMain?.Invoke(window);
                window.Show();
                await closed.Task.WaitAsync(cancellationToken);
                CurrentMain = null;
                // Closing an authenticated main window exits the application.
                // Logout/revocation instead returns to a fresh login surface.
                if (sync.CanUseAccount) return;
            }
        }
        finally
        {
            sync.AccessChanged -= AccessChanged;
            if (CurrentMain is { } window) { window.Hide(); window.Close(); CurrentMain = null; }
        }
    }
}

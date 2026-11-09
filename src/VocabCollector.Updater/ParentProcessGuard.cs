using System.Diagnostics;

namespace VocabCollector.Updater;

public sealed record UpdateParentProcess(int Id, long StartTimeUtcTicks, string ExecutablePath);

public interface IUpdateParentProcessAccess
{
    UpdateParentProcess? Find(int processId);
    Task<bool> WaitForExitAsync(UpdateParentProcess process, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class ParentProcessGuard(IUpdateParentProcessAccess? processAccess = null)
{
    private readonly IUpdateParentProcessAccess processes = processAccess ?? new SystemParentProcessAccess();

    public async Task WaitAsync(int processId, long startTimeUtcTicks, string expectedExecutable,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || startTimeUtcTicks <= 0 || timeout <= TimeSpan.Zero)
            throw new InvalidDataException("应用进程信息无效。");
        expectedExecutable = InstallPathSafety.FullPath(expectedExecutable);
        var process = processes.Find(processId);
        // A closed application is fine. A reused PID must never be treated as that application.
        if (process is null) return;
        if (process.Id != processId || process.StartTimeUtcTicks != startTimeUtcTicks ||
            !string.Equals(InstallPathSafety.FullPath(process.ExecutablePath), expectedExecutable,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("应用进程身份已改变，请重新打开兔词后重试更新。");
        if (!await processes.WaitForExitAsync(process, timeout, cancellationToken))
            throw new TimeoutException("兔词在 60 秒内未退出，更新已取消；请关闭兔词后重试。");
    }

    private sealed class SystemParentProcessAccess : IUpdateParentProcessAccess
    {
        public UpdateParentProcess? Find(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                try
                {
                    return new UpdateParentProcess(process.Id, process.StartTime.ToUniversalTime().Ticks,
                        process.MainModule?.FileName ?? throw new InvalidDataException("无法验证应用进程路径。"));
                }
                catch (InvalidOperationException) when (process.HasExited) { return null; }
            }
            catch (ArgumentException) { return null; }
        }

        public async Task<bool> WaitForExitAsync(UpdateParentProcess expected, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Process process;
            try { process = Process.GetProcessById(expected.Id); }
            catch (ArgumentException) { return true; }
            using (process)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime().Ticks != expected.StartTimeUtcTicks)
                        throw new InvalidDataException("等待退出时应用进程身份已改变。");
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
                    return true;
                }
                catch (InvalidOperationException) when (process.HasExited) { return true; }
                catch (TimeoutException) { return false; }
            }
        }
    }
}

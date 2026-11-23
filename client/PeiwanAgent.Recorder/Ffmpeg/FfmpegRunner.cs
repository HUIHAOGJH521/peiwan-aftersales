namespace PeiwanAgent.Recorder.Ffmpeg;

public static class FfmpegLocator
{
    /// <summary>解析 ffmpeg 路径:绝对/相对路径存在即用;裸文件名则在 PATH 中查找。找不到抛 <see cref="FfmpegNotFoundException"/>。</summary>
    public static string Resolve(string configured, string? pathEnv = null, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (string.IsNullOrWhiteSpace(configured)) configured = "ffmpeg";
        configured = configured.Trim().Trim('"');

        bool hasDir = configured.Contains('\\') || configured.Contains('/');
        if (hasDir)
        {
            if (fileExists(configured)) return configured;
            throw new FfmpegNotFoundException(configured);
        }

        var names = configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { configured } : new[] { configured + ".exe", configured };
        foreach (var dir in (pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                string full;
                try { full = Path.Combine(dir.Trim().Trim('"'), n); } catch { continue; }
                if (fileExists(full)) return full;
            }
        }
        throw new FfmpegNotFoundException(configured);
    }
}

public sealed class FfmpegRunner : IFfmpegRunner
{
    private readonly IProcessLauncher _launcher;
    public string FfmpegPath { get; }

    /// <param name="ffmpegPath">已解析的路径(见 <see cref="FfmpegLocator"/>)。</param>
    public FfmpegRunner(string ffmpegPath, IProcessLauncher? launcher = null)
    {
        FfmpegPath = ffmpegPath;
        _launcher = launcher ?? new SystemProcessLauncher();
    }

    public static FfmpegRunner Create(string configuredPath, IProcessLauncher? launcher = null)
        => new(FfmpegLocator.Resolve(configuredPath), launcher);

    public IManagedProcess Start(IReadOnlyList<string> args) => _launcher.Start(new ProcessSpec(FfmpegPath, args));

    public FfmpegResult Run(IReadOnlyList<string> args, TimeSpan timeout)
    {
        using var p = Start(args);
        if (!p.WaitForExit(timeout))
        {
            p.Kill();
            p.WaitForExit(TimeSpan.FromSeconds(2));
            return new FfmpegResult(p.ExitCode ?? -1, p.Stdout, p.Stderr, TimedOut: true);
        }
        return new FfmpegResult(p.ExitCode ?? -1, p.Stdout, p.Stderr);
    }
}

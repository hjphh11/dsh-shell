using System.Diagnostics;
using System.Threading.Channels;

namespace DshShell.Tests;

/// <summary>
/// A child process whose behaviour is scripted by the test: it yields whatever
/// lines the test queues and exits when the test says so. This is what makes
/// the startup failure paths (npm fails, engine never reports ready, engine dies
/// after ready) deterministic instead of dependent on PATH, the network or real
/// binaries.
/// </summary>
internal sealed class FakeProcess : IChildProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; } = -1;
    public bool Killed { get; private set; }

    /// <summary>Queues a stdout line for the launcher to read.</summary>
    public void Emit(string line) => _stdout.Writer.TryWrite(line);

    /// <summary>Queues a stderr line for the launcher to read.</summary>
    public void EmitError(string line) => _stderr.Writer.TryWrite(line);

    /// <summary>Completes the streams and reports the process as exited.</summary>
    public void Exit(int code)
    {
        HasExited = true;
        ExitCode = code;
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public async IAsyncEnumerable<string> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in _stdout.Reader.ReadAllAsync(ct)) yield return line;
    }

    public async IAsyncEnumerable<string> ReadErrorAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in _stderr.Reader.ReadAllAsync(ct)) yield return line;
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            return await _exit.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return HasExited ? ExitCode : -1;
        }
    }

    public void KillTree()
    {
        Killed = true;
        Exit(-1);
    }

    public void Dispose() { }
}

/// <summary>
/// Hands out one pre-built <see cref="FakeProcess"/> per start, and records the
/// start requests so a test can assert what the launcher tried to run.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<int, FakeProcess> _factory;
    private readonly List<ProcessStartInfo> _starts = [];

    public FakeProcessRunner(Func<int, FakeProcess> factory) => _factory = factory;

    /// <summary>Always hands back this instance.</summary>
    public static FakeProcessRunner Single(FakeProcess process) =>
        new(_ => process);

    public IReadOnlyList<ProcessStartInfo> Starts
    {
        get { lock (_starts) return [.. _starts]; }
    }

    public IChildProcess? Start(ProcessStartInfo startInfo)
    {
        int index;
        lock (_starts)
        {
            index = _starts.Count;
            _starts.Add(startInfo);
        }
        return _factory(index);
    }
}

﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CliWrap.Utils.Extensions;

namespace CliWrap.Utils;

internal class ProcessEx(ProcessStartInfo startInfo) : IDisposable
{
    public readonly Process NativeProcess = new() { StartInfo = startInfo };

    private readonly TaskCompletionSource<object?> _exitTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id => NativeProcess.Id;

    public string Name =>
        // Can't rely on ProcessName because it becomes inaccessible after the process exits
        Path.GetFileName(NativeProcess.StartInfo.FileName);

    // We are purposely using Stream instead of StreamWriter/StreamReader to push the concerns of
    // writing and reading to PipeSource/PipeTarget at the higher level.

    public Stream StandardInput => NativeProcess.StandardInput.BaseStream;

    public Stream StandardOutput => NativeProcess.StandardOutput.BaseStream;

    public Stream StandardError => NativeProcess.StandardError.BaseStream;

    // We have to keep track of StartTime ourselves because it becomes inaccessible after the process exits
    // https://github.com/Tyrrrz/CliWrap/issues/93
    public DateTimeOffset StartTime { get; private set; }

    // We have to keep track of ExitTime ourselves because it becomes inaccessible after the process exits
    // https://github.com/Tyrrrz/CliWrap/issues/93
    public DateTimeOffset ExitTime { get; private set; }

    public int ExitCode => NativeProcess.ExitCode;

    public void Start()
    {
        // Hook up events
        NativeProcess.EnableRaisingEvents = true;
        NativeProcess.Exited += (_, _) =>
        {
            ExitTime = DateTimeOffset.Now;
            _exitTcs.TrySetResult(null);
        };

        // Start the process
        try
        {
            if (!NativeProcess.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start a process with file path '{NativeProcess.StartInfo.FileName}'. "
                        + "Target file is not an executable or lacks execute permissions."
                );
            }

            StartTime = DateTimeOffset.Now;
        }
        catch (Win32Exception ex)
        {
            throw new Win32Exception(
                $"Failed to start a process with file path '{NativeProcess.StartInfo.FileName}'. "
                    + "Target file or working directory doesn't exist, or the provided credentials are invalid.",
                ex
            );
        }
    }

    // Sends SIGINT
    public void Interrupt()
    {
        bool TryInterrupt()
        {
            try
            {
                // On Windows, we need to launch an external executable that will attach
                // to the target process's console and then send a Ctrl+C event to it.
                // https://github.com/Tyrrrz/CliWrap/issues/47
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var signaler = WindowsSignaler.Deploy();
                    return signaler.TrySend(NativeProcess.Id, 0);
                }

                // On Unix, we can just send the signal to the process directly
                if (
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                )
                {
                    return NativeMethods.Unix.Kill(NativeProcess.Id, 2) == 0;
                }

                // Unsupported platform
                return false;
            }
            catch
            {
                return false;
            }
        }

        if (!TryInterrupt())
        {
            // In case of failure, revert to the default behavior of killing the process.
            // Ideally, we should throw an exception here, but this method is called from
            // a cancellation callback upstream, so we can't do that.
            Kill();
            Debug.Fail("Failed to send an interrupt signal.");
        }
    }

    // Sends SIGKILL
    public void Kill()
    {
        try
        {
            NativeProcess.Kill(true);
        }
        catch when (NativeProcess.HasExited)
        {
            // The process has exited before we could kill it. This is fine.
        }
        catch
        {
            // The process either failed to exit or is in the process of exiting.
            // We can't really do anything about it, so just ignore the exception.
            Debug.Fail("Failed to kill the process.");
        }
    }

    public async Task WaitUntilExitAsync(CancellationToken cancellationToken = default)
    {
        await using (
            cancellationToken
                .Register(() => _exitTcs.TrySetCanceled(cancellationToken))
                .ToAsyncDisposable()
        )
            await _exitTcs.Task.ConfigureAwait(false);
    }

    public void Dispose() => NativeProcess.Dispose();
}

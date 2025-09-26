using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

namespace KaliMCP.Tools;

[McpServerToolType]
public static class KaliLinuxToolset
{
    private const string DefaultImage = "kalilinux/kali-rolling";
    private const string DefaultContainerName = "kali-mcp-persistent";
    private static readonly object _lockObject = new object();
    private static readonly SemaphoreSlim _containerSemaphore = new SemaphoreSlim(1, 1);

    [McpServerTool(Name = "kali-exec"), Description("Runs a shell command inside the persistent Kali Linux Docker container and returns the captured output.")]
    public static async Task<string> RunCommandAsync(
        [Description("The shell command to execute inside the container. The command is passed to bash -lc.")] string command,
        [Description("The Docker image to use. Defaults to kalilinux/kali-rolling.")] string? image,
        [Description("The container name to use. Defaults to kali-mcp-persistent.")] string? containerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command must not be empty.", nameof(command));
        }

        string dockerImage = string.IsNullOrWhiteSpace(image) ? DefaultImage : image;
        string containerNameToUse = string.IsNullOrWhiteSpace(containerName) ? DefaultContainerName : containerName;

        // Ensure the container is running
        await EnsureContainerRunningAsync(dockerImage, containerNameToUse, cancellationToken);

        // Execute the command in the existing container
        return await ExecuteInContainerAsync(containerNameToUse, command, cancellationToken);
    }

    [McpServerTool(Name = "kali-container-status"), Description("Check the status of the persistent Kali Linux container.")]
    public static async Task<string> GetContainerStatusAsync(
        [Description("The container name to check. Defaults to kali-mcp-persistent.")] string? containerName,
        CancellationToken cancellationToken)
    {
        string containerNameToUse = string.IsNullOrWhiteSpace(containerName) ? DefaultContainerName : containerName;
        
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        
        psi.ArgumentList.Add("ps");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"name={containerNameToUse}");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("table {{.Names}}\t{{.Status}}\t{{.Image}}");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Docker process.");
        }

        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return $"Container Status:\n{output}";
    }

    [McpServerTool(Name = "kali-container-restart"), Description("Restart the persistent Kali Linux container (useful if it becomes unresponsive).")]
    public static async Task<string> RestartContainerAsync(
        [Description("The Docker image to use. Defaults to kalilinux/kali-rolling.")] string? image,
        [Description("The container name to restart. Defaults to kali-mcp-persistent.")] string? containerName,
        CancellationToken cancellationToken)
    {
        string dockerImage = string.IsNullOrWhiteSpace(image) ? DefaultImage : image;
        string containerNameToUse = string.IsNullOrWhiteSpace(containerName) ? DefaultContainerName : containerName;

        lock (_lockObject)
        {
            // Stop and remove existing container
            Task.Run(async () => await StopAndRemoveContainerAsync(containerNameToUse, CancellationToken.None)).Wait();
        }

        // Start a new one
        await EnsureContainerRunningAsync(dockerImage, containerNameToUse, cancellationToken);
        
        return $"Container '{containerNameToUse}' has been restarted successfully.";
    }

    [McpServerTool(Name = "kali-container-stop"), Description("Stop the persistent Kali Linux container to free up system resources.")]
    public static async Task<string> StopContainerAsync(
        [Description("The container name to stop. Defaults to kali-mcp-persistent.")] string? containerName,
        [Description("Whether to also remove the container after stopping. If false, the container can be restarted later. Defaults to false.")] bool removeContainer = false,
        CancellationToken cancellationToken = default)
    {
        string containerNameToUse = string.IsNullOrWhiteSpace(containerName) ? DefaultContainerName : containerName;

        // Check if container exists first
        if (!ContainerExists(containerNameToUse))
        {
            return $"Container '{containerNameToUse}' does not exist.";
        }

        // Check if container is running
        bool wasRunning = IsContainerRunning(containerNameToUse);
        
        if (!wasRunning)
        {
            if (removeContainer)
            {
                await RemoveContainerAsync(containerNameToUse, cancellationToken);
                return $"Container '{containerNameToUse}' was already stopped and has been removed.";
            }
            return $"Container '{containerNameToUse}' is already stopped.";
        }

        // Stop the container
        var stopPsi = new ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        stopPsi.ArgumentList.Add("stop");
        stopPsi.ArgumentList.Add(containerNameToUse);

        using var stopProcess = new Process { StartInfo = stopPsi };
        if (!stopProcess.Start())
        {
            throw new InvalidOperationException("Failed to start the Docker process.");
        }

        await stopProcess.WaitForExitAsync(cancellationToken);
        
        if (stopProcess.ExitCode != 0)
        {
            string error = await stopProcess.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to stop container: {error}");
        }

        // Optionally remove the container
        if (removeContainer)
        {
            await RemoveContainerAsync(containerNameToUse, cancellationToken);
            return $"Container '{containerNameToUse}' has been stopped and removed successfully.";
        }

        return $"Container '{containerNameToUse}' has been stopped successfully. Use kali-exec to restart it automatically, or use kali-container-restart for manual restart.";
    }

    private static async Task EnsureContainerRunningAsync(string dockerImage, string containerName, CancellationToken cancellationToken)
    {
        await _containerSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Check if container exists and is running
            if (IsContainerRunning(containerName))
            {
                return;
            }

            // Check if container exists but is stopped
            if (ContainerExists(containerName))
            {
                // Start the existing container
                await StartContainerAsync(containerName, cancellationToken);
                return;
            }

            // Create and start a new container
            await CreateAndStartContainerAsync(dockerImage, containerName, cancellationToken);
        }
        finally
        {
            _containerSemaphore.Release();
        }
    }

    private static bool IsContainerRunning(string containerName)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        
        psi.ArgumentList.Add("ps");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"name={containerName}");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("status=running");
        psi.ArgumentList.Add("--quiet");

        using var process = Process.Start(psi);
        if (process == null) return false;
        
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        
        return !string.IsNullOrWhiteSpace(output);
    }

    private static bool ContainerExists(string containerName)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        
        psi.ArgumentList.Add("ps");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add($"name={containerName}");
        psi.ArgumentList.Add("--quiet");

        using var process = Process.Start(psi);
        if (process == null) return false;
        
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        
        return !string.IsNullOrWhiteSpace(output);
    }

    private static async Task CreateAndStartContainerAsync(string dockerImage, string containerName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-d");  // Detached mode
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(containerName);
        psi.ArgumentList.Add("--workdir");
        psi.ArgumentList.Add("/root");
        psi.ArgumentList.Add(dockerImage);
        psi.ArgumentList.Add("sleep");
        psi.ArgumentList.Add("infinity");  // Keep container running

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Docker process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        
        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to create container: {error}");
        }
    }

    private static async Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add(containerName);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Docker process.");
        }

        await process.WaitForExitAsync(cancellationToken);
        
        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to start container: {error}");
        }
    }

    private static async Task StopAndRemoveContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        // Stop the container
        var stopPsi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        stopPsi.ArgumentList.Add("stop");
        stopPsi.ArgumentList.Add(containerName);

        using (var stopProcess = Process.Start(stopPsi))
        {
            if (stopProcess != null)
            {
                await stopProcess.WaitForExitAsync(cancellationToken);
            }
        }

        // Remove the container
        await RemoveContainerAsync(containerName, cancellationToken);
    }

    private static async Task RemoveContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var rmPsi = new ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        rmPsi.ArgumentList.Add("rm");
        rmPsi.ArgumentList.Add(containerName);

        using var rmProcess = new Process { StartInfo = rmPsi };
        if (!rmProcess.Start())
        {
            throw new InvalidOperationException("Failed to start the Docker process.");
        }

        await rmProcess.WaitForExitAsync(cancellationToken);
        
        if (rmProcess.ExitCode != 0)
        {
            string error = await rmProcess.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to remove container: {error}");
        }
    }

    private static async Task<string> ExecuteInContainerAsync(string containerName, string command, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(containerName);
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start the Docker process.");
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            var builder = new StringBuilder();
            builder.AppendLine($"Container: {containerName}");
            builder.AppendLine($"Command: {command}");
            builder.AppendLine($"ExitCode: {process.ExitCode}");

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.AppendLine("Stdout:");
                builder.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                builder.AppendLine("Stderr:");
                builder.AppendLine(stderr.TrimEnd());
            }

            return builder.ToString();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("The 'docker' executable was not found. Make sure Docker is installed and available in PATH.", ex);
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Ignore secondary errors when attempting to cancel the process.
                }
            }

            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
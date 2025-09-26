# dotnet-docker-kali-mcp-server
VS Code client runs commands in Kali docker image using "kali-exec" MCP server

### Pull kali Docker image  
```bash
docker pull docker.io/kalilinux/kali-rolling
```

### Create project
```bash
mkdir <name of folder that contains project>
git init
dotnet new console -n <name of project>
mkdir -p .vscode && touch .vscode/mcp.json
```

### Modify mcp.json  
mcp.json  
```json
{
    "servers": {
        "kali-mcp-server": {
            "command": "docker",
            "args": [
                "run",
                "--rm",
                "-i",
                "--user",
                "root",
                "-v",
                "/var/run/docker.sock:/var/run/docker.sock",
                "kali-mcp-server"
            ],
            "type": "stdio"
        },
        "MCP_DOCKER": {
            "command": "docker",
            "args": [
                "mcp",
                "gateway",
                "run"
            ],
            "type": "stdio"
        }
    }
}   
```

### Install dotnet MCP libraries
```bash
cd <project name>
dotnet add package ModelContextProtocol --version 0.3.0-preview.4
dotnet add package Microsoft.Extensions.Hosting
```

### Modify Program.cs
program.cs
```c#
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Add the MCP services: the transport to use (stdio) and the tools to register.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

### Create KaliLinuxToolset.cs
```bash
mkdir -p Tools
touch ./Tools/KaliLinuxToolset.cs
```

### Modify KaliLinuxToolset.cs
```c#
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

namespace KaliMCP.Tools;

[McpServerToolType]
public static class KaliLinuxToolset
{
    private const string DefaultImage = "kalilinux/kali-rolling";

    [McpServerTool(Name = "kali-exec"), Description("Runs a shell command inside the Kali Linux Docker image and returns the captured output.")]
    public static async Task<string> RunCommandAsync(
        [Description("The shell command to execute inside the container. The command is passed to bash -lc.")] string command,
        [Description("The Docker image to use. Defaults to kalilinux/kali-rolling.")] string? image,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command must not be empty.", nameof(command));
        }

        string dockerImage = string.IsNullOrWhiteSpace(image) ? DefaultImage : image;

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--rm");
        psi.ArgumentList.Add(dockerImage);
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
            builder.AppendLine($"Image: {dockerImage}");
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
```

### Initialize Docker
```bash
docker init
```
Accept defaults for all choices  

### Project Structure  
```
.vscode
│   └── mcp.json
├── Program.cs
├── DockerFile
├── <project name>.csproj
└── Tools
    └── KaliLinuxToolset.cs
```

### Modify DockerFile  
Install Docker CLI inside of the Docker container (inception)  

Replace these lines in DockerFile  
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app
```

with
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

ARG DOCKER_CLI_VERSION=27.1.1
WORKDIR /app

# Install Docker CLI
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl tar \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL "https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_CLI_VERSION}.tgz" -o docker.tgz \
    && tar -xzf docker.tgz --strip-components=1 -C /usr/local/bin docker/docker \
    && rm -rf docker docker.tgz
```

### Containerize dotnet app  
```bash
docker build -t kali-mcp-server .
```

### Connect VS Code client to MCP server  
```bash
docker mcp client connect vscode
```

### Reformat mcp.json   
Right click > Format Document  

### Start MCP server  
Click "Start" next to "kali-mcp-server" MCP server in mcp.json

### Start new CoPilot chat
```
Hi, copilot. Return the exact output of kali-exec ls
```

# Persistent Kali Docker container  

One may have notices the above code interacts with a non-persistent Kali docker container and installed tools are gone at next prompt.  

The following implimentation of `KaliLinuxToolset.cs` communicates with a persistent Kali Docker image, so installed tools persist through the chat session.  

KaliLinuxToolset.cs
```c#
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
        var rmPsi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        rmPsi.ArgumentList.Add("rm");
        rmPsi.ArgumentList.Add(containerName);

        using var rmProcess = Process.Start(rmPsi);
        if (rmProcess != null)
        {
            await rmProcess.WaitForExitAsync(cancellationToken);
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
```

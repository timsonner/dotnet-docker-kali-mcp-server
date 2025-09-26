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

### Initialize docker
```bash
docker init
```
Accept defaults for all choices

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

### Project Structure  
```
.vscode
│   └── mcp.json
├── Program.cs
├── README.md
├── <project name>.csproj
└── Tools
    └── KaliLinuxToolset.cs
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
Hi, copilot. Provide the output of kali-exec ls
```

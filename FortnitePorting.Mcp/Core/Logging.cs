using FortnitePorting.Mcp.Config;
using Serilog;
using Serilog.Events;

namespace FortnitePorting.Mcp.Core;

public static class Logging
{
    /// <summary>
    /// Configures Serilog to write to stderr and a rolling file. Nothing may ever reach
    /// stdout: that stream is reserved for the MCP stdio transport.
    /// </summary>
    public static void Initialize(McpConfig config, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        var logFile = Path.Combine(config.LogFolder.FullName, $"FortnitePorting.Mcp-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Console(
                standardErrorFromLevel: LogEventLevel.Verbose,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFile,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Log file: {Path}", logFile);
    }
}

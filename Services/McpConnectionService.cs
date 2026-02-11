using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using CodeMerger.Models;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages MCP server connection state via named pipes.
    /// Listens for handshake and activity messages from MCP server processes.
    /// </summary>
    public class McpConnectionService : IDisposable
    {
        private readonly string _handshakePipeName;
        private readonly string _activityPipeName;
        
        private CancellationTokenSource? _handshakeCts;
        private CancellationTokenSource? _activityCts;
        private bool _disposed;

        /// <summary>
        /// Fired when an MCP server connects (workspace name).
        /// </summary>
        public event Action<string>? OnConnected;

        /// <summary>
        /// Fired when an MCP server disconnects (workspace name).
        /// </summary>
        public event Action<string>? OnDisconnected;

        /// <summary>
        /// Fired when activity is received (workspace name, activity description).
        /// </summary>
        public event Action<string, string>? OnActivity;

        /// <summary>
        /// Fired when structured activity is received (workspace name, parsed activity message).
        /// Use this for new code that needs timing/state information.
        /// </summary>
        public event Action<string, McpActivityMessage>? OnActivityParsed;

        /// <summary>
        /// Fired when persistent errors occur (error message).
        /// </summary>
        public event Action<string>? OnError;

        public bool IsConnected { get; private set; }
        public string? ConnectedWorkspace { get; private set; }

        public McpConnectionService(string handshakePipeName, string activityPipeName)
        {
            _handshakePipeName = handshakePipeName;
            _activityPipeName = activityPipeName;
        }

        /// <summary>
        /// Start listening for MCP connections.
        /// </summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(McpConnectionService));

            // Flush any stale pipes left by dead processes
            FlushStalePipe(_handshakePipeName);
            FlushStalePipe(_activityPipeName);

            StartHandshakeListener();
            StartActivityListener();
        }

        /// <summary>
        /// Attempts to connect to a stale pipe server as a client to release it.
        /// If no server exists, this is a harmless no-op.
        /// </summary>
        private void FlushStalePipe(string pipeName)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(100); // Quick timeout
                // Connected to stale server - just disconnect, which releases it
            }
            catch
            {
                // No stale pipe or already gone - that's fine
            }
        }

        /// <summary>
        /// Stop listening and clean up resources.
        /// </summary>
        public void Stop()
        {
            _handshakeCts?.Cancel();
            _activityCts?.Cancel();
            
            IsConnected = false;
            ConnectedWorkspace = null;
        }

        /// <summary>
        /// Kill all MCP server processes (other instances of this executable).
        /// </summary>
        /// <returns>Number of processes killed.</returns>
        public int KillServerProcesses()
        {
            var currentProcessId = Process.GetCurrentProcess().Id;
            var currentProcessName = Process.GetCurrentProcess().ProcessName;

            var mcpProcesses = Process.GetProcessesByName(currentProcessName);
            int killed = 0;

            foreach (var process in mcpProcesses)
            {
                if (process.Id == currentProcessId) continue;

                try
                {
                    process.Kill();
                    process.WaitForExit(1000);
                    killed++;
                }
                catch
                {
                    // Process may have already exited
                }
            }

            if (killed > 0)
            {
                IsConnected = false;
                ConnectedWorkspace = null;
            }

            return killed;
        }

        private void StartHandshakeListener()
        {
            _handshakeCts = new CancellationTokenSource();
            var token = _handshakeCts.Token;

            Task.Run(async () =>
            {
                int consecutiveErrors = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(
                            _handshakePipeName, 
                            PipeDirection.In, 
                            NamedPipeServerStream.MaxAllowedServerInstances, 
                            PipeTransmissionMode.Byte, 
                            PipeOptions.Asynchronous);
                        
                        await pipe.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(pipe);
                        string? message = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(message))
                        {
                            IsConnected = true;
                            ConnectedWorkspace = message;
                            OnConnected?.Invoke(message);
                        }
                        
                        consecutiveErrors = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        Debug.WriteLine($"[Handshake Listener] Error #{consecutiveErrors}: {ex.Message}");

                        if (consecutiveErrors == 5)
                        {
                            OnError?.Invoke($"Handshake listener error: {ex.Message}");
                        }

                        var delay = Math.Min(500 * consecutiveErrors, 5000);
                        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
                    }
                }
            }, token);
        }

        private void StartActivityListener()
        {
            _activityCts = new CancellationTokenSource();
            var token = _activityCts.Token;

            Task.Run(async () =>
            {
                int consecutiveErrors = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(
                            _activityPipeName, 
                            PipeDirection.In, 
                            NamedPipeServerStream.MaxAllowedServerInstances, 
                            PipeTransmissionMode.Byte, 
                            PipeOptions.Asynchronous);
                        
                        await pipe.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(pipe);
                        string? message = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(message))
                        {
                            ProcessActivityMessage(message);
                        }
                        
                        consecutiveErrors = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        Debug.WriteLine($"[Activity Listener] Error #{consecutiveErrors}: {ex.Message}");

                        if (consecutiveErrors == 5)
                        {
                            OnError?.Invoke($"Activity listener error: {ex.Message}");
                        }

                        var delay = Math.Min(100 * consecutiveErrors, 5000);
                        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
                    }
                }
            }, token);
        }

        private void ProcessActivityMessage(string message)
        {
            var parts = message.Split('|', 2);
            if (parts.Length != 2)
            {
                // Malformed message, treat as activity with unknown workspace
                OnActivity?.Invoke("", message);
                return;
            }

            var workspaceName = parts[0];
            var activity = parts[1];

            if (activity == "DISCONNECT")
            {
                IsConnected = false;
                ConnectedWorkspace = null;
                OnDisconnected?.Invoke(workspaceName);
            }
            else
            {
                // Any activity means we're connected
                IsConnected = true;
                ConnectedWorkspace = workspaceName;
                
                // Fire legacy event with raw activity string
                OnActivity?.Invoke(workspaceName, activity);
                
                // Try to parse structured activity message
                var activityMsg = McpActivityMessage.Parse(activity);
                if (activityMsg != null)
                {
                    OnActivityParsed?.Invoke(workspaceName, activityMsg);
                }
            }
        }

        public void SendCommand(string command)
        {
            Task.Run(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", McpServer.CommandPipeName, PipeDirection.Out);
                    pipe.Connect(500);
                    using var writer = new StreamWriter(pipe);
                    writer.WriteLine(command);
                    writer.Flush();
                }
                catch { }
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            _handshakeCts?.Dispose();
            _activityCts?.Dispose();
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeMerger.Services
{
    /// <summary>
    /// Streamable HTTP transport for MCP protocol (modern standard).
    /// Single /mcp endpoint handles all MCP requests via POST.
    /// 
    /// ChatGPT and other clients POST JSON-RPC requests and receive JSON responses.
    /// This replaces the legacy SSE transport which required two endpoints.
    /// </summary>
    public class McpHttpTransport : IDisposable
    {
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;
        private bool _disposed;

        private readonly Func<string, string?> _messageHandler;

        public bool IsRunning => _listener?.IsListening ?? false;
        public int Port => _port;

        public event Action<string>? OnLog;
        public event Action<string>? OnClientConnected;
        public event Action<string>? OnClientDisconnected;
        public event Action<string>? OnMessageReceived;

        public McpHttpTransport(int port, Func<string, string?> messageHandler)
        {
            _port = port;
            _messageHandler = messageHandler;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(McpHttpTransport));
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();

            // Listen on all interfaces for tunnel compatibility
            _listener.Prefixes.Add($"http://+:{_port}/");

            try
            {
                _listener.Start();
                Log($"MCP HTTP transport started on http://localhost:{_port}/mcp");
                _listenerTask = Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // Access denied
            {
                // Try localhost only (doesn't require admin)
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");

                _listener.Start();
                Log($"MCP HTTP transport started on http://localhost:{_port}/mcp (localhost only)");
                _listenerTask = Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }

            _listener = null;
            Log("MCP HTTP transport stopped");
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener?.IsListening == true)
            {
                try
                {
                    var context = await _listener.GetContextAsync().WaitAsync(token);
                    _ = Task.Run(() => HandleRequest(context, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Listener error: {ex.Message}");
                    await Task.Delay(100, token);
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext context, CancellationToken token)
        {
            var request = context.Request;
            var response = context.Response;

            // CORS headers for ChatGPT compatibility
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, Accept, Mcp-Session-Id");
            response.Headers.Add("Access-Control-Expose-Headers", "Content-Type, Mcp-Session-Id");
            response.Headers.Add("Access-Control-Max-Age", "86400");

            try
            {
                var path = request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

                Log($"{request.HttpMethod} {path} (Host: {request.Headers["Host"]})");

                // Handle CORS preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                // Route requests
                switch (path)
                {
                    case "/mcp":
                    case "/mcp/":
                        if (request.HttpMethod == "POST")
                            await HandleMcpPost(context);
                        else if (request.HttpMethod == "GET")
                            await HandleMcpGet(context, token);
                        else if (request.HttpMethod == "DELETE")
                            await HandleMcpDelete(context);
                        else
                        {
                            response.StatusCode = 405;
                            response.Close();
                        }
                        break;

                    case "":
                    case "/":
                        await HandleHealthCheck(context);
                        break;

                    // Legacy SSE endpoints - redirect to /mcp
                    case "/sse":
                    case "/sse/":
                        Log("Legacy /sse endpoint called - redirecting to /mcp");
                        if (request.HttpMethod == "GET")
                            await HandleMcpGet(context, token);
                        else
                        {
                            response.StatusCode = 308; // Permanent redirect
                            response.Headers.Add("Location", "/mcp");
                            response.Close();
                        }
                        break;

                    case "/messages":
                    case "/messages/":
                        Log("Legacy /messages endpoint called - handling as /mcp POST");
                        if (request.HttpMethod == "POST")
                            await HandleMcpPost(context);
                        else
                        {
                            response.StatusCode = 308;
                            response.Headers.Add("Location", "/mcp");
                            response.Close();
                        }
                        break;

                    default:
                        Log($"404 Not Found: {path}");
                        response.StatusCode = 404;
                        await WriteJson(response, new { error = "Not Found", path });
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Request error: {ex.Message}");
                try
                {
                    response.StatusCode = 500;
                    response.Close();
                }
                catch { }
            }
        }

        /// <summary>
        /// Handle POST /mcp - the main MCP request handler.
        /// Client POSTs JSON-RPC requests, server responds with JSON.
        /// </summary>
        private async Task HandleMcpPost(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            // Read request body
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }

            // Extract method for logging
            string methodName = "unknown";
            int? requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("method", out var methodProp))
                    methodName = methodProp.GetString() ?? "unknown";
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    requestId = idProp.GetInt32();
            }
            catch { }

            Log($">>> {methodName} (id={requestId})");
            OnMessageReceived?.Invoke(methodName);

            if (methodName == "initialize")
                OnClientConnected?.Invoke("http-client");

            // Process with shared handler
            var result = _messageHandler(body);

            if (result != null)
            {
                Log($"<<< Response: {result.Substring(0, Math.Min(150, result.Length))}...");
                response.ContentType = "application/json";
                response.StatusCode = 200;
                await WriteResponse(response, result);
            }
            else
            {
                // Notification - no response needed
                Log($"<<< (no response for notification)");
                response.StatusCode = 202;
                response.Close();
            }
        }

        /// <summary>
        /// Handle GET /mcp - optional SSE stream for server-initiated messages.
        /// In Streamable HTTP, this is optional. We support it for compatibility.
        /// </summary>
        private async Task HandleMcpGet(HttpListenerContext context, CancellationToken token)
        {
            var response = context.Response;

            // Check if client wants SSE
            var accept = context.Request.Headers["Accept"] ?? "";
            if (accept.Contains("text/event-stream"))
            {
                Log("Client requested SSE stream");

                response.StatusCode = 200;
                response.ContentType = "text/event-stream; charset=utf-8";
                response.Headers.Add("Cache-Control", "no-cache, no-store");
                response.Headers.Add("Connection", "keep-alive");
                response.Headers.Add("X-Accel-Buffering", "no");
                response.SendChunked = true;

                OnClientConnected?.Invoke("sse-client");

                try
                {
                    // Send initial comment to confirm connection
                    var initMsg = Encoding.UTF8.GetBytes(": connected\n\n");
                    await response.OutputStream.WriteAsync(initMsg, 0, initMsg.Length);
                    await response.OutputStream.FlushAsync();

                    // Keep alive with heartbeats
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(15000, token);
                        var heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                        await response.OutputStream.WriteAsync(heartbeat, 0, heartbeat.Length);
                        await response.OutputStream.FlushAsync();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log($"SSE stream error: {ex.Message}");
                }
                finally
                {
                    OnClientDisconnected?.Invoke("sse-client");
                    response.Close();
                }
            }
            else
            {
                // Return server info as JSON
                await HandleHealthCheck(context);
            }
        }

        /// <summary>
        /// Handle DELETE /mcp - session termination (optional in stateless mode).
        /// </summary>
        private async Task HandleMcpDelete(HttpListenerContext context)
        {
            var response = context.Response;
            Log("Session termination requested");
            OnClientDisconnected?.Invoke("http-client");
            response.StatusCode = 204;
            response.Close();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Health check endpoint - returns server info.
        /// </summary>
        private async Task HandleHealthCheck(HttpListenerContext context)
        {
            var response = context.Response;
            response.StatusCode = 200;

            var info = new
            {
                server = "CodeMerger MCP Server",
                version = "2.0.0",
                protocolVersion = "2024-11-05",
                transport = "streamable-http",
                endpoints = new
                {
                    mcp = "/mcp"
                },
                status = "ready"
            };

            await WriteJson(response, info);
        }

        private async Task WriteJson(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(data);
            await WriteResponse(response, json);
        }

        private async Task WriteResponse(HttpListenerResponse response, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[MCP] {message}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }
    }
}

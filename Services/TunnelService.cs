using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages tunneling services (cloudflared) to expose local SSE server to the internet.
    /// Required for ChatGPT Desktop to connect to local CodeMerger instance.
    /// </summary>
    public class TunnelService : IDisposable
    {
        private Process? _tunnelProcess;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private static readonly HttpClient _httpClient = new HttpClient();

        // Cloudflared download URL for Windows 64-bit
        private const string CloudflaredDownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
        
        public string? PublicUrl { get; private set; }
        public bool IsRunning => _tunnelProcess != null && !_tunnelProcess.HasExited;
        public TunnelProvider Provider { get; private set; }

        public event Action<string>? OnLog;
        public event Action<string>? OnUrlAvailable;
        public event Action? OnDisconnected;
        public event Action<string>? OnError;
        public event Action<int>? OnDownloadProgress;

        public enum TunnelProvider
        {
            None,
            Cloudflared
        }

        /// <summary>
        /// Get the path where cloudflared should be installed.
        /// </summary>
        public static string GetCloudflaredInstallPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "CodeMerger", "cloudflared.exe");
        }

        /// <summary>
        /// Check if cloudflared is installed (either in PATH or our install location).
        /// </summary>
        public static bool IsCloudflaredInstalled()
        {
            return FindExecutable("cloudflared") != null;
        }

        /// <summary>
        /// Download and install cloudflared automatically.
        /// </summary>
        public async Task<bool> InstallCloudflaredAsync()
        {
            var installPath = GetCloudflaredInstallPath();
            var installDir = Path.GetDirectoryName(installPath)!;

            try
            {
                // Create directory if needed
                if (!Directory.Exists(installDir))
                {
                    Directory.CreateDirectory(installDir);
                }

                Log("Downloading cloudflared...");
                OnDownloadProgress?.Invoke(0);

                // Download the executable
                using var response = await _httpClient.GetAsync(CloudflaredDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var downloadedBytes = 0L;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(installPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var progress = (int)((downloadedBytes * 100) / totalBytes);
                        OnDownloadProgress?.Invoke(progress);
                    }
                }

                OnDownloadProgress?.Invoke(100);
                Log($"Cloudflared installed to: {installPath}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to install cloudflared: {ex.Message}");
                OnError?.Invoke($"Failed to install cloudflared: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start a tunnel to expose the local port to the internet.
        /// Auto-installs cloudflared if not present.
        /// </summary>
        public async Task<bool> StartAsync(int localPort, bool autoInstall = true)
        {
            if (IsRunning)
            {
                Stop();
            }

            _cts = new CancellationTokenSource();

            // Check if cloudflared is available
            var cloudflaredPath = FindExecutable("cloudflared");
            
            if (cloudflaredPath == null && autoInstall)
            {
                Log("Cloudflared not found. Installing automatically...");
                if (!await InstallCloudflaredAsync())
                {
                    return false;
                }
                cloudflaredPath = GetCloudflaredInstallPath();
            }

            if (cloudflaredPath == null)
            {
                OnError?.Invoke("Cloudflared not installed.");
                return false;
            }

            return await StartCloudflaredAsync(localPort, cloudflaredPath);
        }

        /// <summary>
        /// Start cloudflared tunnel (Cloudflare Quick Tunnel).
        /// </summary>
        private async Task<bool> StartCloudflaredAsync(int localPort, string cloudflaredPath)
        {
            Log($"Starting cloudflared tunnel on port {localPort}...");
            Provider = TunnelProvider.Cloudflared;

            try
            {
                _tunnelProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cloudflaredPath,
                        Arguments = $"tunnel --url http://localhost:{localPort} --http-host-header localhost",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                _tunnelProcess.Exited += (s, e) =>
                {
                    PublicUrl = null;
                    OnDisconnected?.Invoke();
                };

                _tunnelProcess.Start();

                // Parse output for public URL (cloudflared outputs to stderr)
                _ = Task.Run(() => MonitorCloudflaredOutput(_tunnelProcess, _cts!.Token));

                // Wait for URL to become available (max 15 seconds)
                for (int i = 0; i < 30 && PublicUrl == null && IsRunning; i++)
                {
                    await Task.Delay(500);
                }

                if (PublicUrl != null)
                {
                    Log($"cloudflared tunnel established: {PublicUrl}");
                    return true;
                }

                Log("cloudflared failed to establish tunnel");
                Stop();
                return false;
            }
            catch (Exception ex)
            {
                Log($"cloudflared error: {ex.Message}");
                OnError?.Invoke($"cloudflared error: {ex.Message}");
                return false;
            }
        }

        private async Task MonitorStreamForUrl(StreamReader reader, Regex urlRegex, string prefix, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;

                Log($"{prefix} {line}");

                var match = urlRegex.Match(line);
                if (match.Success && PublicUrl == null)
                {
                    PublicUrl = match.Groups[1].Value.TrimEnd('/', '"', '\'', '>', ']');
                    OnUrlAvailable?.Invoke(PublicUrl);
                }
            }
        }

        private async Task MonitorCloudflaredOutput(Process process, CancellationToken token)
        {
            // cloudflared URL pattern: https://xxxx-xxxx-xxxx.trycloudflare.com
            var urlRegex = new Regex(@"(https://[a-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase);

            try
            {
                // cloudflared outputs to stderr but also check stdout
                var stderrTask = MonitorStreamForUrl(process.StandardError, urlRegex, "[cloudflared]", token);
                var stdoutTask = MonitorStreamForUrl(process.StandardOutput, urlRegex, "[cloudflared-out]", token);
                
                await Task.WhenAny(stderrTask, stdoutTask);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"cloudflared monitor error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the tunnel.
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();

            if (_tunnelProcess != null && !_tunnelProcess.HasExited)
            {
                try
                {
                    _tunnelProcess.Kill();
                    _tunnelProcess.WaitForExit(2000);
                }
                catch { }
            }

            _tunnelProcess?.Dispose();
            _tunnelProcess = null;
            PublicUrl = null;
            Provider = TunnelProvider.None;

            Log("Tunnel stopped");
        }

        /// <summary>
        /// Get the full SSE URL for ChatGPT configuration.
        /// </summary>
        public string? GetSseUrl()
        {
            if (string.IsNullOrEmpty(PublicUrl))
                return null;

            // Use /mcp endpoint (Streamable HTTP transport)
            var url = PublicUrl.TrimEnd('/');
            return $"{url}/mcp";
        }

        /// <summary>
        /// Check if cloudflared is installed and available.
        /// </summary>
        public static bool IsCloudflaredAvailable() => FindExecutable("cloudflared") != null;

        /// <summary>
        /// Get instructions for ChatGPT Desktop setup.
        /// </summary>
        public static string GetInstallInstructions()
        {
            return @"To connect ChatGPT Desktop to CodeMerger:

1. Click 'Start ChatGPT Server' in CodeMerger
   - Cloudflared will be installed automatically if needed
   - A public URL will be generated

2. Copy the URL (it will be copied to clipboard automatically)

3. In ChatGPT Desktop:
   - Go to Settings → Apps → Create app
   - Enable 'Developer Mode' (required for MCP)
   - Paste the URL as 'MCP Server URL'
   - Authentication: None

Note: The tunnel uses Cloudflare's free Quick Tunnel service.
No account or configuration required.";
        }

        private static string? FindExecutable(string name)
        {
            // First check our install location
            var ourInstallPath = GetCloudflaredInstallPath();
            if (File.Exists(ourInstallPath))
                return ourInstallPath;

            // Check PATH
            var extensions = new[] { ".exe", "" };
            var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator);

            foreach (var path in paths)
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(path, name + ext);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            // Check common install locations
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var commonPaths = new[]
            {
                Path.Combine(userProfile, "cloudflared.exe"),
                Path.Combine(userProfile, "Downloads", "cloudflared.exe"),
                Path.Combine(userProfile, "Downloads", "cloudflared-windows-amd64.exe"),
                @"C:\Program Files\cloudflared\cloudflared.exe",
                @"C:\cloudflared\cloudflared.exe",
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[Tunnel] {message}");
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

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodeMerger.Services
{
    /// <summary>
    /// Handles GitHub Device Flow OAuth for desktop apps.
    /// No client secret needed — user opens browser, enters a code, done.
    /// Register an OAuth App at https://github.com/settings/developers to get a Client ID.
    /// </summary>
    public class GitHubDeviceFlowService
    {
        // TODO: Replace with your actual OAuth App Client ID after registering at https://github.com/settings/developers
        public static readonly string ClientId = "REPLACE_WITH_YOUR_CLIENT_ID";

        private static readonly HttpClient Http = new();

        /// <summary>
        /// Step 1: Request a device code and user code from GitHub.
        /// Returns the user_code (to show user) and verification_uri (to open in browser).
        /// </summary>
        public async Task<DeviceCodeResponse?> RequestDeviceCodeAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", "public_repo")
            });

            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<DeviceCodeResponse>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }

        /// <summary>
        /// Step 2: Poll GitHub until the user authorizes or timeout.
        /// Returns the access token on success, null on failure/timeout.
        /// </summary>
        public async Task<string?> PollForTokenAsync(DeviceCodeResponse deviceCode, CancellationToken ct = default)
        {
            var interval = Math.Max(deviceCode.Interval, 5);
            var expires = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);

            while (DateTime.UtcNow < expires && !ct.IsCancellationRequested)
            {
                await Task.Delay(interval * 1000, ct);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", ClientId),
                    new KeyValuePair<string, string>("device_code", deviceCode.DeviceCode),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                });

                var response = await Http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("access_token", out var tokenEl))
                    return tokenEl.GetString();

                if (doc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    var error = errorEl.GetString();
                    if (error == "authorization_pending")
                        continue;
                    if (error == "slow_down")
                    {
                        interval += 5;
                        continue;
                    }
                    // expired_token, access_denied, etc.
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the authenticated user's GitHub username using the token.
        /// </summary>
        public async Task<string?> GetUsernameAsync(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Add("Authorization", $"token {token}");
            request.Headers.Add("User-Agent", "CodeMerger");

            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("login", out var loginEl) ? loginEl.GetString() : null;
        }
    }

    public class DeviceCodeResponse
    {
        public string DeviceCode { get; set; } = "";
        public string UserCode { get; set; } = "";
        public string VerificationUri { get; set; } = "";
        public int ExpiresIn { get; set; }
        public int Interval { get; set; } = 5;
    }
}

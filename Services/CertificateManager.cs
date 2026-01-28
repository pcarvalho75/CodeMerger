using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages self-signed certificates for local HTTPS support.
    /// </summary>
    public static class CertificateManager
    {
        private const string CertSubject = "CN=CodeMerger Local";
        private const string CertFriendlyName = "CodeMerger Local HTTPS";

        public static event Action<string>? OnLog;

        /// <summary>
        /// Ensures HTTPS is configured for the given port.
        /// Creates certificate if needed, installs it, and binds to port.
        /// </summary>
        public static bool EnsureHttpsConfigured(int port)
        {
            try
            {
                var cert = GetOrCreateCertificate();
                if (cert == null)
                {
                    Log("Failed to get or create certificate");
                    return false;
                }

                Log($"Certificate ready: {cert.Thumbprint}");

                // Bind certificate to port
                if (!BindCertificateToPort(port, cert.Thumbprint))
                {
                    Log("Failed to bind certificate to port");
                    return false;
                }

                Log($"HTTPS configured on port {port}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"HTTPS setup error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets existing CodeMerger certificate or creates a new one.
        /// </summary>
        public static X509Certificate2? GetOrCreateCertificate()
        {
            // Check if we already have a valid certificate
            var existing = FindExistingCertificate();
            if (existing != null && existing.NotAfter > DateTime.Now.AddDays(30))
            {
                Log("Using existing certificate");
                return existing;
            }

            // Create new self-signed certificate
            Log("Creating new self-signed certificate...");
            return CreateSelfSignedCertificate();
        }

        private static X509Certificate2? FindExistingCertificate()
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);

                foreach (var cert in store.Certificates)
                {
                    if (cert.Subject == CertSubject && cert.NotAfter > DateTime.Now)
                    {
                        return cert;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error finding certificate: {ex.Message}");
            }

            return null;
        }

        private static X509Certificate2? CreateSelfSignedCertificate()
        {
            try
            {
                // Create certificate with RSA key
                using var rsa = RSA.Create(2048);

                var request = new CertificateRequest(
                    CertSubject,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                // Add extensions
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));

                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                        false));

                // Add Subject Alternative Names (SAN) - required for modern browsers
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName("localhost");
                sanBuilder.AddDnsName("127.0.0.1");
                sanBuilder.AddIpAddress(System.Net.IPAddress.Parse("127.0.0.1"));
                sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
                request.CertificateExtensions.Add(sanBuilder.Build());

                // Create certificate valid for 2 years
                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.Now.AddDays(-1),
                    DateTimeOffset.Now.AddYears(2));

                // Export and re-import with exportable private key
                var certWithKey = new X509Certificate2(
                    certificate.Export(X509ContentType.Pfx, ""),
                    "",
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

                // Install to current user store
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Add(certWithKey);
                store.Close();

                Log("Certificate created and installed to CurrentUser\\My store");

                // Also install to Trusted Root (requires elevation prompt for first time)
                try
                {
                    InstallToTrustedRoot(certWithKey);
                }
                catch (Exception ex)
                {
                    Log($"Note: Could not install to Trusted Root (browsers may show warning): {ex.Message}");
                }

                return certWithKey;
            }
            catch (Exception ex)
            {
                Log($"Error creating certificate: {ex.Message}");
                return null;
            }
        }

        private static void InstallToTrustedRoot(X509Certificate2 cert)
        {
            // Export just the public cert (no private key) for root store
            var publicCert = new X509Certificate2(cert.Export(X509ContentType.Cert));

            using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            rootStore.Open(OpenFlags.ReadWrite);

            // Check if already installed
            bool found = false;
            foreach (var existing in rootStore.Certificates)
            {
                if (existing.Thumbprint == cert.Thumbprint)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                rootStore.Add(publicCert);
                Log("Certificate installed to Trusted Root (browsers will trust it)");
            }

            rootStore.Close();
        }

        private static bool BindCertificateToPort(int port, string thumbprint)
        {
            try
            {
                // First, try to delete any existing binding
                var deleteProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"http delete sslcert ipport=0.0.0.0:{port}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                deleteProcess.Start();
                deleteProcess.WaitForExit(5000);

                // Add new binding
                // AppId is a random GUID that identifies our application
                var appId = "{2A3B4C5D-6E7F-8901-ABCD-EF0123456789}";

                var addProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"http add sslcert ipport=0.0.0.0:{port} certhash={thumbprint} appid={appId}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                addProcess.Start();
                var output = addProcess.StandardOutput.ReadToEnd();
                var error = addProcess.StandardError.ReadToEnd();
                addProcess.WaitForExit(5000);

                if (addProcess.ExitCode == 0)
                {
                    Log($"SSL certificate bound to port {port}");
                    return true;
                }

                // If netsh fails, it might need admin rights
                Log($"netsh output: {output} {error}");
                Log("Note: SSL binding may require running as Administrator once");

                // Try to check if binding already exists
                return CheckExistingBinding(port, thumbprint);
            }
            catch (Exception ex)
            {
                Log($"Error binding certificate: {ex.Message}");
                return false;
            }
        }

        private static bool CheckExistingBinding(int port, string thumbprint)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"http show sslcert ipport=0.0.0.0:{port}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                // Check if our thumbprint is bound
                return output.ToLowerInvariant().Contains(thumbprint.ToLowerInvariant());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Remove HTTPS binding from port.
        /// </summary>
        public static void RemoveBinding(int port)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"http delete sslcert ipport=0.0.0.0:{port}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit(5000);
            }
            catch { }
        }

        private static void Log(string message)
        {
            OnLog?.Invoke($"[Cert] {message}");
        }
    }
}

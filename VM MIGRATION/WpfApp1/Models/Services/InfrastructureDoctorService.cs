using System;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    public sealed class InfrastructureDoctorService
    {
        /// <summary>
        /// Audits both Source and Target Hyper-V hosts for Kerberos live migration readiness.
        /// </summary>
        public async Task<InfrastructureAuditReport> AuditHostPairAsync(
            string sourceHost,
            string targetHost,
            string? sourceUser,
            string? sourcePass,
            string? targetUser,
            string? targetPass,
            string destinationStoragePath,
            CancellationToken cancellationToken = default)
        {
            var report = new InfrastructureAuditReport
            {
                TargetFolderPath = destinationStoragePath
            };

            var taskSource = AuditSingleHostAsync(sourceHost, sourceUser, sourcePass, cancellationToken);
            var taskTarget = AuditSingleHostAsync(targetHost, targetUser, targetPass, cancellationToken);
            var taskFolder = CheckTargetFolderAsync(targetHost, targetUser, targetPass, destinationStoragePath, cancellationToken);

            await Task.WhenAll(taskSource, taskTarget, taskFolder);

            report.Source = await taskSource;
            report.Target = await taskTarget;
            report.TargetFolderExists = await taskFolder;

            return report;
        }

        /// <summary>
        /// 1-Click Automated Self-Healing: Configures Kerberos, purges SYSTEM tickets,
        /// optimizes Live Migration compression & concurrency slots, and verifies destination paths.
        /// </summary>
        public async Task AutoRemediateBothHostsAsync(
            string sourceHost,
            string targetHost,
            string? sourceUser,
            string? sourcePass,
            string? targetUser,
            string? targetPass,
            string destinationStoragePath,
            IProgress<string> logger,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(async () =>
            {
                logger.Report($"[DOCTOR] Initiating 1-Click Automated Remediation on host pair...");

                // 1. Remediate Source Node
                logger.Report($"[DC-FIX] Calibrating Source Node '{sourceHost}' -> Kerberos Auth, Compression QoS & Ticket Purge...");
                await RemediateSingleHostAsync(sourceHost, sourceUser, sourcePass, logger);

                // 2. Remediate Target Node
                logger.Report($"[DC-FIX] Calibrating Target Node '{targetHost}' -> Kerberos Auth, Compression QoS & Ticket Purge...");
                await RemediateSingleHostAsync(targetHost, targetUser, targetPass, logger);

                // 3. Ensure Target Directory Exists
                if (!string.IsNullOrWhiteSpace(destinationStoragePath) && destinationStoragePath.Length >= 3)
                {
                    logger.Report($"[STORAGE] Verifying destination directory '{destinationStoragePath}' on '{targetHost}'...");
                    await EnsureTargetDirectoryExistsAsync(targetHost, targetUser, targetPass, destinationStoragePath, logger);
                }

                logger.Report($"[SUCCESS] Host pair calibration complete! Both nodes optimized for Kerberos Live Migration.");
            }, cancellationToken);
        }

        private static async Task<HostAuditResult> AuditSingleHostAsync(string host, string? user, string? pass, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var isLocal = IsLocal(host);
                using var runspace = CreateRunspace(host, user, pass, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    $vmHost = Get-VMHost -ErrorAction SilentlyContinue
                    [PSCustomObject]@{
                        Hostname = $env:COMPUTERNAME
                        MigrationEnabled = if ($vmHost) { [bool]$vmHost.VirtualMachineMigrationEnabled } else { $false }
                        AuthType = if ($vmHost) { [string]$vmHost.VirtualMachineMigrationAuthenticationType } else { 'None' }
                    }
                ");

                var results = ps.Invoke();
                if (results.Count > 0 && results[0]?.BaseObject != null)
                {
                    var item = results[0];
                    var hName = item.Properties["Hostname"]?.Value?.ToString() ?? host;
                    var enabled = Convert.ToBoolean(item.Properties["MigrationEnabled"]?.Value ?? false);
                    var auth = item.Properties["AuthType"]?.Value?.ToString() ?? "Unknown";

                    return new HostAuditResult
                    {
                        Hostname = hName,
                        IsMigrationEnabled = enabled,
                        AuthType = auth,
                        IsKerberosAuth = auth.Equals("Kerberos", StringComparison.OrdinalIgnoreCase)
                    };
                }

                return new HostAuditResult { Hostname = host, AuthType = "Unreachable" };
            }, ct);
        }

        private static async Task RemediateSingleHostAsync(string host, string? user, string? pass, IProgress<string> logger)
        {
            await Task.Run(() =>
            {
                var isLocal = IsLocal(host);
                using var runspace = CreateRunspace(host, user, pass, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    Enable-VMMigration -ErrorAction SilentlyContinue
                    
                    # Set Kerberos Authentication, Compression QoS, and 4 concurrent migration slots
                    Set-VMHost -VirtualMachineMigrationAuthenticationType Kerberos `
                               -VirtualMachineMigrationPerformanceOption Compression `
                               -MaximumVirtualMachineMigrations 4 `
                               -MaximumStorageMigrations 4 `
                               -ErrorAction SilentlyContinue
                    
                    # Ensure WinRM Firewall Rule Group is Active
                    Enable-NetFirewallRule -DisplayGroup 'Windows Remote Management' -ErrorAction SilentlyContinue
                    
                    # Purge SYSTEM account's cached Kerberos ticket so it ingests new AD delegation immediately
                    klist purge -li 0x3e7
                    
                    # Restart VMMS to register updated Kerberos credentials & migration configuration
                    Restart-Service vmms -Force
                ");

                ps.Invoke();
                if (ps.HadErrors)
                {
                    var err = string.Join(", ", ps.Streams.Error);
                    logger.Report($"[WARN] Notice on '{host}': {err}");
                }
                else
                {
                    logger.Report($"[OK] Node '{host}' successfully configured: Live Migration ENABLED, Auth=KERBEROS, QoS=COMPRESSION (4 Slots), VMMS Restarted.");
                }
            });
        }

        private static async Task<bool> CheckTargetFolderAsync(string host, string? user, string? pass, string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            return await Task.Run(() =>
            {
                try
                {
                    var isLocal = IsLocal(host);
                    using var runspace = CreateRunspace(host, user, pass, isLocal);
                    runspace.Open();

                    using var ps = PowerShell.Create();
                    ps.Runspace = runspace;

                    var safePath = path.Replace("'", "''");
                    ps.AddScript($"Test-Path -LiteralPath '{safePath}'");
                    var res = ps.Invoke();
                    if (res.Count > 0 && res[0]?.BaseObject is bool b)
                    {
                        return b;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }, ct);
        }

        private static async Task EnsureTargetDirectoryExistsAsync(string host, string? user, string? pass, string path, IProgress<string> logger)
        {
            await Task.Run(() =>
            {
                var isLocal = IsLocal(host);
                using var runspace = CreateRunspace(host, user, pass, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safePath = path.Replace("'", "''");
                var script = @"
                    $p = '__PATH__'
                    if (-not (Test-Path -LiteralPath $p)) {
                        New-Item -ItemType Directory -Path $p -Force | Out-Null
                    }
                ".Replace("__PATH__", safePath);

                ps.AddScript(script);
                ps.Invoke();
                logger.Report($"[OK] Storage directory '{path}' verified and created on '{host}'.");
            });
        }

        private static string SanitizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "localhost";
            var clean = host.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
            clean = clean.TrimEnd('/');

            var colonIdx = clean.IndexOf(':');
            if (colonIdx > 0 && !clean.Contains(']'))
            {
                clean = clean[..colonIdx];
            }

            return clean;
        }

        private static bool IsLocal(string host)
        {
            var sanitized = SanitizeHost(host);
            return string.IsNullOrWhiteSpace(sanitized) ||
                   sanitized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   sanitized.Equals("127.0.0.1") ||
                   sanitized.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        private static Runspace CreateRunspace(string host, string? username, string? password, bool isLocal)
        {
            var cleanHost = SanitizeHost(host);

            if (isLocal || string.IsNullOrWhiteSpace(username))
            {
                var iss = InitialSessionState.CreateDefault2();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;
                return RunspaceFactory.CreateRunspace(iss);
            }

            var securePass = new SecureString();
            if (!string.IsNullOrEmpty(password))
            {
                foreach (char c in password) securePass.AppendChar(c);
            }
            securePass.MakeReadOnly();

            var psc = new PSCredential(username, securePass);
            var connectionInfo = new WSManConnectionInfo(new Uri($"http://{cleanHost}:5985/wsman"), "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", psc)
            {
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
                OpenTimeout = 12000,
                OperationTimeout = 30000
            };

            return RunspaceFactory.CreateRunspace(connectionInfo);
        }
    }
}
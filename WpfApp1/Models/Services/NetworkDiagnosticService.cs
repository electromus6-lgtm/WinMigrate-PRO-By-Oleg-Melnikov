using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Fast agentless network diagnostic service verifying ICMP latency and 
    /// parallel TCP socket connectivity across WinRM (5985/5986) and SMB (445).
    /// </summary>
    public static class NetworkDiagnosticService
    {
        public static async Task<NetworkDiagnosticResult> TestHostConnectivityAsync(string host, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return new NetworkDiagnosticResult { Host = host, IsReachable = false };
            }

            var target = SanitizeHost(host);

            if (target.Equals("localhost", StringComparison.OrdinalIgnoreCase) || target.Equals("127.0.0.1"))
            {
                return new NetworkDiagnosticResult
                {
                    Host = target,
                    IsReachable = true,
                    LatencyMs = 0,
                    IsWinRmOpen = true,
                    IsWinRmSslOpen = true,
                    IsSmbOpen = true
                };
            }

            return await Task.Run(async () =>
            {
                var isReachable = false;
                long latency = -1;

                // 1. ICMP Ping verification
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(target, 1000);
                    if (reply.Status == IPStatus.Success)
                    {
                        isReachable = true;
                        latency = reply.RoundtripTime;
                    }
                }
                catch
                {
                    // ICMP is frequently blocked by corporate firewalls; proceed to TCP handshakes
                }

                // 2. Parallel TCP Handshake checks with explicit CancellationToken timeouts
                var taskWinRm = CheckPortOpenAsync(target, 5985, 1200);
                var taskWinRmSsl = CheckPortOpenAsync(target, 5986, 1200);
                var taskSmb = CheckPortOpenAsync(target, 445, 1200);

                var winRmOpen = await taskWinRm;
                var winRmSslOpen = await taskWinRmSsl;
                var smbOpen = await taskSmb;

                if (winRmOpen || winRmSslOpen || smbOpen)
                {
                    isReachable = true;
                    if (latency < 0) latency = 1;
                }

                return new NetworkDiagnosticResult
                {
                    Host = target,
                    IsReachable = isReachable,
                    LatencyMs = Math.Max(0, latency),
                    IsWinRmOpen = winRmOpen,
                    IsWinRmSslOpen = winRmSslOpen,
                    IsSmbOpen = smbOpen
                };
            }, cancellationToken);
        }

        private static async Task<bool> CheckPortOpenAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var client = new TcpClient();

                // Direct await with cts.Token guarantees observed tasks and zero finalizer leaks
                await client.ConnectAsync(host, port, cts.Token);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Strips URI schemes (http://, https://), trailing slashes, and port numbers
        /// to prevent SocketException when host strings contain extra protocol formatting.
        /// </summary>
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
    }
}
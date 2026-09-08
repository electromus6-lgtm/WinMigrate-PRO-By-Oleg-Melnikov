using System;

namespace WpfApp1.Models
{
    /// <summary>
    /// Captures socket connectivity, ICMP latency, and firewall port status 
    /// across WinRM HTTP (5985), WinRM HTTPS (5986), and SMB (445) for a Hyper-V node.
    /// </summary>
    public sealed class NetworkDiagnosticResult
    {
        public string Host { get; set; } = string.Empty;
        public bool IsReachable { get; set; }
        public long LatencyMs { get; set; }
        public bool IsWinRmOpen { get; set; }
        public bool IsWinRmSslOpen { get; set; }
        public bool IsSmbOpen { get; set; }
        public bool IsLiveMigrationPortOpen { get; set; } = true;
        public DateTime DiagnosticTimestampUtc { get; set; } = DateTime.UtcNow;

        // ---------------------------------------------------------
        // Enterprise Readiness Indicators
        // ---------------------------------------------------------

        public bool IsComputeMigrationReady => IsWinRmOpen || IsWinRmSslOpen;

        public bool IsStorageMigrationReady => IsSmbOpen;

        public bool IsHighLatency => LatencyMs > 100;

        public bool IsOptimal => IsReachable && IsComputeMigrationReady && IsStorageMigrationReady && !IsHighLatency;

        public string PortBreakdownText =>
            $"WinRM(5985): {(IsWinRmOpen ? "OPEN" : "CLOSED")} | " +
            $"WinRM-SSL(5986): {(IsWinRmSslOpen ? "OPEN" : "CLOSED")} | " +
            $"SMB(445): {(IsSmbOpen ? "OPEN" : "CLOSED")}";

        // ---------------------------------------------------------
        // UI Presentation & Color Branding
        // ---------------------------------------------------------

        public string Summary
        {
            get
            {
                if (!IsReachable)
                {
                    return $"Host Unreachable (ICMP Timeout & TCP Closed)";
                }

                if (IsWinRmOpen && IsSmbOpen)
                {
                    if (IsHighLatency)
                    {
                        return $"High Latency Warning ({LatencyMs}ms | WinRM & SMB Ready - WAN Relocation Delay Risk)";
                    }
                    return $"Fabric Ready ({LatencyMs}ms | WinRM 5985: OK | SMB 445: OK)";
                }

                if (IsWinRmOpen && !IsSmbOpen)
                {
                    return $"Compute Only ({LatencyMs}ms | WinRM: OK | SMB 445: Blocked - Shared Storage Blocked)";
                }

                if (IsWinRmSslOpen)
                {
                    return $"WinRM HTTPS Ready ({LatencyMs}ms | Port 5986: OK | SMB 445: {(IsSmbOpen ? "OK" : "Blocked")})";
                }

                return $"Firewall Warning ({LatencyMs}ms | WinRM 5985/5986 Closed/Filtered)";
            }
        }

        public string HexColor
        {
            get
            {
                if (!IsReachable || !IsComputeMigrationReady)
                {
                    return "#EF4444"; // Red: Critical / Cannot migrate
                }

                if (!IsStorageMigrationReady || IsHighLatency)
                {
                    return "#F59E0B"; // Amber: Partial readiness / High latency hazard
                }

                return "#10B981"; // Emerald Green: 100% Ready
            }
        }
    }
}
using System;

namespace WpfApp1.Models
{
    /// <summary>
    /// Captures Hyper-V Live Migration authentication protocol, service state, 
    /// and transport QoS performance settings for a single physical host node.
    /// </summary>
    public sealed class HostAuditResult
    {
        public string Hostname { get; set; } = string.Empty;
        public bool IsMigrationEnabled { get; set; }
        public bool IsKerberosAuth { get; set; }
        public string AuthType { get; set; } = "Unknown";
        public string PerformanceOption { get; set; } = "Compression";
        public int MaxConcurrentMigrations { get; set; } = 4;
        public bool WinRmFirewallRuleActive { get; set; } = true;

        public bool IsHealthy => IsMigrationEnabled && IsKerberosAuth;

        public string StatusSummary => IsHealthy
            ? "Optimal (Kerberos Live Migration Active)"
            : $"Misconfigured ({AuthType} - 0x8009030D Risk)";

        public string StatusColor => IsHealthy ? "#10B981" : "#EF4444";
    }

    /// <summary>
    /// Consolidated audit report for a source-target host pair,
    /// evaluating Kerberos delegation, 0x8009030D double-hop health, and CSV storage paths.
    /// </summary>
    public sealed class InfrastructureAuditReport
    {
        public HostAuditResult Source { get; set; } = new();
        public HostAuditResult Target { get; set; } = new();
        public bool TargetFolderExists { get; set; }
        public string TargetFolderPath { get; set; } = string.Empty;
        public DateTime AuditedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True only if both nodes have Live Migration enabled with Kerberos authentication 
        /// and the target CSV/local storage directory is verified.
        /// </summary>
        public bool IsFullyHealthy => Source.IsHealthy && Target.IsHealthy && TargetFolderExists;

        public string OverallStatusSummary => IsFullyHealthy
            ? "Infrastructure Health: OPTIMAL. Ready for Live Migrations without CredSSP double-hop errors."
            : "Infrastructure Health: REMEDIATION REQUIRED. Click '1-Click Auto-Repair Both Hosts' to resolve Kerberos and path misconfigurations.";
    }
}
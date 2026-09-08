using System;
using System.Collections.Generic;

namespace WpfApp1.Models
{
    /// <summary>
    /// Central persistent configuration schema saved to %AppData%\WinMigratePro\config.json.
    /// Governs WinRM host targets, QoS migration bandwidth, Webhook alerts, and scheduled queues.
    /// </summary>
    public sealed class AppSettingsModel
    {
        // ---------------------------------------------------------
        // Core Hyper-V Host Connection Preferences
        // ---------------------------------------------------------
        public string LastSourceHost { get; set; } = "192.168.25.29";
        public string LastSourceUser { get; set; } = string.Empty;
        public string LastTargetHost { get; set; } = string.Empty;
        public string LastTargetUser { get; set; } = string.Empty;
        public string DefaultStoragePath { get; set; } = @"C:\ClusterStorage\Volume1";
        public bool EnableCredSsp { get; set; } = true;
        public List<ManagedHostEntry> SavedHosts { get; set; } = [];

        // ---------------------------------------------------------
        // Enterprise Migration Transport & Network QoS
        // ---------------------------------------------------------
        public string MigrationPerformanceOption { get; set; } = "Compression"; // "TCP", "Compression", "SMB"
        public long MigrationBandwidthLimitMbps { get; set; } = 0; // 0 = Unlimited
        public string PreferredMigrationSubnet { get; set; } = string.Empty;

        // ---------------------------------------------------------
        // Enterprise Batch Evacuation & Queue Controls
        // ---------------------------------------------------------
        public int DefaultConcurrencyLimit { get; set; } = 2; // Concurrent Move-VM limit
        public bool AutoRollbackOnHeartbeatFailure { get; set; } = true;
        public int HeartbeatVerificationTimeoutSeconds { get; set; } = 180;
        public List<ScheduledMigrationBatch> ScheduledBatches { get; set; } = [];

        // ---------------------------------------------------------
        // Enterprise Pre-Flight & Health Guard
        // ---------------------------------------------------------
        public bool EnforceCpuCompatibilityCheck { get; set; } = true;
        public int EnforceStorageHeadroomBufferGB { get; set; } = 20; // 20 GB safety margin reserve
        public bool AutoPurgeSystemKerberosTickets { get; set; } = true; // Auto-resolves 0x8009030D

        // ---------------------------------------------------------
        // Enterprise IT Alerting & Webhooks (Discord, Slack, MS Teams)
        // ---------------------------------------------------------
        public bool WebhooksEnabled { get; set; } = false;
        public string DiscordWebhookUrl { get; set; } = string.Empty;
        public string SlackWebhookUrl { get; set; } = string.Empty;
        public string TeamsWebhookUrl { get; set; } = string.Empty;
        public bool NotifyOnBatchStart { get; set; } = true;
        public bool NotifyOnVmSuccess { get; set; } = true;
        public bool NotifyOnVmFailure { get; set; } = true;
        public bool NotifyOnRollback { get; set; } = true;
    }
}
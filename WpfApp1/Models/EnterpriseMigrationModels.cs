using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfApp1.Models
{
    /// <summary>
    /// Represents a point-in-time Hyper-V checkpoint/snapshot and its associated delta AVHDX disk chains.
    /// Used by the AVHDX Chain Guard to detect and merge stale snapshots before live migration.
    /// </summary>
    public class VmCheckpointInfo
    {
        public string CheckpointId { get; set; } = Guid.NewGuid().ToString("N");
        public string CheckpointName { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; } = DateTime.Now;
        public string ParentCheckpointId { get; set; } = string.Empty;
        public List<string> AvhdxPaths { get; set; } = new();
        public long TotalDeltaSizeBytes { get; set; }

        public string FormattedDeltaSize
        {
            get
            {
                double gb = TotalDeltaSizeBytes / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1024 ? $"{gb / 1024.0:F2} TB" : $"{gb:F1} GB";
            }
        }

        public string DisplayText => $"{CheckpointName} ({CreationTime:yyyy-MM-dd HH:mm}) - Delta: {FormattedDeltaSize}";
    }

    /// <summary>
    /// Represents a physical network adapter or IP subnet dedicated to Hyper-V live migration traffic,
    /// isolating migration IO from production gaming and client subnets.
    /// </summary>
    public class MigrationSubnetModel : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private int _priority = 1;

        public string SubnetOrIp { get; set; } = string.Empty;
        public string InterfaceAlias { get; set; } = string.Empty;

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public int Priority
        {
            get => _priority;
            set { _priority = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Represents a virtual network adapter mapping between source and target Hyper-V virtual switches.
    /// Prevents migration failures due to non-matching switch names across datacenter nodes.
    /// </summary>
    public class VmNetworkAdapterMapping : INotifyPropertyChanged
    {
        private string _targetSwitchName = string.Empty;
        private int _targetVlanId;

        public string AdapterId { get; set; } = Guid.NewGuid().ToString("N");
        public string AdapterName { get; set; } = "Network Adapter";
        public string MacAddress { get; set; } = "00-00-00-00-00-00";
        public string SourceSwitchName { get; set; } = string.Empty;
        public int SourceVlanId { get; set; } = 0;
        public bool IsConnected { get; set; } = true;

        public ObservableCollection<string> TargetSwitchOptions { get; set; } = new();

        public string TargetSwitchName
        {
            get => _targetSwitchName;
            set
            {
                if (_targetSwitchName != value)
                {
                    _targetSwitchName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsMappedCorrectly));
                }
            }
        }

        public int TargetVlanId
        {
            get => _targetVlanId;
            set
            {
                if (_targetVlanId != value)
                {
                    _targetVlanId = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsMappedCorrectly => !string.IsNullOrWhiteSpace(TargetSwitchName);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Represents an individual VHDX/VHD drive attached to a workload,
    /// enabling multi-volume storage disaggregation (e.g. OS disk to NVMe CSV, Data disks to HDD CSV).
    /// </summary>
    public class DisaggregatedDiskMapping : INotifyPropertyChanged
    {
        private string _targetDirectoryPath = string.Empty;

        public string DiskId { get; set; } = Guid.NewGuid().ToString("N");
        public string ControllerType { get; set; } = "SCSI";
        public int ControllerNumber { get; set; } = 0;
        public int ControllerLocation { get; set; } = 0;
        public string SourcePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; } = 0;

        public string SizeFormatted
        {
            get
            {
                double gb = SizeBytes / (1024.0 * 1024.0 * 1024.0);
                return $"{gb:F1} GB";
            }
        }

        public string TargetDirectoryPath
        {
            get => _targetDirectoryPath;
            set
            {
                if (_targetDirectoryPath != value)
                {
                    _targetDirectoryPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsTargetPathValid));
                }
            }
        }

        public bool IsTargetPathValid => !string.IsNullOrWhiteSpace(TargetDirectoryPath);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Audit result comparing CPU instruction sets and architecture between source and target nodes.
    /// Detects CPU incompatibility (0x80072740) across Intel/AMD generations.
    /// </summary>
    public class CpuCompatibilityAudit
    {
        public string VmName { get; set; } = string.Empty;
        public string SourceHost { get; set; } = string.Empty;
        public string TargetHost { get; set; } = string.Empty;
        public string SourceCpuVendor { get; set; } = "Unknown";
        public string TargetCpuVendor { get; set; } = "Unknown";
        public string SourceCpuName { get; set; } = "Unknown";
        public string TargetCpuName { get; set; } = "Unknown";
        public bool IsVendorMismatch => !string.Equals(SourceCpuVendor, TargetCpuVendor, StringComparison.OrdinalIgnoreCase);
        public bool IsCompatibilityModeActiveOnVm { get; set; }
        public bool RequiresCompatibilityMode { get; set; }
        public string RecommendationSummary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Defines an enterprise scheduled maintenance batch for unattended overnight evacuations.
    /// </summary>
    public class ScheduledMigrationBatch : INotifyPropertyChanged
    {
        private bool _isExecuted;
        private bool _isCancelled;

        public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
        public string BatchName { get; set; } = "Datacenter Evacuation Window";
        public DateTime ScheduledExecutionUtc { get; set; } = DateTime.UtcNow.AddHours(4);
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public List<string> VmNames { get; set; } = new();
        public string SourceHost { get; set; } = string.Empty;
        public string TargetHost { get; set; } = string.Empty;
        public int ConcurrencyLimit { get; set; } = 2;
        public bool AutoRollbackOnHeartbeatFailure { get; set; } = true;
        public int HeartbeatTimeoutSeconds { get; set; } = 180;

        public bool IsExecuted
        {
            get => _isExecuted;
            set { _isExecuted = value; OnPropertyChanged(); }
        }

        public bool IsCancelled
        {
            get => _isCancelled;
            set { _isCancelled = value; OnPropertyChanged(); }
        }

        public string ScheduledLocalTimeText => ScheduledExecutionUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Post-flight verification capturing guest OS heartbeat, KVP Integration Services,
    /// and ICMP reachability to trigger automatic rollback if a live migration damages guest state.
    /// </summary>
    public class PostMigrationHealthCheck
    {
        public string VmName { get; set; } = string.Empty;
        public string TargetHost { get; set; } = string.Empty;
        public bool HeartbeatServiceOk { get; set; }
        public bool IntegrationServicesHealthy { get; set; }
        public string GuestIpAddress { get; set; } = string.Empty;
        public bool PingSucceeded { get; set; }
        public long PingResponseMs { get; set; }
        public bool RollbackTriggered { get; set; }
        public string DiagnosticLog { get; set; } = string.Empty;
        public DateTime CompletedAt { get; set; } = DateTime.UtcNow;

        public bool IsFullyHealthy => HeartbeatServiceOk && IntegrationServicesHealthy && (string.IsNullOrWhiteSpace(GuestIpAddress) || PingSucceeded);
    }

    /// <summary>
    /// Hyper-V Live Migration transport layer and bandwidth QoS settings.
    /// </summary>
    public class MigrationTransportSettings
    {
        public string PerformanceOption { get; set; } = "Compression";
        public long MaximumBandwidthMbps { get; set; } = 0;
        public string PreferredMigrationSubnet { get; set; } = string.Empty;
        public bool EnableCredSspDelegation { get; set; } = false;
    }

    /// <summary>
    /// Configuration for webhook notifications dispatching alerts to external channels.
    /// </summary>
    public class WebhookNotificationConfig
    {
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
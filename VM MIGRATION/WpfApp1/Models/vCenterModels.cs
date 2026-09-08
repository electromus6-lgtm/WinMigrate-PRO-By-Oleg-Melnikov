using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace WpfApp1.Models
{
    public enum VCenterPowerState
    {
        PoweredOff,
        PoweredOn,
        Suspended
    }

    public enum VCenterFirmwareType
    {
        Bios,
        Efi
    }

    public enum V2VMigrationStatus
    {
        Queued,
        ExportingVmdk,
        ConvertingDisk,
        ProvisioningHyperV,
        Completed,
        Failed,
        Cancelled
    }

    public enum V2VConversionEngineMode
    {
        QemuImgEmbedded,
        StarWindCliDetected
    }

    /// <summary>
    /// Represents a virtual disk attached to a VMware ESXi / vCenter workload.
    /// </summary>
    public class VCenterDiskInfo
    {
        public string DiskKey { get; set; } = string.Empty;
        public string Label { get; set; } = "Hard Disk 1";
        public long CapacityBytes { get; set; }
        public string DatastoreName { get; set; } = string.Empty;
        public string VmdkPath { get; set; } = string.Empty;
        public bool IsThinProvisioned { get; set; } = true;

        public string FormattedSize
        {
            get
            {
                double gb = CapacityBytes / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1024 ? $"{gb / 1024.0:F2} TB" : $"{gb:F1} GB";
            }
        }
    }

    /// <summary>
    /// Represents a VMware vSphere / vCenter virtual machine workload staged for conversion to Hyper-V.
    /// </summary>
    public class VCenterVmModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string VmId { get; set; } = string.Empty; // e.g. "vm-1024"
        public string Name { get; set; } = string.Empty;
        public VCenterPowerState PowerState { get; set; } = VCenterPowerState.PoweredOff;
        public int CpuCount { get; set; } = 2;
        public long MemoryMB { get; set; } = 4096;
        public VCenterFirmwareType Firmware { get; set; } = VCenterFirmwareType.Efi;
        public string GuestOsDescription { get; set; } = "Windows / Linux";
        public string EsxiHostName { get; set; } = string.Empty;
        public string NetworkPortGroup { get; set; } = string.Empty;
        public List<VCenterDiskInfo> Disks { get; set; } = [];

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        // Automatic Firmware Parity: Guarantees correct boot architecture
        public VmGeneration RecommendedHyperVGeneration =>
            Firmware == VCenterFirmwareType.Efi ? VmGeneration.Generation2 : VmGeneration.Generation1;

        public string FormattedRam => MemoryMB >= 1024
            ? $"{MemoryMB / 1024.0:F1} GB"
            : $"{MemoryMB} MB";

        public long TotalDiskBytes => Disks.Sum(d => d.CapacityBytes);

        public string FormattedTotalStorage
        {
            get
            {
                double gb = TotalDiskBytes / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1024 ? $"{gb / 1024.0:F2} TB" : $"{gb:F1} GB";
            }
        }

        public string StatusBadgeColor => PowerState switch
        {
            VCenterPowerState.PoweredOn => "#10B981",
            VCenterPowerState.PoweredOff => "#64748B",
            VCenterPowerState.Suspended => "#F59E0B",
            _ => "#64748B"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Tracks active VMware vCenter to Hyper-V V2V conversion tasks and streaming logs.
    /// </summary>
    public sealed class V2VMigrationJob : INotifyPropertyChanged
    {
        private readonly object _logLock = new();
        private V2VMigrationStatus _status = V2VMigrationStatus.Queued;
        private double _progressPercent;
        private string _currentStep = "Staged";
        private DateTime? _endTime;

        public Guid JobId { get; set; } = Guid.NewGuid();
        public string VmName { get; set; } = string.Empty;
        public string SourceVCenter { get; set; } = string.Empty;
        public string TargetHyperVHost { get; set; } = string.Empty;
        public string TargetStorageVolume { get; set; } = string.Empty;
        public DateTime StartTime { get; set; } = DateTime.Now;

        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime != value)
                {
                    _endTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DurationText));
                }
            }
        }

        public V2VMigrationStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusBadgeColor));
                }
            }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set
            {
                if (Math.Abs(_progressPercent - value) > 0.1)
                {
                    _progressPercent = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentStep
        {
            get => _currentStep;
            set
            {
                if (_currentStep != value)
                {
                    _currentStep = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DurationText
        {
            get
            {
                var end = EndTime ?? DateTime.Now;
                var span = end - StartTime;
                return $"{(int)span.TotalMinutes:D2}m {span.Seconds:D2}s";
            }
        }

        public string StatusBadgeColor => Status switch
        {
            V2VMigrationStatus.Completed => "#10B981",
            V2VMigrationStatus.Failed => "#EF4444",
            V2VMigrationStatus.ConvertingDisk => "#38BDF8",
            V2VMigrationStatus.ExportingVmdk => "#00B6DE",
            V2VMigrationStatus.ProvisioningHyperV => "#F59E0B",
            _ => "#64748B"
        };

        public StringBuilder LogBuilder { get; } = new();

        public string AllLogs
        {
            get
            {
                lock (_logLock)
                {
                    return LogBuilder.ToString();
                }
            }
        }

        public void AppendLog(string line)
        {
            lock (_logLock)
            {
                LogBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
            }
            OnPropertyChanged(nameof(AllLogs));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
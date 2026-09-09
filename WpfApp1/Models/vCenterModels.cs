using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;

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
    /// Represents a virtual disk attached to a VMware ESXi / vCenter workload
    /// with destination storage tiering and Hyper-V controller routing.
    /// </summary>
    public class VCenterDiskInfo : INotifyPropertyChanged
    {
        private string _targetStoragePath = string.Empty;
        private string _targetFileName = string.Empty;
        private string _targetMediaTierHint = string.Empty;
        private string _controllerType = "SCSI";
        private int _controllerNumber;
        private int _controllerLocation;
        private bool _isBootDisk;

        public string DiskKey { get; set; } = string.Empty;
        public string Label { get; set; } = "Hard Disk 1";
        public long CapacityBytes { get; set; } = 42949672960L; // 40 GB baseline
        public string DatastoreName { get; set; } = "datastore1";
        public string VmdkPath { get; set; } = string.Empty;
        public bool IsThinProvisioned { get; set; } = true;

        public bool IsBootDisk
        {
            get => _isBootDisk;
            set { if (_isBootDisk != value) { _isBootDisk = value; OnPropertyChanged(); } }
        }

        public string TargetStoragePath
        {
            get => _targetStoragePath;
            set { if (_targetStoragePath != value) { _targetStoragePath = value; OnPropertyChanged(); } }
        }

        public string TargetFileName
        {
            get => _targetFileName;
            set { if (_targetFileName != value) { _targetFileName = value; OnPropertyChanged(); } }
        }

        public string TargetMediaTierHint
        {
            get => _targetMediaTierHint;
            set { if (_targetMediaTierHint != value) { _targetMediaTierHint = value; OnPropertyChanged(); } }
        }

        public string ControllerType
        {
            get => _controllerType;
            set { if (_controllerType != value) { _controllerType = value; OnPropertyChanged(); } }
        }

        public int ControllerNumber
        {
            get => _controllerNumber;
            set { if (_controllerNumber != value) { _controllerNumber = value; OnPropertyChanged(); } }
        }

        public int ControllerLocation
        {
            get => _controllerLocation;
            set { if (_controllerLocation != value) { _controllerLocation = value; OnPropertyChanged(); } }
        }

        public string FormattedSize
        {
            get
            {
                double gb = CapacityBytes / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1024 ? $"{gb / 1024.0:F2} TB" : $"{gb:F1} GB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Represents a live virtual machine workload discovered on the target Hyper-V node in Tab 5.
    /// Provides real-time collision detection against the selected VMware workload.
    /// </summary>
    public sealed class TargetHyperVWorkloadItem : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush BrushRedCollisionBorder = new(Color.FromRgb(239, 68, 68));
        private static readonly SolidColorBrush BrushNormalBorder = new(Color.FromRgb(23, 40, 60));
        private static readonly SolidColorBrush BrushRedCollisionBg = new(Color.FromRgb(38, 10, 10));
        private static readonly SolidColorBrush BrushNormalBg = new(Color.FromRgb(13, 29, 48));

        static TargetHyperVWorkloadItem()
        {
            BrushRedCollisionBorder.Freeze();
            BrushNormalBorder.Freeze();
            BrushRedCollisionBg.Freeze();
            BrushNormalBg.Freeze();
        }

        private bool _isNameCollision;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public VmOperationalStatus Status { get; set; } = VmOperationalStatus.Off;
        public VmGeneration Generation { get; set; } = VmGeneration.Generation2;
        public int CpuCores { get; set; } = 1;
        public long MemoryMB { get; set; } = 1024;
        public List<string> DiskPaths { get; set; } = [];
        public string AssignedSwitch { get; set; } = string.Empty;

        public bool IsNameCollision
        {
            get => _isNameCollision;
            set
            {
                if (_isNameCollision != value)
                {
                    _isNameCollision = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CardBorderBrush));
                    OnPropertyChanged(nameof(CardBackgroundBrush));
                }
            }
        }

        public string FormattedRam
        {
            get => MemoryMB >= 1024 ? $"{MemoryMB / 1024.0:F1} GB" : $"{MemoryMB} MB";
            set { }
        }

        public string DiskSummary
        {
            get => DiskPaths.Count > 0
                ? $"{DiskPaths.Count} Disk(s): {string.Join(", ", DiskPaths.Select(System.IO.Path.GetFileName))}"
                : "No Hard Disks Attached";
            set { }
        }

        public string GenerationLabel
        {
            get => Generation == VmGeneration.Generation2 ? "Gen 2 (UEFI)" : "Gen 1 (BIOS)";
            set { }
        }

        public SolidColorBrush CardBorderBrush
        {
            get => IsNameCollision ? BrushRedCollisionBorder : BrushNormalBorder;
            set { }
        }

        public SolidColorBrush CardBackgroundBrush
        {
            get => IsNameCollision ? BrushRedCollisionBg : BrushNormalBg;
            set { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Represents a VMware vSphere / vCenter virtual machine workload staged for conversion to Hyper-V.
    /// </summary>
    public class VCenterVmModel : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush GreenPowerBrush = new(Color.FromRgb(16, 185, 129));
        private static readonly SolidColorBrush RedPowerBrush = new(Color.FromRgb(239, 68, 68));
        private static readonly SolidColorBrush AmberPowerBrush = new(Color.FromRgb(245, 158, 11));

        static VCenterVmModel()
        {
            GreenPowerBrush.Freeze();
            RedPowerBrush.Freeze();
            AmberPowerBrush.Freeze();
        }

        private bool _isSelected;
        private VCenterPowerState _powerState = VCenterPowerState.PoweredOff;
        private VCenterFirmwareType _firmware = VCenterFirmwareType.Efi;
        private List<VCenterDiskInfo> _disks = new();

        public string VmId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int CpuCount { get; set; } = 2;
        public long MemoryMB { get; set; } = 4096;
        public string GuestOsDescription { get; set; } = "Windows / Linux";
        public string EsxiHostName { get; set; } = string.Empty;
        public string NetworkPortGroup { get; set; } = string.Empty;

        public VCenterPowerState PowerState
        {
            get => _powerState;
            set
            {
                if (_powerState != value)
                {
                    _powerState = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PowerBadgeColor));
                }
            }
        }

        public VCenterFirmwareType Firmware
        {
            get => _firmware;
            set
            {
                if (_firmware != value)
                {
                    _firmware = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RecommendedHyperVGeneration));
                }
            }
        }

        public List<VCenterDiskInfo> Disks
        {
            get => _disks;
            set
            {
                _disks = value ?? new List<VCenterDiskInfo>();
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiskCount));
                OnPropertyChanged(nameof(TotalDiskBytes));
                OnPropertyChanged(nameof(FormattedTotalStorage));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public VmGeneration RecommendedHyperVGeneration =>
            Firmware == VCenterFirmwareType.Efi ? VmGeneration.Generation2 : VmGeneration.Generation1;

        public int DiskCount => _disks?.Count ?? 0;

        public string FormattedRam => MemoryMB >= 1024
            ? $"{MemoryMB / 1024.0:F1} GB"
            : $"{MemoryMB} MB";

        public long TotalDiskBytes => _disks != null ? _disks.ToArray().Sum(d => d.CapacityBytes) : 0L;

        public string FormattedTotalStorage
        {
            get
            {
                double gb = TotalDiskBytes / (1024.0 * 1024.0 * 1024.0);
                return gb >= 1024 ? $"{gb / 1024.0:F2} TB" : $"{gb:F1} GB";
            }
        }

        public SolidColorBrush PowerBadgeColor => PowerState switch
        {
            VCenterPowerState.PoweredOn => GreenPowerBrush,
            VCenterPowerState.Suspended => AmberPowerBrush,
            _ => RedPowerBrush
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
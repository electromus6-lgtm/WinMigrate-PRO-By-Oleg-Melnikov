using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfApp1.Models
{
    /// <summary>
    /// Represents a physical fixed drive, ReFS volume, or Cluster Shared Volume (CSV) 
    /// available on a Hyper-V compute node for virtual hard disk (VHDX) placement.
    /// </summary>
    public sealed class StorageVolumeModel : INotifyPropertyChanged
    {
        private string _path = string.Empty;
        private string _label = string.Empty;
        private string _fileSystem = "NTFS";
        private double _totalSpaceGB;
        private double _freeSpaceGB;
        private bool _isCsv;

        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Label
        {
            get => _label;
            set
            {
                if (_label != value)
                {
                    _label = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string FileSystem
        {
            get => _fileSystem;
            set
            {
                if (_fileSystem != value)
                {
                    _fileSystem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(IsReFs));
                    OnPropertyChanged(nameof(StorageTypeBadge));
                }
            }
        }

        public double TotalSpaceGB
        {
            get => _totalSpaceGB;
            set
            {
                if (Math.Abs(_totalSpaceGB - value) > 0.001)
                {
                    _totalSpaceGB = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UsedSpaceGB));
                    OnPropertyChanged(nameof(UtilizationPercent));
                    OnPropertyChanged(nameof(FormattedCapacity));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public double FreeSpaceGB
        {
            get => _freeSpaceGB;
            set
            {
                if (Math.Abs(_freeSpaceGB - value) > 0.001)
                {
                    _freeSpaceGB = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UsedSpaceGB));
                    OnPropertyChanged(nameof(UtilizationPercent));
                    OnPropertyChanged(nameof(FormattedCapacity));
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(HealthBadgeColor));
                    OnPropertyChanged(nameof(IsCriticalSpaceWarning));
                }
            }
        }

        public bool IsCsv
        {
            get => _isCsv;
            set
            {
                if (_isCsv != value)
                {
                    _isCsv = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                    OnPropertyChanged(nameof(StorageTypeBadge));
                }
            }
        }

        // ---------------------------------------------------------
        // Enterprise Calculations & Telemetry
        // ---------------------------------------------------------

        public double UsedSpaceGB => Math.Max(0.0, TotalSpaceGB - FreeSpaceGB);

        public double UtilizationPercent => TotalSpaceGB > 0 ? (UsedSpaceGB / TotalSpaceGB) * 100.0 : 0.0;

        public bool IsReFs => string.Equals(FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

        public string StorageTypeBadge => IsCsv ? "CSVFS" : (IsReFs ? "ReFS" : FileSystem);

        public bool IsCriticalSpaceWarning => FreeSpaceGB < 15.0 || UtilizationPercent >= 90.0;

        public string HealthBadgeColor => IsCriticalSpaceWarning
            ? "#EF4444"
            : (UtilizationPercent >= 80.0 ? "#F59E0B" : "#10B981");

        public string FormattedCapacity => $"{FreeSpaceGB:F1} GB Free of {TotalSpaceGB:F1} GB ({UtilizationPercent:F0}% Used)";

        /// <summary>
        /// Evaluates whether the volume can accommodate a specified workload payload (GB) 
        /// while preserving an operator-defined safety headroom buffer (default: 20 GB).
        /// </summary>
        public bool HasAdequateHeadroom(double requiredWorkloadGB, double headroomBufferGB = 20.0)
        {
            return FreeSpaceGB >= (requiredWorkloadGB + headroomBufferGB);
        }

        // ---------------------------------------------------------
        // Presentation Helpers
        // ---------------------------------------------------------

        public string DisplayText =>
            IsCsv
                ? $"[CSV] {Path} ({FreeSpaceGB:F1} GB Free / {TotalSpaceGB:F1} GB)"
                : $"[{FileSystem}] {Path} ({FreeSpaceGB:F1} GB Free / {TotalSpaceGB:F1} GB)";

        public override string ToString() => DisplayText;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
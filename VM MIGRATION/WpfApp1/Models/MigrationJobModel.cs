using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace WpfApp1.Models
{
    public enum JobStatus
    {
        Queued,
        Running,
        Completed,
        Failed,
        RolledBack,
        Cancelled
    }

    public sealed class MigrationJobModel : INotifyPropertyChanged
    {
        private readonly object _logLock = new();
        private JobStatus _status = JobStatus.Queued;
        private DateTime? _endTime;
        private bool _isProcessorCompatibilityApplied;
        private bool _isStorageDisaggregated;
        private int _remappedNetworkAdapterCount;
        private bool _heartbeatVerified;
        private bool _isRolledBack;
        private string _failureReason = string.Empty;

        public Guid JobId { get; set; } = Guid.NewGuid();
        public string VmName { get; set; } = string.Empty;
        public string SourceHost { get; set; } = string.Empty;
        public string TargetHost { get; set; } = string.Empty;
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

        public JobStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusBadgeColor));
                    OnPropertyChanged(nameof(IsCompleted));
                    OnPropertyChanged(nameof(IsRunning));
                }
            }
        }

        // ---------------------------------------------------------
        // Enterprise Metadata & Telemetry Tracking
        // ---------------------------------------------------------

        public bool IsProcessorCompatibilityApplied
        {
            get => _isProcessorCompatibilityApplied;
            set { _isProcessorCompatibilityApplied = value; OnPropertyChanged(); }
        }

        public bool IsStorageDisaggregated
        {
            get => _isStorageDisaggregated;
            set { _isStorageDisaggregated = value; OnPropertyChanged(); }
        }

        public int RemappedNetworkAdapterCount
        {
            get => _remappedNetworkAdapterCount;
            set { _remappedNetworkAdapterCount = value; OnPropertyChanged(); }
        }

        public bool HeartbeatVerified
        {
            get => _heartbeatVerified;
            set { _heartbeatVerified = value; OnPropertyChanged(); }
        }

        public bool IsRolledBack
        {
            get => _isRolledBack;
            set { _isRolledBack = value; OnPropertyChanged(); }
        }

        public string FailureReason
        {
            get => _failureReason;
            set { _failureReason = value; OnPropertyChanged(); }
        }

        // ---------------------------------------------------------
        // UI Presentation & Duration Formatting
        // ---------------------------------------------------------

        public string DurationText
        {
            get
            {
                var end = EndTime ?? DateTime.Now;
                var span = end - StartTime;
                if (span.TotalHours >= 1)
                {
                    return $"{(int)span.TotalHours:D2}h {span.Minutes:D2}m {span.Seconds:D2}s";
                }
                return $"{span.Minutes:D2}m {span.Seconds:D2}s";
            }
            set { } // Allows safe WPF Run.Text TwoWay binding without read-only crashes
        }

        public bool IsCompleted => Status == JobStatus.Completed;
        public bool IsRunning => Status == JobStatus.Running;

        public string StatusBadgeColor => Status switch
        {
            JobStatus.Completed => "#10B981",    // Emerald Green
            JobStatus.Failed => "#EF4444",       // Red
            JobStatus.Running => "#38BDF8",      // Cyan
            JobStatus.Queued => "#F59E0B",       // Amber
            JobStatus.RolledBack => "#F87171",   // Crimson Alert
            JobStatus.Cancelled => "#64748B",    // Slate Gray
            _ => "#64748B"
        };

        // ---------------------------------------------------------
        // Thread-Safe WinRM Log Stream Capture
        // ---------------------------------------------------------

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

        public void AppendLog(string message)
        {
            lock (_logLock)
            {
                LogBuilder.AppendLine(message);
            }
            OnPropertyChanged(nameof(AllLogs));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace WpfApp1.Models;

public class VirtualMachineModel : INotifyPropertyChanged
{
    private Guid _id = Guid.NewGuid();
    private string _name = string.Empty;
    private VmOperationalStatus _status = VmOperationalStatus.Unknown;
    private int _cpuCores;
    private long _assignedRamMB;
    private VmGeneration _generation = VmGeneration.Generation2;
    private string _assignedSwitch = string.Empty;
    private Guid _parentHostId;
    private string _residentHostName = string.Empty;
    private bool _isSelected;

    // Enterprise: Cross-Generation CPU Compatibility Mode (Fixes 0x80072740)
    private bool _compatibilityForMigrationModeEnabled;

    // Enterprise: Dynamic Memory Configuration
    private bool _dynamicMemoryEnabled;
    private long _memoryStartupMB;
    private long _memoryMinimumMB = 512;
    private long _memoryMaximumMB = 1048576; // 1 TB default boundary
    private int _memoryBufferPercentage = 20;

    // Enterprise: Security & vTPM State
    private bool _hasVirtualTpm;
    private bool _secureBootEnabled = true;
    private bool _shielded;

    // Enterprise: Guest OS Heartbeat & Health Telemetry
    private bool _heartbeatOk;
    private string _guestIpAddresses = string.Empty;

    // ---------------------------------------------------------
    // Core Hyper-V Workload Properties
    // ---------------------------------------------------------

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public VmOperationalStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusBadgeColor));
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public int CpuCores
    {
        get => _cpuCores;
        set => SetField(ref _cpuCores, value);
    }

    public long AssignedRamMB
    {
        get => _assignedRamMB;
        set
        {
            if (SetField(ref _assignedRamMB, value))
            {
                OnPropertyChanged(nameof(FormattedRam));
            }
        }
    }

    public VmGeneration Generation
    {
        get => _generation;
        set => SetField(ref _generation, value);
    }

    public List<string> VhdxPaths { get; set; } = [];

    public string AssignedSwitch
    {
        get => _assignedSwitch;
        set => SetField(ref _assignedSwitch, value);
    }

    public Guid ParentHostId
    {
        get => _parentHostId;
        set => SetField(ref _parentHostId, value);
    }

    // ---------------------------------------------------------
    // Enterprise Extensions
    // ---------------------------------------------------------

    public string ResidentHostName
    {
        get => _residentHostName;
        set => SetField(ref _residentHostName, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool CompatibilityForMigrationModeEnabled
    {
        get => _compatibilityForMigrationModeEnabled;
        set => SetField(ref _compatibilityForMigrationModeEnabled, value);
    }

    public bool DynamicMemoryEnabled
    {
        get => _dynamicMemoryEnabled;
        set => SetField(ref _dynamicMemoryEnabled, value);
    }

    public long MemoryStartupMB
    {
        get => _memoryStartupMB;
        set => SetField(ref _memoryStartupMB, value);
    }

    public long MemoryMinimumMB
    {
        get => _memoryMinimumMB;
        set => SetField(ref _memoryMinimumMB, value);
    }

    public long MemoryMaximumMB
    {
        get => _memoryMaximumMB;
        set => SetField(ref _memoryMaximumMB, value);
    }

    public int MemoryBufferPercentage
    {
        get => _memoryBufferPercentage;
        set => SetField(ref _memoryBufferPercentage, value);
    }

    public bool HasVirtualTpm
    {
        get => _hasVirtualTpm;
        set => SetField(ref _hasVirtualTpm, value);
    }

    public bool SecureBootEnabled
    {
        get => _secureBootEnabled;
        set => SetField(ref _secureBootEnabled, value);
    }

    public bool Shielded
    {
        get => _shielded;
        set => SetField(ref _shielded, value);
    }

    public bool HeartbeatOk
    {
        get => _heartbeatOk;
        set => SetField(ref _heartbeatOk, value);
    }

    public string GuestIpAddresses
    {
        get => _guestIpAddresses;
        set => SetField(ref _guestIpAddresses, value);
    }

    // Enterprise Collections: Multi-vNIC Remapping Matrix & Disaggregated Disks
    public ObservableCollection<VmNetworkAdapterMapping> NetworkAdapters { get; set; } = [];
    public ObservableCollection<DisaggregatedDiskMapping> HardDisks { get; set; } = [];

    // ---------------------------------------------------------
    // Dynamic UI & Presentation Helpers
    // ---------------------------------------------------------

    public bool IsRunning => Status == VmOperationalStatus.Running;

    public string FormattedRam => AssignedRamMB >= 1024
        ? $"{(AssignedRamMB / 1024.0):F1} GB"
        : $"{AssignedRamMB} MB";

    public string StatusBadgeColor => Status switch
    {
        VmOperationalStatus.Running => "#10B981",    // Emerald Green
        VmOperationalStatus.Off => "#64748B",        // Slate Gray
        VmOperationalStatus.Paused => "#F59E0B",     // Amber Warning
        VmOperationalStatus.Saved => "#3B82F6",      // Cobalt Blue
        _ => "#EF4444"                               // Red (Unknown / Error)
    };

    public long TotalStorageBytes => HardDisks.Sum(d => d.SizeBytes);

    public string FormattedTotalStorage
    {
        get
        {
            double gb = TotalStorageBytes / (1024.0 * 1024.0 * 1024.0);
            return gb >= 1024 ? $"{(gb / 1024.0):F2} TB" : $"{gb:F1} GB";
        }
    }

    // ---------------------------------------------------------
    // Constructors
    // ---------------------------------------------------------

    public VirtualMachineModel()
    {
    }

    public VirtualMachineModel(
        Guid id,
        string name,
        VmOperationalStatus status,
        int cpuCores,
        long assignedRamMB,
        VmGeneration generation,
        IEnumerable<string> vhdxPaths,
        string assignedSwitch,
        Guid parentHostId)
    {
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Status = status;
        CpuCores = cpuCores;
        AssignedRamMB = assignedRamMB;
        MemoryStartupMB = assignedRamMB;
        Generation = generation;
        AssignedSwitch = assignedSwitch ?? throw new ArgumentNullException(nameof(assignedSwitch));
        ParentHostId = parentHostId;

        if (vhdxPaths != null)
        {
            foreach (var path in vhdxPaths)
            {
                VhdxPaths.Add(path);
                HardDisks.Add(new DisaggregatedDiskMapping
                {
                    SourcePath = path,
                    FileName = Path.GetFileName(path),
                    SizeBytes = 0,
                    TargetDirectoryPath = string.Empty
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(assignedSwitch))
        {
            NetworkAdapters.Add(new VmNetworkAdapterMapping
            {
                AdapterName = "Default Network Adapter",
                SourceSwitchName = assignedSwitch,
                TargetSwitchName = assignedSwitch,
                IsConnected = true
            });
        }
    }

    // ---------------------------------------------------------
    // INotifyPropertyChanged Implementation
    // ---------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
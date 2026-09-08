using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfApp1.Models;

public class HostModel : INotifyPropertyChanged
{
    private Guid _id = Guid.NewGuid();
    private string _hostname = string.Empty;
    private string _ipAddress = string.Empty;
    private int _totalCpuCores;
    private int _availableCpuCores;
    private double _totalRamGB;
    private double _availableRamGB;
    private bool _isInMaintenanceMode;

    // Enterprise Hardware & Hyper-V Fabric Metadata
    private string _cpuManufacturer = "Unknown";
    private string _cpuModelName = "Unknown";
    private bool _isLiveMigrationEnabled = true;
    private string _migrationAuthType = "Kerberos";
    private string _migrationPerformanceOption = "Compression";
    private int _maxConcurrentMigrations = 4;
    private bool _isClustered;
    private string _clusterName = string.Empty;
    private string _osVersion = string.Empty;

    // ---------------------------------------------------------
    // Core Compute Host Specifications
    // ---------------------------------------------------------

    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Hostname
    {
        get => _hostname;
        set => SetField(ref _hostname, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetField(ref _ipAddress, value);
    }

    public int TotalCpuCores
    {
        get => _totalCpuCores;
        set => SetField(ref _totalCpuCores, value);
    }

    public int AvailableCpuCores
    {
        get => _availableCpuCores;
        set => SetField(ref _availableCpuCores, value);
    }

    public double TotalRamGB
    {
        get => _totalRamGB;
        set
        {
            if (SetField(ref _totalRamGB, value))
            {
                OnPropertyChanged(nameof(UsedRamGB));
                OnPropertyChanged(nameof(RamUtilizationPercent));
                OnPropertyChanged(nameof(FormattedRamCapacity));
            }
        }
    }

    public double AvailableRamGB
    {
        get => _availableRamGB;
        set
        {
            if (SetField(ref _availableRamGB, value))
            {
                OnPropertyChanged(nameof(UsedRamGB));
                OnPropertyChanged(nameof(RamUtilizationPercent));
                OnPropertyChanged(nameof(FormattedRamCapacity));
            }
        }
    }

    public bool IsInMaintenanceMode
    {
        get => _isInMaintenanceMode;
        set
        {
            if (SetField(ref _isInMaintenanceMode, value))
            {
                OnPropertyChanged(nameof(MaintenanceStatusBadgeColor));
            }
        }
    }

    // ---------------------------------------------------------
    // Enterprise Hardware & Fabric Metadata
    // ---------------------------------------------------------

    public string CpuManufacturer
    {
        get => _cpuManufacturer;
        set => SetField(ref _cpuManufacturer, value);
    }

    public string CpuModelName
    {
        get => _cpuModelName;
        set => SetField(ref _cpuModelName, value);
    }

    public bool IsLiveMigrationEnabled
    {
        get => _isLiveMigrationEnabled;
        set => SetField(ref _isLiveMigrationEnabled, value);
    }

    public string MigrationAuthType
    {
        get => _migrationAuthType;
        set => SetField(ref _migrationAuthType, value);
    }

    public string MigrationPerformanceOption
    {
        get => _migrationPerformanceOption;
        set => SetField(ref _migrationPerformanceOption, value);
    }

    public int MaxConcurrentMigrations
    {
        get => _maxConcurrentMigrations;
        set => SetField(ref _maxConcurrentMigrations, value);
    }

    public bool IsClustered
    {
        get => _isClustered;
        set => SetField(ref _isClustered, value);
    }

    public string ClusterName
    {
        get => _clusterName;
        set => SetField(ref _clusterName, value);
    }

    public string OsVersion
    {
        get => _osVersion;
        set => SetField(ref _osVersion, value);
    }

    // ---------------------------------------------------------
    // Telemetry & Presentation Calculations
    // ---------------------------------------------------------

    public double UsedRamGB => Math.Max(0.0, TotalRamGB - AvailableRamGB);

    public double RamUtilizationPercent => TotalRamGB > 0 ? (UsedRamGB / TotalRamGB) * 100.0 : 0.0;

    public string FormattedRamCapacity => $"{UsedRamGB:F1} / {TotalRamGB:F1} GB ({RamUtilizationPercent:F0}%)";

    public string MaintenanceStatusBadgeColor => IsInMaintenanceMode ? "#F59E0B" : "#10B981";

    // ---------------------------------------------------------
    // Constructors
    // ---------------------------------------------------------

    public HostModel()
    {
    }

    public HostModel(
        Guid id,
        string hostname,
        string ipAddress,
        int totalCpuCores,
        int availableCpuCores,
        double totalRamGB,
        double availableRamGB,
        bool isInMaintenanceMode)
    {
        Id = id;
        Hostname = hostname ?? throw new ArgumentNullException(nameof(hostname));
        IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
        TotalCpuCores = totalCpuCores;
        AvailableCpuCores = availableCpuCores;
        TotalRamGB = totalRamGB;
        AvailableRamGB = availableRamGB;
        IsInMaintenanceMode = isInMaintenanceMode;
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
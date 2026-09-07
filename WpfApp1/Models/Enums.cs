namespace WpfApp1.Models;

/// <summary>
/// Represents the execution state of a Hyper-V virtual machine,
/// directly matching standard WMI/CIM Msvm_ComputerSystem EnabledState values.
/// </summary>
public enum VmOperationalStatus
{
    Unknown = 0,
    Other = 1,
    Running = 2,
    Off = 3,
    Stopping = 4,
    Saved = 6,
    Paused = 9,
    Starting = 10,
    Reset = 11,
    Saving = 32773,
    Pausing = 32776,
    Resuming = 32777,
    Migrating = 32779
}

/// <summary>
/// Represents the Hyper-V virtual machine architectural generation.
/// Generation 1 = Legacy BIOS / IDE controllers.
/// Generation 2 = UEFI / Synthetic SCSI architecture with hot-add support.
/// </summary>
public enum VmGeneration
{
    Generation1 = 1,
    Generation2 = 2
}

/// <summary>
/// Defines the transport acceleration protocol used for Hyper-V Live Migration.
/// </summary>
public enum MigrationPerformanceMode
{
    Compression,
    Smb,
    Tcp
}

/// <summary>
/// Virtual hard disk file formats supported by Hyper-V backplanes.
/// </summary>
public enum VirtualDiskFormat
{
    Vhdx,
    Vhd,
    Vhds
}

/// <summary>
/// Hyper-V snapshot architecture types.
/// </summary>
public enum HyperVCheckpointType
{
    Production,
    Standard
}

/// <summary>
/// Utility extension methods evaluating Hyper-V operational states.
/// </summary>
public static class VmOperationalStatusExtensions
{
    public static bool IsRunning(this VmOperationalStatus status) =>
        status == VmOperationalStatus.Running;

    public static bool IsOffline(this VmOperationalStatus status) =>
        status == VmOperationalStatus.Off || status == VmOperationalStatus.Saved;

    public static bool CanLiveMigrate(this VmOperationalStatus status) =>
        status == VmOperationalStatus.Running || status == VmOperationalStatus.Off || status == VmOperationalStatus.Saved;

    public static string GetFriendlyName(this VmOperationalStatus status) => status switch
    {
        VmOperationalStatus.Running => "Running",
        VmOperationalStatus.Off => "Powered Off",
        VmOperationalStatus.Saved => "Saved State",
        VmOperationalStatus.Paused => "Paused",
        VmOperationalStatus.Starting => "Booting...",
        VmOperationalStatus.Stopping => "Shutting Down...",
        VmOperationalStatus.Migrating => "Migrating...",
        _ => status.ToString()
    };
}
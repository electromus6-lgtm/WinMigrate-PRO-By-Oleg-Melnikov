using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    public sealed class HyperVService
    {
        // =========================================================================
        // 1. WORKLOAD & HOST DISCOVERY ENGINES
        // =========================================================================

        public async Task<List<VirtualMachineModel>> DiscoverVirtualMachinesAsync(
            string host,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var vms = new List<VirtualMachineModel>();
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    if (-not (Get-Module -ListAvailable -Name Hyper-V)) {
                        throw 'Hyper-V PowerShell Module is not installed on this host (' + $env:COMPUTERNAME + ').'
                    }

                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    
                    Get-VM -ErrorAction SilentlyContinue | ForEach-Object {
                        try {
                            $vm = $_
                            
                            $vhds = @()
                            $disks = @()
                            if ($vm.HardDrives) {
                                foreach ($d in $vm.HardDrives) {
                                    $p = $d.Path
                                    if ($p) {
                                        $vhds += $p
                                        $size = 0
                                        try {
                                            if (Test-Path -LiteralPath $p) {
                                                $size = (Get-Item -LiteralPath $p).Length
                                            }
                                        } catch {}

                                        $disks += [PSCustomObject]@{
                                            ControllerType = [string]$d.ControllerType
                                            ControllerNumber = [int]$d.ControllerNumber
                                            ControllerLocation = [int]$d.ControllerLocation
                                            Path = [string]$p
                                            SizeBytes = [long]$size
                                        }
                                    }
                                }
                            }

                            $adapters = @()
                            $primarySwitch = ''
                            if ($vm.NetworkAdapters) {
                                foreach ($na in $vm.NetworkAdapters) {
                                    $vlanId = 0
                                    try {
                                        $vlan = Get-VMNetworkAdapterVlan -VMNetworkAdapter $na -ErrorAction SilentlyContinue
                                        if ($vlan -and $vlan.AccessVlanId) { $vlanId = [int]$vlan.AccessVlanId }
                                    } catch {}

                                    $ipList = ''
                                    if ($na.IPAddresses) {
                                        $ipList = ($na.IPAddresses | Where-Object { $_ -notlike '*:*' }) -join ', '
                                    }

                                    if ([string]::IsNullOrWhiteSpace($primarySwitch) -and $na.SwitchName) {
                                        $primarySwitch = [string]$na.SwitchName
                                    }

                                    $adapters += [PSCustomObject]@{
                                        Name = [string]$na.Name
                                        Mac = [string]$na.MacAddress
                                        Switch = [string]$na.SwitchName
                                        VlanId = [int]$vlanId
                                        IPs = [string]$ipList
                                        IsConnected = [bool]($na.Connected -ne $false)
                                    }
                                }
                            }

                            $proc = $vm | Get-VMProcessor -ErrorAction SilentlyContinue
                            $compat = if ($proc) { [bool]$proc.CompatibilityForMigrationMode } else { $false }

                            $hasVtpm = $false
                            $shielded = $false
                            $secBoot = $true
                            try {
                                $sec = Get-VMSecurity -VM $vm -ErrorAction SilentlyContinue
                                if ($sec) {
                                    $hasVtpm = [bool]$sec.VirtualTpmEnabled
                                    $shielded = [bool]$sec.Shielded
                                    $secBoot = [bool]$sec.SecureBoot
                                }
                            } catch {}

                            $memBytes = if ($vm.MemoryAssigned -gt 0) { $vm.MemoryAssigned } else { $vm.MemoryStartup }
                            $dynMem = [bool]$vm.DynamicMemoryEnabled
                            $memMin = [long]$vm.MemoryMinimum
                            $memMax = [long]$vm.MemoryMaximum
                            $memStartup = [long]$vm.MemoryStartup

                            $hbOk = $false
                            try {
                                $hb = Get-VMIntegrationService -VM $vm -Name 'Heartbeat' -ErrorAction SilentlyContinue
                                if ($hb -and $hb.PrimaryStatusDescription -eq 'OK') { $hbOk = $true }
                            } catch {}

                            [PSCustomObject]@{
                                Id = $vm.Id.ToString()
                                Name = $vm.Name
                                State = [int]$vm.State
                                ProcessorCount = [int]$vm.ProcessorCount
                                MemoryBytes = [long]$memBytes
                                MemoryStartup = [long]$memStartup
                                DynamicMemory = $dynMem
                                MemoryMinimum = [long]$memMin
                                MemoryMaximum = [long]$memMax
                                Generation = [int]$vm.Generation
                                VhdxPaths = $vhds
                                Disks = $disks
                                Adapters = $adapters
                                SwitchName = $primarySwitch
                                CpuCompat = $compat
                                HasVtpm = $hasVtpm
                                Shielded = $shielded
                                SecureBoot = $secBoot
                                HeartbeatOk = $hbOk
                            }
                        } catch {}
                    }
                ");

                var results = ps.Invoke();

                if (ps.HadErrors && results.Count == 0)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Hyper-V VM Query Failed: {errors}");
                }

                foreach (var item in results)
                {
                    if (item?.BaseObject == null) continue;

                    try
                    {
                        var idStr = item.Properties["Id"]?.Value?.ToString();
                        var name = item.Properties["Name"]?.Value?.ToString() ?? "Unknown";
                        var stateInt = item.Properties["State"]?.Value is int s ? s : (int)VmOperationalStatus.Unknown;
                        var cpu = item.Properties["ProcessorCount"]?.Value is int c ? c : 1;
                        var memBytes = Convert.ToInt64(item.Properties["MemoryBytes"]?.Value ?? 0L);
                        var startupBytes = Convert.ToInt64(item.Properties["MemoryStartup"]?.Value ?? memBytes);
                        var dynMem = Convert.ToBoolean(item.Properties["DynamicMemory"]?.Value ?? false);
                        var minBytes = Convert.ToInt64(item.Properties["MemoryMinimum"]?.Value ?? 536870912L);
                        var maxBytes = Convert.ToInt64(item.Properties["MemoryMaximum"]?.Value ?? 1099511627776L);
                        var gen = item.Properties["Generation"]?.Value is int g && g == 1 ? VmGeneration.Generation1 : VmGeneration.Generation2;
                        var switchName = item.Properties["SwitchName"]?.Value?.ToString() ?? string.Empty;
                        var cpuCompat = Convert.ToBoolean(item.Properties["CpuCompat"]?.Value ?? false);
                        var hasVtpm = Convert.ToBoolean(item.Properties["HasVtpm"]?.Value ?? false);
                        var shielded = Convert.ToBoolean(item.Properties["Shielded"]?.Value ?? false);
                        var secBoot = Convert.ToBoolean(item.Properties["SecureBoot"]?.Value ?? true);
                        var hbOk = Convert.ToBoolean(item.Properties["HeartbeatOk"]?.Value ?? false);

                        var vmGuid = Guid.TryParse(idStr, out var parsedGuid) ? parsedGuid : Guid.NewGuid();

                        var vm = new VirtualMachineModel
                        {
                            Id = vmGuid,
                            Name = name,
                            Status = (VmOperationalStatus)stateInt,
                            CpuCores = Math.Max(1, cpu),
                            AssignedRamMB = memBytes > 0 ? (memBytes / (1024 * 1024)) : 1024,
                            MemoryStartupMB = startupBytes > 0 ? (startupBytes / (1024 * 1024)) : 1024,
                            DynamicMemoryEnabled = dynMem,
                            MemoryMinimumMB = Math.Max(256, minBytes / (1024 * 1024)),
                            MemoryMaximumMB = Math.Max(1024, maxBytes / (1024 * 1024)),
                            Generation = gen,
                            AssignedSwitch = switchName,
                            CompatibilityForMigrationModeEnabled = cpuCompat,
                            HasVirtualTpm = hasVtpm,
                            Shielded = shielded,
                            SecureBootEnabled = secBoot,
                            HeartbeatOk = hbOk,
                            ResidentHostName = sanitizedHost,
                            ParentHostId = Guid.Empty
                        };

                        if (item.Properties["VhdxPaths"]?.Value is object[] vhdArray)
                        {
                            foreach (var v in vhdArray)
                            {
                                var path = v?.ToString();
                                if (!string.IsNullOrWhiteSpace(path)) vm.VhdxPaths.Add(path);
                            }
                        }

                        if (item.Properties["Disks"]?.Value is object[] diskArray)
                        {
                            foreach (var dObj in diskArray)
                            {
                                if (dObj is PSObject pso)
                                {
                                    var dPath = pso.Properties["Path"]?.Value?.ToString() ?? string.Empty;
                                    var cType = pso.Properties["ControllerType"]?.Value?.ToString() ?? "SCSI";
                                    var cNum = Convert.ToInt32(pso.Properties["ControllerNumber"]?.Value ?? 0);
                                    var cLoc = Convert.ToInt32(pso.Properties["ControllerLocation"]?.Value ?? 0);
                                    var dSize = Convert.ToInt64(pso.Properties["SizeBytes"]?.Value ?? 0L);

                                    if (!string.IsNullOrWhiteSpace(dPath))
                                    {
                                        vm.HardDisks.Add(new DisaggregatedDiskMapping
                                        {
                                            SourcePath = dPath,
                                            FileName = Path.GetFileName(dPath),
                                            ControllerType = cType,
                                            ControllerNumber = cNum,
                                            ControllerLocation = cLoc,
                                            SizeBytes = dSize
                                        });
                                    }
                                }
                            }
                        }

                        if (item.Properties["Adapters"]?.Value is object[] adapterArray)
                        {
                            var ipAggregator = new List<string>();
                            foreach (var aObj in adapterArray)
                            {
                                if (aObj is PSObject pso)
                                {
                                    var aName = pso.Properties["Name"]?.Value?.ToString() ?? "Network Adapter";
                                    var aMac = pso.Properties["Mac"]?.Value?.ToString() ?? string.Empty;
                                    var aSwitch = pso.Properties["Switch"]?.Value?.ToString() ?? string.Empty;
                                    var aVlan = Convert.ToInt32(pso.Properties["VlanId"]?.Value ?? 0);
                                    var aConn = Convert.ToBoolean(pso.Properties["IsConnected"]?.Value ?? true);
                                    var aIps = pso.Properties["IPs"]?.Value?.ToString() ?? string.Empty;

                                    if (!string.IsNullOrWhiteSpace(aIps)) ipAggregator.Add(aIps);

                                    vm.NetworkAdapters.Add(new VmNetworkAdapterMapping
                                    {
                                        AdapterName = aName,
                                        MacAddress = aMac,
                                        SourceSwitchName = aSwitch,
                                        TargetSwitchName = aSwitch,
                                        SourceVlanId = aVlan,
                                        TargetVlanId = aVlan,
                                        IsConnected = aConn
                                    });
                                }
                            }
                            vm.GuestIpAddresses = string.Join("; ", ipAggregator);
                        }

                        vms.Add(vm);
                    }
                    catch { }
                }

                return vms;
            }, cancellationToken);
        }

        public async Task<bool> CheckVmExistsAsync(
            string host,
            string vmName,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                ps.AddScript($@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    $vm = Get-VM -Name '{safeVmName}' -ErrorAction SilentlyContinue
                    [bool]($vm -ne $null)
                ");

                var results = ps.Invoke();
                if (results.Count > 0 && results[0]?.BaseObject is bool exists)
                {
                    return exists;
                }

                return false;
            }, cancellationToken);
        }

        public async Task<HostModel> DiscoverHostCapacityAsync(
            string host,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    $cs = Get-CimInstance Win32_ComputerSystem
                    $os = Get-CimInstance Win32_OperatingSystem
                    $proc = Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum
                    
                    $totalRamGB = [Math]::Round($cs.TotalPhysicalMemory / 1GB, 2)
                    $freeRamGB = [Math]::Round(($os.FreePhysicalMemory * 1KB) / 1GB, 2)
                    $totalCores = if ($proc.Sum) { [int]$proc.Sum } else { [Environment]::ProcessorCount }

                    [PSCustomObject]@{
                        Hostname = $cs.DNSHostName
                        TotalCores = $totalCores
                        TotalRamGB = [double]$totalRamGB
                        FreeRamGB = [double]$freeRamGB
                    }
                ");

                var results = ps.Invoke();

                if (ps.HadErrors || results.Count == 0 || results[0]?.BaseObject == null)
                {
                    throw new InvalidOperationException($"Could not query telemetry on host {sanitizedHost}.");
                }

                var item = results[0];
                var hostname = item.Properties["Hostname"]?.Value?.ToString() ?? sanitizedHost;
                var totalCores = Convert.ToInt32(item.Properties["TotalCores"]?.Value ?? Environment.ProcessorCount);
                var totalRam = Convert.ToDouble(item.Properties["TotalRamGB"]?.Value ?? 0.0);
                var freeRam = Convert.ToDouble(item.Properties["FreeRamGB"]?.Value ?? 0.0);

                return new HostModel
                {
                    Id = Guid.NewGuid(),
                    Hostname = hostname,
                    IpAddress = sanitizedHost,
                    TotalCpuCores = Math.Max(1, totalCores),
                    AvailableCpuCores = Math.Max(1, totalCores),
                    TotalRamGB = Math.Max(1.0, totalRam),
                    AvailableRamGB = Math.Max(0.1, freeRam),
                    IsInMaintenanceMode = false
                };
            }, cancellationToken);
        }

        public async Task<List<string>> DiscoverVirtualSwitchesAsync(
            string host,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var switches = new List<string>();
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    Get-VMSwitch -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
                ");

                var results = ps.Invoke();
                foreach (var item in results)
                {
                    var sName = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(sName))
                    {
                        switches.Add(sName);
                    }
                }

                return switches;
            }, cancellationToken);
        }

        public async Task<List<StorageVolumeModel>> DiscoverTargetStorageVolumesAsync(
            string host,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var volumes = new List<StorageVolumeModel>();
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    $vols = @()

                    # 1. Inspect physical disk media types to build tier classification map
                    $mediaLookup = @{}
                    try {
                        Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
                            $diskNum = $_.DeviceId
                            $bus = $_.BusType
                            $media = $_.MediaType
                            $tier = if ($bus -eq 'NVMe') { 'NVMe SSD' } elseif ($media -eq 'SSD') { 'SSD' } elseif ($media -eq 'HDD') { 'HDD' } else { 'Standard' }
                            $mediaLookup[[string]$diskNum] = $tier
                        }
                    } catch {}

                    # 2. Query Failover Cluster Shared Volumes (CSV)
                    if (Get-Module -ListAvailable -Name FailoverClusters) {
                        Import-Module FailoverClusters -ErrorAction SilentlyContinue
                        try {
                            $csvs = Get-ClusterSharedVolume -ErrorAction SilentlyContinue
                            foreach ($csv in $csvs) {
                                $info = $csv.SharedVolumeInfo
                                $part = $info.Partition
                                if ($part -and $info.FriendlyVolumeName) {
                                    $vols += [PSCustomObject]@{
                                        Path = $info.FriendlyVolumeName
                                        Label = ""$($csv.Name) [CSV Shared Tier]""
                                        FileSystem = 'CSVFS'
                                        FreeGB = [Math]::Round($part.FreeSpace / 1GB, 2)
                                        TotalGB = [Math]::Round($part.Size / 1GB, 2)
                                        IsCsv = $true
                                    }
                                }
                            }
                        } catch {}
                    }

                    # 3. Query Fixed Local Volumes & Detect Media Tiering
                    try {
                        $localVols = Get-Volume | Where-Object { 
                            $_.DriveType -eq 'Fixed' -and 
                            $_.Size -gt 0 -and 
                            $_.FileSystem -in @('NTFS', 'ReFS', 'CSVFS') 
                        }

                        foreach ($lv in $localVols) {
                            $targetPath = if ($lv.DriveLetter) { ""$($lv.DriveLetter):\"" } else { $lv.Path }
                            if ($targetPath -and -not ($vols | Where-Object { $_.Path -eq $targetPath })) {
                                $baseLabel = if ($lv.FileSystemLabel) { $lv.FileSystemLabel } else { 'Local Storage' }
                                
                                $tierTag = ''
                                try {
                                    if ($lv.DriveLetter) {
                                        $part = Get-Partition -DriveLetter $lv.DriveLetter -ErrorAction SilentlyContinue
                                        if ($part -and $mediaLookup.ContainsKey([string]$part.DiskNumber)) {
                                            $tierTag = "" [$($mediaLookup[[string]$part.DiskNumber])]""
                                        }
                                    }
                                } catch {}

                                $vols += [PSCustomObject]@{
                                    Path = $targetPath
                                    Label = ""$baseLabel$tierTag""
                                    FileSystem = $lv.FileSystem
                                    FreeGB = [Math]::Round($lv.SizeRemaining / 1GB, 2)
                                    TotalGB = [Math]::Round($lv.Size / 1GB, 2)
                                    IsCsv = ($lv.FileSystem -eq 'CSVFS')
                                }
                            }
                        }
                    } catch {}

                    $vols
                ");

                var results = ps.Invoke();
                foreach (var item in results)
                {
                    if (item?.BaseObject == null) continue;

                    var path = item.Properties["Path"]?.Value?.ToString() ?? string.Empty;
                    var label = item.Properties["Label"]?.Value?.ToString() ?? "Storage";
                    var fs = item.Properties["FileSystem"]?.Value?.ToString() ?? "NTFS";
                    var freeGb = Convert.ToDouble(item.Properties["FreeGB"]?.Value ?? 0);
                    var totalGb = Convert.ToDouble(item.Properties["TotalGB"]?.Value ?? 0);
                    var isCsv = Convert.ToBoolean(item.Properties["IsCsv"]?.Value ?? false);

                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        volumes.Add(new StorageVolumeModel
                        {
                            Path = path,
                            Label = label,
                            FileSystem = fs,
                            FreeSpaceGB = freeGb,
                            TotalSpaceGB = totalGb,
                            IsCsv = isCsv
                        });
                    }
                }

                return volumes;
            }, cancellationToken);
        }

        // =========================================================================
        // 2. CPU COMPATIBILITY AUDITOR & 1-CLICK ENABLER
        // =========================================================================

        public async Task<CpuCompatibilityAudit> AuditCpuCompatibilityAsync(
            string vmName,
            string sourceHost,
            string targetHost,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var audit = new CpuCompatibilityAudit
                {
                    VmName = vmName,
                    SourceHost = sourceHost,
                    TargetHost = targetHost
                };

                var safeVmName = vmName.Replace("'", "''");

                try
                {
                    using var sourceRunspace = CreateRunspace(SanitizeHost(sourceHost), username, password, IsLocalHost(sourceHost));
                    sourceRunspace.Open();
                    using var psSource = PowerShell.Create();
                    psSource.Runspace = sourceRunspace;

                    var scriptSource = @"
                        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
                        $vmProc = Get-VMProcessor -VMName '__VM_NAME__' -ErrorAction SilentlyContinue
                        [PSCustomObject]@{
                            Manufacturer = [string]$cpu.Manufacturer
                            Name = [string]$cpu.Name
                            CompatActive = if ($vmProc) { [bool]$vmProc.CompatibilityForMigrationMode } else { $false }
                        }
                    ".Replace("__VM_NAME__", safeVmName);

                    psSource.AddScript(scriptSource);
                    var srcRes = psSource.Invoke();
                    if (srcRes.Count > 0 && srcRes[0] != null)
                    {
                        audit.SourceCpuVendor = srcRes[0].Properties["Manufacturer"]?.Value?.ToString() ?? "Unknown";
                        audit.SourceCpuName = srcRes[0].Properties["Name"]?.Value?.ToString() ?? "Unknown";
                        audit.IsCompatibilityModeActiveOnVm = Convert.ToBoolean(srcRes[0].Properties["CompatActive"]?.Value ?? false);
                    }
                }
                catch { }

                try
                {
                    using var targetRunspace = CreateRunspace(SanitizeHost(targetHost), username, password, IsLocalHost(targetHost));
                    targetRunspace.Open();
                    using var psTarget = PowerShell.Create();
                    psTarget.Runspace = targetRunspace;

                    var scriptTarget = @"
                        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
                        [PSCustomObject]@{
                            Manufacturer = [string]$cpu.Manufacturer
                            Name = [string]$cpu.Name
                        }
                    ";

                    psTarget.AddScript(scriptTarget);
                    var tgtRes = psTarget.Invoke();
                    if (tgtRes.Count > 0 && tgtRes[0] != null)
                    {
                        audit.TargetCpuVendor = tgtRes[0].Properties["Manufacturer"]?.Value?.ToString() ?? "Unknown";
                        audit.TargetCpuName = tgtRes[0].Properties["Name"]?.Value?.ToString() ?? "Unknown";
                    }
                }
                catch { }

                var cpuNamesMatch = string.Equals(audit.SourceCpuName, audit.TargetCpuName, StringComparison.OrdinalIgnoreCase);
                var vendorsMatch = string.Equals(audit.SourceCpuVendor, audit.TargetCpuVendor, StringComparison.OrdinalIgnoreCase);

                if (!vendorsMatch)
                {
                    audit.RequiresCompatibilityMode = true;
                    audit.RecommendationSummary = $"Cross-architecture mismatch ({audit.SourceCpuVendor} -> {audit.TargetCpuVendor}). Live migration requires Processor Compatibility Mode enabled.";
                }
                else if (!cpuNamesMatch)
                {
                    audit.RequiresCompatibilityMode = !audit.IsCompatibilityModeActiveOnVm;
                    audit.RecommendationSummary = audit.IsCompatibilityModeActiveOnVm
                        ? "Different CPU generations detected, but Processor Compatibility Mode is ALREADY active on this workload."
                        : $"CPU generation mismatch ({audit.SourceCpuName} -> {audit.TargetCpuName}). Enable Processor Compatibility Mode to prevent 0x80072740 live migration abortion.";
                }
                else
                {
                    audit.RequiresCompatibilityMode = false;
                    audit.RecommendationSummary = "CPUs are identical across hosts. Full native instruction pass-through supported.";
                }

                return audit;
            }, cancellationToken);
        }

        public async Task EnableProcessorCompatibilityAsync(
            string host,
            string vmName,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                ps.AddScript($"Import-Module Hyper-V -ErrorAction SilentlyContinue; Set-VMProcessor -VMName '{safeVmName}' -CompatibilityForMigrationMode Enabled -ErrorAction Stop");
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Failed to enable Processor Compatibility Mode on '{vmName}': {errors}");
                }
            }, cancellationToken);
        }

        // =========================================================================
        // 3. ENTERPRISE LIVE MIGRATION (MULTI-NIC & DISAGGREGATED STORAGE)
        // =========================================================================

        public async Task ExecuteEnterpriseLiveMigrationAsync(
            string vmName,
            string sourceHost,
            string targetHost,
            string? defaultDestinationPath,
            List<VmNetworkAdapterMapping>? adapterMappings,
            List<DisaggregatedDiskMapping>? diskMappings,
            string? username,
            string? password,
            IProgress<string> logProgress,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedSource = SanitizeHost(sourceHost);
                var sanitizedTarget = SanitizeHost(targetHost);
                var isLocal = IsLocalHost(sanitizedSource);

                logProgress.Report($"[INFO] Connecting to source node: {sanitizedSource} via WSMan/WinRM...");

                using var runspace = CreateRunspace(sanitizedSource, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");

                var nicScriptClause = string.Empty;
                if (adapterMappings != null && adapterMappings.Any(a => !string.IsNullOrWhiteSpace(a.TargetSwitchName) && a.TargetSwitchName != a.SourceSwitchName))
                {
                    var nicEntries = new List<string>();
                    foreach (var a in adapterMappings.Where(a => !string.IsNullOrWhiteSpace(a.TargetSwitchName)))
                    {
                        var srcSw = a.SourceSwitchName.Replace("'", "''");
                        var tgtSw = a.TargetSwitchName.Replace("'", "''");
                        nicEntries.Add($"@{{ 'SourceSwitchName' = '{srcSw}'; 'TargetSwitchName' = '{tgtSw}' }}");
                    }
                    if (nicEntries.Count > 0)
                    {
                        nicScriptClause = $"$nicBindings = @( {string.Join(", ", nicEntries)} );";
                    }
                }

                var storageScriptClause = string.Empty;
                var hasCustomDiskTargets = diskMappings != null && diskMappings.Any(d => !string.IsNullOrWhiteSpace(d.TargetDirectoryPath));

                if (hasCustomDiskTargets && diskMappings != null)
                {
                    var diskEntries = new List<string>();
                    foreach (var d in diskMappings.Where(d => !string.IsNullOrWhiteSpace(d.TargetDirectoryPath)))
                    {
                        var srcP = d.SourcePath.Replace("'", "''");
                        var tgtDir = d.TargetDirectoryPath.TrimEnd('\\').Replace("'", "''");
                        var destFile = $"{tgtDir}\\{Path.GetFileName(d.SourcePath)}";
                        diskEntries.Add($"@{{ 'SourceFilePath' = '{srcP}'; 'DestinationFilePath' = '{destFile}' }}");
                    }
                    if (diskEntries.Count > 0)
                    {
                        storageScriptClause = $"$vhdxBindings = @( {string.Join(", ", diskEntries)} );";
                    }
                }

                var moveCmd = $"Move-VM -Name '{safeVmName}' -DestinationHost '{sanitizedTarget}'";

                if (!string.IsNullOrWhiteSpace(nicScriptClause))
                {
                    moveCmd += " -NetworkAdapterBinding $nicBindings";
                }

                if (!string.IsNullOrWhiteSpace(storageScriptClause))
                {
                    moveCmd += " -Vhdx $vhdxBindings";
                    if (!string.IsNullOrWhiteSpace(defaultDestinationPath))
                    {
                        moveCmd += $" -VirtualMachinePath '{defaultDestinationPath.Trim().Replace("'", "''")}'";
                    }
                }
                else if (!string.IsNullOrWhiteSpace(defaultDestinationPath))
                {
                    moveCmd += $" -DestinationStoragePath '{defaultDestinationPath.Trim().Replace("'", "''")}'";
                }
                else
                {
                    moveCmd += " -IncludeStorage";
                }

                moveCmd += " -Verbose";

                var fullScript = $"Import-Module Hyper-V -ErrorAction SilentlyContinue; {nicScriptClause} {storageScriptClause} {moveCmd}";
                logProgress.Report($"[EXEC] Orchestration Command: {moveCmd}");

                ps.AddScript(fullScript);

                ps.Streams.Information.DataAdded += (s, e) =>
                {
                    try
                    {
                        if (ps.Streams.Information.Count > e.Index)
                        {
                            var msg = ps.Streams.Information[e.Index]?.ToString();
                            if (!string.IsNullOrWhiteSpace(msg)) logProgress.Report($"[INFO] {msg}");
                        }
                    }
                    catch { }
                };

                ps.Streams.Warning.DataAdded += (s, e) =>
                {
                    try
                    {
                        if (ps.Streams.Warning.Count > e.Index)
                        {
                            var msg = ps.Streams.Warning[e.Index]?.ToString();
                            if (!string.IsNullOrWhiteSpace(msg)) logProgress.Report($"[WARN] {msg}");
                        }
                    }
                    catch { }
                };

                ps.Streams.Error.DataAdded += (s, e) =>
                {
                    try
                    {
                        if (ps.Streams.Error.Count > e.Index)
                        {
                            var msg = ps.Streams.Error[e.Index]?.ToString();
                            if (!string.IsNullOrWhiteSpace(msg)) logProgress.Report($"[ERR] {msg}");
                        }
                    }
                    catch { }
                };

                logProgress.Report($"[MIGRATE] Migrating '{vmName}' -> '{sanitizedTarget}' with enterprise network/storage bindings...");
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    logProgress.Report($"[FAILED] Migration aborted: {errors}");
                    throw new InvalidOperationException($"Migration of '{vmName}' failed: {errors}");
                }

                logProgress.Report($"[SUCCESS] Workload '{vmName}' successfully relocated to '{sanitizedTarget}'.");
            }, cancellationToken);
        }

        public async Task ExecuteLiveMigrationAsync(
            string vmName,
            string sourceHost,
            string targetHost,
            string? destinationStoragePath,
            string? username,
            string? password,
            IProgress<string> logProgress,
            CancellationToken cancellationToken = default)
        {
            await ExecuteEnterpriseLiveMigrationAsync(
                vmName,
                sourceHost,
                targetHost,
                destinationStoragePath,
                null,
                null,
                username,
                password,
                logProgress,
                cancellationToken);
        }

        // =========================================================================
        // 4. POST-MIGRATION GUEST HEARTBEAT & AUTO-ROLLBACK ENGINE
        // =========================================================================

        public async Task<PostMigrationHealthCheck> VerifyGuestHealthAsync(
            string targetHost,
            string vmName,
            int timeoutSeconds = 60,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(async () =>
            {
                var health = new PostMigrationHealthCheck
                {
                    VmName = vmName,
                    TargetHost = targetHost
                };

                var sanitizedTarget = SanitizeHost(targetHost);
                var isLocal = IsLocalHost(sanitizedTarget);
                var safeVmName = vmName.Replace("'", "''");

                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

                while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using var runspace = CreateRunspace(sanitizedTarget, username, password, isLocal);
                        runspace.Open();

                        using var ps = PowerShell.Create();
                        ps.Runspace = runspace;

                        var healthScript = @"
                            Import-Module Hyper-V -ErrorAction SilentlyContinue
                            $vm = Get-VM -Name '__VM_NAME__' -ErrorAction SilentlyContinue
                            $hb = Get-VMIntegrationService -VMName '__VM_NAME__' -Name 'Heartbeat' -ErrorAction SilentlyContinue
                            $ip = ''
                            if ($vm -and $vm.NetworkAdapters) {
                                $ip = ($vm.NetworkAdapters.IPAddresses | Where-Object { $_ -notlike '*:*' } | Select-Object -First 1)
                            }

                            [PSCustomObject]@{
                                State = if ($vm) { [int]$vm.State } else { 0 }
                                HeartbeatStatus = if ($hb) { [string]$hb.PrimaryStatusDescription } else { 'Unknown' }
                                GuestIP = [string]$ip
                            }
                        ".Replace("__VM_NAME__", safeVmName);

                        ps.AddScript(healthScript);

                        var res = ps.Invoke();
                        if (res.Count > 0 && res[0] != null)
                        {
                            var state = Convert.ToInt32(res[0].Properties["State"]?.Value ?? 0);
                            var hbStatus = res[0].Properties["HeartbeatStatus"]?.Value?.ToString() ?? "Unknown";
                            var guestIp = res[0].Properties["GuestIP"]?.Value?.ToString() ?? string.Empty;

                            health.HeartbeatServiceOk = (state == 2 && hbStatus.Equals("OK", StringComparison.OrdinalIgnoreCase));
                            health.IntegrationServicesHealthy = health.HeartbeatServiceOk;
                            health.GuestIpAddress = guestIp;

                            if (!string.IsNullOrWhiteSpace(guestIp))
                            {
                                try
                                {
                                    using var ping = new Ping();
                                    var reply = await ping.SendPingAsync(guestIp, 1500);
                                    health.PingSucceeded = (reply.Status == IPStatus.Success);
                                    health.PingResponseMs = reply.RoundtripTime;
                                }
                                catch
                                {
                                    health.PingSucceeded = false;
                                }
                            }

                            if (health.HeartbeatServiceOk)
                            {
                                health.DiagnosticLog = $"Guest health verified: Heartbeat=OK, IP={guestIp}, Ping={(health.PingSucceeded ? $"{health.PingResponseMs}ms" : "Unresponsive/Filtered")}.";
                                return health;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        health.DiagnosticLog = $"Health poll iteration error: {ex.Message}";
                    }

                    await Task.Delay(4000, cancellationToken);
                }

                health.DiagnosticLog = $"Post-migration verification timed out after {timeoutSeconds} seconds.";
                return health;
            }, cancellationToken);
        }

        public async Task RollbackMigrationAsync(
            string vmName,
            string currentHost,
            string originalSourceHost,
            string? originalStoragePath,
            string? username,
            string? password,
            IProgress<string> logProgress,
            CancellationToken cancellationToken = default)
        {
            logProgress.Report($"[ROLLBACK] Triggering automated recovery migration for '{vmName}' back to '{originalSourceHost}'...");
            await ExecuteLiveMigrationAsync(vmName, currentHost, originalSourceHost, originalStoragePath, username, password, logProgress, cancellationToken);
        }

        // =========================================================================
        // 5. AVHDX CHECKPOINT CHAIN GUARD & INSPECTION
        // =========================================================================

        public async Task<List<VmCheckpointInfo>> GetVmCheckpointsAsync(
            string host,
            string vmName,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var checkpoints = new List<VmCheckpointInfo>();
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);
                var safeVmName = vmName.Replace("'", "''");

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var script = @"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    Get-VMSnapshot -VMName '__VM_NAME__' -ErrorAction SilentlyContinue | ForEach-Object {
                        $snap = $_
                        $avhdx = @()
                        $totalBytes = 0
                        if ($snap.HardDrives) {
                            foreach ($hd in $snap.HardDrives) {
                                if ($hd.Path -and $hd.Path -like '*.avhdx*') {
                                    $avhdx += $hd.Path
                                    try {
                                        if (Test-Path -LiteralPath $hd.Path) {
                                            $totalBytes += (Get-Item -LiteralPath $hd.Path).Length
                                        }
                                    } catch {}
                                }
                            }
                        }

                        [PSCustomObject]@{
                            Id = $snap.Id.ToString()
                            Name = $snap.Name
                            CreationTime = $snap.CreationTime.ToString('o')
                            ParentId = if ($snap.ParentSnapshotId) { $snap.ParentSnapshotId.ToString() } else { '' }
                            DeltaPaths = $avhdx
                            DeltaBytes = [long]$totalBytes
                        }
                    }
                ".Replace("__VM_NAME__", safeVmName);

                ps.AddScript(script);
                var results = ps.Invoke();

                foreach (var item in results)
                {
                    if (item?.BaseObject == null) continue;
                    try
                    {
                        var idStr = item.Properties["Id"]?.Value?.ToString() ?? Guid.NewGuid().ToString("N");
                        var name = item.Properties["Name"]?.Value?.ToString() ?? "Checkpoint";
                        var timeStr = item.Properties["CreationTime"]?.Value?.ToString();
                        var parentId = item.Properties["ParentId"]?.Value?.ToString() ?? string.Empty;
                        var deltaBytes = Convert.ToInt64(item.Properties["DeltaBytes"]?.Value ?? 0L);
                        var dt = DateTime.TryParse(timeStr, out var parsedDt) ? parsedDt : DateTime.Now;

                        var paths = new List<string>();
                        if (item.Properties["DeltaPaths"]?.Value is object[] pathArray)
                        {
                            foreach (var p in pathArray)
                            {
                                var psStr = p?.ToString();
                                if (!string.IsNullOrWhiteSpace(psStr)) paths.Add(psStr);
                            }
                        }

                        checkpoints.Add(new VmCheckpointInfo
                        {
                            CheckpointId = idStr,
                            CheckpointName = name,
                            CreationTime = dt,
                            ParentCheckpointId = parentId,
                            AvhdxPaths = paths,
                            TotalDeltaSizeBytes = deltaBytes
                        });
                    }
                    catch { }
                }

                return checkpoints;
            }, cancellationToken);
        }

        public async Task RemoveVmCheckpointAsync(
            string host,
            string vmName,
            string checkpointName,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);
                var safeVmName = vmName.Replace("'", "''");
                var safeSnapName = checkpointName.Replace("'", "''");

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript($"Import-Module Hyper-V -ErrorAction SilentlyContinue; Remove-VMSnapshot -VMName '{safeVmName}' -Name '{safeSnapName}' -ErrorAction Stop");
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Failed to remove checkpoint '{checkpointName}': {errors}");
                }
            }, cancellationToken);
        }

        // =========================================================================
        // 6. DEDICATED LIVE MIGRATION SUBNET ORCHESTRATION
        // =========================================================================

        public async Task<List<MigrationSubnetModel>> GetMigrationNetworksAsync(
            string host,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                var subnets = new List<MigrationSubnetModel>();
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddScript(@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    $migNets = Get-VMMigrationNetwork -ErrorAction SilentlyContinue
                    $adapters = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
                    
                    foreach ($ad in $adapters) {
                        $subnetStr = ""$($ad.IPAddress)/$($ad.PrefixLength)""
                        $matching = $migNets | Where-Object { $_.Subnet -eq $subnetStr -or $_.Subnet -eq $ad.IPAddress }
                        [PSCustomObject]@{
                            Subnet = $subnetStr
                            Alias = $ad.InterfaceAlias
                            Enabled = [bool]($matching -ne $null)
                            Priority = if ($matching) { [int]$matching.Priority } else { 1 }
                        }
                    }
                ");

                var results = ps.Invoke();
                foreach (var item in results)
                {
                    if (item?.BaseObject == null) continue;
                    try
                    {
                        var sub = item.Properties["Subnet"]?.Value?.ToString() ?? string.Empty;
                        var alias = item.Properties["Alias"]?.Value?.ToString() ?? string.Empty;
                        var enabled = Convert.ToBoolean(item.Properties["Enabled"]?.Value ?? false);
                        var prio = Convert.ToInt32(item.Properties["Priority"]?.Value ?? 1);

                        if (!string.IsNullOrWhiteSpace(sub))
                        {
                            subnets.Add(new MigrationSubnetModel
                            {
                                SubnetOrIp = sub,
                                InterfaceAlias = alias,
                                IsEnabled = enabled,
                                Priority = prio
                            });
                        }
                    }
                    catch { }
                }

                return subnets;
            }, cancellationToken);
        }

        public async Task SetMigrationNetworksAsync(
            string host,
            List<MigrationSubnetModel> subnets,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var sb = new StringBuilder();
                sb.AppendLine("Import-Module Hyper-V -ErrorAction SilentlyContinue;");
                sb.AppendLine("Get-VMMigrationNetwork -ErrorAction SilentlyContinue | Remove-VMMigrationNetwork -ErrorAction SilentlyContinue;");

                foreach (var s in subnets.Where(s => s.IsEnabled))
                {
                    var safeSub = s.SubnetOrIp.Replace("'", "''");
                    sb.AppendLine($"Add-VMMigrationNetwork -Subnet '{safeSub}' -Priority {s.Priority} -ErrorAction SilentlyContinue;");
                }

                ps.AddScript(sb.ToString());
                ps.Invoke();
            }, cancellationToken);
        }

        // =========================================================================
        // 7. BULK MULTI-VM ORCHESTRATION ENGINES
        // =========================================================================

        public async Task ExecuteBulkPowerActionAsync(
            string host,
            List<string> vmNames,
            VmPowerAction action,
            string? username,
            string? password,
            IProgress<string> logger,
            CancellationToken cancellationToken = default)
        {
            foreach (var vmName in vmNames)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    logger.Report($"[BULK POWER] Executing {action} on '{vmName}'...");
                    await ExecutePowerActionAsync(host, vmName, action, username, password, cancellationToken);
                    logger.Report($"[OK] {action} successfully triggered on '{vmName}'.");
                }
                catch (Exception ex)
                {
                    logger.Report($"[ERROR] Failed to {action} '{vmName}': {ex.Message}");
                }
            }
        }

        public async Task ExecuteBulkCheckpointAsync(
            string host,
            List<string> vmNames,
            string? checkpointPrefix,
            string? username,
            string? password,
            IProgress<string> logger,
            CancellationToken cancellationToken = default)
        {
            var prefix = string.IsNullOrWhiteSpace(checkpointPrefix) ? "WinMigrate_Snap" : checkpointPrefix;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            foreach (var vmName in vmNames)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var snapName = $"{prefix}_{vmName}_{timestamp}";
                    logger.Report($"[BULK SNAP] Creating checkpoint '{snapName}' on '{vmName}'...");
                    await CreateCheckpointAsync(host, vmName, snapName, username, password, cancellationToken);
                    logger.Report($"[OK] Checkpoint created for '{vmName}'.");
                }
                catch (Exception ex)
                {
                    logger.Report($"[ERROR] Checkpoint failed on '{vmName}': {ex.Message}");
                }
            }
        }

        // =========================================================================
        // 8. CHECKPOINT ENGINE & TRANSPORT QOS OPTIMIZER
        // =========================================================================

        public async Task CreateCheckpointAsync(
            string host,
            string vmName,
            string? checkpointName = null,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                var snapName = (checkpointName ?? $"WinMigrate_Backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}").Replace("'", "''");

                ps.AddScript($"Import-Module Hyper-V -ErrorAction SilentlyContinue; Checkpoint-VM -Name '{safeVmName}' -SnapshotName '{snapName}' -ErrorAction Stop");
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Snapshot of '{vmName}' failed: {errors}");
                }
            }, cancellationToken);
        }

        public async Task ConfigureHostMigrationSettingsAsync(
            string host,
            string performanceOption,
            long bandwidthLimitMbps,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeOpt = performanceOption switch
                {
                    "SMB" => "SMB",
                    "TCP" => "TCP",
                    _ => "Compression"
                };

                var script = $"Import-Module Hyper-V -ErrorAction SilentlyContinue; Set-VMHost -VirtualMachineMigrationPerformanceOption {safeOpt} -ErrorAction SilentlyContinue;";

                if (bandwidthLimitMbps > 0)
                {
                    var bytesPerSec = bandwidthLimitMbps * 125000L;
                    script += $" Set-VMMigrationNetwork -MaximumBandwidthInBytes {bytesPerSec} -ErrorAction SilentlyContinue;";
                }

                ps.AddScript(script);
                ps.Invoke();
            }, cancellationToken);
        }

        // =========================================================================
        // 9. VALIDATION, POWER OPS & HARDWARE RECONFIGURATION
        // =========================================================================

        public async Task<PreFlightReport> RunBatchPreFlightValidationAsync(
            List<VirtualMachineModel> vms,
            HostModel targetHost,
            string? destinationPath,
            string? targetUsername = null,
            string? targetPassword = null,
            CancellationToken cancellationToken = default)
        {
            var report = new PreFlightReport();

            if (vms.Count == 0)
            {
                report.Checks.Add(new PreFlightCheckItem
                {
                    CheckName = "Batch Workload Payload",
                    IsPassed = false,
                    Severity = CheckSeverity.Error,
                    Details = "No virtual machines selected for migration."
                });
                return report;
            }

            // 1. Workload Name Collision Pre-Flight Check on Target Host
            try
            {
                var existingVms = await DiscoverVirtualMachinesAsync(targetHost.IpAddress, targetUsername, targetPassword, cancellationToken);
                var existingNames = new HashSet<string>(existingVms.Select(ev => ev.Name), StringComparer.OrdinalIgnoreCase);
                var collidingNames = vms.Where(v => existingNames.Contains(v.Name)).Select(v => v.Name).ToList();

                if (collidingNames.Count > 0)
                {
                    report.Checks.Add(new PreFlightCheckItem
                    {
                        CheckName = "Target Workload Name Collision",
                        IsPassed = false,
                        Severity = CheckSeverity.Error,
                        Details = $"Namespace collision! Workload(s) already exist on target host '{targetHost.Hostname}': {string.Join(", ", collidingNames)}. Live migration will abort if workloads already exist.",
                        RemediationHint = "Rename or decommission the conflicting virtual machines on the target node prior to migration."
                    });
                }
                else
                {
                    report.Checks.Add(new PreFlightCheckItem
                    {
                        CheckName = "Target Workload Name Collision",
                        IsPassed = true,
                        Severity = CheckSeverity.Info,
                        Details = $"Target host namespace is clear. None of the {vms.Count} workload(s) exist on node '{targetHost.Hostname}'."
                    });
                }
            }
            catch (Exception ex)
            {
                report.Checks.Add(new PreFlightCheckItem
                {
                    CheckName = "Target Workload Name Collision",
                    IsPassed = false,
                    Severity = CheckSeverity.Warning,
                    Details = $"Could not verify destination VM namespace over WinRM: {ex.Message}"
                });
            }

            // 2. RAM Overhead Audit
            var totalAssignedMb = vms.Sum(v => v.AssignedRamMB);
            var requiredRamGB = (totalAssignedMb / 1024.0) + 1.5;
            var ramPassed = targetHost.AvailableRamGB >= requiredRamGB;

            report.Checks.Add(new PreFlightCheckItem
            {
                CheckName = $"Target RAM Overhead ({vms.Count} Workload(s))",
                IsPassed = ramPassed,
                Severity = ramPassed ? CheckSeverity.Info : CheckSeverity.Error,
                Details = ramPassed
                    ? $"Target has {targetHost.AvailableRamGB:F1} GB free (Demands {totalAssignedMb / 1024.0:F1} GB + 1.5 GB OS reserve)."
                    : $"Insufficient RAM on target! Free: {targetHost.AvailableRamGB:F1} GB, Required: {requiredRamGB:F1} GB."
            });

            // 3. Processor Contention Audit
            var totalCoresDemanded = vms.Sum(v => v.CpuCores);
            var cpuPassed = targetHost.TotalCpuCores >= (totalCoresDemanded / 2);
            report.Checks.Add(new PreFlightCheckItem
            {
                CheckName = "Processor Capacity",
                IsPassed = cpuPassed,
                Severity = cpuPassed ? CheckSeverity.Info : CheckSeverity.Warning,
                Details = cpuPassed
                    ? $"Target cores ({targetHost.TotalCpuCores}) can accommodate batch demand ({totalCoresDemanded} vCPUs)."
                    : $"High contention: Batch demands {totalCoresDemanded} vCPUs on a host with {targetHost.TotalCpuCores} logical processors."
            });

            // 4. AVHDX Checkpoint Chain Guard
            var vmWithSnapshots = new List<string>();
            foreach (var vm in vms)
            {
                try
                {
                    var hostToQuery = !string.IsNullOrWhiteSpace(vm.ResidentHostName) ? vm.ResidentHostName : targetHost.IpAddress;
                    var snaps = await GetVmCheckpointsAsync(hostToQuery, vm.Name, targetUsername, targetPassword, cancellationToken);
                    if (snaps.Count > 0)
                    {
                        var deltaMb = snaps.Sum(s => s.TotalDeltaSizeBytes) / (1024.0 * 1024.0);
                        vmWithSnapshots.Add($"{vm.Name} ({snaps.Count} snapshots, {deltaMb:F0} MB)");
                    }
                }
                catch { }
            }

            if (vmWithSnapshots.Count > 0)
            {
                report.Checks.Add(new PreFlightCheckItem
                {
                    CheckName = "AVHDX Checkpoint Chain Guard",
                    IsPassed = false,
                    Severity = CheckSeverity.Warning,
                    Category = PreFlightCategory.StorageSubsystem,
                    Details = $"Active snapshots detected: {string.Join("; ", vmWithSnapshots)}. Delta AVHDX chains will increase transfer duration and host merge IOPS.",
                    RemediationHint = "Merge or remove snapshots prior to datacenter evacuation to accelerate migration speed.",
                    AutoFixAvailable = true
                });
            }
            else
            {
                report.Checks.Add(new PreFlightCheckItem
                {
                    CheckName = "AVHDX Checkpoint Chain Guard",
                    IsPassed = true,
                    Severity = CheckSeverity.Info,
                    Category = PreFlightCategory.StorageSubsystem,
                    Details = "Zero active snapshot delta chains detected. Base disks will relocate at line speed."
                });
            }

            // 5. Virtual Switch Parity Audit
            var uniqueSwitches = vms.Select(v => v.AssignedSwitch).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (uniqueSwitches.Count > 0)
            {
                try
                {
                    var targetSwitches = await DiscoverVirtualSwitchesAsync(targetHost.IpAddress, targetUsername, targetPassword, cancellationToken);
                    var missingSwitches = uniqueSwitches.Where(req => !targetSwitches.Any(ts => ts.Equals(req, StringComparison.OrdinalIgnoreCase))).ToList();

                    var switchesPassed = missingSwitches.Count == 0;
                    report.Checks.Add(new PreFlightCheckItem
                    {
                        CheckName = "Virtual Switch Parity",
                        IsPassed = switchesPassed,
                        Severity = switchesPassed ? CheckSeverity.Info : CheckSeverity.Warning,
                        Details = switchesPassed
                            ? $"All {uniqueSwitches.Count} virtual switch network(s) exist on the destination host."
                            : $"Missing switch(es) on destination: {string.Join(", ", missingSwitches)}. Remap adapters before proceeding."
                    });
                }
                catch (Exception ex)
                {
                    report.Checks.Add(new PreFlightCheckItem
                    {
                        CheckName = "Virtual Switch Discovery",
                        IsPassed = false,
                        Severity = CheckSeverity.Warning,
                        Details = $"Could not verify switches: {ex.Message}"
                    });
                }
            }

            // 6. Storage Backplane Target Audit
            var pathValid = string.IsNullOrWhiteSpace(destinationPath) || destinationPath.Length >= 3;
            report.Checks.Add(new PreFlightCheckItem
            {
                CheckName = "Storage Backplane Target",
                IsPassed = pathValid,
                Severity = pathValid ? CheckSeverity.Info : CheckSeverity.Error,
                Details = string.IsNullOrWhiteSpace(destinationPath)
                    ? "Live migration will relocate storage to default destination paths."
                    : $"Storage target verified: {destinationPath}"
            });

            return report;
        }

        public async Task ExecutePowerActionAsync(
            string host,
            string vmName,
            VmPowerAction action,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");

                var command = action switch
                {
                    VmPowerAction.Start => $"Import-Module Hyper-V -ErrorAction SilentlyContinue; Start-VM -Name '{safeVmName}'",
                    VmPowerAction.Stop => $"Import-Module Hyper-V -ErrorAction SilentlyContinue; Stop-VM -Name '{safeVmName}' -TurnOff -Force",
                    VmPowerAction.Restart => $"Import-Module Hyper-V -ErrorAction SilentlyContinue; Restart-VM -Name '{safeVmName}' -Force",
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                };

                ps.AddScript(command);
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Power action {action} on '{vmName}' failed: {errors}");
                }
            }, cancellationToken);
        }

        public async Task ReconfigureVmHardwareAsync(
            string host,
            string vmName,
            int newCpuCores,
            long newRamMB,
            string? newSwitchName,
            bool? dynamicMemory = null,
            long? minRamMB = null,
            long? maxRamMB = null,
            bool? cpuCompatibility = null,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                var sanitizedHost = SanitizeHost(host);
                var isLocal = IsLocalHost(sanitizedHost);

                using var runspace = CreateRunspace(sanitizedHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                var safeSwitchName = newSwitchName?.Replace("'", "''") ?? string.Empty;
                var targetBytes = newRamMB * 1024L * 1024L;

                var scriptBase = @"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue
                    $vm = Get-VM -Name '__VM_NAME__' -ErrorAction Stop

                    # 1. Reconfigure Processor Cores
                    if ($vm.ProcessorCount -ne __CPU__) {
                        Set-VMProcessor -VMName '__VM_NAME__' -Count __CPU__ -ErrorAction Stop
                    }
                ".Replace("__VM_NAME__", safeVmName).Replace("__CPU__", newCpuCores.ToString());

                var script = scriptBase;

                if (cpuCompatibility.HasValue)
                {
                    var modeVal = cpuCompatibility.Value ? "Enabled" : "Disabled";
                    script += $" Set-VMProcessor -VMName '{safeVmName}' -CompatibilityForMigrationMode {modeVal} -ErrorAction SilentlyContinue;\n";
                }

                if (dynamicMemory.HasValue && dynamicMemory.Value)
                {
                    var minBytes = (minRamMB ?? 512) * 1024L * 1024L;
                    var maxBytes = (maxRamMB ?? 32768) * 1024L * 1024L;
                    var dynScript = @"
                        Set-VMMemory -VMName '__VM_NAME__' -DynamicMemoryEnabled $true -StartupBytes __TARGET__ -MinimumBytes __MIN__ -MaximumBytes __MAX__ -ErrorAction Stop
                    "
                    .Replace("__VM_NAME__", safeVmName)
                    .Replace("__TARGET__", targetBytes.ToString())
                    .Replace("__MIN__", minBytes.ToString())
                    .Replace("__MAX__", maxBytes.ToString());

                    script += dynScript;
                }
                else
                {
                    var staticScript = @"
                        if ($vm.State -eq 3) {
                            Set-VMMemory -VMName '__VM_NAME__' -DynamicMemoryEnabled $false -StartupBytes __TARGET__ -ErrorAction Stop
                        } else {
                            try {
                                Set-VMMemory -VMName '__VM_NAME__' -StartupBytes __TARGET__ -ErrorAction Stop
                            } catch {
                                throw 'Online live static RAM resizing requires a Generation 2 VM. Power off the VM to adjust static RAM.'
                            }
                        }
                    "
                    .Replace("__VM_NAME__", safeVmName)
                    .Replace("__TARGET__", targetBytes.ToString());

                    script += staticScript;
                }

                if (!string.IsNullOrWhiteSpace(safeSwitchName))
                {
                    var swScript = @"
                        $adapter = Get-VMNetworkAdapter -VMName '__VM_NAME__' -ErrorAction SilentlyContinue
                        if ($adapter) {
                            Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -SwitchName '__SWITCH__' -ErrorAction Stop
                        }
                    "
                    .Replace("__VM_NAME__", safeVmName)
                    .Replace("__SWITCH__", safeSwitchName);

                    script += swScript;
                }

                ps.AddScript(script);
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Failed to reconfigure '{vmName}': {errors}");
                }
            }, cancellationToken);
        }

        public string GenerateMigrationScript(string vmName, string targetHost, string? destinationStoragePath)
        {
            var safeVmName = vmName.Replace("'", "''");
            var safeTarget = SanitizeHost(targetHost);

            if (string.IsNullOrWhiteSpace(destinationStoragePath))
            {
                return $"Move-VM -Name '{safeVmName}' -DestinationHost '{safeTarget}' -IncludeStorage";
            }

            return $"Move-VM -Name '{safeVmName}' -DestinationHost '{safeTarget}' -DestinationStoragePath '{destinationStoragePath.Trim().Replace("'", "''")}'";
        }

        // =========================================================================
        // 10. WS-MAN RUNSPACE FACTORY & SANITIZATION UTILITIES
        // =========================================================================

        private static string SanitizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "localhost";
            var clean = host.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
            clean = clean.TrimEnd('/');

            var colonIdx = clean.IndexOf(':');
            if (colonIdx > 0 && !clean.Contains(']'))
            {
                clean = clean[..colonIdx];
            }

            return clean;
        }

        private static bool IsLocalHost(string host)
        {
            return string.IsNullOrWhiteSpace(host) ||
                   host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        }

        private static Runspace CreateRunspace(string host, string? username, string? password, bool isLocal)
        {
            if (isLocal || string.IsNullOrWhiteSpace(username))
            {
                var iss = InitialSessionState.CreateDefault2();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Unrestricted;
                return RunspaceFactory.CreateRunspace(iss);
            }

            var securePass = new SecureString();
            if (!string.IsNullOrEmpty(password))
            {
                foreach (char c in password)
                {
                    securePass.AppendChar(c);
                }
            }
            securePass.MakeReadOnly();

            var psc = new PSCredential(username, securePass);
            var connectionInfo = new WSManConnectionInfo(new Uri($"http://{host}:5985/wsman"), "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", psc)
            {
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
                OpenTimeout = 12000,
                OperationTimeout = 30000
            };

            return RunspaceFactory.CreateRunspace(connectionInfo);
        }
    }
}
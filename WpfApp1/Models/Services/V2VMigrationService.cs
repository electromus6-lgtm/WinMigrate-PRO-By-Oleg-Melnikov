using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Master orchestration service for VMware-to-Hyper-V (V2V) cold migrations.
    /// Automates PowerCLI Export-VApp (snapshot merging), Tools\qemu-img.exe disk conversion,
    /// network UNC streaming, multi-disk storage tiering, anti-lock session isolation,
    /// automatic 0xC03A001A disk decompression, and remote Hyper-V VM provisioning over WinRM.
    /// </summary>
    public sealed class V2VMigrationService
    {
        public static bool IsQemuImgAvailable(out string? qemuPath)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;

            var toolsPath = Path.Combine(appDir, "Tools", "qemu-img.exe");
            if (File.Exists(toolsPath))
            {
                qemuPath = toolsPath;
                return true;
            }

            var lowercaseToolsPath = Path.Combine(appDir, "tools", "qemu-img.exe");
            if (File.Exists(lowercaseToolsPath))
            {
                qemuPath = lowercaseToolsPath;
                return true;
            }

            var rootPath = Path.Combine(appDir, "qemu-img.exe");
            if (File.Exists(rootPath))
            {
                qemuPath = rootPath;
                return true;
            }

            qemuPath = "qemu-img.exe";
            return false;
        }

        /// <summary>
        /// Fully automated end-to-end V2V migration pipeline:
        /// 0. Audit destination Hyper-V node for name collisions before staging
        /// 1. Power off VMware VM via vCenter API (if powered on)
        /// 2. Export and merge all active snapshots via PowerCLI Export-VApp into an isolated timestamped session folder
        /// 3. Convert merged VMDK(s) to dynamic VHDX using Tools\qemu-img.exe with storage tiering (NVMe/HDD)
        /// 4. Auto-sanitize VHDX files (strip NTFS compression, sparse flags, and ReFS integrity streams to fix 0xC03A001A)
        /// 5. Reclaim local staging buffer
        /// 6. Provision matching Generation 1 (BIOS) or Generation 2 (UEFI) VM on Hyper-V with multi-disk array
        /// 7. Power on workload on Hyper-V
        /// </summary>
        public static async Task ExecuteV2VMigrationAsync(
            V2VMigrationJob job,
            VCenterVmModel sourceVm,
            string targetHyperVHost,
            string targetStoragePath,
            string targetSwitchName,
            string? hyperVUser,
            string? hyperVPass,
            vCenterDiscoveryService vCenterService,
            IProgress<string> logger,
            CancellationToken cancellationToken = default)
        {
            job.Status = V2VMigrationStatus.ExportingVmdk;
            job.StartTime = DateTime.Now;

            // Unique Session Isolation Folder: Completely prevents file-lock collisions from previous runs
            var cleanVmName = SanitizeFileName(sourceVm.Name);
            var stagingVmFolder = Path.Combine(Path.GetTempPath(), "WinMigrate_V2V_Staging", $"{cleanVmName}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(stagingVmFolder);

            try
            {
                logger.Report($"[V2V] Commencing Automated V2V Migration for '{sourceVm.Name}' (vCenter ➔ Hyper-V '{targetHyperVHost}')...");
                job.AppendLog($"Initiated automated V2V pipeline for {sourceVm.Name} (Staging: {stagingVmFolder}).");

                // ---------------------------------------------------------
                // PHASE 0: PRE-FLIGHT NAME COLLISION CHECK
                // ---------------------------------------------------------
                job.CurrentStep = "Verifying target Hyper-V host & name collision";
                logger.Report($"[PRE-FLIGHT] Auditing target node '{targetHyperVHost}' for collision with workload '{sourceVm.Name}'...");

                var vmAlreadyExists = await CheckVmExistsAsync(targetHyperVHost, sourceVm.Name, hyperVUser, hyperVPass, cancellationToken);
                if (vmAlreadyExists)
                {
                    var collisionErr = $"Pre-flight check failed: A Virtual Machine named '{sourceVm.Name}' already exists on target Hyper-V host '{targetHyperVHost}'. Rename the VM or remove the existing instance before migrating.";
                    logger.Report($"[PRE-FLIGHT ERROR] {collisionErr}");
                    throw new InvalidOperationException(collisionErr);
                }
                logger.Report($"[PRE-FLIGHT PASSED] Node '{targetHyperVHost}' is clear. No name collision detected.");

                // ---------------------------------------------------------
                // PHASE 1: POWER OFF VMWARE WORKLOAD
                // ---------------------------------------------------------
                if (sourceVm.PowerState == VCenterPowerState.PoweredOn)
                {
                    job.CurrentStep = "Shutting down VMware workload";
                    logger.Report($"[vCENTER] Powering off '{sourceVm.Name}' via vSphere API...");
                    job.AppendLog("Requesting guest power-off via vCenter API.");

                    try
                    {
                        await vCenterService.PowerOffVmAsync(sourceVm.VmId, cancellationToken);
                        logger.Report($"[vCENTER] Workload '{sourceVm.Name}' powered off successfully.");
                        job.AppendLog("VMware workload powered off.");
                    }
                    catch (Exception ex)
                    {
                        logger.Report($"[WARN] Notice powering off VM: {ex.Message}");
                    }
                }

                // ---------------------------------------------------------
                // PHASE 2: POWERCLI EXPORT-VAPP (MERGES ALL SNAPSHOTS)
                // ---------------------------------------------------------
                job.CurrentStep = "Exporting VM & Merging Snapshots (PowerCLI)";
                logger.Report($"[EXPORT] Exporting VMDK and merging snapshot chain via PowerCLI Export-VApp...");
                job.AppendLog("Executing PowerCLI Export-VApp to merge snapshots on the fly.");

                await ExportVmViaPowerCliAsync(
                    vCenterService.ConnectedHost,
                    vCenterService.AuthUsername,
                    vCenterService.AuthPassword,
                    sourceVm.Name,
                    stagingVmFolder,
                    job,
                    logger,
                    cancellationToken);

                // Locate all VMDK files produced by Export-VApp (sorted alphabetically for deterministic disk indexing)
                var vmdkCandidates = Directory.GetFiles(stagingVmFolder, "*_disk*.vmdk", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (vmdkCandidates.Count == 0)
                {
                    vmdkCandidates = Directory.GetFiles(stagingVmFolder, "*.vmdk", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                if (vmdkCandidates.Count == 0)
                {
                    throw new FileNotFoundException($"Export-VApp completed but no VMDK files were found inside '{stagingVmFolder}'.");
                }

                logger.Report($"[EXPORT COMPLETE] Staged {vmdkCandidates.Count} virtual disk(s) for multi-tier conversion.");
                job.AppendLog($"Exported {vmdkCandidates.Count} VMDK disk(s) to staging buffer.");

                // ---------------------------------------------------------
                // PHASE 3: MULTI-DISK STORAGE TIERING & QEMU-IMG CONVERSION
                // ---------------------------------------------------------
                job.Status = V2VMigrationStatus.ConvertingDisk;
                job.CurrentStep = "Converting VMDK(s) to VHDX via qemu-img";

                if (!IsQemuImgAvailable(out var qemuPath) || string.IsNullOrWhiteSpace(qemuPath))
                {
                    throw new FileNotFoundException("qemu-img.exe was not found. Please ensure qemu-img.exe and DLLs are placed in the '\\Tools' folder.");
                }

                var cleanTargetHost = SanitizeHost(targetHyperVHost);
                var isTargetLocal = IsLocalHost(cleanTargetHost);
                var convertedDisks = new List<ConvertedDiskAttachment>();
                var totalDisks = vmdkCandidates.Count;
                var genNum = sourceVm.RecommendedHyperVGeneration == VmGeneration.Generation2 ? 2 : 1;

                for (int i = 0; i < totalDisks; i++)
                {
                    var vmdkPath = vmdkCandidates[i];
                    var diskInfo = i < sourceVm.Disks.Count ? sourceVm.Disks[i] : null;

                    // Resolve tiering storage path: use disk-specific tier if configured, otherwise fallback to global volume
                    var diskTargetStorage = !string.IsNullOrWhiteSpace(diskInfo?.TargetStoragePath)
                        ? diskInfo.TargetStoragePath
                        : targetStoragePath;

                    var vhdxFileName = !string.IsNullOrWhiteSpace(diskInfo?.TargetFileName)
                        ? diskInfo.TargetFileName
                        : $"{sourceVm.Name}_Disk{i}.vhdx";

                    if (!vhdxFileName.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
                    {
                        vhdxFileName += ".vhdx";
                    }

                    string localOrUncTargetVhdx;
                    string hyperVLocalVhdx;

                    if (isTargetLocal)
                    {
                        var targetDir = Path.Combine(diskTargetStorage.TrimEnd('\\'), sourceVm.Name);
                        localOrUncTargetVhdx = Path.Combine(targetDir, vhdxFileName);
                        hyperVLocalVhdx = localOrUncTargetVhdx;
                    }
                    else
                    {
                        if (diskTargetStorage.Length >= 2 && diskTargetStorage[1] == ':')
                        {
                            var driveLetter = diskTargetStorage[0];
                            var pathWithoutDrive = diskTargetStorage.Substring(2).TrimStart('\\');
                            var uncDir = $@"\\{cleanTargetHost}\{driveLetter}$\{pathWithoutDrive}\{sourceVm.Name}";
                            localOrUncTargetVhdx = Path.Combine(uncDir, vhdxFileName);
                        }
                        else if (diskTargetStorage.StartsWith(@"\\"))
                        {
                            localOrUncTargetVhdx = Path.Combine(diskTargetStorage.TrimEnd('\\'), sourceVm.Name, vhdxFileName);
                        }
                        else
                        {
                            localOrUncTargetVhdx = Path.Combine($@"\\{cleanTargetHost}\C$\", diskTargetStorage.TrimStart('\\'), sourceVm.Name, vhdxFileName);
                        }

                        hyperVLocalVhdx = Path.Combine(diskTargetStorage.TrimEnd('\\'), sourceVm.Name, vhdxFileName);
                    }

                    // Calculate controller topology:
                    // Gen 2 (UEFI): All disks attached to SCSI Controller 0 Locations 0, 1, 2...
                    // Gen 1 (BIOS): Disk 0 & 1 on IDE Controller 0 (Loc 0, 1), Disks 2+ on SCSI Controller 0 (Loc 0, 1...)
                    string ctrlType;
                    int ctrlNum = 0;
                    int ctrlLoc;

                    if (genNum == 2)
                    {
                        ctrlType = "SCSI";
                        ctrlLoc = i;
                    }
                    else
                    {
                        if (i < 2)
                        {
                            ctrlType = "IDE";
                            ctrlLoc = i;
                        }
                        else
                        {
                            ctrlType = "SCSI";
                            ctrlLoc = i - 2;
                        }
                    }

                    convertedDisks.Add(new ConvertedDiskAttachment
                    {
                        HyperVLocalVhdxPath = hyperVLocalVhdx,
                        ControllerType = ctrlType,
                        ControllerNumber = ctrlNum,
                        ControllerLocation = ctrlLoc
                    });

                    var diskSizeMb = new FileInfo(vmdkPath).Length / (1024.0 * 1024.0);
                    logger.Report($"[QEMU] [{i + 1}/{totalDisks}] Converting '{Path.GetFileName(vmdkPath)}' ({diskSizeMb:F1} MB) ➔ '{localOrUncTargetVhdx}' [{ctrlType} {ctrlNum}:{ctrlLoc}]...");
                    job.AppendLog($"Disk {i + 1}/{totalDisks} [{ctrlType} {ctrlNum}:{ctrlLoc}] Target: {localOrUncTargetVhdx}");

                    // Granular multi-disk progress tracking between 45% and 85%
                    var progressBase = 45.0 + (i * (40.0 / totalDisks));
                    var progressSpan = 40.0 / totalDisks;

                    await ConvertWithQemuImgAsync(qemuPath, vmdkPath, localOrUncTargetVhdx, job, progressBase, progressSpan, logger, cancellationToken);
                }

                // ---------------------------------------------------------
                // PHASE 4: CLEAN UP STAGING BUFFER
                // ---------------------------------------------------------
                try
                {
                    logger.Report($"[CLEANUP] Reclaiming local staging buffer space...");
                    if (Directory.Exists(stagingVmFolder))
                    {
                        Directory.Delete(stagingVmFolder, true);
                    }
                }
                catch { }

                // ---------------------------------------------------------
                // PHASE 5: PROVISION HYPER-V VM (GEN 1 vs GEN 2 MULTI-DISK)
                // ---------------------------------------------------------
                job.Status = V2VMigrationStatus.ProvisioningHyperV;
                job.CurrentStep = "Provisioning Hyper-V workload";
                job.ProgressPercent = 90.0;

                var gen = sourceVm.RecommendedHyperVGeneration;
                logger.Report($"[HYPER-V] Provisioning {gen} VM '{sourceVm.Name}' on node '{cleanTargetHost}' (vCPUs: {sourceVm.CpuCount}, RAM: {sourceVm.MemoryMB} MB, Disks: {convertedDisks.Count})...");
                job.AppendLog($"Target Hyper-V architecture: {gen} (Firmware parity: {sourceVm.Firmware}). Attaching {convertedDisks.Count} VHDX disk(s).");

                await ProvisionHyperVVmAsync(
                    cleanTargetHost,
                    sourceVm.Name,
                    gen,
                    sourceVm.CpuCount,
                    sourceVm.MemoryMB,
                    convertedDisks,
                    targetSwitchName,
                    hyperVUser,
                    hyperVPass,
                    logger,
                    cancellationToken);

                // ---------------------------------------------------------
                // PHASE 6: POWER ON WORKLOAD ON HYPER-V
                // ---------------------------------------------------------
                logger.Report($"[HYPER-V] Starting converted workload '{sourceVm.Name}' on '{cleanTargetHost}'...");
                try
                {
                    await StartHyperVVmAsync(cleanTargetHost, sourceVm.Name, hyperVUser, hyperVPass, cancellationToken);
                    logger.Report($"[HYPER-V] Workload '{sourceVm.Name}' is powered on and booting.");
                    job.AppendLog("Workload started on Hyper-V.");
                }
                catch (Exception pEx)
                {
                    logger.Report($"[WARN] Notice powering on VM on Hyper-V: {pEx.Message}");
                }

                job.Status = V2VMigrationStatus.Completed;
                job.CurrentStep = "Completed Successfully";
                job.ProgressPercent = 100.0;
                job.EndTime = DateTime.Now;

                logger.Report($"[SUCCESS] V2V Migration completed! Workload '{sourceVm.Name}' is live on Hyper-V '{cleanTargetHost}'.");
                job.AppendLog("V2V migration finished with 100% success.");
            }
            catch (Exception ex)
            {
                job.Status = V2VMigrationStatus.Failed;
                job.CurrentStep = $"Failed: {ex.Message}";
                job.EndTime = DateTime.Now;
                logger.Report($"[V2V ERROR] {ex.Message}");
                job.AppendLog($"[EXCEPTION] {ex.Message}\n{ex.StackTrace}");

                try
                {
                    if (Directory.Exists(stagingVmFolder)) Directory.Delete(stagingVmFolder, true);
                }
                catch { }

                throw;
            }
        }

        /// <summary>
        /// Audits target Hyper-V node over WinRM to verify if a workload with the same name already exists.
        /// Prevents expensive disk downloads and conversions from failing at registration time.
        /// </summary>
        public static async Task<bool> CheckVmExistsAsync(
             string? host,
             string? vmName,
             string? username = null,
             string? password = null,
             CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(vmName))
            {
                return false;
            }

            return await Task.Run(() =>
            {
                Runspace? runspace = null;
                PowerShell? ps = null;
                try
                {
                    var cleanHost = SanitizeHost(host);
                    var isLocal = IsLocalHost(cleanHost);

                    runspace = CreateRunspace(cleanHost, username, password, isLocal);
                    runspace.Open();

                    ps = PowerShell.Create();
                    ps.Runspace = runspace;

                    var safeVmName = vmName.Replace("'", "''");
                    ps.AddScript($@"
                        Import-Module Hyper-V -ErrorAction SilentlyContinue
                        $existing = Get-VM -Name '{safeVmName}' -ErrorAction SilentlyContinue
                        [bool]($existing -ne $null)
                    ");

                    var results = ps.Invoke();
                    if (results.Count > 0 && results[0]?.BaseObject is bool exists)
                    {
                        return exists;
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    try { ps?.Dispose(); } catch { }
                    try { runspace?.Dispose(); } catch { }
                }
            }, ct);
        }

        /// <summary>
        /// Executes PowerCLI Export-VApp with a live non-locking background file-watcher monitoring downloaded megabytes and MB/s throughput.
        /// </summary>
        private static async Task ExportVmViaPowerCliAsync(
            string vCenterHost,
            string vCenterUser,
            string vCenterPass,
            string vmName,
            string destinationFolder,
            V2VMigrationJob job,
            IProgress<string> logger,
            CancellationToken ct)
        {
            await Task.Run(async () =>
            {
                var safeHost = vCenterHost.Replace("'", "''");
                var safeUser = vCenterUser.Replace("'", "''");
                var safePass = vCenterPass.Replace("'", "''");
                var safeVmName = vmName.Replace("'", "''");
                var safeDest = destinationFolder.Replace("'", "''");

                var scriptContent = new StringBuilder();
                scriptContent.AppendLine("$ErrorActionPreference = 'Continue';");
                scriptContent.AppendLine("Add-Type -AssemblyName 'System.Core', 'Microsoft.CSharp' -ErrorAction SilentlyContinue;");
                scriptContent.AppendLine("Import-Module VMware.VimAutomation.Core -ErrorAction SilentlyContinue;");
                scriptContent.AppendLine("Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -ErrorAction SilentlyContinue | Out-Null;");
                scriptContent.AppendLine("$ErrorActionPreference = 'Stop';");
                scriptContent.AppendLine($"$vc = Connect-VIServer -Server '{safeHost}' -User '{safeUser}' -Password '{safePass}' -ErrorAction Stop;");
                scriptContent.AppendLine($"$vm = Get-VM -Name '{safeVmName}' -ErrorAction Stop;");
                scriptContent.AppendLine($"Export-VApp -VM $vm -Destination '{safeDest}' -Format OVF -Force -ErrorAction Stop;");
                scriptContent.AppendLine("Disconnect-VIServer -Server $vc -Confirm:$false -ErrorAction SilentlyContinue;");

                var scriptPath = Path.Combine(destinationFolder, "Export_Workload.ps1");
                File.WriteAllText(scriptPath, scriptContent.ToString(), Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                var errBuilder = new StringBuilder();

                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        logger.Report($"[PowerCLI] {e.Data.Trim()}");
                    }
                };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        errBuilder.AppendLine(e.Data);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var sw = Stopwatch.StartNew();
                var lastSize = 0L;

                try
                {
                    while (!proc.HasExited)
                    {
                        await Task.Delay(1000, ct);

                        try
                        {
                            if (Directory.Exists(destinationFolder))
                            {
                                var files = Directory.GetFiles(destinationFolder, "*.*", SearchOption.AllDirectories);
                                var totalBytes = 0L;

                                foreach (var f in files)
                                {
                                    try
                                    {
                                        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                        totalBytes += fs.Length;
                                    }
                                    catch { }
                                }

                                var totalMb = totalBytes / (1024.0 * 1024.0);
                                var deltaBytes = totalBytes - lastSize;
                                lastSize = totalBytes;
                                var speedMbps = (deltaBytes / (1024.0 * 1024.0));

                                job.ProgressPercent = Math.Clamp(5.0 + (totalMb / 150.0), 5.0, 45.0);
                                if (totalMb > 0)
                                {
                                    logger.Report($"[DOWNLOADING] Staged: {totalMb:F1} MB @ {speedMbps:F1} MB/s");
                                }
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    if (!proc.HasExited)
                    {
                        try { proc.Kill(true); } catch { }
                    }
                }

                sw.Stop();

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"PowerCLI Export-VApp failed (Exit Code {proc.ExitCode}):\n{errBuilder}");
                }

                try { File.Delete(scriptPath); } catch { }
            }, ct);
        }

        /// <summary>
        /// Converts the staged VMDK to dynamic VHDX using Tools\qemu-img.exe with segmented progress scaling.
        /// Automatically uncompresses and strips sparse flags to prevent Hyper-V 0xC03A001A boot failure.
        /// </summary>
        private static async Task ConvertWithQemuImgAsync(
            string qemuPath,
            string sourceFile,
            string destVhdx,
            V2VMigrationJob job,
            double progressBase,
            double progressSpan,
            IProgress<string> logger,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var targetDir = Path.GetDirectoryName(destVhdx);
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                if (!File.Exists(sourceFile))
                {
                    throw new FileNotFoundException($"Source VMDK file not found for conversion: {sourceFile}");
                }

                var qemuArgs = $"convert -p -f vmdk -O vhdx \"{sourceFile}\" \"{destVhdx}\"";
                logger.Report($"[qemu-img] Executing: {qemuPath} {qemuArgs}");

                var psi = new ProcessStartInfo
                {
                    FileName = qemuPath,
                    Arguments = qemuArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var errorLog = new StringBuilder();
                using var proc = new Process { StartInfo = psi };

                proc.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;

                    errorLog.AppendLine(e.Data);

                    var match = Regex.Match(e.Data, @"\(\s*([0-9\.]+)/100%\s*\)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
                    {
                        job.ProgressPercent = Math.Clamp(progressBase + ((pct / 100.0) * progressSpan), 5.0, 89.0);
                    }
                    logger.Report($"[qemu-img] {e.Data.Trim()}");
                };

                proc.Start();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    var details = errorLog.ToString().Trim();
                    throw new InvalidOperationException($"qemu-img conversion failed (Exit Code {proc.ExitCode}):\n{details}");
                }

                // 0xC03A001A Guard: Ensure VHDX is completely uncompressed and not sparse
                try
                {
                    var psiCompact = new ProcessStartInfo
                    {
                        FileName = "compact.exe",
                        Arguments = $"/U /F \"{destVhdx}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var pCompact = Process.Start(psiCompact);
                    pCompact?.WaitForExit(5000);

                    var psiSparse = new ProcessStartInfo
                    {
                        FileName = "fsutil.exe",
                        Arguments = $"sparse setflag \"{destVhdx}\" 0",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var pSparse = Process.Start(psiSparse);
                    pSparse?.WaitForExit(5000);
                }
                catch { }

                logger.Report($"[qemu-img] Converted VHDX generated and uncompressed: {destVhdx}");
            }, ct);
        }

        /// <summary>
        /// Provisions the converted workload on the destination Hyper-V node via WinRM.
        /// Attaches all multi-disk VHDX files to the appropriate IDE/SCSI controllers and locations.
        /// </summary>
        private static async Task ProvisionHyperVVmAsync(
            string host,
            string vmName,
            VmGeneration generation,
            int cpuCount,
            long memoryMb,
            List<ConvertedDiskAttachment> attachedDisks,
            string switchName,
            string? username,
            string? password,
            IProgress<string> logger,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                var safeSwitch = switchName.Replace("'", "''");
                var genNum = generation == VmGeneration.Generation2 ? 2 : 1;
                var memBytes = memoryMb * 1024L * 1024L;

                var diskAttachmentBuilder = new StringBuilder();
                if (genNum == 1)
                {
                    diskAttachmentBuilder.AppendLine(@"
                        $scsiCtrl = Get-VMScsiController -VM $vm -ErrorAction SilentlyContinue
                        if (-not $scsiCtrl) {
                            Add-VMScsiController -VM $vm -ErrorAction SilentlyContinue
                        }
                    ");
                }

                foreach (var d in attachedDisks)
                {
                    var safePath = d.HyperVLocalVhdxPath.Replace("'", "''");

                    // 0xC03A001A Guard on Target Host: Strip NTFS Compression, Remove Sparse flags, and disable ReFS Integrity Streams
                    diskAttachmentBuilder.AppendLine($@"
                        try {{
                            compact.exe /U /F '{safePath}' 2>$null | Out-Null
                            fsutil sparse setflag '{safePath}' 0 2>$null | Out-Null
                            if (Get-Command Set-FileIntegrity -ErrorAction SilentlyContinue) {{
                                Set-FileIntegrity -FileName '{safePath}' -Enable $false -ErrorAction SilentlyContinue
                            }}
                        }} catch {{}}
                    ");

                    diskAttachmentBuilder.AppendLine(
                        $"Add-VMHardDiskDrive -VM $vm -ControllerType {d.ControllerType} -ControllerNumber {d.ControllerNumber} -ControllerLocation {d.ControllerLocation} -Path '{safePath}' -ErrorAction Stop");
                }

                var script = $@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue

                    # 1. Verify VM does not already exist
                    $existing = Get-VM -Name '{safeVmName}' -ErrorAction SilentlyContinue
                    if ($existing) {{
                        throw ""A Virtual Machine named '{safeVmName}' already exists on target node {cleanHost}.""
                    }}

                    # 2. Provision Virtual Machine Shell
                    $vm = New-VM -Name '{safeVmName}' -Generation {genNum} -MemoryStartupBytes {memBytes} -ErrorAction Stop

                    # 3. Configure vCPU Cores & Enable Processor Compatibility
                    Set-VMProcessor -VM $vm -Count {cpuCount} -ErrorAction SilentlyContinue
                    try {{
                        Set-VMProcessor -VM $vm -CompatibilityForMigrationMode MinimumFeatureSet -ErrorAction SilentlyContinue
                    }} catch {{
                        Set-VMProcessor -VM $vm -CompatibilityForOlderOperatingSystemsEnabled $true -ErrorAction SilentlyContinue
                    }}

                    # 4. Attach Multi-Disk VHDX Storage Array
                    {diskAttachmentBuilder}

                    # 5. Connect Virtual Switch
                    if (-not [string]::IsNullOrWhiteSpace('{safeSwitch}')) {{
                        $adapter = Get-VMNetworkAdapter -VM $vm -ErrorAction SilentlyContinue
                        if ($adapter) {{
                            Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -SwitchName '{safeSwitch}' -ErrorAction SilentlyContinue
                        }}
                    }}
                ";

                ps.AddScript(script);
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var err = string.Join(Environment.NewLine, ps.Streams.Error.Select(e => e.ToString()));
                    throw new InvalidOperationException($"Failed to provision VM on Hyper-V node '{cleanHost}': {err}");
                }

                logger.Report($"[HYPER-V] Workload '{vmName}' successfully registered with {attachedDisks.Count} hard disk(s) and wired to '{switchName}' on '{cleanHost}'.");
            }, ct);
        }

        private static async Task StartHyperVVmAsync(
            string host,
            string vmName,
            string? username,
            string? password,
            CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var cleanHost = SanitizeHost(host);
                var isLocal = IsLocalHost(cleanHost);

                using var runspace = CreateRunspace(cleanHost, username, password, isLocal);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                var safeVmName = vmName.Replace("'", "''");
                ps.AddScript($"Import-Module Hyper-V -ErrorAction SilentlyContinue; Start-VM -Name '{safeVmName}' -ErrorAction SilentlyContinue");
                ps.Invoke();
            }, ct);
        }

        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var clean = new string(name.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(clean) ? "Workload" : clean;
        }

        private static string SanitizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "localhost";
            var clean = host.Trim();

            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
            clean = clean.TrimEnd('/');

            var colonIdx = clean.IndexOf(':');
            if (colonIdx > 0 && !clean.Contains(']')) clean = clean[..colonIdx];

            return clean;
        }

        private static bool IsLocalHost(string host)
        {
            return string.IsNullOrWhiteSpace(host) ||
                   host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("127.0.0.1") ||
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
                foreach (char c in password) securePass.AppendChar(c);
            }
            securePass.MakeReadOnly();

            var psc = new PSCredential(username, securePass);
            var connectionInfo = new WSManConnectionInfo(new Uri($"http://{host}:5985/wsman"), "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", psc)
            {
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
                OpenTimeout = 15000,
                OperationTimeout = 30000
            };

            return RunspaceFactory.CreateRunspace(connectionInfo);
        }
    }

    /// <summary>
    /// Represents a converted disk ready for remote attachment to a provisioned Hyper-V guest.
    /// </summary>
    public sealed class ConvertedDiskAttachment
    {
        public string HyperVLocalVhdxPath { get; set; } = string.Empty;
        public string ControllerType { get; set; } = "SCSI";
        public int ControllerNumber { get; set; }
        public int ControllerLocation { get; set; }
    }
}
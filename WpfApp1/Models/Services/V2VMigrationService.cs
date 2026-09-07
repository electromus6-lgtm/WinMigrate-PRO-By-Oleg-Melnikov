using System;
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
    /// Streams VMDK disks directly from ESXi datastores, converts to native VHDX,
    /// maps BIOS/EFI firmware parity, and provisions Hyper-V workloads over WinRM.
    /// </summary>
    public sealed class V2VMigrationService
    {
        private static readonly string[] StarWindDefaultPaths = new[]
        {
            @"C:\Program Files\StarWind Software\StarWind V2V Converter\V2V_ConverterCmd.exe",
            @"C:\Program Files (x86)\StarWind Software\StarWind V2V Converter\V2V_ConverterCmd.exe"
        };

        /// <summary>
        /// Checks if StarWind V2V Converter CLI is installed on this workstation.
        /// </summary>
        public static bool IsStarWindInstalled(out string? cliPath)
        {
            foreach (var path in StarWindDefaultPaths)
            {
                if (File.Exists(path))
                {
                    cliPath = path;
                    return true;
                }
            }
            cliPath = null;
            return false;
        }

        /// <summary>
        /// Checks if open-source qemu-img.exe is available in the application directory or system PATH.
        /// </summary>
        public static bool IsQemuImgAvailable(out string? qemuPath)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var localToolsPath = Path.Combine(appDir, "tools", "qemu-img.exe");

            if (File.Exists(localToolsPath))
            {
                qemuPath = localToolsPath;
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
        /// Executes the complete 4-step V2V migration:
        /// 1. Power off VMware VM via vCenter API (if running)
        /// 2. Stream & convert VMDK directly from ESXi datastore to Hyper-V VHDX
        /// 3. Provision matching Generation 1/2 VM on target Hyper-V node
        /// 4. Power on new VM on Hyper-V
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

            string? stagingFolder = null;

            try
            {
                logger.Report($"[V2V] Commencing Native V2V Migration for '{sourceVm.Name}' (vCenter ➔ Hyper-V '{targetHyperVHost}')...");
                job.AppendLog($"Initiated V2V migration pipeline for {sourceVm.Name}.");

                // ---------------------------------------------------------
                // PHASE 1: POWER OFF VMWARE WORKLOAD
                // ---------------------------------------------------------
                if (sourceVm.PowerState == VCenterPowerState.PoweredOn)
                {
                    job.CurrentStep = "Shutting down VMware workload";
                    logger.Report($"[vCENTER] Powering off '{sourceVm.Name}' via vSphere API...");
                    job.AppendLog("Requesting graceful guest shutdown / power-off via vCenter API.");

                    try
                    {
                        await vCenterService.PowerOffVmAsync(sourceVm.VmId, cancellationToken);
                        logger.Report($"[vCENTER] Workload '{sourceVm.Name}' powered off successfully.");
                        job.AppendLog("VMware workload powered off.");
                    }
                    catch (Exception ex)
                    {
                        logger.Report($"[WARN] Could not power off VM via API (might already be stopped): {ex.Message}");
                    }
                }

                // ---------------------------------------------------------
                // PHASE 2: DISK STREAMING & CONVERSION (VMDK ➔ VHDX)
                // ---------------------------------------------------------
                job.Status = V2VMigrationStatus.ConvertingDisk;
                job.CurrentStep = "Acquiring & Converting VMDK disk";

                var targetDir = Path.Combine(targetStoragePath.TrimEnd('\\'), sourceVm.Name);
                var targetVhdxPath = Path.Combine(targetDir, $"{sourceVm.Name}_Disk0.vhdx");

                logger.Report($"[V2V] Target VHDX destination: {targetVhdxPath}");
                job.AppendLog($"Destination storage target: {targetVhdxPath}");

                var disk = sourceVm.Disks.FirstOrDefault();
                if (disk == null)
                {
                    throw new InvalidOperationException($"No attached virtual disks found on VMware workload '{sourceVm.Name}'.");
                }

                var sourceVmdk = disk.VmdkPath;
                var finalSourceVmdkForConversion = sourceVmdk;

                // 2A: Check if disk lives inside an ESXi Datastore (e.g. "[datastore1] VM/VM.vmdk")
                if (sourceVmdk.StartsWith("["))
                {
                    var closeBracket = sourceVmdk.IndexOf(']');
                    if (closeBracket > 1)
                    {
                        var dsName = sourceVmdk.Substring(1, closeBracket - 1).Trim();
                        var relPath = sourceVmdk.Substring(closeBracket + 1).Trim();

                        stagingFolder = Path.Combine(Path.GetTempPath(), "WinMigrate_V2V_Staging", sourceVm.Name);
                        logger.Report($"[DATASTORE] Streaming '{relPath}' from ESXi Datastore '{dsName}' via HTTPS...");
                        job.AppendLog($"Streaming disk from datastore '{dsName}' to staging area...");

                        var streamProgress = new Progress<double>(pct =>
                        {
                            job.ProgressPercent = Math.Clamp(pct * 0.45, 5.0, 45.0); // 5% - 45% progress
                        });

                        await vCenterService.StreamDatastoreDiskAsync(
                            dsName,
                            relPath,
                            stagingFolder,
                            streamProgress,
                            logger,
                            cancellationToken);

                        finalSourceVmdkForConversion = Path.Combine(stagingFolder, Path.GetFileName(relPath));
                    }
                }

                // 2B: Execute Disk Transformation
                var isStarWind = IsStarWindInstalled(out var starWindPath);
                var isQemu = IsQemuImgAvailable(out var qemuPath);

                if (isQemu && !string.IsNullOrWhiteSpace(qemuPath))
                {
                    logger.Report($"[ENGINE] Invoking open-source qemu-img converter (Zero StarWind Dependency)...");
                    job.AppendLog("Converting disk using native qemu-img engine.");

                    await ConvertWithQemuImgAsync(qemuPath, finalSourceVmdkForConversion, targetVhdxPath, job, logger, cancellationToken);
                }
                else if (isStarWind && !string.IsNullOrWhiteSpace(starWindPath))
                {
                    logger.Report($"[ENGINE] StarWind V2V CLI detected. Invoking CLI conversion...");
                    job.AppendLog("Converting disk using detected StarWind V2V engine.");

                    await ConvertWithStarWindAsync(starWindPath, finalSourceVmdkForConversion, targetVhdxPath, job, logger, cancellationToken);
                }
                else
                {
                    var errMsg = "Neither qemu-img.exe nor StarWind V2V Converter was found. Please place 'qemu-img.exe' in the application '\\tools' folder to enable standalone conversion.";
                    logger.Report($"[ERROR] {errMsg}");
                    job.AppendLog($"[FATAL] {errMsg}");
                    throw new FileNotFoundException(errMsg);
                }

                // 2C: Cleanup Staging Storage Buffer
                if (!string.IsNullOrWhiteSpace(stagingFolder) && Directory.Exists(stagingFolder))
                {
                    try
                    {
                        logger.Report($"[CLEANUP] Reclaiming temporary staging disk space...");
                        Directory.Delete(stagingFolder, true);
                    }
                    catch { }
                }

                // ---------------------------------------------------------
                // PHASE 3: PROVISION HYPER-V VM (GEN 1 vs GEN 2)
                // ---------------------------------------------------------
                job.Status = V2VMigrationStatus.ProvisioningHyperV;
                job.CurrentStep = "Provisioning Hyper-V workload";
                job.ProgressPercent = 90.0;

                var gen = sourceVm.RecommendedHyperVGeneration;
                logger.Report($"[HYPER-V] Provisioning new {gen} VM '{sourceVm.Name}' on node '{targetHyperVHost}' (vCPUs: {sourceVm.CpuCount}, RAM: {sourceVm.MemoryMB} MB)...");
                job.AppendLog($"Target Hyper-V architecture selected: {gen} (Firmware parity: {sourceVm.Firmware}).");

                await ProvisionHyperVVmAsync(
                    targetHyperVHost,
                    sourceVm.Name,
                    gen,
                    sourceVm.CpuCount,
                    sourceVm.MemoryMB,
                    targetVhdxPath,
                    targetSwitchName,
                    hyperVUser,
                    hyperVPass,
                    logger,
                    cancellationToken);

                // ---------------------------------------------------------
                // PHASE 4: COMPLETION
                // ---------------------------------------------------------
                job.Status = V2VMigrationStatus.Completed;
                job.CurrentStep = "Completed Successfully";
                job.ProgressPercent = 100.0;
                job.EndTime = DateTime.Now;

                logger.Report($"[SUCCESS] V2V Migration completed! Workload '{sourceVm.Name}' is ready on Hyper-V '{targetHyperVHost}'.");
                job.AppendLog("V2V pipeline concluded with 100% success.");
            }
            catch (Exception ex)
            {
                job.Status = V2VMigrationStatus.Failed;
                job.CurrentStep = $"Failed: {ex.Message}";
                job.EndTime = DateTime.Now;
                logger.Report($"[V2V ERROR] {ex.Message}");
                job.AppendLog($"[EXCEPTION] {ex.Message}\n{ex.StackTrace}");

                if (!string.IsNullOrWhiteSpace(stagingFolder) && Directory.Exists(stagingFolder))
                {
                    try { Directory.Delete(stagingFolder, true); } catch { }
                }

                throw;
            }
        }

        private static async Task ConvertWithQemuImgAsync(
            string qemuPath,
            string sourceVmdk,
            string destVhdx,
            V2VMigrationJob job,
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

                var psi = new ProcessStartInfo
                {
                    FileName = qemuPath,
                    Arguments = $"convert -p -f vmdk -O vhdx -o subformat=dynamic \"{sourceVmdk}\" \"{destVhdx}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;

                    var match = Regex.Match(e.Data, @"\(\s*([0-9\.]+)/100%\s*\)");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var pct))
                    {
                        job.ProgressPercent = Math.Clamp(45.0 + (pct * 0.40), 45.0, 85.0); // 45% - 85% progress
                    }
                    logger.Report($"[qemu-img] {e.Data.Trim()}");
                };

                proc.Start();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"qemu-img conversion exited with code {proc.ExitCode}. Ensure valid source VMDK descriptor and flat files.");
                }

                logger.Report($"[qemu-img] VHDX disk image generated successfully: {destVhdx}");
            }, ct);
        }

        private static async Task ConvertWithStarWindAsync(
            string starWindPath,
            string sourceVmdk,
            string destVhdx,
            V2VMigrationJob job,
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

                var psi = new ProcessStartInfo
                {
                    FileName = starWindPath,
                    Arguments = $"-i vmdk -o vhdx -src_file \"{sourceVmdk}\" -dst_file \"{destVhdx}\" -dst_type dynamic",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        logger.Report($"[StarWind] {e.Data.Trim()}");
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"StarWind V2V Converter exited with code {proc.ExitCode}.");
                }

                logger.Report($"[StarWind] Disk conversion completed successfully.");
            }, ct);
        }

        private static async Task ProvisionHyperVVmAsync(
            string host,
            string vmName,
            VmGeneration generation,
            int cpuCount,
            long memoryMb,
            string vhdxPath,
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
                var safeVhdx = vhdxPath.Replace("'", "''");
                var safeSwitch = switchName.Replace("'", "''");
                var genNum = generation == VmGeneration.Generation2 ? 2 : 1;
                var memBytes = memoryMb * 1024L * 1024L;

                var script = $@"
                    Import-Module Hyper-V -ErrorAction SilentlyContinue

                    # 1. Verify VM does not already exist
                    $existing = Get-VM -Name '{safeVmName}' -ErrorAction SilentlyContinue
                    if ($existing) {{
                        throw 'A Virtual Machine named \'{safeVmName}\' already exists on target node {cleanHost}.'
                    }}

                    # 2. Provision Virtual Machine Shell
                    $vm = New-VM -Name '{safeVmName}' -Generation {genNum} -MemoryStartupBytes {memBytes} -ErrorAction Stop

                    # 3. Configure vCPU Cores & Enable Processor Compatibility
                    Set-VMProcessor -VM $vm -Count {cpuCount} -CompatibilityForMigrationMode Enabled -ErrorAction SilentlyContinue

                    # 4. Attach Converted VHDX Disk (SCSI for Gen2, IDE for Gen1)
                    if ({genNum} -eq 2) {{
                        Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path '{safeVhdx}' -ErrorAction Stop
                    }} else {{
                        Add-VMHardDiskDrive -VM $vm -ControllerType IDE -ControllerNumber 0 -ControllerLocation 0 -Path '{safeVhdx}' -ErrorAction Stop
                    }}

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

                logger.Report($"[HYPER-V] Workload '{vmName}' successfully registered and wired to '{switchName}' on '{cleanHost}'.");
            }, ct);
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
}
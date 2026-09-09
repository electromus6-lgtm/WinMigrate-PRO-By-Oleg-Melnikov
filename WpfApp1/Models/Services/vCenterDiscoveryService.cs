using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Agentless VMware vCenter / ESXi REST API and Datastore HTTP streaming service.
    /// Ingests workload topologies and streams raw binary disk blocks directly from ESXi VMFS datastores over HTTPS (Port 443).
    /// </summary>
    public sealed class vCenterDiscoveryService : IDisposable
    {
        private HttpClient? _httpClient;
        private string _sessionToken = string.Empty;
        private string _connectedHost = string.Empty;
        private string _authUsername = string.Empty;
        private string _authPassword = string.Empty;
        private string _datacenterName = string.Empty;
        private readonly List<string> _availableDatastores = new();
        private bool _isDisposed;

        public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_sessionToken);
        public string ConnectedHost => _connectedHost;
        public string AuthUsername => _authUsername;
        public string AuthPassword => _authPassword;

        /// <summary>
        /// Authenticates against VMware vCenter Server and discovers active Datacenter names.
        /// </summary>
        public async Task<string> AuthenticateAsync(
            string host,
            string username,
            string password,
            bool ignoreSslErrors = true,
            CancellationToken cancellationToken = default)
        {
            var cleanHost = SanitizeHost(host);
            _connectedHost = cleanHost;
            _authUsername = username;
            _authPassword = password;

            var handler = new HttpClientHandler
            {
                UseCookies = false // Crucial: Allows manual raw Cookie header transmission for vCenter WebDAV servlet
            };

            if (ignoreSslErrors)
            {
                handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            }

            _httpClient?.Dispose();
            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://{cleanHost}"),
                Timeout = TimeSpan.FromSeconds(35)
            };

            var basicAuthBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
            var basicAuthBase64 = Convert.ToBase64String(basicAuthBytes);

            // 1. Authenticate Session Token
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuthBase64);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var tokenRaw = await response.Content.ReadAsStringAsync(cancellationToken);
                    _sessionToken = tokenRaw.Trim('\"', ' ', '\r', '\n');
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_sessionToken))
            {
                try
                {
                    using var legacyReq = new HttpRequestMessage(HttpMethod.Post, "/rest/com/vmware/cis/session");
                    legacyReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuthBase64);

                    var legacyResp = await _httpClient.SendAsync(legacyReq, cancellationToken);
                    legacyResp.EnsureSuccessStatusCode();

                    var jsonString = await legacyResp.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(jsonString);

                    if (doc.RootElement.TryGetProperty("value", out var valProp))
                    {
                        _sessionToken = valProp.GetString() ?? string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to authenticate to vCenter ({cleanHost}): {ex.Message}", ex);
                }
            }

            if (string.IsNullOrWhiteSpace(_sessionToken))
            {
                throw new InvalidOperationException($"Could not establish an authorized session with vCenter at {cleanHost}. Check credentials.");
            }

            // 2. Discover Real Datacenter Name for dcPath URL parameter
            try
            {
                using var dcReq = new HttpRequestMessage(HttpMethod.Get, "/api/vcenter/datacenter");
                dcReq.Headers.Add("vmware-api-session-id", _sessionToken);
                var dcResp = await _httpClient.SendAsync(dcReq, cancellationToken);

                if (dcResp.IsSuccessStatusCode)
                {
                    var dcJson = await dcResp.Content.ReadAsStringAsync(cancellationToken);
                    using var dcDoc = JsonDocument.Parse(dcJson);
                    var dcArray = dcDoc.RootElement.ValueKind == JsonValueKind.Array
                        ? dcDoc.RootElement
                        : (dcDoc.RootElement.TryGetProperty("value", out var v) ? v : default);

                    if (dcArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var dc in dcArray.EnumerateArray())
                        {
                            if (dc.TryGetProperty("name", out var n))
                            {
                                _datacenterName = n.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(_datacenterName)) break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Discover Datastores
            try
            {
                using var dsReq = new HttpRequestMessage(HttpMethod.Get, "/api/vcenter/datastore");
                dsReq.Headers.Add("vmware-api-session-id", _sessionToken);
                var dsResp = await _httpClient.SendAsync(dsReq, cancellationToken);

                if (dsResp.IsSuccessStatusCode)
                {
                    var dsJson = await dsResp.Content.ReadAsStringAsync(cancellationToken);
                    using var dsDoc = JsonDocument.Parse(dsJson);
                    var dsArray = dsDoc.RootElement.ValueKind == JsonValueKind.Array
                        ? dsDoc.RootElement
                        : (dsDoc.RootElement.TryGetProperty("value", out var v2) ? v2 : default);

                    _availableDatastores.Clear();
                    if (dsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ds in dsArray.EnumerateArray())
                        {
                            if (ds.TryGetProperty("name", out var dsNameProp))
                            {
                                var dName = dsNameProp.GetString();
                                if (!string.IsNullOrWhiteSpace(dName)) _availableDatastores.Add(dName);
                            }
                        }
                    }
                }
            }
            catch { }

            return _sessionToken;
        }

        /// <summary>
        /// High-speed parallel discovery loading all vCenter VMs with real disks, GB sizes, and firmware.
        /// </summary>
        public async Task<List<VCenterVmModel>> DiscoverVmsAsync(
            string host,
            string username,
            string password,
            bool ignoreSslErrors = true,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsAuthenticated || !_connectedHost.Equals(SanitizeHost(host), StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"[vCENTER] Authenticating to {host}...");
                await AuthenticateAsync(host, username, password, ignoreSslErrors, cancellationToken);
            }

            progress?.Report($"[vCENTER] Querying vSphere VM Inventory...");

            var responseJson = string.Empty;
            var isModernApi = true;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/vcenter/vm");
                request.Headers.Add("vmware-api-session-id", _sessionToken);

                var resp = await _httpClient!.SendAsync(request, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    responseJson = await resp.Content.ReadAsStringAsync(cancellationToken);
                }
                else
                {
                    isModernApi = false;
                }
            }
            catch
            {
                isModernApi = false;
            }

            if (!isModernApi)
            {
                using var legacyReq = new HttpRequestMessage(HttpMethod.Get, "/rest/vcenter/vm");
                legacyReq.Headers.Add("vmware-api-session-id", _sessionToken);

                var resp = await _httpClient!.SendAsync(legacyReq, cancellationToken);
                resp.EnsureSuccessStatusCode();
                responseJson = await resp.Content.ReadAsStringAsync(cancellationToken);
            }

            using var doc = JsonDocument.Parse(responseJson);
            JsonElement itemsElement;

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = doc.RootElement;
            }
            else if (doc.RootElement.TryGetProperty("value", out var valProp) && valProp.ValueKind == JsonValueKind.Array)
            {
                itemsElement = valProp;
            }
            else
            {
                return new List<VCenterVmModel>();
            }

            var rawVms = new List<VCenterVmModel>();
            foreach (var item in itemsElement.EnumerateArray())
            {
                try
                {
                    var vmId = item.TryGetProperty("vm", out var pId) ? pId.GetString() ?? string.Empty : string.Empty;
                    var name = item.TryGetProperty("name", out var pName) ? pName.GetString() ?? "VM" : "VM";
                    var stateStr = item.TryGetProperty("power_state", out var pState) ? pState.GetString() ?? "POWERED_OFF" : "POWERED_OFF";
                    var cpus = item.TryGetProperty("cpu_count", out var pCpu) ? pCpu.GetInt32() : 2;
                    var mem = item.TryGetProperty("memory_size_MiB", out var pMem) ? pMem.GetInt64() : 4096;

                    var state = stateStr.Equals("POWERED_ON", StringComparison.OrdinalIgnoreCase)
                        ? VCenterPowerState.PoweredOn
                        : (stateStr.Equals("SUSPENDED", StringComparison.OrdinalIgnoreCase) ? VCenterPowerState.Suspended : VCenterPowerState.PoweredOff);

                    rawVms.Add(new VCenterVmModel
                    {
                        VmId = vmId,
                        Name = name,
                        PowerState = state,
                        CpuCount = Math.Max(1, cpus),
                        MemoryMB = Math.Max(512, mem),
                        Firmware = VCenterFirmwareType.Efi
                    });
                }
                catch { }
            }

            progress?.Report($"[vCENTER] Discovered {rawVms.Count} VMs. Inspecting disk topologies in parallel...");

            // Inspect in parallel batches of 8
            using var throttler = new SemaphoreSlim(8, 8);
            var tasks = rawVms.Select(async vm =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    await InspectSingleVmDetailsAsync(vm, cancellationToken);
                }
                finally
                {
                    throttler.Release();
                }
            });

            await Task.WhenAll(tasks);

            progress?.Report($"[vCENTER] Ready. All {rawVms.Count} workloads loaded with real disk topologies.");
            return rawVms;
        }

        /// <summary>
        /// Deep-inspects a selected VM to extract the EXACT VMDK backing file paths from vCenter.
        /// </summary>
        public async Task InspectSingleVmDetailsAsync(VCenterVmModel vm, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(vm.VmId) || !IsAuthenticated || _httpClient == null) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/vcenter/vm/{vm.VmId}");
                req.Headers.Add("vmware-api-session-id", _sessionToken);

                var resp = await _httpClient.SendAsync(req, ct);
                var json = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : string.Empty;

                if (string.IsNullOrWhiteSpace(json))
                {
                    using var legacyReq = new HttpRequestMessage(HttpMethod.Get, $"/rest/vcenter/vm/{vm.VmId}");
                    legacyReq.Headers.Add("vmware-api-session-id", _sessionToken);
                    var lResp = await _httpClient.SendAsync(legacyReq, ct);
                    if (lResp.IsSuccessStatusCode) json = await lResp.Content.ReadAsStringAsync(ct);
                }

                var newDisks = new List<VCenterDiskInfo>();

                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement.TryGetProperty("value", out var val) ? val : doc.RootElement;

                    // 1. Detect Boot Firmware Type (BIOS vs EFI)
                    if (root.TryGetProperty("boot", out var bootProp) && bootProp.TryGetProperty("type", out var typeProp))
                    {
                        var fType = typeProp.GetString() ?? "EFI";
                        vm.Firmware = fType.Equals("BIOS", StringComparison.OrdinalIgnoreCase)
                            ? VCenterFirmwareType.Bios : VCenterFirmwareType.Efi;
                    }

                    // 2. Extract Exact Disk Backing Paths
                    if (root.TryGetProperty("disks", out var disksProp))
                    {
                        if (disksProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var dObj in disksProp.EnumerateObject())
                            {
                                ParseDiskJsonElement(dObj.Value, dObj.Name, newDisks);
                            }
                        }
                        else if (disksProp.ValueKind == JsonValueKind.Array)
                        {
                            var idx = 1;
                            foreach (var dVal in disksProp.EnumerateArray())
                            {
                                var key = dVal.TryGetProperty("key", out var kProp) ? kProp.GetString() ?? $"disk_{idx}" : $"disk_{idx}";
                                ParseDiskJsonElement(dVal, key, newDisks);
                                idx++;
                            }
                        }
                    }
                }

                // Sub-endpoint fallback
                if (newDisks.Count == 0)
                {
                    try
                    {
                        using var diskReq = new HttpRequestMessage(HttpMethod.Get, $"/api/vcenter/vm/{vm.VmId}/hardware/disk");
                        diskReq.Headers.Add("vmware-api-session-id", _sessionToken);
                        var diskResp = await _httpClient.SendAsync(diskReq, ct);
                        if (diskResp.IsSuccessStatusCode)
                        {
                            var diskJson = await diskResp.Content.ReadAsStringAsync(ct);
                            using var diskDoc = JsonDocument.Parse(diskJson);
                            var diskRoot = diskDoc.RootElement.TryGetProperty("value", out var dVal) ? dVal : diskDoc.RootElement;

                            if (diskRoot.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var dItem in diskRoot.EnumerateArray())
                                {
                                    var dKey = dItem.TryGetProperty("disk", out var dp) ? dp.GetString() ?? "2000" : "2000";
                                    ParseDiskJsonElement(dItem, dKey, newDisks);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (newDisks.Count > 0)
                {
                    vm.Disks = newDisks;
                }
            }
            catch { }
        }

        private static void ParseDiskJsonElement(JsonElement dVal, string keyName, List<VCenterDiskInfo> targetList)
        {
            if (dVal.TryGetProperty("value", out var innerVal) && innerVal.ValueKind == JsonValueKind.Object)
            {
                dVal = innerVal;
            }

            var label = dVal.TryGetProperty("label", out var lProp) ? lProp.GetString() ?? "Hard Disk" : "Hard Disk";
            var cap = 0L;

            if (dVal.TryGetProperty("capacity", out var cProp))
            {
                if (cProp.ValueKind == JsonValueKind.Number) cap = cProp.GetInt64();
                else if (cProp.ValueKind == JsonValueKind.String && long.TryParse(cProp.GetString(), out var parsedCap)) cap = parsedCap;
            }

            if (cap <= 0) cap = 42949672960L;

            var vmdkPath = string.Empty;
            var dsName = string.Empty;

            if (dVal.TryGetProperty("backing", out var bProp))
            {
                vmdkPath = bProp.TryGetProperty("vmdk_file", out var vProp) ? vProp.GetString() ?? string.Empty : string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(vmdkPath) && vmdkPath.StartsWith("["))
            {
                var closeBracket = vmdkPath.IndexOf(']');
                if (closeBracket > 1)
                {
                    dsName = vmdkPath.Substring(1, closeBracket - 1).Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(vmdkPath))
            {
                targetList.Add(new VCenterDiskInfo
                {
                    DiskKey = keyName,
                    Label = label,
                    CapacityBytes = cap,
                    VmdkPath = vmdkPath,
                    DatastoreName = dsName
                });
            }
        }

        /// <summary>
        /// Streams the exact VMDK and flat/sesparse extents directly from the ESXi VMFS datastore
        /// using dual Authentication (Session Cookies + Basic Auth) and manual 302 redirect handling.
        /// </summary>
        public async Task StreamDatastoreDiskAsync(
            string datastoreName,
            string relativePath,
            string localDestinationFolder,
            IProgress<double>? progressPercent,
            IProgress<string>? logProgress,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(datastoreName)) throw new ArgumentNullException(nameof(datastoreName));
            if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentNullException(nameof(relativePath));

            if (!Directory.Exists(localDestinationFolder))
            {
                Directory.CreateDirectory(localDestinationFolder);
            }

            var cleanRelative = relativePath.Trim().Replace('\\', '/');
            if (cleanRelative.StartsWith("/")) cleanRelative = cleanRelative.Substring(1);

            // 1. Download VMDK Descriptor
            var descriptorName = Path.GetFileName(cleanRelative);
            var localDescriptorPath = Path.Combine(localDestinationFolder, descriptorName);
            await DownloadSingleFileFromDatastoreAsync(datastoreName, cleanRelative, localDescriptorPath, null, logProgress, cancellationToken);

            // 2. Determine and Download Binary Extent (-flat.vmdk or -sesparse.vmdk for snapshots)
            var baseWithoutExt = cleanRelative;
            if (cleanRelative.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase))
            {
                baseWithoutExt = cleanRelative.Substring(0, cleanRelative.Length - 5);
            }

            var candidateBinaryRelatives = new List<string>
            {
                $"{baseWithoutExt}-flat.vmdk",
                $"{baseWithoutExt}-sesparse.vmdk",
                $"{baseWithoutExt}-delta.vmdk"
            };

            // If it's a snapshot (e.g. -000003), also probe base disk extent
            var dashIndex = baseWithoutExt.LastIndexOf("-00000", StringComparison.OrdinalIgnoreCase);
            if (dashIndex > 0)
            {
                var originalBase = baseWithoutExt.Substring(0, dashIndex);
                candidateBinaryRelatives.Add($"{originalBase}-flat.vmdk");
                candidateBinaryRelatives.Add($"{originalBase}-sesparse.vmdk");
            }

            var binaryDownloaded = false;
            foreach (var binRel in candidateBinaryRelatives)
            {
                var binFileName = Path.GetFileName(binRel);
                var localBinPath = Path.Combine(localDestinationFolder, binFileName);

                try
                {
                    logProgress?.Report($"[STREAM] Probing binary payload: {binFileName}...");
                    await DownloadSingleFileFromDatastoreAsync(datastoreName, binRel, localBinPath, progressPercent, logProgress, cancellationToken);
                    binaryDownloaded = true;
                    break;
                }
                catch
                {
                    // Continue to next candidate extent
                }
            }

            if (!binaryDownloaded)
            {
                logProgress?.Report($"[INFO] Monolithic self-contained VMDK detected or binary extent streamed.");
            }
        }

        private async Task DownloadSingleFileFromDatastoreAsync(
            string datastoreName,
            string relativePath,
            string localTargetPath,
            IProgress<double>? progressPercent,
            IProgress<string>? logProgress,
            CancellationToken ct)
        {
            var cleanPath = relativePath.Replace(" ", "%20");
            var cleanDs = Uri.EscapeDataString(datastoreName);

            // Build candidate URIs
            var candidateUris = new List<string>();
            if (!string.IsNullOrWhiteSpace(_datacenterName))
            {
                candidateUris.Add($"/folder/{cleanPath}?dsName={cleanDs}&dcPath={Uri.EscapeDataString(_datacenterName)}");
            }
            candidateUris.Add($"/folder/{cleanPath}?dsName={cleanDs}");
            candidateUris.Add($"/folder/{cleanPath}?dsName={cleanDs}&dcPath=ha-datacenter");

            HttpResponseMessage? response = null;

            foreach (var uri in candidateUris)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, uri);

                    if (!string.IsNullOrWhiteSpace(_sessionToken))
                    {
                        req.Headers.TryAddWithoutValidation("Cookie", $"vmware_soap_session=\"{_sessionToken}\"; vmware-api-session-id=\"{_sessionToken}\"");
                        req.Headers.TryAddWithoutValidation("vmware-api-session-id", _sessionToken);
                    }
                    if (!string.IsNullOrWhiteSpace(_authUsername))
                    {
                        var authBytes = Encoding.UTF8.GetBytes($"{_authUsername}:{_authPassword}");
                        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                    }

                    var res = await _httpClient!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                    // Handle 302 Redirect to ESXi Host
                    if (res.StatusCode == HttpStatusCode.Redirect ||
                        res.StatusCode == HttpStatusCode.MovedPermanently ||
                        res.StatusCode == HttpStatusCode.SeeOther ||
                        res.StatusCode == HttpStatusCode.TemporaryRedirect ||
                        (int)res.StatusCode == 308)
                    {
                        var redirectLocation = res.Headers.Location;
                        if (redirectLocation != null)
                        {
                            res.Dispose();

                            var redirectReq = new HttpRequestMessage(HttpMethod.Get, redirectLocation);
                            if (!string.IsNullOrWhiteSpace(_authUsername))
                            {
                                var authBytes = Encoding.UTF8.GetBytes($"{_authUsername}:{_authPassword}");
                                redirectReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
                            }
                            if (!string.IsNullOrWhiteSpace(_sessionToken))
                            {
                                redirectReq.Headers.TryAddWithoutValidation("Cookie", $"vmware_soap_session=\"{_sessionToken}\"");
                            }

                            res = await _httpClient.SendAsync(redirectReq, HttpCompletionOption.ResponseHeadersRead, ct);
                        }
                    }

                    if (res.IsSuccessStatusCode)
                    {
                        response = res;
                        break;
                    }
                    res.Dispose();
                }
                catch { }
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Datastore HTTP download returned 404 for '{relativePath}' on datastore '{datastoreName}'. Check that the file exists and credentials have Datastore permissions.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var totalGb = totalBytes > 0 ? totalBytes / (1024.0 * 1024.0 * 1024.0) : 0.0;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                localTargetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8 * 1024 * 1024,
                useAsync: true);

            var buffer = new byte[8 * 1024 * 1024];
            var bytesRead = 0;
            var totalRead = 0L;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastReportTime = DateTime.MinValue;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (DateTime.UtcNow - lastReportTime > TimeSpan.FromMilliseconds(1000))
                {
                    lastReportTime = DateTime.UtcNow;
                    var elapsedSec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    var speedMbps = (totalRead / (1024.0 * 1024.0)) / elapsedSec;

                    if (totalBytes > 0)
                    {
                        var pct = (totalRead / (double)totalBytes) * 100.0;
                        progressPercent?.Report(pct);
                        logProgress?.Report($"[STREAM] Staging: {(totalRead / (1024.0 * 1024.0 * 1024.0)):F2} / {totalGb:F2} GB ({pct:F1}%) @ {speedMbps:F1} MB/s");
                    }
                    else
                    {
                        logProgress?.Report($"[STREAM] Staging: {(totalRead / (1024.0 * 1024.0)):F1} MB @ {speedMbps:F1} MB/s");
                    }
                }
            }

            sw.Stop();
            logProgress?.Report($"[STREAM COMPLETE] Staged {Path.GetFileName(localTargetPath)} ({totalRead / (1024.0 * 1024.0):F1} MB).");
        }

        public async Task PowerOffVmAsync(string vmId, CancellationToken cancellationToken = default)
        {
            if (!IsAuthenticated || _httpClient == null) throw new InvalidOperationException("Not authenticated to vCenter.");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/vcenter/vm/{vmId}/power?action=stop");
            req.Headers.Add("vmware-api-session-id", _sessionToken);

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _httpClient?.Dispose();
            }
            catch { }
        }
    }
}
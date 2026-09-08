using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfApp1.Models;
using WpfApp1.Services;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private readonly HyperVService _hyperVService;
        private readonly InfrastructureDoctorService _doctorService = new();
        private readonly MigrationSchedulerService _schedulerService;
        private readonly DispatcherTimer _telemetryTimer;

        private readonly List<VirtualMachineModel> _selectedBatchVms = []  ;
        // Phase 2: vCenter V2V Importer State
        private readonly vCenterDiscoveryService _vCenterService = new();
        private readonly V2VMigrationService _v2vService = new();
        private readonly ObservableCollection<VCenterVmModel> _vCenterVms = [];
        private VCenterVmModel? _selectedVCenterVm;
        private HostModel? _targetHostCapacity;
        private PreFlightReport? _latestPreFlightReport;
        private VirtualMachineModel? _tuningVm;
        private string _tuningVmHost = string.Empty;

        // Active Checkpoint Management Tracking
        private string _checkpointVmName = string.Empty;
        private string _checkpointHostName = string.Empty;
        private readonly ObservableCollection<VmCheckpointInfo> _vmCheckpoints = [];
        private readonly ObservableCollection<MigrationSubnetModel> _migrationSubnets = [];

        private readonly ObservableCollection<ManagedHostEntry> _managedHosts = [];
        private readonly ObservableCollection<MigrationJobModel> _jobHistory = [];
        private readonly List<AggregatedVmViewItem> _allDiscoveredVms = [];

        private readonly Dictionary<string, string> _sessionPasswordCache = new(StringComparer.OrdinalIgnoreCase);

        private Point _dragStartPoint;
        private bool _isSourceConnected;
        private bool _isTargetConnected;
        private bool _isPollingActive;
        private bool _isMigrationRunning;

        public MainWindow()
        {
            InitializeComponent();
            _hyperVService = new HyperVService();
            _schedulerService = new MigrationSchedulerService(_hyperVService);

            // Wire up scheduler queue events
            _schedulerService.BatchStarted += OnSchedulerBatchStarted;
            _schedulerService.WorkloadFinished += OnSchedulerWorkloadFinished;
            _schedulerService.BatchFinished += OnSchedulerBatchFinished;
            _schedulerService.BatchTriggered += OnSchedulerBatchTriggered;

            ManagedHostsListBox.ItemsSource = _managedHosts;
            JobsListBox.ItemsSource = _jobHistory;
            CheckpointsListBox.ItemsSource = _vmCheckpoints;
            MigrationSubnetsListBox.ItemsSource = _migrationSubnets;

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(6)
            };
            _telemetryTimer.Tick += TelemetryTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var settings = await SettingsService.LoadSettingsAsync();

            TxtSourceHost.Text = settings.LastSourceHost;
            TxtSourceUser.Text = settings.LastSourceUser;
            TxtTargetHost.Text = settings.LastTargetHost;
            TxtTargetUser.Text = settings.LastTargetUser;
            TxtDefaultStoragePath.Text = settings.DefaultStoragePath;
            ChkCredSsp.IsChecked = settings.EnableCredSsp;

            // Load QoS & Performance Settings
            foreach (ComboBoxItem item in CmbMigrationPerformance.Items)
            {
                if (string.Equals(item.Content?.ToString(), settings.MigrationPerformanceOption, StringComparison.OrdinalIgnoreCase))
                {
                    CmbMigrationPerformance.SelectedItem = item;
                    break;
                }
            }
            TxtMigrationBandwidthLimit.Text = settings.MigrationBandwidthLimitMbps.ToString();

            // Initialize Scheduler UI
            DpScheduleDate.SelectedDate = DateTime.Today;
            // Initialize vCenter V2V Importer List & Engine Detector
            VCenterVmListBox.ItemsSource = _vCenterVms;
            var starWindInstalled = V2VMigrationService.IsStarWindInstalled(out var swPath);
            var qemuAvailable = V2VMigrationService.IsQemuImgAvailable(out _);
            TxtV2VEngineStatus.Text = starWindInstalled
                ? $"Engine: StarWind V2V CLI Detected ({swPath})"
                : (qemuAvailable
                    ? "Engine: Open-Source qemu-img.exe Ready"
                    : "Engine: Neither StarWind nor qemu-img found. Place qemu-img.exe in '\\tools' folder.");

            _managedHosts.Clear();
            foreach (var h in settings.SavedHosts)
            {
                _managedHosts.Add(h);
            }

            _telemetryTimer.Start();

            // CLI Deep-Linking
            var cli = App.CliOptions;
            if (!string.IsNullOrWhiteSpace(cli.SourceHost)) TxtSourceHost.Text = cli.SourceHost;
            if (!string.IsNullOrWhiteSpace(cli.SourceUser)) TxtSourceUser.Text = cli.SourceUser;
            if (!string.IsNullOrWhiteSpace(cli.TargetHost)) TxtTargetHost.Text = cli.TargetHost;

            if (cli.AutoConnect && !string.IsNullOrWhiteSpace(TxtSourceHost.Text))
            {
                BtnConnectSource_Click(this, new RoutedEventArgs());
                if (!string.IsNullOrWhiteSpace(TxtTargetHost.Text))
                {
                    BtnConnectTarget_Click(this, new RoutedEventArgs());
                }
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _telemetryTimer.Stop();
            _schedulerService.Dispose();
            await SaveCurrentSettingsAsync();
        }

        private async void TelemetryTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPollingActive || _isMigrationRunning) return;
            _isPollingActive = true;

            try
            {
                if (_isSourceConnected)
                {
                    var sourceHost = TxtSourceHost.Text.Trim();
                    var sourceUser = TxtSourceUser.Text.Trim();
                    _sessionPasswordCache.TryGetValue(sourceHost, out var cachedPass);

                    var sSnap = await TelemetryPollerService.QueryHostTelemetryAsync(
                        sourceHost,
                        string.IsNullOrEmpty(sourceUser) ? null : sourceUser,
                        cachedPass);

                    if (sSnap.RamTotalGB > 0)
                    {
                        SourceTelemetryBar.Visibility = Visibility.Visible;
                        PbSourceCpu.Value = sSnap.CpuUtilizationPercent;
                        TxtSourceCpuGauge.Text = $"{sSnap.CpuUtilizationPercent:F0}%";

                        PbSourceRam.Value = sSnap.RamUtilizationPercent;
                        TxtSourceRamGauge.Text = $"{sSnap.RamUsedGB:F1} / {sSnap.RamTotalGB:F1} GB ({sSnap.RamUtilizationPercent:F0}%)";
                    }
                }

                if (_isTargetConnected && _targetHostCapacity != null)
                {
                    var targetHost = TxtTargetHost.Text.Trim();
                    var targetUser = TxtTargetUser.Text.Trim();
                    _sessionPasswordCache.TryGetValue(targetHost, out var cachedPass);

                    var tSnap = await TelemetryPollerService.QueryHostTelemetryAsync(
                        targetHost,
                        string.IsNullOrEmpty(targetUser) ? null : targetUser,
                        cachedPass);

                    if (tSnap.RamTotalGB > 0)
                    {
                        PbTargetRam.Value = tSnap.RamUtilizationPercent;
                        TxtTargetRamGaugePercent.Text = $"{tSnap.RamUtilizationPercent:F0}% USED ({tSnap.RamUsedGB:F1} GB / {tSnap.RamTotalGB:F1} GB)";
                        TxtTargetRam.Text = (tSnap.RamTotalGB - tSnap.RamUsedGB).ToString("F1");
                    }
                }
            }
            catch
            {
                // Silently ignore telemetry blips
            }
            finally
            {
                _isPollingActive = false;
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb && pb.Template.FindName("placeholder", pb) is TextBlock tb)
            {
                tb.Visibility = string.IsNullOrEmpty(pb.Password) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // =================================================================
        // DRAG AND DROP MIGRATION INTERACTION
        // =================================================================
        private void SourceVmList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
        }

        private void SourceVmList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _selectedBatchVms.Count == 0) return;

            var currentPoint = e.GetPosition(null);
            var diff = _dragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                try
                {
                    DragDrop.DoDragDrop(SourceVmList, _selectedBatchVms, DragDropEffects.Move);
                }
                catch
                {
                    // Drag interrupt fallback
                }
            }
        }

        private void TargetDropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(List<VirtualMachineModel>)) || e.Data.GetDataPresent(typeof(VirtualMachineModel)))
            {
                TargetCardBorder.BorderBrush = (SolidColorBrush)FindResource("BrushCyanAccentHover");
                TargetCardBorder.Background = new SolidColorBrush(Color.FromRgb(15, 35, 55));
                e.Effects = DragDropEffects.Move;
            }
        }

        private void TargetDropZone_DragLeave(object sender, DragEventArgs e)
        {
            TargetCardBorder.BorderBrush = (SolidColorBrush)FindResource("BrushCardBorder");
            TargetCardBorder.Background = (SolidColorBrush)FindResource("BrushCardDark");
        }

        private void TargetDropZone_Drop(object sender, DragEventArgs e)
        {
            TargetCardBorder.BorderBrush = (SolidColorBrush)FindResource("BrushCardBorder");
            TargetCardBorder.Background = (SolidColorBrush)FindResource("BrushCardDark");

            if (_selectedBatchVms.Count > 0)
            {
                if (_targetHostCapacity != null)
                {
                    BtnExecuteMigration_Click(this, new RoutedEventArgs());
                }
                else
                {
                    MessageBox.Show($"{_selectedBatchVms.Count} workload(s) staged for batch migration. Connect and verify the Target Host to proceed.", "Batch Staged", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        // =================================================================
        // ORCHESTRATOR ACTIONS
        // =================================================================
        private async void BtnConnectSource_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtSourceHost.Text.Trim();
            var user = TxtSourceUser.Text.Trim();
            var pass = TxtSourcePass.Password;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Please enter a valid Source Host or IP.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(pass))
            {
                _sessionPasswordCache[host] = pass;
            }

            BtnConnectSource.IsEnabled = false;
            BtnConnectSource.Content = "Discovering VMs...";
            TxtSourceStatus.Text = $"Querying Hyper-V on {host}...";

            try
            {
                var netDiag = await NetworkDiagnosticService.TestHostConnectivityAsync(host);
                SourceNetworkBadge.Visibility = Visibility.Visible;
                TxtSourceNetworkStatus.Text = netDiag.Summary;
                TxtSourceNetworkStatus.Foreground = new BrushConverter().ConvertFromString(netDiag.HexColor) as SolidColorBrush;

                var vms = await _hyperVService.DiscoverVirtualMachinesAsync(host, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(pass) ? null : pass);

                if (vms.Count == 0)
                {
                    TxtSourceStatus.Text = $"Connected to {host}, but no VMs were found.";
                    SourceEmptyState.Visibility = Visibility.Visible;
                    SourceVmList.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SourceVmList.ItemsSource = vms;
                    SourceEmptyState.Visibility = Visibility.Collapsed;
                    SourceVmList.Visibility = Visibility.Visible;
                }

                _isSourceConnected = true;

                if (!_managedHosts.Any(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase)))
                {
                    _managedHosts.Add(new ManagedHostEntry { Hostname = host, Username = user, IsOnline = true });
                }

                await SaveCurrentSettingsAsync();
            }
            catch (Exception ex)
            {
                _isSourceConnected = false;
                MessageBox.Show($"Failed to connect to {host}:\n\n{ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtSourceStatus.Text = "Discovery failed. Check WinRM permissions or host status.";
                SourceEmptyState.Visibility = Visibility.Visible;
                SourceVmList.Visibility = Visibility.Collapsed;
            }
            finally
            {
                BtnConnectSource.IsEnabled = true;
                BtnConnectSource.Content = "Connect & Discover VMs";
            }
        }

        private async void BtnConnectTarget_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtTargetHost.Text.Trim();
            var user = TxtTargetUser.Text.Trim();
            var pass = TxtTargetPass.Password;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Please enter a valid Target Host or IP.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(pass))
            {
                _sessionPasswordCache[host] = pass;
            }

            BtnConnectTarget.IsEnabled = false;
            BtnConnectTarget.Content = "Scanning Capacity & CSVs...";
            TxtTargetStatus.Text = $"Verifying capacity and volumes on {host}...";

            try
            {
                var targetNetDiag = await NetworkDiagnosticService.TestHostConnectivityAsync(host);
                TargetNetworkBadge.Visibility = Visibility.Visible;
                TxtTargetNetworkStatus.Text = targetNetDiag.Summary;
                TxtTargetNetworkStatus.Foreground = new BrushConverter().ConvertFromString(targetNetDiag.HexColor) as SolidColorBrush;

                _targetHostCapacity = await _hyperVService.DiscoverHostCapacityAsync(host, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(pass) ? null : pass);

                TxtTargetNameHeader.Text = $"NODE READY: {_targetHostCapacity.Hostname.ToUpper()}";
                TxtTargetCores.Text = _targetHostCapacity.TotalCpuCores.ToString();
                TxtTargetRam.Text = _targetHostCapacity.AvailableRamGB.ToString("F1");

                var usedRam = Math.Max(0, _targetHostCapacity.TotalRamGB - _targetHostCapacity.AvailableRamGB);
                var ramPercent = _targetHostCapacity.TotalRamGB > 0 ? (usedRam / _targetHostCapacity.TotalRamGB) * 100.0 : 0.0;

                PbTargetRam.Value = ramPercent;
                TxtTargetRamGaugePercent.Text = $"{ramPercent:F0}% USED ({usedRam:F1} GB / {_targetHostCapacity.TotalRamGB:F1} GB)";

                var storageVolumes = await _hyperVService.DiscoverTargetStorageVolumesAsync(host, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(pass) ? null : pass);
                if (storageVolumes.Count > 0)
                {
                    CmbTargetStorageVolumes.ItemsSource = storageVolumes;
                    var defaultVol = storageVolumes.FirstOrDefault(v => v.IsCsv) ?? storageVolumes[0];
                    CmbTargetStorageVolumes.SelectedItem = defaultVol;
                    TxtDefaultStoragePath.Text = defaultVol.Path;
                }

                TargetEmptyState.Visibility = Visibility.Collapsed;
                TargetCapacityPanel.Visibility = Visibility.Visible;
                TxtDestinationStatus.Text = _targetHostCapacity.Hostname;

                _isTargetConnected = true;
                await SaveCurrentSettingsAsync();
            }
            catch (Exception ex)
            {
                _isTargetConnected = false;
                MessageBox.Show($"Failed to verify target {host}:\n\n{ex.Message}", "Target Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtTargetStatus.Text = "Target verification failed.";
                TargetEmptyState.Visibility = Visibility.Visible;
                TargetCapacityPanel.Visibility = Visibility.Collapsed;
                TxtDestinationStatus.Text = "No Target Configured";
            }
            finally
            {
                BtnConnectTarget.IsEnabled = true;
                BtnConnectTarget.Content = "Connect & Verify Target";
            }
        }

        private void CmbTargetStorageVolumes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTargetStorageVolumes.SelectedItem is StorageVolumeModel vol)
            {
                TxtDefaultStoragePath.Text = vol.Path;
            }
        }

        private void SourceVmList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedBatchVms.Clear();
            foreach (var item in SourceVmList.SelectedItems)
            {
                if (item is VirtualMachineModel vm)
                {
                    _selectedBatchVms.Add(vm);
                }
            }

            if (_selectedBatchVms.Count == 0)
            {
                TxtPayloadStatus.Text = "None Selected";
            }
            else if (_selectedBatchVms.Count == 1)
            {
                var vm = _selectedBatchVms[0];
                TxtPayloadStatus.Text = $"{vm.Name} ({vm.AssignedRamMB:N0} MB)";
            }
            else
            {
                var totalRam = _selectedBatchVms.Sum(v => v.AssignedRamMB);
                TxtPayloadStatus.Text = $"{_selectedBatchVms.Count} VMs Selected ({totalRam:N0} MB Total)";
            }
        }

        // =================================================================
        // CPU ARCHITECTURE AUDITOR & 1-CLICK COMPATIBILITY FIX
        // =================================================================
        private async void BtnCpuAudit_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select a VM from the Source list first.", "No Payload Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetHost = TxtTargetHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetHost))
            {
                MessageBox.Show("Please connect and verify a Target Host first.", "No Target Specified", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var vm = _selectedBatchVms[0];
            var sourceHost = TxtSourceHost.Text.Trim();
            _sessionPasswordCache.TryGetValue(sourceHost, out var sPass);

            TxtCpuSourceDetails.Text = $"Source Host ({sourceHost}) CPU: Interrogating...";
            TxtCpuTargetDetails.Text = $"Target Host ({targetHost}) CPU: Interrogating...";
            TxtCpuAuditRecommendation.Text = "Running instruction compatibility audit...";
            CpuCompatibilityModal.Visibility = Visibility.Visible;

            try
            {
                var audit = await _hyperVService.AuditCpuCompatibilityAsync(
                    vm.Name, sourceHost, targetHost, TxtSourceUser.Text.Trim(), sPass);

                TxtCpuSourceDetails.Text = $"Source Host ({audit.SourceHost}): {audit.SourceCpuVendor} - {audit.SourceCpuName}";
                TxtCpuTargetDetails.Text = $"Target Host ({audit.TargetHost}): {audit.TargetCpuVendor} - {audit.TargetCpuName}";
                TxtCpuAuditRecommendation.Text = audit.RecommendationSummary;
                BtnFixCpuCompatibility.IsEnabled = audit.RequiresCompatibilityMode;
            }
            catch (Exception ex)
            {
                TxtCpuAuditRecommendation.Text = $"Audit error: {ex.Message}";
            }
        }

        private void BtnCloseCpuModal_Click(object sender, RoutedEventArgs e)
        {
            CpuCompatibilityModal.Visibility = Visibility.Collapsed;
        }

        private async void BtnFixCpuCompatibility_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0) return;
            var vm = _selectedBatchVms[0];
            var sourceHost = TxtSourceHost.Text.Trim();
            _sessionPasswordCache.TryGetValue(sourceHost, out var sPass);

            BtnFixCpuCompatibility.IsEnabled = false;
            try
            {
                await _hyperVService.EnableProcessorCompatibilityAsync(sourceHost, vm.Name, TxtSourceUser.Text.Trim(), sPass);
                vm.CompatibilityForMigrationModeEnabled = true;
                MessageBox.Show($"Processor Compatibility Mode enabled for '{vm.Name}'. Cross-generation migration is now unlocked!", "CPU Compatibility Applied", MessageBoxButton.OK, MessageBoxImage.Information);
                CpuCompatibilityModal.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to enable Processor Compatibility Mode:\n\n{ex.Message}", "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnFixCpuCompatibility.IsEnabled = true;
            }
        }

        // =================================================================
        // MULTI-vNIC REMAPPING MATRIX
        // =================================================================
        private async void BtnRemapAdapters_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select a VM from the Source list first.", "No Payload Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var vm = _selectedBatchVms[0];
            var targetHost = TxtTargetHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetHost))
            {
                MessageBox.Show("Please connect to a Target Host first so available switches can be discovered.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _sessionPasswordCache.TryGetValue(targetHost, out var tPass);
            try
            {
                var targetSwitches = await _hyperVService.DiscoverVirtualSwitchesAsync(targetHost, TxtTargetUser.Text.Trim(), tPass);
                foreach (var nic in vm.NetworkAdapters)
                {
                    nic.TargetSwitchOptions.Clear();
                    foreach (var s in targetSwitches) nic.TargetSwitchOptions.Add(s);
                    if (string.IsNullOrWhiteSpace(nic.TargetSwitchName) && targetSwitches.Count > 0)
                    {
                        nic.TargetSwitchName = targetSwitches.FirstOrDefault(ts => ts.Equals(nic.SourceSwitchName, StringComparison.OrdinalIgnoreCase)) ?? targetSwitches[0];
                    }
                }
            }
            catch { }

            NicMappingListBox.ItemsSource = vm.NetworkAdapters;
            MultiNicRemapModal.Visibility = Visibility.Visible;
        }

        private void BtnCloseNicModal_Click(object sender, RoutedEventArgs e)
        {
            MultiNicRemapModal.Visibility = Visibility.Collapsed;
        }

        private void BtnApplyNicMappings_Click(object sender, RoutedEventArgs e)
        {
            MultiNicRemapModal.Visibility = Visibility.Collapsed;
            MessageBox.Show("Virtual network adapter bindings saved for migration.", "vNIC Matrix Updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        // DISAGGREGATED MULTI-VHDX STORAGE MATRIX
        // =================================================================
        private void BtnDisaggregateStorage_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select a VM from the Source list first.", "No Payload Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var vm = _selectedBatchVms[0];
            var defaultStorage = TxtDefaultStoragePath.Text.Trim();

            foreach (var disk in vm.HardDisks)
            {
                if (string.IsNullOrWhiteSpace(disk.TargetDirectoryPath))
                {
                    disk.TargetDirectoryPath = defaultStorage;
                }
            }

            DisaggregatedDisksListBox.ItemsSource = vm.HardDisks;
            DisaggregatedStorageModal.Visibility = Visibility.Visible;
        }

        private void BtnCloseDisaggregatedModal_Click(object sender, RoutedEventArgs e)
        {
            DisaggregatedStorageModal.Visibility = Visibility.Collapsed;
        }

        private void BtnApplyDisaggregatedPaths_Click(object sender, RoutedEventArgs e)
        {
            DisaggregatedStorageModal.Visibility = Visibility.Collapsed;
            MessageBox.Show("Disaggregated VHDX destination paths configured.", "Storage Matrix Updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // =================================================================
        // MAINTENANCE WINDOW SCHEDULER & CONCURRENCY THROTTLER
        // =================================================================
        private void BtnScheduleBatch_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select one or more VMs to schedule for migration.", "No Payload Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_targetHostCapacity == null)
            {
                MessageBox.Show("Please connect and verify a Target Host first.", "Target Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtScheduleBatchName.Text = $"Datacenter Evacuation ({_selectedBatchVms.Count} VMs)";
            DpScheduleDate.SelectedDate = DateTime.Today;
            TxtScheduleTime.Text = DateTime.Now.AddHours(2).ToString("HH:mm");
            SchedulerModal.Visibility = Visibility.Visible;
        }

        private void BtnCloseSchedulerModal_Click(object sender, RoutedEventArgs e)
        {
            SchedulerModal.Visibility = Visibility.Collapsed;
        }

        private void BtnConfirmScheduleBatch_Click(object sender, RoutedEventArgs e)
        {
            var date = DpScheduleDate.SelectedDate ?? DateTime.Today;
            if (!TimeSpan.TryParse(TxtScheduleTime.Text.Trim(), out var time))
            {
                MessageBox.Show("Please enter a valid execution time in HH:mm format.", "Invalid Time", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var executionTimeLocal = date.Date + time;
            if (executionTimeLocal <= DateTime.Now)
            {
                executionTimeLocal = executionTimeLocal.AddDays(1);
            }

            int concurrency = int.TryParse(TxtScheduleConcurrency.Text.Trim(), out var c) ? Math.Clamp(c, 1, 8) : 2;

            var batch = new ScheduledMigrationBatch
            {
                BatchName = TxtScheduleBatchName.Text.Trim(),
                ScheduledExecutionUtc = executionTimeLocal.ToUniversalTime(),
                SourceHost = TxtSourceHost.Text.Trim(),
                TargetHost = _targetHostCapacity!.IpAddress,
                ConcurrencyLimit = concurrency,
                AutoRollbackOnHeartbeatFailure = ChkAutoRollback.IsChecked == true,
                HeartbeatTimeoutSeconds = 180,
                VmNames = [.. _selectedBatchVms.Select(v => v.Name)]
            };

            _schedulerService.EnqueueBatch(batch);
            SchedulerModal.Visibility = Visibility.Collapsed;

            MessageBox.Show($"Batch '{batch.BatchName}' enqueued successfully!\nScheduled Execution: {batch.ScheduledLocalTimeText}\nConcurrency Limit: {batch.ConcurrencyLimit}\nAuto-Rollback: {(batch.AutoRollbackOnHeartbeatFailure ? "Enabled" : "Disabled")}", "Batch Enqueued", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSchedulerBatchTriggered(ScheduledMigrationBatch batch)
        {
            Dispatcher.Invoke(async () =>
            {
                var sourceHost = batch.SourceHost;
                _sessionPasswordCache.TryGetValue(sourceHost, out var sPass);

                var workloads = _selectedBatchVms.Where(v => batch.VmNames.Contains(v.Name)).ToList();
                if (workloads.Count == 0)
                {
                    try
                    {
                        var discovered = await _hyperVService.DiscoverVirtualMachinesAsync(sourceHost, TxtSourceUser.Text.Trim(), sPass);
                        workloads = [.. discovered.Where(v => batch.VmNames.Contains(v.Name))];
                    }
                    catch { }
                }

                var progress = new Progress<string>(line =>
                {
                    TxtTerminalLogs.AppendText($"{line}\n");
                    TxtTerminalLogs.ScrollToEnd();
                });

                await _schedulerService.ExecuteBatchAsync(
                    batch,
                    workloads,
                    TxtDefaultStoragePath.Text.Trim(),
                    TxtSourceUser.Text.Trim(),
                    sPass,
                    progress);
            });
        }

        private void OnSchedulerBatchStarted(ScheduledMigrationBatch batch)
        {
            Dispatcher.Invoke(() =>
            {
                NavJobs.IsChecked = true;
                TxtTerminalLogs.AppendText($"\n[SCHEDULER] Automated batch '{batch.BatchName}' triggered at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            });
        }

        private void OnSchedulerWorkloadFinished(ScheduledMigrationBatch batch, string vmName, bool success, string details)
        {
            Dispatcher.Invoke(() =>
            {
                var job = new MigrationJobModel
                {
                    VmName = vmName,
                    SourceHost = batch.SourceHost,
                    TargetHost = batch.TargetHost,
                    Status = success ? JobStatus.Completed : JobStatus.Failed,
                    EndTime = DateTime.Now
                };
                job.AppendLog($"[SCHEDULER] {details}");
                _jobHistory.Insert(0, job);
            });
        }

        private void OnSchedulerBatchFinished(ScheduledMigrationBatch batch)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTerminalLogs.AppendText($"[SCHEDULER] Batch '{batch.BatchName}' execution completed.\n");
            });
        }

        // =================================================================
        // AVHDX CHECKPOINT CHAIN MANAGER MODAL
        // =================================================================
        private async void BtnManageVmCheckpoints_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem aggVm)
            {
                await OpenCheckpointManagerAsync(aggVm.Name, aggVm.ResidentHost);
            }
        }

        private async void BtnCreateVmCheckpoint_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem aggVm)
            {
                _sessionPasswordCache.TryGetValue(aggVm.ResidentHost, out var pass);
                var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(aggVm.ResidentHost, StringComparison.OrdinalIgnoreCase));

                try
                {
                    var snapName = $"WinMigrate_Snap_{DateTime.Now:yyyyMMdd_HHmmss}";
                    await _hyperVService.CreateCheckpointAsync(aggVm.ResidentHost, aggVm.Name, snapName, hostEntry?.Username, pass);
                    MessageBox.Show($"Point-in-time checkpoint '{snapName}' created for '{aggVm.Name}'!", "Checkpoint Created", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create checkpoint for '{aggVm.Name}':\n\n{ex.Message}", "Checkpoint Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task OpenCheckpointManagerAsync(string vmName, string host)
        {
            _checkpointVmName = vmName;
            _checkpointHostName = host;

            TxtCheckpointVmName.Text = $"Workload: {vmName}";
            TxtCheckpointHostName.Text = $"Resident Node: {host}";
            TxtCheckpointChainSummary.Text = "Interrogating snapshots & AVHDX delta disks...";

            CheckpointManagerModal.Visibility = Visibility.Visible;
            await RefreshCheckpointListAsync();
        }

        private async Task RefreshCheckpointListAsync()
        {
            _vmCheckpoints.Clear();
            _sessionPasswordCache.TryGetValue(_checkpointHostName, out var pass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(_checkpointHostName, StringComparison.OrdinalIgnoreCase));

            try
            {
                var snaps = await _hyperVService.GetVmCheckpointsAsync(_checkpointHostName, _checkpointVmName, hostEntry?.Username, pass);
                foreach (var s in snaps) _vmCheckpoints.Add(s);

                var totalBytes = snaps.Sum(s => s.TotalDeltaSizeBytes);
                var totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);

                TxtCheckpointChainSummary.Text = snaps.Count > 0
                    ? $"{snaps.Count} Checkpoints ({totalGb:F1} GB Cumulative Delta)"
                    : "Zero Active Checkpoints (Clean Base Disk)";
            }
            catch (Exception ex)
            {
                TxtCheckpointChainSummary.Text = $"Inspection Error: {ex.Message}";
            }
        }

        private void BtnCloseCheckpointModal_Click(object sender, RoutedEventArgs e)
        {
            CheckpointManagerModal.Visibility = Visibility.Collapsed;
        }

        private async void BtnDeleteCheckpoint_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VmCheckpointInfo snap)
            {
                var confirm = MessageBox.Show(
                    $"Merge and delete checkpoint '{snap.CheckpointName}'?\n\nHyper-V will merge the delta AVHDX disk back into the parent base disk.",
                    "Confirm Checkpoint Merge",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                _sessionPasswordCache.TryGetValue(_checkpointHostName, out var pass);
                var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(_checkpointHostName, StringComparison.OrdinalIgnoreCase));

                try
                {
                    await _hyperVService.RemoveVmCheckpointAsync(_checkpointHostName, _checkpointVmName, snap.CheckpointName, hostEntry?.Username, pass);
                    MessageBox.Show($"Checkpoint '{snap.CheckpointName}' removed and merge routine initiated!", "Merge Completed", MessageBoxButton.OK, MessageBoxImage.Information);
                    await RefreshCheckpointListAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to remove checkpoint:\n\n{ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnCreateNewSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_checkpointVmName)) return;

            _sessionPasswordCache.TryGetValue(_checkpointHostName, out var pass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(_checkpointHostName, StringComparison.OrdinalIgnoreCase));

            try
            {
                var snapName = $"Manual_Snap_{DateTime.Now:yyyyMMdd_HHmmss}";
                await _hyperVService.CreateCheckpointAsync(_checkpointHostName, _checkpointVmName, snapName, hostEntry?.Username, pass);
                await RefreshCheckpointListAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Checkpoint creation failed:\n\n{ex.Message}", "Action Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =================================================================
        // DEDICATED LIVE MIGRATION SUBNETS (VIEW 4)
        // =================================================================
        private async void BtnDetectMigrationSubnets_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtTargetHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                host = TxtSourceHost.Text.Trim();
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Please enter a Source or Target Host IP to scan physical network interfaces.", "Host Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sessionPasswordCache.TryGetValue(host, out var pass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));

            BtnDetectMigrationSubnets.IsEnabled = false;

            try
            {
                var detected = await _hyperVService.GetMigrationNetworksAsync(host, hostEntry?.Username, pass);
                _migrationSubnets.Clear();
                foreach (var d in detected) _migrationSubnets.Add(d);

                MessageBox.Show($"Detected {detected.Count} physical network interface subnet(s) on '{host}'.", "Subnet Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to query migration networks on '{host}':\n\n{ex.Message}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnDetectMigrationSubnets.IsEnabled = true;
            }
        }

        private async void BtnSaveMigrationSubnets_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtTargetHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) host = TxtSourceHost.Text.Trim();

            if (string.IsNullOrWhiteSpace(host) || _migrationSubnets.Count == 0)
            {
                MessageBox.Show("No subnets configured to apply.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _sessionPasswordCache.TryGetValue(host, out var pass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));

            BtnSaveMigrationSubnets.IsEnabled = false;

            try
            {
                await _hyperVService.SetMigrationNetworksAsync(host, [.. _migrationSubnets], hostEntry?.Username, pass);
                MessageBox.Show($"Dedicated live migration network subnets successfully applied to '{host}'!", "Subnets Configured", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to configure migration subnets on '{host}':\n\n{ex.Message}", "Configuration Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSaveMigrationSubnets.IsEnabled = true;
            }
        }

        // =================================================================
        // GLOBAL INVENTORY BULK MULTI-VM ACTIONS
        // =================================================================
        private void AggregatedWorkloadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var count = AggregatedWorkloadsList.SelectedItems.Count;
            if (count >= 2)
            {
                BulkActionBar.Visibility = Visibility.Visible;
                TxtBulkSelectionCount.Text = $"{count} Selected";
            }
            else
            {
                BulkActionBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnBulkStartVm_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteBulkOperationAsync(VmPowerAction.Start);
        }

        private async void BtnBulkStopVm_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteBulkOperationAsync(VmPowerAction.Stop);
        }

        private async void BtnBulkRestartVm_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteBulkOperationAsync(VmPowerAction.Restart);
        }

        private async void BtnBulkCheckpointVm_Click(object sender, RoutedEventArgs e)
        {
            var selected = AggregatedWorkloadsList.SelectedItems.Cast<AggregatedVmViewItem>().ToList();
            if (selected.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Create instant point-in-time checkpoints for {selected.Count} selected workloads?",
                "Confirm Bulk Snapshot",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            NavJobs.IsChecked = true;
            TxtTerminalLogs.AppendText($"\n[BULK CHECKPOINT] Starting snapshot batch across {selected.Count} workloads...\n");

            var progress = new Progress<string>(line =>
            {
                TxtTerminalLogs.AppendText($"{line}\n");
                TxtTerminalLogs.ScrollToEnd();
            });

            var groups = selected.GroupBy(s => s.ResidentHost);
            foreach (var group in groups)
            {
                var host = group.Key;
                var vmNames = group.Select(v => v.Name).ToList();
                _sessionPasswordCache.TryGetValue(host, out var pass);
                var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));

                await _hyperVService.ExecuteBulkCheckpointAsync(host, vmNames, "BulkSnap", hostEntry?.Username, pass, progress);
            }

            MessageBox.Show($"Bulk snapshot routine completed across {selected.Count} workloads!", "Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshInventoryAsync();
        }

        private async Task ExecuteBulkOperationAsync(VmPowerAction action)
        {
            var selected = AggregatedWorkloadsList.SelectedItems.Cast<AggregatedVmViewItem>().ToList();
            if (selected.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Execute '{action}' on all {selected.Count} selected workloads?",
                "Confirm Bulk Power Action",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            NavJobs.IsChecked = true;
            TxtTerminalLogs.AppendText($"\n[BULK POWER] Initiating {action} across {selected.Count} workloads...\n");

            var progress = new Progress<string>(line =>
            {
                TxtTerminalLogs.AppendText($"{line}\n");
                TxtTerminalLogs.ScrollToEnd();
            });

            var groups = selected.GroupBy(s => s.ResidentHost);
            foreach (var group in groups)
            {
                var host = group.Key;
                var vmNames = group.Select(v => v.Name).ToList();
                _sessionPasswordCache.TryGetValue(host, out var pass);
                var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));

                await _hyperVService.ExecuteBulkPowerActionAsync(host, vmNames, action, hostEntry?.Username, pass, progress);
            }

            await Task.Delay(1500);
            await RefreshInventoryAsync();
        }

        // =================================================================
        // ENTERPRISE HARDWARE SPEC TUNER (DYNAMIC MEMORY & NUMA)
        // =================================================================
        private async void BtnEditVmSpecs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VirtualMachineModel vm)
            {
                await OpenVmHardwareTunerAsync(vm, TxtSourceHost.Text.Trim(), TxtSourceUser.Text.Trim());
            }
        }

        private async void BtnEditInventoryVmSpecs_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem aggVm)
            {
                var vmModel = new VirtualMachineModel
                {
                    Id = aggVm.Id,
                    Name = aggVm.Name,
                    Status = aggVm.Status,
                    CpuCores = aggVm.CpuCores,
                    AssignedRamMB = aggVm.MemoryMB,
                    AssignedSwitch = aggVm.AssignedSwitch
                };

                var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(aggVm.ResidentHost, StringComparison.OrdinalIgnoreCase));
                await OpenVmHardwareTunerAsync(vmModel, aggVm.ResidentHost, hostEntry?.Username);
            }
        }

        private async Task OpenVmHardwareTunerAsync(VirtualMachineModel vm, string host, string? user)
        {
            _tuningVm = vm;
            _tuningVmHost = host;

            TxtTunerVmName.Text = $"Workload: {vm.Name}";
            TxtTunerHostName.Text = $"Resident Host: {host}";
            TxtTunerState.Text = vm.Status.ToString().ToUpper();
            TxtTunerState.Foreground = vm.Status == VmOperationalStatus.Running
                ? (SolidColorBrush)FindResource("BrushSuccess")
                : (SolidColorBrush)FindResource("BrushCyanAccentHover");

            TxtTunerCpu.Text = vm.CpuCores.ToString();
            TxtTunerRamMb.Text = vm.AssignedRamMB.ToString();

            // Dynamic Memory & CPU Compatibility
            ChkCpuCompatibilityMode.IsChecked = vm.CompatibilityForMigrationModeEnabled;
            ChkDynamicMemory.IsChecked = vm.DynamicMemoryEnabled;
            TxtTunerMinRamMb.Text = vm.MemoryMinimumMB.ToString();
            TxtTunerMaxRamMb.Text = vm.MemoryMaximumMB.ToString();
            PanelDynamicMemoryBounds.Visibility = vm.DynamicMemoryEnabled ? Visibility.Visible : Visibility.Collapsed;

            // Discover available virtual switches on the resident node
            _sessionPasswordCache.TryGetValue(host, out var pass);
            try
            {
                var switches = await _hyperVService.DiscoverVirtualSwitchesAsync(host, user, pass);
                CmbTunerSwitches.ItemsSource = switches;
                CmbTunerSwitches.SelectedItem = switches.FirstOrDefault(s => s.Equals(vm.AssignedSwitch, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                CmbTunerSwitches.ItemsSource = new List<string> { vm.AssignedSwitch };
                CmbTunerSwitches.SelectedIndex = 0;
            }

            VmHardwareTunerModal.Visibility = Visibility.Visible;
        }

        private void BtnCloseTunerModal_Click(object sender, RoutedEventArgs e)
        {
            VmHardwareTunerModal.Visibility = Visibility.Collapsed;
        }

        private void ChkDynamicMemory_Checked(object sender, RoutedEventArgs e)
        {
            PanelDynamicMemoryBounds.Visibility = Visibility.Visible;
        }

        private void ChkDynamicMemory_Unchecked(object sender, RoutedEventArgs e)
        {
            PanelDynamicMemoryBounds.Visibility = Visibility.Collapsed;
        }

        private void BtnAdd1Gb_Click(object sender, RoutedEventArgs e)
        {
            if (long.TryParse(TxtTunerRamMb.Text.Trim(), out var cur)) TxtTunerRamMb.Text = (cur + 1024).ToString();
        }

        private void BtnAdd2Gb_Click(object sender, RoutedEventArgs e)
        {
            if (long.TryParse(TxtTunerRamMb.Text.Trim(), out var cur)) TxtTunerRamMb.Text = (cur + 2048).ToString();
        }

        private void BtnAdd4Gb_Click(object sender, RoutedEventArgs e)
        {
            if (long.TryParse(TxtTunerRamMb.Text.Trim(), out var cur)) TxtTunerRamMb.Text = (cur + 4096).ToString();
        }

        private async void BtnApplyVmSpecs_Click(object sender, RoutedEventArgs e)
        {
            if (_tuningVm == null || string.IsNullOrWhiteSpace(_tuningVmHost)) return;

            if (!int.TryParse(TxtTunerCpu.Text.Trim(), out var newCpu) || newCpu < 1)
            {
                MessageBox.Show("Please enter a valid vCPU count (minimum 1).", "Invalid vCPU", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!long.TryParse(TxtTunerRamMb.Text.Trim(), out var newRamMb) || newRamMb < 512)
            {
                MessageBox.Show("Please enter a valid RAM allocation (minimum 512 MB).", "Invalid RAM", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dynMem = ChkDynamicMemory.IsChecked == true;
            long? minRam = long.TryParse(TxtTunerMinRamMb.Text.Trim(), out var minVal) ? minVal : 512;
            long? maxRam = long.TryParse(TxtTunerMaxRamMb.Text.Trim(), out var maxVal) ? maxVal : 32768;
            var cpuCompat = ChkCpuCompatibilityMode.IsChecked == true;

            var newSwitch = CmbTunerSwitches.SelectedItem?.ToString() ?? string.Empty;
            _sessionPasswordCache.TryGetValue(_tuningVmHost, out var pass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(_tuningVmHost, StringComparison.OrdinalIgnoreCase));

            BtnApplyVmSpecs.IsEnabled = false;

            try
            {
                await _hyperVService.ReconfigureVmHardwareAsync(
                    _tuningVmHost,
                    _tuningVm.Name,
                    newCpu,
                    newRamMb,
                    newSwitch,
                    dynMem,
                    minRam,
                    maxRam,
                    cpuCompat,
                    hostEntry?.Username,
                    pass);

                _tuningVm.CpuCores = newCpu;
                _tuningVm.AssignedRamMB = newRamMb;
                _tuningVm.AssignedSwitch = newSwitch;
                _tuningVm.DynamicMemoryEnabled = dynMem;
                _tuningVm.CompatibilityForMigrationModeEnabled = cpuCompat;

                MessageBox.Show($"Hardware specs for '{_tuningVm.Name}' updated successfully on '{_tuningVmHost}'!", "Tuning Applied", MessageBoxButton.OK, MessageBoxImage.Information);
                VmHardwareTunerModal.Visibility = Visibility.Collapsed;

                if (_isSourceConnected && _tuningVmHost.Equals(TxtSourceHost.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    BtnConnectSource_Click(this, new RoutedEventArgs());
                }
                await RefreshInventoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hardware reconfiguration failed:\n\n{ex.Message}", "Tuning Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnApplyVmSpecs.IsEnabled = true;
            }
        }

        // =================================================================
        // RUNBOOK PREVIEW & SCRIPT MODAL
        // =================================================================
        private void BtnPreviewScript_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select one or more VMs from the Source list first.", "No Payload Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targetHost = TxtTargetHost.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetHost))
            {
                MessageBox.Show("Please specify and connect to a Target Host first.", "No Target Specified", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sourceHost = TxtSourceHost.Text.Trim();
            var storagePath = TxtDefaultStoragePath.Text.Trim();

            var runbook = GenerateBatchProductionRunbook(_selectedBatchVms, sourceHost, targetHost, storagePath);
            TxtScriptPreviewCode.Text = runbook;
            ScriptPreviewModal.Visibility = Visibility.Visible;
        }

        private void BtnCloseScriptModal_Click(object sender, RoutedEventArgs e)
        {
            ScriptPreviewModal.Visibility = Visibility.Collapsed;
        }

        private void BtnCopyScript_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(TxtScriptPreviewCode.Text);
            MessageBox.Show("Batch PowerShell Runbook copied to clipboard!", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnSaveScriptFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PowerShell Script (*.ps1)|*.ps1|All Files (*.*)|*.*",
                FileName = $"BatchMigrate_{_selectedBatchVms.Count}VMs_{DateTime.Now:yyyyMMdd_HHmm}.ps1",
                Title = "Export Batch PowerShell Runbook"
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, TxtScriptPreviewCode.Text);
                MessageBox.Show($"Batch runbook saved to:\n{dialog.FileName}", "Export Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static string GenerateBatchProductionRunbook(List<VirtualMachineModel> vms, string sourceHost, string targetHost, string storagePath)
        {
            var storageParam = string.IsNullOrWhiteSpace(storagePath)
                ? "-IncludeStorage"
                : $"-DestinationStoragePath \"{storagePath}\"";

            var vmListFormatted = string.Join(", ", vms.Select(v => $"'{v.Name}'"));

            return $@"<#
.SYNOPSIS
    Automated Multi-VM Batch Live Migration Runbook
    Lead Architect: Oleg Melnikov
    Organization:   ElectroMU Gaming Network
    Console:        WinMigrate Pro (ElectroMU Edition)

.DESCRIPTION
    Executes sequential Live Migration across {vms.Count} workloads with transcript logging.
    Source Host:    {sourceHost}
    Target Host:    {targetHost}
    Storage:        {storagePath}
    Payload:        {vms.Count} Virtual Machines
    License:        MIT License (Open Source)
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$logPath = ""$env:TEMP\WinMigrate_Batch_{vms.Count}VMs_$timestamp.log""
$workloads = @({vmListFormatted})

Start-Transcript -Path $logPath -Append

Write-Host ""======================================================="" -ForegroundColor Cyan
Write-Host ""  WinMigrate Pro (ElectroMU Edition): Migration Runbook"" -ForegroundColor Cyan
Write-Host ""  Author: Oleg Melnikov (ElectroMU Gaming Network)     "" -ForegroundColor Cyan
Write-Host ""======================================================="" -ForegroundColor Cyan
Write-Host ""[INFO] Batch Size:      $($workloads.Count) Workloads"" -ForegroundColor Gray
Write-Host ""[INFO] Source Host:     {sourceHost}"" -ForegroundColor Gray
Write-Host ""[INFO] Target Host:     {targetHost}"" -ForegroundColor Gray
Write-Host ""[INFO] Transcript Log:  $logPath"" -ForegroundColor Gray
Write-Host """"

try {{
    if (-not (Get-Module -ListAvailable -Name Hyper-V)) {{
        throw 'Hyper-V PowerShell Module is not installed on the executing node.'
    }}
    Import-Module Hyper-V -ErrorAction SilentlyContinue

    Write-Host ""[PRE-FLIGHT] Verifying target compute node '{targetHost}'..."" -ForegroundColor Yellow
    $targetNode = Get-VMHost -ComputerName '{targetHost}' -ErrorAction Stop
    Write-Host ""[PRE-FLIGHT] Target node verified: $($targetNode.FullyQualifiedDomainName)"" -ForegroundColor Green
    Write-Host """"

    $index = 1
    foreach ($vmName in $workloads) {{
        Write-Host ""-------------------------------------------------------"" -ForegroundColor Cyan
        Write-Host ""[$index/$($workloads.Count)] Relocating: $vmName"" -ForegroundColor Cyan
        Write-Host ""-------------------------------------------------------"" -ForegroundColor Cyan

        $vm = Get-VM -ComputerName '{sourceHost}' -Name $vmName
        Write-Host ""[STATUS] State: $($vm.State) | Memory: $([Math]::Round($vm.MemoryAssigned / 1MB)) MB"" -ForegroundColor Gray

        Write-Host ""[MIGRATE] Invoking Move-VM pipeline..."" -ForegroundColor Green
        Move-VM -ComputerName '{sourceHost}' -Name $vmName -DestinationHost '{targetHost}' {storageParam} -Verbose

        Write-Host ""[SUCCESS] Workload '$vmName' successfully relocated."" -ForegroundColor Green
        Write-Host """"
        $index++
    }}

    Write-Host ""======================================================="" -ForegroundColor Green
    Write-Host ""  All $($workloads.Count) Workload Migrations Completed Successfully! "" -ForegroundColor Green
    Write-Host ""======================================================="" -ForegroundColor Green
}}
catch {{
    Write-Error ""[BATCH FAILED] Migration interrupted with exception: $_""
    throw $_
}}
finally {{
    Stop-Transcript
    Write-Host ""[INFO] Batch session concluded. Review audit transcript at: $logPath"" -ForegroundColor Gray
}}";
        }

        // =================================================================
        // PRE-FLIGHT & ENTERPRISE BATCH LIVE MIGRATION EXECUTION
        // =================================================================
        private async void BtnExecuteMigration_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBatchVms.Count == 0)
            {
                MessageBox.Show("Please select one or more VMs to migrate.", "Action Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_targetHostCapacity == null)
            {
                MessageBox.Show("Please connect and verify a Target Host first.", "Action Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var storagePath = TxtDefaultStoragePath.Text.Trim();

            _latestPreFlightReport = await _hyperVService.RunBatchPreFlightValidationAsync(
                _selectedBatchVms,
                _targetHostCapacity,
                storagePath,
                TxtTargetUser.Text.Trim(),
                TxtTargetPass.Password);

            PreFlightCheckList.ItemsSource = _latestPreFlightReport.Checks;
            BtnProceedMigration.IsEnabled = _latestPreFlightReport.CanProceed;
            PreFlightModal.Visibility = Visibility.Visible;
        }

        private void BtnClosePreFlight_Click(object sender, RoutedEventArgs e)
        {
            PreFlightModal.Visibility = Visibility.Collapsed;
        }

        private async void BtnProceedMigration_Click(object sender, RoutedEventArgs e)
        {
            PreFlightModal.Visibility = Visibility.Collapsed;

            if (_selectedBatchVms.Count == 0 || _targetHostCapacity == null) return;

            var sourceHost = TxtSourceHost.Text.Trim();
            var targetHost = _targetHostCapacity.IpAddress;
            var storagePath = TxtDefaultStoragePath.Text.Trim();
            var user = TxtSourceUser.Text.Trim();
            var pass = TxtSourcePass.Password;

            NavJobs.IsChecked = true;
            TxtTerminalLogs.Text = $"[BATCH START] Initiating enterprise live migration sequence ({_selectedBatchVms.Count} workloads) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";

            BtnExecuteMigration.IsEnabled = false;
            _isMigrationRunning = true;

            // Apply host QoS & Migration Performance Settings
            var perfOption = (CmbMigrationPerformance.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Compression";
            var bandwidthLimit = long.TryParse(TxtMigrationBandwidthLimit.Text.Trim(), out var bw) ? bw : 0;

            try
            {
                await _hyperVService.ConfigureHostMigrationSettingsAsync(sourceHost, perfOption, bandwidthLimit, user, pass);
                TxtTerminalLogs.AppendText($"[QoS] Configured Live Migration: Mode={perfOption}, BandwidthLimit={(bandwidthLimit == 0 ? "Unlimited" : $"{bandwidthLimit} Mbps")}\n");
            }
            catch (Exception qosEx)
            {
                TxtTerminalLogs.AppendText($"[QoS WARN] Could not apply host QoS: {qosEx.Message}\n");
            }

            try
            {
                var batchIndex = 1;
                var totalVms = _selectedBatchVms.Count;

                foreach (var vm in _selectedBatchVms.ToList())
                {
                    var job = new MigrationJobModel
                    {
                        VmName = vm.Name,
                        SourceHost = sourceHost,
                        TargetHost = targetHost,
                        Status = JobStatus.Running
                    };

                    _jobHistory.Insert(0, job);
                    JobsListBox.SelectedItem = job;

                    TxtTerminalLogs.AppendText($"=======================================================\n");
                    TxtTerminalLogs.AppendText($"[{batchIndex}/{totalVms}] Relocating Workload: {vm.Name}\n");
                    TxtTerminalLogs.AppendText($"=======================================================\n");

                    var progress = new Progress<string>(line =>
                    {
                        job.AppendLog(line);
                        if (JobsListBox.SelectedItem == job)
                        {
                            TxtTerminalLogs.AppendText($"{line}\n");
                            TxtTerminalLogs.ScrollToEnd();
                        }
                    });

                    try
                    {
                        await _hyperVService.ExecuteEnterpriseLiveMigrationAsync(
                            vm.Name,
                            sourceHost,
                            targetHost,
                            storagePath,
                            [.. vm.NetworkAdapters],
                            [.. vm.HardDisks],
                            user,
                            pass,
                            progress);

                        job.Status = JobStatus.Completed;
                        job.EndTime = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        job.Status = JobStatus.Failed;
                        job.EndTime = DateTime.Now;
                        TxtTerminalLogs.AppendText($"[ERROR] Workload '{vm.Name}' failed: {ex.Message}\n\n");
                    }

                    batchIndex++;
                }

                MessageBox.Show($"Enterprise live migration completed for {totalVms} workload(s)!", "Batch Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                _isMigrationRunning = false;
                BtnExecuteMigration.IsEnabled = true;
            }
        }

        private void JobsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JobsListBox.SelectedItem is MigrationJobModel job)
            {
                TxtTerminalLogs.Text = job.AllLogs;
            }
        }

        private void BtnClearJobs_Click(object sender, RoutedEventArgs e)
        {
            _jobHistory.Clear();
            TxtTerminalLogs.Text = "Job history cleared. Select a running or completed task to view logs.";
        }

        private void BtnExportAuditReport_Click(object sender, RoutedEventArgs e)
        {
            if (_jobHistory.Count == 0)
            {
                MessageBox.Show("No migration tasks to export. Execute one or more live migrations first.", "Audit Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "HTML Compliance Report (*.html)|*.html|All Files (*.*)|*.*",
                FileName = $"WinMigrate_AuditReport_{DateTime.Now:yyyyMMdd_HHmm}.html",
                Title = "Export Migration Audit Report"
            };

            if (dialog.ShowDialog() == true)
            {
                var html = AuditReportService.GenerateHtmlAuditReport(_jobHistory);
                File.WriteAllText(dialog.FileName, html);
                MessageBox.Show($"Audit compliance report saved successfully:\n\n{dialog.FileName}", "Export Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // =================================================================
        // INFRASTRUCTURE DOCTOR
        // =================================================================
        private async void BtnInfraDoctor_Click(object sender, RoutedEventArgs e)
        {
            var sourceHost = TxtSourceHost.Text.Trim();
            var targetHost = TxtTargetHost.Text.Trim();
            var storagePath = TxtDefaultStoragePath.Text.Trim();

            if (string.IsNullOrWhiteSpace(sourceHost) || string.IsNullOrWhiteSpace(targetHost))
            {
                MessageBox.Show("Please specify both a Source and Target host first.", "Host Specification Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            InfraDoctorModal.Visibility = Visibility.Visible;
            await RunDoctorAuditAsync();
        }

        private void BtnCloseDoctorModal_Click(object sender, RoutedEventArgs e)
        {
            InfraDoctorModal.Visibility = Visibility.Collapsed;
        }

        private async void BtnReRunAudit_Click(object sender, RoutedEventArgs e)
        {
            await RunDoctorAuditAsync();
        }

        private async Task RunDoctorAuditAsync()
        {
            var sourceHost = TxtSourceHost.Text.Trim();
            var targetHost = TxtTargetHost.Text.Trim();
            var storagePath = TxtDefaultStoragePath.Text.Trim();

            _sessionPasswordCache.TryGetValue(sourceHost, out var sPass);
            _sessionPasswordCache.TryGetValue(targetHost, out var tPass);

            TxtDocSourceHost.Text = $"Source: {sourceHost}";
            TxtDocTargetHost.Text = $"Target: {targetHost}";
            TxtDoctorLogs.Text = $"[AUDIT] Interrogating Hyper-V Live Migration settings on {sourceHost} and {targetHost}...\n";

            try
            {
                var audit = await _doctorService.AuditHostPairAsync(
                    sourceHost, targetHost,
                    TxtSourceUser.Text.Trim(), sPass,
                    TxtTargetUser.Text.Trim(), tPass,
                    storagePath);

                TxtDocSourceMig.Text = $"Live Migration: {(audit.Source.IsMigrationEnabled ? "ENABLED" : "DISABLED")}";
                TxtDocSourceAuth.Text = $"Auth Protocol: {audit.Source.AuthType}";
                TxtDocSourceAuth.Foreground = audit.Source.IsKerberosAuth ? (SolidColorBrush)FindResource("BrushSuccess") : (SolidColorBrush)FindResource("BrushDanger");

                TxtDocTargetMig.Text = $"Live Migration: {(audit.Target.IsMigrationEnabled ? "ENABLED" : "DISABLED")}";
                TxtDocTargetAuth.Text = $"Auth Protocol: {audit.Target.AuthType}";
                TxtDocTargetAuth.Foreground = audit.Target.IsKerberosAuth ? (SolidColorBrush)FindResource("BrushSuccess") : (SolidColorBrush)FindResource("BrushDanger");

                TxtDocTargetFolder.Text = $"Directory '{storagePath}': {(audit.TargetFolderExists ? "EXISTS" : "MISSING")}";

                if (audit.IsFullyHealthy)
                {
                    TxtDoctorLogs.AppendText("[AUDIT PASSED] Both nodes are configured for Kerberos Live Migration. Ready for transfers.\n");
                }
                else
                {
                    TxtDoctorLogs.AppendText("[AUDIT WARNING] One or both nodes are misconfigured (e.g. CredSSP instead of Kerberos, or missing directory). Click 'Auto-Repair Both Hosts' to fix.\n");
                }
            }
            catch (Exception ex)
            {
                TxtDoctorLogs.AppendText($"[AUDIT ERROR] {ex.Message}\n");
            }
        }

        private async void BtnAutoRepairHosts_Click(object sender, RoutedEventArgs e)
        {
            var sourceHost = TxtSourceHost.Text.Trim();
            var targetHost = TxtTargetHost.Text.Trim();
            var storagePath = TxtDefaultStoragePath.Text.Trim();

            _sessionPasswordCache.TryGetValue(sourceHost, out var sPass);
            _sessionPasswordCache.TryGetValue(targetHost, out var tPass);

            BtnAutoRepairHosts.IsEnabled = false;
            TxtDoctorLogs.Text = string.Empty;

            var progress = new Progress<string>(line =>
            {
                TxtDoctorLogs.AppendText($"{line}\n");
                TxtDoctorLogs.ScrollToEnd();
            });

            try
            {
                await _doctorService.AutoRemediateBothHostsAsync(
                    sourceHost, targetHost,
                    TxtSourceUser.Text.Trim(), sPass,
                    TxtTargetUser.Text.Trim(), tPass,
                    storagePath,
                    progress);

                MessageBox.Show("Auto-remediation completed! Re-auditing hosts...", "Remediation Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                await RunDoctorAuditAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Remediation encountered an error:\n\n{ex.Message}", "Remediation Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnAutoRepairHosts.IsEnabled = true;
            }
        }

        // =================================================================
        // GLOBAL INVENTORY ACTIONS
        // =================================================================
        private async void BtnAddHost_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtNewHostName.Text.Trim();
            var user = TxtNewHostUser.Text.Trim();
            var pass = TxtNewHostPass.Password;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Please enter a valid Hostname or IP.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(pass))
            {
                _sessionPasswordCache[host] = pass;
            }

            var existing = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Username = user;
            }
            else
            {
                var entry = new ManagedHostEntry
                {
                    Hostname = host,
                    Username = string.IsNullOrEmpty(user) ? null : user,
                    IsOnline = true
                };
                _managedHosts.Add(entry);
            }

            await SaveCurrentSettingsAsync();
            await RefreshInventoryAsync();
        }

        private async void BtnRemoveHost_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ManagedHostEntry host)
            {
                _managedHosts.Remove(host);
                await SaveCurrentSettingsAsync();
                await RefreshInventoryAsync();
            }
        }

        private async void BtnRefreshAll_Click(object sender, RoutedEventArgs e)
        {
            BtnRefreshAll.IsEnabled = false;
            BtnRefreshAll.Content = "Querying Hosts...";

            try
            {
                await RefreshInventoryAsync();
            }
            finally
            {
                BtnRefreshAll.IsEnabled = true;
                BtnRefreshAll.Content = "Refresh All";
            }
        }

        private async Task RefreshInventoryAsync()
        {
            _allDiscoveredVms.Clear();

            var tasks = _managedHosts.Select(async host =>
            {
                try
                {
                    _sessionPasswordCache.TryGetValue(host.Hostname, out var pass);
                    var vms = await _hyperVService.DiscoverVirtualMachinesAsync(host.Hostname, host.Username, pass);
                    return vms.Select(v => new AggregatedVmViewItem
                    {
                        Id = v.Id,
                        Name = v.Name,
                        Status = v.Status,
                        MemoryMB = v.AssignedRamMB,
                        ResidentHost = host.Hostname,
                        CpuCores = v.CpuCores,
                        AssignedSwitch = v.AssignedSwitch
                    });
                }
                catch
                {
                    return [];
                }
            });

            var results = await Task.WhenAll(tasks);
            foreach (var batch in results)
            {
                _allDiscoveredVms.AddRange(batch);
            }

            ApplyVmSearchFilter();
        }

        private void TxtSearchVms_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyVmSearchFilter();
        }

        private void ApplyVmSearchFilter()
        {
            var filter = TxtSearchVms?.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(filter))
            {
                AggregatedWorkloadsList.ItemsSource = _allDiscoveredVms.ToList();
            }
            else
            {
                AggregatedWorkloadsList.ItemsSource = _allDiscoveredVms
                    .Where(v => v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                v.ResidentHost.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        // =================================================================
        // POWER OPERATIONS (SINGLE VM)
        // =================================================================
        private async void BtnStartVm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem vm)
            {
                await ExecutePowerOp(vm, VmPowerAction.Start);
            }
        }

        private async void BtnStopVm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem vm)
            {
                await ExecutePowerOp(vm, VmPowerAction.Stop);
            }
        }

        private async void BtnRestartVm_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AggregatedVmViewItem vm)
            {
                await ExecutePowerOp(vm, VmPowerAction.Restart);
            }
        }

        private async Task ExecutePowerOp(AggregatedVmViewItem vm, VmPowerAction action)
        {
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(vm.ResidentHost, StringComparison.OrdinalIgnoreCase));
            _sessionPasswordCache.TryGetValue(vm.ResidentHost, out var pass);

            try
            {
                await _hyperVService.ExecutePowerActionAsync(
                    vm.ResidentHost,
                    vm.Name,
                    action,
                    hostEntry?.Username,
                    pass);

                MessageBox.Show($"Power action '{action}' executed successfully on VM '{vm.Name}'.", "Power Operation", MessageBoxButton.OK, MessageBoxImage.Information);

                await Task.Delay(1500);
                await RefreshInventoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to execute '{action}' on VM '{vm.Name}':\n\n{ex.Message}", "Power Operation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =================================================================
        // SETTINGS ACTIONS
        // =================================================================
        private async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            BtnSaveSettings.IsEnabled = false;

            try
            {
                await SaveCurrentSettingsAsync();

                if (ChkCredSsp.IsChecked == true)
                {
                    if (SettingsService.IsRunningAsAdmin())
                    {
                        try
                        {
                            await SettingsService.ConfigureCredSspAsync(TxtTargetHost.Text.Trim());
                            MessageBox.Show("Platform settings and CredSSP system delegation applied successfully!", "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        catch (Exception credEx)
                        {
                            MessageBox.Show($"Settings saved, but CredSSP encountered an error:\n\n{credEx.Message}", "CredSSP Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        MessageBox.Show("Platform settings saved! (Note: CredSSP system-wide delegation requires launching Visual Studio or the App as Administrator).", "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show("Platform settings successfully persisted to disk!", "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSaveSettings.IsEnabled = true;
            }
        }

        private async Task SaveCurrentSettingsAsync()
        {
            var perfOption = (CmbMigrationPerformance.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Compression";
            var bwLimit = long.TryParse(TxtMigrationBandwidthLimit.Text.Trim(), out var bw) ? bw : 0;

            var settings = new AppSettingsModel
            {
                LastSourceHost = TxtSourceHost.Text.Trim(),
                LastSourceUser = TxtSourceUser.Text.Trim(),
                LastTargetHost = TxtTargetHost.Text.Trim(),
                LastTargetUser = TxtTargetUser.Text.Trim(),
                DefaultStoragePath = TxtDefaultStoragePath.Text.Trim(),
                EnableCredSsp = ChkCredSsp.IsChecked == true,
                MigrationPerformanceOption = perfOption,
                MigrationBandwidthLimitMbps = bwLimit,
                SavedHosts = [.. _managedHosts]

            };

            await SettingsService.SaveSettingsAsync(settings);

        }
        // =================================================================
        // PHASE 2: vCENTER V2V IMPORTER ACTIONS
        // =================================================================

        private async void BtnConnectVCenter_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtVCenterHost.Text.Trim();
            var user = TxtVCenterUser.Text.Trim();
            var pass = TxtVCenterPass.Password;
            var ignoreSsl = ChkIgnoreSsl.IsChecked == true;

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))
            {
                MessageBox.Show("Please enter a valid vCenter host and administrative username.", "Validation Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnConnectVCenter.IsEnabled = false;
            BtnConnectVCenter.Content = "Querying vSphere...";
            TxtVCenterStatus.Text = $"Connecting to VMware vCenter at {host}...";

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    TxtVCenterStatus.Text = msg;
                });

                var vms = await _vCenterService.DiscoverVmsAsync(host, user, pass, ignoreSsl, progress);

                _vCenterVms.Clear();
                foreach (var vm in vms)
                {
                    _vCenterVms.Add(vm);
                }

                if (_vCenterVms.Count == 0)
                {
                    TxtVCenterStatus.Text = $"Connected to {host}, but no virtual machines were returned.";
                    VCenterEmptyState.Visibility = Visibility.Visible;
                    VCenterVmListBox.Visibility = Visibility.Collapsed;
                }
                else
                {
                    VCenterEmptyState.Visibility = Visibility.Collapsed;
                    VCenterVmListBox.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to vCenter ({host}):\n\n{ex.Message}", "vCenter Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtVCenterStatus.Text = "Connection to vCenter failed. Check host IP, SSL toggle, and credentials.";
                VCenterEmptyState.Visibility = Visibility.Visible;
                VCenterVmListBox.Visibility = Visibility.Collapsed;
            }
            finally
            {
                BtnConnectVCenter.IsEnabled = true;
                BtnConnectVCenter.Content = "Connect & Discover vCenter VMs";
            }
        }

        private async void BtnConnectV2VTarget_Click(object sender, RoutedEventArgs e)
        {
            var host = TxtV2VTargetHost.Text.Trim();
            var user = TxtV2VTargetUser.Text.Trim();
            var pass = TxtV2VTargetPass.Password;

            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Please enter a valid Target Hyper-V Host IP or FQDN.", "Validation Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(pass))
            {
                _sessionPasswordCache[host] = pass;
            }

            BtnConnectV2VTarget.IsEnabled = false;
            BtnConnectV2VTarget.Content = "Scanning Hyper-V Node...";

            try
            {
                var switches = await _hyperVService.DiscoverVirtualSwitchesAsync(
                    host,
                    string.IsNullOrEmpty(user) ? null : user,
                    string.IsNullOrEmpty(pass) ? null : pass);

                CmbV2VSwitches.ItemsSource = switches;
                if (switches.Count > 0)
                {
                    CmbV2VSwitches.SelectedIndex = 0;
                }

                try
                {
                    var volumes = await _hyperVService.DiscoverTargetStorageVolumesAsync(
                        host,
                        string.IsNullOrEmpty(user) ? null : user,
                        string.IsNullOrEmpty(pass) ? null : pass);

                    if (volumes.Count > 0)
                    {
                        var defaultVol = volumes.FirstOrDefault(v => v.IsCsv) ?? volumes[0];
                        TxtV2VStoragePath.Text = defaultVol.Path;
                    }
                }
                catch { }

                MessageBox.Show($"Connected to Hyper-V host '{host}'! Discovered {switches.Count} virtual switch(es).", "Hyper-V Target Verified", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to Hyper-V host '{host}':\n\n{ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConnectV2VTarget.IsEnabled = true;
                BtnConnectV2VTarget.Content = "Connect & Discover Hyper-V Switches";
            }
        }

        private void VCenterVmListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VCenterVmListBox.SelectedItem is VCenterVmModel vm)
            {
                _selectedVCenterVm = vm;
                TxtV2VSelectedVmInfo.Text = $"Workload: {vm.Name} | vCPUs: {vm.CpuCount} | RAM: {vm.FormattedRam} | Disks: {vm.Disks.Count} ({vm.FormattedTotalStorage})";

                var gen = vm.RecommendedHyperVGeneration;
                TxtV2VFirmwareParity.Text = $"Target Generation: {gen} (Automatic Parity for VMware {vm.Firmware})";
                TxtV2VFirmwareParity.Foreground = (SolidColorBrush)FindResource("BrushSuccess");
            }
            else
            {
                _selectedVCenterVm = null;
                TxtV2VSelectedVmInfo.Text = "Select a VMware VM on the left to review conversion specs.";
                TxtV2VFirmwareParity.Text = "Target Generation: Auto-detected from firmware";
            }
        }

        private async void BtnExecuteV2V_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedVCenterVm == null)
            {
                MessageBox.Show("Please select a VMware VM from the left list to convert.", "No Workload Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var targetHost = TxtV2VTargetHost.Text.Trim();
            var targetStorage = TxtV2VStoragePath.Text.Trim();
            var targetSwitch = CmbV2VSwitches.SelectedItem?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(targetHost) || string.IsNullOrWhiteSpace(targetStorage))
            {
                MessageBox.Show("Please specify both a target Hyper-V host and destination CSV/volume path.", "Target Specification Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Initiate cold V2V conversion for '{_selectedVCenterVm.Name}'?\n\n" +
                $"Source: VMware vCenter ({TxtVCenterHost.Text.Trim()})\n" +
                $"Target: Hyper-V ({targetHost})\n" +
                $"Storage: {targetStorage}\n" +
                $"Architecture: {_selectedVCenterVm.RecommendedHyperVGeneration}\n\n" +
                "Note: If the VMware VM is powered on, it will be cleanly powered off before disk extraction.",
                "Confirm V2V Migration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnExecuteV2V.IsEnabled = false;
            PbV2VProgress.Value = 0;
            TxtV2VPercentText.Text = "0%";
            TxtV2VStepText.Text = "Initializing conversion...";

            var job = new V2VMigrationJob
            {
                VmName = _selectedVCenterVm.Name,
                SourceVCenter = TxtVCenterHost.Text.Trim(),
                TargetHyperVHost = targetHost,
                TargetStorageVolume = targetStorage
            };

            _sessionPasswordCache.TryGetValue(targetHost, out var targetPass);
            var hostEntry = _managedHosts.FirstOrDefault(h => h.Hostname.Equals(targetHost, StringComparison.OrdinalIgnoreCase));

            var progress = new Progress<string>(msg =>
            {
                TxtV2VStepText.Text = msg;
                TxtV2VPercentText.Text = $"{job.ProgressPercent:F0}%";
                PbV2VProgress.Value = job.ProgressPercent;
            });

            try
            {
                await V2VMigrationService.ExecuteV2VMigrationAsync(
                    job,
                    _selectedVCenterVm,
                    targetHost,
                    targetStorage,
                    targetSwitch,
                    hostEntry?.Username,
                    targetPass,
                    _vCenterService,
                    progress);

                PbV2VProgress.Value = 100;
                TxtV2VPercentText.Text = "100%";
                TxtV2VStepText.Text = "Migration completed successfully!";

                MessageBox.Show(
                    $"V2V Migration of '{_selectedVCenterVm.Name}' completed successfully!\n\nThe workload is now provisioned and ready on Hyper-V host '{targetHost}'.",
                    "V2V Conversion Succeeded",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await RefreshInventoryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"V2V Migration interrupted:\n\n{ex.Message}", "Conversion Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtV2VStepText.Text = $"Failed: {ex.Message}";
            }
            finally
            {
                BtnExecuteV2V.IsEnabled = true;
            }
        }
    }

}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Enterprise batch scheduler and concurrency throttler for Hyper-V live migrations.
    /// Manages maintenance windows and automated post-flight verification with rollback.
    /// </summary>
    public sealed class MigrationSchedulerService : IDisposable
    {
        private readonly HyperVService _hyperVService;
        private readonly ObservableCollection<ScheduledMigrationBatch> _batches = [];
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningBatchCts = new();
        private CancellationTokenSource? _serviceCts;
        private Task? _timerLoopTask;
        private bool _isDisposed;

        public ObservableCollection<ScheduledMigrationBatch> Batches => _batches;

        public event Action<ScheduledMigrationBatch>? BatchTriggered;
        public event Action<ScheduledMigrationBatch>? BatchStarted;
        public event Action<ScheduledMigrationBatch, string, bool, string>? WorkloadFinished;
        public event Action<ScheduledMigrationBatch>? BatchFinished;

        public MigrationSchedulerService(HyperVService hyperVService)
        {
            _hyperVService = hyperVService ?? throw new ArgumentNullException(nameof(hyperVService));
            StartSchedulerWorker();
        }

        public void EnqueueBatch(ScheduledMigrationBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            _batches.Add(batch);
        }

        public void CancelBatch(string batchId)
        {
            var batch = _batches.FirstOrDefault(b => b.BatchId == batchId);
            batch?.IsCancelled = true;

            if (_runningBatchCts.TryRemove(batchId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { }
            }
        }

        /// <summary>
        /// Executes a multi-VM batch with strict concurrency limits and post-migration health auditing.
        /// </summary>
        public async Task ExecuteBatchAsync(
            ScheduledMigrationBatch batch,
            List<VirtualMachineModel> workloads,
            string? destinationStoragePath,
            string? username,
            string? password,
            IProgress<string> logProgress,
            CancellationToken cancellationToken = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (workloads == null || workloads.Count == 0) return;

            var batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runningBatchCts[batch.BatchId] = batchCts;

            batch.IsExecuted = true;
            BatchStarted?.Invoke(batch);

            var concurrency = Math.Clamp(batch.ConcurrencyLimit, 1, 8);
            using var throttler = new SemaphoreSlim(concurrency, concurrency);

            logProgress.Report($"[QUEUE] Starting Batch '{batch.BatchName}' ({workloads.Count} workloads, Max Concurrency: {concurrency}).");

            var migrationTasks = new List<Task>();

            foreach (var vm in workloads)
            {
                if (batchCts.IsCancellationRequested || batch.IsCancelled)
                {
                    logProgress.Report($"[QUEUE] Batch execution cancelled by operator.");
                    break;
                }

                await throttler.WaitAsync(batchCts.Token);

                var currentVm = vm;
                var task = Task.Run(async () =>
                {
                    var vmLogPrefix = $"[{currentVm.Name}]";
                    var isSuccess = false;
                    var statusDetails = string.Empty;

                    try
                    {
                        logProgress.Report($"{vmLogPrefix} Slot acquired. Initiating live migration to '{batch.TargetHost}'...");

                        // 1. Check & Enforce Processor Compatibility if needed
                        if (currentVm.CompatibilityForMigrationModeEnabled)
                        {
                            try
                            {
                                await _hyperVService.EnableProcessorCompatibilityAsync(
                                    batch.SourceHost,
                                    currentVm.Name,
                                    username,
                                    password,
                                    batchCts.Token);
                                logProgress.Report($"{vmLogPrefix} Processor Compatibility Mode verified/enabled.");
                            }
                            catch (Exception ex)
                            {
                                logProgress.Report($"{vmLogPrefix} [WARN] Could not toggle CPU compatibility: {ex.Message}");
                            }
                        }

                        // 2. Execute Enterprise Relocation (honoring multi-NIC and disaggregated disks)
                        await _hyperVService.ExecuteEnterpriseLiveMigrationAsync(
                            currentVm.Name,
                            batch.SourceHost,
                            batch.TargetHost,
                            destinationStoragePath,
                            [.. currentVm.NetworkAdapters],
                            [.. currentVm.HardDisks],
                            username,
                            password,
                            logProgress,
                            batchCts.Token);

                        // 3. Post-Flight Verification & Automated Rollback
                        if (batch.AutoRollbackOnHeartbeatFailure && currentVm.IsRunning)
                        {
                            logProgress.Report($"{vmLogPrefix} Commencing post-migration heartbeat verification ({batch.HeartbeatTimeoutSeconds}s timeout)...");

                            var health = await _hyperVService.VerifyGuestHealthAsync(
                                batch.TargetHost,
                                currentVm.Name,
                                batch.HeartbeatTimeoutSeconds,
                                username,
                                password,
                                batchCts.Token);

                            if (!health.HeartbeatServiceOk)
                            {
                                logProgress.Report($"{vmLogPrefix} [ALERT] Workload unresponsive after migration! Triggering auto-rollback...");
                                await _hyperVService.RollbackMigrationAsync(
                                    currentVm.Name,
                                    batch.TargetHost,
                                    batch.SourceHost,
                                    destinationStoragePath,
                                    username,
                                    password,
                                    logProgress,
                                    CancellationToken.None);

                                isSuccess = false;
                                statusDetails = "Rolled back: Heartbeat failed post-migration.";
                            }
                            else
                            {
                                isSuccess = true;
                                statusDetails = $"Healthy: Heartbeat OK, IP={health.GuestIpAddress}";
                                logProgress.Report($"{vmLogPrefix} Verification passed ({statusDetails}).");
                            }
                        }
                        else
                        {
                            isSuccess = true;
                            statusDetails = "Migration completed successfully.";
                        }
                    }
                    catch (Exception ex)
                    {
                        isSuccess = false;
                        statusDetails = $"Failed: {ex.Message}";
                        logProgress.Report($"{vmLogPrefix} [ERROR] {ex.Message}");
                    }
                    finally
                    {
                        throttler.Release();
                        WorkloadFinished?.Invoke(batch, currentVm.Name, isSuccess, statusDetails);
                    }
                }, batchCts.Token);

                migrationTasks.Add(task);
            }

            await Task.WhenAll(migrationTasks);

            _runningBatchCts.TryRemove(batch.BatchId, out _);
            logProgress.Report($"[QUEUE] Batch '{batch.BatchName}' completed.");
            BatchFinished?.Invoke(batch);
        }

        private void StartSchedulerWorker()
        {
            _serviceCts = new CancellationTokenSource();
            _timerLoopTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));

                while (!_serviceCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(_serviceCts.Token);

                        var nowUtc = DateTime.UtcNow;
                        var pending = _batches
                            .Where(b => !b.IsExecuted && !b.IsCancelled && b.ScheduledExecutionUtc <= nowUtc)
                            .ToList();

                        foreach (var batch in pending)
                        {
                            // Trigger event for UI to run batch with current credentials
                            BatchTriggered?.Invoke(batch);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Resilient timer loop
                    }
                }
            }, _serviceCts.Token);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _serviceCts?.Cancel();
                _serviceCts?.Dispose();

                foreach (var cts in _runningBatchCts.Values)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _runningBatchCts.Clear();
            }
            catch { }
        }
    }
}
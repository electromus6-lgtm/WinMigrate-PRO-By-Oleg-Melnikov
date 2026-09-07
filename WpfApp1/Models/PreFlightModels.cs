using System;
using System.Collections.Generic;
using System.Linq;

namespace WpfApp1.Models;

public enum CheckSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public enum VmPowerAction
{
    Start,
    Stop,
    Restart
}

public enum PreFlightCategory
{
    General,
    ComputeProcessor,
    MemoryRam,
    StorageSubsystem,
    VirtualNetwork,
    SecurityShieldedVm
}

public class PreFlightCheckItem
{
    public string CheckName { get; set; } = string.Empty;
    public bool IsPassed { get; set; }
    public CheckSeverity Severity { get; set; } = CheckSeverity.Info;
    public PreFlightCategory Category { get; set; } = PreFlightCategory.General;
    public string Details { get; set; } = string.Empty;
    public string RemediationHint { get; set; } = string.Empty;
    public bool AutoFixAvailable { get; set; }

    public string SeverityBadgeColor => Severity switch
    {
        CheckSeverity.Info => "#38BDF8",      // Cyan / Info
        CheckSeverity.Warning => "#F59E0B",   // Amber / Warning
        CheckSeverity.Error => "#EF4444",     // Red / Error
        CheckSeverity.Critical => "#DC2626",  // Crimson / Critical
        _ => "#64748B"
    };

    public string StatusSymbol => IsPassed ? "✔" : (Severity == CheckSeverity.Warning ? "⚠" : "✖");
}

public class PreFlightReport
{
    public List<PreFlightCheckItem> Checks { get; set; } = [];
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool CanProceed => Checks.All(c => (c.Severity != CheckSeverity.Error && c.Severity != CheckSeverity.Critical) || c.IsPassed);

    public bool HasWarnings => Checks.Any(c => c.Severity == CheckSeverity.Warning);
    public int PassedCount => Checks.Count(c => c.IsPassed);
    public int WarningCount => Checks.Count(c => !c.IsPassed && c.Severity == CheckSeverity.Warning);
    public int FailureCount => Checks.Count(c => !c.IsPassed && (c.Severity == CheckSeverity.Error || c.Severity == CheckSeverity.Critical));

    public double ReadinessScorePercentage
    {
        get
        {
            if (Checks.Count == 0) return 0.0;
            var score = 100.0;
            foreach (var check in Checks)
            {
                if (!check.IsPassed)
                {
                    if (check.Severity == CheckSeverity.Critical) score -= 40.0;
                    else if (check.Severity == CheckSeverity.Error) score -= 25.0;
                    else if (check.Severity == CheckSeverity.Warning) score -= 10.0;
                }
            }
            return Math.Max(0.0, Math.Min(100.0, score));
        }
    }

    public string SummaryText => CanProceed
        ? (HasWarnings
            ? $"Validation Passed with {WarningCount} Warning(s). Live Migration is permissible."
            : "All validation gates passed cleanly. Workloads are ready for relocation.")
        : $"Validation Failed: {FailureCount} critical blocker(s) detected. Correct errors before proceeding.";
}

public sealed class ManagedHostEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Hostname { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool IsOnline { get; set; }
}

public sealed class AggregatedVmViewItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public VmOperationalStatus Status { get; set; }
    public long MemoryMB { get; set; }
    public string ResidentHost { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public string AssignedSwitch { get; set; } = string.Empty;
}
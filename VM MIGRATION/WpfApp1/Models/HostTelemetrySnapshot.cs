namespace WpfApp1.Models
{
    public sealed class HostTelemetrySnapshot
    {
        public string Hostname { get; init; } = string.Empty;
        public double CpuUtilizationPercent { get; init; }
        public double RamUsedGB { get; init; }
        public double RamTotalGB { get; init; }
        public double RamUtilizationPercent => RamTotalGB > 0 ? (RamUsedGB / RamTotalGB) * 100.0 : 0.0;
        public int TotalVms { get; init; }
        public int RunningVms { get; init; }
    }
}
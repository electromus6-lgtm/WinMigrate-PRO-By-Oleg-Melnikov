using System;
using System.Text;

namespace WpfApp1.Models
{
    /// <summary>
    /// Command-line interface parser enabling Windows Admin Center (WAC) deep-linking, 
    /// unattended automation pipelines, and direct desktop shortcut orchestration.
    /// </summary>
    public sealed class CommandLineOptions
    {
        public string? SourceHost { get; set; }
        public string? SourceUser { get; set; }
        public string? TargetHost { get; set; }
        public string? TargetUser { get; set; }
        public string? DestinationStoragePath { get; set; }
        public string? PreselectedVmName { get; set; }
        public string MigrationPerformanceOption { get; set; } = "Compression";
        public int ConcurrencyLimit { get; set; } = 2;
        public bool AutoConnect { get; set; }
        public bool ShowHelp { get; set; }

        public static CommandLineOptions Parse(string[] args)
        {
            var options = new CommandLineOptions();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].Trim();

                // Source Node
                if ((arg.Equals("--source", StringComparison.OrdinalIgnoreCase) || arg.Equals("-s", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.SourceHost = args[++i].Trim();
                }
                // Target Node
                else if ((arg.Equals("--target", StringComparison.OrdinalIgnoreCase) || arg.Equals("-t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.TargetHost = args[++i].Trim();
                }
                // Source Credential
                else if ((arg.Equals("--user", StringComparison.OrdinalIgnoreCase) || arg.Equals("-u", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.SourceUser = args[++i].Trim();
                }
                // Target Credential
                else if ((arg.Equals("--targetuser", StringComparison.OrdinalIgnoreCase) || arg.Equals("-tu", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.TargetUser = args[++i].Trim();
                }
                // Destination Storage Path / CSV
                else if ((arg.Equals("--storage", StringComparison.OrdinalIgnoreCase) || arg.Equals("-d", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.DestinationStoragePath = args[++i].Trim();
                }
                // Specific Workload Pre-Selection
                else if ((arg.Equals("--vm", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    options.PreselectedVmName = args[++i].Trim();
                }
                // Transport QoS (Compression, SMB, TCP)
                else if (arg.Equals("--performance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.MigrationPerformanceOption = args[++i].Trim();
                }
                // Concurrency Limit
                else if ((arg.Equals("--concurrency", StringComparison.OrdinalIgnoreCase) || arg.Equals("-c", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i].Trim(), out var parsedC))
                    {
                        options.ConcurrencyLimit = Math.Clamp(parsedC, 1, 8);
                    }
                }
                // Automated Connection Trigger
                else if (arg.Equals("--autoconnect", StringComparison.OrdinalIgnoreCase) || arg.Equals("-a", StringComparison.OrdinalIgnoreCase))
                {
                    options.AutoConnect = true;
                }
                // Help Flag
                else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase) || arg.Equals("-?"))
                {
                    options.ShowHelp = true;
                }
            }

            return options;
        }

        public static string GetUsageHelp()
        {
            var sb = new StringBuilder();
            sb.AppendLine("WinMigrate Pro (ElectroMU Edition) - CLI Usage Syntax:");
            sb.AppendLine("  WinMigratePro.exe [options]");
            sb.AppendLine();
            sb.AppendLine("Options:");
            sb.AppendLine("  -s,  --source <host>         Source Hyper-V compute node (IP or FQDN)");
            sb.AppendLine("  -t,  --target <host>         Target Hyper-V compute node (IP or FQDN)");
            sb.AppendLine("  -u,  --user <domain\\user>   Administrative username for source node");
            sb.AppendLine("  -tu, --targetuser <user>     Administrative username for target node");
            sb.AppendLine("  -d,  --storage <path>        Destination CSV/volume path (e.g. C:\\ClusterStorage\\Volume1)");
            sb.AppendLine("  -v,  --vm <name>             Pre-select specific workload for migration");
            sb.AppendLine("  -c,  --concurrency <1-8>     Simultaneous live migration limit (Default: 2)");
            sb.AppendLine("       --performance <mode>    Transport QoS (Compression | SMB | TCP)");
            sb.AppendLine("  -a,  --autoconnect           Automatically trigger discovery on launch");
            sb.AppendLine("  -h,  --help                  Display this CLI syntax manual");
            return sb.ToString();
        }
    }
}
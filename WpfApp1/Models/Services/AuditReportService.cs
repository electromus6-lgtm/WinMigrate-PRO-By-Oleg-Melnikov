using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Enterprise compliance and audit report generator producing standalone, 
    /// self-contained HTML reports with executive KPIs, workload matrices, and syntax-highlighted transcripts.
    /// </summary>
    public static class AuditReportService
    {
        public static string GenerateHtmlAuditReport(IEnumerable<MigrationJobModel> jobs)
        {
            var jobList = jobs.ToList();
            var total = jobList.Count;
            var completed = jobList.Count(j => j.Status == JobStatus.Completed);
            var failed = jobList.Count(j => j.Status == JobStatus.Failed);
            var successRate = total > 0 ? (completed / (double)total) * 100.0 : 100.0;

            var timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
            var timestampLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Enterprise Transcript Intelligence
            var cpuCompatCount = jobList.Count(j => (j.AllLogs ?? string.Empty).Contains("Processor Compatibility", StringComparison.OrdinalIgnoreCase));
            var disaggregatedCount = jobList.Count(j => (j.AllLogs ?? string.Empty).Contains("DestinationFilePath", StringComparison.OrdinalIgnoreCase) || (j.AllLogs ?? string.Empty).Contains("-Vhdx", StringComparison.OrdinalIgnoreCase));
            var multiNicCount = jobList.Count(j => (j.AllLogs ?? string.Empty).Contains("NetworkAdapterBinding", StringComparison.OrdinalIgnoreCase));
            var heartbeatVerifiedCount = jobList.Count(j => (j.AllLogs ?? string.Empty).Contains("Heartbeat OK", StringComparison.OrdinalIgnoreCase) || (j.AllLogs ?? string.Empty).Contains("Heartbeat verified", StringComparison.OrdinalIgnoreCase));
            var rollbackCount = jobList.Count(j => (j.AllLogs ?? string.Empty).Contains("[ROLLBACK]", StringComparison.OrdinalIgnoreCase) || (j.AllLogs ?? string.Empty).Contains("auto-rollback", StringComparison.OrdinalIgnoreCase));

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang='en'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='UTF-8'>");
            sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1.0'>");
            sb.AppendLine("<title>WinMigrate Pro - Datacenter Live Migration Compliance Audit</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(":root { --bg: #060c14; --card-bg: #0d1726; --card-border: #1a2c42; --text-main: #f1f5f9; --text-muted: #8da4be; --cyan: #00b6de; --cyan-hover: #38c8ea; --green: #10b981; --red: #ef4444; --amber: #f59e0b; }");
            sb.AppendLine("* { box-sizing: border-box; }");
            sb.AppendLine("body { font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, sans-serif; background: var(--bg); color: var(--text-main); margin: 0; padding: 32px 40px; }");
            sb.AppendLine(".container { max-width: 1400px; margin: 0 auto; }");
            sb.AppendLine(".header-bar { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 1px solid var(--card-border); padding-bottom: 24px; margin-bottom: 28px; }");
            sb.AppendLine(".brand-title { font-size: 26px; font-weight: 800; color: var(--cyan); margin: 0 0 6px 0; letter-spacing: -0.5px; }");
            sb.AppendLine(".brand-subtitle { font-size: 13px; color: var(--text-muted); margin: 0; }");
            sb.AppendLine(".btn-print { background: #0e2035; color: var(--cyan); border: 1px solid var(--cyan); border-radius: 6px; padding: 10px 18px; font-weight: 600; cursor: pointer; transition: all 0.2s; font-size: 12px; }");
            sb.AppendLine(".btn-print:hover { background: var(--cyan); color: #fff; }");
            sb.AppendLine(".meta-badge { font-size: 12px; color: var(--text-muted); text-align: right; }");
            sb.AppendLine(".kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 16px; margin-bottom: 32px; }");
            sb.AppendLine(".kpi-card { background: var(--card-bg); border: 1px solid var(--card-border); border-radius: 10px; padding: 18px; text-align: center; }");
            sb.AppendLine(".kpi-label { font-size: 11px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.5px; }");
            sb.AppendLine(".kpi-val { font-size: 28px; font-weight: 800; margin-top: 6px; }");
            sb.AppendLine(".table-wrapper { background: var(--card-bg); border: 1px solid var(--card-border); border-radius: 10px; overflow: hidden; margin-bottom: 36px; box-shadow: 0 8px 24px rgba(0,0,0,0.3); }");
            sb.AppendLine("table { width: 100%; border-collapse: collapse; text-align: left; font-size: 13px; }");
            sb.AppendLine("th { background: #09111c; color: var(--text-muted); padding: 14px 18px; font-weight: 700; border-bottom: 1px solid var(--card-border); text-transform: uppercase; font-size: 11px; letter-spacing: 0.5px; }");
            sb.AppendLine("td { padding: 14px 18px; border-bottom: 1px solid #132238; vertical-align: middle; }");
            sb.AppendLine("tr:last-child td { border-bottom: none; }");
            sb.AppendLine("tr:hover td { background: rgba(0, 182, 222, 0.03); }");
            sb.AppendLine(".badge { padding: 4px 10px; border-radius: 4px; font-size: 11px; font-weight: 700; display: inline-block; }");
            sb.AppendLine(".badge-completed { background: rgba(16, 185, 129, 0.15); color: var(--green); border: 1px solid var(--green); }");
            sb.AppendLine(".badge-failed { background: rgba(239, 68, 68, 0.15); color: var(--red); border: 1px solid var(--red); }");
            sb.AppendLine(".badge-running { background: rgba(56, 189, 248, 0.15); color: #38bdf8; border: 1px solid #38bdf8; }");
            sb.AppendLine(".tag { display: inline-block; padding: 2px 6px; border-radius: 3px; font-size: 10px; font-weight: 600; margin-right: 4px; }");
            sb.AppendLine(".tag-cpu { background: #172a3a; color: #38c8ea; border: 1px solid #1e4566; }");
            sb.AppendLine(".tag-storage { background: #262113; color: #f59e0b; border: 1px solid #4a3c1a; }");
            sb.AppendLine(".tag-nic { background: #1c152a; color: #c084fc; border: 1px solid #3f2963; }");
            sb.AppendLine(".tag-health { background: #0f291e; color: #34d399; border: 1px solid #1a533c; }");
            sb.AppendLine(".tag-rollback { background: #3b1111; color: #f87171; border: 1px solid #632020; }");
            sb.AppendLine(".transcript-section h2 { font-size: 18px; font-weight: 700; color: #fff; margin: 0 0 16px 0; }");
            sb.AppendLine("details { background: var(--card-bg); border: 1px solid var(--card-border); border-radius: 8px; margin-bottom: 12px; overflow: hidden; }");
            sb.AppendLine("summary { padding: 14px 18px; cursor: pointer; font-weight: 700; color: var(--cyan); display: flex; justify-content: space-between; align-items: center; user-select: none; }");
            sb.AppendLine("summary:hover { background: #0f1c2e; }");
            sb.AppendLine(".terminal-logs { background: #04070c; border-top: 1px solid var(--card-border); padding: 16px 20px; font-family: 'Consolas', 'Courier New', monospace; font-size: 11.5px; line-height: 1.6; color: #cbd5e1; white-space: pre-wrap; overflow-x: auto; max-height: 380px; }");
            sb.AppendLine(".footer-card { border-top: 1px solid var(--card-border); padding-top: 24px; margin-top: 48px; text-align: center; color: var(--text-muted); font-size: 12px; line-height: 1.6; }");
            sb.AppendLine("@media print { body { background: #fff !important; color: #000 !important; padding: 10px !important; } .kpi-card, .table-wrapper, details { background: #fff !important; border-color: #cbd5e1 !important; color: #000 !important; box-shadow: none !important; } th { background: #f1f5f9 !important; color: #000 !important; } td { color: #000 !important; } .btn-print { display: none; } .terminal-logs { background: #f8fafc !important; color: #0f172a !important; border-color: #cbd5e1 !important; } }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class='container'>");

            // Top Header
            sb.AppendLine("<div class='header-bar'>");
            sb.AppendLine("  <div>");
            sb.AppendLine("    <div class='brand-title'>WinMigrate Pro: Datacenter Relocation Compliance Audit</div>");
            sb.AppendLine("    <p class='brand-subtitle'>Enterprise Hyper-V Orchestration Platform &bull; ElectroMU Gaming Network</p>");
            sb.AppendLine("  </div>");
            sb.AppendLine("  <div class='meta-badge'>");
            sb.AppendLine($"    <div>Local Generated: <strong>{timestampLocal}</strong></div>");
            sb.AppendLine($"    <div>UTC Session: <strong>{timestampUtc}</strong></div>");
            sb.AppendLine("    <div style='margin-top:8px;'><button class='btn-print' onclick='window.print()'>🖨 Print / Save as PDF</button></div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</div>");

            // KPI Dashboard
            sb.AppendLine("<div class='kpi-grid'>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Total Workloads</div><div class='kpi-val' style='color:#ffffff;'>{total}</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Successful</div><div class='kpi-val' style='color:var(--green);'>{completed}</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Failed / Aborted</div><div class='kpi-val' style='color:var(--red);'>{failed}</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Success Rate</div><div class='kpi-val' style='color:{(successRate >= 99.9 ? "var(--green)" : "var(--amber)")};'>{successRate:F1}%</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>CPU Compatibility</div><div class='kpi-val' style='color:var(--cyan);'>{cpuCompatCount}</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Guest Heartbeats</div><div class='kpi-val' style='color:#34d399;'>{heartbeatVerifiedCount}</div></div>");
            sb.AppendLine($"  <div class='kpi-card'><div class='kpi-label'>Rollbacks</div><div class='kpi-val' style='color:{(rollbackCount > 0 ? "var(--red)" : "var(--text-muted)")};'>{rollbackCount}</div></div>");
            sb.AppendLine("</div>");

            // Workloads Matrix
            sb.AppendLine("<div class='table-wrapper'>");
            sb.AppendLine("<table>");
            sb.AppendLine("  <thead>");
            sb.AppendLine("    <tr><th>Workload Name</th><th>Source Node</th><th>Target Node</th><th>Enterprise Features</th><th>Status</th><th>Duration</th><th>Start Time</th></tr>");
            sb.AppendLine("  </thead>");
            sb.AppendLine("  <tbody>");

            if (jobList.Count == 0)
            {
                sb.AppendLine("    <tr><td colspan='7' style='text-align:center; padding:30px; color:var(--text-muted);'>No migration jobs recorded in this audit session.</td></tr>");
            }
            else
            {
                foreach (var job in jobList)
                {
                    var badgeClass = job.Status switch
                    {
                        JobStatus.Completed => "badge-completed",
                        JobStatus.Failed => "badge-failed",
                        _ => "badge-running"
                    };

                    var logs = job.AllLogs ?? string.Empty;
                    var tagsHtml = new StringBuilder();

                    if (logs.Contains("Processor Compatibility", StringComparison.OrdinalIgnoreCase))
                        tagsHtml.Append("<span class='tag tag-cpu'>CPU Compat</span>");
                    if (logs.Contains("DestinationFilePath", StringComparison.OrdinalIgnoreCase) || logs.Contains("-Vhdx", StringComparison.OrdinalIgnoreCase))
                        tagsHtml.Append("<span class='tag tag-storage'>Multi-VHDX</span>");
                    if (logs.Contains("NetworkAdapterBinding", StringComparison.OrdinalIgnoreCase))
                        tagsHtml.Append("<span class='tag tag-nic'>vNIC Remapped</span>");
                    if (logs.Contains("Heartbeat OK", StringComparison.OrdinalIgnoreCase) || logs.Contains("Heartbeat verified", StringComparison.OrdinalIgnoreCase))
                        tagsHtml.Append("<span class='tag tag-health'>Heartbeat OK</span>");
                    if (logs.Contains("[ROLLBACK]", StringComparison.OrdinalIgnoreCase) || logs.Contains("auto-rollback", StringComparison.OrdinalIgnoreCase))
                        tagsHtml.Append("<span class='tag tag-rollback'>Auto-Rollback</span>");

                    if (tagsHtml.Length == 0)
                    {
                        tagsHtml.Append("<span style='color:var(--text-muted); font-size:11px;'>Standard Live</span>");
                    }

                    sb.AppendLine("    <tr>");
                    sb.AppendLine($"      <td><strong style='color:#fff;'>{WebUtility.HtmlEncode(job.VmName)}</strong></td>");
                    sb.AppendLine($"      <td>{WebUtility.HtmlEncode(job.SourceHost)}</td>");
                    sb.AppendLine($"      <td>{WebUtility.HtmlEncode(job.TargetHost)}</td>");
                    sb.AppendLine($"      <td>{tagsHtml}</td>");
                    sb.AppendLine($"      <td><span class='badge {badgeClass}'>{job.Status}</span></td>");
                    sb.AppendLine($"      <td>{WebUtility.HtmlEncode(job.DurationText)}</td>");
                    sb.AppendLine($"      <td>{job.StartTime:yyyy-MM-dd HH:mm:ss}</td>");
                    sb.AppendLine("    </tr>");
                }
            }

            sb.AppendLine("  </tbody>");
            sb.AppendLine("</table>");
            sb.AppendLine("</div>");

            // Embedded Streaming Transcripts
            sb.AppendLine("<div class='transcript-section'>");
            sb.AppendLine("  <h2>Workload Relocation Transcripts &amp; WinRM Event Logs</h2>");

            foreach (var job in jobList)
            {
                var formattedLogs = FormatTranscriptHtml(job.AllLogs ?? "No transcript records captured.");
                sb.AppendLine("  <details>");
                sb.AppendLine($"    <summary><span>Workload: <strong>{WebUtility.HtmlEncode(job.VmName)}</strong> ({job.SourceHost} ➔ {job.TargetHost})</span> <span class='badge {(job.Status == JobStatus.Completed ? "badge-completed" : "badge-failed")}'>{job.Status}</span></summary>");
                sb.AppendLine($"    <div class='terminal-logs'>{formattedLogs}</div>");
                sb.AppendLine("  </details>");
            }

            sb.AppendLine("</div>");

            // Footer & Attributions
            sb.AppendLine("<div class='footer-card'>");
            sb.AppendLine("  <strong>WinMigrate Pro (ElectroMU Edition)</strong> &bull; Centralized Agentless Windows Hyper-V Orchestration Console<br/>");
            sb.AppendLine("  Lead Systems Architect: <strong>Oleg Melnikov</strong> &bull; Organization: <strong>ElectroMU Gaming Network</strong><br/>");
            sb.AppendLine("  Target Runtime: Microsoft .NET 10 / .NET 9 (C# 13) &bull; Licensed under the MIT Open Source License");
            sb.AppendLine("</div>");

            sb.AppendLine("</div>"); // container
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static string FormatTranscriptHtml(string rawLogs)
        {
            var lines = rawLogs.Split(["\r\n", "\n"], StringSplitOptions.None);
            var sb = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = WebUtility.HtmlEncode(rawLine);

                if (line.Contains("[SUCCESS]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"<span style='color:#10b981; font-weight:bold;'>{line}</span>");
                }
                else if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) || line.Contains("[FAILED]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"<span style='color:#ef4444; font-weight:bold;'>{line}</span>");
                }
                else if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"<span style='color:#f59e0b; font-weight:bold;'>{line}</span>");
                }
                else if (line.Contains("[ROLLBACK]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"<span style='color:#f87171; background:rgba(239,68,68,0.1); font-weight:bold;'>{line}</span>");
                }
                else if (line.Contains("[EXEC]", StringComparison.OrdinalIgnoreCase) || line.Contains("[MIGRATE]", StringComparison.OrdinalIgnoreCase) || line.Contains("[QoS]", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"<span style='color:#00b6de;'>{line}</span>");
                }
                else
                {
                    sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }
    }
}
# WinMigrate Pro (ElectroMU Edition)

**Centralized Agentless Hyper-V Live Migration & Datacenter Orchestration Console**  
**Author & Lead Systems Architect:** Oleg Melnikov  
**Organization:** ElectroMU Gaming Network  
**Target Runtime:** Microsoft .NET 10.0 / .NET 9.0 (C# 13, WPF) on Windows Server 2022/2025 & Windows 11  
**License:** [MIT License](LICENSE)

---

## 1. Executive Summary

**WinMigrate Pro (ElectroMU Edition)** is a native Windows enterprise orchestration console designed to operate alongside or replace heavy tools like SCVMM (System Center Virtual Machine Manager) and Windows Admin Center (WAC). 

It delivers **100% agentless** discovery, visual OLE drag-and-drop live migrations, concurrency-controlled batch evacuations, automated pre-flight validation checkers, and self-healing infrastructure diagnostics across standalone Hyper-V compute nodes and CSV failover clusters.

Unlike standard tools that fail on hardware differences or WinRM double-hop barriers, WinMigrate Pro actively identifies and fixes real-world datacenter bottlenecks:
* **0x80072740 Processor Incompatibility:** Cross-generation CPU instruction pass-through auditor and 1-click enabler.
* **0x8009030D WinRM Double-Hop:** 1-Click Kerberos Infrastructure Doctor, automated AD Constrained Delegation calibration, and SYSTEM ticket purger.
* **Virtual Switch Mismatch:** Multi-vNIC switch and VLAN ID remapping matrix.
* **Storage Exhaustion:** Multi-VHDX disaggregated per-disk routing and 20 GB safety headroom guards.

---

## 2. Master Feature Breakdown

### Tab 1: Migration Orchestrator
* **Agentless WinRM Discovery:** Connects to any remote Hyper-V node via WSMan without installing background host agents.
* **Visual OLE Drag-and-Drop:** Drag workloads from the Source VM list and drop them onto the Target Card to stage live migrations.
* **Datacenter Evacuation Engine:** Multi-select workloads (Ctrl+Click / Shift+Click) to calculate cumulative RAM/vCPU overhead and trigger automated batch evacuations.
* **Cluster Shared Volume (CSV) Selector:** Auto-queries `Get-ClusterSharedVolume` and `Get-Volume` with real-time free space metrics.
* **Automated Pre-Flight Gatekeeper:** Evaluates target RAM overhead (with host OS reserve buffer), vCPU contention, virtual switch parity, storage backplane targets, and active AVHDX snapshot delta chains.
* **Live Background Telemetry Gauges:** Background timer polling CPU load % and RAM utilization gauges with WinRM-throttling mitigation.
* **Infrastructure Doctor & 1-Click Auto-Repair:** Audits host migration protocols and fixes Kerberos/WinRM double-hop issues with one click.
* **PowerShell Runbook Inspector:** Generates unattended `.ps1` production automation runbooks with transcript logging.
* **Live VM Hardware Tuner:** Real-time and offline vCPU count adjustment, RAM sizing in MB, Dynamic Memory bounds (Min/Max/Buffer), and Virtual Switch re-mapping.
* **Multi-vNIC Remapping Matrix:** Allows re-mapping multiple network adapters when migrating across nodes with disparate virtual switch names.
* **Disaggregated Multi-VHDX Storage Matrix:** Route OS disks to fast NVMe CSVs and data disks to high-capacity secondary storage tiers.
* **Maintenance Window Scheduler:** Schedule overnight evacuations with strict `SemaphoreSlim` concurrency throttling (1–8 parallel migrations) and automated post-flight guest heartbeat health verification with auto-rollback.

### Tab 2: Global Virtual Machine Inventory
* **Multi-Host Aggregator:** Centralized multi-host inventory querying all managed Hyper-V nodes simultaneously.
* **Cyber Telemetry Grid:** Displays VM Name, Status Badge, RAM, Resident Host, and inline operation buttons.
* **Client-Side Instant Search:** Filter across workloads and hostnames with live type-ahead filtering.
* **Bulk Multi-VM Operations:** Multi-select workloads to execute bulk **▶ Start**, **⏹ Stop**, **🔄 Restart**, and **📸 Snap** across the fleet.
* **AVHDX Checkpoint & Delta Chain Manager:** Deep-inspects snapshots, calculates cumulative `.avhdx` delta disk storage, and merges/purges stale snapshot chains with 1 click.

### Tab 3: Active Jobs & History
* **Real-Time Job Tracker:** Tracks every live migration with status badges (`RUNNING`, `COMPLETED`, `FAILED`, `ROLLED_BACK`), duration meters, and timestamps.
* **Streaming Terminal Console:** Streams PowerShell verbose, information, warning, and error records live into a cyber-glass terminal.
* **HTML Compliance Audit Exporter:** Generates standalone, self-contained HTML compliance audit reports with KPI summary cards, syntax-highlighted transcripts, and print-to-PDF formatting.

### Tab 4: Platform Settings
* **Atomic JSON Persistence:** Auto-loads and saves settings to `%AppData%\WinMigratePro\config.json` using atomic staging files to prevent 0-byte corruption.
* **Transport QoS & Performance Options:** Configure live migration acceleration modes (`Compression`, `SMB Multichannel`, `TCP`) and network bandwidth limits (Mbps).
* **Dedicated Live Migration Networks:** Detect physical host network adapters and assign dedicated 10GbE/25GbE migration subnets to prevent saturated client gaming links.
* **CredSSP Security Delegation:** One-click configuration of native Windows CredSSP client delegation.
* **Author & Network Attribution:** Official architectural credit badge for Oleg Melnikov and the ElectroMU Gaming Network.

---

## 3. Command-Line Deep-Linking & Automation

WinMigrate Pro supports direct CLI parameter ingestion for Windows Admin Center (WAC) deep-linking and scheduled task integration:

```cmd
WinMigratePro.exe [options]

Options:
  -s,  --source <host>         Source Hyper-V compute node (IP or FQDN)
  -t,  --target <host>         Target Hyper-V compute node (IP or FQDN)
  -u,  --user <domain\user>    Administrative username for source node
  -tu, --targetuser <user>     Administrative username for target node
  -d,  --storage <path>        Destination CSV/volume path (e.g. C:\ClusterStorage\Volume1)
  -v,  --vm <name>             Pre-select specific workload for migration
  -c,  --concurrency <1-8>     Simultaneous live migration limit (Default: 2)
       --performance <mode>    Transport QoS (Compression | SMB | TCP)
  -a,  --autoconnect           Automatically trigger discovery on launch
  -h,  --help                  Display CLI syntax manual
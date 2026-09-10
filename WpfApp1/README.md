⚡ WinMigrate Pro (ElectroMU Edition)

<p align="center">
  <strong>Native, Centralized, Agentless Hyper-V Live Migration Cockpit & VMware V2V Conversion Engine</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0%20%7C%209.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/C%23-13-239120?style=for-the-badge&logo=csharp&logoColor=white" alt="C# 13" />
  <img src="https://img.shields.io/badge/Platform-Windows%20Server%202022%20%2F%202025%20%7C%20Win%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows Server" />
  <img src="https://img.shields.io/badge/Architecture-100%25%20Agentless%20WSMan-00B4D8?style=for-the-badge" alt="Agentless" />
  <img src="https://img.shields.io/badge/License-GPLv3-00F5D4?style=for-the-badge" alt="License: GPLv3" />
</p>



 📌 Table of Contents
- [Executive Overview](#-executive-overview)
- [Why WinMigrate Pro? (Competitive Matrix)](#-why-winmigrate-pro-competitive-matrix)
- [Master System Architecture](#-master-system-architecture)
- [Deep-Dive Feature Breakdown](#-deep-dive-feature-breakdown)
  - [1. Migration Orchestrator (Live Workloads)](#1-migration-orchestrator-live-workload-cockpit)
  - [2. Global Virtual Machine Inventory](#2-global-virtual-machine-inventory--fleet-control)
  - [3. vCenter to Hyper-V V2V Importer & Multi-Disk Tiering](#3-vcenter-to-hyper-v-v2v-importer--multi-disk-tiering)
  - [4. Active Jobs, Logging & Compliance Audit](#4-active-jobs-telemetry--compliance-audit)
  - [5. Platform Settings & Subnet Isolation](#5-platform-settings--qos-subnet-isolation)
- [1-Click Infrastructure Doctor & Self-Healing](#-1-click-infrastructure-doctor--self-healing)
- [Command-Line Interface (CLI) & Automation](#-command-line-interface-cli--automation)
- [Deployment & Prerequisites](#-deployment--prerequisites)
- [Building from Source](#-building-from-source)
- [Security & Credential Architecture](#-security--credential-architecture)
- [Author & License](#-author--license)



🚀 Executive Overview

WinMigrate Pro (ElectroMU Edition)** is a native Windows desktop orchestration cockpit engineered as a high-performance, agentless alternative to System Center Virtual Machine Manager (SCVMM) and Windows Admin Center (WAC).

Designed for enterprise datacenters, high-density gaming clusters, and MSP environments, WinMigrate Pro unifies **Hyper-V Live Migration orchestration** and **VMware vSphere cold V2V disk conversion** into a single glassmorphism cockpit. It features OLE drag-and-drop live moves, multi-disk storage tiering (NVMe SSD vs. HDD), automated firmware parity resolution (BIOS/EFI ➔ Gen1/Gen2), pre-flight namespace collision guards, and 1-click self-healing for notorious Kerberos double-hop (`0x8009030D`) and CPU instruction (`0x80072740`) errors.



⚖ Why WinMigrate Pro? (Competitive Matrix)

| Feature / Capability | WinMigrate Pro | SCVMM | Windows Admin Center (WAC) | StarWind V2V |
| :--- | :---: | :---: | :---: | :---: |
| **Agentless Architecture** | ✅ Yes (Native WinRM) | ❌ Requires Host Agents | ✅ Web Gateway | ✅ Standalone |
| **Zero Database Footprint** | ✅ Atomic JSON | ❌ Heavy SQL Server | ✅ SQLite / None | ✅ None |
| **Visual OLE Drag-and-Drop** | ✅ Instant | ❌ Multi-Step Wizard | ❌ No | ❌ No |
| **1-Click Kerberos Self-Healing (`0x8009030D`)** | ✅ Automated Purge & Fix | ❌ Manual AD Config | ❌ Manual PowerShell | ❌ No |
| **CPU Architecture Mismatch Guard (`0x80072740`)** | ✅ Live Audit & 1-Click Fix | ❌ Manual Setting | ❌ Manual Setting | ❌ No |
| **Multi-vNIC Remapping Matrix** | ✅ Real-Time Mapping | ❌ Complex Logical Nets | ⚠️ Basic | ❌ No |
| **Direct vCenter V2V with Firmware Parity** | ✅ Auto Gen1/Gen2 Matching | ❌ Deprecated / Complex | ❌ No | ⚠️ Manual Selection |
| **Multi-Disk Storage Tiering (NVMe vs. HDD)** | ✅ Per-Disk Routing | ⚠️ Complex Profiles | ❌ Single Path | ❌ Single Target |
| **Pre-Flight Name Collision Engine** | ✅ Phase 0 Live Gatekeeper | ⚠️ Post-Validation | ❌ Fails Mid-Transfer | ❌ Overwrite Risk |
| **HTML Compliance Audit Exporter** | ✅ Print-to-PDF Ready | ⚠️ Requires SSRS | ❌ Basic Logs | ❌ Log File Only |


 🏛 Master System Architecture

 ┌─────────────────────────────────────────────────────────────────────────────────────────┐
 │                               WINMIGRATE PRO COCKPIT                                    │
 │                    .NET 10 / C# 13 Native Desktop Orchestrator                          │
 └─────────────┬───────────────────────────┬───────────────────────────┬───────────────────┘
               │ (vSphere REST API)        │ (Agentless WinRM / CIM)   │ (qemu-img Pipeline)
               ▼                           ▼                           ▼
 ┌───────────────────────────┐ ┌───────────────────────────┐ ┌───────────────────────────┐
 │     VMWARE VCENTER /      │ │     SOURCE HYPER-V        │ │      TARGET HYPER-V       │
 │        ESXi HOSTS         │ │       COMPUTE NODE        │ │       COMPUTE NODE        │
 ├───────────────────────────┤ ├───────────────────────────┤ ├───────────────────────────┤
 │ • vSphere REST v7.0/8.0   │ │ • Live Workload Discovery │ │ • Pre-Flight Gatekeeper   │
 │ • PowerCLI Snapshot Merge │ │ • CPU / RAM Telemetry     │ │ • Storage Tiering (NVMe)  │
 │ • VMDK Stream Extraction  │ │ • Live Move-VM Transport  │ │ • Multi-vNIC Remapping    │
 │ • Firmware Inspection     │ │ • 1-Click Infra Doctor    │ │ • Gen1/Gen2 Auto-Wiring   │
 └───────────────────────────┘ └───────────────────────────┘ └───────────────────────────┘

 🧩 Deep-Dive Feature Breakdown

1. Migration Orchestrator (Live Workload Cockpit)
Agentless WinRM Discovery: Discovers standalone and clustered Hyper-V nodes over WSMan with no agent footprint on compute hosts.
Visual OLE Drag-and-Drop: Drag running workloads directly from the Source list and drop them onto the Target Node card to stage a Live Migration.
Batch Evacuation Engine: Multi-select virtual machines with dynamic calculation of cumulative memory and processor requirements.
Automated Pre-Flight Gatekeeper:
Evaluates destination RAM availability with a dedicated host OS reserve buffer.
Calculates processor contention ratios.
Audits target virtual switch parity.
Validates storage backplane paths with a 20 GB safety headroom guard.
Audits active AVHDX checkpoint delta chains to prevent IOPS saturation during line-rate moves.
Live Telemetry Gauges: Real-time background polling of CPU load % and RAM utilization gauges with automated query throttling.
Live Hardware Tuner: Adjust vCPU allocation, static/dynamic memory boundaries (Min/Max/Buffer), and switch assignments on the fly.
Multi-vNIC Remapping Matrix: Remap multiple guest network adapters when migrating between hosts with disparate virtual switch topologies or VLAN IDs.
Disaggregated Multi-VHDX Storage Matrix: Route operating system virtual disks to high-speed NVMe CSVs and secondary data disks to high-capacity storage pools.
Maintenance Window Scheduler: Queue unattended batch evacuations using SemaphoreSlim concurrency throttling (1–8 parallel migrations) with automated post-migration guest heartbeat verification and automatic rollback.

2. Global Virtual Machine Inventory & Fleet Control
Multi-Host Aggregator: Centralized inventory querying all managed Hyper-V compute nodes simultaneously.
Bulk Fleet Control: Multi-select workloads across disparate hosts to execute unified ▶ Start, ⏹ Stop, 🔄 Restart, and 📸 Snap operations.
AVHDX Checkpoint & Delta Chain Manager: Inspect active snapshot trees, compute cumulative .avhdx delta disk storage consumption, and merge stale delta chains with 1 click.
Instant Type-Ahead Search: Real-time client-side filtering across workload names, hostnames, and IP subnets.

3. vCenter to Hyper-V V2V Importer & Multi-Disk Tiering
Direct vSphere REST Client: Direct API authentication against VMware vCenter / ESXi 7.0 & 8.0 with self-signed SSL verification bypass.
Automated Firmware Parity Engine:
VMware BIOS ➔ Provisioned as Generation 1 (IDE Boot Controller).
VMware EFI / Secure Boot ➔ Provisioned as Generation 2 (SCSI Synthetic Boot Controller).
Phase 0 Pre-Flight Name Collision Engine: Interrogates the target Hyper-V node over WinRM before initiating multi-gigabyte disk downloads or conversions, preventing wasted bandwidth and disk I/O.
Multi-Disk NVMe SSD vs. HDD Storage Tiering Matrix:
Route OS/Boot VMDK ➔ Fast NVMe SSD Pool (C:\ClusterStorage\Volume1_NVMe).
Route Data/Secondary VMDKs ➔ High-Capacity HDD Storage (D:\HyperV_HDD_Storage).
Chunked Conversion Pipeline: Standalone embedded qemu-img.exe conversion pipeline with live background byte/speed monitoring.
Live Target Node Explorer: Visualizes existing Hyper-V workloads on the destination host, complete with reactive neon collision badges (#EF4444) if an existing VM matches the selected VMware guest name.

4. Active Jobs, Telemetry & Compliance Audit
Real-Time Job Telemetry: Tracks active tasks with dynamic status indicators (RUNNING, COMPLETED, FAILED, ROLLED_BACK), duration stopwatches, and step metrics.
Streaming Cyber Terminal: Captures and formats PowerShell Information, Warning, Verbose, and Error records in a cyber-styled terminal window.
Standalone HTML Compliance Audit Exporter: Generates standalone, self-contained HTML audit reports complete with KPI cards, transcript logs, and clean print-to-PDF formatting.

5. Platform Settings & QoS Subnet Isolation
Atomic JSON Configuration: Configuration persists to %AppData%\WinMigratePro\config.json using atomic write/replace operations to prevent zero-byte corruption during sudden power events.
Transport QoS & Performance Options: Configure Live Migration transport modes (Compression, SMB Multichannel, TCP) and assign strict bandwidth ceilings (Mbps).
Dedicated Live Migration Subnet Selector: Automatically inspects host network adapters to isolate migration traffic to high-speed 10GbE/25GbE interfaces, preserving client bandwidth on gaming and production networks.
🩺 1-Click Infrastructure Doctor & Self-Healing
WinMigrate Pro features built-in diagnostics for resolving common Hyper-V Live Migration errors:

Text
[ISSUE] Kerberos Authentication Failure (0x8009030D)
 ├── Root Cause: Stale Kerberos SYSTEM tickets (0x3e7) or missing Active Directory Constrained Delegation.
 └── 1-Click Auto-Repair:
      • Executes 'klist purge -li 0x3e7' to flush stale security tokens.
      • Audits and configures Microsoft Virtual System Migration Service SPNs.
      • Validates mutual Kerberos trust across source and target nodes.

[ISSUE] CPU Architecture Mismatch (0x80072740)
 ├── Root Cause: Disparate Intel/AMD CPU instruction sets between cluster nodes.
 └── 1-Click Auto-Repair:
      • Interrogates CPU instruction sets on source and target compute nodes.
      • Toggles 'CompatibilityForMigrationMode' to mask mismatched instruction sets.
💻 Command-Line Interface (CLI) & Automation
WinMigrate Pro supports direct CLI parameter ingestion for Windows Admin Center (WAC) deep-linking and automated scheduler integrations:

Powershell
WinMigratePro.exe [options]
CLI Options Reference
Flag	Long Argument	Description	Example
-s	--source	Source Hyper-V Hostname or IP	-s 192.168.1.10
-t	--target	Target Hyper-V Hostname or IP	-t 192.168.1.20
-u	--user	Administrative user for source host	-u "CORP\admin"
-tu	--targetuser	Administrative user for target host	-tu "CORP\admin"
-d	--storage	Target storage volume / CSV path	-d "C:\ClusterStorage\Volume1"
-v	--vm	Specific VM name to pre-select	-v "DB-Prod-01"
-c	--concurrency	Concurrency limit (1–8 parallel moves)	-c 4
--performance	Transport QoS Mode (Compression / SMB / TCP)	--performance SMB
-a	--autoconnect	Automatically trigger discovery on launch	-a
-h	--help	Display CLI syntax manual	-h

📦 Deployment & Prerequisites
System Requirements
Management Workstation: Windows 10 / 11 (x64) or Windows Server 2022 / 2025.
Target Hyper-V Hosts: Windows Server 2016 / 2019 / 2022 / 2025, or Hyper-V Server.
VMware vCenter / ESXi: vSphere 6.7 / 7.0 / 8.0 REST API.
Runtime: .NET 10.0 Desktop Runtime or .NET 9.0 Desktop Runtime.
Firewall Ports:
TCP 5985 / 5986 (WinRM HTTP/HTTPS)
TCP 445 (SMB Management / Administrative Shares)
TCP 443 (vSphere REST API / Datastore HTTPS Streaming)
🔨 Building from Source


Powershell
# 1. Clone the repository
git clone https://github.com/YourUsername/WinMigratePro.git
cd WinMigratePro

# 2. Restore NuGet dependencies
dotnet restore

# 3. Build Release binary (x64)
dotnet build -c Release -r win-x64

# 4. Publish self-contained standalone executable
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
info
Ensure companion binaries (qemu-img.exe and associated MinGW runtime DLLs) reside inside the Tools\ subfolder alongside WinMigratePro.exe.

🔒 Security & Credential Architecture
Zero-Agent Footprint: Installs no permanent services, drivers, or background listeners on production virtualization hosts.
Memory-Only Credential Safety: Host passwords and tokens are held in volatile memory using SecureString and never written unencrypted to disk.
Native Windows Authentication: Leverages native Negotiate/Kerberos and CredSSP authentication protocols via standard Windows WSMan channels.

👤 Author & Enterprise Consulting

* **Lead Systems Architect**: **Oleg Melnikov**
* **LinkedIn**: [![LinkedIn Profile](https://img.shields.io/badge/LinkedIn-Oleg%20Melnikov-0077B5?style=for-the-badge&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/oleg-melnikov-4b694a164/)
* **Organization**: [ElectroMU Gaming Network](https://electromu.net)
* **License**: Distributed under the [GNU General Public License v3.0 (GPLv3)](LICENSE).


Copyright (C) 2026 Oleg Melnikov (ElectroMU Gaming Network)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

using System;
using System.Collections.Generic;

namespace WpfApp1.Services;

/// <summary>
/// Represents a physical datacenter site, availability zone, or campus boundary 
/// hosting compute nodes, virtual switch fabrics, and storage classifications.
/// </summary>
public class DatacenterModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string EnvironmentTier { get; set; } = "Production"; // "Production", "DisasterRecovery", "Staging", "Edge"
    public string DomainFqdn { get; set; } = string.Empty;
    public bool IsDrSite { get; set; }
    public bool IsActive { get; set; } = true;

    public List<string> Networks { get; set; } = [];
    public List<string> StorageClassifications { get; set; } = [];
    public List<string> Hostnames { get; set; } = [];
    public List<string> ClusterNames { get; set; } = [];

    public int TotalHostCount => Hostnames.Count;

    public string DisplayText => $"{Name} ({Location}) - [{EnvironmentTier}]";

    public DatacenterModel()
    {
    }

    public DatacenterModel(
        Guid id,
        string name,
        string location,
        IEnumerable<string> networks,
        IEnumerable<string> storageClassifications)
    {
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Location = location ?? throw new ArgumentNullException(nameof(location));

        if (networks != null)
        {
            Networks.AddRange(networks);
        }

        if (storageClassifications != null)
        {
            StorageClassifications.AddRange(storageClassifications);
        }
    }

    public override string ToString() => DisplayText;
}
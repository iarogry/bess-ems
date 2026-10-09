using BatteryEms.Application.Orchestration;
using BatteryEms.Application.Planning;

namespace BatteryEms.Host.Planning;

public sealed class EmsPlanningOptions
{
    public bool Enabled { get; set; }
    public bool PrepareOnStartup { get; set; }
    public string PlanDirectory { get; set; } = string.Empty;
    public System.Collections.ObjectModel.Collection<EquipmentPlanningTarget> Targets { get; } = [];
    public bool DayAuthorizationsEnabled { get; set; }
    public string? WriterOwnerId { get; set; }
    public bool ActivationEnabled { get; set; }
    public string? ActivationBindingsDirectory { get; set; }
    public Uri? BrokerBaseUrl { get; set; }
    public string? BrokerToken { get; set; }
    public ShadowDeyeProjectionOptions Deye { get; set; } = new();
}

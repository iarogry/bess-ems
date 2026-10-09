using BatteryEms.Domain;
using BatteryEms.Application.Site;

namespace BatteryEms.Application.Configuration;

public interface IConfigurationLoader
{
    BatteryAsset LoadAsset(string filePath);

    IReadOnlyList<BatteryAsset> LoadAssets(string filePath);

    ModbusMappingConfiguration LoadModbusMapping(string filePath);

    MqttMappingConfiguration LoadMqttMapping(string filePath);

    OpcUaMappingConfiguration LoadOpcUaMapping(string filePath);

    Schedule LoadSchedule(string filePath);

    RetentionPolicy LoadRetentionPolicy(string filePath);

    SiteBalanceConfiguration LoadSiteBalanceConfiguration(string filePath);
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException() { }

    public ConfigurationValidationException(string message) : base(message) { }

    public ConfigurationValidationException(string message, Exception inner) : base(message, inner) { }
}

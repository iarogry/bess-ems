namespace BatteryEms.BrokerHost;

// Separate application-server process. The normal BESS host never maps this API.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1052",
    Justification = "An instance Program marker supports an isolated ASP.NET test host.")]
public class Program
{
    protected Program() { }

    public static void Main(string[] args)
    {
        using var app = DeviceWriteBrokerHostBuilder.BuildApp(args);
        app.Run();
    }
}

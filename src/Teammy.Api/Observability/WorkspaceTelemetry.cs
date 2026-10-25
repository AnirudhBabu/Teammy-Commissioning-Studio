using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Teammy.Api.Observability;

public static class WorkspaceTelemetry
{
    public const string Name = "Teammy.Workspace";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Commands = Meter.CreateCounter<long>("teammy.workspace.commands", "{command}");
    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>("teammy.workspace.command.duration", "s");
}

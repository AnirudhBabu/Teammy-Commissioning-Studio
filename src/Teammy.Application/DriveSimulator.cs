using System.Collections.Immutable;
using System.Globalization;
using Teammy.Domain;

namespace Teammy.Application;

public interface IDriveSimulator
{
    SimulationRun Simulate(DriveConfiguration configuration, DriveProfile profile, string runId, Actor actor, DateTimeOffset now);
}

/// <summary>
/// A deterministic engineering demonstration, not a hardware driver or a certified motor model.
/// Values make configuration constraints visible; they must never authorize real equipment.
/// </summary>
public sealed class DeterministicDriveSimulator : IDriveSimulator
{
    public SimulationRun Simulate(DriveConfiguration configuration, DriveProfile profile, string runId, Actor actor, DateTimeOffset now)
    {
        ConfigurationRules.Validate(configuration, profile);
        var accelerationRate = configuration.TargetSpeedRpm / configuration.AccelerationSeconds;
        var decelerationRate = configuration.TargetSpeedRpm / configuration.DecelerationSeconds;
        var accelerationStress = accelerationRate / profile.MaxRampRateRpmPerSecond;
        var steadyCurrent = profile.RatedCurrentAmps * (0.2 + configuration.LoadPercent * 0.008);
        var peakCurrent = profile.RatedCurrentAmps * (0.3 + configuration.LoadPercent * 0.0075 + accelerationStress * 0.15);
        var peakTemperature = 25 + configuration.LoadPercent * 0.5 + Math.Pow(peakCurrent / profile.RatedCurrentAmps, 2) * 12;

        var checks = ImmutableArray.Create(
            new SimulationCheck("acceleration-envelope", "Acceleration envelope", accelerationRate <= profile.MaxRampRateRpmPerSecond,
                Invariant($"{accelerationRate:F1} rpm/s; profile limit {profile.MaxRampRateRpmPerSecond:F1} rpm/s.")),
            new SimulationCheck("deceleration-envelope", "Deceleration envelope", decelerationRate <= profile.MaxRampRateRpmPerSecond,
                Invariant($"{decelerationRate:F1} rpm/s; profile limit {profile.MaxRampRateRpmPerSecond:F1} rpm/s.")),
            new SimulationCheck("current-headroom", "Current headroom", peakCurrent <= configuration.CurrentLimitAmps,
                Invariant($"Estimated peak {peakCurrent:F2} A; configured limit {configuration.CurrentLimitAmps:F2} A.")),
            new SimulationCheck("thermal-envelope", "Thermal envelope", peakTemperature <= profile.MaxTemperatureCelsius,
                Invariant($"Estimated peak {peakTemperature:F1} C; profile limit {profile.MaxTemperatureCelsius:F1} C.")));

        var passed = checks.All(check => check.Passed);
        var duration = configuration.AccelerationSeconds + 2 + configuration.DecelerationSeconds;
        var samples = ImmutableArray.CreateBuilder<TelemetrySample>(25);
        for (var index = 0; index < 25; index++)
        {
            var seconds = duration * index / 24;
            var speed = seconds <= configuration.AccelerationSeconds
                ? configuration.TargetSpeedRpm * seconds / configuration.AccelerationSeconds
                : seconds <= configuration.AccelerationSeconds + 2
                    ? configuration.TargetSpeedRpm
                    : configuration.TargetSpeedRpm * Math.Max(0, 1 - (seconds - configuration.AccelerationSeconds - 2) / configuration.DecelerationSeconds);
            var current = seconds < configuration.AccelerationSeconds ? peakCurrent : steadyCurrent;
            if (index == 0 || index == 24)
            {
                current = 0;
            }

            samples.Add(new TelemetrySample(
                Math.Round(seconds, 3), Math.Round(speed, 2), Math.Round(current, 3),
                Math.Round(25 + (peakTemperature - 25) * seconds / duration, 2)));
        }

        return new SimulationRun(runId, configuration.Id, configuration.Revision, actor.Id, now, passed, checks, samples.MoveToImmutable());
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}

internal static class ConfigurationRules
{
    public static void Validate(DriveConfiguration configuration, DriveProfile profile)
    {
        Range(configuration.TargetSpeedRpm, 1, profile.MaxSpeedRpm, "Target speed");
        Range(configuration.AccelerationSeconds, 0.25, 120, "Acceleration ramp");
        Range(configuration.DecelerationSeconds, 0.25, 120, "Deceleration ramp");
        Range(configuration.CurrentLimitAmps, 0.1, profile.RatedCurrentAmps * 1.25, "Current limit");
        Range(configuration.LoadPercent, 0, 100, "Load");
    }

    private static void Range(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new WorkspaceException(WorkspaceErrorKind.Validation, "validation_failed",
                FormattableString.Invariant($"{name} must be between {minimum} and {maximum}."));
        }
    }
}

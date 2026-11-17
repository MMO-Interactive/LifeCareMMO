using System.Collections.Immutable;

namespace StormChase.Protocol;

/// <summary>Reduced environmental sounding. Heights are AGL; wind components point east (+X) and north (+Z).</summary>
public sealed record AtmosphereLevel(float HeightMeters, float PressureHpa, float TemperatureCelsius,
    float DewPointCelsius, float WindEastMetersPerSecond, float WindNorthMetersPerSecond,
    float ParcelTemperatureCelsius = 0);

/// <summary>
/// Surface-parcel diagnostics derived from the sounding, not assigned forecast labels.
/// CIN is signed negative. STP is the SPC fixed-layer surface-parcel formulation;
/// VtpGame is a gameplay extension, not the operational effective-layer VTP.
/// Composites describe the environment and do not guarantee tornadoes or EF ratings.
/// </summary>
public sealed record AtmosphereDiagnostics(float CapeJkg, float CinJkg, float Cape03KmJkg,
    float LclMeters, float LfcMeters, float EquilibriumLevelMeters,
    float Shear01KmMetersPerSecond, float Shear06KmMetersPerSecond,
    float Srh01KmM2S2, float Srh03KmM2S2, NetVector3 StormMotion,
    float Stp, float VtpGame, float SurfaceTemperatureCelsius, float DewPointCelsius,
    float LowLevelLapseRateCPerKm, float InitiationPotential01, float TornadoPotential01);

/// <summary>Deterministic daily outlook sampled at 15:00 and the following 02:00; risk is a gameplay outlook.</summary>
public sealed record AtmosphereDayForecast(int DayNumber, string Name, float Risk,
    AtmosphereDiagnostics Peak, AtmosphereDiagnostics Overnight);

/// <summary>Server-owned sample and outlook. Collections are immutable and serialize as ordinary JSON arrays.</summary>
public sealed record AtmosphereSnapshot(long ServerTick, double CampaignSeconds, int DayNumber,
    float Hour, NetVector3 SamplePosition, ImmutableArray<AtmosphereLevel> Sounding,
    AtmosphereDiagnostics Diagnostics, ImmutableArray<AtmosphereDayForecast> ThreeDayForecast,
    int ModelVersion = 1, float CampaignStartHour = 8, double CampaignDayLengthSeconds = 1200);

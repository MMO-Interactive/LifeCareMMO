using Engine.Environment;
using StormChase.Networking;
using StormChase.Protocol;

namespace StormChase.Server;

/// <summary>The campaign weather model run inside this process: single-player-style hosting without a climate process.</summary>
internal sealed class LocalClimate(StormChaseCampaignWeather weather) : IClimateFeed
{
    public int Seed => weather.Seed;
    public double ElapsedSeconds => weather.ElapsedSeconds;
    public float Hour => weather.Hour;
    public int DayNumber => weather.DayNumber;
    public string ForecastName => weather.ForecastName;
    public float StartHour => weather.StartHour;
    public EnvironmentSceneState Current => weather.Current;
    public AtmosphereSnapshot Atmosphere => weather.Atmosphere;
    public void Update(double elapsedWallSeconds) => weather.Update(elapsedWallSeconds);
    public void Dispose() { }
}

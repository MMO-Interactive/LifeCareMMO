/// <summary>
/// A player job. Storm chaser is the original game; the emergency services issue a department vehicle and fill the
/// dispatch board with their own calls (StormChaseContracts.Jobs.cs). To add a job: append it here, give it a
/// department vehicle (StormChaseCareer.Vehicles, the server's NativeCareerAdapter specs and a Blender model), and
/// add its calls in CreateJobOffers. The online wire index is the position in <see cref="StormChaseJobs.All"/>.
/// </summary>
internal sealed record StormChaseJob(string Id, string Title, string Department, string? DutyVehicle, string Summary);

internal static class StormChaseJobs
{
    public const string Chaser = "chaser", Police = "police", Fire = "fire", Ems = "ems";

    public static readonly StormChaseJob[] All =
    [
        new(Chaser, "Storm chaser", "Field research", null,
            "Chase storms in your own vehicle: probes, photographs, research and community work."),
        new(Police, "Police officer", "County sheriff", "police-cruiser",
            "Warn towns in a tornado's path, secure damaged buildings and keep the highways safe."),
        new(Fire, "Firefighter", "County fire department", "fire-engine",
            "Search collapsed buildings, clear storm debris and keep shelters and stations ready."),
        new(Ems, "EMT", "County EMS", "ambulance",
            "Treat people injured by the storm, transport them to the clinic and staff shelters."),
    ];

    public static StormChaseJob? Find(string? id) => All.FirstOrDefault(job => job.Id == id);
    public static StormChaseJob Get(string? id) => Find(id) ?? All[0];
    public static bool IsDutyVehicle(string vehicleId) => All.Any(job => job.DutyVehicle == vehicleId);
    /// <summary>Online action/target index of a job (its position in <see cref="All"/>), or -1.</summary>
    public static int WireIndex(string id) => Array.FindIndex(All, job => job.Id == id);
}

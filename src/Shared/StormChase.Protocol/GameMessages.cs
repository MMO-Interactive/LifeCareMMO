namespace StormChase.Protocol;

public enum MessageKind : ushort { JoinRequest = 1, JoinAccepted, JoinRejected, ClientInput, WorldSnapshot, GameplayAction, ActionResult, Ping, Pong, DebrisEvent, DebrisSnapshot, AtmosphereSnapshot }
public enum PlayerMovementMode { OnFoot, Driving, Fly }
public enum GameplayActionKind { EnterVehicle, ExitVehicle, DeployProbe, TakePhoto, RepairVehicle, BuyProbe, RecoverProbe, Respawn, Purchase, SelectVehicle, Refuel, ToggleAnchors, RecoverVehicle, AcceptContract, ContractInteract, CancelContract, Salvage, BuyHouse, BuyFurniture, PlaceFurniture, VisitHome, SetHomeVisits, SelectJob }
public readonly record struct NetVector3(float X, float Y, float Z)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}
public readonly record struct NetQuaternion(float X, float Y, float Z, float W)
{
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W);
    public static NetQuaternion Identity => new(0, 0, 0, 1);
}
/// <summary>All currency is integer cents, including persisted wallets and action receipts.</summary>
public sealed record CareerProgress(long CashCents, int Reputation, int Probes, int Photos, int CompletedChases, string Rank, int Popularity = 0);
public sealed record JoinRequest(string Ticket, string WorldContentFingerprint = "");
public sealed record JoinAccepted(uint PlayerId, Guid CharacterId, CareerProgress Career, WorldSnapshot Snapshot);
public sealed record JoinRejected(string Reason);
public sealed record ClientInput(long ClientTick, NetVector3 Position, float Yaw, float Pitch, PlayerMovementMode Mode, int VehicleId, bool Sprint, float Forward = 0, float Strafe = 0, float Turn = 0, bool Brake = false, bool Jump = false);
public sealed record PlayerState(uint PlayerId, string Name, NetVector3 Position, float Yaw, PlayerMovementMode Mode, int VehicleId, long ClientTick = 0, float Pitch = 0, NetVector3 Velocity = default);
public sealed record VehicleState(int VehicleId, NetVector3 Position, float Yaw, NetVector3 Velocity, float Health, uint DriverId, NetQuaternion Rotation = default, NetVector3 AngularVelocity = default, float FuelLiters = 0, bool Anchored = false, int VehicleType = 0);
public sealed record TornadoState(int Episode, string Stage, NetVector3 Position, float Intensity, float WindMph, float Radius, float FunnelReach, float AgeSeconds);
public sealed record StormState(int StormId, NetVector3 Position, string Stage, float AgeSeconds, float Strength, float Radius, float Heading, float Speed, TornadoState? Tornado);
public sealed record DestructionDelta(long EventId, int ObjectId, NetVector3 Position, NetVector3 Velocity, bool Destroyed, bool PowerFlash);
public sealed record ProbeState(int ProbeId, uint OwnerId, NetVector3 Position, float WindMph, float PressureMb, bool Active);
public enum DebrisEventKind { Released, Removed }
/// <summary>Stable IDs and server poses are authoritative for every physical fragment, including sleeping bodies.</summary>
public sealed record DebrisBodyState(int BodyId, int ObjectId, NetVector3 Position, NetQuaternion Rotation, NetVector3 LinearVelocity, NetVector3 AngularVelocity, bool Sleeping, bool CollisionEnabled, string StablePartId = "", bool Archived = false, bool Collected = false);
public sealed record DebrisEvent(long EventId, long BaselineRevision, DebrisEventKind Kind, DebrisBodyState Body);
/// <summary>A full baseline replaces a client's body catalogue before collisions are enabled; motion updates do not remove omitted bodies.</summary>
public sealed record DebrisSnapshot(long ServerTick, long BaselineRevision, bool FullBaseline, DebrisBodyState[] Bodies, int ChunkIndex = 0, int ChunkCount = 1);
public sealed record ActivityState(int Id, string Title, string Kind, NetVector3 Position, int StepCount, long RewardCashCents, int RewardReputation, bool Available, string Description);
public sealed record MissionState(int Id, string Title, string Kind, int Step, int StepCount, string Objective, NetVector3 TargetPosition, bool Complete, float WorkSeconds = 0, float RequiredWorkSeconds = 0);
public sealed record WorldSnapshot(long ServerTick, double SimulationSeconds, int Seed, PlayerState[] Players, VehicleState[] Vehicles, StormState[] Storms, DestructionDelta[] Destruction, ProbeState[] Probes, DebrisSnapshot? Debris = null, Engine.Environment.EnvironmentSceneState? Environment = null, MissionState? Mission = null, ActivityState[]? Activities = null, CareerProgress? Career = null, HousingState? Housing = null, AtmosphereSnapshot? Atmosphere = null);
public sealed record GameplayAction(long RequestId, GameplayActionKind Kind, int TargetId, NetVector3 Position, int Slot = -1,float PlacementYaw = 0);
public sealed record ActionResult(long RequestId, bool Success, string Message, CareerProgress? Career = null);
public sealed record Ping(long Timestamp);

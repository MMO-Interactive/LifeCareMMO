namespace StormChase.Server;

public sealed record CredentialsRequest(string Username, string Password);
public sealed record AccountDto(string AccountId, string Username);
public sealed record CharacterCreateRequest(string Name);
public sealed record CharacterDto(string Id, string Name, long Reputation, DateTimeOffset CreatedUtc);
public sealed record VehicleConditionDto(float FuelLiters, float Health);
public sealed record CareerDto(string CharacterId, decimal Cash, int ProbeLevel, int CameraLevel, bool Mesonet,
    string[] OwnedVehicles, string SelectedVehicle, Dictionary<string, VehicleConditionDto> VehicleConditions,
    long Reputation, long Version, int Photos=0, int ResearchSamples=0, int CompletedChases=0,int Popularity=0,int CompletedContracts=0,string ContractState="");
public sealed record PurchaseRequest(string Sku, string OperationId);
public sealed record PurchaseDto(bool Purchased, bool Duplicate, string Message, CareerDto Career);
public sealed record SelectVehicleRequest(string VehicleId);
public sealed record JoinTicketRequest(string CharacterId,string? ServerId=null);
public sealed record JoinTicketDto(string Ticket, DateTimeOffset ExpiresUtc, string Host, int GamePort,
    string ServerId, string CharacterId,string ApiAddress="");
public sealed record ServerInfoDto(string ServerId, string Name, string Region, int Players, int MaxPlayers, string Mode,
    string Version, string Host, int HttpPort, int GamePort, bool AuthenticationRequired,
    int WorldSeed, float WorldSizeKm, double CampaignSeconds, float Hour, int Day, string Forecast, int StormCount,
    bool Ready=false, string WorldContentFingerprint="", string TransportIdentity="", string TransportAuthority="",string ApiAddress="",int RelayPort=0);
public sealed record OfferDto(string Sku, string Name, decimal Price, string Description);
public sealed record ApiError(string Error);
public sealed record AuthenticatedCharacter(string AccountId, string CharacterId, string Name, CareerDto Career);
public sealed record AccountRecord(string Id, string Username, string PasswordHash);

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal sealed partial class StormChaseWorld
{
    public string ContentFingerprint {get;private set;}="";

    /// <summary>
    /// Earlier world versions whose existing houses (building ids, HomeIds and placement) are unchanged in this one,
    /// so homes bought there stay owned. 0b919913… is the world before the Gulf coast / Gull Harbor update; its 607
    /// buildings keep their ids and exact positions (artifacts/world-rework-backup, lot-smoke comparison).
    /// Only add a fingerprint here after verifying that; otherwise leave saved homes tied to their own world.
    /// </summary>
    public static IReadOnlyList<string> HousingCompatibleFingerprints {get;}=["0b919913cfacd5d9e87243d67ccc77322b1661ba0d3b84e7a86090b006dde8a7"];
    public bool IsHousingCompatible(string fingerprint)=>fingerprint==ContentFingerprint||HousingCompatibleFingerprints.Contains(fingerprint,StringComparer.Ordinal);
    /// <summary>Homes in settlements added after release number after every original home, so saved HomeIds keep naming the same house.</summary>
    public static bool IsAppendedHome(string buildingId)=>buildingId.StartsWith("building/gullharbor/",StringComparison.Ordinal);

    // Assembly hashes differ between the headless server and the graphical
    // client. Hash the generated content itself, including every aperture,
    // physical module transform, authored mesh and actual road/terrain mesh.
    private string ComputeContentFingerprint()
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer=new ContentHashWriter(hash);
        void Text(string value)
        {
            byte[] utf8=Encoding.UTF8.GetBytes(value);writer.UInt((uint)utf8.Length);writer.Bytes(utf8);
        }
        void Number(float value)
        {
            writer.UInt((uint)BitConverter.SingleToInt32Bits(value));
        }
        void Point(Vector3f value){Number(value.X);Number(value.Y);Number(value.Z);}
        Text("MMOI/StormChase/GeneratedWorld/v4/ClientVegetation");
        Number(PlayableExtentMeters);Number(PlayableCenterZ);Number(LakeCenterX);Number(LakeCenterZ);
        Number(LakeRadiusX);Number(LakeRadiusZ);Number(LakeWaterLevel);Number(BuildingRoadClearanceMeters);Number(BuildingLotClearanceMeters);
        foreach(var pad in SettlementPads){Number(pad.X);Number(pad.Z);Number(pad.Height);Number(pad.Radius);}
        foreach(var building in buildings.OrderBy(b=>b.Id,StringComparer.Ordinal))
        {
            Text(building.Id);Text(building.Kind);Text(building.SettlementId);Point(building.Position);Number(building.YawDegrees);Number(building.Scale);
            foreach(string id in building.DestructiblePartIds.OrderBy(id=>id,StringComparer.Ordinal))Text(id);
        }
        var items=destructible.Concat(staticCollision).Concat(roads).Concat(scenery.Select(p=>p.Item))
            .OrderBy(i=>i.MotionVectorId??i.Mesh.Id,StringComparer.Ordinal).ThenBy(i=>i.Position.X).ThenBy(i=>i.Position.Z).ThenBy(i=>i.Position.Y).ToArray();
        foreach(var item in items)
        {
            Text(item.MotionVectorId??"");Text(item.Mesh.Id);Text(item.MaterialId);Point(item.Position);Point(item.Scale);
            Point(item.RotationEulerDegrees??new Vector3f(0,item.RotationYawDegrees,0));
        }
        var meshes=items.Select(i=>i.Mesh).Concat(pages.SelectMany(p=>p.Meshes)).Distinct().OrderBy(m=>m.Id,StringComparer.Ordinal);
        foreach(var handle in meshes)
        {
            Text(handle.Id);
            if(!catalog.TryGetMesh(handle,out var mesh)||mesh is null)throw new InvalidOperationException("Fingerprint lost world mesh: "+handle.Id);
            Number(mesh.Vertices.Count);
            foreach(var vertex in mesh.Vertices)Point(vertex.Position);
            var indices=mesh.WideIndices.Count>0?mesh.WideIndices:mesh.Indices.Select(i=>(uint)i).ToArray();
            Number(indices.Count);
            foreach(uint index in indices)writer.UInt(index);
        }
        foreach(var station in gasStations.OrderBy(s=>s.Id,StringComparer.Ordinal))
        {Text(station.Id);Text(station.BuildingId);Point(station.Position);Point(station.ParkingPosition);}
        writer.Flush();return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class ContentHashWriter(IncrementalHash hash)
    {
        private readonly byte[] buffer=new byte[65536];private int count;
        public void UInt(uint value)
        {
            if(count>buffer.Length-4)Flush();
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(count,4),value);count+=4;
        }
        public void Bytes(ReadOnlySpan<byte> value)
        {
            while(!value.IsEmpty)
            {
                int length=Math.Min(value.Length,buffer.Length-count);value[..length].CopyTo(buffer.AsSpan(count));count+=length;value=value[length..];
                if(count==buffer.Length)Flush();
            }
        }
        public void Flush(){if(count>0){hash.AppendData(buffer.AsSpan(0,count));count=0;}}
    }
}

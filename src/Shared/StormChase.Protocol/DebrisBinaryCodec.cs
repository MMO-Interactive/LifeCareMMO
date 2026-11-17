using System.Text;

namespace StormChase.Protocol;

/// <summary>Compact authoritative body poses. Twelve motion bodies with IDs supplied by the baseline fit in one 1200-byte native datagram.</summary>
internal static class DebrisBinaryCodec
{
    internal static byte[] Encode(DebrisSnapshot value)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(value.ServerTick); writer.Write(value.BaselineRevision); writer.Write(value.FullBaseline);
        writer.Write((ushort)value.ChunkIndex); writer.Write((ushort)value.ChunkCount); writer.Write((ushort)value.Bodies.Length);
        foreach (var body in value.Bodies)
        {
            writer.Write(body.BodyId); writer.Write(body.ObjectId); Vector(writer, body.Position);
            writer.Write(body.Rotation.X); writer.Write(body.Rotation.Y); writer.Write(body.Rotation.Z); writer.Write(body.Rotation.W);
            Vector(writer, body.LinearVelocity); Vector(writer, body.AngularVelocity);
            writer.Write((byte)((body.Sleeping ? 1 : 0) | (body.CollisionEnabled ? 2 : 0) | (body.Archived ? 4 : 0) | (body.Collected ? 8 : 0)));
            var part = Encoding.UTF8.GetBytes(body.StablePartId);
            writer.Write((ushort)part.Length); writer.Write(part);
        }
        return stream.ToArray();
    }

    internal static DebrisSnapshot? Decode(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), false); using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var tick = reader.ReadInt64(); var revision = reader.ReadInt64(); var full = reader.ReadByte();
        var chunk = reader.ReadUInt16(); var chunks = reader.ReadUInt16(); var count = reader.ReadUInt16();
        if (full > 1 || count > 4096 || count * 63L > stream.Length - stream.Position) return null;
        var bodies = new DebrisBodyState[count];
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadInt32(); var objectId = reader.ReadInt32(); var position = Vector(reader);
            var rotation = new NetQuaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var linear = Vector(reader); var angular = Vector(reader); var flags = reader.ReadByte(); var stringBytes = reader.ReadUInt16();
            if ((flags & ~15) != 0 || stringBytes > 512 || stringBytes > stream.Length - stream.Position) return null;
            var part = new UTF8Encoding(false, true).GetString(reader.ReadBytes(stringBytes));
            bodies[i] = new(id, objectId, position, rotation, linear, angular, (flags & 1) != 0, (flags & 2) != 0, part, (flags & 4) != 0, (flags & 8) != 0);
        }
        return stream.Position == stream.Length ? new(tick, revision, full == 1, bodies, chunk, chunks) : null;
    }
    private static void Vector(BinaryWriter writer, NetVector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    private static NetVector3 Vector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DragNWashCoop;

internal enum PacketKind : byte
{
    Hello = 1,
    Welcome,
    Reject,
    Pose,
    ActionRequest,
    WorldEvent,
    WorldState,
    SceneChange,
    SceneReady,
    SnapshotChunk,
    SnapshotEnd,
    PlayerLeft,
    Ping,
    DragonPose,
    LevelChange,
    ObjectState,
    Emote,
    Colours
}

internal enum WorldEventKind : byte
{
    Interact = 1,
    DragonState,
    FlagBool,
    FlagString,
    Weather,
    ToolGrant,
    ToolEquip,
    HandContact,
    SprayContact,
    Decal,
    StoryAdvance,
    Pause,
    DialogueStart,
    DialogueNext,
    DialogueOption,
    Gate,
    RackFill,
    Performance,
    SexAct,
    Cum,
    RackSpray,
    Intermission,
    LevelRespawn,
    SpongeContact,
    SplashContact,
    Bark,
    BarkEnd
}

internal enum CoopRole : byte { None, Host, Guest }

/// <summary>Every packet is framed by Steam; this header protects the game from stale scene data.</summary>
internal readonly struct Envelope
{
    internal const uint Magic = 0x43574E44; // DNWC
    internal const ushort Version = 6;   // 5: poses carry the sender's clock (avatar interpolation); 6: yips, colours
    internal const int MaxPacketBytes = 48 * 1024;

    internal readonly PacketKind Kind;
    internal readonly uint SceneEpoch;
    internal readonly uint Sequence;
    internal readonly BinaryReader Reader;

    private Envelope(PacketKind kind, uint epoch, uint sequence, BinaryReader reader)
    {
        Kind = kind;
        SceneEpoch = epoch;
        Sequence = sequence;
        Reader = reader;
    }

    internal static byte[] Encode(PacketKind kind, uint epoch, uint sequence, Action<BinaryWriter>? payload = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((byte)kind);
        writer.Write(epoch);
        writer.Write(sequence);
        payload?.Invoke(writer);
        writer.Flush();
        if (stream.Length > MaxPacketBytes) throw new InvalidDataException("Co-op packet exceeds size limit");
        return stream.ToArray();
    }

    internal static bool TryDecode(byte[] bytes, out Envelope result)
    {
        result = default;
        if (bytes.Length < 15 || bytes.Length > MaxPacketBytes) return false;
        var reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8);
        try
        {
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version) { reader.Dispose(); return false; }
            var kind = (PacketKind)reader.ReadByte();
            if (!Enum.IsDefined(typeof(PacketKind), kind)) { reader.Dispose(); return false; }
            result = new Envelope(kind, reader.ReadUInt32(), reader.ReadUInt32(), reader);
            return true;
        }
        catch (EndOfStreamException) { reader.Dispose(); return false; }
    }
}

internal static class NetBinary
{
    internal static void Write(this BinaryWriter writer, Vector3 p)
    {
        writer.Write(p.x); writer.Write(p.y); writer.Write(p.z);
    }

    internal static void Write(this BinaryWriter writer, Quaternion q)
    {
        writer.Write(q.x); writer.Write(q.y); writer.Write(q.z); writer.Write(q.w);
    }

    internal static Vector3 ReadVector3(this BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    internal static Quaternion ReadQuaternion(this BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    internal static void WriteShortString(this BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > 1024) throw new InvalidDataException("String too long");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadShortString(this BinaryReader reader)
    {
        int len = reader.ReadUInt16();
        if (len > 1024) throw new InvalidDataException("String too long");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    internal static void WriteLongString(this BinaryWriter writer, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > 40 * 1024) throw new InvalidDataException("State string too long");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadLongString(this BinaryReader reader)
    {
        int len = reader.ReadUInt16();
        if (len > 40 * 1024) throw new InvalidDataException("State string too long");
        var bytes = reader.ReadBytes(len);
        if (bytes.Length != len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
}

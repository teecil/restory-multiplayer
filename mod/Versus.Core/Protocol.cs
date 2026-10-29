using System;
using System.IO;
using System.Text;

namespace RestoryVersus.Core
{
    // Wire format: [magic u16][version u8][kind u8][flags u8][seq u32][ack u32][payload...]
    // Reliable messages carry a non-zero seq and are resent until the peer echoes it in ack.
    public enum MsgKind : byte
    {
        Hello = 1,      // name, protocol, game build
        Ack = 2,        // ack only, no payload
        Progress = 3,   // unreliable racer state, sent often
        Finish = 4,     // reliable: final time
        Heartbeat = 5,  // unreliable keepalive
        Bye = 6,        // reliable: leaving
        Ready = 7,      // reliable: at the bench with a contest device, waiting for GO (or not any more)
        StartRace = 8,  // reliable, host -> client: race number and delay until the shared 3-2-1-GO intro
        LobbySelect = 9,  // reliable, host -> client: the device chosen in the lobby
        LobbyReady = 10,  // reliable, client -> host: ready (or not) for the chosen device
        LobbyLaunch = 11, // reliable, host -> client: start now with this device
        PlayerInfo = 12,  // reliable: name and avatar id (sent on change and when the peer says Hello)
        LobbyGoWorkshop = 13, // reliable, host -> client: both go to the versus workshop to pick a device
        Rematch = 14,         // reliable: this player wants (or no longer wants) a rematch on the same device

        // Co-op (CoopSession). A versus peer and a co-op peer recognise each other's Hello and report it.
        CoopHello = 20,     // name, game build
        CoopSaveBegin = 21, // reliable, host -> guest: file name, length, SHA-256 of the host's save
        CoopSaveChunk = 22, // reliable, host -> guest: offset, bytes
        CoopSaveDone = 23,  // reliable, guest -> host: the save arrived intact (or not)
        CoopLaunch = 24,    // reliable, host -> guest: load it now
        CoopWorld = 25,     // reliable: this side is in the shop; object count and a hash of their ids
        CoopHostState = 26, // unreliable, host -> guest, 4x a second: game clock, clock running, shutters open, money
        CoopObject = 27,    // reliable: a room object settled (guest -> host: a request; host -> guest: the outcome)
        CoopFragment = 28,  // reliable: a piece of a message too big for one packet (kind, total, offset, bytes)
        CoopDayEnd = 29,    // reliable, host -> guest: the host ended the day; the next day's save follows
        CoopAction = 30,    // reliable, either way: a named game action with its data (inventory, purchases)
        CoopHeld = 31,      // unreliable, either way, ~12x a second while dragging: the object in the player's hand
    }

    public sealed class RacerSnapshot
    {
        public RacePhase Phase;
        public int RaceNo;      // 0 = not started by a synced countdown
        public string DeviceId = "";
        public int PartsPlaced;
        public int PartsTotal;
        public int Resets;
        public long ElapsedMs;
        public float InGameSeconds;

        public RacerSnapshot Clone() => (RacerSnapshot)MemberwiseClone();

        public void Write(BinaryWriter w)
        {
            w.Write((byte)Phase);
            w.Write(RaceNo);
            w.Write(DeviceId ?? "");
            w.Write(PartsPlaced);
            w.Write(PartsTotal);
            w.Write(Resets);
            w.Write(ElapsedMs);
            w.Write(InGameSeconds);
        }

        public static RacerSnapshot Read(BinaryReader r) => new RacerSnapshot
        {
            Phase = (RacePhase)r.ReadByte(),
            RaceNo = r.ReadInt32(),
            DeviceId = r.ReadString(),
            PartsPlaced = r.ReadInt32(),
            PartsTotal = r.ReadInt32(),
            Resets = r.ReadInt32(),
            ElapsedMs = r.ReadInt64(),
            InGameSeconds = r.ReadSingle(),
        };
    }

    public sealed class Packet
    {
        public const ushort Magic = 0x5276; // "Rv"
        public const byte ProtocolVersion = 10;
        public const int MaxSize = 1200;

        public MsgKind Kind;
        public uint Seq;
        public uint Ack;
        public byte[] Payload = Array.Empty<byte>();

        public byte[] Encode()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(ProtocolVersion);
                w.Write((byte)Kind);
                w.Write((byte)0);
                w.Write(Seq);
                w.Write(Ack);
                w.Write(Payload);
                if (ms.Length > MaxSize) throw new InvalidOperationException("packet too large: " + ms.Length);
                return ms.ToArray();
            }
        }

        // Protocol version of a datagram that carries our magic, or -1. Lets a peer running another version
        // of the mod be reported instead of silently ignored.
        public static int PeekVersion(byte[] data, int length)
        {
            if (length < 3 || data[0] != (byte)(Magic & 0xFF) || data[1] != (byte)(Magic >> 8)) return -1;
            return data[2];
        }

        // Returns null for anything that is not ours or not our protocol version.
        public static Packet Decode(byte[] data, int length)
        {
            if (length < 13) return null;
            using (var ms = new MemoryStream(data, 0, length))
            using (var r = new BinaryReader(ms, Encoding.UTF8))
            {
                if (r.ReadUInt16() != Magic) return null;
                if (r.ReadByte() != ProtocolVersion) return null;
                var p = new Packet { Kind = (MsgKind)r.ReadByte() };
                r.ReadByte();
                p.Seq = r.ReadUInt32();
                p.Ack = r.ReadUInt32();
                p.Payload = r.ReadBytes(length - 13);
                return p;
            }
        }

        public static byte[] Build(Action<BinaryWriter> body)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                body(w);
                return ms.ToArray();
            }
        }

        public BinaryReader Reader() => new BinaryReader(new MemoryStream(Payload), Encoding.UTF8);
    }
}

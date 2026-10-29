using System.IO;

namespace RestoryVersus.Core
{
    // Where a room object came to rest: its id (the save's UniqueId), the game's object state, the transform it
    // sits under (a path both copies resolve the same way) and its pose there. Hidden: it went onto someone's own
    // bench (the room no longer shows it); Gone: it left the world (recycled, used up).
    // Devices (Kind 1) also carry the device's own save data (Data), so the other side can rebuild it as it is now.
    public sealed class ObjState
    {
        public const byte KindObject = 0;
        public const byte KindDevice = 1;
        public const int MaxData = 1024 * 1024;

        public string Id = "";
        public byte Kind;
        public byte[] Data = new byte[0];
        public byte State;
        public string Parent = "";
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1f;
        public bool Hidden;
        public bool Gone;
        public bool FromGuest;
        public int Seq;

        private const int MaxText = 512;

        public void Write(BinaryWriter w)
        {
            w.Write(Id ?? "");
            w.Write(State);
            w.Write(Parent ?? "");
            w.Write(X); w.Write(Y); w.Write(Z);
            w.Write(Qx); w.Write(Qy); w.Write(Qz); w.Write(Qw);
            w.Write((byte)((Hidden ? 1 : 0) | (Gone ? 2 : 0) | (FromGuest ? 4 : 0)));
            w.Write(Seq);
            w.Write(Kind);
            w.Write(Data?.Length ?? 0);
            if (Data != null) w.Write(Data);
        }

        public static ObjState Read(BinaryReader r)
        {
            var s = new ObjState
            {
                Id = r.ReadString(),
                State = r.ReadByte(),
                Parent = r.ReadString(),
                X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(),
                Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(),
            };
            byte flags = r.ReadByte();
            s.Hidden = (flags & 1) != 0;
            s.Gone = (flags & 2) != 0;
            s.FromGuest = (flags & 4) != 0;
            s.Seq = r.ReadInt32();
            s.Kind = r.ReadByte();
            int len = r.ReadInt32();
            if (len < 0 || len > MaxData) throw new System.ArgumentException("bad object data length");
            s.Data = r.ReadBytes(len);
            if (s.Data.Length != len) throw new EndOfStreamException();
            if (s.Id.Length == 0 || s.Id.Length > MaxText || s.Parent.Length > MaxText) throw new System.ArgumentException("bad object state");
            return s;
        }

        public ObjState Clone() => (ObjState)MemberwiseClone();

        public bool SameData(ObjState o) => o != null && System.Linq.Enumerable.SequenceEqual(o.Data ?? new byte[0], Data ?? new byte[0]);

        public bool SamePlace(ObjState o) => o != null && o.Id == Id && o.Kind == Kind && o.State == State && o.Parent == Parent && o.Hidden == Hidden && o.Gone == Gone
            && o.X == X && o.Y == Y && o.Z == Z && o.Qx == Qx && o.Qy == Qy && o.Qz == Qz && o.Qw == Qw;
    }
}

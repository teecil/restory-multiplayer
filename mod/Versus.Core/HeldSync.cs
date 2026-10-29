using System;
using System.Collections.Generic;
using System.IO;

namespace RestoryVersus.Core
{
    // An object in the other player's hand: where it is now (world pose), sent often and unreliably while
    // dragging. DragId numbers the sender's drags; a release closes one.
    public sealed class HeldPose
    {
        public string Id = "";
        public byte Kind;          // ObjState.KindObject / KindDevice
        public int DragId;
        public float X, Y, Z;
        public float Qx, Qy, Qz, Qw = 1f;
        public long ReceivedMs;

        public void Write(BinaryWriter w)
        {
            w.Write(Id ?? "");
            w.Write(Kind);
            w.Write(DragId);
            w.Write(X); w.Write(Y); w.Write(Z);
            w.Write(Qx); w.Write(Qy); w.Write(Qz); w.Write(Qw);
        }

        public static HeldPose Read(BinaryReader r)
        {
            var p = new HeldPose
            {
                Id = r.ReadString(),
                Kind = r.ReadByte(),
                DragId = r.ReadInt32(),
                X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(),
                Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(),
            };
            if (p.Id.Length == 0 || p.Id.Length > 512) throw new ArgumentException("bad held pose");
            return p;
        }
    }

    // What the other player holds. The release travels reliably and before the drop's outcome (ObjState), so
    // once it is in, any pose of that drag still arriving late (unreliable, unordered) is dropped and cannot
    // pull the settled object back towards the hand.
    public sealed class HeldTracker
    {
        public const long TimeoutMs = 1500; // no news for this long: the other player let go (or their game died)

        private readonly Dictionary<string, HeldPose> held = new Dictionary<string, HeldPose>();
        private int lastReleased = -1;

        public IReadOnlyDictionary<string, HeldPose> Held => held;

        public bool IsHeld(string id) => held.ContainsKey(id);

        // A pose from the other side. False if it belongs to a drag that already ended.
        public bool Accept(HeldPose p, long nowMs)
        {
            if (p == null || p.DragId <= lastReleased) return false;
            if (held.TryGetValue(p.Id, out var have) && have.DragId > p.DragId) return false;
            p.ReceivedMs = nowMs;
            held[p.Id] = p;
            return true;
        }

        public void Release(int dragId, string id)
        {
            if (dragId > lastReleased) lastReleased = dragId;
            held.Remove(id);
        }

        // Holds that went silent; the caller makes those objects free again.
        public List<string> Expire(long nowMs)
        {
            var gone = new List<string>();
            foreach (var kv in held)
                if (nowMs - kv.Value.ReceivedMs > TimeoutMs) gone.Add(kv.Key);
            foreach (var id in gone) held.Remove(id);
            return gone;
        }

        // The other player (re)joined or the game reloaded: nothing is held, numbering starts over.
        public void Reset()
        {
            held.Clear();
            lastReleased = -1;
        }
    }
}

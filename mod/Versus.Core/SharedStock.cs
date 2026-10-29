using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RestoryVersus.Core
{
    // One stock of parts for both players (owner's answer: a part the guest takes is gone from the host's stock).
    // The stock is a multiset: key (which container, which exact item) -> count.
    //   host  -> sends its whole stock once, then only what changed, each time with the number of the last guest
    //            change it applied (the whole stock on every change was tens of KB per part moved: the keys hold
    //            the item's full JSON). Both are compressed.
    //   guest -> keeps the host's stock from those messages; sends only its own changes (+/- per key), numbered;
    //            it takes the host's stock as its own only once that stock includes all of its changes, so a stock
    //            sent before a change arrived does not bring back a part the guest just took. A change from the host
    //            with no whole stock to apply it to (a guest that joined again) asks for the whole stock.
    // Rides on CoopSession named actions ("stock", "stockd" host -> guest; "stock+", "stock?" guest -> host).
    public sealed class SharedStock
    {
        private readonly CoopSession session;
        private Dictionary<string, int> baseline;      // guest: the stock as last agreed
        private Dictionary<string, int> lastSent;      // host: the stock as last sent
        private int guestSeq;                          // guest: last change sent
        private int appliedSeq;                        // host: last guest change applied
        private int sentAck = -1;                      // host: appliedSeq in the last snapshot
        private bool wantFull;                         // host: the guest asked for the whole stock
        private Dictionary<string, int> hostView;      // guest: the host's stock, from the whole stock and changes
        private bool askedFull;                        // guest: asked for the whole stock, not arrived yet

        // Host: apply this guest change to the real stock (key, +/- count).
        public event Action<Dictionary<string, int>> OnApplyChange;
        // Guest: make the local stock exactly this.
        public event Action<Dictionary<string, int>> OnReplace;

        public int ChangesSent { get; private set; }
        public int ChangesApplied { get; private set; }
        public int SnapshotsSent { get; private set; }
        public int SnapshotsTaken { get; private set; }
        public long BytesSent { get; private set; }
        public bool HasBaseline => baseline != null;
        // Host: a guest change was applied and the guest still waits for the stock that includes it.
        public bool WantsSend => session.IsHost && (sentAck != appliedSeq || wantFull);

        // The guest's shop was loaded again (a new day from the host's save): start counting from what it holds.
        public void Reloaded()
        {
            baseline = null;
            lastSent = null;
        }

        public SharedStock(CoopSession session)
        {
            this.session = session;
            session.OnAction += OnAction;
        }

        // Every few frames with the stock as the game shows it now.
        public void Tick(Dictionary<string, int> current)
        {
            if (current == null || !session.Playing) return;
            if (session.IsHost)
            {
                if (wantFull) { lastSent = null; wantFull = false; }
                if (lastSent != null && sentAck == appliedSeq && Same(lastSent, current)) return;
                bool full = lastSent == null;
                var body = Encode(appliedSeq, full ? current : Diff(lastSent, current));
                lastSent = Copy(current);
                sentAck = appliedSeq;
                SnapshotsSent++;
                BytesSent += body.Length;
                session.SendAction(full ? "stock" : "stockd", body);
                return;
            }
            // Both copies loaded the same save, so the guest's stock at the start is the host's: changes count from it.
            if (baseline == null) { baseline = Copy(current); return; }
            var diff = Diff(baseline, current);
            if (diff.Count == 0) return;
            baseline = Copy(current);
            ChangesSent++;
            session.SendAction("stock+", Encode(++guestSeq, diff));
        }

        private void OnAction(string name, byte[] data)
        {
            if ((name == "stock" || name == "stockd") && !session.IsHost)
            {
                var entries = Decode(data, out int ack);
                if (name == "stock") { hostView = entries; askedFull = false; }
                else if (hostView == null)
                {
                    if (!askedFull) { askedFull = true; session.SendAction("stock?", Array.Empty<byte>()); }
                    return;
                }
                else Apply(hostView, entries);
                if (ack < guestSeq) return; // our latest change is not in it yet; a later one will be
                baseline = Copy(hostView);
                SnapshotsTaken++;
                OnReplace?.Invoke(Copy(hostView));
            }
            else if (name == "stock?" && session.IsHost) wantFull = true;
            else if (name == "stock+" && session.IsHost)
            {
                var change = Decode(data, out int seq);
                if (seq <= appliedSeq) return;
                appliedSeq = seq;
                ChangesApplied++;
                OnApplyChange?.Invoke(change);
            }
        }

        public static Dictionary<string, int> Diff(Dictionary<string, int> from, Dictionary<string, int> to)
        {
            var d = new Dictionary<string, int>();
            foreach (var k in from.Keys.Union(to.Keys))
            {
                from.TryGetValue(k, out int a);
                to.TryGetValue(k, out int b);
                if (a != b) d[k] = b - a;
            }
            return d;
        }

        private static void Apply(Dictionary<string, int> stock, Dictionary<string, int> change)
        {
            foreach (var kv in change)
            {
                stock.TryGetValue(kv.Key, out int n);
                n += kv.Value;
                if (n > 0) stock[kv.Key] = n; else stock.Remove(kv.Key);
            }
        }

        private static bool Same(Dictionary<string, int> a, Dictionary<string, int> b) => Diff(a, b).Count == 0;
        private static Dictionary<string, int> Copy(Dictionary<string, int> d) => new Dictionary<string, int>(d);

        private static byte[] Encode(int number, Dictionary<string, int> entries)
        {
            var raw = Packet.Build(w =>
            {
                w.Write(number);
                w.Write(entries.Count);
                foreach (var kv in entries.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    w.Write(kv.Key);
                    w.Write(kv.Value);
                }
            });
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static Dictionary<string, int> Decode(byte[] data, out int number)
        {
            using (var r = new BinaryReader(new GZipStream(new MemoryStream(data), CompressionMode.Decompress)))
            {
                number = r.ReadInt32();
                int n = r.ReadInt32();
                if (n < 0 || n > 100000) throw new ArgumentException("bad stock size");
                var d = new Dictionary<string, int>();
                for (int i = 0; i < n; i++) d[r.ReadString()] = r.ReadInt32();
                return d;
            }
        }
    }
}

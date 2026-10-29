using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FullSerializer;
using HarmonyLib;
using Restory.Data.Licenses;
using Restory.Data.SaveLoad.FullSerializerWrappers;
using Restory.Gameplay.Delivery;
using Restory.Gameplay.Inventory;
using Restory.Gameplay.Licenses;
using Restory.StorageSystem;
using Restory.StorageSystem.StorageElements;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Co-op: one stock of parts and one wallet for both (owner's answers in local/coop-design.md).
    //   parts: every Inventory in the shop, as a multiset of exact items (the item's save JSON). SharedStock
    //          (Versus.Core, fuzzed) keeps the host's stock the truth: the guest sends only its own changes.
    //   buying: on the guest, a purchase in any PC shop is forwarded as it happens: the money it takes, each
    //          SendToDelivery call (same overload, same arguments through the game's own serializer) and each
    //          licence bought. The host pays from its wallet and queues the delivery; the courier comes to the
    //          host's shop (the guest has no visits); the parts end up in the shared stock once unpacked.
    internal static class CoopStock
    {
        private static CoopSession session;
        private static SharedStock stock;
        private static float nextTick;
        private static bool wasInShop;
        private static bool inPurchase;

        public static int ItemsApplied { get; private set; }
        public static int RemoveMisses { get; private set; }
        public static int PurchasesForwarded { get; private set; }
        public static int PurchasesApplied { get; private set; }
        public static int StockTotal { get; private set; } = -1;
        public static string StockHash { get; private set; } = "";
        public static SharedStock Stock => stock;

        public static void Attach(CoopSession s)
        {
            session = s;
            stock = null;
            wasInShop = false;
            if (s == null) return;
            stock = new SharedStock(s);
            stock.OnApplyChange += ApplyChange;
            stock.OnReplace += Replace;
            s.OnAction += OnAction;
        }

        private static fsSerializer Fs
        {
            get
            {
                var wrapper = CoopDevices.Serializer;
                return wrapper == null ? null : AccessTools.Field(typeof(FullSerializerWrapperBase), "FsSerializer").GetValue(wrapper) as fsSerializer;
            }
        }

        private static string ToJson(object value, Type type)
        {
            Fs.TrySerialize(type, value, out var data).AssertSuccessWithoutWarnings();
            return fsJsonPrinter.CompressedJson(data);
        }

        private static object FromJson(string json, Type type)
        {
            object result = null;
            Fs.TryDeserialize(fsJsonParser.Parse(json), type, ref result).AssertSuccessWithoutWarnings();
            return result;
        }

        // ----- parts -----

        // An item's text, cached per item object: the stock is read 4x a second and items in storage do not change.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StorageItemElement, string> itemJson =
            new System.Runtime.CompilerServices.ConditionalWeakTable<StorageItemElement, string>();

        private static string ItemJson(StorageItemElement item) => itemJson.GetValue(item, it => ToJson(it, typeof(StorageItemElement)));

        // The shop's inventories, looked up again every 5 s or when one is gone (a scene search 4x a second
        // would cost frame time).
        private static List<Inventory> inventories = new List<Inventory>();
        private static float nextInventoryLookup;

        private static readonly Dictionary<Inventory, string> inventoryPaths = new Dictionary<Inventory, string>();
        private static bool stockDirty = true;
        private static float nextForcedRead;

        private static List<Inventory> Inventories()
        {
            if (Time.unscaledTime >= nextInventoryLookup || inventories.Any(i => i == null))
            {
                nextInventoryLookup = Time.unscaledTime + 5f;
                var found = UnityEngine.Object.FindObjectsByType<Inventory>(FindObjectsSortMode.None)
                    .OrderBy(i => CoopRoom.PathOf(i.transform), StringComparer.Ordinal).ToList();
                foreach (var inv in found)
                {
                    if (inventoryPaths.ContainsKey(inv)) continue;
                    inventoryPaths[inv] = CoopRoom.PathOf(inv.transform);
                    inv.StorageElements.StorageChanged += _ => stockDirty = true;
                    stockDirty = true;
                }
                foreach (var gone in inventoryPaths.Keys.Where(k => k == null).ToList()) inventoryPaths.Remove(gone);
                inventories = found;
            }
            return inventories;
        }

        private static string PathOfInventory(Inventory inv) => inventoryPaths.TryGetValue(inv, out var p) ? p : CoopRoom.PathOf(inv.transform);

        private static Dictionary<string, int> Read()
        {
            var d = new Dictionary<string, int>();
            foreach (var inv in Inventories())
            {
                string prefix = PathOfInventory(inv) + "#";
                foreach (IReadOnlyStorageSlot slot in inv.StorageElements)
                {
                    if (!(slot.Item is StorageItemElement item)) continue;
                    string key = prefix + ItemJson(item);
                    d.TryGetValue(key, out int n);
                    d[key] = n + Math.Max(1, slot.Count);
                }
            }
            return d;
        }

        public static void Tick()
        {
            if (stock == null || session == null || !session.Playing) return;
            bool inShop = CoopGame.InShopFor(3f) && CoopDevices.Serializer != null;
            if (!inShop) { if (wasInShop) Plugin.Log.LogInfo("co-op stock: out of the shop"); wasInShop = false; return; }
            if (!wasInShop)
            {
                // Entered (or re-entered after a new day): both hold the same loaded stock; count changes from here.
                wasInShop = true;
                stock.Reloaded();
            }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.25f;
            Inventories(); // keeps the change subscriptions current
            if (!stockDirty && Time.unscaledTime < nextForcedRead && !stock.WantsSend) return;
            stockDirty = false;
            nextForcedRead = Time.unscaledTime + 2f;
            try
            {
                var current = Read();
                // Parts carry their dirt as a texture: send the ones the other side lacks before the stock.
                CoopTextures.SendFor(current.Keys.SelectMany(CoopTextures.Referenced));
                StockTotal = current.Values.Sum();
                StockHash = Hash(current);
                stock.Tick(current);
            }
            catch (Exception e) { Plugin.Log.LogError("co-op stock: " + e); nextTick = Time.unscaledTime + 5f; }
        }

        private static string Hash(Dictionary<string, int> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        private static Inventory InventoryAt(string key, out string itemJson)
        {
            int hash = key.IndexOf('#');
            itemJson = hash >= 0 ? key.Substring(hash + 1) : "";
            string path = hash >= 0 ? key.Substring(0, hash) : "";
            var t = CoopRoom.Resolve(path);
            return t != null ? t.GetComponent<Inventory>() : null;
        }

        // Host: the guest took parts out or put parts in.
        private static void ApplyChange(Dictionary<string, int> change)
        {
            foreach (var kv in change)
            {
                var inv = InventoryAt(kv.Key, out var itemJson);
                if (inv == null) { RemoveMisses++; Plugin.Log.LogWarning("co-op stock: no inventory for " + kv.Key.Split('#')[0]); continue; }
                if (kv.Value > 0)
                {
                    for (int i = 0; i < kv.Value; i++)
                        inv.StorageElements.AddItem((StorageItemElement)FromJson(itemJson, typeof(StorageItemElement)));
                }
                else
                {
                    for (int i = 0; i < -kv.Value; i++)
                    {
                        int index = -1;
                        for (int s = 0; s < inv.StorageElements.Size; s++)
                        {
                            if (inv.StorageElements[s].Item is StorageItemElement it && ItemJson(it) == itemJson) { index = s; break; }
                        }
                        // Both took the same last part at the same moment: it stays taken on both (logged).
                        if (index < 0) { RemoveMisses++; Plugin.Log.LogWarning("co-op stock: the guest took a part the host no longer has"); break; }
                        inv.StorageElements.ClearItem(index);
                    }
                }
                ItemsApplied += Math.Abs(kv.Value);
            }
            Plugin.Log.LogInfo("co-op stock: applied the guest's change (" + change.Count + " kinds of part)");
        }

        // Guest: the host's stock, which already includes all of the guest's changes.
        private static void Replace(Dictionary<string, int> stockNow)
        {
            var byInventory = new Dictionary<Inventory, List<StorageItemElement>>();
            foreach (var inv in Inventories()) byInventory[inv] = new List<StorageItemElement>();
            foreach (var kv in stockNow.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var inv = InventoryAt(kv.Key, out var itemJson);
                if (inv == null || !byInventory.ContainsKey(inv)) continue;
                for (int i = 0; i < kv.Value; i++) byInventory[inv].Add((StorageItemElement)FromJson(itemJson, typeof(StorageItemElement)));
            }
            foreach (var kv in byInventory)
            {
                var have = kv.Key.StorageElements;
                // Unchanged inventories are left alone (no UI churn).
                var now = new List<string>();
                foreach (IReadOnlyStorageSlot slot in have) if (slot.Item is StorageItemElement it) now.Add(ToJson(it, typeof(StorageItemElement)));
                var want = kv.Value.Select(it => ToJson(it, typeof(StorageItemElement))).ToList();
                if (now.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(want.OrderBy(x => x, StringComparer.Ordinal))) continue;
                have.Clear();
                foreach (var it in kv.Value) have.AddItem(it);
            }
        }

        // ----- buying -----

        private static void OnAction(string name, byte[] data)
        {
            if (!session.IsHost) return;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                {
                    if (name == "spend")
                    {
                        int amount = r.ReadInt32();
                        var wallet = SceneCache.Find<Wallet>(false);
                        bool ok = wallet != null && wallet.TryToRemove(amount);
                        PurchasesApplied++;
                        Plugin.Log.LogInfo("co-op buy: the guest spent " + amount + " of the shared money: " + ok);
                    }
                    else if (name == "deliver")
                    {
                        string signature = r.ReadString();
                        int n = r.ReadInt32();
                        var method = DeliveryOverloads().FirstOrDefault(m => Signature(m) == signature);
                        if (method == null) { Plugin.Log.LogError("co-op buy: unknown delivery " + signature); return; }
                        var ps = method.GetParameters();
                        if (ps.Length != n) throw new ArgumentException("delivery arguments");
                        var args = new object[n];
                        for (int i = 0; i < n; i++) args[i] = FromJson(r.ReadString(), ps[i].ParameterType);
                        var delivery = SceneCache.Find<DeliveryService>(true);
                        applyingPurchase = true;
                        try { method.Invoke(delivery, args); }
                        finally { applyingPurchase = false; }
                        PurchasesApplied++;
                        Plugin.Log.LogInfo("co-op buy: the guest's purchase queued for delivery: " + signature);
                    }
                    else if (name == "license")
                    {
                        var license = FromJson(r.ReadString(), typeof(LicenseInfo)) as LicenseInfo;
                        var licenses = SceneCache.Find<LicensesService>(true);
                        if (license != null && licenses != null && !licenses.Contains(license)) licenses.Add(license);
                        PurchasesApplied++;
                        Plugin.Log.LogInfo("co-op buy: the guest bought a licence: " + (license != null ? license.name : "?"));
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op buy: " + name + " failed: " + e); }
        }

        private static bool applyingPurchase;

        private static IEnumerable<MethodInfo> DeliveryOverloads() =>
            typeof(DeliveryService).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == nameof(DeliveryService.SendToDelivery));

        private static string Signature(MethodInfo m) => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")";

        private static bool Forwarding => inPurchase && CoopShop.GuestPlaying && session != null && session.Playing && Fs != null;

        // The purchase buttons of the three PC shops.
        [HarmonyPatch]
        private static class PurchaseScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Restory.Gameplay.Shops.HomeDepot.HomeDepotShopInteractor), "TryToBuyAllItemsFromShoppingCart");
                yield return AccessTools.Method(typeof(Restory.Gameplay.Shops.Elements.ElementsShopInteractor), "TryToBuyAllItemsFromShoppingCart");
                yield return AccessTools.Method(typeof(Restory.Gameplay.Shops.Devices.DeviceShopInteractor), "TryToBuyAllLotsFromShoppingCart");
            }

            private static void Prefix() => inPurchase = true;
            private static void Finalizer() => inPurchase = false;
        }

        [HarmonyPatch(typeof(Wallet), nameof(Wallet.TryToRemove))]
        private static class ForwardSpending
        {
            private static void Postfix(int moneyAmountToRemove, bool __result)
            {
                if (!Forwarding || !__result) return;
                PurchasesForwarded++;
                session.SendAction("spend", Packet.Build(w => w.Write(moneyAmountToRemove)));
                Plugin.Log.LogInfo("co-op buy: " + moneyAmountToRemove + " spent from the shared money, sent to the host");
            }
        }

        [HarmonyPatch]
        private static class ForwardDeliveries
        {
            private static IEnumerable<MethodBase> TargetMethods() => DeliveryOverloads().Cast<MethodBase>();

            private static void Prefix(MethodBase __originalMethod, object[] __args)
            {
                if (!Forwarding || applyingPurchase) return;
                var m = (MethodInfo)__originalMethod;
                var ps = m.GetParameters();
                var jsons = new string[ps.Length];
                for (int i = 0; i < ps.Length; i++) jsons[i] = ToJson(__args[i], ps[i].ParameterType);
                PurchasesForwarded++;
                session.SendAction("deliver", Packet.Build(w =>
                {
                    w.Write(Signature(m));
                    w.Write(jsons.Length);
                    foreach (var j in jsons) w.Write(j);
                }));
                Plugin.Log.LogInfo("co-op buy: " + Signature(m) + " sent to the host");
            }
        }

        [HarmonyPatch(typeof(LicensesService), nameof(LicensesService.Add))]
        private static class ForwardLicences
        {
            private static void Prefix(LicenseInfo license)
            {
                if (!Forwarding || license == null) return;
                PurchasesForwarded++;
                var json = ToJson(license, typeof(LicenseInfo));
                session.SendAction("license", Packet.Build(w => w.Write(json)));
            }
        }

        // ----- test bots -----

        // Host: a part into the host's stock (what unpacking a delivery box does).
        public static string BotAddPart()
        {
            var inv = Inventories().FirstOrDefault(i => i.StorageElements.Size > 0);
            if (inv == null) return "no part in any inventory to copy";
            var sample = inv.StorageElements[0].Item as StorageItemElement;
            var copy = (StorageItemElement)FromJson(ToJson(sample, typeof(StorageItemElement)), typeof(StorageItemElement));
            inv.StorageElements.AddItem(copy);
            return copy.Info.NameLocalizationKey + " into " + CoopRoom.PathOf(inv.transform);
        }

        // Guest: take one part out of the stock (what taking it to the bench does).
        public static string BotTakePart()
        {
            var inv = Inventories().FirstOrDefault(i => i.StorageElements.Size > 0);
            if (inv == null) return "the stock is empty";
            var name = (inv.StorageElements[0].Item as StorageItemElement)?.Info?.NameLocalizationKey ?? "?";
            inv.StorageElements.ClearItem(0);
            return name + " taken";
        }

        // Guest: buy the way a PC shop does: money from the wallet, then the delivery.
        public static string BotBuy()
        {
            var delivery = SceneCache.Find<DeliveryService>(true);
            var wallet = SceneCache.Find<Wallet>(false);
            var sample = Inventories().Where(i => i.StorageElements.Size > 0).Select(i => i.StorageElements[0].Item as StorageItemElement).FirstOrDefault(x => x != null);
            if (delivery == null || wallet == null || sample == null) return "no shop services or no part to model the purchase on";
            var data = ((StorageItemElement)FromJson(ToJson(sample, typeof(StorageItemElement)), typeof(StorageItemElement))).ElementData;
            inPurchase = true;
            try
            {
                if (!wallet.TryToRemove(100)) return "not enough money";
                delivery.SendToDelivery(data, 2);
            }
            finally { inPurchase = false; }
            return "2x " + sample.Info.NameLocalizationKey + " for 100";
        }
    }
}

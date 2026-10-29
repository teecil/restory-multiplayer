using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.MoneyCash;
using RestoryVersus.Core;

namespace RestoryVersus
{
    // Cash a customer pays with is a room object, but its sum is not in the object's data: CashMoneyService keeps it
    // (MoneyAmountHeld, saved apart). The owner's game: the guest's copy showed 0, and putting it into the register
    // only told the host "the money is gone", so the payment vanished. Now:
    //   the sum travels with the object ("cash": id, sum, sent just before the object itself) and is part of its
    //   room-list signature, so more money added to the same pile reaches the other side too;
    //   the guest putting cash into the register asks the host to do it ("cashin": id): the host's own pile goes into
    //   the one wallet; the guest's copy just goes away.
    internal static class CoopCash
    {
        private static readonly AccessTools.FieldRef<CashMoneyService, CashMoneyObjectRegistry> RegistryField =
            AccessTools.FieldRefAccess<CashMoneyService, CashMoneyObjectRegistry>("registry");
        private static readonly AccessTools.FieldRef<CashMoneyService, CashMoneyObjectFactory> FactoryField =
            AccessTools.FieldRefAccess<CashMoneyService, CashMoneyObjectFactory>("factory");
        private static readonly AccessTools.FieldRef<CashMoneyService, CashMoneyObject> OnDeskField =
            AccessTools.FieldRefAccess<CashMoneyService, CashMoneyObject>("cashMoneyObjectCurrentlyInReceivingFromNpcsSpace");
        private static readonly System.Reflection.MethodInfo LeaveDesk = AccessTools.Method(typeof(CashMoneyService), "RemoveMoneyFromNpcReceivingSpace");

        private static CoopSession session;
        private static readonly Dictionary<string, int> sums = new Dictionary<string, int>();
        private static bool forwarding;

        public static int SumsApplied { get; private set; }
        public static int DepositsForwarded { get; private set; }
        public static int DepositsMade { get; private set; }

        private static CashMoneyService Service => SceneCache.Find<CashMoneyService>(true);

        public static void Attach(CoopSession s)
        {
            session = s;
            sums.Clear();
            if (s != null) s.OnAction += OnAction;
        }

        // CoopRoom, before it reports an object: cash sends its sum first (same ordered channel).
        public static void BeforeReport(InteractiveObject obj)
        {
            if (session == null || obj == null || !obj.TryGetComponent<CashMoneyObject>(out var cash)) return;
            string id = obj.UniqueId;
            int sum = cash.MoneyAmountHeld;
            session.SendAction("cash", Packet.Build(w => { w.Write(id); w.Write(sum); }));
        }

        // CoopRoom, after it made or moved an object from the other side: the sum, and the cash registry.
        public static void AfterApply(InteractiveObject obj)
        {
            if (obj == null || !obj.TryGetComponent<CashMoneyObject>(out var cash)) return;
            if (sums.TryGetValue(obj.UniqueId, out int sum) && cash.MoneyAmountHeld != sum) { cash.SetUp(sum); SumsApplied++; }
            var service = Service;
            if (service != null) RegistryField(service)?.Register(cash);
        }

        public static string SigPart(InteractiveObject o) =>
            o != null && o.TryGetComponent<CashMoneyObject>(out var cash) ? "|$" + cash.MoneyAmountHeld : "";

        private static void OnAction(string name, byte[] data)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
                {
                    if (name == "cash")
                    {
                        string id = r.ReadString();
                        int sum = r.ReadInt32();
                        sums[id] = sum;
                        var o = CoopRoom.Find(id);
                        if (o != null) AfterApply(o);
                    }
                    else if (name == "cashin" && session.IsHost)
                    {
                        string id = r.ReadString();
                        var o = CoopRoom.Find(id);
                        var service = Service;
                        if (o == null || service == null || !o.TryGetComponent<CashMoneyObject>(out var cash))
                        {
                            Plugin.Log.LogWarning("co-op cash: the guest put cash " + id + " into the register, but it is not here");
                            return;
                        }
                        int sum = cash.MoneyAmountHeld;
                        forwarding = true;
                        try { service.TransferMoneyToWallet(cash); }
                        finally { forwarding = false; }
                        DepositsMade++;
                        Plugin.Log.LogInfo("co-op cash: the guest put " + sum + " into the register, in the wallet now");
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op cash: " + name + ": " + e); }
        }

        // Test bots. Host: a customer pays 123 in cash onto the desk. Guest: that pile into the register.
        public static string BotCustomerPays()
        {
            var service = Service;
            if (service == null) return "no cash service";
            return service.TryAddMoneyFromNpcToWindowSpace(123) ? "123 on the desk" : "refused";
        }

        public static string BotDeposit()
        {
            var service = Service;
            var registry = service != null ? RegistryField(service) : null;
            CashMoneyObject pile = null;
            if (registry != null) foreach (var c in registry.All) if (c != null && c.MoneyAmountHeld >= 123 && (pile == null || c.MoneyAmountHeld > pile.MoneyAmountHeld)) pile = c;
            if (pile == null) return "no pile of 123 or more here";
            int sum = pile.MoneyAmountHeld;
            service.TransferMoneyToWallet(pile);
            return "the pile of " + sum + " into the register";
        }

        // The guest's register: the host deposits its own pile; here the copy only goes away.
        [HarmonyPatch(typeof(CashMoneyService), nameof(CashMoneyService.TransferMoneyToWallet))]
        private static class DepositPatch
        {
            private static bool Prefix(CashMoneyService __instance, CashMoneyObject moneyItemToTransfer)
            {
                if (forwarding || session == null || !session.Playing || session.IsHost || moneyItemToTransfer == null) return true;
                string id = moneyItemToTransfer.InteractiveObject.UniqueId;
                session.SendAction("cashin", Packet.Build(w => w.Write(id)));
                DepositsForwarded++;
                Plugin.Log.LogInfo("co-op cash: " + moneyItemToTransfer.MoneyAmountHeld + " into the register, the host puts it in the wallet");
                if (OnDeskField(__instance) == moneyItemToTransfer) LeaveDesk?.Invoke(__instance, null);
                RegistryField(__instance)?.Unregister(moneyItemToTransfer);
                CoopGame.Registry?.Unregister(moneyItemToTransfer.InteractiveObject);
                FactoryField(__instance)?.Destroy(moneyItemToTransfer);
                return false;
            }
        }
    }
}

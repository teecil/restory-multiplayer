using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HarmonyLib;
using Restory.Gameplay.EmailSystems;
using Restory.Gameplay.WorkOrders.EmailOrders;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // E-mail for both. The guest's game makes no mail of its own (CoopShop skips it); the host's letters and taken
    // orders (EmailService + EmailOrdersService) go to the guest when they change, so new letters show up during the
    // day and an order taken by either player moves to "taken" on both.
    // Taking an order on the guest (only when the host allows it, CoopSettings) is a request: the host takes that
    // order itself if nobody has yet, and answers; two players can never take the same order. Its device comes with
    // the host's courier like any order.
    internal static class CoopEmail
    {
        private static readonly AccessTools.FieldRef<EmailService, List<EmailLetterRecordInFolder>> Received =
            AccessTools.FieldRefAccess<EmailService, List<EmailLetterRecordInFolder>>("receivedEmails");
        private static readonly System.Reflection.MethodInfo AssignDevice = AccessTools.Method(typeof(EmailOrdersService), "TryToAssignDeviceContainerToRelevantTrackedOrder");

        private static CoopSession session;
        private static float nextTick;
        private static string lastSent;
        private static string waiting;

        public static int StatesSent { get; private set; }
        public static int StatesApplied { get; private set; }
        public static int OrdersAsked { get; private set; }
        public static int OrdersTakenForGuest { get; private set; }

        private static EmailService Mail => SceneCache.Find<EmailService>(true);
        private static EmailOrdersService Orders => SceneCache.Find<EmailOrdersService>(true);

        public static void Attach(CoopSession s)
        {
            session = s;
            lastSent = null;
            waiting = null;
            if (s != null) s.OnAction += OnAction;
        }

        private static bool Ready => session != null && session.Playing && CoopGame.InShopFor(3f) && CoopDevices.Serializer != null && Mail != null && Orders != null;

        // Both services in one piece, so letters and the orders taken from them stay the same objects.
        private static string StateJson() => CoopDevices.Serializer.ToJson(new object[] { Mail.CaptureState(), Orders.CaptureState() });

        // What the mail means for both players: which letters are in which folder, how many are still to come, which
        // orders are taken and ready. Not who read what (each player's own, and it changed the state every few
        // seconds while the host read mail: the guest's list was rebuilt over and over in the owner's test).
        private static string Meaning(object mailState, object ordersState)
        {
            var sb = new StringBuilder();
            if (mailState is Restory.Data.SaveLoad.Containers.EmailServiceSaveData m)
            {
                sb.Append(m.UpcomingEmails?.Length ?? 0).Append('\n');
                if (m.ReceivedEmails != null)
                    foreach (var r in m.ReceivedEmails)
                    {
                        if (r == null) continue;
                        sb.Append((int)r.Folder).Append('|');
                        if (r.Email is EmailLetterRecord l) sb.Append(l.SubjectLocalizationKey).Append('|').Append(l.ReceivedDateTime.Ticks);
                        else sb.Append(r.Email?.GetType().Name);
                        sb.Append('\n');
                    }
            }
            if (ordersState is Restory.Data.SaveLoad.Containers.EmailOrdersServiceSaveData o && o.TrackedOrders != null)
                foreach (var t in o.TrackedOrders)
                    if (t != null) sb.Append(t.ID).Append('|').Append(t.Order != null ? Key(t.Order) : "").Append('|').Append(t.IsReadyToShipAndMarkedForCourierPickUp).Append('\n');
            return sb.ToString();
        }

        private static string LocalMeaning() => Meaning(Mail.CaptureState(), Orders.CaptureState());

        public static void Tick()
        {
            if (!Ready || Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            try
            {
                if (session.IsHost)
                {
                    var meaning = LocalMeaning();
                    if (meaning == lastSent) return;
                    lastSent = meaning;
                    StatesSent++;
                    session.SendAction("email", Pack(StateJson()));
                    return;
                }
                if (waiting == null) return;
                var state = waiting;
                waiting = null;
                var parts = CoopDevices.Serializer.FromJsonUnsafe<object[]>(state);
                if (parts == null || parts.Length < 2) return;
                if (Meaning(parts[0], parts[1]) == LocalMeaning()) return; // nothing new for this player
                Mail.RestoreState(parts[0]);
                Orders.RestoreState(parts[1]);
                foreach (var d in CoopDevices.All) if (d != null) AssignDevice?.Invoke(Orders, new object[] { d });
                RaiseChanged(Mail);
                StatesApplied++;
                Plugin.Log.LogInfo("co-op email: the host's mail applied");
            }
            catch (Exception e) { Plugin.Log.LogError("co-op email: " + e); nextTick = Time.unscaledTime + 5f; }
        }

        private static void RaiseChanged(EmailService mail)
        {
            var handler = AccessTools.Field(typeof(EmailService), nameof(EmailService.OnContentsChanged))?.GetValue(mail) as Action;
            handler?.Invoke();
        }

        // An order told apart by what it is, not by object (the two games hold different objects for it).
        private static string Key(EmailLetterOrderRecord o) =>
            o.ReceivedDateTime.Ticks + "|" + o.SubjectLocalizationKey + "|" + o.Payment + "|" + (o.DeviceCondition?.ID ?? "");

        private static void OnAction(string name, byte[] data)
        {
            try
            {
                if (name == "email" && !session.IsHost) { waiting = Unpack(data); return; }
                if (name == "takeorder" && session.IsHost) { TakeForGuest(Encoding.UTF8.GetString(data)); return; }
                if (name == "takeorder:ok" && !session.IsHost) { CoopShop.Say("coopOrderTaken"); return; }
                if (name == "takeorder:no" && !session.IsHost) { CoopShop.Say("coopOrderAlreadyTaken"); return; }
            }
            catch (Exception e) { Plugin.Log.LogError("co-op email: " + name + ": " + e); }
        }

        // Host: the guest wants this order. Taken only if it is still in the inbox and nobody took it.
        private static void TakeForGuest(string key)
        {
            var mail = Mail;
            var orders = Orders;
            EmailLetterOrderRecord found = null;
            if (mail != null && orders != null && CoopSettings.GuestTakesOrders)
                foreach (var r in Received(mail))
                    if (r.Email is EmailLetterOrderRecord o && Key(o) == key && r.Folder != EmailFolders.OrdersTaken
                        && !orders.TrackedOrders.Any(t => t.Order == o)) { found = o; break; }
            if (found == null)
            {
                session.SendAction("takeorder:no", Array.Empty<byte>());
                Plugin.Log.LogInfo("co-op email: the guest asked for an order that is taken already (or not allowed)");
                return;
            }
            mail.AcceptOrder(found);
            OrdersTakenForGuest++;
            session.SendAction("takeorder:ok", Array.Empty<byte>());
            Plugin.Log.LogInfo("co-op email: the guest took an order; its device comes with the courier");
        }

        // Test bot (guest): accept the first order in the inbox, as the player would.
        public static string BotTakeOrder()
        {
            var mail = Mail;
            if (mail == null) return "no mail service";
            foreach (var r in Received(mail))
                if (r.Email is EmailLetterOrderRecord o && r.Folder == EmailFolders.OrdersInbox)
                {
                    mail.AcceptOrder(o);
                    return "asked for " + o.SubjectLocalizationKey;
                }
            return "no order in the inbox";
        }

        // The guest presses "accept": the host decides.
        [HarmonyPatch(typeof(EmailService), nameof(EmailService.AcceptOrder))]
        private static class GuestAcceptPatch
        {
            private static bool Prefix(EmailLetterOrderRecord orderToSend)
            {
                if (session == null || !session.Playing || session.IsHost || orderToSend == null) return true;
                if (!CoopSettings.GuestTakesOrders)
                {
                    CoopShop.Say("coopOrdersHost");
                    Plugin.Log.LogInfo("co-op guest: an e-mail order was not accepted; the host has not allowed the guest to take orders");
                    return false;
                }
                OrdersAsked++;
                session.SendAction("takeorder", Encoding.UTF8.GetBytes(Key(orderToSend)));
                Plugin.Log.LogInfo("co-op guest: asked the host to take an e-mail order");
                return false;
            }
        }

        private static byte[] Pack(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                {
                    var b = Encoding.UTF8.GetBytes(text);
                    gz.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }

        private static string Unpack(byte[] packed)
        {
            using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
                return reader.ReadToEnd();
        }
    }
}

using System;
using System.Reflection;
using HarmonyLib;
using Restory.Gameplay.Common;
using Restory.Gameplay.DeliveryRandomParts;
using Restory.Gameplay.EmailSystems;
using Restory.Gameplay.Equipment;
using Restory.Gameplay.NPCs;
using Restory.Gameplay.Shops.Devices;
using Restory.Gameplay.TimeSystems;
using Restory.Gameplay.Visits;
using Restory.TimeSystems;
using RestoryVersus.Core;
using UnityEngine;

namespace RestoryVersus
{
    // Co-op step 2: the host runs the day, the guest's shop follows it (owner's answers in local/coop-design.md:
    // the guest sees no customers, time and the end of the day belong to the host).
    //   host  -> every 250 ms: game clock, whether it runs, whether the shutters are open
    //   guest -> own clock stopped, set to the host's; shutters animate to the host's state, the lever is locked;
    //            everything the clock would set off is switched off: the day-phase dispatcher (visits, e-mails,
    //            shop restock, payments, end of day), the visit queue, e-mails, random part deliveries, device
    //            shop restock, NPC visits; the bicycle (end the day) only tells the guest that the host does it.
    // Clock-driven visuals (wall clock, light, weather) keep working from the host's time.
    internal static class CoopShop
    {
        private sealed class Blocker : IActiveStateSwitchRequester { }
        private static readonly Blocker blocker = new Blocker();
        private static readonly MethodInfo ShuttersAnimate = AccessTools.Method(typeof(WindowShuttersStoreInteractiveItem), "StartAnimation");

        private static TimeSystem timeSystem;
        private static TimeSystem blockedTime;
        private static WindowShuttersStoreInteractiveItem lockedShutters;
        private static float nextShuttersMove;

        // The guest is playing the host's save: the rules below apply.
        public static bool GuestPlaying { get; private set; }
        // How many clock-driven game events the guest skipped (evidence for tests and the log).
        public static int Skipped { get; private set; }
        public static string Notice { get; private set; } = "";
        public static float NoticeUntil { get; private set; }

        public static bool ClockRunning => timeSystem != null && timeSystem.IsActive;

        public static void Tick(CoopSession s)
        {
            bool inShop = s != null && s.Playing && Lobby.Global != null && Lobby.Global.IsInGameLoop;
            GuestPlaying = s != null && !s.IsHost && s.Playing && CoopGame.GuestSlotActive;
            if (!inShop) return;
            var calendar = SceneCache.Find<GameCalendar>(false);
            var shutters = SceneCache.Find<WindowShuttersStoreInteractiveItem>(false);
            if (s.IsHost)
            {
                var hostWallet = SceneCache.Find<Restory.Gameplay.Inventory.Wallet>(false);
                if (calendar != null)
                    s.SetHostState(calendar.CurrentDateTime.Ticks, ClockRunning, shutters != null && shutters.IsWindowOpen, hostWallet != null ? hostWallet.MoneyAvailable : -1);
                return;
            }

            // Guest: the clock is the host's.
            if (timeSystem != null && !ReferenceEquals(blockedTime, timeSystem))
            {
                timeSystem.BlockTimeSystem(blocker);
                blockedTime = timeSystem;
                Plugin.Log.LogInfo("co-op guest: own game clock stopped, following the host's");
            }
            if (calendar != null && s.HostClockTicks >= 0 && calendar.CurrentDateTime.Ticks != s.HostClockTicks)
                calendar.CurrentDateTime = new DateTime(s.HostClockTicks);

            // One wallet: the guest's shows the host's money.
            var wallet = SceneCache.Find<Restory.Gameplay.Inventory.Wallet>(false);
            if (wallet != null && s.HostMoney >= 0 && wallet.MoneyAvailable != s.HostMoney) wallet.Init(s.HostMoney);

            if (shutters != null)
            {
                if (!ReferenceEquals(lockedShutters, shutters))
                {
                    shutters.BlockWindow(blocker);
                    lockedShutters = shutters;
                    Plugin.Log.LogInfo("co-op guest: shutters follow the host (lever locked)");
                }
                if (s.HostStatesReceived > 0 && shutters.IsWindowOpen != s.HostShuttersOpen && Time.unscaledTime >= nextShuttersMove)
                {
                    // The game's own animation (it sets IsWindowOpen when it completes); retried if still running.
                    nextShuttersMove = Time.unscaledTime + 4f;
                    ShuttersAnimate?.Invoke(shutters, null);
                    Plugin.Log.LogInfo("co-op guest: shutters " + (s.HostShuttersOpen ? "opening" : "closing") + " like the host's");
                }
            }
        }

        public static void Say(string key)
        {
            Notice = key;
            NoticeUntil = Time.unscaledTime + 5f;
        }

        // Test bot on the host: open the shop so the clock runs.
        public static bool OpenShuttersForTest()
        {
            var shutters = SceneCache.Find<WindowShuttersStoreInteractiveItem>(false);
            if (shutters == null || shutters.IsWindowOpen || ShuttersAnimate == null) return false;
            ShuttersAnimate.Invoke(shutters, null);
            return true;
        }

        private static bool Skip(string what)
        {
            if (!GuestPlaying) return true;
            Skipped++;
            if (Skipped <= 20) Plugin.Log.LogInfo("co-op guest: skipped " + what + " (the host runs it)");
            return false;
        }

        [HarmonyPatch]
        private static class TimeSystemCapture
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(TimeSystem), nameof(TimeSystem.Initialize));
            private static void Postfix(TimeSystem __instance) => timeSystem = __instance;
        }

        [HarmonyPatch(typeof(MainDayTimesSwitchingProcessingService), "ResolveTimeChanged")]
        private static class NoDayPhases
        {
            private static bool Prefix() => Skip("a day phase change");
        }

        [HarmonyPatch(typeof(CurrentDayVisitsQueueService), nameof(CurrentDayVisitsQueueService.ProcessTimeChanged))]
        private static class NoVisitQueue
        {
            private static bool Prefix() => Skip("the visit queue");
        }

        [HarmonyPatch(typeof(EmailService), nameof(EmailService.ProcessTimeChanged))]
        private static class NoEmails
        {
            private static bool Prefix() => Skip("e-mails");
        }

        [HarmonyPatch(typeof(NarrativeEmailsResendingService), nameof(NarrativeEmailsResendingService.ProcessTimeChanged))]
        private static class NoEmailResends
        {
            private static bool Prefix() => Skip("e-mail resends");
        }

        [HarmonyPatch]
        private static class NoRandomDeliveries
        {
            // An explicit interface implementation: found through the interface map.
            private static MethodBase TargetMethod()
            {
                var map = typeof(DeliveryRandomPartsService).GetInterfaceMap(typeof(ITimeChangeReceiver));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i].Name == nameof(ITimeChangeReceiver.ProcessTimeChanged)) return map.TargetMethods[i];
                return null;
            }

            private static bool Prefix() => Skip("random part deliveries");
        }

        [HarmonyPatch(typeof(DeviceShopSupplier), nameof(DeviceShopSupplier.ProcessTimeChanged))]
        private static class NoShopRestock
        {
            private static bool Prefix() => Skip("device shop restock");
        }

        [HarmonyPatch(typeof(NpcServiceMain), nameof(NpcServiceMain.TryToStartNpcVisitWithInteraction))]
        private static class NoNpcVisits
        {
            private static bool Prefix(ref bool __result)
            {
                if (Skip("an NPC visit")) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(NpcServiceMain), nameof(NpcServiceMain.TryToStartNpcVisitWithoutInteractionSegment))]
        private static class NoNpcPassBy
        {
            private static bool Prefix(ref bool __result)
            {
                if (Skip("an NPC visit without dialogue")) return true;
                __result = false;
                return false;
            }
        }

        // Only the host takes orders: e-mail orders on the guest's PC cannot be accepted.
        [HarmonyPatch(typeof(Restory.Gameplay.WorkOrders.EmailOrders.EmailOrdersService), nameof(Restory.Gameplay.WorkOrders.EmailOrders.EmailOrdersService.SendToDelivery))]
        private static class HostTakesEmailOrders
        {
            private static bool Prefix()
            {
                if (!GuestPlaying) return true;
                Say("coopOrdersHost");
                Plugin.Log.LogInfo("co-op guest: an e-mail order was not accepted; only the host takes orders");
                return false;
            }
        }

        // The host's day ends (store closed, skip week, epilogue): the guest is told; the next day's save follows.
        [HarmonyPatch(typeof(TransitionToEndOfDayService), nameof(TransitionToEndOfDayService.PerformDaySwitchingOperation))]
        private static class HostDayEnds
        {
            private static void Prefix() => Plugin.Instance?.Coop?.HostDayEnding();
        }

        [HarmonyPatch(typeof(TransitionToEndOfDayService), nameof(TransitionToEndOfDayService.PerformSkipToEpilogueOperation))]
        private static class HostSkipsToEpilogue
        {
            private static void Prefix() => Plugin.Instance?.Coop?.HostDayEnding();
        }

        // The bicycle means "ready to end the day"; the day ends when both clicked it (CoopDayReady), so the host
        // cannot end it in the middle of the guest's repair.
        [HarmonyPatch(typeof(DayEnderFromPlayerInteraction), "ResolveBicycleClicked")]
        private static class BothEndTheDay
        {
            private static bool Prefix(DayEnderFromPlayerInteraction __instance) => CoopDayReady.BicycleClicked(__instance);
        }
    }
}

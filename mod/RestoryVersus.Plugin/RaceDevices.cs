using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Restory.Data.Devices;
using Restory.Gameplay.Competitions;
using Restory.Gameplay.Devices;
using Restory.Gameplay.InteractiveObjects;
using Restory.Gameplay.WorkOrders.EmailOrders;
using UnityEngine;

namespace RestoryVersus
{
    // Puts a contest device straight onto the bench, through the same calls the game uses when the
    // player unpacks a purchased contest box (InteractiveObjectBoxContainer.GetInteractiveObject ->
    // DeviceService.CreateInitialCompetitionPackedDeviceContainer -> DeviceService.PlaceDeviceContainer),
    // minus the purchase and the courier. Parts are perfect and laid out by the game's deterministic
    // CompetitionElementsPositioner, so both racers get the same starting position.
    internal static class RaceDevices
    {
        private static readonly MethodInfo GenerateCondition =
            AccessTools.Method(typeof(CompetitionsApp), "GenerateCompetitionDeviceCondition");

        private static CompetitionsApp App => SceneCache.Find<CompetitionsApp>(true);
        private static DeviceService Devices => SceneCache.Find<DeviceService>(true);

        public static bool InGameplay => App != null && Devices != null;

        // The device this mod put on the bench for the current race (destroyed again after the race).
        private static DeviceContainer current;

        public static bool BenchEmpty
        {
            get
            {
                var d = Devices;
                return d != null && d.PlacedDeviceContainer == null;
            }
        }

        public static bool BenchHasRaceDevice
        {
            get
            {
                var d = Devices;
                return d != null && current != null && d.PlacedDeviceContainer == current;
            }
        }

        public static bool IsRaceDevice(DeviceContainer c) => c != null && current != null && c == current;

        // Every contest device of this game build: the list is the same on both copies. Career licenses
        // only gate the contest shop, so the race ignores them.
        public static List<DeviceInfo> Available()
        {
            var list = new List<DeviceInfo>();
            var app = App;
            if (app == null) return list;
            foreach (var info in app.AvailableDevices)
                if (info is DeviceInfo d) list.Add(d);
            list.Sort((a, b) => string.CompareOrdinal(a.ID, b.ID));
            return list;
        }

        public static DeviceInfo Find(string id)
        {
            var app = App;
            if (app == null) return null;
            foreach (var info in app.AvailableDevices)
                if (info is DeviceInfo d && d.ID == id) return d;
            return null;
        }

        public static string DisplayName(string id)
        {
            const string suffix = " - DeviceInfo";
            return id != null && id.EndsWith(suffix) ? id.Substring(0, id.Length - suffix.Length) : id;
        }

        public static void DestroyRaceDevice()
        {
            var d = Devices;
            if (d != null && current != null && d.PlacedDeviceContainer == current)
            {
                d.DestroyDeviceContainer();
                Plugin.Log.LogInfo("race device removed from the bench");
            }
            current = null;
        }

        public static bool TrySpawn(string deviceId, out string error)
        {
            error = null;
            try
            {
                var app = App;
                var devices = Devices;
                if (app == null || devices == null) { error = "not in the workshop yet"; return false; }
                if (devices.PlacedDeviceContainer != null) { error = "the bench is not empty"; return false; }
                if (GenerateCondition == null) { error = "game changed: GenerateCompetitionDeviceCondition not found"; return false; }

                DeviceInfo info = null;
                foreach (var d in app.AvailableDevices)
                    if (d is DeviceInfo di && di.ID == deviceId) { info = di; break; }
                if (info == null) { error = "unknown device " + deviceId; return false; }

                var args = new object[] { info, null };
                var condition = (RandomlyGeneratedDeviceCondition)GenerateCondition.Invoke(app, args);
                var generatedId = (string)args[1];
                var data = devices.CreateDeviceData(condition, devices.transform, new GeneratedDeviceProperty(generatedId));
                // CreateDeviceData marks devices without a career license; the registry would then pack them away.
                data.InteractiveObjectAdditionalProperties.RemoveProperty<PackedAsUnlicensedObjectProperty>();
                var pack = devices.CreateInitialCompetitionPackedDeviceContainer(data, devices.transform);
                current = pack.DeviceContainer;
                devices.PlaceDeviceContainer(pack);
                Plugin.Log.LogInfo("spawned race device " + deviceId + " (" + generatedId + ") on the bench");
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                Plugin.Log.LogError("spawn of " + deviceId + " failed: " + e);
                return false;
            }
        }

        // A race time is not a career contest time: no best-time record and no reward for it.
        [HarmonyPatch(typeof(CompetitionsResultsTrackingService), nameof(CompetitionsResultsTrackingService.TryRecordNewTime))]
        private static class NoCareerRecordPatch
        {
            private static bool Prefix(ref bool __result)
            {
                var mode = ContestHooks.Mode;
                if (mode == null || !IsRaceDevice(mode.CurrentDeviceInCompetition)) return true;
                __result = false;
                Plugin.Log.LogInfo("race time not recorded as a career best time");
                return false;
            }
        }
    }
}

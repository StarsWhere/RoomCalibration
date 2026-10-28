using System;
using System.Reflection;
using System.Threading.Tasks;
using BeatSaber.Settings;
using HarmonyLib;
using HMUI;
using IPA;
using IPA.Config.Stores;
using SiraUtil.Zenject;
using Zenject;

[assembly: AssemblyVersion("0.4.1.0")]

namespace RoomCalibration
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static IPA.Logging.Logger Log;
        internal static CalibrationConfig Config;
        internal static readonly SessionTransform Session = new SessionTransform();
        internal static readonly StartupCalibrationGate Startup = new StartupCalibrationGate();
        Harmony harmony;
        [Init]
        public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
        {
            Log = logger;
            // Preserve the existing IPA store identity while the display name changes.
            var legacy = IPA.Config.Config.GetConfigFor("当前位置校准");
            Config=legacy.Generated<CalibrationConfig>();
            legacy.LoadSync();
            Config.OnReload();
            Session.NativeReset();
            Startup.Begin(Config.AutoCalibrateOnLaunch);
            zenjector.Install<AppInstaller>(Location.App);
            zenjector.Install<MenuInstaller>(Location.Menu);
            zenjector.Install<GameInstaller>(Location.StandardPlayer | Location.CampaignPlayer);
            zenjector.Install<UnsupportedInstaller>(Location.MultiPlayer | Location.Tutorial);
        }
        [OnStart]
        public void Start()
        {
            harmony = new Harmony("local.beatsaber.room-calibration");
            try { Patches.Install(harmony); }
            catch { harmony.UnpatchSelf(); harmony = null; throw; }
            Log.Info("RoomCalibration 0.4.1 enabled; auto calibration on launch="+Config.AutoCalibrateOnLaunch);
        }
        [OnExit]
        public void Exit()
        {
            try { if (CalibrationService.Current != null) CalibrationService.Current.Shutdown(); }
            finally {
                Startup.Complete(); Session.NativeReset();
                if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
            }
        }
    }
    public sealed class AppInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<CalibrationService>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("RoomCalibration.Service").AsSingle().NonLazy();
        }
    }
    public sealed class MenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<MenuContext>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("RoomCalibration.Menu").AsSingle().NonLazy();
        }
    }
    public sealed class GameInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<GameContext>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("RoomCalibration.Game").AsSingle().NonLazy();
        }
    }
    public sealed class UnsupportedInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<UnsupportedContext>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("RoomCalibration.Unsupported").AsSingle().NonLazy();
        }
    }
    internal static class Patches
    {
        static MethodInfo setRoom;
        static MethodInfo provideInitialViews;
        static FieldInfo roomOwner;
        internal static void Install(Harmony harmony)
        {
            setRoom = Require(typeof(VRCenterAdjust), "SetRoomTransformOffset");
            provideInitialViews = Require(typeof(FlowCoordinator), "ProvideInitialViewControllers");
            roomOwner=AccessTools.Field(typeof(VRCenterAdjust),"_settingsManager");
            if(roomOwner==null) throw new MissingFieldException("VRCenterAdjust","_settingsManager");
            Patch(harmony, Require(typeof(VRCenterAdjust), "Start"), "CenterStart");
            harmony.Patch(Require(typeof(VRCenterAdjust),"OnEnable"),postfix:new HarmonyMethod(typeof(Patches),"CenterEnabled"));
            Patch(harmony, Require(typeof(VRCenterAdjust), "ResetRoom"), "NativeReset");
            Patch(harmony, Require(typeof(RoomAdjustSettingsViewController), "ResetRoom"), "NativeReset");
            Patch(harmony, Require(typeof(SettingsIO), "SaveAsync", new[] { typeof(IFileStorage), typeof(Settings) }), "Saving");
            harmony.Patch(Require(typeof(HealthWarningFlowCoordinator), "DidActivate"),
                postfix: new HarmonyMethod(typeof(Patches), "StartupActivated"));
            Patch(harmony, Require(typeof(HealthWarningFlowCoordinator), "AgreeToPrivacyPolicyAsync"), "SkipPrivacy");
            Patch(harmony, Require(typeof(HealthWarningFlowCoordinator), "AgreeToEulaAsync"), "SkipEula");
            Patch(harmony, Require(typeof(HealthWarningFlowCoordinator), "ShowHealthWarningAsync"), "SkipHealthWarning");
        }
        static MethodInfo Require(Type type, string name, Type[] parameters = null)
        {
            MethodInfo method = AccessTools.Method(type, name, parameters);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        static void Patch(Harmony harmony, MethodInfo method, string prefix)
        {
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Patches), prefix));
        }
        static bool CenterStart(VRCenterAdjust __instance)
        {
            CalibrationService current = CalibrationService.Current;
            if (current == null || !current.SessionActive) return true;
            // The original Start clears offsets over 4.1m before applying them.
            setRoom.Invoke(__instance, null);
            return false;
        }
        internal static void SynchronizeRoom(SettingsManager owner)
        {
            foreach(VRCenterAdjust center in UnityEngine.Resources.FindObjectsOfTypeAll<VRCenterAdjust>()) {
                if(center!=null && center.gameObject.activeInHierarchy && Object.ReferenceEquals(roomOwner.GetValue(center),owner))
                    setRoom.Invoke(center,null);
            }
        }
        static void CenterEnabled(VRCenterAdjust __instance)
        {
            CalibrationService current=CalibrationService.Current;
            SettingsManager owner=(SettingsManager)roomOwner.GetValue(__instance);
            if(current!=null && owner!=null && current.OwnsRoom(owner)) setRoom.Invoke(__instance,null);
        }
        static void NativeReset()
        {
            CalibrationService current = CalibrationService.Current;
            if (current != null) current.BeforeNativeReset();
        }
        static void Saving(ref Settings settings)
        {
            CalibrationService current = CalibrationService.Current;
            if (current != null) settings.room = current.PersistentRoom(settings.room);
        }
        static void StartupActivated(HealthWarningFlowCoordinator __instance, bool addedToHierarchy,
            ViewController ____providedMainViewController)
        {
            CalibrationService current = CalibrationService.Current;
            if (current != null) current.EnterStartup(__instance);
            if (!addedToHierarchy || ____providedMainViewController != null) return;
            // Every native root flow must provide its initial view synchronously.
            // Skipping all statements completes their tasks before any native view
            // is provided; __StartOnScreenSystem still presents that initial view.
            // BSML's menu container is not initialized in the startup scene yet.
            UnityEngine.GameObject host = new UnityEngine.GameObject("RoomCalibration.StartupView",
                typeof(UnityEngine.RectTransform), typeof(UnityEngine.Canvas),
                typeof(UnityEngine.CanvasGroup), typeof(UnityEngine.UI.GraphicRaycaster), typeof(ViewController));
            host.layer = 5;
            host.transform.SetParent(__instance.transform, false);
            UnityEngine.RectTransform rect = (UnityEngine.RectTransform)host.transform;
            rect.anchorMin = UnityEngine.Vector2.zero; rect.anchorMax = UnityEngine.Vector2.one;
            rect.sizeDelta = UnityEngine.Vector2.zero; rect.anchoredPosition = UnityEngine.Vector2.zero;
            host.SetActive(false);
            ViewController view = host.GetComponent<ViewController>();
            StartupViewLifetime lifetime = __instance.gameObject.AddComponent<StartupViewLifetime>();
            lifetime.View = view;
            provideInitialViews.Invoke(__instance, new object[] { view, null, null, null, null });
            Plugin.Log.Info("Startup flow initial view supplied after skipping statements");
        }
        static bool SkipPrivacy(PlayerDataModel ____playerDataModel, ref Task __result)
        {
            PlayerAgreements agreements = ____playerDataModel.playerData.playerAgreements;
            if (!agreements.AgreedToPrivacyPolicy()) agreements.AgreeToPrivacyPolicy();
            __result = Task.FromResult(true);
            return false;
        }
        static bool SkipEula(PlayerDataModel ____playerDataModel, ref Task __result)
        {
            PlayerAgreements agreements = ____playerDataModel.playerData.playerAgreements;
            if (!agreements.AgreedToEula()) agreements.AgreeToEula();
            __result = Task.FromResult(true);
            return false;
        }
        static bool SkipHealthWarning(PlayerDataModel ____playerDataModel, ref Task __result)
        {
            PlayerData player = ____playerDataModel.playerData;
            if (!player.playerAgreements.AgreedToHealthAndSafety()) player.playerAgreements.AgreeToHealthAndSafety();
            if (!player.DidSelectRegion()) player.MarkRegionAsSelected(2);
            __result = Task.FromResult(true);
            Plugin.Log.Info("Startup policy and health confirmation skipped");
            return false;
        }
    }
    public sealed class StartupViewLifetime : UnityEngine.MonoBehaviour
    {
        internal ViewController View;
        void OnDestroy() { if (View != null) UnityEngine.Object.Destroy(View.gameObject); }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BeatSaber.Settings;
using BeatSaberMarkupLanguage.Components.Settings;
using HarmonyLib;
using IPA;
using RoomCalibration;
using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace RoomCalibrationSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        GameObject owner;
        Harmony probe;
        [OnStart] public void Start()
        {
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_DIR"))) return;
            probe = new Harmony("local.room-calibration.startup-smoke");
            // FPFC omits the startup scene; include it so these checks exercise
            // the same native statement flow used by a headset launch.
            probe.Patch(AccessTools.PropertyGetter(Type.GetType("BeatSaber.Destinations.Destination, BeatSaber.Destinations", true), "shouldLoadHealthWarning"),
                prefix: new HarmonyMethod(typeof(TrackingProbe), "IncludeStartup"));
            probe.Patch(AccessTools.PropertyGetter(typeof(CalibrationService), "Focused"),
                prefix: new HarmonyMethod(typeof(TrackingProbe), "Focus"));
            probe.Patch(AccessTools.Method(typeof(CalibrationService), "TryHeadPose"),
                prefix: new HarmonyMethod(typeof(TrackingProbe), "Pose"));
            probe.Patch(AccessTools.Method(typeof(HMUI.FlowCoordinator), "__StartOnScreenSystem"),
                prefix: new HarmonyMethod(typeof(TrackingProbe), "StartupStarting"),
                postfix: new HarmonyMethod(typeof(TrackingProbe), "StartupStarted"));
            probe.Patch(AccessTools.Method(typeof(HMUI.FlowCoordinator), "PresentViewController"),
                prefix: new HarmonyMethod(typeof(TrackingProbe), "StartupPresenting"));
            Application.logMessageReceived += TrackingProbe.StartupLog;
            RenderPipelineManager.beginCameraRendering += TrackingProbe.PlaceHead;
            RenderPipelineManager.endCameraRendering += TrackingProbe.RecordMenuFrame;
            TrackingProbe.ConfigureLaunch();
            foreach (Type type in new[] { typeof(HealthWarningViewController), typeof(PrivacyPolicyViewController), typeof(EulaViewController) })
                probe.Patch(AccessTools.Method(type, "DidActivate"), postfix: new HarmonyMethod(typeof(TrackingProbe), "NoticeShown"));
            if (TrackingProbe.NoticeMode)
                probe.Unpatch(AccessTools.Method(typeof(HealthWarningFlowCoordinator), "ShowHealthWarningAsync"),
                    HarmonyPatchType.Prefix, "local.beatsaber.room-calibration");
            owner=new GameObject("RoomCalibrationSmoke.Runner");
            UnityEngine.Object.DontDestroyOnLoad(owner); owner.AddComponent<Runner>();
        }
        [OnExit] public void Exit() { Application.logMessageReceived -= TrackingProbe.StartupLog;
            RenderPipelineManager.beginCameraRendering -= TrackingProbe.PlaceHead;
            RenderPipelineManager.endCameraRendering -= TrackingProbe.RecordMenuFrame;
            if(probe!=null) probe.UnpatchSelf(); if(owner!=null) UnityEngine.Object.Destroy(owner); }
    }
    public static class TrackingProbe
    {
        internal static bool Enabled, Tracked;
        internal static int NoticeActivations;
        internal static int StartupBeginCount, StartupCompleteCount, StartupPresentationCount;
        internal static readonly List<string> StartupErrors = new List<string>();
        internal static bool NoticeMode { get { return Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_NOTICE") == "1"; } }
        internal static Vector3 Position;
        internal static Quaternion Rotation;
        internal static bool OriginalAuto, FirstFrameSession;
        internal static int MenuFrames;
        internal static readonly List<string> RenderPaths=new List<string>();
        internal static RoomSettings FirstFrameRoom;
        internal static Vector3 FirstFrameHead;
        internal static string AutoMode { get { return Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_AUTO") ?? "Disabled"; } }
        internal static void ConfigureLaunch()
        {
            CalibrationConfig config=(CalibrationConfig)AccessTools.Field(typeof(RoomCalibration.Plugin),"Config").GetValue(null);
            OriginalAuto=config.AutoCalibrateOnLaunch;
            config.AutoCalibrateOnLaunch=AutoMode!="Disabled";
            object gate=AccessTools.Field(typeof(RoomCalibration.Plugin),"Startup").GetValue(null);
            AccessTools.Method(gate.GetType(),"Begin").Invoke(gate,new object[] { config.AutoCalibrateOnLaunch });
            Position=new Vector3(-1.5f,1.7f,2.5f); Rotation=Quaternion.Euler(0,-65,0);
            Enabled=true; Tracked=AutoMode!="Unavailable";
        }
        static bool MenuCamera(Camera camera)
        {
            CalibrationService service=CalibrationService.Current;
            if(camera==null || service==null || service.Game!=null || camera.GetComponentInParent<MainCamera>()==null) return false;
            MenuContext menu=(MenuContext)AccessTools.Field(typeof(CalibrationService),"menu").GetValue(service);
            return menu!=null && menu.isActiveAndEnabled;
        }
        internal static void PlaceHead(ScriptableRenderContext context,Camera camera)
        {
            if(camera!=null && RenderPaths.Count<8) {
                string path=camera.name+" / scene="+camera.gameObject.scene.name+" / main="+(camera.GetComponentInParent<MainCamera>()!=null);
                if(!RenderPaths.Contains(path)) RenderPaths.Add(path);
            }
            if(Enabled && Tracked && MenuCamera(camera)) {
                camera.transform.localPosition=Position; camera.transform.localRotation=Rotation;
            }
        }
        internal static void RecordMenuFrame(ScriptableRenderContext context,Camera camera)
        {
            if(!MenuCamera(camera) || MenuFrames++!=0) return;
            CalibrationService service=CalibrationService.Current;
            SettingsManager manager=(SettingsManager)AccessTools.Field(typeof(CalibrationService),"settings").GetValue(service);
            FirstFrameRoom=manager.settings.room;
            FirstFrameSession=service.SessionActive;
            FirstFrameHead=camera.transform.position;
        }
        static bool Focus(ref bool __result) { if (!Enabled) return true; __result = true; return false; }
        static bool IncludeStartup(ref bool __result) { __result = true; return false; }
        static bool FPFC(ref bool __result) { if (!Enabled) return true; __result = false; return false; }
        static bool Pose(ref Vector3 position, ref Quaternion rotation, ref string error, ref bool __result)
        {
            if (!Enabled) return true;
            position = Position; rotation = Rotation; error = Tracked ? null : "Headset tracking is not ready; try again";
            __result = Tracked;
            return false;
        }
        static void NoticeShown() { NoticeActivations++; }
        static void StartupStarting(HMUI.FlowCoordinator __instance)
        {
            if (__instance is HealthWarningFlowCoordinator) StartupBeginCount++;
        }
        static void StartupStarted(HMUI.FlowCoordinator __instance)
        {
            if (__instance is HealthWarningFlowCoordinator) StartupCompleteCount++;
        }
        static void StartupPresenting(HMUI.FlowCoordinator __instance, HMUI.ViewController __0)
        {
            if (!(__instance is HealthWarningFlowCoordinator)) return;
            StartupPresentationCount++;
            if (__0 == null) StartupErrors.Add("Native startup tried to present a null main view");
        }
        internal static void StartupLog(string message, string stack, LogType kind)
        {
            if (kind == LogType.Exception && (stack.Contains("__StartOnScreenSystem")
                || stack.Contains("HierarchyManager.StartWithFlowCoordinator")))
                StartupErrors.Add(message+"\n"+stack);
        }
    }
    public sealed class MemoryStorage : IFileStorage
    {
        internal readonly Dictionary<string,string> Files=new Dictionary<string,string>();
        public Task SaveFileAsync(string path,string data,StoragePreference preference) { Files[path]=data; return Task.FromResult(true); }
        public Task<string> LoadFileAsync(string path,StoragePreference preference) { return Task.FromResult(Files[path]); }
        public Task DeleteFileAsync(string path,StoragePreference preference) { Files.Remove(path); return Task.FromResult(true); }
        public Task<bool> FileExistsAsync(string path,StoragePreference preference) { return Task.FromResult(Files.ContainsKey(path)); }
    }
    public sealed class Runner : MonoBehaviour
    {
        readonly List<string> passed=new List<string>();
        readonly List<string> errors=new List<string>();
        CalibrationService service;
        SettingsManager manager;
        SettingsApplicatorSO applicator;
        Settings original;
        CalibrationConfig config;
        ControllerButton originalLeft,originalRight;
        KeyboardShortcut originalKeyboard;
        float originalDuration;
        bool captured, done;
        string directory;
        float started;
        void Start()
        {
            directory=Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_DIR");
            started=Time.realtimeSinceStartup;
            StartCoroutine(Drive());
        }
        IEnumerator Drive()
        {
            Stack<IEnumerator> phases=new Stack<IEnumerator>();
            phases.Push(Run());
            while(phases.Count>0) {
                IEnumerator phase=phases.Peek();
                bool next=false; object current=null;
                try { next=phase.MoveNext(); if(next) current=phase.Current; }
                catch(Exception error) { errors.Add(error.ToString()); break; }
                if(!next) { phases.Pop(); continue; }
                IEnumerator child=current as IEnumerator;
                if(child!=null) { phases.Push(child); continue; }
                yield return current;
            }
            Finish();
        }
        IEnumerator Wait(Func<bool> predicate,float seconds,string label)
        {
            float deadline=Time.realtimeSinceStartup+seconds;
            while(Time.realtimeSinceStartup<deadline && Time.realtimeSinceStartup-started<180) {
                if(predicate()) { passed.Add(label); yield break; }
                yield return null;
            }
            throw new Exception("Timed out: "+label);
        }
        T Active<T>() where T : Component
        {
            return Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x=>x!=null && x.gameObject.activeInHierarchy);
        }
        static object Field(object value,string name) { return AccessTools.Field(value.GetType(),name).GetValue(value); }
        static object Invoke(object value,string name,params object[] arguments)
        {
            return AccessTools.Method(value.GetType(),name).Invoke(value,arguments);
        }
        static void Check(bool value,string label) { if(!value) throw new Exception(label); }
        void Pass(bool value,string label) { Check(value,label); passed.Add(label); }
        static bool Equal(RoomSettings a,RoomSettings b)
        {
            return ((Vector3)a.center-(Vector3)b.center).sqrMagnitude<0.0000001f && Mathf.Abs(Mathf.DeltaAngle(a.rotation,b.rotation))<0.001f;
        }
        void Seed(RoomSettings target)
        {
            object session=Field(service,"session");
            Invoke(session,"Commit",target,manager.settings.room);
            Invoke(service,"Apply",target);
        }
        IEnumerator Run()
        {
            yield return Wait(delegate { return CalibrationService.Current!=null && (bool)Field(CalibrationService.Current,"started"); },70,"early app injection");
            service=CalibrationService.Current;
            manager=(SettingsManager)Field(service,"settings");
            applicator=(SettingsApplicatorSO)Field(service,"applicator");
            original=manager.settings;
            original.room=(RoomSettings)Invoke(service,"PersistentRoom",original.room);
            captured=true;
            config=(CalibrationConfig)AccessTools.Field(typeof(RoomCalibration.Plugin),"Config").GetValue(null);
            originalLeft=config.LeftButton; originalRight=config.RightButton;
            originalKeyboard=config.Keyboard; originalDuration=config.PreparationSeconds;
            Pass((bool)Field(service,"started"),"service initialized");
            if (TrackingProbe.NoticeMode) {
                yield return Wait(delegate { return Active<HealthWarningViewController>()!=null
                    && Active<HealthWarningViewController>().isActivated; },30,"native startup statement active");
                Pass(Active<MenuContext>()==null && (bool)AccessTools.Property(typeof(CalibrationService),"ContextReady").GetValue(service,null),
                    "calibration is eligible on native startup statement before main menu exists");
                yield return StatementCalibrationChecks();
                Button next=(Button)Field(Active<HealthWarningViewController>(),"_continueButton");
                yield return Wait(delegate { return next.interactable; },15,"native statement continue ready");
                next.onClick.Invoke();
            }
            yield return Wait(delegate { return Active<MenuContext>()!=null && Active<MainMenuViewController>()!=null
                && Active<MainMenuViewController>().isActivated; },70,"app/menu injection");
            Pass(TrackingProbe.StartupBeginCount==1 && TrackingProbe.StartupCompleteCount==1,
                "native startup screen hierarchy finishes initialization");
            Pass(TrackingProbe.StartupPresentationCount>=1 && TrackingProbe.StartupErrors.Count==0,
                "native startup always receives a valid initial view and logs no hierarchy exception");
            yield return StartupRenderChecks();
            if (TrackingProbe.NoticeMode) {
                Pass(TrackingProbe.NoticeActivations==1,"only the deliberately restored statement was displayed");
                yield break;
            }
            Pass(TrackingProbe.NoticeActivations==0,"startup reaches main menu without policy or health confirmation");
            if(Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_STARTUP_ONLY")=="1") yield break;
            MenuContext context=Active<MenuContext>();
            SettingsPanel settingsPanel=(SettingsPanel)Field(context,"panel");
            Type registry=Type.GetType("LocalModUI.UiRegistry, LocalModUI",true);
            IEnumerable registered=(IEnumerable)AccessTools.Property(registry,"Entries").GetValue(null,null);
            object calibrationEntry=registered.Cast<object>().FirstOrDefault(value =>
                (string)AccessTools.Property(value.GetType(),"Id").GetValue(value,null)=="calibration");
            Pass(calibrationEntry!=null,"calibration registered in local mod center");
            object page=AccessTools.Property(calibrationEntry.GetType(),"Page").GetValue(calibrationEntry,null);
            Action shortcut=(Action)AccessTools.Property(calibrationEntry.GetType(),"ShortcutAction").GetValue(calibrationEntry,null);
            Pass(System.Object.ReferenceEquals(AccessTools.Property(page.GetType(),"Host").GetValue(page,null),settingsPanel)
                && shortcut!=null && System.Object.ReferenceEquals(shortcut.Target,service)
                && shortcut.Method.Name=="RequestCalibration","center settings and shortcut use the current service");
            object settingsMenu=Field(context,"settingsMenu");
            IEnumerable entries=(IEnumerable)AccessTools.Property(settingsMenu.GetType(),"SettingsMenus").GetValue(settingsMenu,null);
            object calibrationMenu=null;
            foreach(object entry in entries) {
                PropertyInfo host=AccessTools.Property(entry.GetType(),"Host");
                if(host!=null && System.Object.ReferenceEquals(host.GetValue(entry,null),settingsPanel)) calibrationMenu=entry;
            }
            Pass(calibrationMenu!=null,"mod configuration entry registered");
            Invoke(calibrationMenu,"Setup");
            Component settingsView=(Component)AccessTools.Property(calibrationMenu.GetType(),"ViewController").GetValue(calibrationMenu,null);
            Pass(settingsView.GetComponentsInChildren<DropDownListSetting>(true).Length==3
                && settingsView.GetComponentsInChildren<IncrementSetting>(true).Length==1
                && settingsView.GetComponentsInChildren<ToggleSetting>(true).Length==1,"configuration builds optional launch toggle, three bindings and duration control");
            Button[] pageButtons=settingsView.GetComponentsInChildren<Button>(true);
            Button calibrateButton=pageButtons.FirstOrDefault(button => button.GetComponentsInChildren<TMP_Text>(true)
                .Any(label => label.text=="Calibrate to Current Position" || label.text=="按当前位置校准"));
            Button clearButton=pageButtons.FirstOrDefault(button => button.GetComponentsInChildren<TMP_Text>(true)
                .Any(label => label.text=="Clear Session Calibration" || label.text=="清除本次校准"));
            Pass(calibrateButton!=null && clearButton!=null,"settings page exposes calibration and clear actions");
            Pass(!new CalibrationConfig().AutoCalibrateOnLaunch,"new and existing configs without launch setting default to disabled");
            ToggleSetting autoToggle=settingsView.GetComponentInChildren<ToggleSetting>(true);
            bool autoBefore=config.AutoCalibrateOnLaunch;
            autoToggle.Value=!autoBefore; autoToggle.ApplyValue();
            Pass(config.AutoCalibrateOnLaunch!=autoBefore,"launch toggle writes the configuration");
            settingsPanel.AutoCalibrateOnLaunch=autoBefore;
            settingsView.gameObject.SetActive(true);
            yield return null;
            IncrementSetting interval=settingsView.GetComponentInChildren<IncrementSetting>(true);
            Pass(!interval.IsInt && Mathf.Abs(interval.Increments-0.25f)<0.001f,"duration accepts fractional seconds");
            float beforeInterval=config.PreparationSeconds;
            ((Button)Field(interval,"incButton")).onClick.Invoke();
            Pass(Mathf.Abs(config.PreparationSeconds-beforeInterval)>0.001f,"native duration control changes saved configuration");
            settingsPanel.LeftButton="Grip"; settingsPanel.RightButton="Primary (A/X)";
            settingsPanel.Keyboard="F9"; settingsPanel.PreparationSeconds=2.75f;
            Pass(config.LeftButton==ControllerButton.Grip && config.RightButton==ControllerButton.Primary
                && config.Keyboard==KeyboardShortcut.F9 && Mathf.Abs(config.PreparationSeconds-2.75f)<0.001f,"left/right buttons, keyboard and duration configurable");
            settingsPanel.PreparationSeconds=0;
            Pass(Mathf.Abs(config.PreparationSeconds-0.5f)<0.001f,"configuration runtime duration validation");
            RestoreConfiguration();
            settingsView.gameObject.SetActive(false);
            calibrateButton.onClick.Invoke();
            yield return null;
            Pass(service.LastCategory=="tracking" || service.LastCategory=="focus","FPFC rejects untracked calibration");
            Pass(Equal(manager.settings.room,original.room),"rejected calibration leaves room unchanged");
            yield return null;
            PrepareCapture();
            yield return new WaitForSecondsRealtime(2f);
            yield return new WaitForEndOfFrame();
            TMP_Text[] menuLabels=Resources.FindObjectsOfTypeAll<TMP_Text>().Where(x=>x!=null && x.gameObject.activeInHierarchy).ToArray();
            passed.Add("menu labels: "+String.Join(" | ",menuLabels.Select(x=>x.text).ToArray()));
            Pass(menuLabels.Any(x=>x.text=="按当前位置校准"),"main calibration label visible");
            Pass(!menuLabels.Any(x=>x.text=="清除本次校准" || x.text=="Clear Session Calibration"),"clear action is available from settings instead of the main menu");
            foreach(TMP_Text label in menuLabels.Where(x=>x.text.Contains("校准"))) {
                label.ForceMeshUpdate();
                passed.Add("menu calibration text geometry: "+label.text+" / rect="+label.rectTransform.rect+" / font="+label.fontSize
                    +" / alpha="+label.color.a+" / visible="+label.textInfo.characterInfo.Count(x=>x.isVisible)
                    +" / viewport="+Active<MainCamera>().camera.WorldToViewportPoint(label.transform.position));
            }
            Capture("menu.png");
            Invoke(service,"ShowCountdown",2.75);
            yield return null;
            Pass(Resources.FindObjectsOfTypeAll<TMP_Text>().Any(x=>x!=null && x.gameObject.activeInHierarchy
                && x.text=="即将校准 · 2.8 秒"),"pre-calibration countdown appears in player view");
            yield return new WaitForEndOfFrame();
            Capture("countdown.png");
            Invoke(service,"HideCountdown");
            if(Environment.GetEnvironmentVariable("ROOM_CALIBRATION_SMOKE_VISUAL")=="1") yield break;
            yield return new WaitForSecondsRealtime(0.3f);

            RoomSettings target=new RoomSettings { center=new float3(-6,original.room.center.y,5),rotation=-90 };
            Seed(target);
            yield return new WaitForSecondsRealtime(0.2f);
            Pass(service.SessionActive,"synthetic session active");
            VRCenterAdjust center=Active<VRCenterAdjust>();
            Pass(center!=null && ((Vector3)target.center-center.transform.localPosition).sqrMagnitude<0.000001f
                && Quaternion.Angle(center.transform.localRotation,Quaternion.Euler(0,target.rotation,0))<0.01f,"native rig offset updated");
            MainCamera main=PlayerCamera();
            Pass(main!=null && main.GetComponentInParent<VRCenterAdjust>()==center,"head uses native room rig");
            Pass(Resources.FindObjectsOfTypeAll<VRController>().Count(x=>x!=null && x.gameObject.activeInHierarchy
                && x.GetComponentInParent<VRCenterAdjust>()==center)>=2,"both native controllers share calibrated rig");
            Component avatarCamera=main.GetComponents<Component>().FirstOrDefault(x=>x!=null
                && x.GetType().FullName=="CustomAvatar.Rendering.Cameras.MainCameraTracker");
            if(avatarCamera!=null) Pass((Transform)AccessTools.Property(avatarCamera.GetType(),"playerSpace").GetValue(avatarCamera,null)
                ==center.transform,"custom avatar references calibrated player space; motion disabled in FPFC");
            RoomSettings before=manager.settings.room;
            Invoke(center,"Start");
            Pass(Equal(manager.settings.room,before),"native 4.1m scene guard bypassed only for active session");

            Settings saving=manager.settings;
            saving.audio.volume=0.63f;
            MemoryStorage storage=new MemoryStorage();
            Task save=SettingsIO.SaveAsync(storage,saving);
            yield return Wait(delegate { return save.IsCompleted; },2,"native save completed");
            if(save.IsFaulted) throw save.Exception;
            string data=storage.Files[SettingsIO.SettingsPath];
            object[] decodeArgs=new object[] { original,data };
            bool decoded=(bool)AccessTools.Method(typeof(SettingsIO),"Decode").Invoke(null,decodeArgs);
            Settings saved=(Settings)decodeArgs[0];
            Pass(decoded && Equal(saved.room,original.room),"actual SettingsIO save excludes temporary calibration");
            Pass(Mathf.Abs(saved.audio.volume-0.63f)<0.0001f,"other settings survive save interception");
            Pass(Equal(manager.settings.room,target),"asynchronous save does not alter live room");

            Action failedListener=delegate { throw new InvalidOperationException("Expected smoke notification failure"); };
            applicator.roomTransformOffsetDidUpdateEvent+=failedListener;
            try { Invoke(service,"ClearSession"); }
            finally { applicator.roomTransformOffsetDidUpdateEvent-=failedListener; }
            Pass(service.LastCategory=="apply-failed" && service.SessionActive && Equal(manager.settings.room,target),"failed notification rolls back room and session");
            Pass(Vector3.Distance(center.transform.localPosition,target.center)<0.001f,"rollback reaches native rig listeners");

            RoomSettings edited=manager.settings.room;
            edited.center.y+=0.2f;
            manager.settings.room=edited; applicator.NotifyRoomTransformOffsetWasUpdated();
            RoomSettings persistent=(RoomSettings)Invoke(service,"PersistentRoom",edited);
            Pass(Mathf.Abs(persistent.center.y-original.room.center.y-0.2f)<0.0001f,"explicit floor edits remain persistent");
            Invoke(service,"ClearSession");
            Pass(!service.SessionActive && Equal(manager.settings.room,persistent),"clear restores edited baseline");
            manager.settings.room=original.room; applicator.NotifyRoomTransformOffsetWasUpdated();
            Seed(target);
            Invoke(center,"ResetRoom");
            Pass(!service.SessionActive,"native reset clears session");
            manager.settings.room=original.room; applicator.NotifyRoomTransformOffsetWasUpdated();
            Seed(target);

            Type loader=Type.GetType("SongCore.Loader, SongCore",true);
            object level=null;
            yield return Wait(delegate {
                level=AccessTools.Method(loader,"GetLevelById").Invoke(null,new object[] { "custom_level_3DBD9099018FB40EB3F27476589C2F1D0753C5F4" });
                return level!=null;
            },30,"known native test song loaded");
            object pack=AccessTools.Property(loader,"CustomLevelsPack")!=null
                ? AccessTools.Property(loader,"CustomLevelsPack").GetValue(null,null)
                : AccessTools.Field(loader,"CustomLevelsPack").GetValue(null);
            IEnumerable keys=(IEnumerable)AccessTools.Method(level.GetType(),"GetBeatmapKeys").Invoke(level,null);
            object key=null;
            foreach(object candidate in keys) { key=candidate; break; }
            object state=typeof(LevelSelectionFlowCoordinator.State).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                .First(x=>x.GetParameters().Length==4).Invoke(new object[] { SelectLevelCategoryViewController.LevelCategory.All,pack,key,level });
            SoloFreePlayFlowCoordinator soloFlow=Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First();
            AccessTools.Method(typeof(SoloFreePlayFlowCoordinator),"Setup").Invoke(soloFlow,new object[] { state });
            Button solo=(Button)Field(Active<MainMenuViewController>(),"_soloButton");
            solo.onClick.Invoke();
            yield return Wait(delegate { StandardLevelDetailViewController detail=Active<StandardLevelDetailViewController>();
                return detail!=null && (bool)Field(detail,"_contentIsOwnedAndReady")
                    && detail.GetComponentsInChildren<Button>(true).Any(IsPlayButton); },30,"song detail ready");
            yield return new WaitForSecondsRealtime(1f);
            StandardLevelDetailViewController selected=Active<StandardLevelDetailViewController>();
            Button play=selected.GetComponentsInChildren<Button>(true).First(IsPlayButton);
            play.onClick.Invoke();
            passed.Add("start button: "+play.name+" / "+String.Join(" ",play.GetComponentsInChildren<TMP_Text>(true).Select(x=>x.text).ToArray()));
            yield return Wait(delegate { return service.Game!=null && service.Game.Ready
                && !(bool)AccessTools.Property(typeof(GameScenesManager),"isInTransition").GetValue(Field(service,"scenes"),null); },35,"gameplay injection");
            Pass(Equal(manager.settings.room,target),"session preserved entering gameplay");
            yield return new WaitForSecondsRealtime(0.3f);
            GameContext game=service.Game;
            MainCamera gameHead=PlayerCamera();
            VRCenterAdjust gameCenter=gameHead.GetComponentInParent<VRCenterAdjust>();
            passed.Add("game room owner: "+gameHead.name+" / center="+gameCenter.name+" / same manager="
                +System.Object.ReferenceEquals(Field(gameCenter,"_settingsManager"),manager)+" / same applicator="
                +System.Object.ReferenceEquals(Field(gameCenter,"_settingsApplicator"),applicator));
            Invoke(service,"OriginChanged",new object[] { null });
            Pass(service.NeedsCalibration,"runtime origin event marks calibration stale");
            Pass(game.Paused,"origin change uses calibration's native auto-pause path");
            yield return Wait(delegate { return game.Paused; },2,"song paused");
            yield return new WaitForSecondsRealtime(0.6f);
            GameObject ui=(GameObject)Field(game,"ui");
            Pass(ui!=null && ui.activeInHierarchy,"pause controls visible");
            Button[] controls=ui.GetComponentsInChildren<Button>(true);
            Pass(controls.Length==2 && controls.All(x=>x.interactable),"both pause controls interactive");
            Pass(controls.SelectMany(x=>x.GetComponentsInChildren<TMP_Text>(true)).Any(x=>x.text=="按当前位置校准")
                && controls.SelectMany(x=>x.GetComponentsInChildren<TMP_Text>(true)).Any(x=>x.text=="清除本次校准"),"pause labels correct");
            controls.First(x=>x.GetComponentsInChildren<TMP_Text>(true).Any(t=>t.text=="按当前位置校准")).onClick.Invoke();
            Pass((service.LastCategory=="tracking" || service.LastCategory=="focus") && game.Paused,"pause calibration action reaches tracked-pose gate without resuming");
            Pass(NoOverlap(ui,game),"calibration buttons do not overlap native pause actions");
            RoomSettings gameplayBaseline=(RoomSettings)Invoke(service,"PersistentRoom",manager.settings.room);
            Invoke(service,"ClearSession");
            Pass(Vector3.Distance(gameCenter.transform.localPosition,manager.settings.room.center)<0.001f,
                "live gameplay rig follows clear; actual="+gameCenter.transform.localPosition+" / requested="+manager.settings.room.center);
            RoomSettings yawTarget=new RoomSettings { center=new float3(-2,gameplayBaseline.center.y,-3),rotation=game.TargetYaw };
            Seed(yawTarget);
            Pass(game.Paused && Mathf.Abs(Mathf.DeltaAngle(manager.settings.room.rotation,game.TargetYaw))<0.001f,"chart target yaw retained while paused");
            Seed(target);
            PauseMenuManager pauseMenu=(PauseMenuManager)Field(game,"pauseMenu");
            Invoke(pauseMenu,"RestartButtonPressed");
            yield return Wait(delegate { return service.Game!=null && service.Game!=game && service.Game.Ready
                && !(bool)AccessTools.Property(typeof(GameScenesManager),"isInTransition").GetValue(Field(service,"scenes"),null); },30,"restart scene ready");
            Pass(Equal(manager.settings.room,target),"session survives restart");
            yield return new WaitForSecondsRealtime(0.3f);
            GameContext restarted=service.Game;
            if(!restarted.Paused) Check(restarted.TryPause(),"restarted pause accepted");
            yield return Wait(delegate { return restarted.Paused; },2,"restarted song paused");
            yield return new WaitForSecondsRealtime(0.6f);
            // Return the camera to its baseline so the pause screenshot is legible in FPFC.
            Invoke(service,"ClearSession");
            yield return null;
            PrepareCapture();
            yield return null;
            yield return new WaitForEndOfFrame();
            Capture("pause.png");
            yield return new WaitForSecondsRealtime(0.3f);
            RoomSettings returnTarget=new RoomSettings { center=new float3(1,original.room.center.y,-2),rotation=45 };
            Seed(returnTarget);
            Invoke((PauseMenuManager)Field(restarted,"pauseMenu"),"MenuButtonPressed");
            yield return Wait(delegate { return service.Game==null && Active<MenuContext>()!=null
                && !(bool)AccessTools.Property(typeof(GameScenesManager),"isInTransition").GetValue(Field(service,"scenes"),null); },30,"returned to menu");
            Pass(Equal(manager.settings.room,returnTarget),"changed gameplay calibration survives return to menu");
            VRCenterAdjust returnedCenter=PlayerCamera().GetComponentInParent<VRCenterAdjust>();
            Pass(Vector3.Distance(returnedCenter.transform.localPosition,returnTarget.center)<0.001f
                && Quaternion.Angle(returnedCenter.transform.localRotation,Quaternion.Euler(0,returnTarget.rotation,0))<0.01f,
                "reactivated menu rig receives calibration changed during gameplay");
            Invoke(service,"ClearSession");
            Pass(!service.SessionActive && Equal(manager.settings.room,original.room),"final clear restores original baseline");
        }
        IEnumerator StartupRenderChecks()
        {
            if(TrackingProbe.MenuFrames==0) {
                PrepareCapture();
                yield return new WaitForSecondsRealtime(1f);
                if(TrackingProbe.MenuFrames==0) Capture("startup.png");
            }
            yield return Wait(delegate { return TrackingProbe.MenuFrames>0; },5,"first menu frame rendered");
            if(TrackingProbe.AutoMode=="Enabled") {
                Pass(TrackingProbe.FirstFrameSession,"enabled launch option calibrates before the first menu frame finishes");
                Pass(Mathf.Abs(TrackingProbe.FirstFrameHead.x)<0.001f && Mathf.Abs(TrackingProbe.FirstFrameHead.z)<0.001f,
                    "first rendered native head is already centered");
            } else {
                Pass(!TrackingProbe.FirstFrameSession && Equal(TrackingProbe.FirstFrameRoom,original.room),
                    "disabled or untracked startup displays the native room without moving it");
            }
            RoomSettings visible=manager.settings.room;
            TrackingProbe.Tracked=true;
            TrackingProbe.Position+=new Vector3(1,0,0.5f);
            config.AutoCalibrateOnLaunch=true;
            yield return new WaitForSecondsRealtime(0.5f);
            Pass(Equal(manager.settings.room,visible),"late tracking, walking and enabling the option after menu display never trigger automatic movement");
            config.AutoCalibrateOnLaunch=TrackingProbe.OriginalAuto;
            TrackingProbe.Enabled=false;
            if(service.SessionActive) Invoke(service,"ClearSession");
        }
        IEnumerator StatementCalibrationChecks()
        {
            object fpfc=Field(service,"fpfc");
            Harmony tracking=new Harmony("local.room-calibration.fpfc-smoke");
            if(fpfc!=null) tracking.Patch(AccessTools.PropertyGetter(fpfc.GetType(),"Enabled"),
                prefix:new HarmonyMethod(typeof(TrackingProbe),"FPFC"));
            try {
                TrackingProbe.Position=new Vector3(2,1.7f,-3);
                TrackingProbe.Rotation=Quaternion.Euler(0,40,0);
                TrackingProbe.Enabled=true; TrackingProbe.Tracked=true;
                config.PreparationSeconds=0.5f;
                yield return null;
                service.RequestCalibration();
                Pass(service.LastCategory=="preparing","startup statement accepts the normal calibration request");
                yield return Wait(delegate { return service.LastCategory=="calibrated"; },3,"manual calibration completes on startup statement");
            }
            finally {
                TrackingProbe.Enabled=false;
                tracking.UnpatchSelf();
                config.PreparationSeconds=originalDuration;
                if(service.SessionActive) Invoke(service,"ClearSession");
            }
        }
        static bool IsPlayButton(Button button)
        {
            if(button==null || !button.interactable || !button.gameObject.activeInHierarchy) return false;
            string name=button.name.ToLowerInvariant();
            string label=String.Join(" ",button.GetComponentsInChildren<TMP_Text>(true).Select(x=>x.text).ToArray());
            return !name.Contains("practice") && !label.Contains("练习")
                && (name.Contains("action") || name.Contains("play") || label.Contains("开始"));
        }
        void Capture(string name)
        {
            MainCamera main=PlayerCamera();
            Check(main!=null && main.camera!=null,"capture source camera");
            Camera camera=main.camera;
            RenderTexture previousActive=RenderTexture.active,previousTarget=camera.targetTexture;
            bool enabled=camera.enabled;
            Rect previousRect=camera.rect;
            float previousAspect=camera.aspect;
            Vector3 previousPosition=camera.transform.localPosition;
            Quaternion previousRotation=camera.transform.localRotation;
            UniversalAdditionalCameraData cameraData=camera.GetComponent<UniversalAdditionalCameraData>();
            bool xrRendering=cameraData!=null && cameraData.allowXRRendering;
            RenderTexture target=null; Texture2D texture=null;
            try {
                target=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32); target.Create();
                camera.targetTexture=target; camera.enabled=true; camera.rect=new Rect(0,0,1,1); camera.aspect=1920f/1080;
                if(service.Game!=null) { camera.transform.localPosition=new Vector3(0,1.7f,0); camera.transform.localRotation=Quaternion.identity; }
                if(cameraData!=null) cameraData.allowXRRendering=false;
                RenderPipeline.StandardRequest request=new RenderPipeline.StandardRequest();
                request.destination=target; request.face=CubemapFace.Unknown;
                Check(RenderPipeline.SupportsRenderRequest(camera,request),"URP screenshot request supported");
                RenderPipeline.SubmitRenderRequest(camera,request);
                RenderTexture.active=target;
                texture=new Texture2D(1920,1080,TextureFormat.RGBA32,false);
                texture.ReadPixels(new Rect(0,0,1920,1080),0,0,false); texture.Apply();
                Color32[] pixels=texture.GetPixels32();
                int visible=pixels.Count(x=>x.r>24 || x.g>24 || x.b>24);
                for(int i=0;i<pixels.Length;i++) pixels[i].a=255;
                texture.SetPixels32(pixels); texture.Apply();
                File.WriteAllBytes(Path.Combine(directory,name),texture.EncodeToPNG());
                Pass(visible>pixels.Length/200,"visible URP screenshot: "+name+" / camera="+camera.transform.position
                    +" / mask="+camera.cullingMask+" / pixels="+visible+" / fov="+camera.fieldOfView);
            }
            finally {
                RenderTexture.active=previousActive; camera.targetTexture=previousTarget;
                camera.enabled=enabled; camera.rect=previousRect; camera.aspect=previousAspect;
                camera.transform.localPosition=previousPosition; camera.transform.localRotation=previousRotation;
                if(cameraData!=null) cameraData.allowXRRendering=xrRendering;
                if(texture!=null) UnityEngine.Object.Destroy(texture);
                if(target!=null) { target.Release(); UnityEngine.Object.Destroy(target); }
            }
        }
        void PrepareCapture()
        {
            if(service.Game!=null) return; // The screenshot uses a temporary eye pose when no headset exists.
            MainCamera main=Active<MainCamera>();
            Check(main!=null,"capture main camera available");
            FirstPersonFlyingController controller=Active<FirstPersonFlyingController>();
            Check(controller!=null,"native FPFC capture controller available");
            MethodInfo inject=AccessTools.Method(typeof(FirstPersonFlyingController),"Inject");
            object center=Field(controller,"_centerAdjust");
            object left=Field(controller,"_controller0"),right=Field(controller,"_controller1");
            object driver=Field(controller,"_trackedPoseDriver");
            Check(center!=null && left!=null && right!=null,"native FPFC capture dependencies available");
            inject.Invoke(controller,new object[] { main.camera,center,left,right,driver,true });
            controller.enabled=true;
        }
        MainCamera PlayerCamera()
        {
            if(service!=null && service.Game!=null) {
                PlayerTransforms player=Active<PlayerTransforms>();
                if(player!=null) {
                    Transform head=(Transform)Field(player,"_headTransform");
                    MainCamera main=head==null?null:head.GetComponent<MainCamera>();
                    if(main!=null) return main;
                }
                throw new Exception("Gameplay head camera unavailable");
            }
            return Active<MainCamera>();
        }
        static bool NoOverlap(GameObject ui,GameContext game)
        {
            PauseMenuManager manager=(PauseMenuManager)Field(game,"pauseMenu");
            RectTransform rect=(RectTransform)ui.transform;
            Transform parent=rect.parent;
            Vector3[] corners=new Vector3[4]; rect.GetWorldCorners(corners);
            float top=corners.Max(x=>parent.InverseTransformPoint(x).y);
            foreach(string field in new[] { "_continueButton","_restartButton","_backButton" }) {
                RectTransform native=(RectTransform)((Button)Field(manager,field)).transform;
                native.GetWorldCorners(corners);
                float bottom=corners.Min(x=>parent.InverseTransformPoint(x).y);
                if(top>bottom-0.1f) return false;
            }
            return true;
        }
        void Finish()
        {
            if(done) return; done=true;
            passed.Add("render paths: "+String.Join(" | ",TrackingProbe.RenderPaths.ToArray())+" / menuFrames="+TrackingProbe.MenuFrames);
            try {
                if(captured && service!=null) {
                    RestoreConfiguration();
                    Invoke(Field(service,"session"),"NativeReset");
                    manager.settings=original; applicator.NotifyRoomTransformOffsetWasUpdated();
                }
            } catch(Exception error) { errors.Add("Restore failed: "+error); }
            errors.AddRange(TrackingProbe.StartupErrors);
            string json="{\"passed\":"+(errors.Count==0?"true":"false")+",\"checks\":["
                +String.Join(",",passed.Select(Quote).ToArray())+"],\"errors\":["+String.Join(",",errors.Select(Quote).ToArray())+"]}";
            File.WriteAllText(Path.Combine(directory,"report.json"),json,new UTF8Encoding(false));
            Application.Quit(errors.Count==0?0:1);
        }
        void RestoreConfiguration()
        {
            if(config==null) return;
            config.LeftButton=originalLeft; config.RightButton=originalRight;
            config.Keyboard=originalKeyboard; config.PreparationSeconds=originalDuration;
            config.AutoCalibrateOnLaunch=TrackingProbe.OriginalAuto;
            config.Changed();
        }
        static string Quote(string value)
        {
            return "\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";
        }
        void Update() { if(!done && Time.realtimeSinceStartup-started>180) { errors.Add("Runtime smoke deadline exceeded"); Finish(); } }
    }
}

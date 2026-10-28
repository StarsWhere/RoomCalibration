using System;
using System.Collections.Generic;
using System.Globalization;
using BeatSaber.Settings;
using HarmonyLib;
using SiraUtil.Tools.FPFC;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Zenject;

namespace RoomCalibration
{
    public sealed class CalibrationService : MonoBehaviour
    {
        public static CalibrationService Current { get; private set; }
        public bool SessionActive { get { return session.Active; } }
        public bool NeedsCalibration { get { return session.NeedsCalibration; } }
        public string LastResult { get; private set; }
        public string LastCategory { get; private set; }
        public GameContext Game { get; private set; }
        internal readonly SessionTransform session = Plugin.Session;
        readonly ButtonChord gesture = new ButtonChord();
        readonly RequestGate requests = new RequestGate();
        readonly StartupCalibrationGate startup = Plugin.Startup;
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        SettingsManager settings;
        SettingsApplicatorSO applicator;
        IVRPlatformHelper platform;
        IXRSystemState xrState;
        IFPFCSettings fpfc;
        GameScenesManager scenes;
        MenuContext menu;
        HealthWarningFlowCoordinator startupFlow;
        UnsupportedContext unsupported;
        Feedback feedback;
        int generation;
        int configRevision;
        string progressMessage;
        bool requireHold;
        bool started, stopped, ownUpdate, inputFailureReported;

        [Inject]
        public void Construct(SettingsManager manager, SettingsApplicatorSO settingsApplicator,
            IVRPlatformHelper helper, IXRSystemState state, GameScenesManager gameScenes,
            [InjectOptional] IFPFCSettings fpfcSettings)
        {
            settings = manager; applicator = settingsApplicator; platform = helper;
            xrState = state; scenes = gameScenes; fpfc = fpfcSettings;
            if (Current != null && Current != this) throw new InvalidOperationException("Duplicate calibration service");
            Current = this;
        }
        void Start()
        {
            settings.settings.room = session.Attach(settings.settings.room);
            feedback = new Feedback(transform);
            applicator.roomTransformOffsetDidUpdateEvent += RoomChanged;
            scenes.transitionDidStartEvent += TransitionStarted;
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
            InputDevices.deviceDisconnected += Disconnected;
            InputDevices.deviceConnected += Connected;
            xrState.AddListener(FocusChanged, XRSystemEventType.InputFocusLost, XRSystemEventType.VRFocusLost,
                XRSystemEventType.ControllersDidDisconnect);
            RefreshSubsystems();
            RenderPipelineManager.beginCameraRendering += BeforeCameraRendering;
            configRevision=CalibrationConfig.Revision;
            started = true;
            if (session.Active) Apply(settings.settings.room);
            Plugin.Log.Info("Calibration service initialized; auto calibration pending="+startup.Pending);
        }
        void Update()
        {
            if (!started || stopped) return;
            try {
                if(configRevision!=CalibrationConfig.Revision) {
                    configRevision=CalibrationConfig.Revision;
                    Invalidate("Calibration settings changed; start again");
                }
                bool focused = Focused;
                bool eligible = focused && ContextReady;
                bool left = false, right = false;
                InputDevice leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                InputDevice rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                bool haveButtons=ReadButton(leftDevice,Plugin.Config.LeftButton,out left)
                    && ReadButton(rightDevice,Plugin.Config.RightButton,out right);
                if(requests.Pending && requireHold && (!haveButtons || !left || !right))
                    Invalidate("Buttons released; calibration cancelled");
                if(gesture.Tick(left,right,eligible && haveButtons && !requests.Pending)) Begin(false,true);
                if(Plugin.Config.Keyboard!=KeyboardShortcut.Disabled
                    && Input.GetKeyDown((KeyCode)((int)KeyCode.F6+(int)Plugin.Config.Keyboard-1))) RequestCalibration();
                ProcessRequest(eligible);
                inputFailureReported = false;
            }
            catch (Exception error) {
                requests.Cancel(); gesture.Reset();
                if (!inputFailureReported) { Report("input", "Calibration failed; try again", error); inputFailureReported = true; }
            }
        }
        void LateUpdate() { if (feedback != null) feedback.Tick(Time.realtimeSinceStartup); }
        bool Focused { get { return xrState.hasInputFocus && xrState.hasVrFocus && xrState.hasHmdMounted; } }
        bool ContextReady
        {
            get {
                if (scenes.isInTransition || unsupported != null) return false;
                if (Game != null) return Game.Ready;
                return (menu != null && menu.isActiveAndEnabled)
                    || (startupFlow != null && startupFlow.isActivated && startupFlow.isActiveAndEnabled);
            }
        }
        internal void EnterMenu(MenuContext value) { menu = value; Invalidate(null); }
        internal void EnterStartup(HealthWarningFlowCoordinator value) { startupFlow = value; Invalidate(null); }
        internal void LeaveMenu(MenuContext value) { if (menu == value) { menu = null; Invalidate(null); } }
        internal void EnterGame(GameContext value) { Game = value; Invalidate(null); }
        internal void LeaveGame(GameContext value) { if (Game == value) { Game = null; Invalidate(null); } }
        internal void EnterUnsupported(UnsupportedContext value) { unsupported = value; Invalidate(null); }
        internal void LeaveUnsupported(UnsupportedContext value) { if (unsupported == value) { unsupported = null; Invalidate(null); } }
        public void RequestCalibration() { Begin(false,false); }
        public void RequestClear() { Begin(true,false); }
        static bool ReadButton(InputDevice device,ControllerButton choice,out bool pressed)
        {
            pressed=false;
            if(!device.isValid || choice==ControllerButton.Disabled) return false;
            switch(choice) {
                case ControllerButton.StickClick: return device.TryGetFeatureValue(CommonUsages.primary2DAxisClick,out pressed);
                case ControllerButton.Primary: return device.TryGetFeatureValue(CommonUsages.primaryButton,out pressed);
                case ControllerButton.Secondary: return device.TryGetFeatureValue(CommonUsages.secondaryButton,out pressed);
                case ControllerButton.Grip:
                case ControllerButton.Trigger:
                    float analog;
                    if(device.TryGetFeatureValue(choice==ControllerButton.Grip?CommonUsages.grip:CommonUsages.trigger,out analog)) {
                        pressed=analog>=0.8f; return true;
                    }
                    return device.TryGetFeatureValue(choice==ControllerButton.Grip?CommonUsages.gripButton:CommonUsages.triggerButton,out pressed);
                default: return false;
            }
        }
        void ShowCountdown(double seconds)
        {
            string message="Calibrating in: "+(Math.Ceiling(Math.Max(0,seconds)*10)/10).ToString("0.0",CultureInfo.InvariantCulture)+" seconds";
            if(progressMessage==message) return;
            progressMessage=message; LastCategory="preparing"; LastResult=message;
            if(feedback!=null) feedback.Show(message,Time.realtimeSinceStartup);
        }
        void HideCountdown()
        {
            progressMessage=null;
            if(feedback!=null) feedback.Hide();
        }
        void Begin(bool clear,bool hold)
        {
            if (stopped || !started) return;
            if (requests.Pending) return;
            if (fpfc != null && fpfc.Enabled) { Report("tracking", "Connect and wear the headset to calibrate", null); return; }
            if (!Focused) { Report("focus", "Wear the headset and return to the game to calibrate", null); return; }
            if (unsupported != null) { Report("unsupported", "This mode cannot pause for calibration; return to the menu", null); return; }
            if (!ContextReady) { Report("transition", "Wait for the scene to finish loading", null); return; }
            if (clear && !session.Active) { Report("empty", "No session calibration to clear", null); return; }
            float preparation=clear?0:InputSchema.Duration(Plugin.Config.PreparationSeconds);
            if (!requests.Begin(clear, generation, Time.realtimeSinceStartup,preparation)) return;
            requireHold=hold;
            if(!clear) ShowCountdown(preparation);
            try {
                if (Game != null && !Game.Paused) {
                    if (!Game.TryPause()) { requests.Cancel(); Report("pause-refused", "Cannot pause now; calibration was not applied", null); }
                }
            }
            catch (Exception error) { requests.Cancel(); Report("pause-failed", "Pause failed; calibration was not applied", error); }
        }
        void ProcessRequest(bool eligible)
        {
            bool clear = requests.Clear;
            bool safe = Game == null || Game.Paused;
            RequestAction action = requests.Tick(generation, eligible, safe, Time.realtimeSinceStartup);
            if (action == RequestAction.Cancel) { Report("cancelled", "Calibration cancelled; try again", null); return; }
            if (action == RequestAction.Timeout) { Report("pause-timeout", "Pause timed out; calibration was not applied", null); return; }
            if(action==RequestAction.Preparing) { ShowCountdown(requests.Remaining(Time.realtimeSinceStartup)); return; }
            if (action != RequestAction.Apply) return;
            if (clear) { ClearSession(); return; }
            Vector3 position; Quaternion rotation; string error;
            if (!TryHeadPose(out position, out rotation, out error)) { Report("tracking", error, null); return; }
            CalibratePose(position, rotation);
        }
        void BeforeCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!started || stopped || startup.MenuPresented || camera == null || Game != null
                || menu == null || !menu.isActiveAndEnabled || camera.GetComponentInParent<MainCamera>() == null) return;
            if (!startup.BeforeFirstMenuFrame()) return;
            try {
                Vector3 position; Quaternion rotation; string error;
                if (!Focused || !TryHeadPose(out position, out rotation, out error)) {
                    Plugin.Log.Warn("Launch calibration skipped: tracking unavailable before first menu frame; use manual calibration");
                    return;
                }
                if (CalibratePose(position, rotation, true))
                    Plugin.Log.Info("Launch calibration applied before first menu frame");
            }
            catch (Exception error) {
                Plugin.Log.Error("Launch calibration failed before first menu frame; manual calibration remains available: "+error);
            }
        }
        bool CalibratePose(Vector3 position, Quaternion rotation)
        {
            return CalibratePose(position, rotation, false);
        }
        bool CalibratePose(Vector3 position, Quaternion rotation, bool automatic)
        {
            RoomSettings target;
            string error;
            float targetYaw = Game == null ? 0f : Game.TargetYaw;
            if (!Geometry.TryCalibrate(settings.settings.room, position,
                new float4(rotation.x, rotation.y, rotation.z, rotation.w), targetYaw, out target, out error)) {
                if (automatic) Plugin.Log.Warn("Launch calibration skipped: "+error);
                else Report("pose", error, null);
                return false;
            }
            RoomSettings previous = settings.settings.room;
            SessionSnapshot snapshot = session.Snapshot;
            try {
                session.Commit(target, previous);
                Apply(target);
                startup.Complete();
                if (!automatic) {
                    Report("calibrated", "Calibrated to current position and direction", null);
                    Pulse();
                }
                return true;
            }
            catch (Exception exception) { Rollback(snapshot, previous, exception); }
            return false;
        }
        bool TryHeadPose(out Vector3 position, out Quaternion rotation, out string error)
        {
            position = Vector3.zero; rotation = Quaternion.identity;
            error = "Headset tracking is not ready; try again";
            if (fpfc != null && fpfc.Enabled) return false;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked; InputTrackingState tracking;
            if (!head.isValid || !head.TryGetFeatureValue(CommonUsages.isTracked, out tracked) || !tracked
                || !head.TryGetFeatureValue(CommonUsages.trackingState, out tracking)
                || (tracking & (InputTrackingState.Position | InputTrackingState.Rotation))
                    != (InputTrackingState.Position | InputTrackingState.Rotation)) return false;
            if (!platform.GetNodePose(XRNode.Head, 0, out position, out rotation)) return false;
            error = null;
            return true;
        }
        void Apply(RoomSettings room)
        {
            ownUpdate = true;
            try {
                settings.settings.room = room;
                // Some rigs are disabled or missed OnEnable's injection-time
                // subscription. Use the game's own setter for all current rigs.
                Patches.SynchronizeRoom(settings);
                applicator.NotifyRoomTransformOffsetWasUpdated();
            }
            finally { ownUpdate = false; }
        }
        void ClearSession()
        {
            RoomSettings previous = settings.settings.room;
            SessionSnapshot snapshot = session.Snapshot;
            try { Apply(session.Clear(previous)); Report("cleared", "Session calibration cleared", null); Pulse(); }
            catch (Exception error) { Rollback(snapshot, previous, error); }
        }
        void Rollback(SessionSnapshot snapshot, RoomSettings previous, Exception error)
        {
            session.Restore(snapshot);
            try { Apply(previous); }
            catch (Exception rollbackError) {
                Plugin.Log.Error("Calibration rollback notification failed: " + rollbackError);
                // A multicast event stops at the first throwing mod. Make sure
                // the remaining rig/avatar listeners also see the restored room.
                Action callbacks = (Action)AccessTools.Field(typeof(SettingsApplicatorSO), "roomTransformOffsetDidUpdateEvent").GetValue(applicator);
                if (callbacks != null) foreach (Action callback in callbacks.GetInvocationList()) {
                    try { callback(); }
                    catch (Exception listenerError) { Plugin.Log.Error("Room rollback listener " + callback.Method + " failed: " + listenerError); }
                }
            }
            Report("apply-failed", "Calibration update failed; previous offset restored", error);
        }
        internal RoomSettings PersistentRoom(RoomSettings room) { return stopped || !started ? room : session.PersistentRoom(room); }
        internal bool OwnsRoom(SettingsManager owner) { return System.Object.ReferenceEquals(settings,owner); }
        void RoomChanged()
        {
            if(!ownUpdate) { session.Observe(settings.settings.room); Patches.SynchronizeRoom(settings); }
        }
        internal void BeforeNativeReset() { session.NativeReset(); Invalidate("Room reset; calibration cancelled"); }
        void TransitionStarted(GameScenesManager.SceneTransitionType type, float duration) { Invalidate(null); }
        void SceneLoaded(Scene scene, LoadSceneMode mode) { Invalidate(null); RefreshSubsystems(); }
        void SceneUnloaded(Scene scene) { Invalidate(null); }
        void Disconnected(InputDevice device) { Invalidate("Device disconnected; calibration cancelled"); }
        void Connected(InputDevice device) { RefreshSubsystems(); }
        void FocusChanged(XRSystemEventType type) { Invalidate("Game exited; calibration cancelled"); }
        void Invalidate(string message)
        {
            bool pending = requests.Pending;
            generation++; requests.Cancel(); gesture.Reset();
            if(progressMessage!=null) HideCountdown();
            if (pending) Report("cancelled", message ?? "Scene changed; calibration cancelled", null);
        }
        void RefreshSubsystems()
        {
            foreach (XRInputSubsystem input in inputs) {
                input.trackingOriginUpdated -= OriginChanged; input.boundaryChanged -= OriginChanged;
            }
            inputs.Clear(); SubsystemManager.GetSubsystems(inputs);
            foreach (XRInputSubsystem input in inputs) {
                input.trackingOriginUpdated += OriginChanged; input.boundaryChanged += OriginChanged;
            }
        }
        void OriginChanged(XRInputSubsystem input)
        {
            Invalidate(null);
            session.OriginChanged();
            if (!session.Active) return;
            try { if (Game != null && !Game.Paused) Game.TryPause(); }
            catch (Exception error) { Plugin.Log.Error("Pause after tracking origin change failed: " + error); }
            Report("origin-changed", "Tracking origin changed; calibrate again", null);
        }
        void Pulse()
        {
            try {
                platform.TriggerHapticPulse(XRNode.LeftHand, 0.12f, 0.3f, 0.5f);
                platform.TriggerHapticPulse(XRNode.RightHand, 0.12f, 0.3f, 0.5f);
            }
            catch (Exception error) { Plugin.Log.Warn("Calibration applied; haptic feedback unavailable: " + error.Message); }
        }
        void Report(string category, string message, Exception error)
        {
            progressMessage=null;
            LastCategory = category; LastResult = message;
            if (error != null) Plugin.Log.Error("RoomCalibration [" + category + "]: " + error);
            else Plugin.Log.Info("RoomCalibration [" + category + "]: " + message);
            try { if (feedback != null) feedback.Show(message, Time.realtimeSinceStartup); }
            catch (Exception feedbackError) { Plugin.Log.Error("Calibration feedback failed: " + feedbackError); }
        }
        public void Shutdown()
        {
            if (stopped) return;
            stopped = true; requests.Cancel(); gesture.Reset();
            if (started) {
                applicator.roomTransformOffsetDidUpdateEvent -= RoomChanged;
                scenes.transitionDidStartEvent -= TransitionStarted;
                SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
                InputDevices.deviceDisconnected -= Disconnected;
                InputDevices.deviceConnected -= Connected;
                xrState.RemoveListener(FocusChanged);
                foreach (XRInputSubsystem input in inputs) {
                    input.trackingOriginUpdated -= OriginChanged; input.boundaryChanged -= OriginChanged;
                }
                inputs.Clear();
                RenderPipelineManager.beginCameraRendering -= BeforeCameraRendering;
                if (session.Active) {
                    try { Apply(session.PersistentRoom(settings.settings.room)); }
                    catch (Exception error) { Plugin.Log.Error("Restore room on shutdown failed: " + error); }
                }
            }
            if (feedback != null) { feedback.Dispose(); feedback = null; }
            if (Current == this) Current = null;
        }
        void OnDestroy() { Shutdown(); }
    }
}

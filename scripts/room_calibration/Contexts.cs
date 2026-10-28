using System;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Settings;
using HarmonyLib;
using LocalModUI;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace RoomCalibration
{
    public sealed class MenuContext : MonoBehaviour
    {
        CalibrationService service;
        BSMLSettings settingsMenu;
        SettingsPanel panel;
        IDisposable uiEntry;
        bool registered, quitting;
        [Inject]
        public void Construct(CalibrationService current, BSMLSettings modSettings)
        {
            service = current;
            settingsMenu=modSettings; panel=new SettingsPanel(Plugin.Config, service);
        }
        void Start()
        {
            bool flowAttempted = false;
            try {
                uiEntry = UiRegistry.Register(new UiEntry {
                    Id = "calibration", Title = "Room Calibration", Category = UiCategory.Devices, Order = 20,
                    ShortcutTitle = "Calibrate to Current Position", ShortcutAction = service.RequestCalibration,
                    Page = new SettingsPage {
                        Host = panel, Assembly = typeof(MenuContext).Assembly,
                        Resource = "RoomCalibration.Settings.bsml", Refresh = panel.Refresh,
                    },
                });
                registered = true;
                settingsMenu.AddSettingsMenu("Room Calibration","RoomCalibration.Settings.bsml",panel);
                flowAttempted = true;
                InstantSettingsFlow.Register(panel, error => Plugin.Log.Error("Calibration settings navigation failed: " + error));
                service.EnterMenu(this);
            }
            catch {
                try { service?.LeaveMenu(this); }
                catch (Exception error) { Plugin.Log.Error("Calibration menu state cleanup failed: " + error); }
                try { if (flowAttempted) InstantSettingsFlow.Unregister(panel); }
                catch (Exception error) { Plugin.Log.Error("Calibration navigation cleanup failed: " + error); }
                try { if (registered) settingsMenu.RemoveSettingsMenu(panel); registered = false; }
                catch (Exception error) { Plugin.Log.Error("Calibration menu cleanup failed: " + error); }
                try { uiEntry?.Dispose(); uiEntry = null; }
                catch (Exception error) { Plugin.Log.Error("Calibration registration cleanup failed: " + error); }
                throw;
            }
        }
        void OnDestroy()
        {
            try {
                if (registered && !quitting) settingsMenu.RemoveSettingsMenu(panel);
            }
            catch (Exception error) { Plugin.Log.Error("Calibration menu cleanup failed: " + error); }
            finally {
                uiEntry?.Dispose(); uiEntry = null;
                if (panel != null) InstantSettingsFlow.Unregister(panel);
                registered = false;
                if (service != null) service.LeaveMenu(this);
            }
        }
        void OnApplicationQuit() { quitting = true; }
    }

    public sealed class GameContext : MonoBehaviour
    {
        static readonly System.Reflection.MethodInfo canPause = AccessTools.Method(typeof(PauseController), "get_canPause");
        static readonly System.Reflection.MethodInfo canChangePauseState = AccessTools.Method(typeof(PauseController), "get_canChangePauseState");
        CalibrationService service;
        PauseController controller;
        IGamePause pause;
        PauseMenuManager pauseMenu;
        EnvironmentSpawnRotation spawnRotation;
        GameObject ui;
        public bool Ready { get { return isActiveAndEnabled && controller != null && pause != null && pauseMenu != null; } }
        public bool Paused { get { return pause != null && pause.isPaused; } }
        public float TargetYaw { get { return spawnRotation == null ? 0f : spawnRotation.targetRotation; } }
        [Inject]
        public void Construct(CalibrationService current, [InjectOptional] PauseController pauseController,
            [InjectOptional] IGamePause gamePause, [InjectOptional] PauseMenuManager menuManager,
            [InjectOptional] EnvironmentSpawnRotation environmentSpawnRotation)
        {
            service = current; controller = pauseController; pause = gamePause;
            pauseMenu = menuManager; spawnRotation = environmentSpawnRotation;
        }
        void Start()
        {
            service.EnterGame(this);
            if (controller != null) controller.didPauseEvent += ShowPauseButtons;
            try { CreatePauseButtons(); }
            catch (Exception error) { Plugin.Log.Error("Pause calibration buttons unavailable: " + error); }
        }
        public bool TryPause()
        {
            if (!Ready) return false;
            if (Paused) return true;
            if (!(bool)canPause.Invoke(controller, null) || !(bool)canChangePauseState.Invoke(controller, null)) return false;
            controller.Pause();
            return true;
        }
        void ShowPauseButtons()
        {
            if (ui != null) ui.SetActive(true);
        }
        void CreatePauseButtons()
        {
            if (pauseMenu == null) return;
            Button back = (Button)AccessTools.Field(typeof(PauseMenuManager), "_backButton").GetValue(pauseMenu);
            Button resume = (Button)AccessTools.Field(typeof(PauseMenuManager), "_continueButton").GetValue(pauseMenu);
            Button restart = (Button)AccessTools.Field(typeof(PauseMenuManager), "_restartButton").GetValue(pauseMenu);
            if (back == null || resume == null || restart == null) throw new MissingMemberException("Native pause buttons missing");
            RectTransform parent = back.transform.parent as RectTransform;
            if (parent == null) throw new InvalidOperationException("Native pause buttons have no UI parent");
            ui = new GameObject("RoomCalibration.PauseButtons", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            RectTransform rect = (RectTransform)ui.transform;
            rect.SetParent(parent, false);
            ui.GetComponent<LayoutElement>().ignoreLayout = true;
            HorizontalLayoutGroup row = ui.GetComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.MiddleCenter; row.spacing = 3;
            row.childControlHeight = row.childControlWidth = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            float bottom = Single.PositiveInfinity;
            Vector3[] corners = new Vector3[4];
            foreach (Button button in new[] { back, resume, restart }) {
                ((RectTransform)button.transform).GetWorldCorners(corners);
                foreach (Vector3 corner in corners) bottom = Mathf.Min(bottom, parent.InverseTransformPoint(corner).y);
            }
            rect.anchoredPosition = new Vector2(0, bottom - 7f);
            rect.sizeDelta = new Vector2(95, 8);
            CreateButton(ui.transform, "Calibrate to Current Position", 46, service.RequestCalibration);
            CreateButton(ui.transform, "Clear Session Calibration", 42, service.RequestClear);
            ui.SetActive(Paused);
        }
        void CreateButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction action)
        {
            GameObject host = new GameObject(label, typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            host.transform.SetParent(parent, false);
            LayoutElement size = host.GetComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = width; size.minHeight = size.preferredHeight = 8;
            BSMLParser.Instance.Parse("<button text=\"" + label + "\" pref-height=\"8\" />", host, this);
            Button button = host.GetComponentInChildren<Button>(true);
            if (button == null) throw new InvalidOperationException("BSML pause button missing");
            button.onClick.AddListener(action);
        }
        void Update()
        {
            if (ui != null && ui.activeSelf != Paused) ui.SetActive(Paused);
        }
        void OnDestroy()
        {
            if (controller != null) controller.didPauseEvent -= ShowPauseButtons;
            if (ui != null) { UnityEngine.Object.Destroy(ui); ui = null; }
            if (service != null) service.LeaveGame(this);
        }
    }

    public sealed class UnsupportedContext : MonoBehaviour
    {
        CalibrationService service;
        [Inject] public void Construct(CalibrationService current) { service = current; }
        void Start() { service.EnterUnsupported(this); }
        void OnDestroy() { if (service != null) service.LeaveUnsupported(this); }
    }
}

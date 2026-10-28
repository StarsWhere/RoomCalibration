using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using BeatSaberMarkupLanguage.Attributes;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;
using UnityEngine;

namespace RoomCalibration
{
    public class CalibrationConfig
    {
        ControllerButton left=ControllerButton.StickClick, right=ControllerButton.StickClick;
        KeyboardShortcut keyboard=KeyboardShortcut.F8;
        float duration=1.5f;
        internal static int Revision;
        public virtual bool AutoCalibrateOnLaunch { get; set; }
        [UseConverter(typeof(EnumConverter<ControllerButton>))]
        public virtual ControllerButton LeftButton { get { return left; } set { left=value; } }
        [UseConverter(typeof(EnumConverter<ControllerButton>))]
        public virtual ControllerButton RightButton { get { return right; } set { right=value; } }
        [UseConverter(typeof(EnumConverter<KeyboardShortcut>))]
        public virtual KeyboardShortcut Keyboard { get { return keyboard; } set { keyboard=value; } }
        public virtual float PreparationSeconds { get { return duration; } set { duration=InputSchema.Duration(value); } }
        public virtual void Changed() { Interlocked.Increment(ref Revision); }
        public virtual void OnReload()
        {
            if(!Enum.IsDefined(typeof(ControllerButton),LeftButton)) LeftButton=ControllerButton.StickClick;
            if(!Enum.IsDefined(typeof(ControllerButton),RightButton)) RightButton=ControllerButton.StickClick;
            if(!Enum.IsDefined(typeof(KeyboardShortcut),Keyboard)) Keyboard=KeyboardShortcut.F8;
            PreparationSeconds=InputSchema.Duration(PreparationSeconds);
            Changed();
        }
    }
    public sealed class SettingsPanel : INotifyPropertyChanged
    {
        readonly CalibrationConfig config;
        readonly CalibrationService service;
        [UIComponent("calibration-settings-content")] Transform content;
        public SettingsPanel(CalibrationConfig value, CalibrationService calibration) { config=value; service=calibration; }
        public event PropertyChangedEventHandler PropertyChanged;
        [UIAction("#post-parse")]
        void Parsed()
        {
            if (content != null) content.gameObject.AddComponent<SettingsView>().Initialize(this);
            Refresh();
        }
        public void Refresh()
        {
            foreach (string name in new[] { "AutoCalibrateOnLaunch", "LeftButton", "RightButton", "Keyboard", "PreparationSeconds" })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        public void Calibrate() { service.RequestCalibration(); }
        public void Clear() { service.RequestClear(); }
        public bool AutoCalibrateOnLaunch
        {
            get { return config.AutoCalibrateOnLaunch; }
            set { config.AutoCalibrateOnLaunch=value; config.Changed(); }
        }
        public List<object> ButtonChoices { get { return new List<object> { "Stick Click", "Grip", "Trigger", "Primary (A/X)", "Secondary (B/Y)", "Disabled" }; } }
        public List<object> KeyboardChoices { get { return new List<object> { "Disabled", "F6", "F7", "F8", "F9", "F10", "F11", "F12" }; } }
        public string LeftButton
        {
            get { return (string)ButtonChoices[(int)config.LeftButton]; }
            set { int index=ButtonChoices.IndexOf(value); if(index>=0) { config.LeftButton=(ControllerButton)index; config.Changed(); } }
        }
        public string RightButton
        {
            get { return (string)ButtonChoices[(int)config.RightButton]; }
            set { int index=ButtonChoices.IndexOf(value); if(index>=0) { config.RightButton=(ControllerButton)index; config.Changed(); } }
        }
        public string Keyboard
        {
            get { return (string)KeyboardChoices[(int)config.Keyboard]; }
            set { int index=KeyboardChoices.IndexOf(value); if(index>=0) { config.Keyboard=(KeyboardShortcut)index; config.Changed(); } }
        }
        public float PreparationSeconds
        {
            get { return config.PreparationSeconds; }
            set { config.PreparationSeconds=InputSchema.Duration(value); config.Changed(); }
        }
        public string FormatSeconds(float value) { return value.ToString("0.##",CultureInfo.InvariantCulture)+" seconds"; }
    }
    public sealed class SettingsView : MonoBehaviour
    {
        SettingsPanel panel;
        internal void Initialize(SettingsPanel value) { panel = value; if (isActiveAndEnabled) panel.Refresh(); }
        void OnEnable() { panel?.Refresh(); }
        void OnDestroy() { panel = null; }
    }
}

using System;
using BeatSaberMarkupLanguage;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RoomCalibration
{
    internal sealed class Feedback : IDisposable
    {
        readonly Transform owner;
        GameObject root;
        TMP_Text text;
        Transform head;
        float hideAt;
        internal Feedback(Transform parent) { owner = parent; }
        internal void Show(string message, float now)
        {
            MainCamera[] cameras = Resources.FindObjectsOfTypeAll<MainCamera>();
            head = null;
            foreach (MainCamera camera in cameras) {
                if (camera != null && camera.gameObject.activeInHierarchy && camera.camera != null && camera.camera.enabled) {
                    head = camera.transform; break;
                }
            }
            if (head == null || BeatSaberUI.MainTextFont == null) return;
            if (root == null) {
                root = new GameObject("RoomCalibration.Feedback", typeof(RectTransform), typeof(Canvas));
                root.transform.SetParent(owner, false);
                RectTransform rect = (RectTransform)root.transform;
                rect.sizeDelta = new Vector2(1000, 180); rect.localScale = Vector3.one * 0.001f;
                Canvas canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2
                    | AdditionalCanvasShaderChannels.TexCoord3 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
                canvas.sortingOrder = 1000;
                GameObject plate = new GameObject("Background", typeof(RectTransform), typeof(ImageView));
                plate.transform.SetParent(root.transform, false);
                RectTransform plateRect = (RectTransform)plate.transform;
                plateRect.anchorMin = Vector2.zero; plateRect.anchorMax = Vector2.one;
                plateRect.offsetMin = plateRect.offsetMax = Vector2.zero;
                ImageView background = plate.GetComponent<ImageView>();
                background.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
                background.sprite = BeatSaberMarkupLanguage.Utilities.ImageResources.BlankSprite;
                background.color = new Color(0, 0, 0, 0.85f); background.raycastTarget = false;
                GameObject label = new GameObject("Status", typeof(RectTransform), typeof(CurvedTextMeshPro));
                label.transform.SetParent(root.transform, false);
                RectTransform labelRect = (RectTransform)label.transform;
                labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                text = label.GetComponent<CurvedTextMeshPro>();
                text.font = BeatSaberUI.MainTextFont;
                text.fontSharedMaterial = BeatSaberUI.MainFlatUIFontMaterial ?? text.font.material;
                text.fontSize = 55; text.alignment = TextAlignmentOptions.Center;
                text.raycastTarget = false; text.color = Color.white;
            }
            text.text = message;
            hideAt = now + 3f;
            root.SetActive(true);
            Tick(now);
        }
        internal void Tick(float now)
        {
            if (root == null || !root.activeSelf) return;
            if (now >= hideAt || head == null || !head.gameObject.activeInHierarchy) { root.SetActive(false); return; }
            root.transform.SetPositionAndRotation(head.position + head.forward * 1.35f + head.up * 0.2f, head.rotation);
        }
        internal void Hide() { if(root!=null) root.SetActive(false); }
        public void Dispose()
        {
            if(root!=null) { UnityEngine.Object.Destroy(root); root=null; }
        }
    }
}

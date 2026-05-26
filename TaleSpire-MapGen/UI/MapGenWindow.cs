using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using TaleSpireMapGen.Generation;
using UnityEngine;

namespace TaleSpireMapGen.UI
{
    public class MapGenWindow : MonoBehaviour
    {
        // ──────────────────────────────────────────────────────────────────────
        // State
        // ──────────────────────────────────────────────────────────────────────

        private bool _visible = false;
        private Rect _windowRect = new Rect(20, 20, 440, 540);

        private readonly List<ITemplate> _templates = new List<ITemplate>
        {
            new DungeonTemplate(),
            new AiTemplate(),
        };

        private int    _selectedTemplate = 0;
        private string _aiJson           = "";
        private string _seedText         = "0";
        private int    _themeIndex       = 0;
        private string _statusLine       = "";
        private string _detailsLine      = "";
        private bool   _lastSuccess      = false;

        private Vector2 _aiScroll = Vector2.zero;

        // Styles — created lazily after the skin is available
        private GUIStyle _labelStyle;
        private GUIStyle _dimStyle;

        // ──────────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────────

        public void Toggle() => _visible = !_visible;

        // ──────────────────────────────────────────────────────────────────────
        // IMGUI
        // ──────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (!_visible) return;

            EnsureStyles();

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 0.97f);

            _windowRect = GUILayout.Window(
                GUIUtility.GetControlID(FocusType.Passive),
                _windowRect,
                DrawWindow,
                "MapGen");

            // Prevent the window from growing wider than its initial size
            // (e.g. if the user pastes long text into a field)
            if (_windowRect.width > 440) _windowRect.width = 440;

            GUI.backgroundColor = prevBg;
        }

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);

            _dimStyle = new GUIStyle(GUI.skin.label);
            _dimStyle.normal.textColor = new Color(0.60f, 0.60f, 0.60f);
        }

        private void DrawWindow(int id)
        {
            // ── Close button ─────────────────────────────────────────────────
            if (GUI.Button(new Rect(_windowRect.width - 26, 2, 22, 18), "X"))
            {
                _visible = false;
                return;
            }

            // ── Template selector ────────────────────────────────────────────
            GUILayout.Label("Template:", _labelStyle);
            for (int i = 0; i < _templates.Count; i++)
            {
                bool selected = GUILayout.Toggle(i == _selectedTemplate, _templates[i].Name);
                if (selected && i != _selectedTemplate)
                {
                    _selectedTemplate = i;
                    _statusLine = "";
                    _detailsLine = "";
                }
            }

            if (_templates[_selectedTemplate].Description is string desc)
                GUILayout.Label(desc, _dimStyle);

            GUILayout.Space(6);

            // ── AI JSON input (only for AiTemplate) ─────────────────────────
            if (_templates[_selectedTemplate] is AiTemplate)
            {
                GUILayout.Label("Paste AI JSON here:", _labelStyle);
                _aiScroll = GUILayout.BeginScrollView(_aiScroll,
                    GUILayout.MinHeight(140), GUILayout.MaxWidth(428));
                _aiJson = GUILayout.TextArea(_aiJson, GUILayout.ExpandHeight(true));
                GUILayout.EndScrollView();
                GUILayout.Space(4);
            }

            // ── Seed ─────────────────────────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed:", _labelStyle, GUILayout.Width(44));
            _seedText = GUILayout.TextField(_seedText, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            // ── Theme dropdown ───────────────────────────────────────────────
            GUILayout.Space(4);
            GUILayout.Label("Theme:", _labelStyle);
            string[] themes = TileCatalog.KnownThemes;
            _themeIndex = Mathf.Clamp(_themeIndex, 0, themes.Length - 1);
            _themeIndex = GUILayout.SelectionGrid(_themeIndex, themes, 1);

            GUILayout.Space(8);

            // ── Generate button ──────────────────────────────────────────────
            if (GUILayout.Button("Generate & Copy Slab"))
                OnGenerate();

            // ── Status ───────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(_statusLine))
            {
                GUILayout.Space(6);
                var prevColor = GUI.color;
                GUI.color = _lastSuccess
                    ? new Color(0.35f, 1.0f, 0.35f)
                    : new Color(1.0f, 0.40f, 0.40f);
                GUILayout.Label(_statusLine);
                GUI.color = prevColor;
            }

            if (!string.IsNullOrEmpty(_detailsLine))
                GUILayout.Label(_detailsLine, _dimStyle);

            GUI.DragWindow();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Generation
        // ──────────────────────────────────────────────────────────────────────

        private void OnGenerate()
        {
            if (!int.TryParse(_seedText, out int seed)) seed = 0;

            string[] themes = TileCatalog.KnownThemes;
            string theme = (_themeIndex >= 0 && _themeIndex < themes.Length)
                ? themes[_themeIndex]
                : "Dungeon Cellar";

            var parameters = new TemplateParams
            {
                Seed   = seed,
                Theme  = theme,
                AiJson = _templates[_selectedTemplate] is AiTemplate ? _aiJson : null,
            };

            try
            {
                LayoutSpec spec = _templates[_selectedTemplate].Generate(parameters);

                string specJson = JsonConvert.SerializeObject(spec, Formatting.Indented);
                Debug.Log($"[MapGen] LayoutSpec:\n{specJson}");

                var placements = SlabBuilder.Build(spec);
                string slab = SlabEncoder.Encode(placements);
                SlabEncoder.CopyToClipboard(slab);

                int roomCount = spec.Rooms?.Count ?? 0;
                int connCount = spec.Connections?.Count ?? 0;
                int tileCount = placements.Count;

                _lastSuccess = true;
                _statusLine  = "Copied to clipboard!";
                _detailsLine = $"{roomCount} rooms, {connCount / 2} corridors, {tileCount} tiles";

                Debug.Log($"[MapGen] Slab ({tileCount} tiles): {slab.Substring(0, Math.Min(40, slab.Length))}...");
            }
            catch (Exception ex)
            {
                _lastSuccess = false;
                _statusLine  = $"Error: {ex.Message}";
                _detailsLine = "";
                Debug.LogError($"[MapGen] Generation failed: {ex}");
            }
        }
    }
}

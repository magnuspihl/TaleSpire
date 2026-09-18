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
        private Rect _windowRect = new Rect(20, 20, 440, 600);

        private readonly List<ITemplate> _templates = new List<ITemplate>
        {
            new DungeonTemplate(),
            new AiTemplate(),
        };

        private int    _selectedTemplate = 0;
        private string _aiJson           = "";
        private string _seedText         = "0";
        private int    _themeIndex       = 0;
        private int    _upperThemeIndex  = -1; // -1 = same as ground
        private int    _themeTab         = 0;  // 0=ground, 1=upper
        private int    _dungeonSize      = 1;  // 0=Small 1=Medium 2=Large
        private int    _minFloors        = 1;
        private int    _maxFloors        = 2;
        private int    _clutterDensity   = 0;
        private string _statusLine       = "";
        private string _detailsLine      = "";
        private string _warnLine         = "";
        private bool   _lastSuccess      = false;

        // How many rooms of each purpose the user has asked for. Keyed by purpose name and kept
        // across theme changes, so switching from a medieval set to a modern one and back does not
        // silently drop what was asked for — only the current style's purposes are ever sent.
        private readonly Dictionary<string, int> _quota = new Dictionary<string, int>();

        private Vector2 _aiScroll    = Vector2.zero;
        private Vector2 _themeScroll = Vector2.zero;
        private Vector2 _quotaScroll = Vector2.zero;

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

            // ── Dungeon settings (only for DungeonTemplate) ──────────────────
            if (_templates[_selectedTemplate] is DungeonTemplate)
            {
                GUILayout.Space(6);
                GUILayout.Label("Size:", _labelStyle);
                _dungeonSize = GUILayout.SelectionGrid(_dungeonSize,
                    new[] { "Small", "Medium", "Large" }, 3);

                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Min Floors:", _labelStyle, GUILayout.Width(76));
                _minFloors = IntSpin(_minFloors, 1, _maxFloors);
                GUILayout.Space(14);
                GUILayout.Label("Max Floors:", _labelStyle, GUILayout.Width(76));
                _maxFloors = IntSpin(_maxFloors, _minFloors, 2);
                GUILayout.EndHorizontal();

                GUILayout.Space(4);
                GUILayout.Label("Clutter:", _labelStyle);
                _clutterDensity = GUILayout.SelectionGrid(_clutterDensity,
                    new[] { "None", "Sparse", "Normal", "Dense" }, 4);

                // Only with clutter on: a quota decides what rooms are *for*, and what a room is
                // for is only visible through the furniture that gets put in it.
                if (_clutterDensity > 0) DrawQuota();
            }

            // ── Theme list ───────────────────────────────────────────────────
            // Scrolled rather than laid out in full: the catalog grew from 3 themes to 20, and a
            // column of 20 buttons is taller than the window. Ground and upper storeys share the
            // one list through a tab, because two stacked lists would not fit either.
            GUILayout.Space(4);
            string[] themes = TileCatalog.KnownThemes;

            bool twoStorey = _templates[_selectedTemplate] is DungeonTemplate && _maxFloors >= 2;
            if (twoStorey)
                _themeTab = GUILayout.SelectionGrid(_themeTab,
                    new[] { "Ground theme", "Upper theme" }, 2);
            else
                _themeTab = 0;

            if (twoStorey && _themeTab == 1)
            {
                // "Same as ground" is prepended, so the upper list is offset by one from `themes`.
                var choices = new string[themes.Length + 1];
                choices[0] = "— same as ground —";
                themes.CopyTo(choices, 1);

                _themeScroll = GUILayout.BeginScrollView(_themeScroll,
                    GUILayout.Height(150), GUILayout.MaxWidth(428));
                int sel = GUILayout.SelectionGrid(
                    Mathf.Clamp(_upperThemeIndex + 1, 0, choices.Length - 1), choices, 2);
                GUILayout.EndScrollView();
                _upperThemeIndex = sel - 1;
            }
            else
            {
                _themeIndex = Mathf.Clamp(_themeIndex, 0, themes.Length - 1);
                _themeScroll = GUILayout.BeginScrollView(_themeScroll,
                    GUILayout.Height(150), GUILayout.MaxWidth(428));
                _themeIndex = GUILayout.SelectionGrid(_themeIndex, themes, 2);
                GUILayout.EndScrollView();
            }

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

            if (!string.IsNullOrEmpty(_warnLine))
            {
                var prevColor = GUI.color;
                GUI.color = new Color(1.0f, 0.80f, 0.35f);
                GUILayout.Label(_warnLine);
                GUI.color = prevColor;
            }

            GUI.DragWindow();
        }

        // ── Purpose quota ────────────────────────────────────────────────────
        // The purposes offered are the ones the *ground* theme's style defines, because the style
        // is a property of the prop vocabulary rather than of a storey: a modern map has no
        // Treasury to ask for, and offering one would produce a shortfall the user cannot act on.
        private void DrawQuota()
        {
            var purposes = RequestablePurposes();
            if (purposes.Count == 0) return;

            GUILayout.Space(4);
            GUILayout.Label("Rooms of a given purpose (0 = let the generator decide):", _dimStyle);

            _quotaScroll = GUILayout.BeginScrollView(_quotaScroll,
                GUILayout.Height(104), GUILayout.MaxWidth(428));
            for (int i = 0; i < purposes.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 2 && j < purposes.Count; j++)
                {
                    string name = purposes[j].Name;
                    GUILayout.Label(name, _labelStyle, GUILayout.Width(106));
                    _quota[name] = IntSpin(_quota.TryGetValue(name, out int n) ? n : 0, 0, 9);
                    GUILayout.Space(10);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        // Empty is a real recipe, but it is the bucket a room lands in when nothing else fits, and
        // any room suits it — so a quota of it is met trivially and only takes rooms away from the
        // purposes that were asked for. Offering it invites a knob that cannot report a shortfall.
        private List<PropRecipe> RequestablePurposes()
        {
            var result = new List<PropRecipe>();
            foreach (var recipe in RecipeCatalog.Purposes(RecipeCatalog.StyleFor(CurrentTheme())))
                if (recipe.Name != RecipeCatalog.Empty) result.Add(recipe);
            return result;
        }

        private string CurrentTheme()
        {
            string[] themes = TileCatalog.KnownThemes;
            return _themeIndex >= 0 && _themeIndex < themes.Length
                ? themes[_themeIndex]
                : "Dungeon Cellar";
        }

        /// <summary>The non-zero quota entries the current style actually defines, or null.</summary>
        private Dictionary<string, int> QuotaForGeneration()
        {
            var result = new Dictionary<string, int>();
            foreach (var recipe in RequestablePurposes())
                if (_quota.TryGetValue(recipe.Name, out int n) && n > 0)
                    result[recipe.Name] = n;
            return result.Count > 0 ? result : null;
        }

        // ─ [–] N [+] spinner ─────────────────────────────────────────────────
        private int IntSpin(int value, int min, int max)
        {
            if (GUILayout.Button("–", GUILayout.Width(20)) && value > min) value--;
            GUILayout.Label(value.ToString(), _labelStyle, GUILayout.Width(16));
            if (GUILayout.Button("+", GUILayout.Width(20)) && value < max) value++;
            return value;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Generation
        // ──────────────────────────────────────────────────────────────────────

        private void OnGenerate()
        {
            if (!int.TryParse(_seedText, out int seed)) seed = 0;

            string[] themes = TileCatalog.KnownThemes;
            string theme = CurrentTheme();

            string upperTheme = (_upperThemeIndex >= 0 && _upperThemeIndex < themes.Length)
                ? themes[_upperThemeIndex]
                : null;

            var parameters = new TemplateParams
            {
                Seed        = seed,
                Theme       = theme,
                UpperTheme  = upperTheme,
                AiJson      = _templates[_selectedTemplate] is AiTemplate ? _aiJson : null,
                DungeonSize = _dungeonSize,
                MinFloors   = _minFloors,
                MaxFloors   = _maxFloors,
                ClutterDensity = _clutterDensity,
                PurposeQuota   = _clutterDensity > 0 ? QuotaForGeneration() : null,
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

                // Props ride the same list as tiles, so the split has to be recovered from the
                // GUID — the same way TaleSpire itself decides what a placement is.
                int propCount = 0;
                foreach (var p in placements)
                    if (PropCatalog.TryGet(new Guid(p.guid).ToString(), out _)) propCount++;
                int tileCount = placements.Count - propCount;

                var yLevels = new System.Collections.Generic.HashSet<float>();
                foreach (var r in spec.Rooms ?? new System.Collections.Generic.List<RoomSpec>())
                    yLevels.Add(r.OriginY);
                string levelText = yLevels.Count > 1 ? $", {yLevels.Count} levels" : "";

                _lastSuccess = true;
                _statusLine  = "Copied to clipboard!";
                string propText = propCount > 0 ? $", {propCount} props" : "";
                _detailsLine = $"{roomCount} rooms, {connCount / 2} corridors, {tileCount} tiles{propText}{levelText}";

                // A quota is exact, so asking for more rooms of a purpose than the map has rooms
                // that suit it is a normal outcome and has to be said out loud — otherwise the only
                // evidence is furniture that is not there.
                _warnLine = "";
                if (spec.PurposeShortfall != null && spec.PurposeShortfall.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in spec.PurposeShortfall) parts.Add($"{kv.Value} {kv.Key}");
                    _warnLine = $"No room suited: {string.Join(", ", parts)}";
                }

                Debug.Log($"[MapGen] Slab ({tileCount} tiles): {slab.Substring(0, Math.Min(40, slab.Length))}...");
            }
            catch (Exception ex)
            {
                _lastSuccess = false;
                _statusLine  = $"Error: {ex.Message}";
                _detailsLine = "";
                _warnLine    = "";
                Debug.LogError($"[MapGen] Generation failed: {ex}");
            }
        }
    }
}

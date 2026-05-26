using BepInEx;
using BepInEx.Configuration;
using TaleSpireMapGen.Generation;
using TaleSpireMapGen.UI;
using UnityEngine;

namespace TaleSpireMapGen
{
    [BepInPlugin("org.talespire.plugins.mapgen", "TaleSpire MapGen", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyboardShortcut> _toggleShortcut;
        private MapGenWindow _window;

        private void Awake()
        {
            _toggleShortcut = Config.Bind(
                "Hotkeys",
                "ToggleWindow",
                new KeyboardShortcut(KeyCode.F8),
                "Toggle the MapGen window"
            );

            _window = gameObject.AddComponent<MapGenWindow>();
            SlabBuilder.DebugLog = msg => Logger.LogInfo(msg);
            Logger.LogInfo("TaleSpire MapGen loaded. Press F8 to open.");
        }

        private void Update()
        {
            if (_toggleShortcut.Value.IsDown())
                _window.Toggle();
        }
    }
}

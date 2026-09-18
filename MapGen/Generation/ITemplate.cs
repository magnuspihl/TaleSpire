using System.Collections.Generic;

namespace TaleSpireMapGen.Generation
{
    public interface ITemplate
    {
        string Name { get; }
        string Description { get; }
        LayoutSpec Generate(TemplateParams parameters);
    }

    public class TemplateParams
    {
        public int Seed;
        public string Theme;      // tile catalog folder name
        public string UpperTheme; // theme for upper storeys; null/empty = same as Theme
        public string AiJson;     // non-null only in AI mode
        public int DungeonSize;   // 0=Small, 1=Medium, 2=Large
        public int MinFloors;     // 1–2
        public int MaxFloors;     // 1–2
        public int ClutterDensity; // 0=bare, 1=sparse, 2=normal, 3=dense
        // How many rooms of a given purpose the map must have, e.g. {"Treasury": 1, "Cell": 2}.
        // Purposes not named here are drawn by weight to fill what the quota leaves over, so an
        // empty quota still produces a mix rather than nothing.
        public Dictionary<string, int> PurposeQuota;
    }
}

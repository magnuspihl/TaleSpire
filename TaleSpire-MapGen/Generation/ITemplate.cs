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
        public string Theme;    // tile catalog folder name
        public string AiJson;   // non-null only in AI mode
        public int DungeonSize; // 0=Small, 1=Medium, 2=Large
        public int MinFloors;   // 1–2
        public int MaxFloors;   // 1–2
    }
}

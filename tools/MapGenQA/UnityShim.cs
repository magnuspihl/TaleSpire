// Stand-ins for the only two Unity surfaces the generator touches, so MapGen's own sources
// compile into this console harness unmodified. Internal so they can never collide with the
// real UnityEngine types when MapGen is built for the game.

namespace UnityEngine
{
    internal static class Debug
    {
        public static void LogWarning(object message) =>
            System.Console.Error.WriteLine($"[ProfileCatalog warning] {message}");

        public static void Log(object message) =>
            System.Console.Error.WriteLine($"[log] {message}");

        public static void LogError(object message) =>
            System.Console.Error.WriteLine($"[error] {message}");
    }

    internal static class GUIUtility
    {
        public static string systemCopyBuffer { get; set; }
    }
}

using System.Reflection;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// A field-by-field copy of one of the game's objects that has no copy of its own (object.MemberwiseClone): every field it
    /// has now, and any a game update adds, so the copy stays the game's own thing with only what the caller then changes.
    /// References are shared, not copied: replace a list or other object the copy must not share.
    /// </summary>
    internal static class ShallowCopy
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>A new object of <paramref name="source"/>'s own type with its fields' values.</summary>
        public static T Of<T>(T source) where T : class => (T)Clone.Invoke(source, null);
    }
}

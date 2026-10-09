using HarmonyLib;
using VRage.Generics;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The renderer's one-frame billboards (MyBillboardRenderer.m_billboardsOncePool): light glares are added to it
    /// while the scene is drawn, and it is cleared once per frame, at frame start. Drawn a second time in the frame,
    /// the second eye would also draw the first eye's glares, twice as bright and placed for the other eye. Rewinding
    /// the pool to where it stood before the first eye keeps what was added before the scene was drawn.
    /// </summary>
    internal static class OnceBillboards
    {
        private static readonly MyObjectsPoolSimple<MyBillboard> Pool = (MyObjectsPoolSimple<MyBillboard>)AccessTools.Field(
            typeof(MyDX11Render).Assembly.GetType("VRageRender.MyBillboardRenderer", throwOnError: true), "m_billboardsOncePool").GetValue(null);

        private static readonly AccessTools.FieldRef<MyObjectsPoolSimple<MyBillboard>, int> NextAllocateIndex =
            AccessTools.FieldRefAccess<MyObjectsPoolSimple<MyBillboard>, int>("m_nextAllocateIndex");

        /// <summary>Debug: let the eyes share them, as before (<see cref="RenderDebug"/> SharedBillboards=1).</summary>
        public static bool Shared { get; set; }

        /// <summary>Where the pool stands now, to <see cref="Rewind"/> to.</summary>
        public static int Mark() => NextAllocateIndex(Pool);

        /// <summary>Drops what was added since <paramref name="mark"/>.</summary>
        public static void Rewind(int mark)
        {
            if (!Shared && NextAllocateIndex(Pool) > mark)
                NextAllocateIndex(Pool) = mark;
        }
    }
}

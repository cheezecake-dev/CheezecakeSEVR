// PictureMaterial: our own transparent materials, each showing one picture given to the renderer by name.
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A transparent material of our own for billboards that show one picture as it is: the inside of the helmet
    /// (<see cref="HelmetInterior"/>).
    /// </summary>
    internal static class PictureMaterial
    {
        /// <summary>
        /// At start-up, on the game thread with no world drawn, as the game registers its own: <paramref name="id"/> in the game's
        /// list of transparent materials (MyTransparentMaterials), which both threads read while a world is drawn, showing the
        /// generated texture <paramref name="texture"/> (MyRenderProxy.CreateGeneratedTexture): unlit, no atlas, both faces, and
        /// solid until it is within <paramref name="softMetres"/> of the scene behind it, where the renderer fades it.
        /// </summary>
        public static void Add(MyStringId id, string texture, float softMetres)
        {
            MyTransparentMaterials.AddMaterial(new MyTransparentMaterial(id, MyTransparentMaterialTextureType.FileTexture,
                texture, null, softParticleDistanceScale: softMetres, canBeAffectedByOtherLights: false, alphaMistingEnable: false,
                color: Vector4.One, colorAdd: Vector4.Zero, shadowMultiplier: Vector4.Zero, lightMultiplier: Vector4.One,
                isFlareOccluder: false, triangleFaceCulling: false));
        }
    }
}

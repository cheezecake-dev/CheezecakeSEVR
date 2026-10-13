// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitPanelGuard.cs; SurfaceView and SurfaceKey from Player/PhysicalSurface.cs), with
// his permission.
// His SurfaceView is the target of his whole spatial UI; the dashboard switches use only its pose, size, keys and touch state,
// so that is all it keeps here. A switch is his ModelControl style, whose key face sits on the surface (his KeyHeight 0).
using System.Collections.Generic;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    /// <summary>His SurfaceKey (Player/PhysicalSurface.cs), cut to what the dashboard switches use: a key's label and bounds (0-1 across and down the surface).</summary>
    internal sealed class SurfaceKey
    {
        internal readonly string Label;
        internal readonly RectangleF Bounds;
        internal bool Enabled=true,Round;
        internal SurfaceKey(string label,float x,float y,float width,float height) { Label=label; Bounds=new RectangleF(x,y,width,height); }
    }

    /// <summary>His SurfaceView, cut to a touch target: where it is (world), how big, its keys, and what the hands are doing to it.</summary>
    internal sealed class SurfaceView
    {
        public string Id;
        public MatrixD Pose;
        public float Width,Height;
        public SurfaceKey[] Keys=new SurfaceKey[0];
        public bool Enabled=true,GeometryFeedback;
        public int Hover=-1,Pressed=-1;
    }

    internal static class CockpitPanelGuard
    {
        internal const float Margin=.005f;
        internal static BoundingBox KeyBounds(SurfaceView surface,SurfaceKey key,float margin=0)
        {
            var b=key.Bounds;
            const float z=0;
            return new BoundingBox(new Vector3((b.X-.5f)*surface.Width-margin,(.5f-b.Y-b.Height)*surface.Height-margin,z-margin),
                new Vector3((b.X+b.Width-.5f)*surface.Width+margin,(.5f-b.Y)*surface.Height+margin,z+margin));
        }
        internal static bool NearSurface(SurfaceView surface,CockpitProbe probe) => surface.Enabled &&
            CockpitTouch.NearKey(surface,probe.Transform(MatrixD.Invert(surface.Pose)),out _,out _,margin:Margin)>=0;
        internal static bool NearPlane(SurfaceView surface,CockpitProbe probe) => probe.Transform(MatrixD.Invert(surface.Pose)).Intersects(
            new BoundingBox(new Vector3(-surface.Width/2-Margin,-surface.Height/2-Margin,-Margin),
                new Vector3(surface.Width/2+Margin,surface.Height/2+Margin,Margin)),.001f);
        internal struct Region
        {
            internal Matrix Frame;
            internal BoundingBox Bounds;
            internal Region(Matrix frame,BoundingBox bounds) { Frame=frame; Bounds=bounds; }
        }
        // Inspection fixture: all switch covers open, moving grips at the supplied value.
        internal static Region[] Regions(CockpitRig rig,float value=0)
        {
            var regions=new List<Region>();
            for(int slot=0;slot<rig.Count;slot++)
            {
                var surface=CockpitButtons.Preview(rig.Subtype,slot);
                var handle=rig.HandleAt(slot); var bar=rig.BarAt(slot); var lever=rig.LeverAt(slot); var button=rig.ButtonAt(slot);
                surface.Pose*=handle?.Visual(value) ?? bar?.Visual(value) ?? lever?.Visual(value) ?? button?.Visual(value==1) ?? Matrix.Identity;
                var bounds=handle!=null && !handle.Pinch ? new BoundingBox(new Vector3(-handle.HalfWidth,-handle.Radius,-2*handle.Radius)-new Vector3(Margin),
                    new Vector3(handle.HalfWidth,handle.Radius,0)+new Vector3(Margin)):KeyBounds(surface,surface.Keys[0],Margin);
                regions.Add(new Region((Matrix)surface.Pose,bounds));
                if(lever?.CoverActor>=0)
                {
                    var cover=new SurfaceView {Width=.019f,Height=.035f};
                    regions.Add(new Region((Matrix)lever.CoverPose(1),KeyBounds(cover,new SurfaceKey("",0,0,1,1),Margin)));
                }
            }
            return regions.ToArray();
        }
    }
}

// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitProbe.cs, Player/Shared/SegmentBox.cs, Player/SegmentDisk.cs),
// with his permission.
using System;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal struct CockpitProbe
    {
        internal const float Radius=.011f,Length=.028f,TipExtension=.003f;
        internal const float CenterSide=.0025f,CenterUp=-.0005f;
        internal Vector3D Tip,Start,End;
        internal CockpitProbe(MatrixD pointer,bool left=false)
        {
            Tip=pointer.Translation;
            // The index bone axis is offset from the glove mesh center, mirrored between hands.
            var center=Tip+pointer.Right*(left ? CenterSide:-CenterSide)+pointer.Up*CenterUp;
            Start=center+pointer.Forward*(TipExtension-Radius);
            End=Start-pointer.Forward*Length;
        }
        internal CockpitProbe Transform(MatrixD matrix) => new CockpitProbe {
            Tip=Vector3D.Transform(Tip,matrix),Start=Vector3D.Transform(Start,matrix),End=Vector3D.Transform(End,matrix) };
        internal float DistanceSquared(BoundingBox box,out Vector3 contact) => SegmentBox.DistanceSquared(Start,End,box,out contact);
        internal bool Intersects(BoundingBox bounds,float padding=0) => DistanceSquared(bounds,out _) <= (Radius+padding)*(Radius+padding);
    }

    internal static class SegmentBox
    {
        internal static float DistanceSquared(Vector3D start,Vector3D endPoint,BoundingBox box,out Vector3 contact)
        {
            contact=Vector3.Zero;
            if(!start.IsValid() || !endPoint.IsValid()) return float.MaxValue;
            var a=(Vector3)start; var d=(Vector3)(endPoint-start);
            float best=float.MaxValue,begin=0;
            // Segment-to-box distance is quadratic between crossings of the six box planes.
            for(int interval=0;interval<7;interval++)
            {
                float end=1;
                for(int axis=0;axis<3;axis++)
                {
                    float delta=Component(d,axis),origin=Component(a,axis);
                    if(Math.Abs(delta)<1e-8f) continue;
                    float lo=(Component(box.Min,axis)-origin)/delta,hi=(Component(box.Max,axis)-origin)/delta;
                    if(lo>begin+1e-6f && lo<end) end=lo;
                    if(hi>begin+1e-6f && hi<end) end=hi;
                }
                float middle=(begin+end)*.5f,aa=0,ab=0;
                for(int axis=0;axis<3;axis++)
                {
                    float delta=Component(d,axis),origin=Component(a,axis),min=Component(box.Min,axis),max=Component(box.Max,axis);
                    float at=origin+delta*middle;
                    if(at>=min && at<=max) continue;
                    float offset=origin-(at<min ? min : max);
                    aa+=delta*delta; ab+=delta*offset;
                }
                float t=aa>1e-12f ? MathHelper.Clamp(-ab/aa,begin,end) : middle;
                var point=a+d*t; var nearest=Vector3.Clamp(point,box.Min,box.Max);
                float distance=Vector3.DistanceSquared(point,nearest);
                if(distance<best) { best=distance; contact=nearest; }
                if(end>=1) break;
                begin=end;
            }
            return best;
        }
        private static float Component(Vector3 v,int axis) => axis==0 ? v.X : axis==1 ? v.Y : v.Z;
    }

    internal static class SegmentDisk
    {
        internal static Vector3 Closest(Vector3 point,BoundingBox bounds)
        {
            var center=bounds.Center;
            float radius=Math.Min(bounds.HalfExtents.X,bounds.HalfExtents.Y);
            var delta=new Vector2(point.X-center.X,point.Y-center.Y);
            float length=delta.Length();
            if(length>radius) delta*=radius/length;
            return new Vector3(center.X+delta.X,center.Y+delta.Y,MathHelper.Clamp(point.Z,bounds.Min.Z,bounds.Max.Z));
        }
        internal static float DistanceSquared(Vector3D start,Vector3D end,BoundingBox bounds)
        {
            if(!start.IsValid() || !end.IsValid() || bounds.HalfExtents.X<=0 || bounds.HalfExtents.Y<=0) return float.MaxValue;
            var a=(Vector3)start; var d=(Vector3)(end-start);
            float low=0,high=1;
            // Distance to a convex cap has a monotone derivative along the finger segment.
            for(int i=0;i<24;i++)
            {
                float t=(low+high)*.5f; var point=a+d*t;
                if(Vector3.Dot(d,point-Closest(point,bounds))<0) low=t; else high=t;
            }
            var nearest=a+d*((low+high)*.5f);
            return Vector3.DistanceSquared(nearest,Closest(nearest,bounds));
        }
    }
}

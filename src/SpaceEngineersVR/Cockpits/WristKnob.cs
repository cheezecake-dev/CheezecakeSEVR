// Adapted from ColbyJack1134's SpaceEngineersVR (Player/WristKnob.cs), with his permission.
// Only the knob's turn maths is carried (CockpitThrottle uses it); his wrist-panel knob drawing and surface key are not.
using System;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class WristKnob
    {
        internal const float Travel=MathHelper.Pi*1.5f;
        internal sealed class Turn
        {
            private Matrix previous,grab;
            private readonly Vector3 axis;
            private readonly float travel,direction;
            public Turn(Vector3? axis=null,float travel=Travel,float direction=-1)
            { this.axis=Vector3.Normalize(axis ?? Vector3.Backward); this.travel=travel; this.direction=direction; }
            private float initial;
            public float Value {get; private set;}
            public Matrix Wrist {get; private set;}
            public void Begin(Matrix local,float value) {previous=grab=local; initial=Value=value; Wrist=local;}
            public void Move(Matrix local)
            {
                if(!local.IsValid()) return;
                var delta=Quaternion.CreateFromRotationMatrix(Matrix.Invert(previous.GetOrientation())*local.GetOrientation());
                float projected=Vector3.Dot(new Vector3(delta.X,delta.Y,delta.Z),axis);
                float length=(float)Math.Sqrt(projected*projected+delta.W*delta.W);
                if(length<.001f) return;
                float angle=2*(float)Math.Atan2(projected/length,delta.W/length);
                if(angle>MathHelper.Pi) angle-=MathHelper.TwoPi;
                if(angle < -MathHelper.Pi) angle+=MathHelper.TwoPi;
                previous=local;
                Value=MathHelper.Clamp(Value+direction*angle/travel,0,1);
                Wrist=grab*Matrix.CreateFromAxisAngle(axis,(Value-initial)*travel/direction);
            }
        }
    }
}

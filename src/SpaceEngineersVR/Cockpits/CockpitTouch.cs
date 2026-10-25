// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitTouch.cs), with his permission.
// Phase 4 of docs/COCKPIT_PORT.md: his fingertip touch on the dashboard's switches, covers, pull bars and handles, kept close
// to his text. The fingertip picks the switch; the trigger (or the grip) presses it, or pinches a lever, cover or bar and moves
// it with the hand. Changes from his:
// - his Controller becomes our Hand; his nested input class Hand is HandInput, beside our Hand;
// - his pointer (the aim pose, or his arm's free point pose) is our index fingertip (FingerPress) aimed along the controller's
//   aim, put into the seat by CockpitControls' tracking-to-seat move; his free wrist is the controller's wrist (GripWrist);
// - his guards for features we have not got (his remote and third-person views, LCD bridges, floating windows, helmet HUD,
//   weapon handling, flight settings window) are dropped; a hand on one of our screens (ScreenTouch) is not free;
// - his seat panel target is not added: the seat panel's keys are a page of our quick menu (SeatPage);
// - the hover labels (Labels, LabelPose) are not carried, nor his TryFreeBarGrip; his palm region on handle bars is the
//   controller's palm (CockpitHand.Palm), within BarGrabRadius of the bar;
// - TryAttachment and TryBarPalm feed our HandIK through PalmTarget below (his TrackedArms.WristWorld's cockpit part);
// - his TapHoldLevers is VRSettings.ColbyTapHoldLevers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class CockpitTouch
    {
        internal const string ClaimName="ColbyCockpitSwitch";
        private static readonly Stopwatch clock=Stopwatch.StartNew();
        /// <summary>His MultiplayerRuntime.Now: seconds on a steady clock.</summary>
        internal static double Now => clock.Elapsed.TotalSeconds;

        internal sealed class Target
        {
            public SurfaceView Surface;
            public bool Lever,Cover,Pull,Analog,FingerSlide;
            public CockpitRig.Handle Handle;
            public int Slot=-1;
            public float Position,Travel;
            public Vector3 Pivot,Axis;
            public bool Hinged => Lever && !FingerSlide || Cover || Handle?.Hinged==true;
            public bool Draggable => Hinged || Pull || Analog || FingerSlide;
            public bool Pinch => Draggable && !FingerSlide;
        }
        internal sealed class HandInput
        {
            private readonly InputGate press=new InputGate();
            private readonly InputGate squeeze=new InputGate();
            private readonly PointerIntent pointer=new PointerIntent();
            private bool guardedSqueeze;
            private readonly HoldGesture holdGesture=new HoldGesture();
            private bool? gripSource,heldGrip;
            public string Surface { get; private set; }
            public int Held { get; private set; }=-1;
            public bool Captured { get; private set; }
            public bool Committed { get; private set; }
            public bool Pressed { get; private set; }
            public bool Consumed { get; private set; }
            public void Reset()
            {
                holdGesture.Reset(); press.Block(); squeeze.Block(); pointer.Reset(); heldGrip=null; guardedSqueeze=false; Surface=null; Held=-1; Captured=Committed=Pressed=false;
            }
            internal void Sample(bool available,InteractionInput input,string target,int key,bool reachable=true,bool guarded=false,bool softCapture=true,bool retainSqueeze=false,bool tapHold=false)
            {
                if(gripSource!=input.Near) { Reset(); gripSource=input.Near; }
                if(input.Near && Surface!=null && heldGrip.HasValue) input=input.Select(heldGrip.Value);
                bool down=holdGesture.Update(input.Down,Surface!=null && Committed,tapHold && input.Near && input.Grip && available && reachable,Now);
                float pressure=down && !input.Down ? 1 : !down && input.Down ? 0:input.Pressure;
                Sample(available,pressure,down,target,key,input.CanAcquire,reachable,guarded,softCapture && !input.Near,retainSqueeze,input.Near || !softCapture);
                if(input.Down && !down) Consumed=true;
                if(Captured) heldGrip=input.Near ? input.Grip:(bool?)null;
            }
            public void Sample(bool available,float pressure,bool down,string target,int key,bool canAcquire=true,bool reachable=true,bool guarded=false,bool softCapture=true,bool retainSqueeze=false,bool clickOnly=false)
            {
                Captured=Pressed=false;
                if(clickOnly && !down && !(retainSqueeze && Surface!=null && Committed))
                { Surface=null; Held=-1; Committed=Consumed=guardedSqueeze=false; }
                if(pressure<=.025f && !down) Consumed=guardedSqueeze=false;
                if(!available || float.IsNaN(pressure)) { Reset(); return; }
                press.Update(true,down);
                squeeze.Update(true,Deadzone(pressure)>0 || down);
                pointer.Begin(true,clickOnly ? down ? 1:0:pressure,down,target!=null);
                if(!reachable || (Committed && !retainSqueeze ? !down : pressure<.08f && !down))
                { Surface=null; Held=-1; Committed=false; }
                if(Surface==null && (!Consumed || guardedSqueeze) && (softCapture && pressure>=.20f || press.Pressed) && canAcquire &&
                    target!=null && key>=0 && pointer.Capture(target,true))
                { Surface=target; Held=key; Captured=Consumed=true; guardedSqueeze=false; }
                if(Surface!=null && !Committed && press.Pressed) { Committed=Pressed=true; }
                if(press.Pressed && canAcquire && guarded) Consumed=true;
                if(press.Pressed) guardedSqueeze=false;
                // Reserve a nearby squeeze before its flight-action threshold.
                if(!Consumed && (squeeze.Pressed || clickOnly && pressure>.025f) && !down && canAcquire && guarded) Consumed=guardedSqueeze=true;
            }
        }
        // His VrMath.Deadzone.
        private static float Deadzone(float value,float deadzone=0.18f) =>
            Math.Abs(value)<=deadzone ? 0 : Math.Sign(value)*(Math.Abs(value)-deadzone)/(1-deadzone);
        internal sealed class HoverGrace
        {
            internal string Surface { get; private set; }
            private int key;
            private Vector3 contact;
            private Vector3D tip;
            private double last;
            internal void Reset() { Surface=null; }
            internal void Remember(string surface,int index,Vector3D localTip,Vector3 localContact,double now)
            { Surface=surface; key=index; tip=localTip; contact=localContact; last=now; }
            internal bool TryGet(Vector3D localTip,double now,out int index,out Vector3 localContact)
            {
                index=-1; localContact=Vector3.Zero;
                if(Surface==null || !localTip.IsValid() || now<last || now-last>.1 || Vector3D.DistanceSquared(tip,localTip)>.0001)
                { Reset(); return false; }
                index=key; localContact=contact; return true;
            }
        }
        internal struct Result
        {
            public int Hover,Held;
            public bool Pressed;
            public bool? Requested;
            public float? Position;
            public Hand Actor;
        }
        internal class SurfaceHold
        {
            public readonly HandInput Input=new HandInput();
            public Vector3 StartHand,Anchor;
            public Matrix Wrist;
            public DateTime Grabbed;
            public void Capture(Matrix localWrist,Vector3 anchor)
            { Wrist=localWrist; StartHand=localWrist.Translation; Anchor=anchor; Grabbed=DateTime.UtcNow; }
            public bool Reachable(Vector3 localHand) => Vector3.Distance(localHand,StartHand)<.28f;
            public bool Attachment(MatrixD surface,out MatrixD wrist,out Vector3D contact,out float blend)
            {
                wrist=(MatrixD)Wrist*surface; contact=Vector3D.Transform(Anchor,surface);
                blend=MathHelper.Clamp((float)(DateTime.UtcNow-Grabbed).TotalSeconds/.09f,0,1);
                return Input.Surface!=null;
            }
        }
        private sealed class Contact : SurfaceHold
        {
            public readonly ControlDrag Drag=new ControlDrag();
            public readonly HoverGrace Grace=new HoverGrace();
            public Target Target,Hover;
            public int HoverKey=-1,Change=-1;
            public Vector3 FitAtGrab;
            public float GripOffset;
            public readonly CockpitFeedback.ValuePulse ValuePulse=new CockpitFeedback.ValuePulse();
            public float StartPosition;
        }
        private static readonly Contact[] hands={new Contact(),new Contact()};
        private static object owner;
        private static int Index(Hand hand) => hand==Hand.Left ? 1 : 0;
        public static bool LeftPointing { get; private set; }
        public static bool RightPointing { get; private set; }
        public static bool OwnsRight => hands[0].Input.Consumed;
        public static bool Owns(Hand hand) => hands[Index(hand)].Input.Consumed;
        internal static bool Hovering(Hand hand) => hands[Index(hand)].Hover!=null;
        public static bool Attached(Hand hand) => hands[Index(hand)].Input.Surface!=null;
        internal static bool HoldingBar(Hand hand) => hands[Index(hand)].Target?.Analog==true && hands[Index(hand)].Target.Handle.Pinch==false && Attached(hand);
        public static bool Pinching(Hand hand) => hands[Index(hand)].Target?.Pinch==true && Attached(hand);
        private static Hand Controller(int i) => i==0 ? Hand.Right : Hand.Left;
        private static void Consume(int i)
        {
            InteractionInput.Read(Controller(i)).Consume();
        }
        /// <summary>
        /// GripClaim's taker: a grip squeezed with the fingertip on a switch is the switch's (ahead of the sticks), unless the palm is
        /// closed on a stick: a hand round a stick whose fingertip reaches a switch beside it takes the stick. A handle bar, taken by
        /// the palm itself, still comes first; beside a stick the palm closed on, only when nearer the bar than the stick (<see cref="BarReach"/>).
        /// </summary>
        internal static bool Take(Hand hand) => VRSettings.ColbyCockpit && Hovering(hand) &&
            (IsBar(hands[Index(hand)].Hover) || !CockpitControls.OnStick(hand));
        private static bool IsBar(Target target) => target?.Analog==true && !target.Handle.Pinch;
        public static void Reset()
        {
            foreach(var h in hands) { h.Input.Reset(); h.Grace.Reset(); h.Target=h.Hover=null; h.HoverKey=-1; }
            LeftPointing=RightPointing=false; owner=null;
        }
        internal static Vector3 Compensate(Vector3 hand,Vector3 fit,Vector3 startFit) => hand-(fit-startFit);
        /// <summary>A handle bar is taken with the palm this near its middle line, metres (as a stick is, CockpitControls.StickReach): a hand closing round it, or just short of it, has it.</summary>
        internal const float BarGrabRadius=.06f;
        internal static bool NearBar(CockpitProbe finger,CockpitProbe? palm,out float distance,out Vector3 contact,float halfWidth=.06f,float radius=.0155f,float margin=0)
        {
            var bounds=new BoundingBox(new Vector3(-halfWidth,-radius,-2*radius),new Vector3(halfWidth,radius,0));
            bounds.Min-=new Vector3(margin); bounds.Max+=new Vector3(margin);
            float squared=finger.DistanceSquared(bounds,out contact);
            distance=(float)Math.Sqrt(squared);
            bool hit=squared<=CockpitProbe.Radius*CockpitProbe.Radius;
            if(palm.HasValue && PalmOnBar(palm.Value,out float separation,out var nearest,halfWidth,radius,margin) && (!hit || separation<distance))
            { hit=true; distance=separation; contact=nearest; }
            return hit;
        }
        /// <summary>The palm within <see cref="BarGrabRadius"/> of a bar's middle line: how far, and the nearest point on the line.</summary>
        private static bool PalmOnBar(CockpitProbe palm,out float separation,out Vector3 contact,float halfWidth,float radius,float margin)
        {
            var axis=new BoundingBox(new Vector3(-halfWidth,0,-radius),new Vector3(halfWidth,0,-radius));
            separation=(float)Math.Sqrt(palm.DistanceSquared(axis,out var nearest));
            contact=new Vector3(nearest.X,0,0);
            return separation<=BarGrabRadius+margin;
        }
        /// <summary>
        /// <see cref="NearBar"/> for a hand whose palm may be closed on a stick, <paramref name="stickGap"/> from its grip
        /// (float.MaxValue when not): then only the palm reaches a bar, and only one nearer it than the stick, so a bar beside
        /// the stick neither takes the hand nor holds back its squeeze.
        /// </summary>
        private static bool BarReach(CockpitProbe finger,CockpitProbe? palm,float stickGap,out float distance,out Vector3 contact,float halfWidth,float radius,float margin=0)
        {
            if(stickGap==float.MaxValue) return NearBar(finger,palm,out distance,out contact,halfWidth,radius,margin);
            distance=float.MaxValue; contact=Vector3.Zero;
            return palm.HasValue && PalmOnBar(palm.Value,out distance,out contact,halfWidth,radius,margin) && distance<stickGap;
        }
        internal static int NearKey(SurfaceView s,CockpitProbe probe,out float distance,out Vector3 contact,float padding=.001f,float margin=0)
        {
            distance=float.MaxValue; contact=Vector3.Zero; int key=-1;
            if(!s.Enabled) return -1;
            for(int i=0;i<s.Keys.Length;i++)
            {
                var k=s.Keys[i]; if(!k.Enabled || k.Bounds.Width<=0 || k.Bounds.Height<=0) continue;
                var bounds=CockpitPanelGuard.KeyBounds(s,k,margin);
                if(!probe.Intersects(bounds,padding)) continue;
                if(k.Round && SegmentDisk.DistanceSquared(probe.Start,probe.End,bounds)>(CockpitProbe.Radius+padding)*(CockpitProbe.Radius+padding)) continue;
                var point=k.Round ? SegmentDisk.Closest((Vector3)probe.Tip,bounds) : Vector3.Clamp((Vector3)probe.Tip,bounds.Min,bounds.Max);
                float candidate=Vector3.Distance((Vector3)probe.Tip,point);
                if(candidate>=distance) continue;
                distance=candidate; contact=point; key=i;
            }
            return key;
        }

        /// <summary>His Headset position (world): the camera's.</summary>
        internal static Vector3D HeadWorld() => MySector.MainCamera?.Position ?? Vector3D.Zero;

        /// <summary>
        /// His pointer: the index fingertip, pointing the way the controller aims, in the world. Through the seat this frame,
        /// as the sticks are (CockpitControls.TrackingToSeat).
        /// </summary>
        private static MatrixD Pointer(Hand hand,MyCockpit seat)
        {
            HandState state=VRInput.Get(hand);
            Matrix aim=state.Aim;
            aim.Translation=FingerPress.FingertipInTracking(hand,state);
            return (MatrixD)(aim*CockpitControls.TrackingToSeat)*seat.WorldMatrix;
        }
        /// <summary>His TrackedArms.FreeWristWorld: the controller's wrist, not drawn onto anything, in the world.</summary>
        private static MatrixD FreeWristWorld(Hand hand,MyCockpit seat) =>
            (MatrixD)CockpitHand.GripWrist(CockpitControls.HandLocal(hand))*seat.WorldMatrix;

        /// <summary>Once per input frame (VRControls), after SeatFit.Update, then CockpitButtons.Update.</summary>
        public static void BeginFrame()
        {
            CockpitButtons.Prepare();
            LeftPointing=RightPointing=false;
            var seat=SeatFit.Seat;
            bool eligible=SeatFit.Eligible(seat);
            if(!eligible)
            {
                for(int i=0;i<2;i++) if(hands[i].Input.Consumed)
                {
                    hands[i].Input.Sample(false,InteractionInput.Read(Controller(i)),null,-1);
                    if(hands[i].Input.Consumed) Consume(i);
                }
                Reset();
                return;
            }
            bool changed=!ReferenceEquals(owner,seat);
            owner=seat;
            bool available=eligible && !changed && !CockpitControls.MenuOpen && CockpitControls.ViewOk &&
                VRInput.Get(Hand.Left).Tracked && VRInput.Get(Hand.Right).Tracked;
            var targets=new List<Target>(CockpitButtons.Targets);
            double now=(double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;
            Vector3D head=HeadWorld();
            for(int i=0;i<2;i++)
            {
                var h=hands[i]; var hand=Controller(i);
                var input=InteractionInput.Read(hand);
                // His: a hand flying with its thumbstick works no switch.
                bool flying=VRInput.Get(hand).Stick.LengthSquared()>.04f;
                bool free=available && !CockpitControls.Held(hand) && !ScreenTouch.Down(hand);
                h.Change=-1;
                if(!free)
                {
                    h.Input.Sample(false,input,null,-1); h.Grace.Reset(); h.Target=h.Hover=null; h.HoverKey=-1;
                    if(h.Input.Consumed) Consume(i);
                    continue;
                }
                MatrixD rawWrist=FreeWristWorld(hand,seat);
                Matrix localWrist=(Matrix)(rawWrist*seat.PositionComp.WorldMatrixNormalizedInv);
                Vector3 raw=localWrist.Translation;
                var pointer=Pointer(hand,seat);
                var probe=new CockpitProbe(pointer,hand==Hand.Left);
                bool guarded=false;
                // His palm region on handle bars: the hand's palm (CockpitHand.Palm), so a bar is taken by closing the hand round it, not only by the fingertip.
                Vector3D palmWorld=Vector3D.Transform(CockpitHand.Palm(CockpitControls.HandLocal(hand)),seat.WorldMatrix);
                bool hasPalm=palmWorld.IsValid(); CockpitProbe palmRegion=new CockpitProbe { Tip=palmWorld,Start=palmWorld,End=palmWorld };
                float stickGap=CockpitControls.OnStick(hand) ? CockpitControls.StickGap(hand) : float.MaxValue;
                Target chosen=null; int key=-1; float nearest=float.MaxValue; Vector3 chosenContact=Vector3.Zero;
                foreach(var target in targets)
                {
                    var s=target.Surface;
                    if((!target.Analog || target.Handle.Pinch) && Vector3D.Dot(s.Pose.Backward,head-s.Pose.Translation)<=.005) continue;
                    var localProbe=probe.Transform(MatrixD.Invert(s.Pose));
                    float distance; Vector3 contact;
                    if(target.Analog && !target.Handle.Pinch)
                        guarded|=BarReach(localProbe,hasPalm ? (CockpitProbe?)palmRegion.Transform(MatrixD.Invert(s.Pose)):null,stickGap,
                            out _,out _,target.Handle.HalfWidth,target.Handle.Radius,CockpitPanelGuard.Margin);
                    else guarded|=NearKey(s,localProbe,out _,out _,target.Pull ? .004f : .001f,CockpitPanelGuard.Margin)>=0;
                    int candidate=target.Analog && !target.Handle.Pinch ? BarReach(localProbe,hasPalm ? (CockpitProbe?)palmRegion.Transform(MatrixD.Invert(s.Pose)):null,stickGap,out distance,out contact,target.Handle.HalfWidth,target.Handle.Radius) ? 0:-1 :
                        NearKey(s,localProbe,out distance,out contact,target.Pull ? .004f : .001f);
                    if(candidate<0) continue;
                    guarded=true;
                    if(flying || hands[1-i].Input.Surface==s.Id) continue;
                    if(h.Hover?.Surface.Id==s.Id && h.HoverKey==candidate) distance-=.001f;
                    if(distance<nearest) { chosen=target; key=candidate; nearest=distance; chosenContact=contact; }
                }
                if(flying || h.Input.Surface!=null) h.Grace.Reset();
                else if(chosen!=null)
                    h.Grace.Remember(chosen.Surface.Id,key,Vector3D.Transform(probe.Tip,MatrixD.Invert(chosen.Surface.Pose)),chosenContact,now);
                else if(h.Grace.Surface!=null)
                {
                    var recent=targets.FirstOrDefault(t=>t.Surface.Id==h.Grace.Surface);
                    if(recent==null || hands[1-i].Input.Surface==recent.Surface.Id ||
                        Vector3D.Dot(recent.Surface.Pose.Backward,head-recent.Surface.Pose.Translation)<=.005)
                        h.Grace.Reset();
                    else if(h.Grace.TryGet(Vector3D.Transform(probe.Tip,MatrixD.Invert(recent.Surface.Pose)),now,out int recentKey,out var recentContact) &&
                        recentKey>=0 && recentKey<recent.Surface.Keys.Length && recent.Surface.Keys[recentKey].Enabled)
                    { chosen=recent; key=recentKey; chosenContact=recentContact; guarded=true; }
                }
                bool pointing=targets.Any(t=>Vector3D.Distance(rawWrist.Translation,t.Surface.Pose.Translation)<.4);
                if(i==0) RightPointing=pointing; else LeftPointing=pointing;
                if(chosen!=null && (h.Hover?.Surface.Id!=chosen.Surface.Id || h.HoverKey!=key) && !h.Input.Consumed)
                    CockpitFeedback.Hover(hand);
                h.Hover=chosen; h.HoverKey=key;
                var held=targets.FirstOrDefault(t=>t.Surface.Id==h.Input.Surface);
                Vector3 motion=Compensate(raw,SeatFit.Offset,h.FitAtGrab);
                bool reachable=h.Input.Surface==null || held!=null;
                h.Input.Sample(true,input,chosen?.Surface.Id,key,reachable,guarded,retainSqueeze:held?.Draggable==true,tapHold:VRSettings.ColbyTapHoldLevers && (held ?? chosen)?.Analog==true);
                if(h.Input.Captured)
                {
                    held=chosen; h.StartHand=raw; h.FitAtGrab=SeatFit.Offset; h.Wrist=localWrist; h.Grabbed=DateTime.UtcNow;
                    var s=held.Surface; var b=s.Keys[h.Input.Held].Bounds;
                    Vector3D anchor=Vector3D.Transform(new Vector3D((b.Center.X-.5)*s.Width,(.5-b.Center.Y)*s.Height,0),s.Pose);
                    if(held.Pull || held.Analog)
                        anchor=Vector3D.Transform(chosenContact,s.Pose);
                    h.Capture(localWrist,(Vector3)Vector3D.Transform(anchor,seat.PositionComp.WorldMatrixNormalizedInv));
                    h.StartPosition=held.Position; h.GripOffset=chosenContact.X;
                    h.ValuePulse.Reset(held.Position);
                    if(held.Hinged) h.Drag.Begin(raw,held.Handle!=null ? Vector3.Transform(held.Handle.Center,held.Handle.Visual(held.Position)):h.Anchor,held.Pivot,held.Axis,held.Position,held.Travel);
                    else if(held.Pull || held.Analog || held.FingerSlide) h.Drag.BeginLinear(raw,held.Axis,held.Position,held.Travel);
                    CockpitFeedback.Engage(hand);
                }
                h.Target=h.Input.Surface!=null ? held : null;
                if(h.Target?.Draggable==true && !h.Input.Captured) h.Change=h.Drag.Move(motion,h.Input.Committed);
                if(h.Target?.Analog==true && h.Input.Committed && CockpitActions.ReadAnalog(h.Target.Slot,out _,out _) &&
                    h.ValuePulse.Sample(h.Drag.Value,DateTime.UtcNow)) CockpitFeedback.Activate(hand,.10f,.012f);
                if(h.Input.Consumed)
                {
                    h.Hover=h.Target; h.HoverKey=h.Target!=null ? h.Input.Held : -1;
                    Consume(i);
                }
            }
        }
        public static Result Read(string surface)
        {
            var result=new Result { Hover=-1,Held=-1 };
            for(int i=0;i<2;i++)
            {
                var h=hands[i];
                if(h.Hover?.Surface.Id==surface) result.Hover=h.HoverKey;
                if(h.Input.Surface!=surface) continue;
                result.Held=h.Input.Committed ? h.Input.Held : -1; result.Hover=h.Input.Held; result.Actor=Controller(i);
                result.Pressed=h.Input.Pressed;
                if(h.Target?.Draggable==true)
                {
                    result.Position=h.Drag.Value;
                    if(h.Change>=0) result.Requested=h.Change==1;
                }
            }
            return result;
        }
        internal static bool TryBarPalm(Hand hand,out MatrixD palm,out float blend)
        {
            palm=MatrixD.Identity; blend=0;
            if(!HoldingBar(hand) || !SeatFit.Eligible(SeatFit.Seat)) return false;
            var h=hands[Index(hand)];
            var handle=CockpitRig.Find(SeatFit.Seat.BlockDefinition.Id.SubtypeName)?.HandleAt(h.Target.Slot);
            if(handle==null) return false;
            palm=(MatrixD)handle.Palm(hand==Hand.Left,CockpitButtons.SwitchPosition(h.Target.Slot),h.GripOffset)*SeatFit.Seat.WorldMatrix;
            blend=MathHelper.Clamp((float)(DateTime.UtcNow-h.Grabbed).TotalSeconds/.12f,0,1);
            return true;
        }
        /// <summary>
        /// The handle bar <paramref name="hand"/> holds, as drawn (<paramref name="pose"/>: its rest place to where it is now, in
        /// the cockpit's space), the cockpit and its rig (<see cref="CockpitGrip"/>). False when it holds none.
        /// </summary>
        internal static bool TryHeldBar(Hand hand,out CockpitRig.Handle handle,out Matrix pose,out MyCockpit cockpit,out CockpitRig rig)
        {
            handle=null; pose=Matrix.Identity; cockpit=SeatFit.Seat; rig=null;
            if(!HoldingBar(hand) || !SeatFit.Eligible(cockpit)) return false;
            var h=hands[Index(hand)];
            rig=CockpitRig.Find(cockpit.BlockDefinition.Id.SubtypeName);
            handle=rig?.HandleAt(h.Target.Slot);
            if(handle==null) return false;
            pose=handle.Visual(CockpitButtons.SwitchPosition(h.Target.Slot));
            return true;
        }
        internal static bool TryAttachment(Hand hand,out MatrixD wrist,out Vector3D contact,out float blend)
        {
            var h=hands[Index(hand)];
            wrist=MatrixD.Identity; contact=Vector3D.Zero; blend=0;
            if(h.Input.Surface==null || h.Target==null || !SeatFit.Eligible(SeatFit.Seat)) return false;
            Vector3 anchor=h.Anchor;
            if(h.Target.Hinged)
            {
                float value=h.Target.Cover ? CockpitButtons.CoverPosition(h.Target.Slot) : CockpitButtons.SwitchPosition(h.Target.Slot);
                anchor=Vector3.Transform(anchor,CockpitStickMath.Around(h.Target.Pivot,Matrix.CreateFromAxisAngle(h.Target.Axis,(value-h.StartPosition)*h.Target.Travel)));
            }
            else if(h.Target.Pull || h.Target.Analog)
                anchor+=h.Target.Axis*((CockpitButtons.SwitchPosition(h.Target.Slot)-h.StartPosition)*h.Target.Travel);
            else if(CockpitRig.Find(SeatFit.Seat.BlockDefinition.Id.SubtypeName)?.ButtonAt(h.Target.Slot) is CockpitRig.Button button && h.Input.Committed)
                anchor-=button.Normal*button.Travel;
            wrist=(MatrixD)h.Wrist*SeatFit.Seat.WorldMatrix;
            contact=Vector3D.Transform(anchor,SeatFit.Seat.WorldMatrix);
            blend=MathHelper.Clamp((float)(DateTime.UtcNow-h.Grabbed).TotalSeconds/.09f,0,1);
            return true;
        }

        /// <summary>
        /// His TrackedArms.WristWorld, the cockpit-switch part, for our HandIK: <paramref name="target"/> is the palm bone's
        /// target in <paramref name="body"/>'s model space. A hand on a handle bar has its palm put on the bar (TryBarPalm) and
        /// its fingers closed round it (<paramref name="grip"/>); a hand on a switch, cover or lever keeps the wrist it had when
        /// it took hold, moved so the drawn index fingertip stays on the contact as the control moves (TryAttachment), so the
        /// hand is seen working the switch in front of the dashboard. His pinch contact (between index and thumb) is our drawn
        /// index fingertip (FingerPress.TipInPalm). False (target unchanged) when the hand is on nothing of his.
        /// </summary>
        internal static bool PalmTarget(Hand hand,Sandbox.Game.Entities.Character.MyCharacter body,ref Matrix target,out bool grip)
        {
            grip=false;
            var seat=SeatFit.Seat;
            if(!VRSettings.ColbyCockpit || body?.PositionComp==null || !SeatFit.Eligible(seat) || seat.Pilot!=body) return false;
            MatrixD toBody=body.PositionComp.WorldMatrixNormalizedInv;
            Matrix attached; float blend;
            if(TryBarPalm(hand,out var barPalm,out blend))
            { attached=(Matrix)(barPalm*toBody); grip=true; CockpitGrip.PushPalm(hand,ref attached); }
            else if(TryAttachment(hand,out var wrist,out var contact,out blend))
            {
                HandState state=VRInput.Get(hand);
                if(!state.Tracked) return false;
                // The palm where the controller has it, relative to his wrist (GripWrist) on the same controller.
                Matrix palmInWrist=Tracking.HandIK.PalmTarget(hand,state)*Matrix.Invert(CockpitHand.GripWrist(CockpitHand.GripTracking(hand)));
                MatrixD palm=(MatrixD)palmInWrist*wrist;
                palm.Translation+=contact-Vector3D.Transform((Vector3D)FingerPress.TipInPalm(hand),palm);
                attached=(Matrix)(palm*toBody);
            }
            else return false;
            if(!attached.IsValid()) return false;
            var q=Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(target),Quaternion.CreateFromRotationMatrix(attached),blend);
            var blended=Matrix.CreateFromQuaternion(q);
            blended.Translation=Vector3.Lerp(target.Translation,attached.Translation,blend);
            target=blended;
            return true;
        }
    }
}

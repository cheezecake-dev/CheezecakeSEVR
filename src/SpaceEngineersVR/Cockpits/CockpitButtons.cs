// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitButtons.cs), with his permission.
// Phase 4 of docs/COCKPIT_PORT.md: his touch targets per dashboard slot, the covers, and the switch positions the render poses,
// kept close to his text. Changes from his: switch state and covers are this player's own (no multiplayer sync: his shared
// covers become CockpitActions' local record); his per-seat Alignment of a target is not carried (the target is where his rig
// data puts it); his hover label (Label) is not carried, nor anything drawn by his overlay: the switches, covers and their
// highlights are the cockpit's own meshes, posed and lit in the world by CockpitRender. A handle assigned a numeric action
// drives it (his analog path, phase 6: CockpitActions.ReadAnalog/SetAnalog); with anything else assigned it works as a two-way
// switch, as his does.
using System;
using System.Collections.Generic;
using System.Linq;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class CockpitButtons
    {
        private static readonly float[] positions=new float[CockpitLayout.MaximumCount];
        private static readonly double[] nextAnalogSend=new double[CockpitLayout.MaximumCount];
        private static readonly float?[] sentAnalog=new float?[CockpitLayout.MaximumCount];
        private static readonly float?[] pendingAnalog=new float?[CockpitLayout.MaximumCount];
        private static readonly DateTime[] pulses=new DateTime[CockpitLayout.MaximumCount];
        private static readonly float[] covers=new float[CockpitLayout.MaximumCount];
        private static readonly bool[] open=new bool[CockpitLayout.MaximumCount];
        private static string subtype;
        static CockpitButtons() { ResetCovers(); }
        private static int CoverIndex(int slot) => CockpitRig.Find(subtype)?.CoverIndex(slot) ?? -1;
        private static DateTime lastUpdate;
        internal static float SwitchPosition(int slot,string model=null) => model!=null && model!=subtype ? 0 : positions[slot];
        internal static float CoverPosition(int slot,string model=null)
        {
            if(model!=null && model!=subtype) return 0;
            int index=CoverIndex(slot);
            return index<0 ? 0 : covers[index];
        }
        private static Sandbox.Game.Entities.MyCockpit owner;
        private static readonly RenderRecovery recovery=new RenderRecovery("Cockpit buttons");
        private static bool failed => recovery.Failed;
        public static SurfaceView[] Views { get; private set; }=new SurfaceView[0];
        internal static CockpitTouch.Target[] Targets { get; private set; }=new CockpitTouch.Target[0];
        public static int HoveredSwitch { get; private set; }=-1;
        internal static SurfaceView Preview(string subtype,int index)
        {
            var pose=CockpitLayout.Control(subtype,index,out float size);
            var rig=CockpitRig.Find(subtype);
            return new SurfaceView { Id="CockpitControl"+index,
                Pose=pose,Width=size,Height=rig.HandleAt(index)!=null ? .032f : rig.BarAt(index)?.Height ?? rig.LeverAt(index)?.Height ?? size,GeometryFeedback=(rig.ButtonAt(index)?.Actor ?? 0)>=0,
                Keys=new[] {new SurfaceKey("",0,0,1,1) {Round=rig.ButtonAt(index)?.Round ?? false}} };
        }
        private static void Release() { HoveredSwitch=-1; Targets=new CockpitTouch.Target[0]; Views=new SurfaceView[0]; }
        private static void ResetCovers()
        {
            Array.Clear(covers,0,covers.Length); Array.Clear(open,0,open.Length);
            var saved=owner==null ? null : CockpitActions.LoadLocal(owner).Covers;
            if(saved!=null) for(int i=0;i<Math.Min(saved.Length,covers.Length);i++) { open[i]=saved[i]; covers[i]=saved[i] ? 1 : 0; }
        }
        private static void SaveCovers(int index)
        {
            if(owner==null) return;
            CockpitActions.SaveCovers(owner,open);
        }
        public static void Reset()
        {
            recovery.Clear(); owner=null; Release(); ResetCovers(); CockpitTouch.Reset();
            Array.Clear(positions,0,positions.Length); Array.Clear(pulses,0,pulses.Length);
            Array.Clear(nextAnalogSend,0,nextAnalogSend.Length); Array.Clear(sentAnalog,0,sentAnalog.Length); Array.Clear(pendingAnalog,0,pendingAnalog.Length);
            lastUpdate=DateTime.MinValue; CockpitActions.Reset();
        }
        public static void Prepare()
        {
            if(failed) return;
            try
            {
                var seat=SeatFit.Seat;
                bool eligible=SeatFit.Eligible(seat) && CockpitLayout.Supported(seat.BlockDefinition.Id.SubtypeName);
                if(!eligible) seat=null;
                CockpitActions.Update(seat);
                if(!ReferenceEquals(owner,seat))
                { Array.Clear(pendingAnalog,0,pendingAnalog.Length); Array.Clear(nextAnalogSend,0,nextAnalogSend.Length); Array.Clear(sentAnalog,0,sentAnalog.Length); owner=seat; subtype=seat?.BlockDefinition.Id.SubtypeName; Release(); ResetCovers(); Array.Clear(positions,0,positions.Length); Array.Clear(pulses,0,pulses.Length); }
                Release();
                if(!eligible || CockpitControls.MenuOpen || CockpitControls.Adjusting)
                { Array.Clear(pendingAnalog,0,pendingAnalog.Length); Array.Clear(sentAnalog,0,sentAnalog.Length); return; }
                subtype=seat.BlockDefinition.Id.SubtypeName;
                var rig=CockpitRig.Find(subtype);
                var head=CockpitTouch.HeadWorld();
                var targets=new List<CockpitTouch.Target>();
                for(int i=0;i<rig.Count;i++)
                {
                    var lever=rig.LeverAt(i); var handle=rig.HandleAt(i); var bar=rig.BarAt(i);
                    int coverIndex=CoverIndex(i);
                    bool covered=coverIndex>=0;
                    if(covered && CockpitRender.Ready)
                    {
                        var cover=new SurfaceView { Id="CockpitCover"+i,GeometryFeedback=true,
                            Pose=lever.CoverPose(covers[coverIndex])*seat.WorldMatrix,Width=.019f,Height=.035f,
                            Keys=new[] {new SurfaceKey("",0,0,1,1)} };
                        if(Vector3D.Dot(cover.Pose.Backward,head-cover.Pose.Translation)<0) cover.Pose=MatrixD.CreateRotationY(Math.PI)*cover.Pose;
                        targets.Add(new CockpitTouch.Target { Surface=cover,Slot=i,Cover=true,Position=covers[coverIndex],
                            Pivot=lever.Hinge,Axis=lever.CoverAxis,Travel=CockpitRig.Lever.CoverTravel });
                    }
                    bool modeled=(rig.ButtonAt(i)?.Actor ?? 0)>=0;
                    if(modeled && !(CockpitRender.Ready && (!covered || open[coverIndex] && covers[coverIndex]>.98f))) continue;
                    var s=Preview(subtype,i);
                    s.GeometryFeedback=modeled;
                    if(modeled) s.Pose*=handle?.Visual(positions[i]) ?? bar?.Visual(positions[i]) ?? lever?.Visual(positions[i]) ?? Matrix.Identity;
                    var native=s.Pose*seat.WorldMatrix;
                    s.Pose=native;
                    MatrixD correction=seat.WorldMatrix*MatrixD.Invert(native)*s.Pose*seat.PositionComp.WorldMatrixNormalizedInv;
                    targets.Add(new CockpitTouch.Target { Surface=s,Slot=i,Analog=handle!=null,Handle=handle,Lever=lever!=null,FingerSlide=lever?.FingerSlide==true,Pull=bar!=null,Position=positions[i],
                        Pivot=(Vector3)Vector3D.Transform(handle?.Pivot ?? lever?.Pivot ?? Vector3.Zero,correction),
                        Axis=(Vector3)Vector3D.TransformNormal(handle?.Axis ?? bar?.Normal ?? (lever?.FingerSlide==true ? lever.SlideAxis:lever?.Axis) ?? Vector3.Right,correction),
                        Travel=handle?.Range ?? bar?.Travel ?? (lever?.FingerSlide==true ? CockpitRig.Lever.FingerTravel:lever?.AngularTravel) ?? CockpitRig.Lever.Travel });
                }
                Targets=targets.ToArray(); Views=targets.Select(t=>t.Surface).ToArray();
                recovery.Succeeded();
            }
            catch(Exception ex) { Fail(ex); }
        }
        /// <summary>Once per input frame (VRControls), after CockpitTouch.BeginFrame.</summary>
        public static void Update()
        {
            if(failed || !SeatFit.Eligible(owner) || !CockpitLayout.Supported(subtype)) return;
            try
            {
                var now=DateTime.UtcNow;
                float step=(float)Math.Min(.05,Math.Max(0,(now-lastUpdate).TotalSeconds))*12; lastUpdate=now;
                foreach(var target in Targets)
                {
                    var s=target.Surface; int i=target.Slot;
                    var input=CockpitTouch.Read(s.Id);
                    s.Hover=input.Hover; s.Pressed=input.Held;
                    if(target.Cover)
                    {
                        int coverIndex=CoverIndex(i);
                        if(input.Requested.HasValue && CockpitActions.SharedReady) { open[coverIndex]=input.Requested.Value; SaveCovers(coverIndex); CockpitFeedback.Click(input.Actor,cover:true); }
                        covers[coverIndex]=input.Position ?? covers[coverIndex]+MathHelper.Clamp((open[coverIndex] ? 1 : 0)-covers[coverIndex],-step*.65f,step*.65f);
                        continue;
                    }
                    if(s.Hover>=0 && HoveredSwitch<0) HoveredSwitch=i;
                    if(target.Analog && CockpitActions.ReadAnalog(i,out _,out _))
                    {
                        if(input.Held>=0 && input.Position.HasValue && (!sentAnalog[i].HasValue || Math.Abs(input.Position.Value-sentAnalog[i].Value)>.0001f)) pendingAnalog[i]=input.Position.Value;
                        if(pendingAnalog[i].HasValue && (input.Held<0 || CockpitTouch.Now>=nextAnalogSend[i]))
                        {
                            CockpitActions.SetAnalog(i,pendingAnalog[i].Value);
                            sentAnalog[i]=pendingAnalog[i]; pendingAnalog[i]=null; nextAnalogSend[i]=CockpitTouch.Now+.05;
                        }
                        if(input.Held<0) sentAnalog[i]=null;
                        continue;
                    }
                    bool activate=target.Draggable ? input.Requested.HasValue : input.Pressed;
                    if(activate)
                    {
                        bool? requested=input.Requested;
                        if(target.Pull && requested==false && !CockpitActions.ReadState(i,out _)) continue;
                        if(CockpitActions.Activate(i,requested))
                        { pulses[i]=now.AddSeconds(.28); CockpitFeedback.Click(input.Actor); }
                        if(CockpitActions.Editing) { CockpitTouch.Reset(); Release(); return; }
                    }
                }
                // Covered levers still reflect changes made through terminals or other controls.
                for(int i=0;i<CockpitLayout.Count(subtype);i++)
                {
                    bool numeric=CockpitActions.ReadAnalog(i,out float actual,out _);
                    bool stateful=numeric || CockpitActions.ReadState(i,out actual);
                    float state=stateful ? actual : now<pulses[i] ? 1 : 0;
                    var input=CockpitTouch.Read("CockpitControl"+i);
                    float target=input.Position ?? state;
                    if(input.Position.HasValue && CockpitRig.Find(subtype)?.LeverAt(i)!=null)
                        target=MathHelper.Lerp(state,target,.65f);
                    positions[i]=numeric ? target:positions[i]+MathHelper.Clamp(target-positions[i],-step,step);
                }
            }
            catch(Exception ex) { Fail(ex); }
        }
        private static void Fail(Exception ex)
        {
            Release(); CockpitTouch.Reset(); CockpitActions.Reset();
            recovery.Fail(ex,"Cockpit switches disabled; flight input retained");
        }
    }
}

// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitControls.cs), with his permission.
// Phases 1 to 3 of docs/COCKPIT_PORT.md: his UpdateCore, ApplyFlight and WristWorld (sticks, and the wheel, yoke, throttle and
// saddle-bar branches), his stick placement (unlock, move, lock, reset) and his haptics, kept close to his text. Not carried
// yet: turrets, his remote view and multiplayer timing, his control blocking (BlockGrip, BlockUntilRelease). His per-cockpit
// FlightSettings.For(seat) is carried in phase 8 (FlightSettings.cs), kept locally per world and cockpit. His Controller becomes our Hand (CockpitHand), his Controls bindings our VRInput, his Vibrate our
// haptics, his tracking-to-seat matrix is made from our tracking-to-body one (HandWorld.TryGetTrackingToBody), and his
// TrackedArms' use of WristWorld becomes PalmTarget, which HandIK asks for.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static partial class CockpitControls
    {
        internal const string ClaimName="ColbyCockpit";
        // His RotationSpeed and default ShipRollSensitivity, passed to ApplyFlight as his Main did.
        internal const float RotationSpeed=10f,ShipRollSensitivity=.6f;

        private static MyCockpit seat;
        private static CockpitRig Rig => seat==null ? null : CockpitRig.Find(seat.BlockDefinition.Id.SubtypeName);
        private static readonly GripCapture left=new GripCapture(),right=new GripCapture();
        private static readonly CockpitSteering steering=new CockpitSteering();
        private static readonly CockpitBarTilt barTilt=new CockpitBarTilt();
        private static float throttlePosition;
        private static readonly CockpitThrottle throttle=new CockpitThrottle();
        private static readonly CockpitYoke yoke=new CockpitYoke();
        private static float yokeRoll;
        private static float throttleCommand;
        internal static Matrix SteeringVisual => YokeMode ? Rig.Wheel.YokeVisual(yokeRoll,yoke.Visual) : Rig?.Wheel?.Visual(steering.VisualPosition,barTilt.Visual) ?? Matrix.Identity;
        private static bool YokeMode => VehicleFlight && Rig.Wheel.ThrottleActor<0 && tuning.WheelMotion;
        internal static float Throttle => IsSteering && Rig.Wheel.ThrottleActor>=0 && right.Held ? throttleCommand:0;
        internal static float ThrottleVisual { get; private set; }
        internal static bool IsSteering => Rig?.Wheel!=null;
        internal static bool VehicleFlight => IsSteering && tuning.FlightMode;
        private static Matrix leftNeutral,rightNeutral;
        private static Vector3 leftAngles,rightAngles;
        private static FlightTuning tuning=new FlightTuning();
        private static Matrix leftVisual=Matrix.Identity,rightVisual=Matrix.Identity;
        private static readonly CockpitFeedback.StickPulse leftDetent=new CockpitFeedback.StickPulse(),rightDetent=new CockpitFeedback.StickPulse();
        private static readonly CockpitFeedback.StickPulse barDetent=new CockpitFeedback.StickPulse();
        private static readonly CockpitStickMath.Filter leftFilter=new CockpitStickMath.Filter(),rightFilter=new CockpitStickMath.Filter();
        private static double lastInput;
        private static DateTime leftPulse,rightPulse,barPulse,grabLeft,grabRight;
        private static Vector3 translation,rotation;
        private static Vector2 barCommand;
        private static FlightTuning previousTuning;
        private static bool leftWas,rightWas;
        private static readonly CockpitFeedback.ProximityPulse leftHover=new CockpitFeedback.ProximityPulse(),rightHover=new CockpitFeedback.ProximityPulse();
        private static readonly StickPlacement placement=new StickPlacement();
        private static Vector3 leftStartOffset,rightStartOffset;
        private static bool viewOk;
        private static Matrix trackingToSeat;
        private static readonly Stopwatch clock=Stopwatch.StartNew();

        /// <summary>The seat is his: its sticks are drawn moving and fly it.</summary>
        internal static bool Owns => seat!=null && Eligible(seat) && CockpitRender.Ready;
        /// <summary>His rig owns this seat (SeatFit and the quick menu's Seat page are his only there).</summary>
        internal static bool OwnsSeat(MyCockpit cockpit) => cockpit!=null && cockpit==seat && Owns;
        /// <summary>The cockpit his code is posing has this pilot.</summary>
        internal static bool Seated(MyCharacter character) => seat!=null && character!=null && seat.Pilot==character;

        public static bool Adjusting => placement.Unlocked;
        public static bool CanAdjust => seat!=null && !IsSteering && Eligible(seat) && CockpitRender.Ready && !menuOpen;
        private static bool SingleLeft => !IsSteering && Rig!=null && Rig.Right==null;
        internal static bool OwnsRightThumb => seat!=null && !IsSteering && !Adjusting && (right.Held || SingleLeft && left.Held);
        public static bool Held(Hand hand) => hand==Hand.Left ? left.Held : right.Held;
        private static bool menuOpen;
        // Read by CockpitTouch: a menu is open, and this frame's tracking-to-seat move (valid when ViewOk).
        internal static bool MenuOpen => menuOpen;
        internal static bool ViewOk => viewOk;
        internal static Matrix TrackingToSeat => trackingToSeat;
        /// <summary>The hand is on one of his controls, a stick or a switch: it presses no screen or button panel meanwhile.</summary>
        internal static bool HandBusy(Hand hand) => Held(hand) || CockpitTouch.Attached(hand);
        private static bool Available => seat!=null && Eligible(seat) && CockpitRender.Ready && !menuOpen && viewOk &&
            VRInput.Get(Hand.Left).Tracked && VRInput.Get(Hand.Right).Tracked;
        /// <summary>A wheel's or handlebar's grip takes hold with the wrist this near where holding it puts the wrist, metres.</summary>
        internal const float CaptureRadius=.14f;
        /// <summary>
        /// A stick takes hold with the hand's palm this near its grip, metres, whichever way the hand is turned: a hand closing
        /// anywhere round the handle, or just short of it, has it. His measure (the wrist within CaptureRadius of where holding
        /// the stick puts it, which is wider for a hand already turned as on the stick) takes hold too.
        /// </summary>
        internal const float StickReach=.10f;
        /// <summary>How far along a stick's grip, each way from where the hand holds it, the handle counts, metres.</summary>
        internal const float StickGripHalfLength=.06f;
        internal static bool NearGrip(Hand hand) => Available && NearDistance(hand==Hand.Left,GripDistance(hand));
        /// <summary>The hand's palm is closed on a stick (well inside its reach): the grip is the stick's, ahead of a switch the fingertip happens to rest on.</summary>
        internal static bool OnStick(Hand hand) => Available && !IsSteering && GripDistance(hand)<StickReach/2;
        /// <summary>How far the hand's palm is from its stick's grip, metres (on the scale of <see cref="StickReach"/>); float.MaxValue with no stick.</summary>
        internal static float StickGap(Hand hand) => Available && !IsSteering ? GripDistance(hand) : float.MaxValue;
        private static float Reach => IsSteering ? CaptureRadius:StickReach;
        private static bool NearDistance(bool isLeft,float distance) => IsSteering ?
            (isLeft ? leftHover:rightHover).Contains(distance,CaptureRadius):distance<StickReach;
        private static float GripDistance(Hand hand)
        {
            bool isLeft=hand==Hand.Left;
            var stick=isLeft ? Rig?.Left:Rig?.Right;
            if(Rig==null || stick==null && !IsSteering) return float.MaxValue;
            var controller=HandLocal(hand);
            var attached=CockpitHand.WristForPalm(seat.Pilot,hand,StickPalm(isLeft));
            // A stick: the nearer of the palm's distance from its grip and his wrist measure, on the palm's scale.
            if(!IsSteering) return Math.Min(StickDistance(stick,isLeft ? leftVisual:rightVisual,CockpitHand.Palm(controller)),GripDistance(controller,attached)*StickReach/CaptureRadius);
            if(!CockpitStickMath.GripAligned(CockpitHand.GripWrist(controller),attached)) return float.MaxValue;
            return GripDistance(controller,attached);
        }
        /// <summary>How far <paramref name="palm"/> (the seat's space) is from the stick's grip as drawn: its line through the hand's hold, a hand's width each way.</summary>
        internal static float StickDistance(CockpitRig.Stick stick,Matrix visual,Vector3 palm)
        {
            Vector3 contact=Vector3.Transform(stick.Contact,visual);
            Vector3 line=Vector3.TransformNormal(stick.GripShaft,visual);
            if(!contact.IsValid() || !palm.IsValid() || line.LengthSquared()<1e-8f) return float.MaxValue;
            line.Normalize();
            float along=MathHelper.Clamp(Vector3.Dot(palm-contact,line),-StickGripHalfLength,StickGripHalfLength);
            return Vector3.Distance(palm,contact+line*along);
        }
        internal static float GripDistance(Matrix controller,Matrix attachedWrist) =>
            Vector3.Distance(CockpitHand.GripWrist(controller).Translation,attachedWrist.Translation);
        private static Matrix StickPalm(bool isLeft) =>
            IsSteering ? (YokeMode ? Rig.Wheel.YokePalm(isLeft,yokeRoll,yoke.Visual) : Rig.Wheel.Palm(isLeft,steering.VisualPosition,ThrottleVisual,barTilt.Visual)) :
            (isLeft ? Rig.Left:Rig.Right).Palm(isLeft)*(isLeft ? leftVisual:rightVisual);

        /// <summary>GripClaim's taker: a grip that starts by a stick (or on a stick held by a tap) is the cockpit's.</summary>
        internal static bool Take(Hand hand) => VRSettings.ColbyCockpit && seat!=null && (Held(hand) || NearGrip(hand));

        /// <summary>RenderDebug ColbyStickPlacement: 1 unlocks the sticks to be moved, 0 locks and saves them, -1 puts them back where the cockpit has them.</summary>
        internal enum PlacementRequest { None, Unlock, Lock, Reset }
        internal static volatile PlacementRequest Request;

        public static void Reset()
        {
            Release(); leftAngles=rightAngles=Vector3.Zero; seat=null; previousTuning=null; leftWas=rightWas=false; CockpitRender.Reset();
        }
        public static void Release()
        {
            if (Adjusting) placement.Cancel();
            left.Release(); right.Release();
            leftHover.Sample(false,0,0); rightHover.Sample(false,0,0);
            translation=rotation=Vector3.Zero; steering.Reset(); throttlePosition=0; throttle.Reset(); yoke.Reset(); yokeRoll=0; throttleCommand=ThrottleVisual=0;
            barTilt.Reset(); barCommand=Vector2.Zero;
            leftFilter.Update(false,Vector3.Zero,0,0); rightFilter.Update(false,Vector3.Zero,0,0);
            leftDetent.Sample(false,Vector3.Zero,DateTime.UtcNow); rightDetent.Sample(false,Vector3.Zero,DateTime.UtcNow);
            barDetent.Sample(false,Vector3.Zero,DateTime.UtcNow); barPulse=DateTime.MinValue;
            SetVisuals();
        }
        private static void SetVisuals()
        {
            var rig=Rig;
            if(IsSteering) { leftVisual=rightVisual=SteeringVisual; return; }
            leftVisual=StickPlacement.Visual(rig?.Left!=null ? rig.Left.Visual(leftAngles) : Matrix.Identity,placement.Left);
            rightVisual=StickPlacement.Visual(rig?.Right!=null ? rig.Right.Visual(rightAngles) : Matrix.Identity,placement.Right);
        }
        public static void ToggleAdjustment()
        {
            if (!CanAdjust) return;
            bool locking=Adjusting;
            if (locking) { placement.Lock(); SavePlacement(); }
            Release();
            if (!locking) placement.Unlock();
            Log.Info($"ColbyCockpit: sticks {(locking ? $"locked at left {placement.Left}, right {placement.Right}" : "unlocked; grip a stick to move it")}");
        }
        public static void ResetPlacement()
        {
            if (!CanAdjust) return;
            Release(); placement.Load(Vector3.Zero,Vector3.Zero); SetVisuals(); SavePlacement();
            Log.Info("ColbyCockpit: sticks put back where the cockpit has them");
        }

        // His Config.StickPlacements, one entry per subtype, held in our settings file as subtype:lx,ly,lz,rx,ry,rz;...
        private static void SavePlacement()
        {
            string subtype=seat.BlockDefinition.Id.SubtypeName;
            var entries=new List<string>();
            foreach(var pair in Placements())
                if(pair.Key!=subtype) entries.Add(Entry(pair.Key,pair.Value.Item1,pair.Value.Item2));
            entries.Add(Entry(subtype,placement.Left,placement.Right));
            VRSettings.ColbyStickPlacements=string.Join(";",entries);
            VRSettings.Save();
        }
        private static string Entry(string subtype,Vector3 l,Vector3 r) =>
            subtype+":"+string.Join(",",new[] {l.X,l.Y,l.Z,r.X,r.Y,r.Z}.Select(v => v.ToString("0.####",CultureInfo.InvariantCulture)));
        private static Dictionary<string,Tuple<Vector3,Vector3>> Placements()
        {
            var result=new Dictionary<string,Tuple<Vector3,Vector3>>();
            foreach(string item in (VRSettings.ColbyStickPlacements ?? "").Split(';'))
            {
                int colon=item.LastIndexOf(':');
                if(colon<=0) continue;
                string[] parts=item.Substring(colon+1).Split(',');
                if(parts.Length!=6) continue;
                var v=new float[6];
                bool ok=true;
                for(int i=0;i<6 && ok;i++) ok=float.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out v[i]);
                if(ok) result[item.Substring(0,colon).Trim()]=Tuple.Create(new Vector3(v[0],v[1],v[2]),new Vector3(v[3],v[4],v[5]));
            }
            return result;
        }

        private static bool Eligible(MyCockpit cockpit) => cockpit!=null && !cockpit.Closed && !cockpit.MarkedForClose &&
            ModelSupported(cockpit) &&
            cockpit.Pilot==MySession.Static?.LocalCharacter && cockpit.Pilot!=null && !cockpit.Pilot.IsDead &&
            MySession.Static.CameraController==cockpit && (cockpit.IsInFirstPersonView || cockpit.ForceFirstPersonCamera);

        private static bool ModelSupported(MyCockpit cockpit)
        {
            string subtype=cockpit.BlockDefinition.Id.SubtypeName,model=cockpit.BlockDefinition.InteriorModel ?? cockpit.BlockDefinition.Model;
            var rig=CockpitRig.Find(subtype);
            // Stick cockpits, handle cockpits and wheel cockpits (rovers, buggies, speeders), as his.
            if(rig==null || !(rig.HasSticks || rig.Handles.Length>0 || rig.Wheel!=null)) return false;
            if(rig.Matches(model)) return true;
            // A mod's cockpit registered as built on a game cockpit (SevrApi RegisterCockpit) whose model is another: its author is told once.
            if(!CockpitRig.HasControls(subtype) && unmatchedLogged.Add(subtype))
                Log.Warn($"SEVR API: the cockpit {subtype} is registered as built on the game's cockpit with the model {rig.Model}, but its model is {model ?? "none"}; "+
                    "the controls are measured on that model, so it has no VR controls (docs/MOD_API.md, Cockpits)");
            return false;
        }
        private static readonly HashSet<string> unmatchedLogged=new HashSet<string>(StringComparer.Ordinal);

        public static Matrix HandLocal(Hand hand) => SeatLocal(CockpitHand.GripTracking(hand));
        private static Matrix SeatLocal(Matrix tracking) => tracking*trackingToSeat;

        // Tracking space -> seat model space this frame: his PlayerToAbsolute inverse and seat head matrix, as our
        // tracking-to-body move for the pilot, then the pilot's model into the seat's. Made in doubles, narrowed once.
        private static bool UpdateTrackingToSeat()
        {
            if(!HandWorld.TryGetTrackingToBody(seat.Pilot,out MatrixD trackingToModel,out _)) return false;
            trackingToSeat=(Matrix)(trackingToModel*seat.Pilot.WorldMatrix*seat.PositionComp.WorldMatrixNormalizedInv);
            return trackingToSeat.IsValid();
        }

        public static MatrixD WristWorld(Hand hand)
        {
            Matrix local=CockpitHand.GripWrist(HandLocal(hand));
            if (Held(hand))
            {
                bool isLeft=hand==Hand.Left;
                Matrix attached=CockpitHand.WristForPalm(seat.Pilot,hand,StickPalm(isLeft));
                float t=MathHelper.Clamp((float)(DateTime.UtcNow-(isLeft ? grabLeft : grabRight)).TotalSeconds/0.12f,0,1);
                Quaternion q=Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(local),Quaternion.CreateFromRotationMatrix(attached),t);
                Matrix blended=Matrix.CreateFromQuaternion(q);
                blended.Translation=Vector3.Lerp(local.Translation,attached.Translation,t);
                local=blended;
            }
            return (MatrixD)local*seat.WorldMatrix;
        }

        /// <summary>
        /// His TrackedArms' seated wrist (WristWorld), for our HandIK: while <paramref name="hand"/> holds a stick, the palm
        /// bone's target in <paramref name="body"/>'s model space moves from where the controller puts it onto the stick's
        /// palm over his 0.12 s, and stays there. His WristForPalm puts the suit's palm bone on the stick's palm, so that palm
        /// is the target itself. True also tells HandIK to close the fingers round the stick (his FingerMode Stick, with
        /// <see cref="CockpitHand.StickFingers"/>). False (target unchanged) when the hand holds nothing of his.
        /// </summary>
        internal static bool PalmTarget(Hand hand,MyCharacter body,ref Matrix target)
        {
            if (!Held(hand) || !Seated(body) || !Eligible(seat) || !CockpitRender.Ready || body.PositionComp==null) return false;
            bool isLeft=hand==Hand.Left;
            if (!IsSteering && (isLeft ? Rig?.Left : Rig?.Right)==null) return false;
            Matrix attached=(Matrix)((MatrixD)StickPalm(isLeft)*seat.WorldMatrix*body.PositionComp.WorldMatrixNormalizedInv);
            if (!attached.IsValid()) return false;
            // Off the grip as far as the last frame found the knuckles in it (CockpitGrip).
            CockpitGrip.PushPalm(hand,ref attached);
            float t=MathHelper.Clamp((float)(DateTime.UtcNow-(isLeft ? grabLeft : grabRight)).TotalSeconds/0.12f,0,1);
            Quaternion q=Quaternion.Slerp(Quaternion.CreateFromRotationMatrix(target),Quaternion.CreateFromRotationMatrix(attached),t);
            Matrix blended=Matrix.CreateFromQuaternion(q);
            blended.Translation=Vector3.Lerp(target.Translation,attached.Translation,t);
            target=blended;
            return true;
        }

        /// <summary>
        /// The stick <paramref name="hand"/> holds, as drawn (<paramref name="pose"/>: its rest place to where it is now, in the
        /// cockpit's space), the cockpit and its rig (<see cref="CockpitGrip"/>). False when it holds none of his sticks; a
        /// steering wheel or yoke is not a stick.
        /// </summary>
        internal static bool TryHeldStick(Hand hand,out CockpitRig.Stick stick,out Matrix pose,out MyCockpit cockpit,out CockpitRig rig)
        {
            stick=null; pose=Matrix.Identity; cockpit=seat; rig=Rig;
            if(!Held(hand) || seat==null || rig==null || IsSteering) return false;
            bool isLeft=hand==Hand.Left;
            stick=isLeft ? rig.Left:rig.Right;
            pose=isLeft ? leftVisual:rightVisual;
            return stick!=null;
        }

        /// <summary>The VR controls are off this frame: let go of the seat.</summary>
        public static void Stop() { if (seat!=null) Reset(); }

        /// <summary>Once per input frame (VRControls), before the seat's input is worked out.</summary>
        public static void Update(bool menu)
        {
            menuOpen=menu;
            UpdateCore();
        }
        private static void UpdateCore()
        {
            var next=MySession.Static?.ControlledEntity as MyCockpit;
            if (!VRInput.Active || !VRSettings.ColbyCockpit || !CockpitRenderPatch.Patched || !Eligible(next))
            { if (seat!=null) Reset(); Request=PlacementRequest.None; return; }
            if (seat!=next)
            {
                Reset(); seat=next;
                Log.Info($"ColbyCockpit: seated in {seat.BlockDefinition.Id.SubtypeName}; checking its stick meshes");
                Placements().TryGetValue(seat.BlockDefinition.Id.SubtypeName,out var saved);
                placement.Load(saved==null ? Vector3.Zero : saved.Item1,saved==null ? Vector3.Zero : saved.Item2);
                SetVisuals();
            }
            if (SeatSpaceMoved()) Release();
            viewOk=UpdateTrackingToSeat();
            var nextTuning=Tuning();
            if(previousTuning==null || !previousTuning.Same(nextTuning))
            {
                if(previousTuning!=null) Release();
                previousTuning=nextTuning;
            }
            RefreshVisuals();
            PlacementRequest request=Request;
            if (request!=PlacementRequest.None && CanAdjust)
            {
                Request=PlacementRequest.None;
                if (request==PlacementRequest.Reset) ResetPlacement();
                else if ((request==PlacementRequest.Unlock)!=Adjusting) ToggleAdjustment();
            }
            bool available=Available;
            if (!available && Adjusting) Release();
            bool leftDown=VRInput.IsPressed(Hand.Left,VRButtons.Grip) && GripClaim.Owner(Hand.Left)==ClaimName;
            bool rightDown=VRInput.IsPressed(Hand.Right,VRButtons.Grip) && GripClaim.Owner(Hand.Right)==ClaimName;
            Matrix l=available ? HandLocal(Hand.Left) : Matrix.Identity;
            Matrix r=available ? HandLocal(Hand.Right) : Matrix.Identity;
            Vector3 lp=CockpitHand.Palm(l),rp=CockpitHand.Palm(r);
            float leftDistance=available ? GripDistance(Hand.Left):float.MaxValue;
            float rightDistance=available ? GripDistance(Hand.Right):float.MaxValue;
            if(leftHover.Sample(available,leftDistance,Reach,leftDown || left.Consumed || CockpitTouch.Owns(Hand.Left)))
                CockpitFeedback.Hover(Hand.Left);
            if(rightHover.Sample(available,rightDistance,Reach,rightDown || right.Consumed || CockpitTouch.OwnsRight))
                CockpitFeedback.Hover(Hand.Right);
            bool leftNear=available && NearDistance(true,leftDistance);
            bool rightNear=available && NearDistance(false,rightDistance);
            // A grip squeezed on a stick that something else took (a switch under the fingertip, a screen): said, so a stick that will not take can be traced.
            foreach(var hand in new[] { Hand.Left,Hand.Right })
                if((hand==Hand.Left ? leftNear:rightNear) && VRInput.IsNewPressed(hand,VRButtons.Grip) && GripClaim.Owner(hand)!=ClaimName)
                    Log.Info($"ColbyCockpit: {(hand==Hand.Left ? "left" : "right")} {(IsSteering ? "grip" : "stick")} squeezed {(hand==Hand.Left ? leftDistance:rightDistance)*100f:0.0} cm from it, but the grip went to {GripClaim.Owner(hand) ?? "nothing"}");
            double seconds=clock.Elapsed.TotalSeconds;
            if (left.Update(available && !CockpitTouch.Owns(Hand.Left),leftDown,leftNear,true,VRSettings.ColbyTapHoldSticks && !Adjusting,seconds))
            {
                grabLeft=DateTime.UtcNow; leftDetent.Sample(false,Vector3.Zero,grabLeft);
                leftStartOffset=placement.Left;
                leftNeutral=l; leftAngles=Vector3.Zero; CockpitHand.Vibrate(Hand.Left,0,0.055f,110,0.5f);
                Log.Info($"ColbyCockpit: left {(IsSteering ? "grip" : "stick")} taken {leftDistance*100f:0.0} cm from it (reach {Reach*100f:0} cm)");
            }
            if (right.Update(available && !CockpitTouch.OwnsRight,rightDown,rightNear,true,VRSettings.ColbyTapHoldSticks && !Adjusting,seconds))
            {
                grabRight=DateTime.UtcNow; rightDetent.Sample(false,Vector3.Zero,grabRight);
                rightStartOffset=placement.Right;
                rightNeutral=r; rightAngles=Vector3.Zero; CockpitHand.Vibrate(Hand.Right,0,0.055f,110,0.5f);
                Log.Info($"ColbyCockpit: right {(IsSteering ? "grip" : "stick")} taken {rightDistance*100f:0.0} cm from it (reach {Reach*100f:0} cm)");
            }
            if (left.Held!=leftWas || right.Held!=rightWas)
                Log.Info($"ColbyCockpit: left {(IsSteering ? "grip" : "stick")} {(left.Held ? "held" : "free")}, right {(IsSteering ? "grip" : "stick")} {(right.Held ? "held" : "free")}");
            tuning=nextTuning;
            float deadzone=tuning.TiltDeadzone;
            bool twist=VRSettings.ColbyStickTwist;
            float sensitivity=tuning.Rotation;
            float dt=(float)Math.Min(.05,Math.Max(0,seconds-lastInput)); lastInput=seconds;
            if (Adjusting)
            {
                if (left.Held) placement.Move(true,leftStartOffset,CockpitHand.Palm(leftNeutral),lp);
                if (right.Held) placement.Move(false,rightStartOffset,CockpitHand.Palm(rightNeutral),rp);
            }
            var now=DateTime.UtcNow;
            Vector3 feedbackTranslation,feedbackRotation;
            if(IsSteering)
            {
                // Positive model rotation turns left; native wheel X is positive to the right.
                float value=-steering.Update(Rig.Wheel,left.Held,lp,right.Held,rp,dt);
                bool pairWas=barTilt.Active;
                Matrix rawLeft=available ? SeatLocal(VRInput.Get(Hand.Left).Grip):Matrix.Identity;
                Matrix rawRight=available ? SeatLocal(VRInput.Get(Hand.Right).Grip):Matrix.Identity;
                barCommand=barTilt.Update(available && VehicleFlight && Rig.Wheel.ThrottleActor>=0 && tuning.BarTiltEnabled && left.Held && right.Held,
                    rawLeft,rawRight,Rig.Wheel,tuning,dt,Rig.Wheel.Visual(steering.Position));
                bool pairChanged=pairWas!=barTilt.Active;
                if(pairChanged)
                {
                    leftDetent.Sample(false,Vector3.Zero,now); rightDetent.Sample(false,Vector3.Zero,now);
                    barDetent.Sample(false,Vector3.Zero,now); barPulse=now.AddMilliseconds(80);
                    leftPulse=rightPulse=now.AddMilliseconds(80);
                    if(barTilt.Active) CockpitFeedback.Engage(grabLeft>grabRight ? Hand.Right:Hand.Left);
                }
                if(Rig.Wheel.ThrottleActor>=0)
                {
                    Matrix throttleFrame=barTilt.Active ? barTilt.ThrottleFrame(rawLeft,rawRight,Rig.Wheel):Rig.Wheel.Visual(steering.Position).GetOrientation();
                    throttlePosition=throttle.Update(right.Held,barTilt.Active,r,throttleFrame,Rig.Wheel.RightShaft,Rig.Wheel.ThrottleRange);
                    throttleCommand=rightFilter.Update(right.Held,new Vector3(CockpitStickMath.Axis((throttlePosition)*tuning.ThrottleSensitivity,tuning.TwistDeadzone),0,0),dt,tuning.Smoothing).X;
                    // His trigger (Primary, PointerPressure) twists the drawn throttle with no ship toolbar item selected.
                    float trigger=available && seat.Toolbar?.SelectedItem==null && VRInput.IsPressed(Hand.Right,VRButtons.Trigger) ? MathHelper.Clamp(VRInput.Get(Hand.Right).Trigger,0,1):0;
                    float target=Math.Max(right.Held ? throttleCommand:0,trigger);
                    ThrottleVisual=right.Held || trigger>0 ? CockpitStickMath.SmoothVisual(new Vector3(ThrottleVisual,0,0),new Vector3(target,0,0),dt).X:
                        CockpitStickMath.ReturnVisual(new Vector3(ThrottleVisual,0,0),dt).X;
                }
                feedbackTranslation=new Vector3(steering.Position,0,0);
                feedbackRotation=new Vector3(steering.Position,right.Held ? throttleCommand:0,0);
                if(barTilt.Active)
                {
                    feedbackTranslation=new Vector3(-CockpitStickMath.Axis(barTilt.Raw.X/tuning.BarPitchTravel,tuning.BarPitchDeadzone/tuning.BarPitchTravel),
                        CockpitStickMath.Axis(value*tuning.Translation,deadzone),CockpitStickMath.Axis(barTilt.Raw.Y/tuning.BarRollTravel,tuning.BarRollDeadzone/tuning.BarRollTravel));
                    feedbackRotation=feedbackTranslation; feedbackRotation.Y=throttleCommand;
                }
                float pitch=yoke.Update(YokeMode,left.Held,rawLeft.Translation,right.Held,rawRight.Translation,Rig.Wheel.Axis,tuning,dt);
                float wheelCommand=CockpitStickMath.Axis(value*Rig.Wheel.Range/tuning.WheelRollTravel,tuning.TiltDeadzone);
                float rollTarget=CockpitStickMath.Response(wheelCommand*tuning.RollSensitivity,tuning.RotationCurve)*tuning.WheelRollTravel/Rig.Wheel.Range;
                yokeRoll=YokeMode && (left.Held || right.Held) ? CockpitStickMath.SmoothVisual(new Vector3(yokeRoll,0,0),new Vector3(-rollTarget,0,0),dt).X:CockpitStickMath.ReturnVisual(new Vector3(yokeRoll,0,0),dt).X;
                if(YokeMode) { feedbackTranslation=new Vector3(CockpitStickMath.Response(pitch*tuning.PitchSensitivity,tuning.RotationCurve),0,CockpitStickMath.Response(wheelCommand*tuning.RollSensitivity,tuning.RotationCurve)); feedbackRotation=feedbackTranslation; }
                translation=leftFilter.Update(left.Held || right.Held,new Vector3(CockpitStickMath.Axis(value*tuning.Translation,deadzone),0,0),dt,tuning.Smoothing);
                rotation=Vector3.Zero;
            }
            else
            {
                translation=left.Held && !Adjusting && !SingleLeft ? CockpitStickMath.Translation(leftNeutral,l,deadzone,twist,tuning.Translation,Rig.Left.Frame,tuning.TwistDeadzone) : Vector3.Zero;
                rotation=Adjusting ? Vector3.Zero : SingleLeft && left.Held ? CockpitStickMath.Rotation(leftNeutral,l,deadzone,twist,sensitivity,Rig.Left.Frame,tuning.TwistDeadzone) : right.Held ? CockpitStickMath.Rotation(rightNeutral,r,deadzone,twist,sensitivity,Rig.Right.Frame,tuning.TwistDeadzone) : Vector3.Zero;
                translation=leftFilter.Update(left.Held && !Adjusting,translation,dt,tuning.Smoothing);
                rotation=rightFilter.Update((right.Held || SingleLeft && left.Held) && !Adjusting,rotation,dt,tuning.Smoothing);
                feedbackTranslation=CockpitStickMath.Response(translation,tuning.TranslationCurve);
                feedbackRotation=CockpitStickMath.Response(rotation,tuning.RotationCurve);
                if(Rig.Left!=null)
                    leftAngles=left.Held && !Adjusting ? CockpitStickMath.SmoothVisual(leftAngles,CockpitStickMath.HandVisual(leftNeutral,l,twist,SingleLeft ? sensitivity:tuning.Translation,Rig.Left.Frame),dt):CockpitStickMath.ReturnVisual(leftAngles,dt);
                if(Rig.Right!=null)
                    rightAngles=right.Held && !Adjusting ? CockpitStickMath.SmoothVisual(rightAngles,CockpitStickMath.HandVisual(rightNeutral,r,twist,sensitivity,Rig.Right.Frame),dt):CockpitStickMath.ReturnVisual(rightAngles,dt);
            }
            SetVisuals();
            int barEvent=barDetent.Sample(barTilt.Active || YokeMode && yoke.Active,new Vector3(feedbackTranslation.X,0,feedbackTranslation.Z),now);
            if(barEvent!=0)
            {
                if(left.Held) Pulse(Hand.Left,barEvent,ref leftPulse);
                if(right.Held) Pulse(Hand.Right,barEvent,ref rightPulse);
                barPulse=now.AddMilliseconds(130);
            }
            bool sharedFeedback=barTilt.Active || YokeMode && yoke.Active;
            var leftFeedback=sharedFeedback ? new Vector3(steering.Position,0,0):SingleLeft ? feedbackRotation:feedbackTranslation;
            var rightFeedback=sharedFeedback ? new Vector3(steering.Position,throttleCommand,0):feedbackRotation;
            Feedback(Hand.Left,left.Held && !Adjusting,leftFeedback,leftDetent,ref leftPulse,sharedFeedback && now<barPulse);
            Feedback(Hand.Right,right.Held && !Adjusting,rightFeedback,rightDetent,ref rightPulse,sharedFeedback && now<barPulse);
            CockpitFeedback.Motion(Hand.Left,left.Held && !Adjusting,sharedFeedback ? feedbackTranslation:SingleLeft ? feedbackRotation:feedbackTranslation,DateTime.UtcNow<leftPulse || (DateTime.UtcNow-grabLeft).TotalSeconds<.08);
            CockpitFeedback.Motion(Hand.Right,right.Held && !Adjusting,feedbackRotation,DateTime.UtcNow<rightPulse || (DateTime.UtcNow-grabRight).TotalSeconds<.08);
            if (leftWas && !left.Held) CockpitHand.Vibrate(Hand.Left,0,0.025f,75,0.2f);
            if (rightWas && !right.Held) CockpitHand.Vibrate(Hand.Right,0,0.025f,75,0.2f);
            leftWas=left.Held; rightWas=right.Held;
            RefreshVisuals();
        }
        // His UpdateCore lets go of the sticks when PlayerToAbsolute (a recentre) or SeatFit.Offset changes: a held stick's
        // neutral was taken in the seat space before the move. Ours recentres in GameHead, so its count stands in for his origin.
        private static MyCockpit spaceSeat;
        private static Vector3 spaceFit;
        private static int spaceCentre;
        private static bool SeatSpaceMoved()
        {
            bool moved=spaceSeat==seat && (spaceFit!=SeatFit.Offset || spaceCentre!=GameHead.Recentres);
            spaceSeat=seat; spaceFit=SeatFit.Offset; spaceCentre=GameHead.Recentres;
            return moved;
        }
        private static void Feedback(Hand hand,bool held,Vector3 axes,CockpitFeedback.StickPulse detent,ref DateTime next,bool suppressed=false)
        {
            var now=DateTime.UtcNow;
            int pulse=detent.Sample(held,axes,now);
            if(pulse==0 || suppressed) return;
            Pulse(hand,pulse,ref next);
        }
        private static void Pulse(Hand hand,int pulse,ref DateTime next)
        {
            bool center=pulse==1;
            CockpitHand.Vibrate(hand,0,center ? .018f : .045f,center ? 150 : 75,center ? .32f : .38f);
            next=DateTime.UtcNow.AddMilliseconds(130);
        }
        public static void RefreshVisuals()
        {
            if (seat==null || !Eligible(seat)) return;
            CockpitRender.Update(seat,leftVisual,rightVisual,left.Held,right.Held,placement.Left,placement.Right);
        }

        // His FlightSettings.For(seat): this cockpit's own tuning, or the defaults for one with none.
        private static FlightTuning Tuning() => FlightSettings.For(seat);

        /// <summary>
        /// His ApplyFlight: the wheel, yoke and bar branch, then the stick branch. <paramref name="leftStick"/> and <paramref name="rightStick"/> are the thumbsticks
        /// (+Y forward) standing in for his ThrustLRFB and ThrustRotate bindings.
        /// </summary>
        public static void ApplyFlight(float speed,Vector2 leftStick,Vector2 rightStick,ref Vector3 move,ref Vector2 rotate,ref float roll)
        {
            if (seat==null || !Eligible(seat) || !CockpitRender.Ready) return;
            if (Adjusting) { move=Vector3.Zero; rotate=Vector2.Zero; roll=0; return; }
            if(IsSteering)
            {
                bool held=left.Held || right.Held;
                bool useBarTilt=barTilt.Active;
                var thumb=right.Held ? rightStick:Vector2.Zero;
                if(YokeMode)
                {
                    float steer=-steering.Position*Rig.Wheel.Range/tuning.WheelRollTravel;
                    CockpitStickMath.ApplyYoke(tuning,left.Consumed || right.Consumed,held,right.Held,CockpitStickMath.Axis(steer,tuning.TiltDeadzone),yoke.Command,
                        thumb,speed,ShipRollSensitivity,ref move,ref rotate,ref roll);
                    return;
                }
                CockpitStickMath.ApplySteering(tuning,VehicleFlight,left.Consumed || right.Consumed,held,right.Held,useBarTilt,
                    translation.X,Throttle,barCommand,thumb,speed,ShipRollSensitivity,ref move,ref rotate,ref roll);
                return;
            }
            bool singleLeft=SingleLeft;
            CockpitStickMath.ApplyFlight(left.Consumed && !singleLeft,right.Consumed || singleLeft && left.Consumed,left.Held ? translation : Vector3.Zero,right.Held || singleLeft && left.Held ? rotation : Vector3.Zero,
                left.Held && !singleLeft ? leftStick.Y : 0,OwnsRightThumb ? rightStick.X : 0,speed,ShipRollSensitivity,ref move,ref rotate,ref roll,tuning.RotationCurve,tuning.TranslationCurve,left.Held && !singleLeft ? leftStick.X:0);
        }
    }
}

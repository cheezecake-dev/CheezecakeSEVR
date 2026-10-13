// Adapted from ColbyJack1134's SpaceEngineersVR (Player/InteractionInput.cs), with his permission.
// His near-hand reading only (the far, laser one is his spatial UI's). His grip and trigger pressures become VRInput's analog
// Squeeze and Trigger; a grip another feature holds (GripClaim) reads as released. His Consume, which blocked the hand's
// flight bindings until release, becomes: the trigger is held back from the game (VRControls.SwallowTrigger), and the grip is
// the switches' through their GripClaim, taken while the fingertip is on a switch.
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input;

namespace SpaceEngineersVR.Cockpits
{
    internal readonly struct InteractionInput
    {
        internal const float GripThreshold=.8f;
        internal readonly bool Near,Right,Down,CanAcquire,Grip;
        internal readonly float Pressure;
        private readonly float gripPressure,triggerPressure;
        private readonly bool gripReady,triggerReady;
        internal InteractionInput(bool near,bool right,float pressure,bool down,bool canAcquire)
        {
            Near=near; Right=right; Pressure=pressure; Down=down; CanAcquire=canAcquire; Grip=near;
            gripPressure=near ? pressure:0; triggerPressure=near ? 0:pressure;
            gripReady=near && canAcquire; triggerReady=!near && canAcquire;
        }
        internal InteractionInput(bool right,float grip,float trigger,bool gripReady,bool triggerReady,bool? source=null)
        {
            Near=true; Right=right; gripPressure=grip; triggerPressure=trigger;
            this.gripReady=gripReady; this.triggerReady=triggerReady;
            Grip=source ?? (grip>GripThreshold || trigger<=.55f && grip>=trigger);
            Pressure=Grip ? grip:trigger;
            Down=Pressure>(Grip ? GripThreshold:.55f);
            CanAcquire=Grip ? gripReady:triggerReady;
        }
        internal InteractionInput Select(bool grip) => new InteractionInput(Right,gripPressure,triggerPressure,gripReady,triggerReady,grip);
        internal static InteractionInput Read(Hand hand)
        {
            HandState state=VRInput.Get(hand);
            bool gripDown=VRInput.IsPressed(hand,VRButtons.Grip);
            bool gripFree=!gripDown || GripClaim.Owner(hand)==CockpitTouch.ClaimName;
            float grip=gripFree ? Analog(state.Squeeze,gripDown) : 0;
            float trigger=Analog(state.Trigger,VRInput.IsPressed(hand,VRButtons.Trigger));
            return new InteractionInput(hand==Hand.Right,grip,trigger,gripFree,true);
        }
        // A source with only the button says all or nothing.
        private static float Analog(float analog,bool pressed) => analog>0 ? analog : pressed ? 1 : 0;
        internal void Consume()
        {
            Hand hand=Right ? Hand.Right:Hand.Left;
            VRControls.SwallowTrigger(hand,$"the {hand.ToString().ToLowerInvariant()} trigger working a cockpit switch");
        }
    }
}

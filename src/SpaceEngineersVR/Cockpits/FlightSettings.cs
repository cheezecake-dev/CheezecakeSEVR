// Adapted from ColbyJack1134's SpaceEngineersVR (Player/FlightSettings.cs), with his permission.
// Phase 8 of docs/COCKPIT_PORT.md. His pages, their keys, ranges, units and Apply/Cancel/Reset are his (Layout and AddSlider,
// near verbatim). His window was a SurfaceView painted by his own overlay over the hands, on a floating MenuWindow or the wrist;
// here the same layout is a game GUI screen (FlightSettingsScreen), which our plugin shows in VR as the menu panel and the laser
// points at. Opened from the quick menu's Seat page (SeatPage). His For(seat) read the tuning from the cockpit's shared memory (MultiplayerRuntime);
// here it is kept locally per world and cockpit, his own LocalOnly path, in VRSettings.ColbyFlightTunings. A cockpit with none of
// its own starts from FlightTuning's defaults with its three vehicle switches from our settings (colbyVehicleFlight, colbyBarTilt,
// colbyWheelMotion). Not carried: the wrist copy (OnWrist), the window drag (UpdateWindow, Floating). His TapHoldLevers (phase 6)
// is VRSettings.ColbyTapHoldLevers, on the Grip page as his.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using SpaceEngineersVR.Gui;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class FlightSettings
    {
        private static FlightSettingsScreen screen;
        internal static bool IsOpen => screen!=null;

        internal static string Title(MyCockpit owner) => (owner?.CustomName?.ToString() ?? "Cockpit")+
            (CockpitRig.Find(owner?.BlockDefinition.Id.SubtypeName)?.Wheel!=null ? " Controls":" Flight Settings");

        // ---- His For(seat), kept locally per world and cockpit ----

        private static FlightTuning cached;
        private static long cachedSeat;
        private static string cachedWorld,cachedText;
        private static bool cachedFlight,cachedBars,cachedWheel;

        /// <summary>A cockpit with no tuning of its own: FlightTuning's defaults, with its vehicle switches from our settings.</summary>
        internal static FlightTuning Defaults() => new FlightTuning {
            FlightMode=VRSettings.ColbyVehicleFlight,BarTiltEnabled=VRSettings.ColbyBarTilt,WheelMotion=VRSettings.ColbyWheelMotion };

        /// <summary>His For(seat). Called every input frame: the same instance comes back until the tuning or the settings change.</summary>
        internal static FlightTuning For(MyCockpit seat)
        {
            if(seat==null) return Defaults();
            string world=MySession.Static?.CurrentPath ?? "",text=VRSettings.ColbyFlightTunings;
            bool flight=VRSettings.ColbyVehicleFlight,bars=VRSettings.ColbyBarTilt,wheel=VRSettings.ColbyWheelMotion;
            if(cached!=null && cachedSeat==seat.EntityId && cachedWorld==world && ReferenceEquals(cachedText,text) &&
                cachedFlight==flight && cachedBars==bars && cachedWheel==wheel)
                return cached;
            FlightTuning stored=null;
            string encoded=Entries(text).FirstOrDefault(e=>e.World==world && e.Cockpit==seat.EntityId).Tuning;
            if(encoded!=null)
                try { stored=FlightTuning.Decode(encoded); }
                catch(Exception ex) { Log.Warn("ColbyCockpit: the flight settings of this cockpit could not be read and are left at their defaults: "+ex.Message); }
            cached=stored ?? Defaults(); cachedSeat=seat.EntityId; cachedWorld=world; cachedText=text;
            cachedFlight=flight; cachedBars=bars; cachedWheel=wheel;
            return cached;
        }

        /// <summary>His MultiplayerRuntime.SaveFlight, on his LocalOnly path: this world's cockpit keeps the tuning.</summary>
        internal static void Save(MyCockpit seat,FlightTuning value)
        {
            string world=MySession.Static?.CurrentPath ?? "",encoded=value.Encode();
            var entries=Entries(VRSettings.ColbyFlightTunings).Where(e=>e.World!=world || e.Cockpit!=seat.EntityId).ToList();
            entries.Add((world,seat.EntityId,encoded));
            VRSettings.ColbyFlightTunings=string.Join(";",entries.Select(e=>Convert.ToBase64String(Encoding.UTF8.GetBytes(e.World))+","+e.Cockpit+","+e.Tuning));
            VRSettings.Save();
            Log.Info($"ColbyCockpit: flight settings saved for {seat.BlockDefinition.Id.SubtypeName} {seat.EntityId}");
        }

        // world,cockpit,tuning per entry, entries split by ';': the world path in base64, the tuning as FlightTuning.Encode gives it.
        private static IEnumerable<(string World,long Cockpit,string Tuning)> Entries(string text)
        {
            foreach(string item in (text ?? "").Split(new[] {';'},StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts=item.Split(',');
                if(parts.Length!=3 || !long.TryParse(parts[1],out long cockpit)) continue;
                string world;
                try { world=Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])); }
                catch(FormatException) { continue; }
                yield return (world,cockpit,parts[2]);
            }
        }

        // ---- His Open / Close / Check ----

        /// <summary>His Open: his seat panel's key 4, here the quick menu's Seat page (SeatPage).</summary>
        internal static void Open()
        {
            var owner=SeatFit.Seat;
            if(!SeatFit.Eligible(owner) || screen!=null) return;
            try
            {
                CockpitControls.Release(); CockpitTouch.Reset();
                screen=new FlightSettingsScreen(owner,For(owner).Copy());
                MyGuiSandbox.AddScreen(screen);
                Log.Info($"ColbyCockpit: flight settings open for {owner.BlockDefinition.Id.SubtypeName}");
            }
            catch(Exception ex)
            {
                screen=null; Log.Warn("ColbyCockpit: the flight settings failed to open: "+ex);
                try { MyAPIGateway.Utilities?.ShowNotification("Flight settings unavailable",2000); } catch(Exception) { }
            }
        }
        internal static void Closed(FlightSettingsScreen which) { if(ReferenceEquals(screen,which)) screen=null; }
        /// <summary>His Check: the screen closes when the player is no longer in the cockpit it was opened for.</summary>
        internal static bool StillValid(MyCockpit owner) => ReferenceEquals(owner,SeatFit.Seat) && SeatFit.Eligible(owner);

        // ---- His Layout and AddSlider: keys in 0-1 across and down his panel ----

        internal sealed class Key
        {
            internal string Label,Value;
            internal float X,Y,W,H;
            internal bool Active,Caption,IsSlider;
            internal Action Action;
            // A slider: its value in shown units, its range in shown units, and what it sets.
            internal float Shown,Min,Max;
            internal string Unit;
            internal Action<float> Change;
        }

        internal static List<Key> Layout(FlightTuning value,bool personal,bool sticks,string subtype,int page,Action<int> setPage,
            Action<bool> setPersonal,Action toggleSticks,bool levers=false,Action toggleLevers=null)
        {
            var keys=new List<Key>();
            bool vehicle=CockpitRig.Find(subtype)?.Wheel!=null;
            bool saddle=CockpitRig.Find(subtype)?.Wheel?.ThrottleActor>=0;
            Action<string,float,float,float,float,Action,bool> button=(label,x,y,w,h,action,active)=>keys.Add(new Key {Label=label,X=x,Y=y,W=w,H=h,Action=action,Active=active});
            Action<string,float,float,float,float> caption=(label,x,y,w,h)=>keys.Add(new Key {Label=label,X=x,Y=y,W=w,H=h,Caption=true});
            if(vehicle)
            {
                float width=.23f,step=.24f;
                button("Steering",.025f,.12f,width,.075f,()=>{setPersonal(false); setPage(0);},!personal && page==0);
                button("Flight",.025f+step,.12f,width,.075f,()=>{setPersonal(false); setPage(1);},!personal && page==1);
                button(saddle ? "Bars":"Motion",.025f+step*2,.12f,width,.075f,()=>{setPersonal(false); setPage(2);},!personal && page==2);
                button("Grip",.025f+step*3,.12f,width,.075f,()=>setPersonal(true),personal);
            }
            else
            {
                button("Tuning",.025f,.12f,.47f,.075f,()=>setPersonal(false),!personal);
                button("Grip",.505f,.12f,.47f,.075f,()=>setPersonal(true),personal);
            }
            if(personal)
            {
                button(vehicle ? "Tap to hold steering":"Tap to hold joysticks",.035f,.29f,.93f,.12f,toggleSticks,sticks);
                keys[keys.Count-1].Value=sticks ? "On":"Off";
                if(toggleLevers!=null)
                {
                    button("Tap to hold analog levers",.035f,.46f,.93f,.12f,toggleLevers,levers);
                    keys[keys.Count-1].Value=levers ? "On":"Off";
                }
            }
            else if(vehicle && page==0)
            {
                AddSlider(keys,"Steering sensitivity",.035f,.25f,value.Translation,.25f,2,100,"%",v=>value.Translation=v);
                AddSlider(keys,"Steering deadzone",.525f,.25f,value.TiltDeadzone,0,.35f,100,"%",v=>value.TiltDeadzone=v);
                button("Squared",.035f,.415f,.215f,.08f,()=>value.TranslationCurve=2,value.TranslationCurve==2);
                button("Linear",.265f,.415f,.215f,.08f,()=>value.TranslationCurve=1,value.TranslationCurve==1);
                AddSlider(keys,"Input smoothing",.525f,.405f,value.Smoothing,0,.1f,1000," ms",v=>value.Smoothing=v);
                if(saddle)
                {
                    AddSlider(keys,"Throttle sensitivity",.035f,.58f,value.ThrottleSensitivity,.25f,2,100,"%",v=>value.ThrottleSensitivity=v);
                    AddSlider(keys,"Throttle deadzone",.525f,.58f,value.TwistDeadzone,0,.35f,100,"%",v=>value.TwistDeadzone=v);
                }
            }
            else if(vehicle && page==2 && saddle)
            {
                button("Two-hand pitch / roll",.035f,.225f,.93f,.09f,()=>value.BarTiltEnabled=!value.BarTiltEnabled,value.BarTiltEnabled);
                keys[keys.Count-1].Value=value.BarTiltEnabled ? "On":"Off";
                AddSlider(keys,"Pitch travel",.035f,.35f,value.BarPitchTravel,.02f,.05f,100," cm",v=>{value.BarPitchTravel=v; value.BarPitchDeadzone=Math.Min(value.BarPitchDeadzone,v*.5f);});
                AddSlider(keys,"Pitch deadzone",.525f,.35f,value.BarPitchDeadzone,0,.015f,1000," mm",v=>value.BarPitchDeadzone=v);
                AddSlider(keys,"Roll travel",.035f,.51f,value.BarRollTravel,MathHelper.ToRadians(5),MathHelper.ToRadians(10),180f/MathHelper.Pi,"°",v=>{value.BarRollTravel=v; value.BarRollDeadzone=Math.Min(value.BarRollDeadzone,v*.5f);});
                AddSlider(keys,"Roll deadzone",.525f,.51f,value.BarRollDeadzone,0,MathHelper.ToRadians(4),180f/MathHelper.Pi,"°",v=>value.BarRollDeadzone=v);
                caption("Flight only. Grip both bars to set neutral.",.035f,.69f,.93f,.055f);
                caption("Lift both for pitch up; tilt the bar for roll.",.035f,.76f,.93f,.055f);
            }
            else if(vehicle && page==2)
            {
                button("Wheel motion pitch / roll",.035f,.225f,.93f,.09f,()=>value.WheelMotion=!value.WheelMotion,value.WheelMotion);
                keys[keys.Count-1].Value=value.WheelMotion ? "On":"Off";
                AddSlider(keys,"Pitch travel",.035f,.35f,value.WheelPitchTravel,.02f,.06f,100," cm",v=>{value.WheelPitchTravel=v; value.WheelPitchDeadzone=Math.Min(value.WheelPitchDeadzone,v*.5f);});
                AddSlider(keys,"Pitch deadzone",.525f,.35f,value.WheelPitchDeadzone,0,.015f,1000," mm",v=>value.WheelPitchDeadzone=v);
                AddSlider(keys,"Roll travel",.035f,.51f,value.WheelRollTravel,MathHelper.ToRadians(15),MathHelper.ToRadians(60),180f/MathHelper.Pi,"°",v=>value.WheelRollTravel=v);
                AddSlider(keys,"Roll deadzone",.525f,.51f,value.TiltDeadzone,0,.35f,100,"%",v=>value.TiltDeadzone=v);
                caption("Flight only. Pull for pitch up; turn for roll.",.035f,.69f,.93f,.055f);
                caption("Right thumb left / right controls yaw.",.035f,.76f,.93f,.055f);
            }
            else if(vehicle)
            {
                button("Driving",.035f,.225f,.44f,.075f,()=>value.FlightMode=false,!value.FlightMode);
                button("Flight",.525f,.225f,.44f,.075f,()=>value.FlightMode=true,value.FlightMode);
                AddSlider(keys,"Yaw sensitivity",.035f,.335f,value.Rotation,.25f,2,100,"%",v=>value.Rotation=v);
                AddSlider(keys,"Pitch sensitivity",.525f,.335f,value.PitchSensitivity,.25f,2,100,"%",v=>value.PitchSensitivity=v);
                AddSlider(keys,"Roll sensitivity",.035f,.505f,value.RollSensitivity,.25f,2,100,"%",v=>value.RollSensitivity=v);
                button("Squared",.525f,.535f,.215f,.08f,()=>value.RotationCurve=2,value.RotationCurve==2);
                button("Linear",.755f,.535f,.215f,.08f,()=>value.RotationCurve=1,value.RotationCurve==1);
            }
            else
            {
                caption("ROTATION",.035f,.215f,.44f,.05f);
                caption("TRANSLATION",.525f,.215f,.44f,.05f);
                AddSlider(keys,"Sensitivity",.035f,.28f,value.Rotation,.25f,2,100,"%",v=>value.Rotation=v);
                AddSlider(keys,"Sensitivity",.525f,.28f,value.Translation,.25f,2,100,"%",v=>value.Translation=v);
                button("Squared",.035f,.425f,.215f,.08f,()=>value.RotationCurve=2,value.RotationCurve==2);
                button("Linear",.265f,.425f,.215f,.08f,()=>value.RotationCurve=1,value.RotationCurve==1);
                button("Squared",.525f,.425f,.215f,.08f,()=>value.TranslationCurve=2,value.TranslationCurve==2);
                button("Linear",.755f,.425f,.215f,.08f,()=>value.TranslationCurve=1,value.TranslationCurve==1);
                AddSlider(keys,"Tilt deadzone",.035f,.535f,value.TiltDeadzone,0,.35f,100,"%",v=>value.TiltDeadzone=v);
                AddSlider(keys,"Twist deadzone",.525f,.535f,value.TwistDeadzone,0,.35f,100,"%",v=>value.TwistDeadzone=v);
                AddSlider(keys,"Input smoothing",.035f,.69f,value.Smoothing,0,.1f,1000," ms",v=>value.Smoothing=v);
            }
            return keys;
        }
        // His AddSlider: the value is shown and stepped in whole units of `unit` (his Math.Round(v*scale)/scale), kept inside his range
        // so FlightTuning.Valid holds after the round trip through degrees.
        private static void AddSlider(List<Key> keys,string label,float x,float y,float value,float min,float max,float scale,string unit,Action<float> change)
        {
            keys.Add(new Key {Label=label,X=x,Y=y,W=.44f,H=.12f,IsSlider=true,Shown=(float)Math.Round(value*scale),
                Min=(float)Math.Round(min*scale),Max=(float)Math.Round(max*scale),Unit=unit,Change=shown=>change(MathHelper.Clamp(shown/scale,min,max))});
        }
    }

    /// <summary>
    /// His flight settings window as a game GUI screen: our plugin shows it as the menu panel, so the hands are drawn in front of it
    /// as of any menu. His key layout (0-1 across and down his panel) is placed onto the screen's area below the caption.
    /// </summary>
    internal sealed class FlightSettingsScreen : MyGuiScreenBase
    {
        // His window was .55 m wide and .31 m high.
        private static readonly Vector2 ScreenSize=new Vector2(.80f,.80f*.31f/.55f*1600f/1200f+.06f);
        private const float Head=.07f,TextScale=.8f;

        private readonly MyCockpit owner;
        private FlightTuning draft;
        private bool personal,stickHold,leverHold;
        private int vehiclePage;
        private bool rebuild;

        internal FlightSettingsScreen(MyCockpit owner,FlightTuning draft)
            : base(new Vector2(.5f,.5f),MyGuiConstants.SCREEN_BACKGROUND_COLOR,ScreenSize,false,null,
                   MySandboxGame.Config.UIBkOpacity,MySandboxGame.Config.UIOpacity)
        {
            this.owner=owner; this.draft=draft; stickHold=VRSettings.ColbyTapHoldSticks; leverHold=VRSettings.ColbyTapHoldLevers;
            EnabledBackgroundFade=true;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "ColbyFlightSettingsScreen";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            AddCaption(FlightSettings.Title(owner),null,new Vector2(0f,.003f));
            Vector2 size=Size ?? ScreenSize;
            float left=-size.X/2f,top=-size.Y/2f+Head,width=size.X,height=size.Y-Head;
            Func<float,float,Vector2> at=(x,y)=>new Vector2(left+x*width,top+y*height);

            var keys=FlightSettings.Layout(draft,personal,stickHold,owner.BlockDefinition.Id.SubtypeName,vehiclePage,
                page=>{vehiclePage=page; rebuild=true;},value=>{personal=value; rebuild=true;},()=>{stickHold=!stickHold; rebuild=true;},
                leverHold,()=>{leverHold=!leverHold; rebuild=true;});
            foreach(var key in keys)
            {
                if(key.Caption)
                    Controls.Add(new MyGuiControlLabel(at(key.X,key.Y+key.H/2),new Vector2(key.W*width,key.H*height),key.Label,null,TextScale,"White",
                        MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
                else if(key.IsSlider)
                    AddSlider(key,at(key.X,key.Y),key.W*width,key.H*height);
                else
                {
                    var k=key;
                    AddButton(key.Value==null ? key.Label:key.Label+": "+key.Value,at(key.X,key.Y),new Vector2(key.W*width,key.H*height),key.Active,
                        ()=>{k.Action(); rebuild=true;});
                }
            }
            // His bottom row.
            AddButton("Reset defaults",at(.035f,.875f),new Vector2(.25f*width,.085f*height),false,()=>
            {
                if(personal) stickHold=leverHold=false; else draft=FlightSettings.Defaults();
                rebuild=true;
            });
            AddButton("Cancel",at(.65f,.875f),new Vector2(.15f*width,.085f*height),false,()=>CloseScreen());
            AddButton("Apply",at(.815f,.875f),new Vector2(.15f*width,.085f*height),true,Apply);
            CloseButtonEnabled=true;
        }

        private void AddButton(string text,Vector2 at,Vector2 size,bool active,Action pressed)
        {
            var button=new MyGuiControlButton(at,MyGuiControlButtonStyleEnum.Rectangular,size,null,MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP,
                null,new StringBuilder(text),TextScale,MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,MyGuiControlHighlightType.WHEN_CURSOR_OVER,
                sender=>Guard(text,pressed));
            button.CustomStyle=DatapadScreen.ButtonStyle;
            button.Size=size;
            button.Checked=active;
            button.ColorMask=active ? Vector4.One:DatapadScreen.DimColor;
            Controls.Add(button);
        }

        private void AddSlider(FlightSettings.Key key,Vector2 at,float width,float height)
        {
            var label=new MyGuiControlLabel(at+new Vector2(0,height*.25f),null,key.Label+": "+key.Shown+key.Unit,null,TextScale,"White",
                MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
            var slider=new MyGuiControlSlider(at+new Vector2(0,height*.7f),key.Min,key.Max,width*.95f,key.Shown,null,"{0}",0,TextScale,0f,"White",null,
                MyGuiControlSliderStyleEnum.Default,MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER,true,false)
            { Value=key.Shown };
            slider.ValueChanged=changed=>Guard(key.Label,()=>
            {
                float shown=(float)Math.Round(changed.Value);
                key.Change(shown);
                label.Text=key.Label+": "+shown+key.Unit;
            });
            Controls.Add(label);
            Controls.Add(slider);
        }

        /// <summary>His Apply: the cockpit keeps the tuning if it changed, and the grip page's choice is saved.</summary>
        private void Apply()
        {
            if(draft.Encode()!=FlightSettings.For(owner).Encode()) FlightSettings.Save(owner,draft);
            if(stickHold!=VRSettings.ColbyTapHoldSticks || leverHold!=VRSettings.ColbyTapHoldLevers)
            { VRSettings.ColbyTapHoldSticks=stickHold; VRSettings.ColbyTapHoldLevers=leverHold; VRSettings.Save(); }
            CloseScreen();
        }

        public override bool Update(bool hasFocus)
        {
            if(!FlightSettings.StillValid(owner)) { CloseScreen(); return base.Update(hasFocus); }
            if(rebuild) { rebuild=false; Guard("page",()=>RecreateControls(false)); }
            return base.Update(hasFocus);
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            FlightSettings.Closed(this);
        }

        private static void Guard(string what,Action action) => Log.Guard("ColbyFlightSettings",what,action);
    }
}

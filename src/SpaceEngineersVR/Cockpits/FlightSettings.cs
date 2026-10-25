// Adapted from ColbyJack1134's SpaceEngineersVR (Player/FlightSettings.cs), with his permission.
// Phase 8 of docs/COCKPIT_PORT.md. His pages, their keys, ranges, units and Apply/Cancel/Reset are his (Layout and AddSlider,
// near verbatim). His window was a SurfaceView painted by his own overlay over the hands, on a floating MenuWindow or the wrist;
// here the same layout is a game GUI screen (FlightSettingsScreen), which our plugin shows in VR as the menu panel and the laser
// points at. Opened from the quick menu's Seat page (SeatPage). His For(seat) read the tuning from the cockpit's shared memory (MultiplayerRuntime);
// here it is kept locally per world and cockpit, his own LocalOnly path, in VRSettings.ColbyFlightTunings. A cockpit with none of
// its own starts from FlightTuning's defaults with its three vehicle switches from our settings (colbyVehicleFlight, colbyBarTilt,
// colbyWheelMotion). Not carried: the wrist copy (OnWrist), the window drag (UpdateWindow, Floating). His Grip page is the Hands
// page, with his TapHoldSticks and TapHoldLevers (phase 6) and our twist and thumbstick settings beside them, so every cockpit
// choice is changed from the seat; a Levers page (ours) sets each throttle lever to spring back to zero or stay where let go, and a
// Screens page (ours) sets what each of the cockpit's screens shows, from the same list as the game's own screen settings.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using SpaceEngineersVR.Gui;
using VRage;
using VRage.Game;
using VRage.Game.GUI.TextPanel;
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

        /// <summary>The page shown: one of the cockpit's tuning pages (a vehicle has three), its levers, or the grip (the player's own, every cockpit).</summary>
        internal const int LeversPage=10,GripPage=11,ScreensPage=12;

        /// <summary>A Screens page row: the screen's name, what it shows, and a step back or on through the game's list.</summary>
        internal sealed class ScreenChoice
        {
            internal string Name,Shows;
            internal Action Previous,Next;
        }

        /// <summary>The Grip page's switches, the player's own for every cockpit: name, what it does, its state, and its change.</summary>
        internal sealed class Choice
        {
            internal string Label;
            internal bool On;
            internal Action Toggle;
        }

        internal static List<Key> Layout(FlightTuning value,string subtype,int page,Action<int> setPage,IList<Choice> grip,
            IList<(int Slot,string Name)> throttles,bool hasHandles,Func<int,bool> holds,Action<int> toggleHold,IList<ScreenChoice> screens=null)
        {
            var keys=new List<Key>();
            bool vehicle=CockpitRig.Find(subtype)?.Wheel!=null;
            bool saddle=CockpitRig.Find(subtype)?.Wheel?.ThrottleActor>=0;
            Action<string,float,float,float,float,Action,bool> button=(label,x,y,w,h,action,active)=>keys.Add(new Key {Label=label,X=x,Y=y,W=w,H=h,Action=action,Active=active});
            Action<string,float,float,float,float> caption=(label,x,y,w,h)=>keys.Add(new Key {Label=label,X=x,Y=y,W=w,H=h,Caption=true});
            var tabs=new List<(string Label,int Page)>();
            if(vehicle) { tabs.Add(("Steering",0)); tabs.Add(("Flight",1)); tabs.Add((saddle ? "Bars":"Motion",2)); }
            else tabs.Add(("Tuning",0));
            if(hasHandles) tabs.Add(("Levers",LeversPage));
            if(screens?.Count>0) tabs.Add(("Screens",ScreensPage));
            tabs.Add(("Hands",GripPage));
            float tabStep=.95f/tabs.Count;
            for(int t=0;t<tabs.Count;t++)
            {
                int target=tabs[t].Page;
                button(tabs[t].Label,.025f+tabStep*t,.12f,tabStep-.01f,.075f,()=>setPage(target),page==target);
            }
            if(page==GripPage)
            {
                for(int i=0;i<grip.Count;i++)
                {
                    var choice=grip[i];
                    button(choice.Label,.035f,.225f+i*.105f,.93f,.09f,choice.Toggle,choice.On);
                    keys[keys.Count-1].Value=choice.On ? "On":"Off";
                }
                caption("These are yours, in every cockpit.",.035f,.76f,.93f,.055f);
            }
            else if(page==ScreensPage && screens!=null)
            {
                caption("What each screen shows, as in the game's screen settings.",.035f,.215f,.93f,.05f);
                for(int i=0;i<screens.Count && i<7;i++)
                {
                    var row=screens[i];
                    float y=.28f+i*.085f;
                    caption(row.Name,.035f,y,.36f,.075f);
                    button("<",.40f,y,.075f,.075f,row.Previous,false);
                    button(row.Shows,.485f,y,.40f,.075f,row.Next,true);
                    button(">",.895f,y,.075f,.075f,row.Next,false);
                }
            }
            else if(page==LeversPage)
            {
                if(throttles.Count==0)
                {
                    caption("No handle here works a thrust override yet.",.035f,.26f,.93f,.055f);
                    caption("Assign one: touch a handle and tap Y,",.035f,.33f,.93f,.055f);
                    caption("or Assign switches on the quick menu's Cockpit page.",.035f,.40f,.93f,.055f);
                }
                else
                {
                    caption("A throttle lever let go goes back to zero thrust.",.035f,.215f,.93f,.05f);
                    for(int i=0;i<throttles.Count && i<7;i++)
                    {
                        int slot=throttles[i].Slot;
                        bool stays=holds(slot);
                        button(throttles[i].Name,.035f,.28f+i*.085f,.93f,.075f,()=>toggleHold(slot),!stays);
                        keys[keys.Count-1].Value=stays ? "Stays where let go":"Springs back to zero";
                    }
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

        /// <summary>The colour of a button that is not the one chosen.</summary>
        private static readonly Vector4 DimColor=new Vector4(0.6f,0.66f,0.7f,1f);

        private readonly MyCockpit owner;
        private FlightTuning draft;
        private bool stickHold,leverHold,twist,thumbsticks;
        private int page;
        private bool[] holds;
        // The Screens page: each screen's choice as an index into contents (0 none, 1 text and images, then the scripts).
        private readonly int[] shows,shown;
        private readonly List<(string Name,ContentType Type,string Script)> contents=Contents();
        private bool rebuild;

        internal FlightSettingsScreen(MyCockpit owner,FlightTuning draft)
            : base(new Vector2(.5f,.5f),MyGuiConstants.SCREEN_BACKGROUND_COLOR,ScreenSize,false,null,
                   MySandboxGame.Config.UIBkOpacity,MySandboxGame.Config.UIOpacity)
        {
            this.owner=owner; this.draft=draft; stickHold=VRSettings.ColbyTapHoldSticks; leverHold=VRSettings.ColbyTapHoldLevers;
            twist=VRSettings.ColbyStickTwist; thumbsticks=VRSettings.ColbyThumbsticksChoice;
            holds=new bool[CockpitLayout.Count(owner.BlockDefinition.Id.SubtypeName)];
            bool[] saved=CockpitActions.HoldsCopy();
            Array.Copy(saved,holds,Math.Min(saved.Length,holds.Length));
            var surfaces=Surfaces(owner);
            shows=new int[surfaces.Count]; shown=new int[surfaces.Count];
            for(int i=0;i<surfaces.Count;i++) shows[i]=shown[i]=Showing(surfaces[i]);
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

            string subtype=owner.BlockDefinition.Id.SubtypeName;
            bool vehicle=CockpitRig.Find(subtype)?.Wheel!=null;
            var grip=new List<FlightSettings.Choice>
            {
                new FlightSettings.Choice { Label=vehicle ? "Tap to hold steering":"Tap to hold joysticks",On=stickHold,Toggle=()=>stickHold=!stickHold },
                new FlightSettings.Choice { Label="Tap to hold analog levers",On=leverHold,Toggle=()=>leverHold=!leverHold },
            };
            if(!vehicle) grip.Add(new FlightSettings.Choice { Label="Twist a stick to yaw, or to move up and down",On=twist,Toggle=()=>twist=!twist });
            grip.Add(new FlightSettings.Choice { Label="Thumbsticks fly and drive too",On=thumbsticks,Toggle=()=>thumbsticks=!thumbsticks });
            var keys=FlightSettings.Layout(draft,subtype,page,value=>{page=value; rebuild=true;},grip,
                CockpitActions.ThrottleLevers(),CockpitActions.HasHandles,slot=>slot<holds.Length && holds[slot],
                slot=>{ if(slot<holds.Length) holds[slot]=!holds[slot]; rebuild=true; },ScreenRows());
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
                if(page==FlightSettings.GripPage) { stickHold=leverHold=thumbsticks=false; twist=true; }
                else if(page==FlightSettings.LeversPage) Array.Clear(holds,0,holds.Length);
                else if(page==FlightSettings.ScreensPage) Array.Copy(shown,shows,shows.Length);
                else draft=FlightSettings.Defaults();
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
            button.CustomStyle=QuickActionsScreen.TileStyle;
            button.Size=size;
            button.Checked=active;
            button.ColorMask=active ? Vector4.One:DimColor;
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
            if(stickHold!=VRSettings.ColbyTapHoldSticks || leverHold!=VRSettings.ColbyTapHoldLevers || twist!=VRSettings.ColbyStickTwist || thumbsticks!=VRSettings.ColbyThumbsticksChoice)
            {
                VRSettings.ColbyTapHoldSticks=stickHold; VRSettings.ColbyTapHoldLevers=leverHold; VRSettings.ColbyStickTwist=twist; VRSettings.ColbyThumbsticksChoice=thumbsticks;
                VRSettings.Save();
            }
            bool[] saved=CockpitActions.HoldsCopy();
            if(holds.Where((stays,slot)=>stays!=(slot<saved.Length && saved[slot])).Any()) CockpitActions.SaveHolds(holds);
            var surfaces=Surfaces(owner);
            for(int i=0;i<shows.Length && i<surfaces.Count;i++)
            {
                if(shows[i]==shown[i]) continue;
                var (name,type,script)=contents[shows[i]];
                // The game's own content and script setters: synced with the cockpit and saved with the world, as its screen settings do.
                surfaces[i].ContentType=type;
                if(type==ContentType.SCRIPT) surfaces[i].Script=script;
                Log.Info($"ColbyFlightSettings: screen {i} ({surfaces[i].DisplayName}) of {owner.BlockDefinition.Id.SubtypeName} now shows {name}");
            }
            CloseScreen();
        }

        /// <summary>The game's screen choices, in its screen settings' order: none, text and images, then every script.</summary>
        private static List<(string Name,ContentType Type,string Script)> Contents()
        {
            var list=new List<(string Name,ContentType Type,string Script)> { ("None",ContentType.NONE,null),("Text and images",ContentType.TEXT_AND_IMAGE,null) };
            var scripts=MyTextSurfaceScriptFactory.Instance?.Scripts;
            if(scripts!=null)
                foreach(var script in scripts) list.Add((MyTexts.GetString(script.Value.DisplayName),ContentType.SCRIPT,script.Key));
            return list;
        }

        /// <summary>The cockpit's screens, through the game's own surface provider.</summary>
        internal static List<Sandbox.ModAPI.Ingame.IMyTextSurface> Surfaces(MyCockpit cockpit)
        {
            var list=new List<Sandbox.ModAPI.Ingame.IMyTextSurface>();
            if(cockpit is Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider provider)
                for(int i=0;i<provider.SurfaceCount;i++) { var surface=provider.GetSurface(i); if(surface!=null) list.Add(surface); }
            return list;
        }

        private int Showing(Sandbox.ModAPI.Ingame.IMyTextSurface surface)
        {
            if(surface.ContentType==ContentType.SCRIPT)
            {
                int at=contents.FindIndex(c=>c.Type==ContentType.SCRIPT && string.Equals(c.Script,surface.Script,StringComparison.OrdinalIgnoreCase));
                return at>=0 ? at:0;
            }
            return surface.ContentType==ContentType.NONE ? 0:1;
        }

        private List<FlightSettings.ScreenChoice> ScreenRows()
        {
            var surfaces=Surfaces(owner);
            var rows=new List<FlightSettings.ScreenChoice>();
            for(int i=0;i<shows.Length && i<surfaces.Count;i++)
            {
                int at=i;
                string key=surfaces[i].DisplayName ?? surfaces[i].Name;
                string name=string.IsNullOrEmpty(key) ? "Screen "+(i+1):MyTexts.GetString(MyStringId.GetOrCompute(key));
                rows.Add(new FlightSettings.ScreenChoice { Name=name,Shows=contents[shows[i]].Name,
                    Previous=()=>shows[at]=(shows[at]+contents.Count-1)%contents.Count,Next=()=>shows[at]=(shows[at]+1)%contents.Count });
            }
            return rows;
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

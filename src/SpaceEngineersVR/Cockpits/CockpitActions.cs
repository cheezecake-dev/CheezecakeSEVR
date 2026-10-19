// Adapted from ColbyJack1134's SpaceEngineersVR (Player/CockpitActions.cs; the toolbar text from Multiplayer/CockpitMemory.cs),
// with his permission.
// Phases 4-6 of docs/COCKPIT_PORT.md: each cockpit's own button-panel toolbar, one slot per dashboard control, what pressing
// a switch does, and what a handle drives. Local only, as his own fallback without a host (LoadLocal/SaveLocal): the toolbar
// and the covers are kept per world and cockpit in the settings file (colbyCockpitSwitches), and a cockpit never seen starts
// with every switch empty. His analog handles (ReadAnalog, SetAnalog, Analog, Compatible) are carried, SetAnalog applying the
// value here as his LocalOnly path does (MultiplayerRuntime.ApplyAnalog). Not carried: his multiplayer sharing
// (MultiplayerRuntime, CockpitMemory's record and layout upgrades), his remote view, and his assignment overlay's own controls
// and page layout (CockpitAssignmentToolbar, CockpitAssignmentLayout): Configure opens the game's own toolbar config screen on
// the cockpit's toolbar as the game does for a button panel, slot N being the cockpit's Nth control (buttons, then levers,
// then handles, then bars), with his assignment rules on that screen (CockpitAssignment).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;

namespace SpaceEngineersVR.Cockpits
{
    // Native button-panel assignment/activation, independent of the ship hotbar.
    internal static class CockpitActions
    {
        private static MyCockpit owner;
        private static MyToolbar toolbar,previous;
        private static bool editing,previousAutoUpdate;
        private static string loadedXml;
        /// <summary>The grid the toolbar's Init subscribed its OnFatBlockClosed to, which Reset takes it off again.</summary>
        private static MyCubeGrid toolbarGrid;
        private static readonly System.Reflection.MethodInfo fatBlockClosed=AccessTools.Method(typeof(MyToolbar),"OnFatBlockClosed");
        // His: the host shares the switches. Here they are always this player's own.
        internal static bool SharedReady => owner!=null;
        internal static bool Editing => editing;
        private static MyGuiScreenBase editor;
        private static CockpitAssignment assignment;
        private static readonly List<AnalogControl.Channel>[] analog=new List<AnalogControl.Channel>[CockpitLayout.MaximumCount];
        private static readonly double[] nextAnalog=new double[CockpitLayout.MaximumCount];
        public static MyToolbar Toolbar => toolbar;
        internal static bool EditingToolbar(MyToolbar candidate) => editing && ReferenceEquals(candidate,toolbar);
        internal static bool ReadAnalog(int slot,out float position,out string label)
        {
            position=0; label=null;
            if(toolbar==null || owner==null || editing || !AnalogControl.IsHandle(owner.BlockDefinition.Id.SubtypeName,slot)) return false;
            if(CockpitTouch.Now>=nextAnalog[slot])
            {
                nextAnalog[slot]=CockpitTouch.Now+.2;
                toolbar.UpdateItemForIdentity(slot,MySession.Static.LocalPlayerId,false);
                var item=toolbar.GetItemAtIndex(slot);
                analog[slot]=AnalogControl.Resolve(item,MySession.Static.LocalPlayerId,false);
            }
            var channels=analog[slot];
            if(channels==null || channels.Count==0) return false;
            position=channels[0].Position(); label=channels[0].Label();
            string first=label;
            if(channels.Skip(1).Any(c=>c.Label()!=first)) label="Mixed";
            return true;
        }
        internal static bool SetAnalog(int slot,float position)
        {
            if(toolbar==null || owner==null || editing || !SeatFit.Eligible(owner) || !SharedReady ||
                !((Sandbox.ModAPI.IMyTerminalBlock)owner).HasPlayerAccess(MySession.Static.LocalPlayerId) ||
                !ReadAnalog(slot,out _,out _)) return false;
            // His MultiplayerRuntime.ApplyAnalog, as it runs without a host: the handle's item, enabled, drives its blocks.
            if(!AnalogControl.Finite(position) || position<0 || position>1) return false;
            long identity=MySession.Static.LocalPlayerId;
            toolbar.UpdateItemForIdentity(slot,identity,false);
            var item=toolbar.GetItemAtIndex(slot);
            if(item?.Enabled!=true) return false;
            var channels=AnalogControl.Resolve(item,identity);
            if(channels!=null) AnalogControl.Set(owner,identity,channels,position);
            return true;
        }
        internal static string AnalogLabel(int slot,float position) => analog[slot]?.FirstOrDefault()?.Label(position);

        public static void Update(MyCockpit seat)
        {
            if(ReferenceEquals(owner,seat)) return;
            Reset();
            if(seat==null) return;
            owner=seat;
            int count=CockpitLayout.Count(seat.BlockDefinition.Id.SubtypeName);
            toolbar=new MyToolbar(MyToolbarType.ButtonPanel,Math.Min(9,count),(count+8)/9);
            MyObjectBuilder_Toolbar builder=null;
            string stored=Find(MySession.Static.CurrentPath,seat.EntityId)?.Toolbar;
            if(!string.IsNullOrEmpty(stored))
                try { builder=ParseToolbar(stored); }
                catch(Exception ex) { Log.Warn("ColbyCockpit: the switch assignments of this cockpit could not be read and are left empty: "+ex.Message); }
            toolbar.Init(builder,seat);
            toolbarGrid=seat.CubeGrid;
            loadedXml=builder==null ? "" : WriteToolbar(builder);
            toolbar.ItemChanged+=Changed;
            Log.Info($"ColbyCockpit: {seat.BlockDefinition.Id.SubtypeName} has {count} switches, {builder?.Slots?.Count ?? 0} assigned");
        }
        private static void Changed(MyToolbar source,MyToolbar.IndexArgs index,bool gamepad)
        {
            if(!ReferenceEquals(source,toolbar) || owner==null) return;
            if(index.ItemIndex>=CockpitLayout.Count(owner.BlockDefinition.Id.SubtypeName))
            {
                if(source.GetItemAtIndex(index.ItemIndex)!=null) source.SetItemAtIndex(index.ItemIndex,null);
                return;
            }
            Array.Clear(nextAnalog,0,nextAnalog.Length);
            loadedXml=WriteToolbar(source.GetObjectBuilder());
            SaveToolbar();
        }
        private static void SaveToolbar()
        {
            var record=LoadLocal(owner);
            record.Toolbar=loadedXml;
            SaveLocal(owner,record);
        }
        /// <summary>His covers' save: this cockpit's open covers, kept with its toolbar.</summary>
        internal static void SaveCovers(MyCockpit seat,bool[] open)
        {
            var record=LoadLocal(seat);
            record.Covers=(bool[])open.Clone();
            SaveLocal(seat,record);
        }
        public static bool Activate(int slot,bool? desired=null)
        {
            if(toolbar==null || owner==null || !SeatFit.Eligible(owner) || editing || !((Sandbox.ModAPI.IMyTerminalBlock)owner).HasPlayerAccess(MySession.Static.LocalPlayerId)) return false;
            toolbar.UpdateItemForIdentity(slot,MySession.Static.LocalPlayerId,false);
            var item=toolbar.GetItemAtIndex(slot);
            if(item==null) { Configure(slot); return false; }
            bool view=CockpitSwitchState.ViewState(item,out bool active);
            // His: a view switch that is on hands the view back. The switches only work from the seat's own view, so it is never on here.
            if(view && active) return true;
            var controller=MySession.Static.ControlledEntity as MyShipController;
            bool weapon=CockpitSwitchState.WeaponState(item,controller,out bool selected);
            if(weapon && (selected || desired==false))
            {
                // Repeated selection cycles ammunition in vanilla. Down clears only this weapon's selection.
                if(selected && desired!=true) controller.SwitchToWeapon((MyToolbarItemWeapon)null);
                return true;
            }
            if(!item.Enabled || (item is MyToolbarItemTerminalGroup group && !group.PlayerHasAccessToAllBlocks(MySession.Static.LocalPlayerId)))
            { Notify("This switch action is unavailable or access is denied."); return false; }
            if(view)
            {
                if(desired==false) return false;
                return toolbar.ActivateItemAtIndex(slot);
            }
            if(weapon) return controller!=null && toolbar.ActivateItemAtIndex(slot);
            bool stateful=CockpitSwitchState.Read(item,out _);
            if(!stateful && desired==false) return false;
            return stateful && desired.HasValue ? CockpitSwitchState.Set(item,desired.Value) : toolbar.ActivateItemAtIndex(slot);
        }
        private static readonly List<Sandbox.ModAPI.Ingame.IMyTerminalBlock> rankBlocks=new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
        private static bool Handle(int slot) => owner!=null && AnalogControl.IsHandle(owner.BlockDefinition.Id.SubtypeName,slot);
        private static bool AllBlocks(MyToolbarItemActions item,Predicate<Sandbox.ModAPI.Ingame.IMyTerminalBlock> test)
        {
            rankBlocks.Clear();
            if(item is MyToolbarItemTerminalBlock block) block.FetchAllBlocks(rankBlocks);
            else if(item is MyToolbarItemTerminalGroup group) group.FetchAllBlocks(rankBlocks);
            return rankBlocks.Count>0 && rankBlocks.TrueForAll(b=>b!=null && test(b));
        }
        internal static bool Analog(int slot,MyToolbarItemActions item) => Handle(slot) && AllBlocks(item,b=>AnalogControl.Resolve(b,item.ActionId)!=null);
        internal static bool Compatible(int slot,MyToolbarItemActions item,string action) => owner!=null &&
            AllBlocks(item,b=>Handle(slot) ? AnalogControl.Resolve(b,action)!=null : CockpitSwitchState.ShowsReady(b,action) || CockpitSwitchState.ViewAction(action,b) || CockpitSwitchState.BatteryAction(b,action));
        public static bool ReadState(int slot,out float state)
        {
            state=0;
            if(toolbar==null || owner==null || editing) return false;
            toolbar.UpdateItemForIdentity(slot,MySession.Static.LocalPlayerId,false);
            return CockpitSwitchState.Read(toolbar.GetItemAtIndex(slot),out state);
        }
        /// <summary>Opens the game's toolbar config screen on this cockpit's switches, at <paramref name="selected"/>'s page.</summary>
        public static void Configure(int selected=-1)
        {
            if(owner==null || toolbar==null || editing || MyGuiScreenToolbarConfigBase.Static!=null) return;
            previous=MyToolbarComponent.CurrentToolbar; previousAutoUpdate=MyToolbarComponent.AutoUpdate;
            try
            {
                editing=true;
                MyToolbarComponent.CurrentToolbar=toolbar; MyToolbarComponent.AutoUpdate=false;
                if(selected>=0) toolbar.SwitchToPage(selected/toolbar.SlotCount);
                editor=MyGuiSandbox.CreateScreen(MyPerGameSettings.GUI.ToolbarConfigScreen,0,owner,null);
                assignment=new CockpitAssignment((MyGuiScreenToolbarConfigBase)editor,toolbar,CockpitLayout.Count(owner.BlockDefinition.Id.SubtypeName),selected);
                assignment.Update();
                editor.Closed+=Closed;
                MyGuiSandbox.AddScreen(MyGuiScreenGamePlay.ActiveGameplayScreen=editor);
                Log.Info($"ColbyCockpit: the toolbar config screen is open on this cockpit's switches, at switch {selected+1}");
            }
            catch(Exception ex)
            { RestoreEditor(); Log.Warn("ColbyCockpit: the switch configuration failed: "+ex); Notify("Switch configuration unavailable"); }
        }
        private static void Closed(MyGuiScreenBase screen,bool unloading) { RestoreEditor(); }
        private static void RestoreEditor()
        {
            if(!editing) return;
            editing=false;
            assignment?.Dispose(); assignment=null;
            if(editor!=null) editor.Closed-=Closed;
            editor=null;
            if(ReferenceEquals(MyToolbarComponent.CurrentToolbar,toolbar)) MyToolbarComponent.CurrentToolbar=previous;
            MyToolbarComponent.AutoUpdate=previousAutoUpdate; previous=null;
        }
        private static void Notify(string text)
        {
            try { MyAPIGateway.Utilities?.ShowNotification(text,2000); }
            catch(Exception) { }
        }

        // ---- His local record: CockpitMemory.Record's toolbar and covers, and his Config.CockpitActions/CockpitStates ----

        internal sealed class Record
        {
            internal string Toolbar="";
            internal bool[] Covers=new bool[0];
        }
        private static MyObjectBuilder_Toolbar ParseToolbar(string xml)
            => string.IsNullOrEmpty(xml) ? null:MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_Toolbar>(xml);
        private static string WriteToolbar(MyObjectBuilder_Toolbar value)
        {
            value.ToolbarType=MyToolbarType.ButtonPanel;
            return MyAPIGateway.Utilities.SerializeToXML(value);
        }
        internal static Record LoadLocal(MyCockpit seat)
        {
            var stored=Find(MySession.Static.CurrentPath,seat.EntityId);
            return stored==null ? new Record() : new Record { Toolbar=stored.Toolbar,Covers=(bool[])stored.Covers.Clone() };
        }
        internal static void SaveLocal(MyCockpit seat,Record record)
        {
            string path=MySession.Static.CurrentPath; long id=seat.EntityId;
            var entries=Entries().Where(s=>s.World!=path || s.Cockpit!=id).ToList();
            entries.Add(new Entry { World=path,Cockpit=id,Toolbar=record.Toolbar ?? "",Covers=record.Covers ?? new bool[0] });
            VRSettings.ColbyCockpitSwitches=string.Join(";",entries.Select(e=>Encode(e.World)+","+e.Cockpit+","+Encode(e.Toolbar)+","+
                new string(e.Covers.Select(c=>c ? '1':'0').ToArray())));
            VRSettings.Save();
        }
        private sealed class Entry
        {
            public string World,Toolbar;
            public long Cockpit;
            public bool[] Covers;
        }
        private static Entry Find(string world,long cockpit) => Entries().FirstOrDefault(s=>s.World==world && s.Cockpit==cockpit);
        // world,cockpit,toolbar,covers per entry, entries split by ';': the world path and toolbar XML in base64, covers as 0 and 1.
        private static IEnumerable<Entry> Entries()
        {
            foreach(string item in (VRSettings.ColbyCockpitSwitches ?? "").Split(new[] {';'},StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts=item.Split(',');
                if(parts.Length!=4 || !long.TryParse(parts[1],out long cockpit)) continue;
                string world,toolbarXml;
                try { world=Decode(parts[0]); toolbarXml=Decode(parts[2]); }
                catch(FormatException) { continue; }
                yield return new Entry { World=world,Cockpit=cockpit,Toolbar=toolbarXml,Covers=parts[3].Select(c=>c=='1').ToArray() };
            }
        }
        private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        private static string Decode(string text) => Encoding.UTF8.GetString(Convert.FromBase64String(text));

        public static void Reset()
        {
            if(editor!=null) editor.CloseScreen();
            RestoreEditor();
            if(toolbar!=null)
            {
                if(owner?.HasInventory==true) owner.GetInventory().ContentsChanged-=toolbar.CharacterInventory_OnContentsChanged;
                toolbar.ItemChanged-=Changed; toolbar.Clear();
                // MyToolbar.Init subscribes to the seat's grid and nothing in the game takes it off: without this each seat
                // change would leave a toolbar on the grid until the grid closes.
                if(toolbarGrid!=null && fatBlockClosed!=null)
                    try { toolbarGrid.OnFatBlockClosed-=(Action<MyCubeBlock>)Delegate.CreateDelegate(typeof(Action<MyCubeBlock>),toolbar,fatBlockClosed); }
                    catch(Exception ex) { Log.Warn("ColbyCockpit: the switch toolbar could not be taken off its grid: "+ex.Message); }
            }
            toolbarGrid=null;
            Array.Clear(analog,0,analog.Length); Array.Clear(nextAnalog,0,nextAnalog.Length);
            toolbar=null; owner=null; loadedXml=null;
        }
    }
}

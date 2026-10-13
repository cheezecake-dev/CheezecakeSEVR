// Adapted from ColbyJack1134's SpaceEngineersVR (GUI/CockpitAssignment.cs, Patches/CockpitAssignmentPatch.cs), with his permission.
// Phase 5 of docs/COCKPIT_PORT.md: his rules for assigning cockpit switches, on the game's own toolbar config screen that
// CockpitActions.Configure opens on the cockpit's toolbar. Carried, close to his text:
// - the switch being assigned is selected on the screen, and the slots past the cockpit's last control are greyed and refuse
//   a drop;
// - a double click (or the trigger held on one catalog item, HoldToAssign) puts the item on that switch, even when the switch
//   already has something (the game's own double click puts it on the first empty slot instead), then offers the item's
//   actions in the game's own menu;
// - two switches may share an action (the game's drop clears the other slot holding the same item);
// - in the actions menu after a drop, the actions that suit the switch come first: ones that show their state on a lever, a
//   camera view, or, on a handle, a numeric value the handle can drive;
// - a handle's numeric action skips the value dialog: the handle supplies the value, and the speed is the block's full speed
//   (AnalogControl.DefaultParameters);
// - a camera's view and control, and a jump drive's jump, are offered as on a ship toolbar (CockpitViewAssignmentPatch);
// - page labels past the ninth page are numbered (PageLabel).
// Not carried (his overlay's own controls, on top of the game's screen): the ship-toolbar source row, the page arrows, the
// relabelled toolbar caption, the VR marker icon on suiting actions, and his per-cockpit page layout (CockpitAssignmentLayout,
// CockpitAssignmentToolbar): slot N on the screen is the cockpit's Nth control. Clearing a switch is the game's own: drag its
// item off the toolbar.
// His Harmony attributes become PatchRegistry patches (Patch, from StereoPatches), each body under a FeatureGuard: a failure
// leaves the game's screen as it is.
using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Graphics.GUI;
using VRage.Collections;
using VRage.Game;
using VRage.Input;
using VRage.Utils;

namespace SpaceEngineersVR.Cockpits
{
    internal sealed class CockpitAssignment : IDisposable
    {
        private static CockpitAssignment current;
        private static readonly FeatureGuard guard=new FeatureGuard("ColbyCockpit switch assignment");
        private static readonly System.Reflection.MethodInfo nativeDrop=AccessTools.Method(typeof(MyGuiScreenToolbarConfigBase),"OnDragAndDropOnDrop");
        private static readonly System.Reflection.MethodInfo nativeDoubleClick=AccessTools.Method(typeof(MyGuiScreenToolbarConfigBase),"OnGridItemDoubleClicked",
            new[] {typeof(MyGuiControlGrid),typeof(MyGuiControlGrid.EventArgs),typeof(bool)});
        internal const double HoldSeconds=.4;
        private readonly MyGuiScreenToolbarConfigBase screen;
        private readonly MyToolbar target;
        private readonly int count,selected;
        private MyGuiControlToolbar toolbar;
        private MyGuiControlGridDragAndDrop drag;
        private MyDragAndDropEventArgs pendingClick;
        private MyGuiControlContextMenu clickMenu;
        private int dropSlot=-1,dropPage=-1,pendingPage=-1,heldItem=-1;
        private DateTime heldSince;

        public CockpitAssignment(MyGuiScreenToolbarConfigBase screen,MyToolbar target,int count,int selected)
        { this.screen=screen; this.target=target; this.count=count; this.selected=selected; current=this; screen.Closed+=Closed; }
        private int Control(int index) => index>=0 && index<count ? index:-1;
        private void Closed(MyGuiScreenBase screen,bool unloading) => Dispose();
        private T Field<T>(string name) where T:class => AccessTools.Field(typeof(MyGuiScreenToolbarConfigBase),name)?.GetValue(screen) as T;
        public void Update()
        {
            var next=Field<MyGuiControlToolbar>("m_toolbarControl");
            if(next==null) return;
            if(toolbar!=next) { toolbar=next; drag=Field<MyGuiControlGridDragAndDrop>("m_dragAndDrop"); }
            int first=target.CurrentPage*target.SlotCount;
            if(selected>=0) toolbar.ToolbarGrid.SelectedIndex=selected>=first && selected<first+target.SlotCount ? (int?)(selected-first) : null;
            for(int i=0;i<target.SlotCount;i++)
                if(Control(first+i)<0)
                {
                    var item=toolbar.ToolbarGrid.GetItemAt(i);
                    if(item==null) toolbar.ToolbarGrid.SetItemAt(i,new MyGuiGridItem((string[])null,null,"",null) { Enabled=false });
                    else item.Enabled=false;
                }
        }
        internal static MyDragAndDropEventArgs DoubleClickDrop(MyGuiScreenToolbarConfigBase screen,MyGuiControlGrid grid,MyGuiGridItem item,int itemIndex)
        {
            var c=current;
            if(c==null || c.screen!=screen || c.toolbar==null || c.Control(c.selected)<0 ||
                !ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target) || c.toolbar.IsToolbarGrid(grid)) return null;
            int slot=c.selected-c.target.CurrentPage*c.target.SlotCount;
            if(slot<0 || slot>=c.target.SlotCount || item?.Enabled!=true) return null;
            return new MyDragAndDropEventArgs { Item=item,DragFrom=new MyDragAndDropInfo {Grid=grid,ItemIndex=itemIndex},
                DropTo=new MyDragAndDropInfo {Grid=c.toolbar.ToolbarGrid,ItemIndex=slot} };
        }
        internal static bool HandleDoubleClick(MyGuiScreenToolbarConfigBase screen,MyGuiControlGrid grid,MyGuiControlGrid.EventArgs args)
        {
            var drop=DoubleClickDrop(screen,grid,grid.TryGetItemAt(args.RowIndex,args.ColumnIndex),args.ItemIndex);
            if(drop==null) return false;
            // The catalog still owns the second press and can reclaim focus on release.
            current.pendingClick=drop;
            current.pendingPage=current.target.CurrentPage;
            return true;
        }
        internal static bool HandleInput(MyGuiScreenToolbarConfigBase screen)
        {
            var c=current;
            if(c==null || c.screen!=screen) return false;
            if(c.dropPage>=0 && c.dropPage!=c.target.CurrentPage)
            {
                c.pendingClick=null; c.clickMenu?.Deactivate(); c.clickMenu=null;
                c.Field<MyGuiControlContextMenu>("m_onDropContextMenu")?.Deactivate();
                c.dropSlot=c.dropPage=-1;
            }
            if(c.pendingClick==null && c.clickMenu==null) c.HoldToAssign(DateTime.UtcNow);
            else c.heldItem=-1;
            if(c.pendingClick!=null)
            {
                if(MyInput.Static.IsPrimaryButtonPressed()) return true;
                var drop=c.pendingClick; c.pendingClick=null;
                if(c.pendingPage!=c.target.CurrentPage || DoubleClickDrop(screen,drop.DragFrom.Grid,drop.Item,drop.DragFrom.ItemIndex)==null) return true;
                nativeDrop.Invoke(screen,new object[] {drop.DragFrom.Grid,drop});
                var menu=c.Field<MyGuiControlContextMenu>("m_onDropContextMenu");
                if(menu?.Enabled==true)
                {
                    menu.Enabled=false;
                    c.Field<MyGuiControlContextMenu>("m_contextMenu").Enabled=false;
                    c.toolbar.HideContextMenu();
                    menu.AllowKeyboardNavigation=true;
                    menu.Activate();
                    screen.FocusedControl=menu.GetInnerList();
                    c.clickMenu=menu;
                }
                return true;
            }
            if(c.clickMenu==null) return false;
            if(!c.clickMenu.Visible)
            {
                if(MyInput.Static.IsPrimaryButtonPressed()) return true;
                c.clickMenu=null;
                return false;
            }
            if(!ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target))
            { c.clickMenu.Deactivate(); c.clickMenu=null; return true; }
            // Route the menu before the catalog, including outside-click and Escape cancellation.
            c.clickMenu.IsMouseOver=c.clickMenu.GetInnerList().CheckMouseOver(false);
            c.clickMenu.HandleInput();
            return true;
        }
        // Holding the trigger on one catalog item acts as a double click. Leaving the item first keeps the native drag.
        private void HoldToAssign(DateTime now)
        {
            var grid=Field<MyGuiControlGrid>("m_gridBlocks");
            if(grid==null || !MyInput.Static.IsPrimaryButtonPressed()) { heldItem=-1; return; }
            int over=grid.MouseOverIndex;
            if(MyInput.Static.IsNewPrimaryButtonPressed())
            {
                heldItem=grid.IsValidIndex(over) && grid.MouseOverItem?.Enabled==true ? over:-1;
                heldSince=now; return;
            }
            if(heldItem<0) return;
            if(over!=heldItem) { heldItem=-1; return; }
            if((now-heldSince).TotalSeconds<HoldSeconds) return;
            heldItem=-1;
            drag?.Stop();
            var args=new MyGuiControlGrid.EventArgs { ItemIndex=over,RowIndex=over/grid.ColumnsCount,ColumnIndex=over%grid.ColumnsCount,Button=MySharedButtonsEnum.Primary };
            nativeDoubleClick.Invoke(screen,new object[] {grid,args,false});
        }
        internal static bool HandleDrop(MyGuiScreenToolbarConfigBase screen,MyDragAndDropEventArgs args)
        {
            var c=current;
            if(c==null || c.screen!=screen || c.toolbar==null) return false;
            bool target=args.DropTo!=null && c.toolbar.IsToolbarGrid(args.DropTo.Grid);
            int index=target ? c.target.SlotToIndex(args.DropTo.ItemIndex) : -1;
            c.dropSlot=c.Control(index); c.dropPage=target ? c.target.CurrentPage:-1;
            if(target && (c.dropSlot<0 || !ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target))) return true;
            return false;
        }
        internal static bool FillActions(MyGuiScreenToolbarConfigBase screen,MyGuiControlContextMenu menu,MyToolbarItemActions item,out bool filled)
        {
            filled=false;
            var c=current;
            // Only the chooser that follows a drop onto a switch; the catalog's secondary-click menu has no target switch.
            if(c==null || c.screen!=screen || c.dropSlot<0 || item==null || menu!=c.Field<MyGuiControlContextMenu>("m_onDropContextMenu")) return false;
            int slot=c.dropSlot;
            filled=Fill(menu,item.PossibleActions(c.target.ToolbarType),id=>CockpitActions.Compatible(slot,item,id));
            return true;
        }
        internal static bool Assign(MyToolbarItem item,int slot,string action=null)
        {
            var c=current;
            if(c==null || !ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target)) return false;
            int index=c.target.SlotToIndex(slot),control=c.Control(index);
            if(slot<0 || slot>=c.target.SlotCount || control<0) return true;
            var actions=item as MyToolbarItemActions;
            string previous=actions?.ActionId;
            if(action!=null && actions!=null) actions.ActionId=action;
            Action<bool> commit=success=> {
                if(!success || current!=c || !ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target) || c.target.SlotToIndex(slot)!=index)
                { if(action!=null && actions!=null) actions.ActionId=previous; return; }
                c.target.SetItemAtIndex(index,item);
            };
            // Physical controls may intentionally share an action; native hotbar deduplication would clear another control.
            if(item==null) commit(true);
            else if(actions!=null && CockpitActions.Analog(control,actions))
            { AnalogControl.DefaultParameters(item,actions.ActionId); commit(true); }
            else MyGuiScreenToolbarConfigBase.RequestItemParameters(item,commit);
            return true;
        }
        // Compatible actions first; native order is kept within each group. (His marker icon on them is not carried.)
        internal static bool Fill(MyGuiControlContextMenu menu,ListReader<ITerminalAction> actions,Func<string,bool> compatible)
        {
            if(actions.Count==0) return false;
            menu.Enabled=true;
            menu.CreateNewContextMenu();
            foreach(var entry in actions.Select(a=>new { Action=a,Compatible=compatible(a.Id) }).OrderBy(a=>a.Compatible ? 0:1).ToList())
                menu.AddItem(entry.Action.Name,"",entry.Action.Icon,entry.Action.Id);
            return true;
        }
        // A handle supplies the target value itself, so its numeric actions skip the native value dialogs.
        internal static bool SkipParameters(MyToolbarItem item)
        {
            var c=current;
            // Only the item chosen from the drop chooser, so dropSlot is the slot it is assigned to.
            if(c==null || c.dropSlot<0 || !ReferenceEquals(MyToolbarComponent.CurrentToolbar,c.target) ||
                !ReferenceEquals(item,c.Field<MyToolbarItem>("m_onDropContextMenuItem")) ||
                !(item is MyToolbarItemActions action) || !CockpitActions.Analog(c.dropSlot,action)) return false;
            AnalogControl.DefaultParameters(item,action.ActionId);
            return true;
        }
        public void Dispose()
        {
            screen.Closed-=Closed;
            if(current==this) current=null;
        }

        // ---- His patches (Patches/CockpitAssignmentPatch.cs, and the PageLabel patch from GUI/CockpitAssignment.cs) ----

        public static void Patch(Harmony harmony)
        {
            var screenType=typeof(MyGuiScreenToolbarConfigBase);
            HarmonyMethod Method(string name) => new HarmonyMethod(typeof(CockpitAssignment),name);
            System.Reflection.MethodInfo Find(Type type,string name,Type[] arguments=null) =>
                (arguments==null ? AccessTools.Method(type,name) : AccessTools.Method(type,name,arguments)) ?? throw new MissingMethodException(type.Name+"."+name);
            harmony.Patch(Find(screenType,nameof(MyGuiScreenToolbarConfigBase.Update)),postfix:Method(nameof(UpdatePostfix)));
            harmony.Patch(Find(screenType,"OnDragAndDropOnDrop"),prefix:Method(nameof(DropPrefix)));
            harmony.Patch(nativeDoubleClick ?? throw new MissingMethodException("MyGuiScreenToolbarConfigBase.OnGridItemDoubleClicked"),prefix:Method(nameof(DoubleClickPrefix)));
            harmony.Patch(Find(screenType,nameof(MyGuiScreenToolbarConfigBase.DropGridItemToToolbar)),prefix:Method(nameof(DropToToolbarPrefix)));
            harmony.Patch(Find(screenType,nameof(MyGuiScreenToolbarConfigBase.UpdateGridItemByRightClick)),prefix:Method(nameof(RightClickPrefix)));
            harmony.Patch(Find(screenType,"UpdateContextMenu"),prefix:Method(nameof(ContextMenuPrefix)));
            harmony.Patch(Find(screenType,nameof(MyGuiScreenToolbarConfigBase.RequestItemParameters)),prefix:Method(nameof(ParametersPrefix)));
            harmony.Patch(Find(screenType,nameof(MyGuiScreenToolbarConfigBase.HandleInput)),prefix:Method(nameof(InputPrefix)),postfix:Method(nameof(UpdatePostfix)));
            harmony.Patch(Find(typeof(MyToolbarItemTerminalBlock),nameof(MyToolbarItemTerminalBlock.PossibleActions)),prefix:Method(nameof(PossibleActionsPrefix)));
            harmony.Patch(Find(typeof(MyToolbarComponent),nameof(MyToolbarComponent.GetSlotControlText)),postfix:Method(nameof(PageLabelPostfix)));
        }

        private static bool Run(Func<bool> body,string what)
        {
            if(current==null || !guard.On) return false;
            try { return body(); }
            catch(Exception e) { guard.Failed(e,"ColbyCockpit: switch assignment ("+what+") failed; the game's screen carries on as it is"); return false; }
        }

        private static void UpdatePostfix(MyGuiScreenToolbarConfigBase __instance)
            => Run(()=>{ if(current?.screen==__instance) current.Update(); return false; },"update");
        private static bool DropPrefix(MyGuiScreenToolbarConfigBase __instance,MyDragAndDropEventArgs eventArgs)
            => !Run(()=>HandleDrop(__instance,eventArgs),"drop");
        private static bool DoubleClickPrefix(MyGuiScreenToolbarConfigBase __instance,MyGuiControlGrid sender,MyGuiControlGrid.EventArgs eventArgs)
            => !Run(()=>HandleDoubleClick(__instance,sender,eventArgs),"double click");
        private static bool DropToToolbarPrefix(MyToolbarItem item,int slot)
            => !Run(()=>Assign(item,slot),"drop on a switch");
        private static bool RightClickPrefix(MyToolbarItem item,int slot,string selectedAction)
            => !Run(()=>Assign(item,slot,selectedAction),"action on a switch");
        private static bool ContextMenuPrefix(MyGuiScreenToolbarConfigBase __instance,ref MyGuiControlContextMenu currentContextMenu,MyToolbarItemActions item,ref bool __result)
        {
            var menu=currentContextMenu; bool filled=false;
            if(!Run(()=>FillActions(__instance,menu,item,out filled),"actions menu")) return true;
            __result=filled; return false;
        }
        private static bool ParametersPrefix(MyToolbarItem item,Action<bool> callback)
        {
            if(!Run(()=>SkipParameters(item),"handle values")) return true;
            callback(true); return false;
        }
        private static bool InputPrefix(MyGuiScreenToolbarConfigBase __instance)
            => !Run(()=>HandleInput(__instance),"input");
        // Switches are the pilot's ship controls: offer the ship-only view, control and Jump actions native button panels exclude.
        private static void PossibleActionsPrefix(MyToolbarItemTerminalBlock __instance,ref MyToolbarType type)
        {
            if(type!=MyToolbarType.ButtonPanel) return;
            var block=__instance.Block;
            if(Run(()=>CockpitActions.EditingToolbar(MyToolbarComponent.CurrentToolbar) &&
                (CockpitSwitchState.ViewBlock(block) || block is Sandbox.Game.Entities.MyJumpDrive),"ship actions"))
                type=MyToolbarType.Ship;
        }
        // Native page labels reuse slot bindings, which end at the ninth slot.
        private static void PageLabelPostfix(int slotIndex,ref StringBuilder __result)
        {
            if(__result==null && slotIndex>=0 && current!=null) __result=new StringBuilder((slotIndex+1).ToString());
        }
    }
}

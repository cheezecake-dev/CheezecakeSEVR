// Adapted from ColbyJack1134's SpaceEngineersVR (Player/SeatFit.cs, Patches/SeatFitPatch.cs), with his permission.
// Phase 7 of docs/COCKPIT_PORT.md. His SeatFitPatch is not carried as a patch: CockpitHead already owns
// MyCockpit.GetHeadMatrix, and adds his offset there through Head below, so the camera and his SeatLocal see one head.
// Dropped with the features they belong to: his RemoteView seat and his BodyFit seat origin (Player.SeatOrigin). His
// Config.SeatFits is the settings file's colbySeatFits, written when a move ends rather than each frame. The seat is his
// only while his cockpit setting is on and his rig owns it (CockpitControls.OwnsSeat).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class SeatFit
    {
        public static MyCockpit Seat { get; private set; }
        public static Vector3 Offset { get; private set; }
        private static bool unsaved;
        public static bool Eligible(MyCockpit seat) => VRInput.Active && VRSettings.ColbyCockpit && seat!=null && !seat.Closed && !seat.MarkedForClose &&
            seat.Pilot!=null && seat.Pilot==MySession.Static?.LocalCharacter && !seat.Pilot.IsDead &&
            MySession.Static.ControlledEntity==seat && MySession.Static.CameraController==seat &&
            (seat.IsInFirstPersonView || seat.ForceFirstPersonCamera) && CockpitControls.OwnsSeat(seat);
        internal static Vector3 Limit(Vector3 value) => value.IsValid() ? Vector3.Clamp(value,new Vector3(-0.35f,-0.45f,-0.45f),new Vector3(0.35f,0.3f,0.45f)) : Vector3.Zero;
        internal static Vector3 Default(string subtype) => CockpitRig.Find(subtype)?.DefaultSeatOffset ?? Vector3.Zero;
        /// <summary>Once per input frame (VRControls), after CockpitControls.Update.</summary>
        public static void Update()
        {
            var next=MySession.Static?.ControlledEntity as MyCockpit;
            if (!Eligible(next)) next=null;
            if (Seat!=next)
            {
                Save();
                Seat=next; Offset=Default(Seat?.BlockDefinition.Id.SubtypeName);
                if (Seat!=null && Saved().TryGetValue(Seat.BlockDefinition.Id.SubtypeName,out Vector3 saved)) Offset=Limit(saved);
                if (Seat!=null) Log.Info($"ColbyCockpit: seat fit in {Seat.BlockDefinition.Id.SubtypeName} is ({Offset.X:F2}, {Offset.Y:F2}, {Offset.Z:F2}) m (x right, y up, z back)");
            }
        }
        /// <summary>
        /// One step of the quick menu's seat page (QuickActionsScreen): the fit moved <paramref name="metres"/> along
        /// <paramref name="direction"/> in the seat's own axes, within the same limits as his held arrows, and saved at once.
        /// False when there is no seat, or the fit is already at its limit that way.
        /// </summary>
        public static bool Nudge(Vector3 direction,float metres)
        {
            if (Seat==null) return false;
            Vector3 value=Limit(Offset+direction*metres);
            if (value==Offset) return false;
            Offset=value; unsaved=true; Save();
            return true;
        }
        /// <summary>The quick menu's seat reset: the fit straight back to the cockpit's own (his reset key eased it there while held), and saved.</summary>
        public static void ResetFit()
        {
            if (Seat==null) return;
            Vector3 value=Default(Seat.BlockDefinition.Id.SubtypeName);
            if (value==Offset) return;
            Offset=value; unsaved=true; Save();
        }
        /// <summary>The fit is the cockpit's own.</summary>
        public static bool AtDefault => Seat==null || Offset==Default(Seat.BlockDefinition.Id.SubtypeName);
        /// <summary>Whether a step of <paramref name="metres"/> along <paramref name="direction"/> would still move the fit.</summary>
        public static bool CanNudge(Vector3 direction,float metres) => Seat!=null && Limit(Offset+direction*metres)!=Offset;
        /// <summary>Writes this seat's fit into the settings file, once a move has ended (his config wrote it every frame).</summary>
        public static void Save()
        {
            if (!unsaved || Seat==null) { unsaved=false; return; }
            unsaved=false;
            string subtype=Seat.BlockDefinition.Id.SubtypeName;
            var fits=Saved();
            fits[subtype]=Offset;
            VRSettings.ColbySeatFits=string.Join(";",fits.Select(p=>string.Format(CultureInfo.InvariantCulture,"{0}:{1:0.###},{2:0.###},{3:0.###}",p.Key,p.Value.X,p.Value.Y,p.Value.Z)));
            VRSettings.Save();
            Log.Info($"ColbyCockpit: seat fit in {subtype} saved as ({Offset.X:F2}, {Offset.Y:F2}, {Offset.Z:F2}) m");
        }
        /// <summary>The saved fits by cockpit subtype: "subtype:x,y,z;..." in the settings file. A malformed entry is skipped.</summary>
        private static Dictionary<string,Vector3> Saved()
        {
            var result=new Dictionary<string,Vector3>(StringComparer.Ordinal);
            foreach (string entry in (VRSettings.ColbySeatFits ?? string.Empty).Split(new[] {';'},StringSplitOptions.RemoveEmptyEntries))
            {
                int colon=entry.LastIndexOf(':');
                if (colon<=0) continue;
                string[] parts=entry.Substring(colon+1).Split(',');
                if (parts.Length!=3) continue;
                if (float.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out float x) &&
                    float.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float y) &&
                    float.TryParse(parts[2],NumberStyles.Float,CultureInfo.InvariantCulture,out float z))
                    result[entry.Substring(0,colon).Trim()]=new Vector3(x,y,z);
            }
            return result;
        }
        public static MatrixD UnadjustedHead()
        {
            MatrixD result=Seat.GetHeadMatrix(false,false);
            result.Translation-=Vector3D.TransformNormal(Offset,Seat.WorldMatrix);
            return result;
        }
        public static void Reset() { Save(); Seat=null; Offset=Vector3.Zero; }

        /// <summary>
        /// His SeatFitPatch's postfix, called by CockpitHead on the local pilot's seat head matrix: the eye moved by the fit, in
        /// the seat's own axes. Nothing for any other seat, or with his cockpit setting off.
        /// </summary>
        internal static void Head(MyCockpit cockpit,ref MatrixD head)
        {
            if (cockpit==Seat && Offset!=Vector3.Zero && Eligible(cockpit))
                head.Translation+=Vector3D.TransformNormal(Offset,cockpit.WorldMatrix);
        }
    }
}

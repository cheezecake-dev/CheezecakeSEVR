using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using VRage;
using VRage.Game;
using VRage.Utils;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's entries for what the game has a key for but its radial definitions leave out, after the game's
    /// own entries (theirs keep their places):
    /// <list type="bullet">
    /// <item>"Remote access", on the Menu page beside the game's other screens: the remote access screen
    /// (MyGuiScreenGamePlay.cs:828 REMOTE_ACCESS_MENU -> controlledEntity.ShowRemoteAccessMenu), on foot or from a seat.</item>
    /// <item>"Emotes", a page of the game's emotes: the emote switcher's own list, in its order, with its icons, each played by
    /// the switcher's own call (MyEmoteSwitcher.ActivateEmoteLinear: the hands emptied, then the animation). The game has no
    /// radial for them: its switcher (EMOTE_SWITCHER) is a held button whose four directions pick from a page of four, so a
    /// press opens nothing (MyEmoteSwitcher.HandleInput). Ten to a ring, then "More emotes". Greyed where the game would not
    /// play one (not the character on foot or a cockpit's pilot, or up a ladder: MyAnimationActivator.Activate), and an emote
    /// from a DLC the player lacks is greyed as the switcher greys it.</item>
    /// </list>
    /// Default mount point, on the build ring, is <see cref="BuildEntries"/>'s.
    /// An entry for a control presses the game's own control as the wrist panel's tiles do (<see cref="WristButton.Press"/>): the
    /// wheel closes, and the gameplay screen's next input pass is told the control was pressed, once, so the game does what the
    /// key does, with its own checks and sounds.
    /// </summary>
    internal static class GameControlEntries
    {
        private const string Hud = GuiTextures.Hud;
        private const string BackIcon = GuiTextures.Back;

        /// <summary>The HUD's remote with its signal, the game's picture for remote control.</summary>
        private const string RemoteIcon = Hud + "RemoteControlON.png";

        /// <summary>The game's Menu page (its screens: blueprints, the admin and spawn menus, the players, the chat).</summary>
        private static readonly MyStringId MenuPage = MyStringId.GetOrCompute("RadialMenuGroupTitle_Menu");

        /// <summary>Emotes on one ring: with Back and "More emotes" it is the wheel's full ring of 12 before it grows.</summary>
        private const int EmotesPerRing = 10;

        // The emote switcher's list and calls, which it keeps to itself (MyEmoteSwitcher).
        private static FieldInfo animations, definition;
        private static MethodInfo activate, hasDlcs;
        private static bool emotesBroken;

        /// <summary>Adds the entries to the wheel as it opens. Game thread.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top)
        {
            AddRemoteAccess(top);
            AddEmotes(top);
        }

        /// <summary>An entry that presses a game control once the wheel has closed.</summary>
        private static ActionsWheel.Entry Control(string name, string icon, MyStringId control, Func<string> stateText) =>
            new ActionsWheel.Entry
            {
                Name = name,
                Icon = icon,
                StateText = stateText,
                Run = () => WristButton.Press(control, name + " (actions wheel)"),
            };

        private static void AddRemoteAccess(List<ActionsWheel.Entry> top)
        {
            ActionsWheel.Entry entry = Control("Remote access", RemoteIcon, MyControlsSpace.REMOTE_ACCESS_MENU, () => "Over the antennas");
            string menu = MyTexts.GetString(MenuPage);
            ActionsWheel.Entry page = top.Find(e => e.Page != null && e.Name == menu);
            (page?.Page ?? top).Add(entry);
        }

        // ---- Emotes ----

        private static void AddEmotes(List<ActionsWheel.Entry> top)
        {
            MyEmoteSwitcher switcher = MySession.Static?.GetComponent<MyEmoteSwitcher>();
            if (switcher == null || switcher.AnimationCount == 0 || !HookEmotes())
                return;
            var list = (IList)animations.GetValue(switcher);
            var emotes = new List<ActionsWheel.Entry>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (!(definition.GetValue(list[i]) is MyDefinitionBase emote))
                    continue;
                int index = i;
                string name = string.IsNullOrEmpty(emote.DisplayNameText) ? emote.Id.SubtypeName : emote.DisplayNameText;
                bool owned = HasDlcs(switcher, index);
                emotes.Add(new ActionsWheel.Entry
                {
                    Name = name,
                    Icon = switcher.GetIconLinear(index),
                    Run = () => Play(switcher, index, name),
                    CanUse = () => owned && CanEmote(),
                    StateText = owned ? (Func<string>)null : () => "DLC",
                });
            }
            if (emotes.Count == 0)
                return;
            top.Add(new ActionsWheel.Entry
            {
                Name = "Emotes",
                Icon = emotes[0].Icon,
                Page = Ring(emotes, 0),
                CanUse = CanEmote,
                StateText = () => $"{emotes.Count} emotes",
            });
        }

        /// <summary>One ring of emotes from <paramref name="from"/>: Back, up to <see cref="EmotesPerRing"/>, and the next ring as "More emotes" (one left over goes on this ring).</summary>
        private static List<ActionsWheel.Entry> Ring(List<ActionsWheel.Entry> emotes, int from)
        {
            var ring = new List<ActionsWheel.Entry> { new ActionsWheel.Entry { Name = "Back", Icon = BackIcon, IsBack = true } };
            int to = emotes.Count - from <= EmotesPerRing + 1 ? emotes.Count : from + EmotesPerRing;
            ring.AddRange(emotes.GetRange(from, to - from));
            if (to < emotes.Count)
            {
                int left = emotes.Count - to;
                ring.Add(new ActionsWheel.Entry
                {
                    Name = "More emotes",
                    Icon = emotes[to].Icon,
                    Page = Ring(emotes, to),
                    CanUse = CanEmote,
                    StateText = () => $"{left} more",
                });
            }
            return ring;
        }

        /// <summary>The game plays an emote on the character on foot, or on a cockpit's pilot, and not up a ladder (MyAnimationActivator.Activate).</summary>
        private static bool CanEmote()
        {
            IMyControllableEntity controlled = MySession.Static?.ControlledEntity;
            MyCharacter character = controlled is MyCockpit cockpit ? cockpit.Pilot : controlled as MyCharacter;
            return character != null && !character.IsDead && !character.IsOnLadder;
        }

        private static void Play(MyEmoteSwitcher switcher, int index, string name)
        {
            activate.Invoke(switcher, new object[] { index });
            Log.Info($"Actions wheel: emote '{name}' ({index + 1} of {switcher.AnimationCount}) played through the game's emote switcher");
        }

        private static bool HasDlcs(MyEmoteSwitcher switcher, int index)
        {
            try
            {
                return (bool)hasDlcs.Invoke(switcher, new object[] { index, null });
            }
            catch (Exception)
            {
                return true; // the game's own call checks again as the emote is played
            }
        }

        private static bool HookEmotes()
        {
            if (activate != null)
                return true;
            if (emotesBroken)
                return false;
            Type switcher = typeof(MyEmoteSwitcher);
            Type prioritized = AccessTools.Inner(switcher, "MyPrioritizedDefinition");
            animations = AccessTools.Field(switcher, "m_animations");
            definition = prioritized == null ? null : AccessTools.Field(prioritized, "Definition");
            hasDlcs = AccessTools.Method(switcher, "HasNecessaryDLCsLinear", new[] { typeof(int), typeof(string).MakeByRefType() });
            MethodInfo play = AccessTools.Method(switcher, "ActivateEmoteLinear", new[] { typeof(int) });
            if (animations == null || definition == null || hasDlcs == null || play == null)
            {
                emotesBroken = true;
                Log.Warn("Actions wheel: the game's emote switcher could not be read; no Emotes page");
                return false;
            }
            activate = play;
            return true;
        }
    }
}

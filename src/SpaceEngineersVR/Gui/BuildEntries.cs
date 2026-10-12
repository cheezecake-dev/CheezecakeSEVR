using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.GUI;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using VRage.Audio;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The actions wheel's own entries for building, after the game's:
    /// <list type="bullet">
    /// <item>"Default mount point", on the SystemBuild ring (a block in hand): the game's CUBE_DEFAULT_MOUNTPOINT (T), which
    /// has no button of its own on a controller. On, the block in hand turns to sit on its default face against what it is
    /// put on until it is turned by hand. Leaves the wheel up for another go, as the game's switches do. The game keeps it in
    /// its own config (MyCubeBuilder.AlignToDefault), so it holds from one session to the next.</item>
    /// <item>"Voxel material", while the voxel hand is in hand (creative): a page of the game's own VoxelHand radial (the one
    /// its VOXEL_MATERIAL_SELECT opens), its last-used materials on the page itself and each group of materials a ring of
    /// its own. Choosing one sets the brush's material and puts it first among the last used, as the game's radial does.</item>
    /// </list>
    /// </summary>
    internal static class BuildEntries
    {
        private const string MountIcon = "Textures\\GUI\\Icons\\HUD 2017\\BlockRotate.png";
        private const string VoxelIcon = "Textures\\GUI\\Icons\\HUD 2017\\SetupVoxelHand.png";
        private const string BackIcon = GuiTextures.Back;
        private const string RadialComponentType = "Sandbox.Game.Screens.Helpers.MyRadialMenuComponent";

        private static MethodInfo pushLastUsedVoxel;

        /// <summary>Adds the entries that fit what is in hand to the end of the top ring. Game thread, as the wheel opens.</summary>
        internal static void AddTo(List<ActionsWheel.Entry> top, string subtype)
        {
            try
            {
                if (subtype == "SystemBuild")
                    top.Add(MountPoint());
                if (MySessionComponentVoxelHand.Static?.Enabled == true)
                {
                    ActionsWheel.Entry voxel = VoxelMaterials();
                    if (voxel != null)
                        top.Add(voxel);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Actions wheel: the building entries could not be added");
            }
        }

        private static ActionsWheel.Entry MountPoint() => new ActionsWheel.Entry
        {
            Name = "Default mount point",
            Icon = MountIcon,
            Stays = true,
            CanUse = () => MyCubeBuilder.Static != null,
            StateText = () => MyCubeBuilder.Static?.AlignToDefault == true ? "On" : "Off",
            Run = () =>
            {
                MyCubeBuilder builder = MyCubeBuilder.Static;
                if (builder == null)
                    return;
                // The setter is the game's own: it saves the game's config, as the T key does.
                builder.AlignToDefault = !builder.AlignToDefault;
                MyGuiAudio.PlaySound(MyGuiSounds.HudClick);
                Log.Info($"Build: default mount point {(builder.AlignToDefault ? "on" : "off")} (the game's CUBE_DEFAULT_MOUNTPOINT, T; saved to the game's config)");
            },
        };

        /// <summary>The game's VoxelHand radial as a page; null when the game has none.</summary>
        private static ActionsWheel.Entry VoxelMaterials()
        {
            MyRadialMenu menu = MyDefinitionManager.Static?.GetRadialMenuDefinition("VoxelHand");
            if (menu == null)
            {
                Log.Warn("Actions wheel: the game has no VoxelHand radial menu");
                return null;
            }
            List<ActionsWheel.Entry> entries = ActionsWheel.Build(menu, VRSettings.ThirdPerson, out _);
            entries.RemoveAll(entry => entry.IsInventory);
            if (entries.Count == 0)
                return null;
            Choose(entries);
            var page = new List<ActionsWheel.Entry> { new ActionsWheel.Entry { Name = "Back", Icon = BackIcon, IsBack = true } };
            page.AddRange(entries);
            return new ActionsWheel.Entry
            {
                Name = "Voxel material",
                Icon = CurrentIcon(),
                Page = page,
                StateText = BuildMode.VoxelMaterialName,
            };
        }

        /// <summary>Each material, on this ring and the rings under it, is the game's item plus its place among the last used.</summary>
        private static void Choose(List<ActionsWheel.Entry> entries)
        {
            foreach (ActionsWheel.Entry entry in entries)
            {
                if (entry.Page != null)
                {
                    Choose(entry.Page);
                    continue;
                }
                MyRadialMenuItem item = entry.Item;
                if (item == null)
                    continue;
                string name = entry.Name;
                entry.Run = () =>
                {
                    string before = BuildMode.VoxelMaterialName();
                    item.Activate();
                    PushLastUsed(item);
                    Log.Info($"Voxel hand: material {before} -> {BuildMode.VoxelMaterialName()} (chose {name} on the wheel)");
                };
            }
        }

        /// <summary>As the game's voxel radial (MyGuiControlRadialMenuVoxel.ActivateItem): the material goes first among the last used.</summary>
        private static void PushLastUsed(MyRadialMenuItem item)
        {
            try
            {
                Type type = AccessTools.TypeByName(RadialComponentType);
                object component = type == null ? null : MySession.Static?.GetComponent(type);
                if (component == null)
                    return;
                if (pushLastUsedVoxel == null)
                    pushLastUsedVoxel = AccessTools.Method(type, "PushLastUsedVoxel");
                // The game's method takes only a material item (null for anything else, which it leaves alone).
                ParameterInfo[] parameters = pushLastUsedVoxel?.GetParameters();
                if (parameters == null || parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(item))
                    return;
                pushLastUsedVoxel.Invoke(component, new object[] { item });
            }
            catch (Exception e)
            {
                Log.Error(e, "Voxel hand: the material could not be put among the last used");
            }
        }

        private static string CurrentIcon()
        {
            string icon = MySessionComponentVoxelHand.Static?.CurrentMaterialTextureName;
            return string.IsNullOrEmpty(icon) ? VoxelIcon : icon;
        }
    }
}

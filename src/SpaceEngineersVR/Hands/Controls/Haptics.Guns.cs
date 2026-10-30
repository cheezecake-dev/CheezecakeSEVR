// Haptics, guns: each shot felt as the gun it comes from, and the click of an empty one.
using System;
using System.Diagnostics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SpaceEngineersVR.Input;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Haptics
    {
        // ---- Guns: the shot's pulse from the gun's own figures, so each weapon feels like itself ----

        /// <summary>
        /// The pulse's strength: from the least, for a gun with no climb, rising with the gun's own vertical recoil
        /// (MyWeaponDefinition.RecoilGroundVertical, degrees) to full at <see cref="RecoilForFull"/>.
        /// </summary>
        public const float ShotLeastAmplitude = 0.5f, RecoilForFull = 8f;

        /// <summary>
        /// The pulse's length: this share of the time between shots at the gun's rate of fire (WeaponProperties.CurrentWeaponRateOfFire),
        /// so full auto is a rhythm of short pulses with gaps between and a slow gun a longer push, kept within these.
        /// </summary>
        public const float ShotShareOfInterval = 0.4f, ShotShortest = 0.025f, ShotLongest = 0.09f;

        /// <summary>A launcher's shot: a long heavy thump, whatever its figures.</summary>
        public const float LauncherAmplitude = 1f, LauncherSeconds = 0.2f;

        /// <summary>The hand on the gun's fore grip (<see cref="TwoHand"/>) feels this much of the shot.</summary>
        public const float SupportShare = 0.6f;

        /// <summary>The empty gun's click: light and short, at most this often while the trigger is held.</summary>
        public const float DryAmplitude = 0.3f, DrySeconds = 0.015f, DryEverySeconds = 0.25f;

        private static long dryAt;

        /// <summary>
        /// One shot of <paramref name="gun"/>, felt in the hand that holds it as that gun: as strong as its recoil climbs, as long
        /// as its rate of fire leaves room for, a launcher a heavy thump; and in the other hand too while it holds the gun's fore
        /// grip. While the gun's own kick has its pulse playing on the tool hand (<see cref="Kickback.Rumbling"/>, a gun made for two
        /// hands fired with one), that hand keeps the kick's.
        /// </summary>
        internal static void Shot(MyAutomaticRifleGun gun)
        {
            float amplitude, seconds;
            string magazine = gun.GunBase?.CurrentAmmoMagazineDefinition?.Id.SubtypeName ?? string.Empty;
            if (magazine.IndexOf("Missile", StringComparison.OrdinalIgnoreCase) >= 0 || magazine.IndexOf("Rocket", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                amplitude = LauncherAmplitude;
                seconds = LauncherSeconds;
            }
            else
            {
                // A gun with no climb of its own takes the kick's typical one, as the kick does (Kickback.ShotRecoil).
                float recoil = gun.GunBase?.WeaponDefinition?.RecoilGroundVertical ?? 0f;
                if (!(recoil > 0f))
                    recoil = (float)Kickback.GunFallbackVertical;
                amplitude = MathHelper.Lerp(ShotLeastAmplitude, 1f, MathHelper.Clamp(recoil / RecoilForFull, 0f, 1f));
                float rate = gun.GunBase?.WeaponProperties?.CurrentWeaponRateOfFire ?? 0f;
                seconds = rate > 0f ? MathHelper.Clamp(ShotShareOfInterval * 60f / rate, ShotShortest, ShotLongest) : ShotLongest;
            }
            if (!Kickback.Rumbling)
                Pulse(ToolHand, amplitude, seconds);
            if (TwoHand.Holds(gun))
                Pulse(TwoHand.OffHand, amplitude * SupportShare, seconds);
        }

        /// <summary>
        /// The game's reaction to a shot it refused (MyAutomaticRifleGun.ShootFailReactionLocal, which plays the gun's own empty
        /// click): when the refusal is for no ammunition (a magazine out or none left), a light click in the trigger hand too, at
        /// most every <see cref="DryEverySeconds"/>. The HUD already shows the empty magazine, so it tells nothing new.
        /// </summary>
        private static readonly FeatureGuard dryGuard = new FeatureGuard(nameof(Haptics) + " empty click");

        private static void AfterShootFailed(MyAutomaticRifleGun __instance, object[] __args)
        {
            if (!dryGuard.On)
                return;
            try
            {
                if (__args == null || __args.Length < 2 || !(__args[0] is MyShootActionEnum action) || action != MyShootActionEnum.PrimaryAction
                    || __args[1]?.ToString() != "OutOfAmmo" || !IsLocal(__instance.Owner))
                    return;
                long now = Stopwatch.GetTimestamp();
                if (now - dryAt < (long)(DryEverySeconds * Stopwatch.Frequency))
                    return;
                dryAt = now;
                Pulse(ToolHand, DryAmplitude, DrySeconds);
            }
            catch (Exception e)
            {
                dryGuard.Failed(e, "Haptics (empty click) failed");
            }
        }

        private static void PatchGuns(HarmonyLib.Harmony harmony) =>
            Hook(harmony, "empty click", () => HarmonyLib.AccessTools.Method(typeof(MyAutomaticRifleGun), "ShootFailReactionLocal"), nameof(AfterShootFailed));
    }
}

using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character.Components;
using VRage.Game.Entity.UseObject;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The use object the game acts on is whatever the character's detector component answers (<c>UseObject</c>): the Use key's
    /// <c>MyCharacter.Use</c>, the build planner's take, dump and production, the terminal and inventory screens. Both detector
    /// classes answer through it (MyCharacterClosestDetectorComponent overrides it). <see cref="With"/> runs one game call with
    /// the detector answering with a chosen object, which is how a fingertip, a grab, the quick inventory and the wrist panel
    /// act on what the hand is on rather than what the head looks at. One patch of each getter serves them all.
    /// </summary>
    internal static class DetectorAnswer
    {
        /// <summary>The getters are patched; without them nothing can be answered, and the features that need it are off.</summary>
        public static bool Hooked { get; private set; }

        /// <summary>The game's closest-use detector (internal to the game), the one a character on foot has. Throws when the game no longer has it.</summary>
        public static Type Closest =>
            closest ??= AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                        ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");

        private static Type closest;

        /// <summary>A call running on this thread has the detector answer with <see cref="answer"/>.</summary>
        [ThreadStatic]
        private static bool answering;

        /// <summary>The use object answered for that call; null: nothing, as when the use ray is on nothing.</summary>
        [ThreadStatic]
        private static IMyUseObject answer;

        public static void Patch(Harmony harmony)
        {
            var getters = new[]
            {
                AccessTools.PropertyGetter(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UseObject))
                    ?? throw new MissingMemberException(nameof(MyCharacterDetectorComponent), "UseObject"),
                AccessTools.DeclaredProperty(Closest, nameof(MyCharacterDetectorComponent.UseObject))?.GetGetMethod(true)
                    ?? throw new MissingMemberException(Closest.Name, "UseObject"),
            };
            foreach (var getter in getters)
                harmony.Patch(getter, postfix: new HarmonyMethod(typeof(DetectorAnswer), nameof(Answer)));
            Hooked = true;
            Log.Info("Detector answer: the character's use object can be answered for one call (detector UseObject x2)");
        }

        /// <summary>
        /// For a feature's patching: throws when the getters are not patched, so the feature comes off whole
        /// (<see cref="PatchRegistry.Apply"/>) rather than acting on what the head looks at.
        /// </summary>
        public static void Require(string feature)
        {
            if (!Hooked)
                throw new InvalidOperationException($"{feature} needs the detector's answer, which could not be hooked");
        }

        private static void Answer(ref IMyUseObject __result)
        {
            if (answering)
                __result = answer;
        }

        /// <summary>
        /// Runs <paramref name="call"/> with the detector answering with <paramref name="use"/> on this thread, for the length of
        /// the call; null answers nothing, so the game acts as it does with nothing in reach (the terminal and inventory screens
        /// then open the character's own). A call made inside another's answers with its own and gives the outer one back after it.
        /// </summary>
        public static void With(IMyUseObject use, Action call)
        {
            bool outerAnswering = answering;
            IMyUseObject outer = answer;
            answering = true;
            answer = use;
            try
            {
                call();
            }
            finally
            {
                answering = outerAnswering;
                answer = outer;
            }
        }
    }
}

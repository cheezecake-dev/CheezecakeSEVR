// Small reflection and IL helpers shared by the features that bind to the game's private renderer types.

using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SpaceEngineersVR.Rendering
{
    internal static class ReflectionUtil
    {
        /// <summary>
        /// Starts a dynamic method that may touch non-public members of <paramref name="owner"/>'s module, and returns its
        /// IL generator. Used to call renderer internals without a compile-time reference to them.
        /// </summary>
        public static ILGenerator Begin(string name, Type returns, Type[] parameters, Type owner, out DynamicMethod method)
        {
            method = new DynamicMethod(name, returns, parameters, owner, skipVisibility: true);
            return method.GetILGenerator();
        }

        /// <summary>The field, or a <see cref="MissingFieldException"/> naming it: a game update that renamed it fails loudly here.</summary>
        public static FieldInfo Field(Type type, string name) =>
            AccessTools.Field(type, name) ?? throw new MissingFieldException(type.Name, name);

        public static FieldInfo[] Fields(Type type, params string[] names) =>
            names.Select(name => Field(type, name)).ToArray();

        /// <summary>The reflected member, or a <see cref="MissingMemberException"/> naming it.</summary>
        public static T Need<T>(T member, string name) where T : class =>
            member ?? throw new MissingMemberException(name);
    }
}

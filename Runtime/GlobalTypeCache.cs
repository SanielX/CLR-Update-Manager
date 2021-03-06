using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HostGame
{
    /// <summary>
    /// Utility that allows caching/finding classess by their full name (e.g. UnityEngine.GameObject).
    /// Basically same as <see cref="UnityEditor.TypeCache"/> but for runtime
    /// </summary>
    /// TODO: Actually could save all types to an array and just deserialize it at runtime
    /// In case of additionally loaded assemblies (who does that?) just do default search
    public static class GlobalTypeCache
    {
        private static Dictionary<string, Type> CachedTypes = new Dictionary<string, Type>();
        private static Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        public static void UpdateAssembliesList()
            => assemblies = AppDomain.CurrentDomain.GetAssemblies();
        
        /// <summary>
        /// Finds type in all assemblies by name.
        /// Caches result even if it was null.
        /// </summary>
        public static Type FindType(string fullName)
        {
            if (fullName is null)
                return null;

            Type result;
            if (CachedTypes.TryGetValue(fullName, out result))
                return result;

            foreach (var assembly in assemblies)
            {
                result = assembly.GetType(fullName, false);
                
                if (result != null)
                {
                    CachedTypes[fullName] = result;
                    return result;
                }
            }

            CachedTypes[fullName] = null;
            return null;
        }

        /// <summary>
        /// Returns all types with class/struct name matching given string.
        /// </summary>
        /// <returns>
        /// Null if none matching type found or a list with 1 or more results
        /// </returns>
        public static List<Type> FindTypesByName(string name)
        {
            List<Type> result = null;

            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name == name)
                    {
                        if (result is null) result = new List<Type>(1);
                        result.Add(type);
                    }
                }
            }

            return result;
        }

        internal static void CacheCLRScripts()
        {
            foreach (var asm in assemblies)
            {
                if (asm.FullName.StartsWith("Unity."))
                    continue;

                foreach (var type in asm.GetTypes())
                {
                    if ((type.IsAbstract && type.IsSealed) || type.IsValueType || type.IsInterface)
                        continue;

                    if (typeof(CLRScript).IsAssignableFrom(type))
                    {
                        CachedTypes[type.FullName] = type;
                    }
                }
            }
        }
    }
}

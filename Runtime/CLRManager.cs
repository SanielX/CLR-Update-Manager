using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Profiling;

using Debug = UnityEngine.Debug;

namespace HostGame
{
    internal class CLRManager : MonoBehaviour
    {
        [Serializable]  // Just to be able to view it in editor
        internal class Bucket  // All scripts within bucket have same type
        {
            public Type ScriptType;
            public int ExecutionIndex;
            public List<CLRScript> ScriptList;
        }

#if UNITY_EDITOR

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            if (instance != null)
            {
                Destroy(instance);
            }

            instance = null;
        }

#endif

        private readonly struct InitCommand
        {
            public InitCommand(CLRScript script, bool addOrRemove)
            {
                this.script = script;
                this.addOrRemove = addOrRemove;
            }

            public readonly CLRScript script;
            public readonly bool addOrRemove;  // true is add, false is remove
        }

        // This is huge hack imo, but otherwise we would get GC on every update
        private enum CallbackType
        {
            ManagedStart = -1, 
            Update = 0,
            FixedUpdate, 
            LateUpdate,
            PreUpdate, 
            EarlyUpdate,

            Count
        };

        #region Profiler  

#if ENABLE_PROFILER
        private Dictionary<Type, string[]> profileStrings = new Dictionary<Type, string[]>();

        private void AddTypeToProfileStrings(Type t, out string[] result)
        {
            string[] strings = new string[6];
            strings[0] = $"{t.Name}.OnManagedStart()";
            strings[1] = $"{t.Name}.OnUpdate()";
            strings[2] = $"{t.Name}.OnFixedUpdate()";
            strings[3] = $"{t.Name}.OnLateUpdate()";
            strings[4] = $"{t.Name}.OnPreUpdate()";
            strings[5] = $"{t.Name}.OnEarlyUpdate()";

            profileStrings[t] = strings;
            result = strings;
        }

#endif

        private string GetProfileString(Type t, CallbackType callback)
        {
#if ENABLE_PROFILER
            if (!profileStrings.TryGetValue(t, out var strings))
                AddTypeToProfileStrings(t, out strings);

            return strings[(int)callback + 1];
#else
            return null;
#endif
        }

        [Conditional("ENABLE_PROFILER")]
        private void BeginProfileSample(Type t, CallbackType type)
        {
            Profiler.BeginSample(GetProfileString(t, type));
        }

        [Conditional("ENABLE_PROFILER")]
        private void EndProfileSample()
        {
            Profiler.EndSample();
        }

        #endregion

        private static CLRManager instance;

        public static CLRManager Instance
        {
            get
            {
                if (!instance)
                {
                    var go = new GameObject();
                    go.hideFlags = HideFlags.DontSave;
                    go.name = "CLR Manager";
                    instance = go.AddComponent<CLRManager>();
#if UNITY_EDITOR
                    if (Application.isPlaying)
#endif
                        DontDestroyOnLoad(go);
                }

                return instance;
            }
        }

        private bool mayNeedGC = false;  // sets to true after any script was removed from any list
                                         // this allows to run deletion of buckets only when it may have sense, instead of just every N frame
        private Queue<InitCommand> initQueue = new Queue<InitCommand>(256);

        private int zeroPriorityIndexUpd = 0;
        private Dictionary<Type, Bucket> managedTypeToBucket = new Dictionary<Type, Bucket>(8);  // Having dictionary allows to add scripts as O(1) operation
        private List<Bucket> managedScripts = new List<Bucket>(8);

        private int zeroPriorityIndexLt = 0;
        private Dictionary<Type, Bucket> managedLateTypeToBucket = new Dictionary<Type, Bucket>(4);
        private List<Bucket> managedLateScripts = new List<Bucket>(4);

        private int zeroPriorityIndexFx = 0;
        private Dictionary<Type, Bucket> managedFixedTypeToBucket = new Dictionary<Type, Bucket>(4);
        private List<Bucket> managedFixedScripts = new List<Bucket>(4);

        private int zeroPriorityIndexPr = 0;
        private Dictionary<Type, Bucket> managedPreTypeToBucket = new Dictionary<Type, Bucket>(1);
        private List<Bucket> managedPreScripts = new List<Bucket>(1);

        private int zeroPriorityIndexEr = 0;
        private Dictionary<Type, Bucket> managedEarlyTypeToBucket = new Dictionary<Type, Bucket>(1);
        private List<Bucket> managedEarlyScripts = new List<Bucket>(1);

        internal static void Add(CLRScript script)
        {
            // TODO: Support for ExecuteAlways code?
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
#endif 
            Instance.initQueue.Enqueue(new InitCommand(script, true));
        }

        internal static void Remove(CLRScript script)
        {
#if UNITY_EDITOR
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
#endif 
            Instance.initQueue.Enqueue(new InitCommand(script, false));
        }

        private void EarlyUpdate()
        {
            // Replicating OnEnable/Disable calls in order they came
            while (initQueue.Count > 0)
            {
                var command = initQueue.Dequeue();
                CLRScript clrScript = command.script;

                if (clrScript is null) // Use real null check, not unity check
                    continue;

                if (command.addOrRemove == false) // remove even if destroyed
                {
                    RemoveScript(clrScript, clrScript.__setupFlags);
                    continue;
                }

                if(clrScript) // add if not destroyed
                    AddScript(clrScript);
            }

            // TODO: Make it work
            if (mayNeedGC &&
               (CLRManagerSettings.BucketGCFrequency != -1 &&
                Time.frameCount % CLRManagerSettings.BucketGCFrequency == 0))
            {
                RemoveEmptyBuckets(managedEarlyScripts, managedEarlyTypeToBucket);
                RemoveEmptyBuckets(managedPreScripts, managedPreTypeToBucket);
                RemoveEmptyBuckets(managedFixedScripts, managedFixedTypeToBucket);
                RemoveEmptyBuckets(managedScripts, managedTypeToBucket);
                RemoveEmptyBuckets(managedLateScripts, managedLateTypeToBucket);
                mayNeedGC = false;
            }

            UpdateList(managedPreScripts, CallbackType.EarlyUpdate);
        }

        private void PreUpdate()
        {
            UpdateList(managedPreScripts, CallbackType.PreUpdate);
        }

        private void Update()
        {
            UpdateList(managedScripts, CallbackType.Update);
        }

        private void LateUpdate()
        {
            UpdateList(managedLateScripts, CallbackType.LateUpdate);
        }

        private void FixedUpdate()
        {
            UpdateList(managedFixedScripts, CallbackType.FixedUpdate);
        }

        // Funny enough, compiler should inline this method and remove switch!
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateList(List<Bucket> buckets, CallbackType callback)
        {
            for (int i = 0; i < buckets.Count; i++)
            {
                for (int j = 0; j < buckets[i].ScriptList.Count; j++)
                {
                    CLRScript clrScript = buckets[i].ScriptList[j];  // I'd still like to make Span out of list and avoid bound check tho

                    if (IsScriptActive(clrScript))
                    {
#if UNITY_ASSERTIONS
                        try
                        {
#endif
                            BeginProfileSample(clrScript.ScriptType, callback);

                            switch (callback)
                            {
                            case CallbackType.Update:
                                clrScript.OnUpdate();
                                break;

                            case CallbackType.FixedUpdate:
                                clrScript.OnFixedUpdate();
                                break;

                            case CallbackType.LateUpdate:
                                clrScript.OnLateUpdate();
                                break;

                            case CallbackType.PreUpdate:
                                clrScript.OnPreUpdate();
                                break;

                            case CallbackType.EarlyUpdate:
                                clrScript.OnEarlyUpdate();
                                break;
                            }

                            EndProfileSample();

#if UNITY_ASSERTIONS
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
#endif 
                        }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveEmptyBuckets(List<Bucket> buckets, Dictionary<Type, Bucket> bucketDict)
        {
            for (int i = 0; i < buckets.Count; i++)
            {
                if(buckets[i].ScriptList.Count == 0)
                {
                    bucketDict.Remove(buckets[i].ScriptType);
                    buckets.RemoveAt(i);
                    i--;
                    continue;
                }
            }
        }

#if UNITY_EDITOR

        // This is for AppDomain disabled
        private void OnApplicationQuit()
        {
            DestroyImmediate(gameObject);
        }

#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsScriptActive(CLRScript s)
        {
            // Using real null check instead of what unity overrides
            // Do unity-null check if script doesn't mind it
            return s is { /* not null */ } && ((s.__setupFlags & CLRSetupFlags.NoSafetyChecks) != 0 || (s && s.enabled && s.gameObject && s.gameObject.activeInHierarchy));
        }

        #region Script List Handeling

        private void AddScript(CLRScript script)
        {
            CLRSetupFlags usedCalls = script.__setupFlags;
            if (usedCalls.HasFlag(CLRSetupFlags.Update))
                AddScriptToBucket(script, managedTypeToBucket, managedScripts, ref zeroPriorityIndexUpd);

            if (usedCalls.HasFlag(CLRSetupFlags.LateUpdate))
                AddScriptToBucket(script, managedLateTypeToBucket, managedLateScripts, ref zeroPriorityIndexLt);

            if (usedCalls.HasFlag(CLRSetupFlags.FixedUpdate))
                AddScriptToBucket(script, managedFixedTypeToBucket, managedFixedScripts, ref zeroPriorityIndexFx);

            if (usedCalls.HasFlag(CLRSetupFlags.PreUpdate))
                AddScriptToBucket(script, managedPreTypeToBucket, managedPreScripts, ref zeroPriorityIndexPr);

            if (usedCalls.HasFlag(CLRSetupFlags.EarlyUpdate))
                AddScriptToBucket(script, managedEarlyTypeToBucket, managedEarlyScripts, ref zeroPriorityIndexEr);
        }

        private void RemoveScript(CLRScript script, CLRSetupFlags callsToRemove)
        {
            if (callsToRemove.HasFlag(CLRSetupFlags.Update))
                RemoveScriptFromList(script, managedTypeToBucket, ref zeroPriorityIndexUpd);

            if (callsToRemove.HasFlag(CLRSetupFlags.LateUpdate))
                RemoveScriptFromList(script, managedLateTypeToBucket, ref zeroPriorityIndexLt);

            if (callsToRemove.HasFlag(CLRSetupFlags.FixedUpdate))
                RemoveScriptFromList(script, managedFixedTypeToBucket, ref zeroPriorityIndexFx);

            if (callsToRemove.HasFlag(CLRSetupFlags.PreUpdate))
                RemoveScriptFromList(script, managedPreTypeToBucket, ref zeroPriorityIndexPr);

            if (callsToRemove.HasFlag(CLRSetupFlags.EarlyUpdate))
                RemoveScriptFromList(script, managedEarlyTypeToBucket, ref zeroPriorityIndexEr);

            mayNeedGC = true; 
        }

        private void RemoveScriptFromList(CLRScript script, Dictionary<Type, Bucket> dict, ref int zeroPointIndex)
        {
            if (!dict.TryGetValue(script.ScriptType, out var bucket))
            {
#if UNITY_EDITOR
                if(UnityEditor.EditorApplication.isPlaying) // It does spam about deleting destoyed stuff
#endif
                Debug.LogError($"Script {script} can not be removed because it was never added", script);
                return;
            }

            int index = bucket.ScriptList.IndexOf(script);
            if (index == -1)
                return;

            if (zeroPointIndex > 0 && index <= zeroPointIndex)
                zeroPointIndex--;

            bucket.ScriptList.RemoveAt(index);
        }

        private static void AddScriptToBucket(CLRScript script, Dictionary<Type, Bucket> dict, List<Bucket> bucketList, ref int index)
        {
            if (dict.TryGetValue(script.ScriptType, out var bucket))
            {
                bucket.ScriptList.Add(script);
                return;
            }

            bucket = new Bucket()
            {
                ScriptType = script.ScriptType,
                ExecutionIndex = script.ExecutionIndex,
                ScriptList = new List<CLRScript>(16)
            };
            bucket.ScriptList.Add(script);

            dict[script.ScriptType] = bucket;
            AddBucketToList(bucket, bucketList, ref index);
        }

        private static void AddBucketToList(Bucket script, List<Bucket> scriptsList, ref int startIndex)
        {
            if (scriptsList.Count == 0)
            {
                scriptsList.Add(script);

                if (script.ExecutionIndex < 0)
                    startIndex++;
                return;
            }

            int seekIndex = script.ExecutionIndex;
            System.Type scriptType = script.ScriptType;

            int i = Mathf.Max(0, startIndex - 1);
            int sanityCheck = 0;
            int maxIterations = scriptsList.Count * 2;
            while (sanityCheck < maxIterations)
            {
                sanityCheck++;
                if (sanityCheck > maxIterations)
                {
                    Debug.LogError("Something is wrong");
                    break;
                }

                var currentScript = scriptsList[i];

                if (seekIndex >= currentScript.ExecutionIndex)
                {
                    var nextScript = i + 1 < scriptsList.Count ? scriptsList[i + 1] : null;

                    if (nextScript == null)
                    {
                        scriptsList.Add(script);
                        ++i;
                        break;
                    }
                    else if (nextScript.ScriptType != scriptType)
                    {
                        if (nextScript.ExecutionIndex <= seekIndex)
                        {
                            ++i;
                            continue;
                        }
                        else
                        {
                            scriptsList.Insert(++i, script);
                            break;
                        }
                    }
                    else
                    {
                        scriptsList.Insert(++i, script);
                        break;
                    }
                }

                if (seekIndex < currentScript.ExecutionIndex)
                {
                    var prevScript = i > 0 ? scriptsList[i - 1] : null;

                    if (prevScript == null)
                    {
                        scriptsList.Insert(0, script);
                        break;
                    }
                    else if (prevScript.ScriptType != scriptType)
                    {
                        if (prevScript.ExecutionIndex < seekIndex)
                        {
                            scriptsList.Insert(--i, script);
                            break;
                        }
                        else
                        {
                            --i;
                            continue;
                        }
                    }
                    else
                    {
                        scriptsList.Insert(--i, script);
                        break;
                    }
                }
            }

            if (i < startIndex)
                startIndex++;
        }

        #endregion Script List Handeling

        #region Player Loop Modification

        private struct CLRPreUpdate { }

        private struct CLREarlyUpdate { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterCustomUpdates()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();

            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                ref PlayerLoopSystem rootSubsystems = ref loop.subSystemList[i];

                if (rootSubsystems.type == typeof(EarlyUpdate))
                {
                    ref var subSystems = ref loop.subSystemList[i].subSystemList;
#if UNITY_EDITOR  // Protection against DomainReload off
                    if (FindSubsystem(subSystems, typeof(CLREarlyUpdate)))
                        continue;
#endif

                    Array.Resize(ref subSystems, rootSubsystems.subSystemList.Length + 1);
                    int index = subSystems.Length - 1;

                    // And injecting system at the end of default loop
                    subSystems[index] = new PlayerLoopSystem()
                    {
                        type = typeof(CLREarlyUpdate),
                        updateDelegate = EarlyUpdateCallback
                    };
                }

                if (rootSubsystems.type == typeof(PreUpdate))
                {
                    ref var subSystems = ref loop.subSystemList[i].subSystemList;
#if UNITY_EDITOR
                    if (FindSubsystem(subSystems, typeof(CLRPreUpdate)))
                        continue;
#endif
                    Array.Resize(ref subSystems, rootSubsystems.subSystemList.Length + 1);
                    int index = subSystems.Length - 1;

                    subSystems[index] = new PlayerLoopSystem()
                    {
                        type = typeof(CLRPreUpdate),
                        updateDelegate = PreUpdateCallback
                    };
                }
            }

            PlayerLoop.SetPlayerLoop(loop);

            static bool FindSubsystem(PlayerLoopSystem[] subSystems, Type systemType)
            {
                foreach (var subs in subSystems)
                {
                    if (subs.type == systemType)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static void PreUpdateCallback()
        {
            Instance.PreUpdate();
        }

        private static void EarlyUpdateCallback()
        {
            Instance.EarlyUpdate();
        }

        #endregion Player Loop Modification
    }
}
using System;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Mynell.Lottie
{
    /// <summary>
    /// Drives all active <see cref="LottiePlayer"/>s: advances time, schedules the render jobs right after LateUpdate
    /// (so they run on worker threads in parallel with the rest of the frame) and uploads the textures just before
    /// the frame is rendered.
    /// </summary>
    public static class LottieScheduler
    {
        static readonly List<LottiePlayer> s_Players = new List<LottiePlayer>();
        static readonly List<LottiePlayer> s_Snapshot = new List<LottiePlayer>();
        static readonly List<LottiePlayer> s_InFlight = new List<LottiePlayer>();
        static bool s_Installed;
        static double s_Clock;   // unscaled seconds advanced by the ticks (frame rate limit)

        struct LottieScheduleRenders { }
        struct LottieCompleteRenders { }

        /// <summary>Number of registered players.</summary>
        public static int PlayerCount => s_Players.Count;

        internal static void Register(LottiePlayer p)
        {
            Install();
            if (!s_Players.Contains(p)) s_Players.Add(p);
        }

        internal static void Unregister(LottiePlayer p)
        {
            s_Players.Remove(p);
            s_InFlight.Remove(p);
        }

        /// <summary>Waits for every in-flight render job (call before destroying data shared with jobs).</summary>
        public static void CompleteAll()
        {
            for (int i = 0; i < s_InFlight.Count; i++) s_InFlight[i].CompleteRender();
            s_InFlight.Clear();
        }

        /// <summary>Advances and renders all players immediately (edit mode / manual driving).</summary>
        public static void Tick(float deltaTime, float unscaledDeltaTime)
        {
            ScheduleAll(deltaTime, unscaledDeltaTime, true);
            CompleteAll();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration()
        {
            s_Installed = false;
            Install();
        }

        static void Install()
        {
            if (s_Installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveOurSystems(ref loop);
            bool a = Insert(ref loop, typeof(PreLateUpdate), null,
                new PlayerLoopSystem { type = typeof(LottieScheduleRenders), updateDelegate = OnScheduleRenders });
            bool b = Insert(ref loop, typeof(PostLateUpdate), typeof(PostLateUpdate.FinishFrameRendering),
                new PlayerLoopSystem { type = typeof(LottieCompleteRenders), updateDelegate = OnCompleteRenders });
            if (!a || !b) Debug.LogWarning("[Lottie] Could not hook into the player loop; call LottieScheduler.Tick manually.");
            PlayerLoop.SetPlayerLoop(loop);
            s_Installed = true;
        }

        static void OnScheduleRenders()
        {
            ScheduleAll(Time.deltaTime, Time.unscaledDeltaTime, Application.isPlaying);
        }

        static void OnCompleteRenders() => CompleteAll();

        static void ScheduleAll(float dt, float udt, bool advance)
        {
            if (s_Players.Count == 0) return;
            if (advance) s_Clock += udt;
            double now = advance ? s_Clock : double.NaN;
            s_Snapshot.Clear();
            s_Snapshot.AddRange(s_Players);
            bool any = false;
            // Share the worker threads between the players rendering this frame.
            int rendering = 0;
            for (int i = 0; i < s_Snapshot.Count; i++)
                if (!s_Snapshot[i].IsDisposed && (s_Snapshot[i].NeedsRender || s_Snapshot[i].IsPlaying)) rendering++;
            int threads = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount + 1;
            LottieRenderState.BandBudget = math.max(1, LottieRenderState.BandsPerThread * threads / math.max(1, rendering));
            for (int i = 0; i < s_Snapshot.Count; i++)
            {
                var p = s_Snapshot[i];
                if (p.IsDisposed)
                {
                    s_Players.Remove(p);
                    continue;
                }
                try
                {
                    if (advance && p.Active && p.AutoAdvance && p.IsPlaying) p.Advance(p.UnscaledTime ? udt : dt);
                    if (p.ScheduleRender(now))
                    {
                        s_InFlight.Add(p);
                        any = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            s_Snapshot.Clear();
            LottieRenderState.BandBudget = 0;
            if (any) JobHandle.ScheduleBatchedJobs();
        }

        // ------------------------------------------------------------------ player loop helpers

        static bool Insert(ref PlayerLoopSystem root, Type parent, Type before, PlayerLoopSystem system)
        {
            var subs = root.subSystemList;
            if (subs == null) return false;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != parent) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                int index = list.Count;
                if (before != null)
                    for (int k = 0; k < list.Count; k++)
                        if (list[k].type == before) { index = k; break; }
                list.Insert(index, system);
                subs[i].subSystemList = list.ToArray();
                return true;
            }
            return false;
        }

        static void RemoveOurSystems(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return;
            var list = new List<PlayerLoopSystem>(system.subSystemList.Length);
            foreach (var s in system.subSystemList)
            {
                if (s.type != null && s.type.FullName != null && s.type.FullName.StartsWith(typeof(LottieScheduler).FullName + "+"))
                    continue;
                var copy = s;
                RemoveOurSystems(ref copy);
                list.Add(copy);
            }
            system.subSystemList = list.ToArray();
        }
    }
}

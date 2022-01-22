using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Jobs;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

namespace TransformAccessArrayDemo
{
    [Serializable]
    public class CasterAndDecal
    {
        public Transform DecalTransform;
        public Transform CasterTransform;

        public int DecalTransformId;
        public int CasterTransformId;
    }

    public class TransformAccessArrayManager : MonoBehaviour
    {
        [SerializeField]
        private Transform m_AgentPrefab;

        [SerializeField]
        private Transform m_DecalPrefab;

        [SerializeField]
        private Bounds m_Bounds;

        [Range(0.0f, 1.0f)]
        public float RotationSmoothing = 0.5f;

        [Range(0.0f, 2.0f)]
        public float Speed = 1.0f;

        [Range(0.5f, 5.0f)]
        public float ChangeRotationPeriod = 2.0f;

        [Range(0, 30000)]
        public int Count = 100;

        private TransformAccessArrayWrapper m_Casters;
        private TransformAccessArrayWrapper m_Decals;
        private NativeList<quaternion> m_TargetForwards;
        private NativeList<float> m_DelayRotationChangeCurrents;
        [SerializeField] private LayerMask m_LayerMask;

        private List<CasterAndDecal> m_DecalsInfo;
        private List<GameObject> m_Roots;
        private NativeList<RaycastHit> m_Hits;
        private NativeList<RaycastCommand> m_Commands;
        private JobHandle m_UpdateDependency;

        // When this field is true:
        //  TransformAccessArray objects are put in buckets of HierarchyBucketSize transform with an empty parent (faster)
        //  Just in the root otherwise (slower)
        public bool UseHierarchySplit;
        [Range(32, 1024)]
        [SerializeField] private int HierarchyBucketSize = 256;
        private Transform m_CasterParent;
        private int m_CasterChild = int.MaxValue;
        private Transform m_DecalParent;
        private int m_DecalChild = int.MaxValue;

        private static ProfilerMarker s_ProfilerMarkerWaitForJob = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(Update)}.WaitForJob");
        private static ProfilerMarker s_ProfilerMarkerSync = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(SyncCount)}");
        private static ProfilerMarker s_ProfilerMarkerSyncSpawn = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(SyncCount)}.Spawn");
        private static ProfilerMarker s_ProfilerMarkerSyncDespawn = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(SyncCount)}.Despawn");
        private static ProfilerMarker s_ProfilerMarkerSyncOnEnable = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(OnEnable)}");
        private static ProfilerMarker s_ProfilerMarkerSyncOnDisable = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(OnDisable)}");

        private static ProfilerMarker s_ProfilerMarkerScheduleMoveAgentsJob = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(MoveAgentsJob)}.Schedule");
        private static ProfilerMarker s_ProfilerMarkerScheduleCommandsCreationJob = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(CommandsCreationJob)}.Schedule");
        private static ProfilerMarker s_ProfilerMarkerScheduleRaycastCommand = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(RaycastCommand)}.Schedule");
        private static ProfilerMarker s_ProfilerMarkerScheduleSetPositionsJob = new ProfilerMarker(ProfilerCategory.Scripts, $"{nameof(TransformAccessArray)}.{nameof(SetPositionsJob)}.Schedule");

        private void SetReferences()
        {
            Validate();
        }

        [Conditional("UNITY_EDITOR")]
        private void Validate()
        {
            if (m_AgentPrefab == null)
            {
                Debug.LogError($"{nameof(TransformAccessArrayManager)} ({gameObject.name}) doesn't have {nameof(m_AgentPrefab)} assigned!", gameObject);
            }

            if (m_DecalPrefab == null)
            {
                Debug.LogError($"{nameof(TransformAccessArrayManager)} ({gameObject.name}) doesn't have {nameof(m_DecalPrefab)} assigned!", gameObject);
            }
        }

        private void Reset()
        {
            m_Bounds = new Bounds(transform.position, new Vector3(20, 20, 20));
            SetReferences();
        }

        private ObjectPool<Transform> m_DecalPool;
        private ObjectPool<Transform> m_CasterPool;

        private GameObject m_DecalPoolRoot;
        private GameObject m_CasterPoolRoot;

        private bool m_ApplicationQuit;
        private void OnApplicationQuit()
        {
            m_ApplicationQuit = true;
        }

        Transform GetDecalPoolParent()
        {
            if (m_DecalPoolRoot == null)
                m_DecalPoolRoot = new GameObject("DecalPoolRoot");
            return m_DecalPoolRoot.transform;
        }

        Transform GetCasterPoolParent()
        {
            if (m_CasterPoolRoot == null)
                m_CasterPoolRoot = new GameObject("CasterPoolRoot");
            return m_CasterPoolRoot.transform;
        }

        private void Awake()
        {
            SetReferences();

            m_CasterPool = new ObjectPool<Transform>(() => Instantiate(m_AgentPrefab, GetCasterPoolParent()),
                                                    actionOnGet: obj =>
                                                    {
                                                        obj.gameObject.SetActive(true);
                                                        obj.parent = GetCasterParent();
                                                        obj.SetPositionAndRotation(RandomPositionInsideBounds(), Random.rotationUniform);
                                                    }, actionOnRelease: obj =>
                                                    {
                                                        obj.gameObject.SetActive(false);
                                                        obj.parent = GetCasterPoolParent();
                                                    }, actionOnDestroy: obj =>
                                                    {
                                                        GameObject.Destroy(obj.gameObject);
                                                    }, collectionCheck: false, defaultCapacity: 32, maxSize: 10000);

            m_DecalPool = new ObjectPool<Transform>(() => Instantiate(m_DecalPrefab, GetDecalPoolParent()),
                                                    actionOnGet: obj =>
                                                    {
                                                        obj.gameObject.SetActive(true);
                                                        obj.parent = GetDecalParent();
                                                    }, actionOnRelease: obj =>
                                                    {
                                                        obj.gameObject.SetActive(false);
                                                        obj.parent = GetDecalPoolParent();
                                                    }, actionOnDestroy: obj =>
                                                    {
                                                        GameObject.Destroy(obj.gameObject);
                                                    }, collectionCheck: false, defaultCapacity: 32, maxSize: 10000);
        }

        private void OnEnable()
        {
            using var _ = s_ProfilerMarkerSyncOnEnable.Auto();

            var capacity = Count;
            const Allocator allocator = Allocator.Persistent;

            m_Casters = new TransformAccessArrayWrapper(capacity, allocator);
            m_Decals = new TransformAccessArrayWrapper(capacity, allocator);

            m_DecalsInfo = new List<CasterAndDecal>(capacity);
            m_Roots = new List<GameObject>(2 * capacity / HierarchyBucketSize);

            m_Hits = new NativeList<RaycastHit>(capacity, allocator);
            m_Commands = new NativeList<RaycastCommand>(capacity, allocator);
            m_DelayRotationChangeCurrents = new NativeList<float>(capacity, allocator);
            m_TargetForwards = new NativeList<quaternion>(capacity, allocator);
            m_UpdateDependency = default;

            SyncCount(Count);
        }

        private void OnDisable()
        {
            using var _ = s_ProfilerMarkerSyncOnDisable.Auto();

            m_UpdateDependency.Complete();

            if (m_ApplicationQuit == false)
            {
                SyncCount(0);

                foreach (var go in m_Roots)
                    Destroy(go);
            }

            m_Roots.Clear();

            m_Casters.Dispose();
            m_Decals.Dispose();

            m_Hits.Dispose();
            m_Commands.Dispose();
            m_DelayRotationChangeCurrents.Dispose();
            m_TargetForwards.Dispose();
        }

        private void Update()
        {
            {
                using var _ = s_ProfilerMarkerWaitForJob.Auto();
                m_UpdateDependency.Complete();
            }

            if (m_Casters.Length != Count)
            {
                SyncCount(Count);
            }

            var dependency = new JobHandle();

            // or if your 'move' code is different - could just read it like that
            // this will write to m_Casters.Position and m_Casters.Directions
            //dependency = m_Casters.ScheduleReadPositions(dependency);

            using (var _ = s_ProfilerMarkerScheduleMoveAgentsJob.Auto())
            {
                dependency = m_Casters.ScheduleWritePositions(new MoveAgentsJob
                {
                    DeltaTime = Time.deltaTime,
                    Speed = Speed,
                    RotationSmoothing = RotationSmoothing,
                    ChangeRotationPeriod = ChangeRotationPeriod,
                    Seed = (uint)Time.frameCount,
                    m_TargetForwards = m_TargetForwards,
                    m_DelayRotationChangeCurrents = m_DelayRotationChangeCurrents,
                    Positions = m_Casters.Positions.AsArray(),
                    Directions = m_Casters.Directions.AsArray()
                }, dependency);
            }

            using (var _ = s_ProfilerMarkerScheduleCommandsCreationJob.Auto())
            {
                dependency = new CommandsCreationJob
                {
                    Commands = m_Commands,
                    Positions = m_Casters.Positions.AsArray(),
                    LayerMask = m_LayerMask
                }.Schedule(Count, 256, dependency);
            }


            using (var _ = s_ProfilerMarkerScheduleRaycastCommand.Auto())
            {
                dependency = RaycastCommand.ScheduleBatch(m_Commands.AsArray().GetSubArray(0, Count),
                                                          m_Hits.AsArray(),
                                                          1,
                                                          dependency);
            }

            using (var _ = s_ProfilerMarkerScheduleSetPositionsJob.Auto())
            {
                dependency = m_Decals.ScheduleWritePositions(new SetPositionsJob
                {
                    Hits = m_Hits,
                    Directions = m_Casters.Directions.AsArray()
                }, dependency);
            }

            m_UpdateDependency = dependency;
        }

        private void SyncCount(int neededCount)
        {
            using var _ = s_ProfilerMarkerSync.Auto();

            var currentN = m_Casters.Length;

            if (currentN < neededCount)
            {
                using var spawn = s_ProfilerMarkerSyncSpawn.Auto();

                for (var i = currentN; i < neededCount; ++i)
                {
                    var caster = m_CasterPool.Get();
                    var decal = m_DecalPool.Get();

                    m_DecalsInfo.Add(new CasterAndDecal
                    {
                        CasterTransform = caster,
                        DecalTransform = decal,
                        CasterTransformId = m_Casters.Register(caster),
                        DecalTransformId = m_Decals.Register(decal),
                    });
                }
            }
            else if (currentN > neededCount)
            {
                using var despawn = s_ProfilerMarkerSyncDespawn.Auto();

                for (var i = neededCount; i < currentN; ++i)
                {
                    var info = m_DecalsInfo[i];

                    m_Decals.Deregister(info.DecalTransformId);
                    m_Casters.Deregister(info.CasterTransformId);

                    if (info.DecalTransform)
                        m_DecalPool.Release(info.DecalTransform);
                    if (info.CasterTransform)
                        m_CasterPool.Release(info.CasterTransform);
                }
                m_DecalsInfo.RemoveRange(neededCount, currentN - neededCount);
            }

            m_Hits.Length = neededCount;
            m_DelayRotationChangeCurrents.Length = neededCount;
            m_TargetForwards.Length = neededCount;
            m_Commands.Length = neededCount;

            Assert.AreEqual(neededCount, m_Decals.Length, $"After {nameof(SyncCount)} call {nameof(m_Decals)}.Length must be {neededCount}");
            Assert.AreEqual(neededCount, m_Casters.Length, $"After {nameof(SyncCount)} call {nameof(m_Casters)}.Length must be {neededCount}");
            Assert.AreEqual(neededCount, m_DecalsInfo.Count, $"After {nameof(SyncCount)} call {nameof(m_DecalsInfo)}.Length must be {neededCount}");
            Assert.AreEqual(neededCount, m_Hits.Length, $"After {nameof(SyncCount)} call {nameof(m_Hits)}.Length must be {neededCount}");
            Assert.AreEqual(neededCount, m_DelayRotationChangeCurrents.Length, $"After {nameof(SyncCount)} call {nameof(m_DelayRotationChangeCurrents)}.Length must be {neededCount}");
            Assert.AreEqual(neededCount, m_TargetForwards.Length, $"After {nameof(SyncCount)} call {nameof(m_TargetForwards)}.Length must be {neededCount}");
            Assert.IsTrue(m_Commands.Length >= neededCount, $"After {nameof(SyncCount)} call {nameof(m_Commands)}.Length must be >= {neededCount}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Transform GetCasterParent()
        {
            if (UseHierarchySplit == false)
                return null;

            if (m_CasterChild >= HierarchyBucketSize)
            {
                var go = new GameObject("CasterParent");
                m_Roots.Add(go);
                m_CasterParent = go.transform;
                m_CasterChild = 0;
            }
            ++m_CasterChild;
            return m_CasterParent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Transform GetDecalParent()
        {
            if (UseHierarchySplit == false)
                return null;

            if (m_DecalChild >= HierarchyBucketSize)
            {
                var go = new GameObject("DecalParent");
                m_Roots.Add(go);
                m_DecalParent = go.transform;
                m_DecalChild = 0;
            }
            ++m_DecalChild;
            return m_DecalParent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 RandomPositionInsideBounds()
        {
            var min = m_Bounds.min;
            var max = m_Bounds.max;
            return new Vector3(
                Random.Range(min.x, max.x),
                Random.Range(min.y, max.y),
                Random.Range(min.z, max.z)
            );
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;
            Gizmos.DrawCube(m_Bounds.center, m_Bounds.size);
        }
#endif
    }

    [BurstCompile]
    internal struct MoveAgentsJob : IJobParallelForTransform
    {
        public NativeArray<float> m_DelayRotationChangeCurrents;
        public NativeArray<quaternion> m_TargetForwards;
        [WriteOnly] public NativeArray<float3> Positions;
        [WriteOnly] public NativeArray<float3> Directions;
        public float ChangeRotationPeriod;
        public float Speed;
        public float DeltaTime;
        public float RotationSmoothing;
        public uint Seed;

        public void Execute(int index, TransformAccess transform)
        {
            if (m_DelayRotationChangeCurrents[index] <= 0)
            {
                var rnd = Unity.Mathematics.Random.CreateFromIndex(Seed + (uint)index);
                m_TargetForwards[index] = rnd.NextQuaternionRotation();
                m_DelayRotationChangeCurrents[index] = ChangeRotationPeriod;
            }
            else
            {
                m_DelayRotationChangeCurrents[index] -= DeltaTime;
            }

            // rotate the agent towards target forward, using frame rate independent slerp here
            // to know more about this '1.0f - Mathf.Pow(RotationSmoothing, Time.deltaTime)' try to search 'frame independent lerp', 'exponential decay'
            var newRotation = math.slerp(transform.rotation, m_TargetForwards[index], 1.0f - math.pow(RotationSmoothing, DeltaTime));

            // find 'forward' in 'newRotation' space
            var newWorldForward = math.mul(newRotation, new float3(0, 0, 1));

            // agent is moving towards its forward direction, using Speed field
            var p = (float3)transform.position + newWorldForward * Speed * DeltaTime;
            Positions[index] = p;

            transform.position = p;
            transform.rotation = Quaternion.LookRotation(newWorldForward);

            Directions[index] = newWorldForward;
        }
    }

    [BurstCompile]
    public struct CommandsCreationJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<RaycastCommand> Commands;
        [ReadOnly] public NativeArray<float3> Positions;
        public LayerMask LayerMask;

        public void Execute(int index)
        {
            Commands[index] = new RaycastCommand
            {
                from = Positions[index],
                direction = Vector3.down,
                distance = 50.0f,
                layerMask = LayerMask,
                maxHits = 1
            };
        }
    }

    [BurstCompile]
    public struct SetPositionsJob : IJobParallelForTransform
    {
        [ReadOnly]
        public NativeArray<RaycastHit> Hits;

        [ReadOnly]
        public NativeArray<float3> Directions;

        public void Execute(int index, TransformAccess transform)
        {
            if (Hits[index].normal.sqrMagnitude >= 0.99f)
            {
                transform.localPosition = Hits[index].point;

                // decal's forward is in XZ plane only
                var forward = Directions[index];
                var forwardXZ = new Vector3(forward.x, 0, forward.z);
                transform.localRotation = Quaternion.LookRotation(forwardXZ);
            }
        }
    }
}
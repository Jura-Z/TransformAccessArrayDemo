//#define DEBUG_CHECKS

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Jobs;

namespace TransformAccessArrayDemo
{
    public struct TransformAccessArrayWrapper : IDisposable
    {
        private JobHandle m_ReadPositionsJobHandle;
        private JobHandle m_WritePositionsJobHandle;

        private NativeHashMap<int, int> m_Id2Index;
        private NativeHashMap<int, int> m_Index2Id;
        private NativeList<float3> m_Positions;
        private NativeList<float3> m_Directions;
        private TransformAccessArray m_Transforms;
        private bool m_Initialized;

        private struct IdCounterKey {}
        private static readonly SharedStatic<int> IdCounter = SharedStatic<int>.GetOrCreate<int, IdCounterKey>();

        public TransformAccessArrayWrapper(int capacity, Allocator allocator)
        {
            m_Positions = new NativeList<float3>(capacity, allocator);
            m_Directions = new NativeList<float3>(capacity, allocator);
            m_Id2Index = new NativeHashMap<int, int>(capacity, allocator);
            m_Index2Id = new NativeHashMap<int, int>(capacity, allocator);
            m_Transforms = new TransformAccessArray(capacity);
            m_ReadPositionsJobHandle = default;
            m_WritePositionsJobHandle = default;
            m_Initialized = true;
        }

        public void Dispose() => Dispose(default);
        public void Dispose(JobHandle dependency)
        {
            WaitTillJobsComplete();

            m_Initialized = false;

            if (m_Id2Index.IsCreated)
                m_Id2Index.Dispose(dependency);
            if (m_Index2Id.IsCreated)
                m_Index2Id.Dispose(dependency);
            if (m_Positions.IsCreated)
                m_Positions.Dispose(dependency);
            if (m_Directions.IsCreated)
                m_Directions.Dispose(dependency);
            if (m_Transforms.isCreated)
                m_Transforms.Dispose();
        }

        public void WaitTillJobsComplete()
        {
            m_ReadPositionsJobHandle.Complete();
            m_WritePositionsJobHandle.Complete();
        }

        public int Register(Transform transform)
        {
            if (m_Initialized == false) return 0;

            WaitTillJobsComplete();

            var id = CreateId();

            m_Positions.Add(transform.position);
            m_Directions.Add(transform.forward);
            m_Transforms.Add(transform);

            ValidateMapsState();
            
            return id;
        }

        public bool IsCreated => m_Initialized;
        public int Length => m_Positions.Length;
        public NativeList<float3> Positions => m_Positions;
        public NativeList<float3> Directions => m_Directions;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetLastIndex() => m_Positions.Length - 1;

        // listener id <-> index
        private int CreateId()
        {
            var newId = ++IdCounter.Data;
            var index = GetLastIndex() + 1;
            m_Id2Index[newId] = index;
            m_Index2Id[index] = newId;
            return newId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetIndexId(int id)
        {
            if (m_Id2Index.TryGetValue(id, out var index))
                return index;

#if DEBUG_CHECKS
            ValidateMapsState();
            Assert.IsTrue(false); // listenedId is not known, why?
#endif

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetIndexForId(int id, int index)
        {
#if DEBUG_CHECKS
            Assert.IsTrue(GetIndexId(id) != -1); // Id we want to preserve, it will point to index
#endif

            m_Id2Index[id] = index;
            m_Index2Id[index] = id;
        }

        public (int index, int lastIndex) Deregister(int id)
        {
            if (m_Initialized == false) return (0, 0);

            WaitTillJobsComplete();

            //Debug.Log($"Deregister listener {Id}");

            if (id == 0)
            {
#if DEBUG_CHECKS
                Assert.IsTrue(false); // this is weird, why we called deregister on id == 0?
#endif
                return (0, 0);
            }

            var lastIndex = GetLastIndex();
            var index = GetIndexId(id);

#if DEBUG_CHECKS
            Assert.IsTrue(lastIndex >= 0); // if false - listeners container is empty, why call DeregisterListener? what's in Id?
#endif

            if (index != lastIndex)
            {
                // swap last to index that we're deleting
                m_Positions[index] = m_Positions[lastIndex];
                m_Directions[index] = m_Directions[lastIndex];

                var idToSave = m_Index2Id[lastIndex];
                SetIndexForId(idToSave, index);
            }

            // shrink arrays, deleting the last one
            m_Id2Index.Remove(id);
            m_Index2Id.Remove(lastIndex);
            m_Positions.Length--;
            m_Directions.Length--;
            m_Transforms.RemoveAtSwapBack(index);

            ValidateMapsState();

            return (index, lastIndex);
        }

        [Conditional("DEBUG_CHECKS")]
        private void ValidateMapsState()
        {
            Assert.IsTrue(m_Index2Id.Count() == m_Id2Index.Count());
            foreach (var indx2Id in m_Index2Id)
            {
                Assert.AreEqual(m_Id2Index[indx2Id.Value], indx2Id.Key);
            }
        }

        [BurstCompile]
        private struct GetPositionsJob : IJobParallelForTransform
        {
            [WriteOnly]
            public NativeArray<float3> Positions;
            [WriteOnly]
            public NativeArray<float3> Directions;

            public void Execute(int index, TransformAccess transform)
            {
                Positions[index] = transform.position;
                Directions[index] = transform.rotation * Vector3.forward;
            }
        }

        public JobHandle ScheduleReadPositions(JobHandle dependency)
        {
            dependency = JobHandle.CombineDependencies(dependency, m_ReadPositionsJobHandle, m_WritePositionsJobHandle);

            m_ReadPositionsJobHandle = new GetPositionsJob
                {
                    Positions = m_Positions,
                    Directions = m_Directions,
                }
                .ScheduleReadOnly(m_Transforms, 256, dependency);

            return m_ReadPositionsJobHandle;
        }

        public JobHandle ScheduleWritePositions<T>(T job, JobHandle dependency) where T : struct, IJobParallelForTransform
        {
            dependency = JobHandle.CombineDependencies(dependency, m_ReadPositionsJobHandle, m_WritePositionsJobHandle);

            m_WritePositionsJobHandle = job.Schedule(m_Transforms, dependency);

            return m_WritePositionsJobHandle;
        }
    }
}
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;

namespace TransformAccessArrayDemo.Naive
{
    public class NaiveManager : MonoBehaviour
    {
        [SerializeField]
        private NaiveAgent m_NaiveAgentPrefab;

        [SerializeField]
        private Bounds m_Bounds;

        [Range(0, 30000)]
        public int Count = 100;

        [SerializeField]
        private List<NaiveAgent> m_SpawnedAgent = new List<NaiveAgent>(128);

        private void SetReferences()
        {
            Validate();
        }

        [Conditional("UNITY_EDITOR")]
        private void Validate()
        {
            if (m_NaiveAgentPrefab == null)
            {
                Debug.LogError($"{nameof(NaiveManager)} ({gameObject.name}) doesn't have {nameof(m_NaiveAgentPrefab)} assigned!", gameObject);
            }
        }

        private void Reset()
        {
            m_Bounds = new Bounds(transform.position, new Vector3(20, 20, 20));
            SetReferences();
        }

        private ObjectPool<NaiveAgent> m_AgentPool;
        private GameObject m_AgentPoolRoot;

        Transform GetAgentPoolParent()
        {
            if (m_AgentPoolRoot == null)
                m_AgentPoolRoot = new GameObject("AgentPoolRoot");
            return m_AgentPoolRoot.transform;
        }

        private void Awake()
        {
            SetReferences();

            m_AgentPool = new ObjectPool<NaiveAgent>(() => Instantiate(m_NaiveAgentPrefab, GetAgentPoolParent()),
                                                     actionOnGet: obj =>
                                                     {
                                                         obj.gameObject.SetActive(true);
                                                         obj.transform.SetPositionAndRotation(RandomPositionInsideBounds(), Random.rotationUniform);
                                                     }, actionOnRelease: obj =>
                                                     {
                                                         obj.gameObject.SetActive(false);
                                                         obj.transform.parent = GetAgentPoolParent();
                                                     }, actionOnDestroy: obj =>
                                                     {
                                                         GameObject.Destroy(obj.gameObject);
                                                     }, collectionCheck: false, defaultCapacity: 32, maxSize: 10000);
        }

        private void OnEnable()
        {
            SyncCount(Count);
        }

        private void OnDisable()
        {
            SyncCount(0);
            //m_AgentPool.Clear();
        }

        private void Update()
        {
            if (m_SpawnedAgent.Count != Count)
            {
                SyncCount(Count);
            }
        }

        private void SyncCount(int neededCount)
        {
            var currentN = m_SpawnedAgent.Count;

            if (currentN < neededCount)
            {
                var trans = transform;
                for (var i = currentN; i < neededCount; ++i)
                {
                    var newAgent = Instantiate(m_NaiveAgentPrefab, RandomPositionInsideBounds(), Random.rotationUniform, trans);
                    m_SpawnedAgent.Add(newAgent);
                }
            }
            else if (currentN > neededCount)
            {
                for (var i = neededCount; i < currentN; ++i)
                {
                    Assert.IsNotNull(m_SpawnedAgent[i].gameObject, $"{nameof(m_SpawnedAgent)} contains 'null' at index [{i}]. No null's expected");
                    m_AgentPool.Release(m_SpawnedAgent[i]);
                }

                m_SpawnedAgent.RemoveRange(neededCount, currentN - neededCount);
            }

            Assert.AreEqual(neededCount, m_SpawnedAgent.Count, $"After {nameof(SyncCount)} call {nameof(m_SpawnedAgent)}.Count must be {neededCount}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 RandomPositionInsideBounds()
        {
            return new Vector3(
                Random.Range(m_Bounds.min.x, m_Bounds.max.x),
                Random.Range(m_Bounds.min.y, m_Bounds.max.y),
                Random.Range(m_Bounds.min.z, m_Bounds.max.z)
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
}
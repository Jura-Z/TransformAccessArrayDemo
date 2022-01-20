using Unity.Burst;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

public class UITest : MonoBehaviour
{
    [SerializeField] private RectTransform[] m_Transforms;
    private TransformAccessArray m_TAA;
    private JobHandle m_JobHandle;

    void OnEnable()
    {
        m_TAA = new TransformAccessArray(m_Transforms);
    }

    private void OnDisable()
    {
        m_TAA.Dispose();
    }

    void Update()
    {
        m_JobHandle.Complete();

        var j = new MoveAgentsJob
        {
            dt = Time.deltaTime
        };
        m_JobHandle = j.Schedule(m_TAA);
    }

    [BurstCompile]
    private struct MoveAgentsJob : IJobParallelForTransform
    {
        public float dt;

        public void Execute(int index, TransformAccess transform)
        {
            transform.localPosition += new Vector3(10,0,0) * dt;
        }
    }
}

using System.Diagnostics;
using UnityEngine;

namespace TransformAccessArrayDemo.Naive
{
    public class NaiveAgent : MonoBehaviour
    {
        [SerializeField]
        private float m_DecalMaxDistance = 50.0f;

        [SerializeField]
        private LayerMask m_DecalLayerMask = ~0;

        [SerializeField]
        private Transform m_DecalTransform;

        [Range(0.0f, 1.0f)]
        public float RotationSmoothing = 0.5f;

        [Range(0.0f, 2.0f)]
        public float Speed = 1.0f;

        [Range(0.5f, 5.0f)]
        public float ChangeRotationPeriod = 2.0f;

        private readonly RaycastHit[] m_DecalRaycastResults = new RaycastHit[1];
        private Quaternion m_TargetForward;
        private float m_DelayRotationChangeCurrent;

        private void SetReferences()
        {
            Validate();
        }

        [Conditional("UNITY_EDITOR")]
        private void Validate()
        {
            var hasDecalTransform = m_DecalTransform != null;
            if (hasDecalTransform == false)
            {
                UnityEngine.Debug.LogError($"{nameof(NaiveAgent)} ({gameObject.name}) doesn't have decal transform assigned", gameObject);
            }
        }

        private void Reset()
        {
            SetReferences();
        }

        private void Awake()
        {
            SetReferences();
        }

        private void Update()
        {
            if (m_DelayRotationChangeCurrent <= 0)
            {
                m_TargetForward = Random.rotationUniform;
                m_DelayRotationChangeCurrent = ChangeRotationPeriod;
            }
            else
            {
                m_DelayRotationChangeCurrent -= Time.deltaTime;
            }

            // cache Transform, so we don't spend time calling into C++
            var trans = transform;

            // rotate the agent towards target forward, using frame rate independent slerp here
            // to know more about this '1.0f - Mathf.Pow(RotationSmoothing, Time.deltaTime)' try to search 'frame independent lerp', 'exponential decay'
            var newRotation = Quaternion.Slerp(trans.rotation, m_TargetForward, 1.0f - Mathf.Pow(RotationSmoothing, Time.deltaTime));

            // find 'forward' in 'newRotation' space
            var newWorldForward = newRotation * Vector3.forward;

            // agent is moving towards its forward direction, using Speed field
            var newWorldPosition = trans.position + newWorldForward * Speed * Time.deltaTime;

            // one call to apply position and rotation at the same time, faster than doing that separately
            trans.SetPositionAndRotation(newWorldPosition, newRotation);

            // casting a ray 'down' to find an intersection with 'ground' to position decal transform
            var hits = Physics.RaycastNonAlloc(newWorldPosition, Vector3.down, m_DecalRaycastResults, m_DecalMaxDistance, m_DecalLayerMask);
            if (hits > 0)
            {
                // decal's forward is in XZ plane only
                var forwardXZ = new Vector3(newWorldForward.x, 0, newWorldForward.z);
                m_DecalTransform.SetPositionAndRotation(m_DecalRaycastResults[0].point, Quaternion.LookRotation(forwardXZ));
            }
        }
    }
}
using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using TMPro;
using TransformAccessArrayDemo;
using TransformAccessArrayDemo.Naive;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(TextMeshProUGUI))]
public class UIStatusText : MonoBehaviour
{
    enum DemoMode
    {
        Naive,
        TransformAccessArrayWrongHierarchy,
        TransformAccessArrayRootHierarchy,
        TransformAccessArrayCorrect
    }

    [SerializeField] private TextMeshProUGUI m_Text;
    [SerializeField] private TransformAccessArrayManager m_TransformAccessArray;
    [SerializeField] private NaiveManager m_Naive;
    [SerializeField] private int m_Count;

    private readonly StringBuilder m_StringBuilder = new StringBuilder(256);

    private bool m_Enabled = true;
    private DemoMode m_Mode;
    private SimpleFPSCounter m_FPSInfo;

    private class SimpleFPSCounter
    {
        private const int MaxCount = 32;
        private const double MaxTime = 0.1;

        private int m_Count;
        private double m_Accumulator;
        private double m_SmoothDeltaTime;

        public void Sample()
        {
            m_Accumulator += Time.unscaledDeltaTime;
            if (++m_Count >= MaxCount || m_Accumulator >= MaxTime)
            {
                m_SmoothDeltaTime = m_Accumulator / m_Count;
                m_Accumulator = 0;
                m_Count = 0;
            }
        }

        public override string ToString() => m_SmoothDeltaTime <= 0 ? "" : $"Time per frame: {m_SmoothDeltaTime * 1000.0:F2} ms. FPS: {1.0 / m_SmoothDeltaTime:F1}";
    }

    private void SetReferences()
    {
        if (m_Text == null)
            m_Text = GetComponent<TextMeshProUGUI>();
        if (m_TransformAccessArray == null)
            m_TransformAccessArray = FindObjectOfType<TransformAccessArrayManager>(includeInactive: true);
        if (m_Naive == null)
            m_Naive = FindObjectOfType<NaiveManager>(includeInactive: true);
    }

    private void Reset()
    {
        SetReferences();
    }

    private void Awake()
    {
        m_FPSInfo = new SimpleFPSCounter();
        SetReferences();
        SetMethod();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Return))
        {
            m_Enabled = !m_Enabled;

            SetMethod();
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            m_Mode = m_Mode switch
            {
                DemoMode.TransformAccessArrayCorrect => DemoMode.Naive,
                _ => m_Mode + 1
            };

            SetMethod();
        }

        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            m_Count += 500;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            m_Count -= 500;
        }

        m_Count = Math.Clamp(m_Count, 0, 30000);

        m_Naive.Count = m_Count;
        m_TransformAccessArray.Count = m_Count;

        m_FPSInfo.Sample();

        m_Text.text = GetStatusText();
    }

    private string GetStatusText()
    {
        m_StringBuilder.Clear();
        m_StringBuilder.Append("Method: ");
        m_StringBuilder.AppendLine(CurrentModeString());
        m_StringBuilder.AppendLine("Press Space to change mode");

        m_StringBuilder.Append("Press Enter to ");
        m_StringBuilder.AppendLine(m_Enabled ? "deactivate" : "activate");

        m_StringBuilder.Append("Count: ");
        m_StringBuilder.AppendLine(m_Count.ToString());
        m_StringBuilder.AppendLine("Press 1 to decrease");
        m_StringBuilder.AppendLine("Press 2 to increase");
        m_StringBuilder.Append(m_FPSInfo);

        return m_StringBuilder.ToString();
    }

    private string CurrentModeString()
    {
        return m_Mode switch
        {
            DemoMode.Naive => "Naive",
            DemoMode.TransformAccessArrayWrongHierarchy => "TransformAccessArray + Jobs + Burst + Wrong(all in one) Hierarchy",
            DemoMode.TransformAccessArrayRootHierarchy => "TransformAccessArray + Jobs + Burst + Better(all in root) Hierarchy",
            DemoMode.TransformAccessArrayCorrect => "TransformAccessArray + Jobs + Burst + Optimal(buckets by 256) Hierarchy",
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private void SetMethod()
    {
        var sw = Stopwatch.StartNew();

        m_TransformAccessArray.enabled = false;
        m_Naive.enabled = false;

        if (m_Enabled)
            Debug.Log($"Disabled in {sw.ElapsedMilliseconds} msec");

        sw = Stopwatch.StartNew();

        // first disable, set count, then enable, so spike of used memory is minimal
        if (m_Mode == DemoMode.Naive)
        {
            m_Naive.enabled = m_Enabled;
        }
        else
        {
            m_TransformAccessArray.ParentStrategyType = ToStrategyType(m_Mode);
            m_TransformAccessArray.enabled = m_Enabled;
        }

        if (m_Enabled)
            Debug.Log($"Enabled in {sw.ElapsedMilliseconds} msec");
    }

    private TransformAccessArrayManager.ParentType ToStrategyType(DemoMode mode)
    {
        return mode switch
        {
            DemoMode.TransformAccessArrayWrongHierarchy => TransformAccessArrayManager.ParentType.Single,
            DemoMode.TransformAccessArrayRootHierarchy => TransformAccessArrayManager.ParentType.Root,
            DemoMode.TransformAccessArrayCorrect => TransformAccessArrayManager.ParentType.Bucket,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }
}
